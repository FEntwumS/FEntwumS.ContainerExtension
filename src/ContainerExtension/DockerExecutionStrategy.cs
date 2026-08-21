using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using ContainerExtension.Services.Docker;
using static ContainerExtension.Services.Docker.DockerToolConsole;

using System.Text.RegularExpressions;

namespace ContainerExtension;

public sealed class DockerExecutionException : Exception
{
    public DockerExecutionException(string message) : base(message) { }
    public DockerExecutionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Core engine responsible for redirecting OneWare tool execution commands into isolated Docker containers.
/// Handles runtime daemon connection, telemetry tracing, volume mounting, and pipeline I/O streaming.
/// </summary>
public sealed partial class DockerExecutionStrategy : IToolExecutionStrategy, IDisposable
{
    private const string ToolKey = "DockerExecutionStrategy";

    private static readonly System.Diagnostics.ActivitySource DockerActivitySource = new("OneWare.ContainerExtension");

    [GeneratedRegex(@"(?<=://)[^/\s@]+:[^/\s@]+(?=@)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UriCredentialsRegex();

    private static readonly SearchValues<char> DisallowedPathChars = SearchValues.Create(";&|<>*?[]{}()$\\'\"#~`!\t\n\r");

    private string? _cachedRuntimePath;
    private Uri? _daemonUri;

    private readonly ReaderWriterLockSlim _strategyLock = new();

    private readonly ISettingsService _settingsService;
    private DockerClient? _client;

    private DockerConnectionProvider? _connectionProvider;
    private DockerImageManager? _imageManager;
    private DockerContainerManager? _containerManager;

    private DockerConnectionProvider ConnectionProvider => _connectionProvider!;
    private DockerImageManager ImageManager => _imageManager!;
    private DockerContainerManager ContainerManager => _containerManager!;

    private readonly CancellationTokenSource _strategyCts = new();
    private int _disposed;

    // Background runs started via StartProcess, keyed by the opaque handle handed back to the caller. Each
    // value is that run's CancellationTokenSource (linked to _strategyCts) so StopProcess and Dispose can
    // cancel it. Membership is the liveness signal: a run removes its own entry when it finishes, so a key
    // present in this map means "still running".
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _backgroundRuns = new();

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(DockerExecutionStrategy));
        }
    }

    internal DockerClient Client => _client!;
    public string DetectedRuntime => _detectedRuntime;

    private string _detectedRuntime = "";
    private readonly Task _initTask;
    private readonly DockerToolConsole _console = new();
    private ContainerRunner? _runner;
    private readonly NativeFallbackExecutor _nativeFallback;

    internal async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        await _initTask.WaitAsync(ct).ConfigureAwait(false);
    }

    public DockerExecutionStrategy(IServiceProvider serviceProvider)
    {
        _settingsService = serviceProvider.Resolve<ISettingsService>();
        _nativeFallback = new NativeFallbackExecutor(_settingsService, _console);
        _initTask = Task.Run(InitializeInternalAsync);
    }

    // Delegates the daemon bootstrap to DockerConnectionFactory, then adopts the resulting client + managers
    // and arms the container reaper. On failure the factory returns a connection with a null client (having
    // logged the fault), leaving the strategy in the offline state ExecuteAsync already handles.
    private async Task InitializeInternalAsync()
    {
        var conn = await Services.Docker.DockerConnectionFactory.CreateAsync(_settingsService, _strategyCts.Token).ConfigureAwait(false);
        _detectedRuntime = conn.DetectedRuntime;
        _daemonUri = conn.DaemonUri;
        _client = conn.Client;
        _connectionProvider = conn.ConnectionProvider;
        _imageManager = conn.ImageManager;
        _containerManager = conn.ContainerManager;
        if (_client != null)
        {
            ContainerReaper.TryArm(_client);
            _runner = new ContainerRunner(_client, _settingsService, _console, _daemonUri!);
        }
    }

    public string GetRuntimePath()
    {
        _strategyLock.EnterReadLock();
        try
        {
            if (_cachedRuntimePath != null) return _cachedRuntimePath;
        }
        finally
        {
            _strategyLock.ExitReadLock();
        }

        _strategyLock.EnterWriteLock();
        try
        {
            if (_cachedRuntimePath != null) return _cachedRuntimePath;
            var p = _settingsService.SafeGetSetting(ContainerExtensionModule.DockerRuntimePathSetting, "");
            if (string.IsNullOrWhiteSpace(p))
            {
                _cachedRuntimePath = "docker";
            }
            else if (p.StartsWith('-') || p.AsSpan().ContainsAny(DisallowedPathChars))
            {
                throw new DockerExecutionException("Disallowed characters or format detected in container runtime path.");
            }
            else
            {
                _cachedRuntimePath = p.AsSpan().Contains(' ') && !p.StartsWith('"') ? $"\"{p}\"" : p;
            }
            return _cachedRuntimePath;
        }
        finally
        {
            _strategyLock.ExitWriteLock();
        }
    }

    public Dictionary<string, string> GetActiveSettingsSummary()
    {
        _strategyLock.EnterReadLock();
        try
        {
            return new Dictionary<string, string>(16, StringComparer.Ordinal)
            {
                [ContainerExtensionModule.SettingsKeyImage] = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.FallbackImage),
                [ContainerExtensionModule.SettingsKeyPullPolicy] = _settingsService.SafeGetSetting(ContainerExtensionModule.PullPolicySetting, "if-not-present"),
                [ContainerExtensionModule.SettingsKeyPlatform] = _settingsService.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto"),
                [ContainerExtensionModule.SettingsKeyMemory] = _settingsService.SafeGetSetting(ContainerExtensionModule.MemoryLimitSetting, 0.0) is var m && m > 0 ? $"{m:N0} MB" : "No limit",
                [ContainerExtensionModule.SettingsKeyCpu] = _settingsService.SafeGetSetting(ContainerExtensionModule.CpuLimitSetting, 0.0) is var c && c > 0 ? $"{c:N0} cores" : "No limit",
                [ContainerExtensionModule.SettingsKeyTimeout] = _settingsService.SafeGetSetting(ContainerExtensionModule.TimeoutSetting, 0.0) is var t && t > 0 ? $"{t:N0} min" : "None",
                [ContainerExtensionModule.SettingsKeyNetwork] = _settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge"),
                [ContainerExtensionModule.SettingsKeyAutoRemove] = _settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true) ? "On" : "Off",
                [ContainerExtensionModule.SettingsKeyLogLevel] = _settingsService.SafeGetSetting(ContainerExtensionModule.LogLevelSetting, "Errors Only"),
                [ContainerExtensionModule.SettingsKeyTimestamps] = _settingsService.SafeGetSetting(ContainerExtensionModule.ShowTimestampsSetting, true) ? "On" : "Off",
                [ContainerExtensionModule.SettingsKeyNamePrefix] = _settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-") is var n && string.IsNullOrWhiteSpace(n) ? "(none)" : n,
                [ContainerExtensionModule.SettingsKeyExtraLabels] = _settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "") is var e && string.IsNullOrWhiteSpace(e) ? "None" : e,
                [ContainerExtensionModule.SettingsKeyDashboardRefresh] = _settingsService.SafeGetSetting(ContainerExtensionModule.DashboardRefreshSetting, "Manual"),
                [ContainerExtensionModule.SettingsKeyRetention] = _settingsService.SafeGetSetting(ContainerExtensionModule.TelemetryRetentionSetting, "25"),
                [ContainerExtensionModule.SettingsKeyRuntimePath] = _settingsService.SafeGetSetting(ContainerExtensionModule.DockerRuntimePathSetting, "") is var r && string.IsNullOrWhiteSpace(r) ? "docker (PATH)" : r,
                [ContainerExtensionModule.SettingsKeyBypassNamedPipeCheck] = _settingsService.SafeGetSetting(ContainerExtensionModule.BypassNamedPipeCheckSetting, false) ? "Bypassed" : "Active",
                [ContainerExtensionModule.SettingsKeyAllowNativeFallback] = _settingsService.SafeGetSetting(ContainerExtensionModule.AllowNativeFallbackSetting, false) ? "Enabled" : "Disabled",
                [ContainerExtensionModule.SettingsKeyAllowPrivileged] = _settingsService.SafeGetSetting(ContainerExtensionModule.AllowPrivilegedSetting, false) ? "Allowed" : "Disabled"
            };
        }
        finally
        {
            _strategyLock.ExitReadLock();
        }
    }

    public string GetDefaultImage()
    {
        var image = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, "");
        return string.IsNullOrWhiteSpace(image) ? ContainerExtensionModule.FallbackImage : image;
    }

    public async Task PrePullImageAsync(string image, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await _client!.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            return;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { /* Image not found locally, proceed to pull */ }

        var platform = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto")?.Trim();
        var pullParams = new ImagesCreateParameters { FromImage = image };
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
            pullParams.Platform = platform;

        await Client.Images.CreateImageAsync(pullParams, null, EmptyProgress<JSONMessage>.Instance, ct).ConfigureAwait(false);

        // The daemon streams registry pull failures as in-band JSON over an HTTP 200 response, so a
        // failed pull surfaces here as success. Confirm the image actually materialized locally.
        try
        {
            await Client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Failed to pull image '{image}': image not found on registry.", ex);
        }
    }

    public string GenerateDockerRunCommand()
        => Services.Docker.DockerRunCommandFormatter.Generate(_settingsService, GetRuntimePath(), GetDefaultImage());

    // The exact docker run command from the most recent execution this session, with REAL env values and
    // paths (unmasked). Kept in memory only — never logged or persisted — so the dashboard can copy a
    // verbatim, runnable command to the clipboard while the on-disk telemetry log stays scrubbed.
    internal string? LastRawDockerRunCommand { get; private set; }

    private static readonly System.Threading.Lock WeakProcessLock = new();

    public WeakReference<Process> StartWeakProcess(ToolCommand command)
    {
        lock (WeakProcessLock)
        {
            var runCts = new CancellationTokenSource();

            var dummyProcess = new Process();
            dummyProcess.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
                Arguments = OperatingSystem.IsWindows() ? "127.0.0.1 -n 86400" : "86400",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            dummyProcess.EnableRaisingEvents = true;
            dummyProcess.Exited += (s, e) =>
            {
                try
                {
                    runCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Exception is intentionally ignored because runCts may have already been disposed when container execution finishes naturally.
                }
            };

            try
            {
                dummyProcess.Start();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "Failed to start dummy process in StartWeakProcess", ex);
                // The sentinel never started, so the returned dummy's Exited event can no longer relay a
                // kill into runCts. Cancel here so killing the replacement cannot leave the container
                // running with its only host cancellation handle severed.
                try { runCts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
                try { dummyProcess.Dispose(); } catch { /* original handle is being discarded */ }
                dummyProcess = new Process();
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteAsync(command, runCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        ContainerTelemetry.TrackError("DockerExecutionStrategy", "StartWeakProcess task crashed", ex, command.Executable);
                        var errMsg = $"[ERROR] Execution of background task '{command.Executable}' failed: {ex.Message}";
                        SafeInvoke(() =>
                        {
                            (command.ErrorHandler ?? command.OutputHandler)?.Invoke(errMsg);
                        });
                    }
                    catch (Exception)
                    {
                        // Exception is intentionally ignored because error handling failure during shutdown/crash is non-critical.
                    }
                }
                finally
                {
                    try
                    {
                        if (!dummyProcess.HasExited)
                        {
                            dummyProcess.Kill(true);
                        }
                    }
                    catch (Exception)
                    {
                        // Exception is intentionally ignored because dummy process may have already exited.
                    }
                    try
                    {
                        runCts.Dispose();
                    }
                    catch (Exception)
                    {
                        // Exception is intentionally ignored because token source disposal errors are non-critical.
                    }
                }
            }, CancellationToken.None);

            return new WeakReference<Process>(dummyProcess);
        }
    }

    /// <summary>
    /// Starts <paramref name="command"/> as a tracked, long-running background container run and returns an
    /// opaque handle. Unlike <see cref="StartWeakProcess(ToolCommand)"/> (which exposes a host sentinel
    /// <see cref="Process"/>), a Docker run has no host process, so the run is tracked by handle: cancel it
    /// with <see cref="StopProcess(Guid)"/> or query it with <see cref="IsProcessRunning(Guid)"/>. The run
    /// removes its own entry when it completes.
    /// </summary>
    public Guid StartProcess(ToolCommand command)
    {
        ThrowIfDisposed();

        var handle = Guid.NewGuid();
        // Linked to the strategy token so Dispose (which cancels _strategyCts) tears down in-flight runs.
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_strategyCts.Token);
        _backgroundRuns[handle] = runCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(command, runCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    ContainerTelemetry.TrackError("DockerExecutionStrategy", "StartProcess background task crashed", ex, command.Executable);
                    var errMsg = $"[ERROR] Execution of background task '{command.Executable}' failed: {ex.Message}";
                    SafeInvoke(() =>
                    {
                        (command.ErrorHandler ?? command.OutputHandler)?.Invoke(errMsg);
                    });
                }
                catch (Exception)
                {
                    // Exception is intentionally ignored because error handling failure during shutdown/crash is non-critical.
                }
            }
            finally
            {
                // Removing the entry marks the run as finished; the disposer of runCts is always this task's
                // finally, so StopProcess only ever cancels (never disposes) and no double-dispose can occur.
                _backgroundRuns.TryRemove(handle, out _);
                try
                {
                    runCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Exception is intentionally ignored because the source may already be disposed during shutdown.
                }
            }
        }, CancellationToken.None);

        return handle;
    }

    /// <summary>
    /// Stops a background run previously started with <see cref="StartProcess(ToolCommand)"/> by cancelling
    /// its token; the run then tears its container down cooperatively. Returns <c>true</c> if a live run was
    /// found for <paramref name="handle"/>, otherwise <c>false</c>.
    /// </summary>
    public bool StopProcess(Guid handle)
    {
        if (!_backgroundRuns.TryRemove(handle, out var runCts))
        {
            return false;
        }

        try
        {
            runCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The background run completed and disposed its own CTS between the TryRemove and this Cancel.
        }
        return true;
    }

    /// <summary>
    /// Returns whether a background run started with <see cref="StartProcess(ToolCommand)"/> is still
    /// tracked for <paramref name="handle"/>. A completed or stopped run is no longer tracked.
    /// </summary>
    public bool IsProcessRunning(Guid handle) => _backgroundRuns.ContainsKey(handle);

    public string GetStrategyName() => "Docker Container (DotNet API)";

    public string GetStrategyKey() => ToolKey;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Cancel any tracked background runs before tearing down the strategy CTS they are linked to. Each
        // run's own finally disposes its CTS and removes its entry, so here we only cancel (guarded against a
        // run that just finished and disposed its CTS).
        foreach (var handle in _backgroundRuns.Keys)
        {
            if (_backgroundRuns.TryRemove(handle, out var runCts))
            {
                try
                {
                    runCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Exception is intentionally ignored because the run disposed its own CTS as it completed.
                }
            }
        }

        try
        {
            _strategyCts.Cancel();
            _strategyCts.Dispose();
        }
        catch
        {
            // Ignore cancel/dispose errors
        }

        // Reap this client's tracked containers and release the process-exit hooks it may own.
        ContainerReaper.Disarm(_client);

        _imageManager?.Dispose();
        _containerManager?.Dispose();
        _connectionProvider?.Dispose();
        _client?.Dispose();
        _strategyLock.Dispose();
        ContainerTelemetry.Shutdown();
    }

    public async IAsyncEnumerable<string> StreamContainerLogsAsync(string containerId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await foreach (var log in ContainerManager.StreamContainerLogsAsync(containerId, ct).ConfigureAwait(false))
        {
            yield return log;
        }
    }

    private static string ScrubUserPaths(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "";
        }
        try
        {
            input = UriCredentialsRegex().Replace(input, "***:***");
        }
        catch { /* Ignore regex errors */ }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            input = input.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        }
        var user = Environment.UserName;
        // Replace the account name only at identifier boundaries with a 3-character floor, mirroring
        // ContainerTelemetry.ScrubSensitiveInfo: a bare substring replace corrupts unrelated text such as
        // "max-frequency" when the username is short or a common token. Escape the name so it is matched
        // literally, never as a regex pattern.
        if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3)
        {
            try
            {
                input = System.Text.RegularExpressions.Regex.Replace(input,
                    $@"(?<![A-Za-z0-9]){System.Text.RegularExpressions.Regex.Escape(user)}(?![A-Za-z0-9])",
                    "***", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(1000));
            }
            catch { /* Ignore regex timeout/errors */ }
        }
        return input;
    }
    private sealed class LogTelemetryState
    {
        public string Image = string.Empty;
        public string Executable = string.Empty;
        public double Duration;
        public long ExitCode;
        public string? ImageDigest;
        public bool WasCancelled;
        public string? RunCommand;
        // In-memory only, never persisted: the exact unmasked command for the dashboard's verbatim copy.
        public string? RawRunCommand;
        public long? PeakMemory;
        public double? MaxCpu;
        public bool OomKilled;
        public int MaxEntries;
        public string? ErrorMessage;
    }

    internal static bool IsTargetingEmptyGhdlLibrary(ToolCommand command)
    {
        var exe = command.Executable ?? command.ToolName ?? "";
        if (!exe.Contains("ghdl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (command.Arguments == null)
        {
            return false;
        }

        var args = command.Arguments.ToList();
        bool isElabOrMakeOrRun = args.Any(a => a != null && (a.Equals("-m", StringComparison.Ordinal) || a.Equals("-e", StringComparison.Ordinal) || a.Equals("-r", StringComparison.Ordinal)));
        if (!isElabOrMakeOrRun)
        {
            return false;
        }

        string? libraryName = null;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == null) continue;

            if (a.StartsWith("--work=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-work=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = a.Split('=', 2);
                if (parts.Length > 1)
                {
                    var val = parts[1].Replace('\\', '/').TrimEnd('/');
                    libraryName = Path.GetFileName(val);
                }
                break;
            }
            if ((a.Equals("--work", StringComparison.OrdinalIgnoreCase) || a.Equals("-work", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Count)
            {
                var val = args[i + 1]?.Replace('\\', '/').TrimEnd('/');
                if (val != null)
                {
                    libraryName = Path.GetFileName(val);
                }
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        string? workdir = null;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == null) continue;

            if (a.StartsWith("--workdir=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = a.Split('=', 2);
                if (parts.Length > 1)
                {
                    workdir = parts[1];
                }
                break;
            }
            if (a.Equals("-P", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                workdir = args[i + 1];
                break;
            }
            if (a.StartsWith("-P", StringComparison.OrdinalIgnoreCase) && a.Length > 2)
            {
                workdir = a[2..];
                break;
            }
        }

        var baseDir = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? Directory.GetCurrentDirectory() : command.WorkingDirectory;
        var targetDir = baseDir;
        if (!string.IsNullOrWhiteSpace(workdir))
        {
            targetDir = Path.IsPathRooted(workdir) ? workdir : Path.GetFullPath(Path.Combine(baseDir, workdir));
        }

        if (!Directory.Exists(targetDir))
        {
            return false;
        }

        try
        {
            var files = Directory.GetFiles(targetDir, $"{libraryName}-obj*.cf");
            if (files.Length > 0)
            {
                bool allEmpty = true;
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > 4)
                    {
                        allEmpty = false;
                        break;
                    }
                }
                return allEmpty;
            }
        }
        catch
        {
            // Fallback
        }

        return false;
    }


    public async ValueTask<bool> PingAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ConnectionProvider.PingAsync(ct).ConfigureAwait(false);
    }

    public async Task<SystemInfoResponse?> GetSystemInfoAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ConnectionProvider.GetSystemInfoAsync(ct).ConfigureAwait(false);
    }

    public async Task<IList<ContainerListResponse>> ListContainersAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ContainerManager.ListContainersAsync(ct).ConfigureAwait(false);
    }

    public async Task<IList<ImagesListResponse>> ListImagesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ImageManager.ListImagesAsync(ct).ConfigureAwait(false);
    }

    public async Task StopContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await ContainerManager.StopContainerAsync(containerId, ct).ConfigureAwait(false);
    }

    public async Task StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await ContainerManager.StartContainerAsync(containerId, ct).ConfigureAwait(false);
    }

    public async Task RestartContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await ContainerManager.RestartContainerAsync(containerId, ct).ConfigureAwait(false);
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await ContainerManager.RemoveContainerAsync(containerId, ct).ConfigureAwait(false);
    }

    public async Task RemoveImageAsync(string imageId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await ImageManager.RemoveImageAsync(imageId, ct).ConfigureAwait(false);
    }

    public async Task<(int pulled, int failed, IReadOnlyList<string> failedImages)> UpdateAllImagesAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ImageManager.UpdateAllImagesAsync(progress, ct).ConfigureAwait(false);
    }

    public async Task<int> PruneDanglingImagesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ImageManager.PruneDanglingImagesAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> GetContainerLogsAsync(string containerId, int tailLines = 50, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ContainerManager.GetContainerLogsAsync(containerId, tailLines, ct).ConfigureAwait(false);
    }

    public static (int imageCount, long totalSizeBytes, long reclaimableBytes) ComputeDiskUsage(IList<ImagesListResponse> images)
      => DockerImageManager.ComputeDiskUsage(images);

    public async Task<(int imageCount, long totalSizeBytes, long reclaimableBytes)> GetDiskUsageSummaryAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await ImageManager.GetDiskUsageSummaryAsync(ct).ConfigureAwait(false);
    }


    private string ResolveImage(string toolName)
    {
        var envImage = Environment.GetEnvironmentVariable("ONEWARE_DOCKER_IMAGE");
        var specificImage = _settingsService.SafeGetSetting($"{ContainerExtensionModule.PerToolImagePrefix}{toolName.ToLowerInvariant()}", "");
        var configuredImage = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, "");

        var image = envImage ?? "";
        if (string.IsNullOrWhiteSpace(image)) image = specificImage;
        if (string.IsNullOrWhiteSpace(image)) image = configuredImage;
        if (string.IsNullOrWhiteSpace(image) && ContainerExtensionModule.DefaultToolImages.TryGetValue(toolName, out var toolDefault))
            image = toolDefault;
        if (string.IsNullOrWhiteSpace(image)) image = ContainerExtensionModule.FallbackImage;

        image = image.Trim();
        if (image.Contains('\r'))
        {
            image = image.Replace("\r", "", StringComparison.Ordinal);
        }
        return image;
    }

    private CreateContainerParameters BuildContainerParameters(string image, ToolCommand command)
    {
        var sysInfo = ConnectionProvider.CachedSystemInfo;
        double? remoteCpuCores = sysInfo?.NCPU;
        // Rootless runtimes advertise "name=rootless" in /info SecurityOptions. When system info is
        // unavailable (e.g. a runtime whose /info could not be read), default to false so the standard
        // uid:gid path is used — correct for Docker, OrbStack, and rootful Podman.
        var isRootless = sysInfo?.SecurityOptions?.Any(
            o => o != null && o.Contains("name=rootless", StringComparison.OrdinalIgnoreCase)) ?? false;
        return Services.Docker.DockerCommandBuilder.BuildContainerParameters(
          image,
          command,
          _settingsService,
          Services.Docker.DaemonEndpointValidator.CachedUid,
          Services.Docker.DaemonEndpointValidator.CachedGid,
          (cmd, msg) => _console.SdkLog(cmd, msg),
          remoteCpuCores,
          isRootless);
    }

    /// <summary>
    /// The resource profile (peak container memory, max CPU, OOM flag) of the most recently completed
    /// container execution. A diagnostic side channel for the benchmark harness, which needs the real
    /// in-container memory the stats stream captures; <see cref="ExecuteAsync(ToolCommand)"/> can only
    /// return success and output through the <c>IToolExecutionStrategy</c> contract. Not read in
    /// production, so the overwrite under concurrent executions is benign.
    /// </summary>
    internal ContainerRunner.ResourceProfile? LastResourceProfile { get; private set; }

    /// <summary>
    /// Translates a host tool command into an ephemeral container execution payload.
    /// Manages pulling images, configuring container properties, executing the command, and streaming results.
    /// </summary>
    /// <param name="command">The tool command payload to execute.</param>
    /// <returns>A tuple indicating success status and any buffered output if captured.</returns>
    public Task<(bool success, string output)> ExecuteAsync(ToolCommand command)
    {
        return ExecuteAsync(command, CancellationToken.None);
    }

    internal async Task<(bool success, string output)> ExecuteAsync(ToolCommand command, CancellationToken cancellationToken)
    {
        using var activity = DockerActivitySource.StartActivity("DockerExecutionStrategy.Execute");
        // Only emit telemetry tags when the user has not opted out, and never record the raw host
        // path: it embeds the username. The leaf name alone is sufficient for diagnostics.
        if (ContainerTelemetry.TelemetryOptedOutChecker?.Invoke() != true)
        {
            activity?.SetTag("tool.name", command.ToolName);
            activity?.SetTag("tool.executable", System.IO.Path.GetFileNameWithoutExtension(command.Executable ?? string.Empty));
        }

        // Validate the command before awaiting daemon initialization. These checks are cheap and
        // daemon-independent; running them first rejects a malformed command immediately instead of
        // first blocking on EnsureInitializedAsync, which — on a host with no reachable daemon — waits
        // on the background connect and would otherwise hang before the command is ever inspected.
        var executable = (command.Executable ?? command.ToolName ?? string.Empty).Trim('\r', '\n', ' ', '\t');
        executable = Services.Docker.DockerCommandBuilder.HealEscapedPaths(executable);

        if (!string.IsNullOrEmpty(executable))
        {
            var normalized = executable.Replace('\\', '/');
            var exeName = System.IO.Path.GetFileName(normalized);

            if (exeName.AsSpan().ContainsAny(DockerCommandBuilder.ShellSpecialChars))
            {
                return (false, "Command executable contains prohibited shell control characters.");
            }

            foreach (var ch in exeName)
            {
                if (char.IsControl(ch))
                {
                    return (false, "Command executable contains invalid control characters.");
                }
            }
        }

        if (command.Arguments != null)
        {
            var isYosys = (command.Executable != null && command.Executable.Contains("yosys", StringComparison.OrdinalIgnoreCase))
                || (command.ToolName != null && command.ToolName.Contains("yosys", StringComparison.OrdinalIgnoreCase));

            foreach (var arg in command.Arguments)
            {
                if (arg != null)
                {
                    var hasForbidden = arg.Contains("$(") || arg.Contains("`") || arg.Contains("&&") || arg.Contains("||")
                        || (!isYosys && arg.Contains(";"));

                    if (hasForbidden)
                    {
                        return (false, "Nested shell expansion or command separators detected in argument: " + arg);
                    }
                }
            }
        }

        CancellationToken strategyToken;
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedAsync(_strategyCts.Token).ConfigureAwait(false);
            // Capture the strategy token inside the guarded scope: a shutdown-time Dispose can race the
            // linked-CTS construction below, where reading _strategyCts.Token would otherwise throw an
            // ObjectDisposedException outside any try.
            strategyToken = _strategyCts.Token;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // Dispose raced this call during shutdown: the strategy CTS was disposed/cancelled as the
            // prologue (which reads it outside any try) ran. Return a cancelled result rather than
            // letting an ObjectDisposedException/OperationCanceledException escape ExecuteAsync unhandled.
            return (false, string.Empty);
        }

        if (IsTargetingEmptyGhdlLibrary(command))
        {
            _console.SdkLog(command, "[Docker SDK] Bypassing GHDL make/elaboration on empty library targeting to prevent compilation failures.", RankInfo);
            return (true, string.Empty);
        }

        var stopwatch = Stopwatch.StartNew();
        string image = ContainerExtensionModule.FallbackImage;
        string? imageDigest = null;
        string? reconstructedDockerRun = null;
        long exitCode = -1;
        bool nativeFallbackUsed = false;
        bool wasCancelled = false;
        ContainerRunner.ResourceProfile? resourceProfile = null;

        var timeoutMinutes = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.TimeoutSetting, 0);
        if (double.IsNaN(timeoutMinutes) || double.IsInfinity(timeoutMinutes) || timeoutMinutes < 0)
        {
            timeoutMinutes = 0;
        }
        else if (timeoutMinutes > 10080)
        {
            timeoutMinutes = 10080;
        }
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource cts;
        if (timeoutMinutes > 0)
        {
            timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
            cts = CancellationTokenSource.CreateLinkedTokenSource(strategyToken, timeoutCts.Token, cancellationToken);
        }
        else
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(strategyToken, cancellationToken);
        }
        var ct = cts.Token;

        _console.BeginScope(
            _settingsService.SafeGetSetting<string>(ContainerExtensionModule.LogLevelSetting, "Errors Only"),
            _settingsService.SafeGetSetting<bool>(ContainerExtensionModule.ShowTimestampsSetting, true));

        _console.SdkLog(command, $"[Docker SDK] ExecuteAsync started for '{executable}'.", RankInfo);

        string? errorMessage = null;

        try
        {
            bool isDockerOffline = false;
            Exception? dockerConnectionEx = null;

            if (_daemonUri is null || _client is null || _connectionProvider is null)
            {
                // EnsureInitializedAsync caught a connection failure (URI resolution, named-pipe
                // verification, or client construction) and returned with the connection fields unset.
                // Treat this as offline so the native fallback can engage and an actionable message
                // surfaces, instead of dereferencing a null _daemonUri/_client below and reporting the
                // resulting NullReferenceException as an internal error.
                isDockerOffline = true;
                dockerConnectionEx = new DockerExecutionException("The container runtime could not be initialized. Verify the daemon is running and the runtime path or socket is correct.");
            }
            else if (_daemonUri.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase))
            {
                var socketPath = _daemonUri.LocalPath;
                var (live, socketErr) = await Services.Docker.DaemonEndpointValidator.IsUnixSocketLiveAndWritableAsync(socketPath, ct).ConfigureAwait(false);
                if (!live)
                {
                    isDockerOffline = true;
                    dockerConnectionEx = new DockerExecutionException(socketErr ?? $"Docker socket at '{socketPath}' is not active or readable.");
                }
                else if (_settingsService.SafeGetSetting(ContainerExtensionModule.AllowNativeFallbackSetting, false))
                {
                    // The socket accepts a connection but the daemon API may not answer (a stale or
                    // listening-but-dead socket). Only probe this when native fallback is enabled — the sole
                    // case where reclassifying as offline changes behavior (it lets the host-native fallback
                    // engage instead of failing at container creation) — which keeps the ping and its
                    // API-version-negotiation cost off the hot path in the default configuration. The
                    // try/catch mirrors the generic branch so a non-cancellation ping fault also routes to
                    // the fallback; genuine cancellation (OperationCanceledException) still propagates.
                    try
                    {
                        if (!await PingAsync(ct).ConfigureAwait(false))
                        {
                            isDockerOffline = true;
                            dockerConnectionEx = new DockerExecutionException($"Docker socket at '{socketPath}' is listening, but the daemon API is not responding.");
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        isDockerOffline = true;
                        dockerConnectionEx = new DockerExecutionException($"Docker socket at '{socketPath}' connection failed.", ex);
                    }
                }
            }
            else if (_daemonUri.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase))
            {
                var pipeName = _daemonUri.AbsolutePath.TrimStart('/');
                if (pipeName.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase))
                {
                    pipeName = pipeName[5..];
                }
                if (string.IsNullOrEmpty(pipeName))
                {
                    pipeName = "docker_engine";
                }
                if (!await Services.Docker.DaemonEndpointValidator.VerifyWindowsNamedPipeAsync(pipeName, _settingsService.SafeGetSetting(ContainerExtensionModule.BypassNamedPipeCheckSetting, false), ct: ct).ConfigureAwait(false))
                {
                    isDockerOffline = true;
                    dockerConnectionEx = new DockerExecutionException($"Insecure or unreachable named pipe connection detected for '{pipeName}'. If this is a false positive, you can bypass this check in OneWare Studio Settings under 'Binary Management' -> 'Container Engine' -> check 'Bypass Named Pipe Security Check'.");
                }
            }
            else
            {
                try
                {
                    var live = await PingAsync(ct).ConfigureAwait(false);
                    if (!live)
                    {
                        isDockerOffline = true;
                        dockerConnectionEx = new DockerExecutionException("Docker daemon is unreachable.");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    isDockerOffline = true;
                    dockerConnectionEx = new DockerExecutionException("Docker daemon connection failed.", ex);
                }
            }

            if (isDockerOffline)
            {
                var allowNative = _settingsService.SafeGetSetting(ContainerExtensionModule.AllowNativeFallbackSetting, false);
                if (allowNative)
                {
                    var resolvedPath = NativeFallbackExecutor.FindExecutableInPath(executable);
                    if (resolvedPath != null)
                    {
                        // ExecuteNativelyAsync logs its own host-native telemetry entry; flag the fallback so
                        // the finally does not also log a phantom container entry (exit -1) for a run that
                        // never happened, nor wipe the real one under retention=None.
                        nativeFallbackUsed = true;
                        return await _nativeFallback.ExecuteNativelyAsync(command, resolvedPath, stopwatch, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        _console.SdkLog(command, $"[Docker SDK Fallback Warning] Allow Native Fallback is enabled, but '{executable}' was not found on the host system PATH.", RankInfo);
                    }
                }
                throw dockerConnectionEx ?? new DockerExecutionException("Docker daemon is offline.");
            }

            var resolvedWorkingDir = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? Directory.GetCurrentDirectory() : command.WorkingDirectory;
            var workingDirFull = Path.GetFullPath(resolvedWorkingDir);
            command.PrepareCommand(System.Runtime.InteropServices.OSPlatform.Linux, path => Services.Docker.DockerCommandBuilder.MapPathToContainer(path, workingDirFull));

            if (!OperatingSystem.IsWindows())
            {
                await Services.Docker.DaemonEndpointValidator.EnsureUnixIdsLoadedAsync(ct).ConfigureAwait(false);
            }

            _console.SdkLog(command, $"[Docker SDK] Resolving image for tool '{executable}'...", RankInfo);
            image = ResolveImage(command.ToolName ?? string.Empty);

            _console.SdkLog(command, $"[Docker SDK] Resolved image: {image}", RankInfo);

            _console.SdkLog(command, $"[Docker SDK] Building container parameters...", RankInfo);
            var createParams = BuildContainerParameters(image, command);

            var allowPrivileged = _settingsService.SafeGetSetting(ContainerExtensionModule.AllowPrivilegedSetting, false);
            if (!allowPrivileged)
            {
                if (createParams.HostConfig?.Privileged == true)
                {
                    throw new DockerExecutionException("Privileged container execution is blocked by settings.");
                }
                var extraFlags = _settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "");
                if (extraFlags.Contains("--privileged", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DockerExecutionException("Privileged container execution via extra flags is blocked by settings.");
                }
                if (command.Arguments != null)
                {
                    foreach (var arg in command.Arguments)
                    {
                        if (arg != null && arg.Contains("--privileged", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new DockerExecutionException("Privileged container execution via tool arguments is blocked by settings.");
                        }
                    }
                }
            }

            Services.Docker.BindValidator.ValidateBinds(createParams.HostConfig?.Binds);

            _console.SdkLog(command, $"[Docker SDK] Cmd = [{string.Join(", ", createParams.Cmd ?? [])}]", RankInfo);
            _console.SdkLog(command, $"[Docker SDK] WorkingDir = {createParams.WorkingDir}, Binds = [{string.Join(", ", createParams.HostConfig?.Binds ?? [])}]", RankInfo);

            _console.SdkLog(command, $"[Docker SDK] Ensuring image '{image}' is available...", RankInfo);
            imageDigest = await _runner!.EnsureImageAsync(image, command, ct).ConfigureAwait(false);
            _console.SdkLog(command, $"[Docker SDK] Image ready. Digest = {imageDigest ?? "(none)"}", RankInfo);

            reconstructedDockerRun = Services.Docker.DockerRunCommandFormatter.Reconstruct(createParams, GetRuntimePath());
            LastRawDockerRunCommand = Services.Docker.DockerRunCommandFormatter.Reconstruct(createParams, GetRuntimePath(), maskEnvValues: false);
            _console.SdkLog(command, $"[Docker SDK] Equivalent CLI: {reconstructedDockerRun}", RankInfo);

            _console.SdkLog(command, $"[Docker SDK] Creating and starting container...", RankInfo);
            var result = await _runner!.RunContainerAsync(createParams, command, ct).ConfigureAwait(false);
            exitCode = result.exitCode;
            wasCancelled = result.wasCancelled;
            resourceProfile = result.profile;
            LastResourceProfile = resourceProfile;

            if (wasCancelled)
            {
                // RunContainerAsync drains output and returns on cancellation rather than throwing, so
                // the OperationCanceledException catch below never runs on the timeout path. Surface the
                // timeout cause here, where the timeout source is distinguishable from a caller cancel,
                // so the user sees "timed out after N minutes" instead of a bare exit code of -1.
                if (timeoutMinutes > 0 && timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
                {
                    errorMessage = $"Execution timed out after {timeoutMinutes:N0} minute(s).";
                    SafeInvoke(() => command.ErrorHandler?.Invoke($"[Docker SDK] {errorMessage}"));
                }
                return (false, result.output);
            }

            _console.SdkLog(command, $"[Docker SDK] Container finished. Exit code: {exitCode}", RankInfo);
            return (exitCode == 0, result.output);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            errorMessage = timeoutMinutes > 0
              ? $"Execution timed out after {timeoutMinutes:N0} minute(s)."
              : "Operation cancelled.";
            SafeInvoke(() => command.ErrorHandler?.Invoke($"[Docker SDK] {errorMessage}"));
            return (false, "Cancelled");
        }
        catch (Exception ex)
        {
            errorMessage = ScrubUserPaths(ex.Message);
            var err = ScrubUserPaths($"[Docker SDK Error] {ex.GetType().Name}: {ex.Message}");
            if (ex.Message.Contains("No such image", StringComparison.OrdinalIgnoreCase))
            {
                err += $"\n  Hint: Run 'docker pull {image}' to cache the image locally.";
            }
            if (ex.Message.Contains("pull access denied", StringComparison.OrdinalIgnoreCase))
            {
                err += $"\n  Hint: The image '{image}' does not exist on Docker Hub or requires authentication.";
            }
            if (ex.Message.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                ex is UnauthorizedAccessException ||
                (ex is System.Net.Sockets.SocketException sex && (sex.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied || sex.NativeErrorCode == 13)))
            {
                err += $"\n  Hint: A permission error was encountered. Ensure the current user has read/write permissions to the Docker socket, or add the user to the 'docker' group.";
            }
            if (ex.Message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                err += $"\n  Hint: A host port conflict was detected. Please check if another container or service is using the same port, or configure a different host port mapping.";
            }
            SafeInvoke(() => command.ErrorHandler?.Invoke(err));
            var friendlyEx = new DockerExecutionException(errorMessage, ex);
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"ExecuteAsync failed for '{executable}'", friendlyEx);
            return (false, errorMessage);
        }
        finally
        {
            cts.Dispose();
            timeoutCts?.Dispose();
            stopwatch.Stop();
            var retentionStr = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.TelemetryRetentionSetting, "25");
            if (!nativeFallbackUsed && string.Equals(retentionStr, "None", StringComparison.Ordinal))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        ContainerTelemetry.ClearEntries();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Telemetry clear failed: {ex.Message}");
                    }
                }, CancellationToken.None);
            }
            else if (!nativeFallbackUsed)
            {
                var maxEntries = string.Equals(retentionStr, "Unlimited", StringComparison.Ordinal) ? 0 : int.TryParse(retentionStr, out var n) ? n : 100;
                if (ContainerTelemetry.IsTestEnvironment)
                {
                    try
                    {
                        ContainerTelemetry.LogExecution(
                          image: image,
                          tool: Path.GetFileNameWithoutExtension(executable),
                          durationSeconds: stopwatch.Elapsed.TotalSeconds,
                          exitCode: exitCode,
                          imageDigest: imageDigest,
                          wasCancelled: wasCancelled,
                          dockerRunCommand: reconstructedDockerRun,
                          rawDockerRunCommand: LastRawDockerRunCommand,
                          peakMemoryBytes: resourceProfile?.PeakMemoryBytes,
                          maxCpuPercent: resourceProfile?.MaxCpuPercent,
                          oomKilled: resourceProfile?.OomKilled ?? false,
                          maxEntries: maxEntries,
                          errorMessage: errorMessage);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        System.Diagnostics.Debug.WriteLine($"Telemetry logging failed: {ex.Message}");
                    }
                }
                else
                {
                    var state = new LogTelemetryState
                    {
                        Image = image,
                        Executable = executable,
                        Duration = stopwatch.Elapsed.TotalSeconds,
                        ExitCode = exitCode,
                        ImageDigest = imageDigest,
                        WasCancelled = wasCancelled,
                        RunCommand = reconstructedDockerRun,
                        RawRunCommand = LastRawDockerRunCommand,
                        PeakMemory = resourceProfile?.PeakMemoryBytes,
                        MaxCpu = resourceProfile?.MaxCpuPercent,
                        OomKilled = resourceProfile?.OomKilled ?? false,
                        MaxEntries = maxEntries,
                        ErrorMessage = errorMessage
                    };
                    ThreadPool.QueueUserWorkItem(static s =>
                    {
                        try
                        {
                            ContainerTelemetry.LogExecution(
                              image: s.Image,
                              tool: Path.GetFileNameWithoutExtension(s.Executable),
                              durationSeconds: s.Duration,
                              exitCode: s.ExitCode,
                              imageDigest: s.ImageDigest,
                              wasCancelled: s.WasCancelled,
                              dockerRunCommand: s.RunCommand,
                              rawDockerRunCommand: s.RawRunCommand,
                              peakMemoryBytes: s.PeakMemory,
                              maxCpuPercent: s.MaxCpu,
                              oomKilled: s.OomKilled,
                              maxEntries: s.MaxEntries,
                              errorMessage: s.ErrorMessage);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            System.Diagnostics.Debug.WriteLine($"Telemetry logging failed: {ex.Message}");
                        }
                    }, state, preferLocal: false);
                }
            }

            if (!nativeFallbackUsed && stopwatch.Elapsed.TotalSeconds > 30)
            {
                var status = exitCode == 0 ? "succeeded" : (wasCancelled ? "cancelled" : "failed");
                Console.WriteLine(
                  $"[ContainerExtension] {status}: {executable} completed in {stopwatch.Elapsed.TotalSeconds:F1}s (exit {exitCode})");
            }
        }
    }
}
