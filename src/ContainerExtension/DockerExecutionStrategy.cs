#pragma warning disable VSTHRD002, VSTHRD105, VSTHRD110
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using ContainerExtension.Services.Docker;

using System.Runtime.InteropServices;
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
    private const string ContainerWorkDir = "/workspace";

    private static readonly System.Diagnostics.ActivitySource DockerActivitySource = new("OneWare.ContainerExtension");

    [GeneratedRegex(@"(?<=://)[^/\s@]+:[^/\s@]+(?=@)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UriCredentialsRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9][-a-zA-Z0-9.]*(?::\d{1,5})?$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostOnlyRegex();

    private static readonly SearchValues<char> ShellSpecialAndWhitespaceChars = SearchValues.Create(";&|<>*?[]{}()$\\'\"#~`! \t\n\r\v\f");
    private static readonly SearchValues<char> DisallowedPathChars = SearchValues.Create(";&|<>*?[]{}()$\\'\"#~`!\t\n\r");

    private string? _cachedRuntimePath;
    private readonly Uri _daemonUri;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle Pipe, out uint ServerProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle ProcessHandle, uint DesiredAccess, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle TokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint getegid();

    public static readonly string OSArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();

    private readonly ReaderWriterLockSlim _strategyLock = new();

    private static bool IsProcessTrusted(uint pid)
    {
        if (!OperatingSystem.IsWindows()) return true;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_QUERY = 0x0008;

        try
        {
            using var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == null || hProcess.IsInvalid)
            {
                return false; // Fail closed
            }

            if (OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
            {
                using (hToken)
                {
#pragma warning disable S3869
                    using var identity = new System.Security.Principal.WindowsIdentity(hToken.DangerousGetHandle());
#pragma warning restore S3869
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    bool isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    bool isSystem = identity.IsSystem;
                    using var currentIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    bool isCurrentUser = identity.User != null && currentIdentity.User != null && identity.User.Equals(currentIdentity.User);
                    return isAdmin || isSystem || isCurrentUser;
                }
            }
        }
        catch (PlatformNotSupportedException)
        {
            return true; // Fail-open / fallback if identity queries are not supported
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"IsProcessTrusted failed for pid {pid}", ex);
        }
        return false;
    }

    private static bool VerifyWindowsNamedPipe(string pipeName, int timeoutMs = 200)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        var connectTime = DateTime.Now;
        try
        {
            using var pipeStream = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.None);
            pipeStream.Connect(timeoutMs);
            var safeHandle = pipeStream.SafePipeHandle;
            if (safeHandle != null && !safeHandle.IsInvalid)
            {
                if (GetNamedPipeServerProcessId(safeHandle, out var pid))
                {
                    System.Diagnostics.Process? process = null;
                    try
                    {
                        process = System.Diagnostics.Process.GetProcessById((int)pid);
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        return true; // Fail-open on platforms that do not support process by ID lookups
                    }

                    if (process != null)
                    {
                        using (process)
                        {
                            if (!process.HasExited)
                            {
                                try
                                {
                                    var startTime = process.StartTime;
                                    if (startTime > connectTime.AddMilliseconds(500))
                                    {
                                        return false; // PID reuse detected: process started after pipe connection
                                    }

                                     var name = process.ProcessName;
                                     var isNameWhitelisted = name.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("podman", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("wsl", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("vmmember", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("win-sshproxy", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("System", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("svchost", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("rancher", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("lima", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("com.docker", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("orbstack", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("socat", StringComparison.OrdinalIgnoreCase) ||
                                                             name.Contains("ssh", StringComparison.OrdinalIgnoreCase);

                                     if (isNameWhitelisted && IsProcessTrusted(pid))
                                     {
                                         return true;
                                     }
                                     else
                                     {
                                         ContainerTelemetry.TrackError("DockerExecutionStrategy",
                                             $"Named pipe verification failed for pipe '{pipeName}'. Host process: '{name}' (PID: {pid}), Trusted: {IsProcessTrusted(pid)}", null);
                                     }
                                }
                                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5 || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                                {
                                    return IsProcessTrusted(pid);
                                }
                                catch (PlatformNotSupportedException)
                                {
                                    return true;
                                }
                                catch (InvalidOperationException)
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }
                return false;
            }
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (IOException ex) when (ex.InnerException is FileNotFoundException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static readonly SemaphoreSlim UnixIdSemaphore = new(1, 1);
    private static readonly ConcurrentDictionary<string, string?> OwnerCache = new(StringComparer.Ordinal);
    private static volatile string? _cachedUid;
    private static volatile string? _cachedGid;

    internal static async Task EnsureUnixIdsLoadedAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return;
        if (_cachedUid != null && _cachedGid != null) return;

        await UnixIdSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cachedUid ??= await GetUnixIdInternalAsync("-u", "1000", ct).ConfigureAwait(false);
            _cachedGid ??= await GetUnixIdInternalAsync("-g", "1000", ct).ConfigureAwait(false);
        }
        finally
        {
            UnixIdSemaphore.Release();
        }
    }

    private readonly ISettingsService _settingsService;
    private readonly DockerClient _client;

    private readonly DockerConnectionProvider _connectionProvider;
    private readonly DockerImageManager _imageManager;
    private readonly DockerContainerManager _containerManager;

    private readonly CancellationTokenSource _strategyCts = new();
    private int _disposed;

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(DockerExecutionStrategy));
        }
    }

    internal DockerClient Client => _client;
    public string DetectedRuntime { get; }

    private const int RankOff = 0, RankErrors = 1, RankInfo = 2, RankVerbose = 3;

    private static int LogLevelRank(string level) => level switch
    {
        "Verbose" => RankVerbose,
        "Info" => RankInfo,
        "Errors Only" => RankErrors,
        _ => RankOff
    };

    private static void SafeInvoke(Action action)
    {
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
        else
        {
            action();
        }
    }

    private bool IsLogEnabled(int minRank)
    {
        return _currentLogLevelRank.Value >= minRank;
    }

    private void SdkLog(ToolCommand command, string message, int minRank = RankVerbose)
    {
        if (IsLogEnabled(minRank))
        {
            var line = _currentShowTimestamps.Value
                ? string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:HH:mm:ss.fff}] {message}")
                : message;
            SafeInvoke(() => { (command.OutputHandler ?? command.ErrorHandler)?.Invoke(line); });
        }
    }

    private readonly AsyncLocal<int> _currentLogLevelRank = new();
    private readonly AsyncLocal<bool> _currentShowTimestamps = new();

    private static readonly ConcurrentDictionary<string, bool> ActiveContainers = new(StringComparer.Ordinal);
    private static DockerClient? _staticClientForCleanup;
    private static int _cleanupExecuted;
    private static ConsoleCancelEventHandler? _cancelKeyPressHandler;

    public DockerExecutionStrategy(IServiceProvider serviceProvider)
    {
        _settingsService = serviceProvider.Resolve<ISettingsService>();

        var customSocket = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.DaemonSocketSetting, "");
        var envDockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        var uriText = !string.IsNullOrWhiteSpace(customSocket) ? customSocket : (!string.IsNullOrWhiteSpace(envDockerHost) ? envDockerHost : null);
        Uri? uri = null;
        string runtime;
        var resolved = false;

        if (!string.IsNullOrWhiteSpace(uriText))
        {
            try
            {
                if (uriText.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    bool isLocal = uriText.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                                   uriText.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   uriText.Contains("[::1]", StringComparison.Ordinal);
                    if (!isLocal)
                    {
                        Console.WriteLine("[WARN] Insecure HTTP custom daemon socket requested. Upgrading to https://");
                        uriText = "https" + uriText[4..];
                    }
                }
                uri = new Uri(uriText);
                if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    bool isLocal = uri.Host != null && (
                                   uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                   uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                   uri.Host.Equals("::1", StringComparison.Ordinal));
                    if (!isLocal)
                    {
                        Console.WriteLine("[WARN] Insecure HTTP custom daemon socket scheme. Upgrading to HTTPS.");
                        uri = new UriBuilder(uri) { Scheme = "https" }.Uri;
                    }
                }

                if (uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
                {
                    var hostOnly = uri.Host;
                    if (string.IsNullOrEmpty(hostOnly) || !HostOnlyRegex().IsMatch(hostOnly))
                    {
                        throw new UriFormatException("Insecure or invalid SSH tunnel hostname.");
                    }
                }

                var isNetworkScheme = uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
                                      uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                                      uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                if (isNetworkScheme && uri.Host != null)
                {
                    var hostType = Uri.CheckHostName(uri.Host);
                    if (hostType == UriHostNameType.Unknown)
                    {
                        throw new UriFormatException("Invalid remote daemon hostname.");
                    }

                    if (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                        !uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                        !uri.Host.Equals("::1", StringComparison.Ordinal))
                    {
                        var warningMsg = $"[SECURITY WARNING] Connecting to a remote Docker daemon at '{uri.Host}'. Outbound traffic may expose credentials.";
                        Console.Error.WriteLine(warningMsg);
                        ContainerTelemetry.TrackError("DockerExecutionStrategy", "RemoteDaemonWarning", null, warningMsg);
                    }
                }
                runtime = uriText.Contains("podman", StringComparison.OrdinalIgnoreCase) ? "podman" : "docker (custom)";
                resolved = true;
            }
            catch (UriFormatException)
            {
                resolved = false;
                runtime = "";
            }
        }
        else
        {
            runtime = "";
        }

        if (!resolved)
        {
            if (OperatingSystem.IsWindows())
            {
                uri = new Uri("npipe://./pipe/docker_engine");
                runtime = "docker";
            }
            else
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(_strategyCts.Token);
                probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    (uri, runtime) = ProbeUnixSocket(probeCts.Token);
                }
                catch (Exception ex)
                {
                    uri = new Uri("unix:///var/run/docker.sock");
                    runtime = "docker (default)";
                    ContainerTelemetry.TrackError("DockerExecutionStrategy", "ProbeUnixSocket failed, falling back to default", ex);
                }
            }
        }

        DetectedRuntime = runtime;
        if (uri is null)
        {
            throw new DockerExecutionException("Could not resolve a Docker daemon URI. Ensure Docker is installed and running, or set the DOCKER_HOST environment variable.");
        }
        _daemonUri = uri;

        if (uri.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase))
        {
            var pipeName = uri.AbsolutePath.TrimStart('/');
            if (pipeName.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase))
            {
                pipeName = pipeName[5..];
            }
            if (string.IsNullOrEmpty(pipeName))
            {
                pipeName = "docker_engine";
            }
            if (!VerifyWindowsNamedPipe(pipeName))
            {
                throw new DockerExecutionException($"Insecure named pipe connection detected for '{pipeName}'. Connection aborted.");
            }
        }

        using var config = new DockerClientConfiguration(uri);
        // Negotiate Docker API Version
        System.Version apiVersion = new System.Version(1, 44);
        var tempClient = config.CreateClient();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Task? vTask = null;
        try
        {
            var apiTask = Task.Run(async () =>
            {
                try
                {
                    return await tempClient.System.GetVersionAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return null;
                }
            });
            vTask = apiTask;
            if (apiTask.Wait(TimeSpan.FromMilliseconds(500)) && apiTask.IsCompletedSuccessfully)
            {
                var v = apiTask.Result;
                var apiVerStr = v?.APIVersion;
                if (!string.IsNullOrEmpty(apiVerStr))
                {
                    int endIdx = 0;
                    while (endIdx < apiVerStr.Length && (char.IsDigit(apiVerStr[endIdx]) || apiVerStr[endIdx] == '.'))
                    {
                        endIdx++;
                    }
                    var cleanApiVerStr = apiVerStr[..endIdx];
                    if (System.Version.TryParse(cleanApiVerStr, out var parsedVersion))
                    {
                        apiVersion = parsedVersion;
                    }
                }
            }
            else
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // If already disposed, safe to ignore
                }
                catch (AggregateException)
                {
                    // Catch cancellation callback exceptions, safe to ignore
                }
                apiVersion = new System.Version(1, 45);
            }
        }
        catch (Exception ex)
        {
            var isOffline = ex is OperationCanceledException ||
                            ex is TaskCanceledException ||
                            ex is System.Net.Sockets.SocketException ||
                            ex.InnerException is System.Net.Sockets.SocketException ||
                            (ex is HttpRequestException httpEx && (httpEx.InnerException is System.Net.Sockets.SocketException || httpEx.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)));
            if (!isOffline)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "API version negotiation failed, falling back to 1.45", ex);
            }
            apiVersion = new System.Version(1, 45);
        }
        finally
        {
            if (vTask != null)
            {
                _ = vTask.ContinueWith(t =>
                {
                    tempClient.Dispose();
                    cts.Dispose();
                }, TaskScheduler.Default);
            }
            else
            {
                tempClient.Dispose();
                cts.Dispose();
            }
        }
        _client = config.CreateClient(apiVersion);
        try
        {
            _connectionProvider = new DockerConnectionProvider(_client);
            _imageManager = new DockerImageManager(_client, _settingsService);
            _containerManager = new DockerContainerManager(_client);

            if (Interlocked.CompareExchange(ref _staticClientForCleanup, _client, null) is null)
            {
                AppDomain.CurrentDomain.ProcessExit += CleanupDanglingContainers;
                _cancelKeyPressHandler = (s, e) => CleanupDanglingContainers(s, e);
                Console.CancelKeyPress += _cancelKeyPressHandler;
            }
        }
        catch
        {
            _connectionProvider?.Dispose();
            _client.Dispose();
            throw;
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
            return new Dictionary<string, string>(15, StringComparer.Ordinal)
            {
                [ContainerExtensionModule.SettingsKeyImage] = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.FallbackImage),
                [ContainerExtensionModule.SettingsKeyPullPolicy] = _settingsService.SafeGetSetting(ContainerExtensionModule.PullPolicySetting, "if-not-present"),
                [ContainerExtensionModule.SettingsKeyPlatform] = _settingsService.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto"),
                [ContainerExtensionModule.SettingsKeyMemory] = _settingsService.SafeGetSetting(ContainerExtensionModule.MemoryLimitSetting, 0.0) is var m && m > 0 ? $"{m:N0} MB" : "No limit",
                [ContainerExtensionModule.SettingsKeyCpu] = _settingsService.SafeGetSetting(ContainerExtensionModule.CpuLimitSetting, 0.0) is var c && c > 0 ? $"{c:N0} cores" : "No limit",
                [ContainerExtensionModule.SettingsKeyTimeout] = _settingsService.SafeGetSetting(ContainerExtensionModule.TimeoutSetting, 0.0) is var t && t > 0 ? $"{t:N0} min" : "None",
                [ContainerExtensionModule.SettingsKeyNetwork] = _settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge"),
                [ContainerExtensionModule.SettingsKeyAutoRemove] = _settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true) ? "On" : "Off",
                [ContainerExtensionModule.SettingsKeyLogLevel] = _settingsService.SafeGetSetting(ContainerExtensionModule.LogLevelSetting, "Verbose"),
                [ContainerExtensionModule.SettingsKeyTimestamps] = _settingsService.SafeGetSetting(ContainerExtensionModule.ShowTimestampsSetting, true) ? "On" : "Off",
                [ContainerExtensionModule.SettingsKeyNamePrefix] = _settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-") is var n && string.IsNullOrWhiteSpace(n) ? "(none)" : n,
                [ContainerExtensionModule.SettingsKeyExtraLabels] = _settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "") is var e && string.IsNullOrWhiteSpace(e) ? "None" : e,
                [ContainerExtensionModule.SettingsKeyDashboardRefresh] = _settingsService.SafeGetSetting(ContainerExtensionModule.DashboardRefreshSetting, "Manual"),
                [ContainerExtensionModule.SettingsKeyRetention] = _settingsService.SafeGetSetting(ContainerExtensionModule.TelemetryRetentionSetting, "100"),
                [ContainerExtensionModule.SettingsKeyRuntimePath] = _settingsService.SafeGetSetting(ContainerExtensionModule.DockerRuntimePathSetting, "") is var r && string.IsNullOrWhiteSpace(r) ? "docker (PATH)" : r
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
        try
        {
            await _client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            return;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { /* Image not found locally, proceed to pull */ }

        var platform = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto")?.Trim();
        var pullParams = new ImagesCreateParameters { FromImage = image };
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
            pullParams.Platform = platform;

        await _client.Images.CreateImageAsync(pullParams, null, EmptyProgress<JSONMessage>.Instance, ct).ConfigureAwait(false);
    }

    public string GenerateDockerRunCommand()
    {
        var image = GetDefaultImage();
        var runtimePath = GetRuntimePath();
        var memMb = _settingsService.SafeGetSetting(ContainerExtensionModule.MemoryLimitSetting, 0.0);
        var cpuCores = _settingsService.SafeGetSetting(ContainerExtensionModule.CpuLimitSetting, 0.0);
        var network = _settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge");
        var autoRemove = _settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true);
        var platform = _settingsService.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto");
        var namePrefix = _settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");
        var extraFlags = _settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "");

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{runtimePath} run");
        if (autoRemove)
        {
            sb.Append(" --rm");
        }
        if (!string.IsNullOrWhiteSpace(namePrefix))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --name {namePrefix.TrimEnd('-')}-<tool>-<hhmmss>");
        }
        sb.Append(CultureInfo.InvariantCulture, $" -v \"$(pwd)\":{ContainerWorkDir} -w {ContainerWorkDir}");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            sb.Append(" --user $(id -u):$(id -g)");
        }
        if (memMb > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --memory {memMb:N0}m --memory-swap {memMb:N0}m");
        }
        if (cpuCores > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --cpus {cpuCores:N1}");
        }
        sb.Append(" --init");
        if (!string.Equals(network, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --network {network}");
        }
        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(CultureInfo.InvariantCulture, $" --platform {platform}");
        }
        if (!string.IsNullOrWhiteSpace(extraFlags))
        {
            foreach (var flag in extraFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append(CultureInfo.InvariantCulture, $" --label {flag}");
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $" {image} <tool> <args>");

        return sb.ToString();
    }

    private string ReconstructDockerRunCommand(CreateContainerParameters p)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{GetRuntimePath()} run");

        if (p.HostConfig?.AutoRemove == true)
        {
            sb.Append(" --rm");
        }
        if (!string.IsNullOrEmpty(p.Name))
        {
            var escapedName = p.Name.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" --name \"{escapedName}\"");
        }
        if (!string.IsNullOrEmpty(p.User))
        {
            var escapedUser = p.User.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" --user \"{escapedUser}\"");
        }

        if (p.HostConfig?.Binds != null)
        {
            foreach (var bind in p.HostConfig.Binds)
            {
                var escapedBind = bind.Replace("\"", "\\\"").Replace('\\', '/');
                sb.Append(CultureInfo.InvariantCulture, $" -v \"{escapedBind}\"");
            }
        }

        if (!string.IsNullOrEmpty(p.WorkingDir))
        {
            var escapedWorkingDir = p.WorkingDir.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" -w \"{escapedWorkingDir}\"");
        }

        if (p.HostConfig?.Memory > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --memory {p.HostConfig.Memory / (1024 * 1024)}m");
            if (p.HostConfig.MemorySwap == p.HostConfig.Memory)
            {
                sb.Append(CultureInfo.InvariantCulture, $" --memory-swap {p.HostConfig.MemorySwap / (1024 * 1024)}m");
            }
        }
        if (p.HostConfig?.NanoCPUs > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --cpus {p.HostConfig.NanoCPUs / 1_000_000_000.0:N1}");
        }
        if (p.HostConfig?.Init == true)
        {
            sb.Append(" --init");
        }

        if (!string.IsNullOrEmpty(p.HostConfig?.NetworkMode) &&
          !p.HostConfig.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
        {
            var escapedNetworkMode = p.HostConfig.NetworkMode.Replace("\"", "\\\"");
            sb.Append(CultureInfo.InvariantCulture, $" --network \"{escapedNetworkMode}\"");
        }

        if (p.Env != null)
        {
            foreach (var env in p.Env)
            {
                var eqIdx = env.IndexOf('=');
                if (eqIdx > 0)
                {
                    var key = env[..eqIdx];
                    var val = env[(eqIdx + 1)..];
                    if (IsSensitiveEnvironmentVariable(key))
                    {
                        val = "********";
                    }
                    var escapedEnv = $"{key}={val}".Replace("\"", "\\\"", StringComparison.Ordinal);
                    sb.Append(CultureInfo.InvariantCulture, $" -e \"{escapedEnv}\"");
                }
                else
                {
                    var escapedEnv = env.Replace("\"", "\\\"", StringComparison.Ordinal);
                    sb.Append(CultureInfo.InvariantCulture, $" -e \"{escapedEnv}\"");
                }
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $" {p.Image}");
        if (p.Cmd != null)
        {
            foreach (var arg in p.Cmd)
            {
                if (string.IsNullOrEmpty(arg))
                {
                    sb.Append(" \"\"");
                }
                else if (arg.AsSpan().ContainsAny(ShellSpecialAndWhitespaceChars))
                {
                    var escapedArg = arg.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
                    sb.Append(CultureInfo.InvariantCulture, $" \"{escapedArg}\"");
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture, $" {arg}");
                }
            }
        }

        return sb.ToString();
    }

    private static bool IsSensitiveEnvironmentVariable(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        ReadOnlySpan<char> span = key.AsSpan();
        Span<char> upper = key.Length <= 128 ? stackalloc char[key.Length] : new char[key.Length];
        span.ToUpperInvariant(upper);

        ReadOnlySpan<char> rUpper = upper;
        return rUpper.IndexOf("KEY".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("SECRET".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("PASSWORD".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("TOKEN".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("PASS".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("AUTH".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("CRED".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("CERT".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("PRIVATE".AsSpan(), StringComparison.Ordinal) >= 0 ||
               rUpper.IndexOf("SSH_AUTH_SOCK".AsSpan(), StringComparison.Ordinal) >= 0;
    }

    private static async Task<(bool live, string? errorMessage)> IsUnixSocketLiveAndWritableAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return (false, null);
        }
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            var ep = new System.Net.Sockets.UnixDomainSocketEndPoint(path);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1000);
            try
            {
                await socket.ConnectAsync(ep, timeoutCts.Token).ConfigureAwait(false);
                return (true, null);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return (false, $"Timeout connecting to UNIX socket '{path}'.");
            }
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            var nativeCode = ex.NativeErrorCode;
            var socketCode = ex.SocketErrorCode;
            string? errorMessage;
            if (socketCode == System.Net.Sockets.SocketError.AccessDenied ||
                nativeCode == 13 ||
                nativeCode == 1 ||
                nativeCode == 10013)
            {
                errorMessage = $"Access Denied: Current user does not have permission to access socket '{path}'. Ensure correct group membership (e.g. 'docker').";
            }
            else if (socketCode == System.Net.Sockets.SocketError.ConnectionRefused ||
                     nativeCode == 111 ||
                     nativeCode == 61)
            {
                errorMessage = $"Connection Refused: Docker daemon socket at '{path}' is not running or active.";
            }
            else
            {
                errorMessage = $"Socket Error ({socketCode}, Native: {nativeCode}): {ex.Message}";
            }
            return (false, errorMessage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, $"Unknown connection failure for socket '{path}': {ex.Message}");
        }
    }

    private static bool IsUnixSocketLiveAndWritable(string path, out string? errorMessage, CancellationToken ct = default)
    {
        errorMessage = null;
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            var ep = new System.Net.Sockets.UnixDomainSocketEndPoint(path);
            var connectTask = socket.ConnectAsync(ep);
            bool completed;
            try
            {
                completed = connectTask.Wait(1000, ct);
            }
            catch (AggregateException ex)
            {
                throw ex.InnerException ?? ex;
            }

            if (completed)
            {
                if (connectTask.IsFaulted)
                {
                    connectTask.GetAwaiter().GetResult();
                }
                return true;
            }
            else
            {
                socket.Close();
                errorMessage = $"Timeout connecting to UNIX socket '{path}'.";
                return false;
            }
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            var nativeCode = ex.NativeErrorCode;
            var socketCode = ex.SocketErrorCode;

            if (socketCode == System.Net.Sockets.SocketError.AccessDenied ||
                nativeCode == 13 ||
                nativeCode == 1 ||
                nativeCode == 10013)
            {
                errorMessage = $"Access Denied: Current user does not have permission to access socket '{path}'. Ensure correct group membership (e.g. 'docker').";
            }
            else if (socketCode == System.Net.Sockets.SocketError.ConnectionRefused ||
                     nativeCode == 111 ||
                     nativeCode == 61 ||
                     nativeCode == 10061)
            {
                errorMessage = $"Connection Refused: Socket '{path}' is not active. Ensure the Docker/Podman daemon is running.";
            }
            else if (socketCode == System.Net.Sockets.SocketError.AddressNotAvailable ||
                     nativeCode == 2 ||
                     nativeCode == 10049)
            {
                errorMessage = $"Socket not found or unavailable at '{path}'.";
            }
            else
            {
                errorMessage = $"Socket connection error ({socketCode}/{nativeCode}): {ex.Message}";
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            errorMessage = $"Access Denied: Current user does not have permission to write to socket '{path}'. {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to connect to socket '{path}': {ex.Message}";
            return false;
        }
    }

    private static void ValidateBinds(IList<string>? binds)
    {
        if (binds == null) return;
        foreach (var bind in binds)
        {
            if (string.IsNullOrWhiteSpace(bind)) continue;
            var parts = bind.Split(':');
            if (parts.Length > 0)
            {
                var hostPath = parts[0].Trim();
                if (string.IsNullOrEmpty(hostPath)) continue;

                string fullPath;
                try
                {
                    fullPath = GetCanonicalPath(hostPath);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    throw new DockerExecutionException($"Invalid mount path: '{hostPath}'. Details: {ex.Message}", ex);
                }

                string[] blockedPaths;
                if (OperatingSystem.IsWindows())
                {
                    blockedPaths = new[]
                    {
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
                        @"C:\Windows",
                        @"\\.\pipe"
                    };
                }
                else
                {
                    blockedPaths = new[]
                    {
                        "/etc",
                        "/var/run",
                        "/var/run/docker.sock",
                        "/var/run/containerd",
                        "/proc",
                        "/sys",
                        "/dev",
                        "/boot",
                        "/bin",
                        "/sbin",
                        "/usr/bin",
                        "/usr/sbin"
                    };
                }

                foreach (var blocked in blockedPaths)
                {
                    if (string.Equals(fullPath, blocked, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DockerExecutionException($"Mounting critical host path '{hostPath}' is blocked for security reasons.");
                    }

                    if (fullPath.StartsWith(blocked + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        fullPath.StartsWith(blocked + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DockerExecutionException($"Mounting paths under critical host directory '{blocked}' is blocked for security reasons.");
                    }
                }

                if (parts.Length > 1)
                {
                    var containerPath = parts[1].Trim();
                    if (containerPath.StartsWith("/sys", StringComparison.OrdinalIgnoreCase) ||
                        containerPath.StartsWith("/proc", StringComparison.OrdinalIgnoreCase) ||
                        containerPath.StartsWith("/dev", StringComparison.OrdinalIgnoreCase) ||
                        containerPath.StartsWith("/etc", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DockerExecutionException($"Mapping to container path '{containerPath}' is blocked for security reasons.");
                    }
                }
            }
        }
    }

    private static string GetCanonicalPath(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            var info = new System.IO.DirectoryInfo(resolved);
            if (info.Exists)
            {
                var target = info.LinkTarget;
                if (!string.IsNullOrEmpty(target))
                {
                    return info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
                }
            }
            var fileInfo = new System.IO.FileInfo(resolved);
            if (fileInfo.Exists)
            {
                var target = fileInfo.LinkTarget;
                if (!string.IsNullOrEmpty(target))
                {
                    return fileInfo.ResolveLinkTarget(true)?.FullName ?? fileInfo.FullName;
                }
            }
            return resolved;
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static (Uri uri, string runtime) ProbeUnixSocket(CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var uid = GetUnixId("-u", "1000");

        var candidates = new (string path, string name)[]
        {
            ("/var/run/docker.sock",                          "docker"),
            (Path.Combine(home, ".docker/run/docker.sock"),              "docker (user)"),
            ($"/run/user/{uid}/podman/podman.sock",                  "podman"),
            (Path.Combine(home, ".colima/default/docker.sock"),            "colima"),
            (Path.Combine(home, ".local/share/containers/podman/machine/podman.sock"), "podman (machine)"),
            (Path.Combine(home, ".orbstack/run/docker.sock"),             "orbstack"),
        };

        foreach (var (path, name) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var owner = GetUnixFileOwner(path, ct);
                if (owner != null && !string.Equals(owner, uid, StringComparison.Ordinal) && !string.Equals(owner, "0", StringComparison.Ordinal))
                {
                    Console.WriteLine($"[WARN] Insecure socket owner '{owner}' for socket '{path}'. Expected owner {uid} or 0.");
                    continue;
                }
            }
            ct.ThrowIfCancellationRequested();
            if (IsUnixSocketLiveAndWritable(path, out var error, ct))
            {
                return (new Uri($"unix://{path}"), name);
            }
            else if (error != null && error.StartsWith("Access Denied", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[WARN] {error}");
            }
        }

        // If no candidate is active/live, see if any candidate file exists on disk
        // We check in reverse order to prefer specific runtimes (orbstack, colima, podman) over generic defaults.
        for (int i = candidates.Length - 1; i >= 0; i--)
        {
            var (path, name) = candidates[i];
            if (File.Exists(path))
            {
                return (new Uri($"unix://{path}"), name);
            }
        }

        // If files are deleted when offline, check if the parent directories exist (specific to user home)
        for (int i = candidates.Length - 1; i >= 0; i--)
        {
            var (path, name) = candidates[i];
            if (!string.IsNullOrEmpty(home) && path.Contains(home, StringComparison.Ordinal))
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    return (new Uri($"unix://{path}"), name);
                }
            }
        }

        return (new Uri("unix:///var/run/docker.sock"), "docker (default)");
    }

    private static string? GetUnixFileOwner(string path, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        if (OwnerCache.TryGetValue(path, out var cachedOwner))
        {
            return cachedOwner;
        }
        try
        {
            var isMac = OperatingSystem.IsMacOS();
            using (var p = new Process())
            {
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = "stat",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (isMac)
                {
                    p.StartInfo.ArgumentList.Add("-f");
                    p.StartInfo.ArgumentList.Add("%u");
                }
                else
                {
                    p.StartInfo.ArgumentList.Add("-c");
                    p.StartInfo.ArgumentList.Add("%u");
                }
                p.StartInfo.ArgumentList.Add(path);

                ct.ThrowIfCancellationRequested();
                p.Start();
                if (p.WaitForExit(1000))
                {
                    var output = p.StandardOutput.ReadToEnd().Trim();
                    _ = p.StandardError.ReadToEnd();
                    if (p.ExitCode != 0)
                    {
                        OwnerCache[path] = null;
                        return null;
                    }
                    if (string.IsNullOrWhiteSpace(output))
                    {
                        OwnerCache[path] = null;
                        return null;
                    }
                    OwnerCache[path] = output;
                    return output;
                }
                else
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(500);
                    }
                    catch
                    {
                        // Ignore
                    }
                    return null;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed with Win32Exception for '{path}'", ex);
            return null;
        }
        catch (IOException ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed with IOException for '{path}'", ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed with UnauthorizedAccessException for '{path}'", ex);
            return null;
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed for '{path}'", ex);
            return null;
        }
    }

    private static string GetUnixId(string arg, string fallback)
    {
        if (OperatingSystem.IsWindows()) return fallback;
        if (string.Equals(arg, "-u", StringComparison.Ordinal) && _cachedUid != null) return _cachedUid;
        if (string.Equals(arg, "-g", StringComparison.Ordinal) && _cachedGid != null) return _cachedGid;

        UnixIdSemaphore.Wait();
        try
        {
            if (string.Equals(arg, "-u", StringComparison.Ordinal) && _cachedUid != null) return _cachedUid;
            if (string.Equals(arg, "-g", StringComparison.Ordinal) && _cachedGid != null) return _cachedGid;

            var val = GetUnixIdInternal(arg, fallback);
            if (string.Equals(arg, "-u", StringComparison.Ordinal)) _cachedUid = val;
            if (string.Equals(arg, "-g", StringComparison.Ordinal)) _cachedGid = val;
            return val;
        }
        finally
        {
            UnixIdSemaphore.Release();
        }
    }

    private static string GetUnixIdInternal(string arg, string fallback)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                if (string.Equals(arg, "-u", StringComparison.Ordinal))
                {
                    return geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                if (string.Equals(arg, "-g", StringComparison.Ordinal))
                {
                    return getegid().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Fall back to executing "id"
            }
        }

        Process? p = null;
        try
        {
            p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "id",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.StartInfo.ArgumentList.Add(arg);
            p.Start();

            var output = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();

            if (p.WaitForExit(1000))
            {
                var id = output.Trim();
                if (!string.IsNullOrEmpty(id) && int.TryParse(id, out _))
                {
                    return id;
                }
            }
            else
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(500);
                }
                catch
                {
                    // Ignore
                }
                ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe for '{arg}' timed out", null);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Fallback gracefully
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe failed for '{arg}'", ex);
        }
        finally
        {
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(500);
                    }
                }
                catch
                {
                    // Ignore
                }
                try
                {
                    p.Dispose();
                }
                catch
                {
                    // Ignore
                }
            }
        }
        return fallback;
    }

    private static async Task<string> GetUnixIdInternalAsync(string arg, string fallback, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                if (string.Equals(arg, "-u", StringComparison.Ordinal))
                {
                    return geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                if (string.Equals(arg, "-g", StringComparison.Ordinal))
                {
                    return getegid().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Fall back
            }
        }

        Process? p = null;
        try
        {
            p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "id",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.StartInfo.ArgumentList.Add(arg);
            p.Start();

            var readOutTask = p.StandardOutput.ReadToEndAsync(ct);
            var readErrTask = p.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1000);
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                var id = (await readOutTask.ConfigureAwait(false)).Trim();
                _ = await readErrTask.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(id) && int.TryParse(id, out _))
                {
                    return id;
                }
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill();
                    await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
                try
                {
                    await Task.WhenAny(Task.WhenAll(readOutTask, readErrTask), Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
                ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe for '{arg}' timed out", null);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Fallback gracefully
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe failed for '{arg}'", ex);
        }
        finally
        {
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignore
                }
                try
                {
                    p.Dispose();
                }
                catch
                {
                    // Ignore
                }
            }
        }
        return fallback;
    }

    public ValueTask<bool> PingAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _connectionProvider.PingAsync(ct);
    }

    public Task<SystemInfoResponse?> GetSystemInfoAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _connectionProvider.GetSystemInfoAsync(ct);
    }

    public Task<IList<ContainerListResponse>> ListContainersAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _containerManager.ListContainersAsync(ct);
    }

    public Task<IList<ImagesListResponse>> ListImagesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _imageManager.ListImagesAsync(ct);
    }

    public Task StopContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _containerManager.StopContainerAsync(containerId, ct);
    }

    public Task StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _containerManager.StartContainerAsync(containerId, ct);
    }

    public Task RestartContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _containerManager.RestartContainerAsync(containerId, ct);
    }

    public Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _containerManager.RemoveContainerAsync(containerId, ct);
    }

    public Task RemoveImageAsync(string imageId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _imageManager.RemoveImageAsync(imageId, ct);
    }

    public Task<(int pulled, int failed)> UpdateAllImagesAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _imageManager.UpdateAllImagesAsync(progress, ct);
    }

    public Task<int> PruneDanglingImagesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _imageManager.PruneDanglingImagesAsync(ct);
    }

    public Task<string> GetContainerLogsAsync(string containerId, int tailLines = 50, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _containerManager.GetContainerLogsAsync(containerId, tailLines, ct);
    }

    public static (int imageCount, long totalSizeBytes, long reclaimableBytes) ComputeDiskUsage(IList<ImagesListResponse> images)
      => DockerImageManager.ComputeDiskUsage(images);

    public Task<(int imageCount, long totalSizeBytes, long reclaimableBytes)> GetDiskUsageSummaryAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _imageManager.GetDiskUsageSummaryAsync(ct);
    }

    private static void CleanupDanglingContainers(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _cleanupExecuted, 1) != 0)
        {
            return;
        }
        if (_staticClientForCleanup == null)
        {
            return;
        }

        var keys = ActiveContainers.Keys;
        if (keys.Count == 0)
        {
            return;
        }

        foreach (var key in keys)
        {
            if (ActiveContainers.TryRemove(key, out var shouldAutoRemove))
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    _staticClientForCleanup.Containers.StopContainerAsync(key, new ContainerStopParameters { WaitBeforeKillSeconds = 1 }, cts.Token).GetAwaiter().GetResult();
                    if (shouldAutoRemove)
                    {
                        _staticClientForCleanup.Containers.RemoveContainerAsync(key, new ContainerRemoveParameters { Force = true }, cts.Token).GetAwaiter().GetResult();
                    }
                }
                catch (Exception)
                {
                    // Best effort on exit
                }
            }
        }
    }

    internal static void DrainLines(StringBuilder buffer, ReadOnlySpan<char> textSpan, Func<string, bool>? handler)
    {
        if (textSpan.IsEmpty)
        {
            return;
        }

        string[]? batchArray = null;
        int batchCount = 0;

        void AddLine(string line)
        {
            if (handler != null)
            {
                if (batchArray == null)
                {
                    batchArray = System.Buffers.ArrayPool<string>.Shared.Rent(16);
                }
                if (batchCount >= batchArray.Length)
                {
                    var newArray = System.Buffers.ArrayPool<string>.Shared.Rent(batchArray.Length * 2);
                    Array.Copy(batchArray, newArray, batchCount);
                    System.Buffers.ArrayPool<string>.Shared.Return(batchArray);
                    batchArray = newArray;
                }
                batchArray[batchCount++] = line;
            }
        }

        int start = 0;
        while (start < textSpan.Length)
        {
            int newlineIdx = textSpan[start..].IndexOf('\n');
            if (newlineIdx < 0)
            {
                break;
            }

            int lineEndRelative = newlineIdx;
            int absoluteLineEnd = start + lineEndRelative;

            // Trim carriage return if present
            int lineEndTrimmed = absoluteLineEnd;
            if (lineEndTrimmed > start && textSpan[lineEndTrimmed - 1] == '\r')
            {
                lineEndTrimmed--;
            }

            string completedLine;
            if (buffer.Length > 0)
            {
                buffer.Append(textSpan[start..lineEndTrimmed]);
                completedLine = buffer.ToString();
                buffer.Clear();
            }
            else
            {
                completedLine = textSpan[start..lineEndTrimmed].ToString();
            }

            AddLine(completedLine);
            start = absoluteLineEnd + 1;
        }

        if (start < textSpan.Length)
        {
            buffer.Append(textSpan[start..]);
        }

        if (batchCount > 0 && batchArray != null)
        {
            var finalCount = batchCount;
            var finalArray = batchArray;
            SafeInvoke(() =>
            {
                try
                {
                    for (int idx = 0; idx < finalCount; idx++)
                    {
                        handler!(finalArray[idx]);
                    }
                }
                finally
                {
                    for (int idx = 0; idx < finalCount; idx++)
                    {
                        finalArray[idx] = null!;
                    }
                    System.Buffers.ArrayPool<string>.Shared.Return(finalArray);
                }
            });
        }

        // Defensive OOM Shield: If a container goes rogue and outputs endless text
        // without newlines, prevent the StringBuilder from crashing the host IDE.
        if (buffer.Length > 8 * 1024 * 1024) // 8 MB limit
        {
            buffer.Clear();
            ContainerTelemetry.TrackError("DockerExecutionStrategy", "OOM Protection triggered: buffer exceeded 8MB threshold without newlines", null);
        }
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
        double? remoteCpuCores = _connectionProvider.CachedSystemInfo?.NCPU;
        return Services.Docker.DockerCommandBuilder.BuildContainerParameters(
          image,
          command,
          _settingsService,
          _cachedUid,
          _cachedGid,
          (cmd, msg) => SdkLog(cmd, msg),
          remoteCpuCores);
    }

    private async Task<string?> EnsureImageAsync(string image, ToolCommand command, CancellationToken ct)
    {
        string? imageDigest = null;
        var platform = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto")?.Trim();
        var pullPolicy = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.PullPolicySetting, "if-not-present");

        bool imageExistsLocally = false;
        try
        {
            var inspectResponse = await _client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            imageDigest = inspectResponse.ID;
            imageExistsLocally = true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            imageExistsLocally = false;
        }

        bool shouldPull = pullPolicy switch
        {
            "always" => true,
            "never" => false,
            _ => !imageExistsLocally
        };

        if (!imageExistsLocally && string.Equals(pullPolicy, "never", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Image '{image}' not found locally and pull policy is 'never'.");
        }

        if (shouldPull)
        {
            SdkLog(command, string.Equals(pullPolicy, "always", StringComparison.Ordinal) && imageExistsLocally
              ? $"[Docker SDK] Pull policy 'always' — refreshing '{image}'..."
              : $"[Docker SDK] Image '{image}' not found locally. Pulling...");

            var pullParams = new ImagesCreateParameters { FromImage = image };
            if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
            {
                pullParams.Platform = platform;
            }

            var progressHandler = new Progress<JSONMessage>(msg =>
            {
                if (msg == null)
                {
                    return;
                }
                try
                {
                    var progressText = string.IsNullOrWhiteSpace(msg.ProgressMessage)
                        ? msg.Status
                        : $"{msg.Status} {msg.ProgressMessage}";

                    if (!string.IsNullOrWhiteSpace(progressText))
                    {
                        SdkLog(command, $"[Docker Pull] {progressText}");
                    }
                }
                catch (Exception)
                {
                    // Keep the image pull task running through status formatting errors
                }
            });

            try
            {
                try
                {
                    await _client.Images.CreateImageAsync(pullParams, null, progressHandler, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !string.IsNullOrWhiteSpace(pullParams.Platform))
                {
                    SdkLog(command, $"[Docker Pull Warning] Pull failed with platform '{pullParams.Platform}': {ex.Message}. Falling back to default host architecture.");
                    pullParams.Platform = null;
                    await _client.Images.CreateImageAsync(pullParams, null, progressHandler, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (imageExistsLocally)
                {
                    SdkLog(command, $"[Docker Pull Warning] Pull failed for '{image}': {ex.Message}. Falling back to cached local version.");
                }
                else
                {
                    throw;
                }
            }

            SdkLog(command, $"[Docker SDK] Pull complete for '{image}'.");

            try
            {
                var postPull = await _client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
                imageDigest = postPull.ID;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", $"Post-pull digest inspect failed for '{image}'", ex);
            }
        }

        if (imageDigest != null)
        {
            var shortDigest = imageDigest.ShortId();
            SdkLog(command, $"[Docker SDK] Resolved digest: {shortDigest}...");
        }

        return imageDigest;
    }

    internal record ResourceProfile(long PeakMemoryBytes, double MaxCpuPercent, int SampleCount, bool OomKilled);

    private async Task<ResourceProfile?> CollectResourceStatsAsync(
      string containerId, ToolCommand command, CancellationToken ct)
    {
        long peakMemory = 0;
        double maxCpu = 0;
        int sampleCount = 0;
        long prevCpuTotal = 0;
        long prevSystemTotal = 0;
        var statsLock = new System.Threading.Lock();

        try
        {
            var progress = new StatelessProgress<ContainerStatsResponse>(stats =>
            {
                if (stats.MemoryStats?.Usage > 0)
                {
                    var currentMem = (long)stats.MemoryStats.Usage;
                    long current;
                    do { current = Interlocked.Read(ref peakMemory); }
                    while (currentMem > current && Interlocked.CompareExchange(ref peakMemory, currentMem, current) != current);
                }

                if (stats.CPUStats?.CPUUsage?.TotalUsage > 0 && stats.CPUStats?.SystemUsage > 0)
                {
                    var cpuTotal = (long)stats.CPUStats.CPUUsage.TotalUsage;
                    var systemTotal = (long)stats.CPUStats.SystemUsage;
                    var onlineCpus = (int)(stats.CPUStats.OnlineCPUs > 0 ? stats.CPUStats.OnlineCPUs : 1);

                    lock (statsLock)
                    {
                        if (prevCpuTotal > 0 && prevSystemTotal > 0)
                        {
                            var cpuDelta = (double)(cpuTotal - prevCpuTotal);
                            var systemDelta = (double)(systemTotal - prevSystemTotal);
                            if (systemDelta > 0 && onlineCpus > 0)
                            {
                                var cpuPercent = (cpuDelta / systemDelta) * onlineCpus * 100.0;
                                var currentMax = Volatile.Read(ref maxCpu);
                                while (cpuPercent > currentMax && Interlocked.CompareExchange(ref maxCpu, cpuPercent, currentMax) != currentMax)
                                {
                                    currentMax = Volatile.Read(ref maxCpu);
                                }
                            }
                        }
                        prevCpuTotal = cpuTotal;
                        prevSystemTotal = systemTotal;
                    }
                }
                Interlocked.Increment(ref sampleCount);
            });

            await _client.Containers.GetContainerStatsAsync(
              containerId, new ContainerStatsParameters { Stream = true }, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Ignore */ }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SdkLog(command, $"[Docker SDK] Stats collection ended: {ex.Message}", RankInfo);
        }

        if (Interlocked.CompareExchange(ref sampleCount, 0, 0) == 0) return null;
        return new ResourceProfile(Interlocked.Read(ref peakMemory), Math.Round(Volatile.Read(ref maxCpu), 1), sampleCount, false);
    }

    private sealed class StatelessProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private async Task<(long exitCode, string output, bool wasCancelled, ResourceProfile? profile)> RunContainerAsync(
      CreateContainerParameters createParams, ToolCommand command, CancellationToken ct)
    {
        var outputBuilder = new StringBuilder();
        var executable = (command.Executable ?? command.ToolName).Replace("\r", "");
        long exitCode = -1;
        bool wasCancelled = false;

        var container = await _client.Containers.CreateContainerAsync(createParams, ct).ConfigureAwait(false);
        var containerId = container.ID;
        var autoRemove = createParams.HostConfig?.AutoRemove ?? true;
        ActiveContainers.TryAdd(containerId, autoRemove);

        var cancelRegistration = ct.CanBeCanceled
          ? ct.Register(() =>
          {
              wasCancelled = true;
              try
              {
                  var stopTask = _client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 2 });
#pragma warning disable VSTHRD110
                  stopTask.ContinueWith(t =>
    {
        if (t.IsFaulted)
            ContainerTelemetry.TrackError("DockerExecutionStrategy",
          $"Async container stop failed for '{containerId.ShortId()}'", t.Exception?.InnerException);
    }, TaskScheduler.Default);
#pragma warning restore VSTHRD110
              }
              catch (Exception ex) when (ex is not OutOfMemoryException)
              {
                  ContainerTelemetry.TrackError("DockerExecutionStrategy", $"Cancel-time container stop failed for '{containerId.ShortId()}'", ex);
              }
          })
          : (CancellationTokenRegistration?)null;

        ResourceProfile? profile = null;
        Task? readTask = null;
        Task<ResourceProfile?>? statsTask = null;
        CancellationTokenSource? statsCts = null;
        CancellationTokenSource? readCts = null;

        try
        {
            SdkLog(command, $"[Docker SDK] Spawning {executable} in {createParams.Image}...");
            SdkLog(command, $"[Docker SDK] Command: {string.Join(" ", createParams.Cmd ?? [])}", RankInfo);

            readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var readToken = readCts.Token;

            using var stream = await _client.Containers.AttachContainerAsync(
              containerId, false,
              new ContainerAttachParameters { Stream = true, Stdout = true, Stderr = true }, ct).ConfigureAwait(false);

            var containerStopwatch = Stopwatch.StartNew();
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
            SdkLog(command, $"[Docker SDK] Container {containerId.ShortId()} started.", RankInfo);

            readTask = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                var stdoutBuf = new StringBuilder();
                var stderrBuf = new StringBuilder();
                var decoder = Encoding.UTF8.GetDecoder();
                var charBuf = System.Buffers.ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(buffer.Length));

                try
                {
                    while (!readToken.IsCancellationRequested)
                    {
                        readToken.ThrowIfCancellationRequested();
                        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, readToken).ConfigureAwait(false);
                        if (result.EOF) break;

                        var charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                        var textSpan = charBuf.AsSpan(0, charCount);
                        lock (outputBuilder)
                        {
                            outputBuilder.Append(textSpan);
                        }

                        if (result.Target == MultiplexedStream.TargetStream.StandardError)
                        {
                            DrainLines(stderrBuf, textSpan, command.ErrorHandler);
                        }
                        else
                        {
                            DrainLines(stdoutBuf, textSpan, command.OutputHandler);
                        }
                    }
                }
                catch (OperationCanceledException) { /* Ignore */ }
                finally
                {
                    System.Buffers.ArrayPool<char>.Shared.Return(charBuf);
                    if (stdoutBuf.Length > 0)
                    {
                        var finalStdout = stdoutBuf.ToString();
                        SafeInvoke(() => command.OutputHandler?.Invoke(finalStdout));
                    }
                    if (stderrBuf.Length > 0)
                    {
                        var finalStderr = stderrBuf.ToString();
                        SafeInvoke(() => command.ErrorHandler?.Invoke(finalStderr));
                    }
                }
            }, CancellationToken.None);

            statsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            statsTask = CollectResourceStatsAsync(containerId, command, statsCts.Token);

            var logRank = _currentLogLevelRank.Value;
            try
            {
                var wait = await _client.Containers.WaitContainerAsync(containerId, ct).ConfigureAwait(false);
                exitCode = wait.StatusCode;
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                if (logRank >= RankErrors)
                    SafeInvoke(() => command.ErrorHandler?.Invoke("[Docker SDK] Container execution was cancelled."));
            }

            if (statsCts != null) await statsCts.CancelAsync().ConfigureAwait(false);
            if (readCts != null) await readCts.CancelAsync().ConfigureAwait(false);
            if (readTask != null) await readTask.ConfigureAwait(false);

            try { profile = await statsTask.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "Resource stats collection failed", ex);
            }

            try
            {
                var inspect = await _client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
                if (inspect.State.OOMKilled && profile != null)
                {
                    profile = profile with { OomKilled = true };
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (exitCode == 137 && profile != null)
                {
                    profile = profile with { OomKilled = true };
                }
            }

            containerStopwatch.Stop();

            var peakInfo = profile != null
              ? $", peak RAM: {profile.PeakMemoryBytes / (1024 * 1024)} MB, max CPU: {profile.MaxCpuPercent:F1}%"
               + (profile.OomKilled ? " ⚠️ OOM KILLED" : "")
              : "";
            SdkLog(command, $"[Docker SDK] Container {containerId.ShortId()} stopped — exit code {exitCode}, ran {containerStopwatch.Elapsed.TotalSeconds:F2}s{peakInfo}.", RankInfo);
        }
        finally
        {
#pragma warning disable VSTHRD103 
            cancelRegistration?.Dispose();
#pragma warning restore VSTHRD103

            try { if (readCts != null) { await readCts.CancelAsync().ConfigureAwait(false); readCts.Dispose(); } } catch { /* Ignore */ }
            try { if (statsCts != null) { await statsCts.CancelAsync().ConfigureAwait(false); statsCts.Dispose(); } } catch { /* Ignore */ }

            if (readTask != null)
                try { await readTask.ConfigureAwait(false); } catch { /* Ignore */ }

            if (statsTask != null)
                try { profile = await statsTask.ConfigureAwait(false); } catch { /* Ignore */ }

            ActiveContainers.TryRemove(containerId, out _);
        }

        string finalOutput;
        lock (outputBuilder) { finalOutput = outputBuilder.ToString(); }

        return (exitCode, finalOutput, wasCancelled, profile);
    }

    /// <summary>
    /// Translates a host tool command into an ephemeral container execution payload.
    /// Manages pulling images, configuring container properties, executing the command, and streaming results.
    /// </summary>
    /// <param name="command">The tool command payload to execute.</param>
    /// <returns>A tuple indicating success status and any buffered output if captured.</returns>
    public async Task<(bool success, string output)> ExecuteAsync(ToolCommand command)
    {
        using var activity = DockerActivitySource.StartActivity("DockerExecutionStrategy.Execute");
        activity?.SetTag("tool.name", command.ToolName);
        activity?.SetTag("tool.executable", command.Executable);
        ThrowIfDisposed();

        if (IsTargetingEmptyGhdlLibrary(command))
        {
            SdkLog(command, "[Docker SDK] Bypassing GHDL make/elaboration on empty library targeting to prevent compilation failures.", RankInfo);
            return (true, string.Empty);
        }

        var executable = command.Executable ?? command.ToolName;

        if (!string.IsNullOrEmpty(executable))
        {
            if (executable.AsSpan().ContainsAny(DockerCommandBuilder.ShellSpecialChars))
            {
                throw new ArgumentException("Command executable contains prohibited shell control characters.", nameof(command));
            }

            foreach (var ch in executable)
            {
                if (char.IsControl(ch))
                {
                    throw new ArgumentException("Command executable contains invalid control characters.", nameof(command));
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
                        throw new ArgumentException("Nested shell expansion or command separators detected in argument: " + arg, nameof(command));
                    }
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        string image = ContainerExtensionModule.FallbackImage;
        string? imageDigest = null;
        string? reconstructedDockerRun = null;
        long exitCode = -1;
        bool wasCancelled = false;
        ResourceProfile? resourceProfile = null;

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
            cts = CancellationTokenSource.CreateLinkedTokenSource(_strategyCts.Token, timeoutCts.Token);
        }
        else
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(_strategyCts.Token);
        }
        var ct = cts.Token;

        _currentLogLevelRank.Value = LogLevelRank(_settingsService.SafeGetSetting<string>(ContainerExtensionModule.LogLevelSetting, "Verbose"));
        _currentShowTimestamps.Value = _settingsService.SafeGetSetting<bool>(ContainerExtensionModule.ShowTimestampsSetting, true);

        SdkLog(command, $"[Docker SDK] ExecuteAsync started for '{executable}'.", RankInfo);

        string? errorMessage = null;

        try
        {
            if (_daemonUri.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase))
            {
                var socketPath = _daemonUri.LocalPath;
                var (live, socketErr) = await IsUnixSocketLiveAndWritableAsync(socketPath, ct).ConfigureAwait(false);
                if (!live)
                {
                    throw new DockerExecutionException(socketErr ?? $"Docker socket at '{socketPath}' is not active or readable.");
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
                if (!VerifyWindowsNamedPipe(pipeName))
                {
                    throw new DockerExecutionException($"Insecure or unreachable named pipe connection detected for '{pipeName}'.");
                }
            }

            var resolvedWorkingDir = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? Directory.GetCurrentDirectory() : command.WorkingDirectory;
            var workingDirFull = Path.GetFullPath(resolvedWorkingDir);
            command.PrepareCommand(System.Runtime.InteropServices.OSPlatform.Linux, path => Services.Docker.DockerCommandBuilder.MapPathToContainer(path, workingDirFull));

            if (!OperatingSystem.IsWindows())
            {
                await EnsureUnixIdsLoadedAsync(ct).ConfigureAwait(false);
            }

            SdkLog(command, $"[Docker SDK] Step 1: Resolving image for tool '{executable}'...", RankInfo);
            image = ResolveImage(command.ToolName ?? string.Empty);

            SdkLog(command, $"[Docker SDK] Step 1: Resolved image: {image}", RankInfo);

            SdkLog(command, $"[Docker SDK] Step 2: Building container parameters...", RankInfo);
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

            ValidateBinds(createParams.HostConfig?.Binds);

            SdkLog(command, $"[Docker SDK] Step 2: Cmd = [{string.Join(", ", createParams.Cmd ?? [])}]", RankInfo);
            SdkLog(command, $"[Docker SDK] Step 2: WorkingDir = {createParams.WorkingDir}, Binds = [{string.Join(", ", createParams.HostConfig?.Binds ?? [])}]", RankInfo);

            SdkLog(command, $"[Docker SDK] Step 3: Ensuring image '{image}' is available...", RankInfo);
            imageDigest = await EnsureImageAsync(image, command, ct).ConfigureAwait(false);
            SdkLog(command, $"[Docker SDK] Step 3: Image ready. Digest = {imageDigest ?? "(none)"}", RankInfo);

            reconstructedDockerRun = ReconstructDockerRunCommand(createParams);
            SdkLog(command, $"[Docker SDK] Equivalent CLI: {reconstructedDockerRun}", RankInfo);

            SdkLog(command, $"[Docker SDK] Step 4: Creating and starting container...", RankInfo);
            var result = await RunContainerAsync(createParams, command, ct).ConfigureAwait(false);
            exitCode = result.exitCode;
            wasCancelled = result.wasCancelled;
            resourceProfile = result.profile;

            SdkLog(command, $"[Docker SDK] Container finished. Exit code: {exitCode}", RankInfo);
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
            var retentionStr = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.TelemetryRetentionSetting, "100");
            if (string.Equals(retentionStr, "None", StringComparison.Ordinal))
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
                });
            }
            else
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

            if (stopwatch.Elapsed.TotalSeconds > 30)
            {
                var status = exitCode == 0 ? "succeeded" : (wasCancelled ? "cancelled" : "failed");
                Console.WriteLine(
                  $"[ContainerExtension] {status}: {executable} completed in {stopwatch.Elapsed.TotalSeconds:F1}s (exit {exitCode})");
            }
        }
    }

    private static readonly System.Threading.Lock WeakProcessLock = new();

    public WeakReference<Process> StartWeakProcess(ToolCommand command)
    {
        lock (WeakProcessLock)
        {
            ThreadPool.QueueUserWorkItem(static state =>
            {
                var (strategy, cmd) = state;
                try
                {
                    _ = strategy.ExecuteAsync(cmd);
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("DockerExecutionStrategy", "StartWeakProcess task crashed", ex, cmd.Executable);
                    var errMsg = $"[ERROR] Execution of background task '{cmd.Executable}' failed: {ex.Message}";
                    SafeInvoke(() =>
                    {
                        (cmd.ErrorHandler ?? cmd.OutputHandler)?.Invoke(errMsg);
                    });
                }
            }, (this, command), preferLocal: false);
            return new WeakReference<Process>(null!);
        }
    }

    public string GetStrategyName() => "Docker Container (DotNet API)";

    public string GetStrategyKey() => ToolKey;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
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

        if (Interlocked.CompareExchange(ref _staticClientForCleanup, null, _client) == _client)
        {
            AppDomain.CurrentDomain.ProcessExit -= CleanupDanglingContainers;
            if (_cancelKeyPressHandler != null)
            {
                try
                {
                    Console.CancelKeyPress -= _cancelKeyPressHandler;
                }
                catch
                {
                    // Ignore Console unregistration errors on shutdown
                }
                _cancelKeyPressHandler = null;
            }
            CleanupDanglingContainers(null, EventArgs.Empty);

            Volatile.Write(ref _cleanupExecuted, 0);
        }

        _connectionProvider.Dispose();
        _client.Dispose();
        _strategyLock.Dispose();
        ContainerTelemetry.Shutdown();
    }

    public IAsyncEnumerable<string> StreamContainerLogsAsync(string containerId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _containerManager.StreamContainerLogsAsync(containerId, ct);
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
        if (!string.IsNullOrWhiteSpace(user))
        {
            input = input.Replace(user, "***", StringComparison.OrdinalIgnoreCase);
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
}
