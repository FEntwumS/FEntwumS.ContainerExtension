#pragma warning disable VSTHRD002, VSTHRD105, VSTHRD110
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using ContainerExtension.Services.Docker;

namespace ContainerExtension;

public sealed class DockerExecutionStrategy : IToolExecutionStrategy, IDisposable
{
    private const string ToolKey = "DockerExecutionStrategy";
    private const string ContainerWorkDir = "/workspace";

    private static readonly Lazy<string> CachedUid = new(() => GetUnixId("-u", "1000"));
    private static readonly Lazy<string> CachedGid = new(() => GetUnixId("-g", "1000"));

    private readonly ISettingsService _settingsService;
    private readonly DockerClient _client;

    private readonly DockerConnectionProvider _connectionProvider;
    private readonly DockerImageManager _imageManager;
    private readonly DockerContainerManager _containerManager;

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
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        else
            action();
    }

    private void SdkLog(ToolCommand command, string message, int minRank = RankVerbose)
    {
        if (_currentLogLevelRank.Value >= minRank)
        {
            var line = _currentShowTimestamps.Value ? $"[{DateTime.Now:HH:mm:ss.fff}] {message}" : message;
            SafeInvoke(() => (command.OutputHandler ?? command.ErrorHandler)?.Invoke(line));
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
                uri = new Uri(uriText);
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
                (uri, runtime) = ProbeUnixSocket();
            }
        }

        DetectedRuntime = runtime;
        if (uri is null)
            throw new InvalidOperationException("Could not resolve a Docker daemon URI. Ensure Docker is installed and running, or set the DOCKER_HOST environment variable.");
            
        using var config = new DockerClientConfiguration(uri);
        _client = config.CreateClient(new System.Version(1, 44));

        _connectionProvider = new DockerConnectionProvider(_client);
        _imageManager = new DockerImageManager(_client, _settingsService);
        _containerManager = new DockerContainerManager(_client);

        if (Interlocked.CompareExchange(ref _staticClientForCleanup, _client, null) == null)
        {
            AppDomain.CurrentDomain.ProcessExit += CleanupDanglingContainers;
            _cancelKeyPressHandler = (s, e) => CleanupDanglingContainers(s, e);
            Console.CancelKeyPress += _cancelKeyPressHandler;
        }
    }

    public string GetRuntimePath()
    {
        var p = _settingsService.SafeGetSetting(ContainerExtensionModule.DockerRuntimePathSetting, "");
        if (string.IsNullOrWhiteSpace(p)) return "docker";
        return p.Contains(' ') && !p.StartsWith('"') ? $"\"{p}\"" : p;
    }

    public Dictionary<string, string> GetActiveSettingsSummary()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Image"] = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.FallbackImage),
            ["Pull Policy"] = _settingsService.SafeGetSetting(ContainerExtensionModule.PullPolicySetting, "if-not-present"),
            ["Platform"] = _settingsService.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto"),
            ["Memory"] = _settingsService.SafeGetSetting(ContainerExtensionModule.MemoryLimitSetting, 0.0) is var m && m > 0 ? $"{m:N0} MB" : "No limit",
            ["CPU"] = _settingsService.SafeGetSetting(ContainerExtensionModule.CpuLimitSetting, 0.0) is var c && c > 0 ? $"{c:N0} cores" : "No limit",
            ["Timeout"] = _settingsService.SafeGetSetting(ContainerExtensionModule.TimeoutSetting, 0.0) is var t && t > 0 ? $"{t:N0} min" : "None",
            ["Network"] = _settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge"),
            ["Auto-Remove"] = _settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true) ? "On" : "Off",
            ["Log Level"] = _settingsService.SafeGetSetting(ContainerExtensionModule.LogLevelSetting, "Verbose"),
            ["Timestamps"] = _settingsService.SafeGetSetting(ContainerExtensionModule.ShowTimestampsSetting, true) ? "On" : "Off",
            ["Name Prefix"] = _settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-") is var n && string.IsNullOrWhiteSpace(n) ? "(none)" : n,
            ["Extra Labels"] = _settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "") is var e && string.IsNullOrWhiteSpace(e) ? "None" : e,
            ["Dashboard Refresh"] = _settingsService.SafeGetSetting(ContainerExtensionModule.DashboardRefreshSetting, "Manual"),
            ["Retention"] = _settingsService.SafeGetSetting(ContainerExtensionModule.TelemetryRetentionSetting, "100"),
            ["Runtime Path"] = _settingsService.SafeGetSetting(ContainerExtensionModule.DockerRuntimePathSetting, "") is var r && string.IsNullOrWhiteSpace(r) ? "docker (PATH)" : r
        };
    }

    public string GetDefaultImage()
    {
        var image = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, "");
        return string.IsNullOrWhiteSpace(image) ? ContainerExtensionModule.FallbackImage : image;
    }

    public async Task PrePullImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await _client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            return; 
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { /* Image not found locally, proceed to pull */ }

        var platform = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
        var pullParams = new ImagesCreateParameters { FromImage = image };
        if (!string.IsNullOrWhiteSpace(platform) && !platform.Equals("auto", StringComparison.OrdinalIgnoreCase))
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
        if (autoRemove) sb.Append(" --rm");
        if (!string.IsNullOrWhiteSpace(namePrefix))
            sb.Append(CultureInfo.InvariantCulture, $" --name {namePrefix.TrimEnd('-')}-<tool>-<hhmmss>");
        sb.Append(CultureInfo.InvariantCulture, $" -v \"$(pwd)\":{ContainerWorkDir} -w {ContainerWorkDir}");
        if (OperatingSystem.IsLinux())
            sb.Append(" --user $(id -u):$(id -g)");
        if (memMb > 0) sb.Append(CultureInfo.InvariantCulture, $" --memory {memMb:N0}m --memory-swap {memMb:N0}m");
        if (cpuCores > 0) sb.Append(CultureInfo.InvariantCulture, $" --cpus {cpuCores:N1}");
        sb.Append(" --init");
        if (!string.Equals(network, "bridge", StringComparison.OrdinalIgnoreCase))
            sb.Append(CultureInfo.InvariantCulture, $" --network {network}");
        if (!string.IsNullOrWhiteSpace(platform) && !platform.Equals("auto", StringComparison.OrdinalIgnoreCase))
            sb.Append(CultureInfo.InvariantCulture, $" --platform {platform}");
        if (!string.IsNullOrWhiteSpace(extraFlags))
        {
            foreach (var flag in extraFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                sb.Append(CultureInfo.InvariantCulture, $" --label {flag}");
        }
        sb.Append(CultureInfo.InvariantCulture, $" {image} <tool> <args>");

        return sb.ToString();
    }

    private string ReconstructDockerRunCommand(CreateContainerParameters p)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{GetRuntimePath()} run");

        if (p.HostConfig?.AutoRemove == true) sb.Append(" --rm");
        if (!string.IsNullOrEmpty(p.Name)) sb.Append(CultureInfo.InvariantCulture, $" --name {p.Name}");
        if (!string.IsNullOrEmpty(p.User)) sb.Append(CultureInfo.InvariantCulture, $" --user {p.User}");

        if (p.HostConfig?.Binds != null)
            foreach (var bind in p.HostConfig.Binds)
                sb.Append(CultureInfo.InvariantCulture, $" -v \"{bind.Replace('\\', '/')}\"");

        if (!string.IsNullOrEmpty(p.WorkingDir)) sb.Append(CultureInfo.InvariantCulture, $" -w {p.WorkingDir}");

        if (p.HostConfig?.Memory > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --memory {p.HostConfig.Memory / (1024 * 1024)}m");
            if (p.HostConfig.MemorySwap == p.HostConfig.Memory)
                sb.Append(CultureInfo.InvariantCulture, $" --memory-swap {p.HostConfig.MemorySwap / (1024 * 1024)}m");
        }
        if (p.HostConfig?.NanoCPUs > 0) sb.Append(CultureInfo.InvariantCulture, $" --cpus {p.HostConfig.NanoCPUs / 1_000_000_000.0:N1}");
        if (p.HostConfig?.Init == true) sb.Append(" --init");

        if (!string.IsNullOrEmpty(p.HostConfig?.NetworkMode) &&
            !p.HostConfig.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
            sb.Append(CultureInfo.InvariantCulture, $" --network {p.HostConfig.NetworkMode}");

        if (p.Env != null)
            foreach (var env in p.Env)
                sb.Append(CultureInfo.InvariantCulture, $" -e \"{env}\"");

        sb.Append(CultureInfo.InvariantCulture, $" {p.Image}");
        if (p.Cmd != null)
        {
            foreach (var arg in p.Cmd)
            {
                if (string.IsNullOrEmpty(arg))
                {
                    sb.Append(" \"\"");
                }
                else if (arg.AsSpan().ContainsAny(DockerCommandBuilder.ShellSpecialChars) || arg.Any(char.IsWhiteSpace))
                {
                    var escapedArg = arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
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

    private static (Uri uri, string runtime) ProbeUnixSocket()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var uid = CachedUid.Value;

        var candidates = new (string path, string name)[]
        {
            ("/var/run/docker.sock",                                                   "docker"),
            (Path.Combine(home, ".docker/run/docker.sock"),                            "docker (user)"),
            ($"/run/user/{uid}/podman/podman.sock",                                    "podman"),
            (Path.Combine(home, ".colima/default/docker.sock"),                        "colima"),
            (Path.Combine(home, ".local/share/containers/podman/machine/podman.sock"), "podman (machine)"),
            (Path.Combine(home, ".orbstack/run/docker.sock"),                          "orbstack"),
        };

        foreach (var (path, name) in candidates)
        {
            if (File.Exists(path))
                return (new Uri($"unix://{path}"), name);
        }

        return (new Uri("unix:///var/run/docker.sock"), "docker (default)");
    }

    private static string GetUnixId(string arg, string fallback)
    {
        try
        {
            using var p = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = "id",
                    Arguments = arg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, 
                    UseShellExecute = false
                }
            };
            p.Start();
            
            // CRITICAL: Read non-blocking before Wait to eliminate OS Buffer starvation deadlocks
            var readOutTask = p.StandardOutput.ReadToEndAsync();
            var readErrTask = p.StandardError.ReadToEndAsync();
            
            try 
            {
                if (p.WaitForExit(1000))
                {
                    try { readOutTask.Wait(500); } catch { /* Ignore */ }
                    if (readOutTask.IsCompletedSuccessfully)
                    {
                        var id = readOutTask.Result.Trim();
                        if (!string.IsNullOrEmpty(id) && int.TryParse(id, out _)) return id;
                    }
                }
                else if (!p.HasExited) 
                { 
                    try { p.Kill(); } catch { /* Ignore */ } 
                    ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe for '{arg}' timed out", null);
                }
            }
            catch (AggregateException) { /* Ignored: Handle unobserved reads gracefully */ }
            
            readOutTask.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            readErrTask.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe failed for '{arg}'", ex);
        }
        return fallback;
    }

    public Task<bool> PingAsync(CancellationToken ct = default)
        => _connectionProvider.PingAsync(ct);

    public Task<SystemInfoResponse?> GetSystemInfoAsync(CancellationToken ct = default)
        => _connectionProvider.GetSystemInfoAsync(ct);

    public Task<IList<ContainerListResponse>> ListContainersAsync(CancellationToken ct = default)
        => _containerManager.ListContainersAsync(ct);

    public Task<IList<ImagesListResponse>> ListImagesAsync(CancellationToken ct = default)
        => _imageManager.ListImagesAsync(ct);

    public Task StopContainerAsync(string containerId, CancellationToken ct = default)
        => _containerManager.StopContainerAsync(containerId, ct);

    public Task StartContainerAsync(string containerId, CancellationToken ct = default)
        => _containerManager.StartContainerAsync(containerId, ct);

    public Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
        => _containerManager.RemoveContainerAsync(containerId, ct);

    public Task RemoveImageAsync(string imageId, CancellationToken ct = default)
        => _imageManager.RemoveImageAsync(imageId, ct);

    public Task<(int pulled, int failed)> UpdateAllImagesAsync(Action<string>? progress = null, CancellationToken ct = default)
        => _imageManager.UpdateAllImagesAsync(progress, ct);

    public Task<int> PruneDanglingImagesAsync(CancellationToken ct = default)
        => _imageManager.PruneDanglingImagesAsync(ct);

    public Task<string> GetContainerLogsAsync(string containerId, int tailLines = 50, CancellationToken ct = default)
        => _containerManager.GetContainerLogsAsync(containerId, tailLines, ct);

    public static (int imageCount, long totalSizeBytes, long reclaimableBytes) ComputeDiskUsage(IList<ImagesListResponse> images)
        => DockerImageManager.ComputeDiskUsage(images);

    public Task<(int imageCount, long totalSizeBytes, long reclaimableBytes)> GetDiskUsageSummaryAsync(CancellationToken ct = default)
        => _imageManager.GetDiskUsageSummaryAsync(ct);

    private static void CleanupDanglingContainers(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _cleanupExecuted, 1) != 0) return;
        if (_staticClientForCleanup == null) return;

        var tasks = new List<Task>();
        foreach (var (containerId, shouldAutoRemove) in ActiveContainers)
        {
            if (!shouldAutoRemove) continue;
            tasks.Add(_staticClientForCleanup.Containers.RemoveContainerAsync(
                containerId, new ContainerRemoveParameters { Force = true }));
        }

        if (tasks.Count > 0)
        {
            try
            {
#pragma warning disable VSTHRD002
                Task.WaitAll(tasks.ToArray(), 2000);
#pragma warning restore VSTHRD002
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "Orphan cleanup error", ex);
            }
        }
    }

    internal static void DrainLines(StringBuilder buffer, string text, Func<string, bool>? handler)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        buffer.Append(text);
        int start = 0;
        List<string>? batch = null;

        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '\n')
            {
                int lineEnd = (i > start && buffer[i - 1] == '\r') ? i - 1 : i;
                var completedLine = buffer.ToString(start, lineEnd - start);
                
                if (handler != null)
                {
                    batch ??= [];
                    batch.Add(completedLine);
                }
                start = i + 1;
            }
        }
        
        if (batch != null && batch.Count > 0)
        {
            SafeInvoke(() => 
            {
                foreach (var line in batch) handler!(line);
            });
        }

        if (start > 0)
        {
            if (start < buffer.Length)
            {
                buffer.Remove(0, start); // Highly performant inline GC bounds shift.
            }
            else
            {
                buffer.Clear();
            }
        }

        // Defensive OOM Shield: If a container goes rogue and outputs endless text
        // without newlines, prevent the StringBuilder from crashing the host IDE.
        if (buffer.Length > 8 * 1024 * 1024) // 8 MB limit
        {
            var chunk = buffer.ToString();
            buffer.Clear();
            if (handler != null)
            {
                SafeInvoke(() => handler(chunk));
            }
        }
    }

    private string ResolveImage(string toolName)
    {
        var envImage = Environment.GetEnvironmentVariable("ONEWARE_DOCKER_IMAGE");
        var specificImage = _settingsService.SafeGetSetting($"{ContainerExtensionModule.PerToolImagePrefix}{toolName}", "");
        var configuredImage = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, "");

        var image = envImage ?? "";
        if (string.IsNullOrWhiteSpace(image)) image = specificImage;
        if (string.IsNullOrWhiteSpace(image)) image = configuredImage;
        if (string.IsNullOrWhiteSpace(image) && ContainerExtensionModule.DefaultToolImages.TryGetValue(toolName, out var toolDefault))
            image = toolDefault;
        if (string.IsNullOrWhiteSpace(image)) image = ContainerExtensionModule.FallbackImage;

        return image.Replace("\r", "").Trim();
    }

    private CreateContainerParameters BuildContainerParameters(string image, ToolCommand command)
    {
        return Services.Docker.DockerCommandBuilder.BuildContainerParameters(
            image,
            command,
            _settingsService,
            CachedUid.Value,
            CachedGid.Value,
            (cmd, msg) => SdkLog(cmd, msg));
    }

    private async Task<string?> EnsureImageAsync(string image, ToolCommand command, CancellationToken ct)
    {
        string? imageDigest = null;
        var platform = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
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
            if (!string.IsNullOrWhiteSpace(platform) && !platform.Equals("auto", StringComparison.OrdinalIgnoreCase))
                pullParams.Platform = platform;

            await _client.Images.CreateImageAsync(
                pullParams, null,
                new Progress<JSONMessage>(msg =>
            {
                var progressText = string.IsNullOrWhiteSpace(msg.ProgressMessage)
                    ? msg.Status
                    : $"{msg.Status} {msg.ProgressMessage}";

                if (!string.IsNullOrWhiteSpace(progressText))
                    SdkLog(command, $"[Docker Pull] {progressText}");
            }),
                ct).ConfigureAwait(false);

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
            var shortDigest = imageDigest.Length > 19 ? imageDigest.Substring(7, 12) : imageDigest;
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

        try
        {
            SdkLog(command, $"[Docker SDK] Spawning {executable} in {createParams.Image}...");
            SdkLog(command, $"[Docker SDK] Command: {string.Join(" ", createParams.Cmd ?? [])}", RankInfo);

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
                var charBuf = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        if (result.EOF) break;

                        var charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                        var text = new string(charBuf, 0, charCount);
                        lock (outputBuilder) { outputBuilder.Append(text); }

                        if (result.Target == MultiplexedStream.TargetStream.StandardError)
                            DrainLines(stderrBuf, text, command.ErrorHandler);
                        else
                            DrainLines(stdoutBuf, text, command.OutputHandler);
                    }
                }
                catch (OperationCanceledException) { /* Ignore */ }
                finally
                {
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
                    profile = profile with { OomKilled = true };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (exitCode == 137 && profile != null)
                    profile = profile with { OomKilled = true };
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

    public async Task<(bool success, string output)> ExecuteAsync(ToolCommand command)
    {
        var executable = command.Executable ?? command.ToolName;
        var stopwatch = Stopwatch.StartNew();
        string image = ContainerExtensionModule.FallbackImage;
        string? imageDigest = null;
        string? reconstructedDockerRun = null;
        long exitCode = -1;
        bool wasCancelled = false;
        ResourceProfile? resourceProfile = null;

        var timeoutMinutes = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.TimeoutSetting, 0);
        using var cts = timeoutMinutes > 0
            ? new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes))
            : new CancellationTokenSource();
        var ct = cts.Token;

        _currentLogLevelRank.Value = LogLevelRank(_settingsService.SafeGetSetting<string>(ContainerExtensionModule.LogLevelSetting, "Verbose"));
        _currentShowTimestamps.Value = _settingsService.SafeGetSetting<bool>(ContainerExtensionModule.ShowTimestampsSetting, true);

        SdkLog(command, $"[Docker SDK] ExecuteAsync started for '{executable}'.", RankInfo);

        string? errorMessage = null;

        try
        {
            SdkLog(command, $"[Docker SDK] Step 1: Resolving image for tool '{executable}'...", RankInfo);
            image = ResolveImage(command.ToolName);

            SdkLog(command, $"[Docker SDK] Step 1: Resolved image: {image}", RankInfo);

            SdkLog(command, $"[Docker SDK] Step 2: Building container parameters...", RankInfo);
            var createParams = BuildContainerParameters(image, command);

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
            errorMessage = ex.Message;
            var err = $"[Docker SDK Error] {ex.GetType().Name}: {ex.Message}";
            if (ex.Message.Contains("No such image", StringComparison.OrdinalIgnoreCase))
                err += $"\n  Hint: Run 'docker pull {image}' to cache the image locally.";
            if (ex.Message.Contains("pull access denied", StringComparison.OrdinalIgnoreCase))
                err += $"\n  Hint: The image '{image}' does not exist on Docker Hub or requires authentication.";
            SafeInvoke(() => command.ErrorHandler?.Invoke(err));
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"ExecuteAsync failed for '{executable}'", ex);
            return (false, ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            var retentionStr = _settingsService.SafeGetSetting<string>(ContainerExtensionModule.TelemetryRetentionSetting, "100");
            if (string.Equals(retentionStr, "None", StringComparison.Ordinal))
            {
                ContainerTelemetry.ClearEntries();
            }
            else
            {
                var maxEntries = string.Equals(retentionStr, "Unlimited", StringComparison.Ordinal) ? 0 : int.TryParse(retentionStr, out var n) ? n : 100;
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

            if (stopwatch.Elapsed.TotalSeconds > 30)
            {
                var status = exitCode == 0 ? "succeeded" : (wasCancelled ? "cancelled" : "failed");
                Console.WriteLine(
                    $"[ContainerExtension] {status}: {executable} completed in {stopwatch.Elapsed.TotalSeconds:F1}s (exit {exitCode})");
            }
        }
    }

    public string GetStrategyName() => "Docker Container (DotNet API)";

    public string GetStrategyKey() => ToolKey;

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _staticClientForCleanup, null, _client) == _client)
        {
            AppDomain.CurrentDomain.ProcessExit -= CleanupDanglingContainers;
            if (_cancelKeyPressHandler != null)
            {
                Console.CancelKeyPress -= _cancelKeyPressHandler;
                _cancelKeyPressHandler = null;
            }
            CleanupDanglingContainers(null, EventArgs.Empty);

            Volatile.Write(ref _cleanupExecuted, 0);
        }

        _client.Dispose();
    }
    
    public IAsyncEnumerable<string> StreamContainerLogsAsync(string containerId, CancellationToken ct = default)
        => _containerManager.StreamContainerLogsAsync(containerId, ct);
}