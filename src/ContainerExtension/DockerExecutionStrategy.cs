using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
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

/// <summary>
/// Implements the Hybrid Execution Strategy Pattern using the Docker.DotNet SDK.
/// Routes EDA tool commands (e.g. ghdl, yosys, nextpnr) through ephemeral
/// Docker/Podman containers, allowing FPGA synthesis without native tool installs.
/// </summary>
/// <remarks>
/// Supports cross-platform socket detection, automatic image pull with platform
/// pinning, UID/GID injection on Linux, .env file parsing, stream demultiplexing,
/// orphan container cleanup, and execution telemetry.
/// </remarks>
public sealed class DockerExecutionStrategy : IToolExecutionStrategy, IDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>Strategy key used for settings serialization and ComboBox registration.</summary>
    private const string ToolKey = "DockerExecutionStrategy";

    /// <summary>Fixed container-side mount path for cross-platform compatibility.</summary>
    private const string ContainerWorkDir = "/workspace";

    /// <summary>Compiled regex for sanitizing container name prefixes at runtime.</summary>
    private static readonly System.Text.RegularExpressions.Regex ContainerNameSanitizer = new(
        @"[^a-zA-Z0-9._\-]", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── Cached UID/GID (Linux only, session-stable) ────────────────────────
    // UID and GID don't change during a session — cache to avoid spawning
    // 'id -u' and 'id -g' processes on every container creation.
    private static readonly Lazy<string> CachedUid = new(() => GetUnixId("-u", "1000"));
    private static readonly Lazy<string> CachedGid = new(() => GetUnixId("-g", "1000"));

    // ── Instance Fields ─────────────────────────────────────────────────────

    private readonly ISettingsService _settingsService;
    private readonly DockerClient _client;

    private readonly DockerConnectionProvider _connectionProvider;
    private readonly DockerImageManager _imageManager;
    private readonly DockerContainerManager _containerManager;

    /// <summary>
    /// Exposes the Docker client for module-level operations (e.g. dangling container cleanup).
    /// Internal so only the extension assembly can access it.
    /// </summary>
    internal DockerClient Client => _client;

    /// <summary>
    /// The detected container runtime name (e.g. "docker", "podman", "colima", "orbstack")
    /// resolved during construction. Exposed for health-check logging and dashboard display.
    /// </summary>
    public string DetectedRuntime { get; }

    // ── Log Level Hierarchy ────────────────────────────────────────────────
    //  Off = 0 │ Errors Only = 1 │ Info = 2 │ Verbose = 3
    private const int RankOff = 0, RankErrors = 1, RankInfo = 2, RankVerbose = 3;

    /// <summary>Maps a human-readable log level name to its numeric rank.</summary>
    private static int LogLevelRank(string level) => level switch
    {
        "Verbose" => RankVerbose,
        "Info" => RankInfo,
        "Errors Only" => RankErrors,
        _ => RankOff
    };

    /// <summary>
    /// Emits a diagnostic message at the specified minimum log level.
    /// When ShowTimestamps is enabled, prepends HH:mm:ss.fff to each message.
    /// </summary>
    /// <param name="command">The tool command whose OutputHandler receives the message.</param>
    /// <param name="message">The formatted log message to emit.</param>
    /// <param name="minRank">Minimum log level rank required for the message to be emitted.</param>
    private void SdkLog(ToolCommand command, string message, int minRank = RankVerbose)
    {
        if (_currentLogLevelRank.Value >= minRank)
        {
            var line = _currentShowTimestamps.Value
                ? $"[{DateTime.Now:HH:mm:ss.fff}] {message}"
                : message;
            // Fall back to ErrorHandler when OutputHandler is null.
            // Upstream tools like nextpnr and gmpack only set ErrorHandler.
            (command.OutputHandler ?? command.ErrorHandler)?.Invoke(line);
        }
    }

    /// <summary>Numeric log level rank — set once per ExecuteAsync call. AsyncLocal ensures correct
    /// isolation when multiple ExecuteAsync calls run concurrently on the same instance.</summary>
    private readonly AsyncLocal<int> _currentLogLevelRank = new();

    /// <summary>Timestamp toggle — set at the start of each ExecuteAsync call. AsyncLocal ensures
    /// the value flows correctly through await and Task.Run boundaries.</summary>
    private readonly AsyncLocal<bool> _currentShowTimestamps = new();

    // ── Static Cleanup Infrastructure ───────────────────────────────────────

    /// <summary>
    /// Thread-safe tracker of currently active container IDs.
    /// The value indicates whether the container should be auto-removed on crash cleanup.
    /// Containers with AutoRemove=false are left intact to preserve debugging state.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> ActiveContainers = new();

    /// <summary>Static client reference for the ProcessExit/CancelKeyPress cleanup hook.</summary>
    private static DockerClient? _staticClientForCleanup;

    /// <summary>Ensures the cleanup hook runs exactly once even if both ProcessExit and CancelKeyPress fire.</summary>
    private static int _cleanupExecuted;

    /// <summary>Stored CancelKeyPress handler for proper unsubscription in <see cref="Dispose"/>.</summary>
    private static ConsoleCancelEventHandler? _cancelKeyPressHandler;

    // ═══════════════════════════════════════════════════════════════════════
    //  Constructor
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bootstraps a native connection to the local Docker/Podman daemon.
    /// <para>
    /// Socket resolution order:
    /// <list type="number">
    ///   <item><c>ContainerExtension_DaemonSocket</c> setting (highest priority)</item>
    ///   <item><c>DOCKER_HOST</c> environment variable</item>
    ///   <item>Windows: <c>npipe://./pipe/docker_engine</c></item>
    ///   <item>Unix: probe Docker → Podman → Colima → OrbStack sockets</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="serviceProvider">The OneWare Studio DI service provider.</param>
    public DockerExecutionStrategy(IServiceProvider serviceProvider)
    {
        _settingsService = serviceProvider.Resolve<ISettingsService>();

        var customSocket = SafeGetSetting<string>(ContainerExtensionModule.DaemonSocketSetting, "");
        var envDockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        // Determine the daemon URI in priority order
        var uriText = !string.IsNullOrWhiteSpace(customSocket)
            ? customSocket
            : (!string.IsNullOrWhiteSpace(envDockerHost) ? envDockerHost : null);

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
                // Malformed DOCKER_HOST or custom socket — fall through to platform-specific probing
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
            throw new InvalidOperationException(
                "Could not resolve a Docker daemon URI. Ensure Docker is installed and running, " +
                "or set the DOCKER_HOST environment variable.");
        using var config = new DockerClientConfiguration(uri);
        _client = config.CreateClient();

        _connectionProvider = new DockerConnectionProvider(_client);
        _imageManager = new DockerImageManager(_client, _settingsService);
        _containerManager = new DockerContainerManager(_client);

        // Register the ProcessExit hook exactly once (thread-safe)
        if (Interlocked.CompareExchange(ref _staticClientForCleanup, _client, null) == null)
        {
            AppDomain.CurrentDomain.ProcessExit += CleanupDanglingContainers;
            _cancelKeyPressHandler = (s, e) => CleanupDanglingContainers(s, e);
            Console.CancelKeyPress += _cancelKeyPressHandler;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Settings Helper
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Safely reads a setting value, returning <paramref name="fallback"/> if the key
    /// is unregistered, missing, or throws during resolution.
    /// </summary>
    private T SafeGetSetting<T>(string key, T fallback)
    {
        try
        {
            var value = _settingsService.GetSettingValue<T>(key);
            return value ?? fallback;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"Setting '{key}' read failed", ex);
            return fallback;
        }
    }

    /// <summary>
    /// Returns the configured container runtime CLI path, with proper quoting for
    /// paths containing spaces. Shared by the dashboard's Quick Actions.
    /// </summary>
    public string GetRuntimePath()
    {
        var p = SafeGetSetting<string>(ContainerExtensionModule.DockerRuntimePathSetting, "");
        if (string.IsNullOrWhiteSpace(p)) return "docker";
        return p.Contains(' ') && !p.StartsWith('"') ? $"\"{p}\"" : p;
    }

    /// <summary>
    /// Returns a snapshot of the current active settings for the dashboard display.
    /// Consolidates settings reads into a single call to reduce redundancy.
    /// </summary>
    public Dictionary<string, string> GetActiveSettingsSummary()
    {
        var image = SafeGetSetting<string>(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.FallbackImage);
        var memMb = SafeGetSetting<double>(ContainerExtensionModule.MemoryLimitSetting, 0);
        var cpuCores = SafeGetSetting<double>(ContainerExtensionModule.CpuLimitSetting, 0);
        var timeout = SafeGetSetting<double>(ContainerExtensionModule.TimeoutSetting, 0);
        var network = SafeGetSetting<string>(ContainerExtensionModule.NetworkModeSetting, "bridge");
        var platform = SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
        var autoRemove = SafeGetSetting<bool>(ContainerExtensionModule.AutoRemoveSetting, true);
        var logLevel = SafeGetSetting<string>(ContainerExtensionModule.LogLevelSetting, "Verbose");
        var showTimestamps = SafeGetSetting<bool>(ContainerExtensionModule.ShowTimestampsSetting, true);
        var pullPolicy = SafeGetSetting<string>(ContainerExtensionModule.PullPolicySetting, "if-not-present");
        var extraFlags = SafeGetSetting<string>(ContainerExtensionModule.ExtraFlagsSetting, "");
        var dashRefresh = SafeGetSetting<string>(ContainerExtensionModule.DashboardRefreshSetting, "Manual");
        var runtimePath = SafeGetSetting<string>(ContainerExtensionModule.DockerRuntimePathSetting, "");
        var namePrefix = SafeGetSetting<string>(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");
        var retention = SafeGetSetting<string>(ContainerExtensionModule.TelemetryRetentionSetting, "100");

        return new Dictionary<string, string>
        {
            ["Image"] = image,
            ["Pull Policy"] = pullPolicy,
            ["Platform"] = platform,
            ["Memory"] = memMb > 0 ? $"{memMb:N0} MB" : "No limit",
            ["CPU"] = cpuCores > 0 ? $"{cpuCores:N0} cores" : "No limit",
            ["Timeout"] = timeout > 0 ? $"{timeout:N0} min" : "None",
            ["Network"] = network,
            ["Auto-Remove"] = autoRemove ? "On" : "Off",
            ["Log Level"] = logLevel,
            ["Timestamps"] = showTimestamps ? "On" : "Off",
            ["Name Prefix"] = string.IsNullOrWhiteSpace(namePrefix) ? "(none)" : namePrefix,
            ["Extra Labels"] = string.IsNullOrWhiteSpace(extraFlags) ? "None" : extraFlags,
            ["Dashboard Refresh"] = dashRefresh,
            ["Retention"] = retention,
            ["Runtime Path"] = string.IsNullOrWhiteSpace(runtimePath) ? "docker (PATH)" : runtimePath
        };
    }

    /// <summary>
    /// Returns the currently configured default Docker image, applying the same
    /// fallback chain as <see cref="ResolveImage"/> but without a tool-specific override.
    /// Used by the background pre-pull on startup.
    /// </summary>
    public string GetDefaultImage()
    {
        var image = SafeGetSetting<string>(ContainerExtensionModule.DefaultImageSetting, "");
        return string.IsNullOrWhiteSpace(image) ? ContainerExtensionModule.FallbackImage : image;
    }

    /// <summary>
    /// Ensures a Docker image is cached locally, pulling it if necessary.
    /// Used for background pre-pull on IDE startup. Skips the pull if the image
    /// already exists locally (equivalent to <c>pull-policy: if-not-present</c>).
    /// </summary>
    public async Task PrePullImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await _client.Images.InspectImageAsync(image, ct).ConfigureAwait(false);
            return; // Already cached
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { /* Need to pull */ }

        var platform = SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
        var pullParams = new ImagesCreateParameters { FromImage = image };
        if (!string.IsNullOrWhiteSpace(platform) && !platform.Equals("auto", StringComparison.OrdinalIgnoreCase))
            pullParams.Platform = platform;

        await _client.Images.CreateImageAsync(pullParams, null, new Progress<JSONMessage>(_ => { }), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates an equivalent <c>docker run</c> CLI command for the current settings.
    /// Useful for debugging and reproducing container executions outside the IDE.
    /// </summary>
    public string GenerateDockerRunCommand()
    {
        var image = GetDefaultImage();
        var runtimePath = GetRuntimePath();
        var memMb = SafeGetSetting<double>(ContainerExtensionModule.MemoryLimitSetting, 0);
        var cpuCores = SafeGetSetting<double>(ContainerExtensionModule.CpuLimitSetting, 0);
        var network = SafeGetSetting<string>(ContainerExtensionModule.NetworkModeSetting, "bridge");
        var autoRemove = SafeGetSetting<bool>(ContainerExtensionModule.AutoRemoveSetting, true);
        var platform = SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
        var namePrefix = SafeGetSetting<string>(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");
        var extraFlags = SafeGetSetting<string>(ContainerExtensionModule.ExtraFlagsSetting, "");

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{runtimePath} run");
        if (autoRemove) sb.Append(" --rm");
        if (!string.IsNullOrWhiteSpace(namePrefix))
            sb.Append(CultureInfo.InvariantCulture, $" --name {namePrefix.TrimEnd('-')}-<tool>-<hhmmss>");
        sb.Append(CultureInfo.InvariantCulture, $" -v \"$(pwd)\":{ContainerWorkDir} -w {ContainerWorkDir}");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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

    /// <summary>
    /// Reconstructs the exact <c>docker run</c> CLI command from the container creation parameters
    /// used during an actual execution. Unlike <see cref="GenerateDockerRunCommand"/>, this captures
    /// the precise bind mounts, env vars, resource limits, and command args that Docker received.
    /// </summary>
    /// <param name="p">The container creation parameters from the execution.</param>
    /// <returns>A fully reconstructed docker run CLI string.</returns>
    private string ReconstructDockerRunCommand(CreateContainerParameters p)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{GetRuntimePath()} run");

        if (p.HostConfig?.AutoRemove == true) sb.Append(" --rm");
        if (!string.IsNullOrEmpty(p.Name)) sb.Append(CultureInfo.InvariantCulture, $" --name {p.Name}");
        if (!string.IsNullOrEmpty(p.User)) sb.Append(CultureInfo.InvariantCulture, $" --user {p.User}");

        // Bind mounts
        if (p.HostConfig?.Binds != null)
            foreach (var bind in p.HostConfig.Binds)
                sb.Append(CultureInfo.InvariantCulture, $" -v \"{bind.Replace('\\', '/')}\"");

        // Working directory
        if (!string.IsNullOrEmpty(p.WorkingDir)) sb.Append(CultureInfo.InvariantCulture, $" -w {p.WorkingDir}");

        // Resource limits
        if (p.HostConfig?.Memory > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" --memory {p.HostConfig.Memory / (1024 * 1024)}m");
            if (p.HostConfig.MemorySwap == p.HostConfig.Memory)
                sb.Append(CultureInfo.InvariantCulture, $" --memory-swap {p.HostConfig.MemorySwap / (1024 * 1024)}m");
        }
        if (p.HostConfig?.NanoCPUs > 0) sb.Append(CultureInfo.InvariantCulture, $" --cpus {p.HostConfig.NanoCPUs / 1_000_000_000.0:N1}");
        if (p.HostConfig?.Init == true) sb.Append(" --init");

        // Network
        if (!string.IsNullOrEmpty(p.HostConfig?.NetworkMode) &&
            !p.HostConfig.NetworkMode.Equals("bridge", StringComparison.OrdinalIgnoreCase))
            sb.Append(CultureInfo.InvariantCulture, $" --network {p.HostConfig.NetworkMode}");

        // Environment variables
        if (p.Env != null)
            foreach (var env in p.Env)
                sb.Append(CultureInfo.InvariantCulture, $" -e \"{env}\"");

        // Image + command
        sb.Append(CultureInfo.InvariantCulture, $" {p.Image}");
        if (p.Cmd != null)
        {
            foreach (var arg in p.Cmd)
            {
                if (string.IsNullOrEmpty(arg))
                {
                    sb.Append(" \"\"");
                }
                else if (arg.Any(c => char.IsWhiteSpace(c) || ";&|<>*?[]{}()$\\'\"#~`!".Contains(c)))
                {
                    // Escape internal double quotes and backslashes for bash compatibility
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

    // ═══════════════════════════════════════════════════════════════════════
    //  Socket Probing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Probes the filesystem for known container runtime UNIX sockets in priority order.
    /// Returns the first reachable socket URI and the corresponding runtime name.
    /// </summary>
    private static (Uri uri, string runtime) ProbeUnixSocket()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var uid = CachedUid.Value;

        var candidates = new (string path, string name)[]
        {
            ("/var/run/docker.sock",                                                   "docker"),
            (Path.Combine(home, ".docker/run/docker.sock"),                            "docker (user)"),
            ($"/run/user/{uid}/podman/podman.sock",                                    "podman"),
            (Path.Combine(home, ".colima/default/docker.sock"),                        "colima"),
            (Path.Combine(home, ".local/share/containers/podman/machine/podman.sock"), "podman (machine)"),
            (Path.Combine(home, ".orbstack/run/docker.sock"),                          "orbstack"),
        };

        foreach (var (path, name) in candidates)
        {
            if (File.Exists(path))
                return (new Uri($"unix://{path}"), name);
        }

        // Fallback — will produce a clear error on first use if Docker is not installed
        return (new Uri("unix:///var/run/docker.sock"), "docker (default)");
    }

    /// <summary>Safely executes 'id -u' or 'id -g' to dynamically fetch the true ID.</summary>
    private static string GetUnixId(string arg, string fallback)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo { FileName = "id", Arguments = arg, RedirectStandardOutput = true, UseShellExecute = false });
            if (p != null)
            {
                if (p.WaitForExit(1000))
                {
                    var id = p.StandardOutput.ReadToEnd().Trim();
                    if (!string.IsNullOrEmpty(id) && int.TryParse(id, out _)) return id;
                }
                else
                {
                    try { p.Kill(); } catch { /* Ignore kill errors */ }
                    ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe for '{arg}' timed out after 1000ms", null);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe failed for '{arg}'", ex);
        }
        return fallback;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Health Check
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pings the daemon to verify connectivity. Used by the health check on plugin load.
    /// </summary>
    /// <returns><c>true</c> if the daemon is reachable; <c>false</c> otherwise.</returns>
    public Task<bool> PingAsync(CancellationToken ct = default)
        => _connectionProvider.PingAsync(ct);

    // ═══════════════════════════════════════════════════════════════════════
    //  Dashboard Query Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Retrieves system-level information from the Docker daemon (version, OS, memory, CPUs).
    /// Used by the Docker Desktop dashboard status panel.
    /// </summary>
    public Task<SystemInfoResponse?> GetSystemInfoAsync(CancellationToken ct = default)
        => _connectionProvider.GetSystemInfoAsync(ct);

    /// <summary>
    /// Lists all containers (running and stopped) from the Docker daemon.
    /// Used by the Docker Desktop dashboard containers section.
    /// </summary>
    public Task<IList<ContainerListResponse>> ListContainersAsync(CancellationToken ct = default)
        => _containerManager.ListContainersAsync(ct);

    /// <summary>
    /// Lists all locally cached Docker images.
    /// Used by the Docker Desktop dashboard images section.
    /// </summary>
    public Task<IList<ImagesListResponse>> ListImagesAsync(CancellationToken ct = default)
        => _imageManager.ListImagesAsync(ct);

    /// <summary>
    /// Stops a specific container by ID. Used by the dashboard's stop button.
    /// </summary>
    public Task StopContainerAsync(string containerId, CancellationToken ct = default)
        => _containerManager.StopContainerAsync(containerId, ct);

    /// <summary>
    /// Starts a specific stopped container by ID. Used by the dashboard's start button.
    /// </summary>
    public Task StartContainerAsync(string containerId, CancellationToken ct = default)
        => _containerManager.StartContainerAsync(containerId, ct);

    /// <summary>
    /// Removes a specific container by ID. Used by the dashboard's remove button.
    /// </summary>
    public Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
        => _containerManager.RemoveContainerAsync(containerId, ct);

    /// <summary>
    /// Removes a specific image by ID. Used by the dashboard's remove button.
    /// </summary>
    public Task RemoveImageAsync(string imageId, CancellationToken ct = default)
        => _imageManager.RemoveImageAsync(imageId, ct);

    /// <summary>
    /// Re-pulls all tagged (non-dangling) local images via the Docker SDK.
    /// Cross-platform replacement for the bash-specific grep/xargs pipeline.
    /// Returns a summary of how many images were updated.
    /// </summary>
    public Task<(int pulled, int failed)> UpdateAllImagesAsync(Action<string>? progress = null, CancellationToken ct = default)
        => _imageManager.UpdateAllImagesAsync(progress, ct);

    /// <summary>
    /// Prunes dangling (untagged) images from the local Docker daemon.
    /// Crucial for preventing disk space leaks after an image is updated or re-pulled.
    /// </summary>
    public Task<int> PruneDanglingImagesAsync(CancellationToken ct = default)
        => _imageManager.PruneDanglingImagesAsync(ct);

    /// <summary>
    /// Retrieves the last <paramref name="tailLines"/> lines of a container's logs.
    /// Used by the dashboard's log viewer button.
    /// </summary>
    public Task<string> GetContainerLogsAsync(string containerId, int tailLines = 50, CancellationToken ct = default)
        => _containerManager.GetContainerLogsAsync(containerId, tailLines, ct);

    /// <summary>
    /// Computes disk usage summary from an already-fetched image list.
    /// Avoids a duplicate ListImagesAsync call when the dashboard already has the data.
    /// </summary>
    public static (int imageCount, long totalSizeBytes, long reclaimableBytes) ComputeDiskUsage(IList<ImagesListResponse> images)
        => DockerImageManager.ComputeDiskUsage(images);

    /// <summary>
    /// Returns a summary of Docker disk usage computed from the local image inventory.
    /// Prefer <see cref="ComputeDiskUsage"/> when images are already available.
    /// </summary>
    public Task<(int imageCount, long totalSizeBytes, long reclaimableBytes)> GetDiskUsageSummaryAsync(CancellationToken ct = default)
        => _imageManager.GetDiskUsageSummaryAsync(ct);

    // ═══════════════════════════════════════════════════════════════════════
    //  Orphan Container Cleanup
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Forcefully removes any active containers tracked in <see cref="ActiveContainers"/>
    /// when the host process exits unexpectedly (crash, Ctrl-C, IDE shutdown).
    /// </summary>
    private static void CleanupDanglingContainers(object? sender, EventArgs e)
    {
        // Interlocked guard: Ctrl+C fires both CancelKeyPress AND ProcessExit.
        // Without this, RemoveContainerAsync would be called twice concurrently
        // for the same containers, causing Docker 404/409 errors.
        if (Interlocked.Exchange(ref _cleanupExecuted, 1) != 0) return;
        if (_staticClientForCleanup == null) return;

        // Fire all remove tasks concurrently — ProcessExit has a strict ~2-3s OS deadline.
        // Sequential waits risk the OS killing the process before later containers are reached.
        var tasks = new List<Task>();
        foreach (var (containerId, shouldAutoRemove) in ActiveContainers)
        {
            // Respect the user's AutoRemove setting — containers with AutoRemove=false
            // are deliberately kept alive for post-mortem debugging
            if (!shouldAutoRemove) continue;
            tasks.Add(_staticClientForCleanup.Containers.RemoveContainerAsync(
                containerId, new ContainerRemoveParameters { Force = true }));
        }

        if (tasks.Count > 0)
        {
            try
            {
                // Safe: ProcessExit handler, no SynchronizationContext to deadlock against.
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

    // ═══════════════════════════════════════════════════════════════════════
    //  Stream Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drains a Docker multiplexed stream line-by-line, dispatching each complete line
    /// to the provided handler. Maintains an incomplete-line carry-over buffer between
    /// calls for correct splitting across TCP read boundaries.
    /// <para>
    /// <b>Optimization:</b> Scans the <see cref="StringBuilder"/> via indexer access instead
    /// of calling <c>ToString()</c> on the full buffer, avoiding one large string allocation
    /// per chunk (~8 KB each during active output streaming).
    /// </para>
    /// </summary>
    internal static void DrainLines(StringBuilder buffer, string text, Func<string, bool>? handler)
    {
        buffer.Append(text);
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '\n')
            {
                int lineEnd = (i > start && buffer[i - 1] == '\r') ? i - 1 : i;
                handler?.Invoke(buffer.ToString(start, lineEnd - start));
                start = i + 1;
            }
        }
        // Retain any incomplete trailing line for the next call
        if (start > 0)
        {
            var remainder = buffer.ToString(start, buffer.Length - start);
            buffer.Clear();
            if (remainder.Length > 0) buffer.Append(remainder);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  .env File Parsing
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a <c>.env</c> file from the working directory and returns a list of
    /// <c>KEY=VALUE</c> strings for Docker container environment injection.
    /// Handles both <c>KEY=value</c> and <c>KEY="quoted value"</c> formats.
    /// </summary>
    internal static List<string>? ParseEnvFile(string workingDir)
    {
        var envPath = Path.Combine(workingDir, ".env");
        if (!File.Exists(envPath)) return null;

        var envVars = new List<string>();
        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#') continue;

            // Strip 'export ' prefix (common in Docker Compose and shell .env files)
            if (trimmed.StartsWith("export ".AsSpan(), StringComparison.Ordinal))
                trimmed = trimmed[7..].TrimStart();

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = trimmed[..eqIdx];
            var valueSpan = trimmed[(eqIdx + 1)..];

            // Strip inline comments: KEY=value # comment (space-prefixed # only, matching Docker's behavior)
            if (valueSpan.Length == 0 || (valueSpan[0] != '"' && valueSpan[0] != '\''))
            {
                var commentIdx = valueSpan.IndexOf(" #".AsSpan(), StringComparison.Ordinal);
                if (commentIdx >= 0)
                    valueSpan = valueSpan[..commentIdx];
            }
            valueSpan = valueSpan.Trim();

            // Strip surrounding quotes: KEY="value" or KEY='value'
            if (valueSpan.Length >= 2 &&
                ((valueSpan[0] == '"' && valueSpan[^1] == '"') ||
                 (valueSpan[0] == '\'' && valueSpan[^1] == '\'')))
            {
                valueSpan = valueSpan[1..^1];
            }

            envVars.Add($"{key.ToString()}={valueSpan.ToString()}");
        }
        return envVars.Count > 0 ? envVars : null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Image Resolution
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the Docker image to use via a 5-level fallback hierarchy:
    /// <list type="number">
    ///   <item><c>ONEWARE_DOCKER_IMAGE</c> env var (CI/CD override)</item>
    ///   <item>Per-tool setting <c>ContainerImage_{toolName}</c></item>
    ///   <item>Global default from plugin settings</item>
    ///   <item><see cref="ContainerExtensionModule.DefaultToolImages"/> dictionary (built-in per-tool defaults)</item>
    ///   <item>Hardcoded fallback: <c>hdlc/ghdl:yosys</c></item>
    /// </list>
    /// </summary>
    /// <param name="toolName">The tool name for per-tool image resolution.</param>
    /// <returns>A sanitized Docker image reference (no \r, trimmed).</returns>
    private string ResolveImage(string toolName)
    {
        var envImage = Environment.GetEnvironmentVariable("ONEWARE_DOCKER_IMAGE");
        var specificImage = SafeGetSetting<string>($"{ContainerExtensionModule.PerToolImagePrefix}{toolName}", "");
        var configuredImage = SafeGetSetting<string>(ContainerExtensionModule.DefaultImageSetting, "");

        var image = envImage ?? "";
        if (string.IsNullOrWhiteSpace(image)) image = specificImage;
        if (string.IsNullOrWhiteSpace(image)) image = configuredImage;
        if (string.IsNullOrWhiteSpace(image) &&
            ContainerExtensionModule.DefaultToolImages.TryGetValue(toolName, out var toolDefault))
            image = toolDefault;
        if (string.IsNullOrWhiteSpace(image)) image = ContainerExtensionModule.FallbackImage;

        // Strip stray \r from textbox/env values — would cause Docker SDK failures
        return image.Replace("\r", "").Trim();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Container Configuration
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly char[] ArgSplitChars = new[] { '=', ' ' };

    /// <summary>
    /// Builds the <see cref="CreateContainerParameters"/> with bind mount, UID mapping,
    /// .env injection, and optional resource limits.
    /// </summary>
    /// <param name="image">The resolved Docker image reference.</param>
    /// <param name="command">The IDE tool command payload.</param>
    /// <returns>Fully configured container creation parameters.</returns>
    private CreateContainerParameters BuildContainerParameters(string image, ToolCommand command)
    {
        var executable = (command.Executable ?? command.ToolName).Replace("\r", "").Replace('\\', '/');
        var workingDirFull = Path.GetFullPath(command.WorkingDirectory);
        var rawPrefix = SafeGetSetting<string>(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");

        // ── Pre-emptively Create Output Directories ─────────────────────────────
        // Docker does not auto-create missing subdirectories inside volume mounts.
        // If an EDA tool (like Yosys) is instructed to write to 'build/output.v',
        // it will crash if 'build/' does not exist. We scan the arguments to gracefully
        // guarantee the folder structure exists on the host before launching the container.

        // Compute strict bounds suffix to prevent prefix bleed (e.g. matching /project2 against /project)
        var workingDirBound = workingDirFull;
        if (!workingDirBound.EndsWith(Path.DirectorySeparatorChar) && !workingDirBound.EndsWith(Path.AltDirectorySeparatorChar))
            workingDirBound += Path.DirectorySeparatorChar;

        if (command.Arguments != null)
        {
            foreach (var arg in command.Arguments)
            {
                try
                {
                    var parts = arg.Split(ArgSplitChars, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    var potentialPath = parts[parts.Length - 1].Trim('"', '\'', '\r', '\n', ' ');

                    // Fast reject: must contain a path struct separator
                    if (potentialPath.Contains('/') || potentialPath.Contains('\\'))
                    {
                        var dir = (potentialPath.EndsWith('/') || potentialPath.EndsWith('\\'))
                            ? potentialPath
                            : Path.GetDirectoryName(potentialPath);

                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            var absoluteDir = Path.GetFullPath(Path.Combine(workingDirFull, dir));

                            // Bounds string normalization for check
                            var absBound = absoluteDir;
                            if (!absBound.EndsWith(Path.DirectorySeparatorChar) && !absBound.EndsWith(Path.AltDirectorySeparatorChar))
                                absBound += Path.DirectorySeparatorChar;

                            // Security check: rigorously verify that the determined path physically lives within the workspace
                            if (absBound.StartsWith(workingDirBound, StringComparison.OrdinalIgnoreCase) &&
                                !Directory.Exists(absoluteDir))
                            {
                                Directory.CreateDirectory(absoluteDir);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ContainerTelemetry.TrackError("DockerExecutionStrategy", "Path validation failed", ex);
                }
            }
        }
        // Assemble the full command line
        var fullCmd = new List<string> { executable };
        if (command.Arguments != null)
        {
            foreach (var arg in command.Arguments)
            {
                // Docker SDK Cmd uses exec form (direct execve) — each list element
                // is already a distinct argv entry. Do NOT add shell-style quotes
                // around arguments with spaces; there is no shell to strip them,
                // so they would become literal quote characters in the argument.
                var processedArg = arg.Replace("\r", "").Replace('\\', '/');
                fullCmd.Add(processedArg);
            }
        }

        var autoRemove = SafeGetSetting<bool>(ContainerExtensionModule.AutoRemoveSetting, true);

        // Generate a named container for identification.
        // Sanitize the prefix at runtime (strips invalid chars even if the validator is bypassed).
        // Docker naming rules: [a-zA-Z0-9][a-zA-Z0-9_.-]
        string? containerName = null;
        if (!string.IsNullOrWhiteSpace(rawPrefix))
        {
            var sanitized = ContainerNameSanitizer.Replace(rawPrefix, "");
            if (sanitized.Length > 0)
            {
                var safeToolName = ContainerNameSanitizer.Replace(command.ToolName ?? "tool", "");
                containerName = $"{sanitized.TrimEnd('-')}-{safeToolName}-{DateTime.Now:HHmmssfff}-{Guid.NewGuid().ToString("N")[..4]}";
            }
        }

        var createParams = new CreateContainerParameters
        {
            Image = image,
            Name = containerName,
            Cmd = fullCmd,
            WorkingDir = ContainerWorkDir,
            HostConfig = new HostConfig
            {
                Binds = new List<string> { $"{workingDirFull}:{ContainerWorkDir}" },
                AutoRemove = autoRemove,
                NetworkMode = SafeGetSetting<string>(ContainerExtensionModule.NetworkModeSetting, "bridge"),
                // Inject tini as PID 1 to reap zombie subprocesses spawned by EDA tools
                // (make, yosys, nextpnr) and forward SIGTERM/SIGINT to the actual workload.
                Init = true
            }
        };

        // Inject host UID/GID on Linux to prevent root-owned output files
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            createParams.User = $"{CachedUid.Value}:{CachedGid.Value}";
        }

        // Parse .env file from working directory
        var envVars = ParseEnvFile(workingDirFull);
        if (envVars != null)
        {
            createParams.Env = envVars;
            SdkLog(command, $"[Docker SDK] Injecting {envVars.Count} environment variable(s) from .env file.");
        }

        // Apply optional resource limits (0 = no limit, from SliderSetting)
        // Clamp to host capacity to prevent Docker ArgumentException
        var memMb = SafeGetSetting<double>(ContainerExtensionModule.MemoryLimitSetting, 0);
        var hostMemMb = ContainerExtensionModule.GetHostMemoryMB();
        if (memMb > 0)
        {
            memMb = Math.Min(memMb, hostMemMb);
            var memBytes = (long)(memMb * 1024 * 1024);
            createParams.HostConfig.Memory = memBytes;
            // Set MemorySwap equal to Memory to disable swap entirely.
            // Without this, Docker doubles the effective limit via disk-backed swap,
            // causing thrashing for I/O-heavy EDA synthesis workloads.
            createParams.HostConfig.MemorySwap = memBytes;
        }

        var cpuCores = SafeGetSetting<double>(ContainerExtensionModule.CpuLimitSetting, 0);
        var hostCores = (double)Environment.ProcessorCount;
        if (cpuCores > 0)
        {
            cpuCores = Math.Min(cpuCores, hostCores);
            createParams.HostConfig.NanoCPUs = (long)(cpuCores * 1_000_000_000);
        }

        // Apply Extra Container Labels as container labels (key=value pairs)
        var extraFlags = SafeGetSetting<string>(ContainerExtensionModule.ExtraFlagsSetting, "");
        if (!string.IsNullOrWhiteSpace(extraFlags))
        {
            createParams.Labels ??= new Dictionary<string, string>();
            foreach (var flag in extraFlags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eqIdx = flag.IndexOf('=');
                if (eqIdx > 0)
                    createParams.Labels[flag.Substring(0, eqIdx)] = flag.Substring(eqIdx + 1);
                else
                    createParams.Labels[flag] = "true";
            }
            SdkLog(command, $"[Docker SDK] Injecting {createParams.Labels.Count} extra label(s) from Extra Container Labels.");
        }

        return createParams;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Image Pull & Digest Pinning
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensures the Docker image is available locally, pulling it if necessary.
    /// Returns the image digest (SHA256) for reproducibility logging, or <c>null</c>
    /// if the digest could not be determined.
    /// </summary>
    /// <param name="image">The Docker image reference to verify/pull.</param>
    /// <param name="command">The tool command (for progress output).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The image digest, or <c>null</c> if unavailable.</returns>
    private async Task<string?> EnsureImageAsync(string image, ToolCommand command, CancellationToken ct)
    {
        string? imageDigest = null;
        var platform = SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
        var pullPolicy = SafeGetSetting<string>(ContainerExtensionModule.PullPolicySetting, "if-not-present");

        // Check if image exists locally
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

        // Apply pull policy
        bool shouldPull = pullPolicy switch
        {
            "always" => true,
            "never" => false,
            _ /* if-not-present */ => !imageExistsLocally
        };

        if (!imageExistsLocally && pullPolicy == "never")
        {
            throw new InvalidOperationException($"Image '{image}' not found locally and pull policy is 'never'.");
        }

        if (shouldPull)
        {
            SdkLog(command, pullPolicy == "always" && imageExistsLocally
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

        // Log the resolved digest for thesis reproducibility
        if (imageDigest != null)
        {
            var shortDigest = imageDigest.Length > 19 ? imageDigest.Substring(7, 12) : imageDigest;
            SdkLog(command, $"[Docker SDK] Resolved digest: {shortDigest}...");
        }

        return imageDigest;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Resource Profiling (OOM Analyzer)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lightweight value holder for peak resource usage captured during container execution.
    /// Recorded in telemetry for thesis evaluation (memory footprint analysis, OOM detection).
    /// </summary>
    /// <param name="PeakMemoryBytes">Highest RSS memory sample in bytes.</param>
    /// <param name="MaxCpuPercent">Highest CPU utilization sample as a percentage (0-N*100 for N cores).</param>
    /// <param name="SampleCount">Number of stats samples collected (≈1/sec from Docker).</param>
    /// <param name="OomKilled">True if the container was killed by the kernel OOM killer.</param>
    internal record ResourceProfile(long PeakMemoryBytes, double MaxCpuPercent, int SampleCount, bool OomKilled);

    /// <summary>
    /// Streams live resource stats from the Docker daemon via <c>GetContainerStatsAsync</c>
    /// and tracks peak memory and max CPU% across all samples.
    /// <para>
    /// Runs as a parallel <see cref="Task"/> alongside the output demuxer in
    /// <see cref="RunContainerAsync"/>. The Docker stats API emits ~1 JSON frame per second.
    /// </para>
    /// </summary>
    /// <param name="containerId">The running container's ID.</param>
    /// <param name="command">Tool command for optional live logging.</param>
    /// <param name="ct">Cancellation token (fires when container exits or timeout).</param>
    /// <returns>A <see cref="ResourceProfile"/> with peak metrics, or null if stats were unavailable.</returns>
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
            // Use IProgress<ContainerStatsResponse> for strongly-typed stats (recommended API).
            // The callback fires ~1/s with a new stats frame while stream=true.
            // Interlocked/Volatile used for formal correctness: Progress<T> posts
            // callbacks via SynchronizationContext, which in non-UI contexts routes
            // to the ThreadPool. While Docker stats arrive sequentially, using
            // atomic operations makes the code safe regardless of scheduling.
            var progress = new Progress<ContainerStatsResponse>(stats =>
            {
                // -- Memory: track peak RSS --------------------------
                if (stats.MemoryStats?.Usage > 0)
                {
                    var currentMem = (long)stats.MemoryStats.Usage;
                    // Interlocked CAS loop for lock-free peak tracking
                    long current;
                    do { current = Interlocked.Read(ref peakMemory); }
                    while (currentMem > current &&
                           Interlocked.CompareExchange(ref peakMemory, currentMem, current) != current);
                }

                // -- CPU: calculate delta-based utilization % --------
                if (stats.CPUStats?.CPUUsage?.TotalUsage > 0 &&
                    stats.CPUStats?.SystemUsage > 0)
                {
                    var cpuTotal = (long)stats.CPUStats.CPUUsage.TotalUsage;
                    var systemTotal = (long)stats.CPUStats.SystemUsage;
                    var onlineCpus = (int)(stats.CPUStats.OnlineCPUs > 0
                        ? stats.CPUStats.OnlineCPUs
                        : 1);

                    // Skip first sample (no previous delta to compare)
                    if (prevCpuTotal > 0 && prevSystemTotal > 0)
                    {
                        var cpuDelta = (double)(cpuTotal - prevCpuTotal);
                        var systemDelta = (double)(systemTotal - prevSystemTotal);

                        if (systemDelta > 0 && onlineCpus > 0)
                        {
                            var cpuPercent = (cpuDelta / systemDelta) * onlineCpus * 100.0;
                            Volatile.Write(ref maxCpu, Math.Max(Volatile.Read(ref maxCpu), cpuPercent));
                        }
                    }

                    prevCpuTotal = cpuTotal;
                    prevSystemTotal = systemTotal;
                }

                Interlocked.Increment(ref sampleCount);
            });

            // Blocks until the stream closes (container exits) or cancellation fires.
            await _client.Containers.GetContainerStatsAsync(
                containerId,
                new ContainerStatsParameters { Stream = true },
                progress,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Expected -- container exited */ }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SdkLog(command, $"[Docker SDK] Stats collection ended: {ex.Message}", RankInfo);
        }

        if (Interlocked.CompareExchange(ref sampleCount, 0, 0) == 0)
            return null;

        return new ResourceProfile(Interlocked.Read(ref peakMemory), Math.Round(Volatile.Read(ref maxCpu), 1), sampleCount, OomKilled: false);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Container Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes the container lifecycle: create → attach streams → start → demultiplex
    /// output → collect resource stats → wait for exit.
    /// Returns the exit code, captured output, cancellation flag, and optional resource profile.
    /// </summary>
    /// <param name="createParams">Pre-built container creation parameters.</param>
    /// <param name="command">The IDE tool command payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of (exitCode, capturedOutput, wasCancelled, resourceProfile).</returns>
    private async Task<(long exitCode, string output, bool wasCancelled, ResourceProfile? profile)> RunContainerAsync(
        CreateContainerParameters createParams, ToolCommand command, CancellationToken ct)
    {
        var outputBuilder = new StringBuilder();
        var executable = (command.Executable ?? command.ToolName).Replace("\r", "");
        long exitCode = -1;
        bool wasCancelled = false;

        // ── Step 1: Create Container ────────────────────────────────────
        var container = await _client.Containers.CreateContainerAsync(createParams, ct).ConfigureAwait(false);
        var containerId = container.ID;
        var autoRemove = createParams.HostConfig?.AutoRemove ?? true;
        ActiveContainers.TryAdd(containerId, autoRemove);

        // Register cancellation callback before try so the finally block can dispose it.
        // This prevents a memory leak if an exception occurs during container execution.
        var cancelRegistration = ct.CanBeCanceled
            ? ct.Register(() =>
            {
                wasCancelled = true;
                try
                {
                    // Capture the Task to prevent unobserved exceptions and enable
                    // diagnostic logging when the stop fails (e.g., container already removed).
                    var stopTask = _client.Containers.StopContainerAsync(containerId,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 2 });
#pragma warning disable VSTHRD110 // Faults are observed in the ContinueWith body
                    stopTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            ContainerTelemetry.TrackError("DockerExecutionStrategy",
                                $"Async container stop failed for '{containerId[..12]}'", t.Exception?.InnerException);
                    }, TaskScheduler.Default);
#pragma warning restore VSTHRD110
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ContainerTelemetry.TrackError("DockerExecutionStrategy", $"Cancel-time container stop failed for '{containerId[..12]}'", ex);
                }
            })
            : (CancellationTokenRegistration?)null;

        // Resource profile collected in parallel (may remain null if stats unavailable)
        ResourceProfile? profile = null;

        try
        {
            SdkLog(command, $"[Docker SDK] Spawning {executable} in {createParams.Image}...");
            SdkLog(command, $"[Docker SDK] Command: {string.Join(" ", createParams.Cmd)}", RankInfo);

            // ── Step 2: Attach Streams ──────────────────────────────────
            using var stream = await _client.Containers.AttachContainerAsync(
                containerId, false,
                new ContainerAttachParameters { Stream = true, Stdout = true, Stderr = true }, ct).ConfigureAwait(false);

            // ── Step 3: Start Container ─────────────────────────────────
            var containerStopwatch = Stopwatch.StartNew();
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
            SdkLog(command, $"[Docker SDK] Container {containerId[..12]} started.", RankInfo);

            // ── Step 4: Demultiplex Output ──────────────────────────────
            // Use CancellationToken.None for the Task.Run call itself so the
            // output demuxer body always executes, even if ct is already cancelled.
            // This ensures the flush in the finally block runs unconditionally.
            var readTask = Task.Run(async () =>
            {
                var buffer = new byte[8192];
                var stdoutBuf = new StringBuilder();
                var stderrBuf = new StringBuilder();
                // Stateful decoder: caches trailing incomplete UTF-8 bytes across 8KB chunk
                // boundaries, preventing multi-byte characters from being split into \uFFFD.
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
                catch (OperationCanceledException) { /* Expected on cancellation */ }
                finally
                {
                    // Flush any remaining partial lines — runs even if ct was pre-cancelled
                    if (stdoutBuf.Length > 0) command.OutputHandler?.Invoke(stdoutBuf.ToString());
                    if (stderrBuf.Length > 0) command.ErrorHandler?.Invoke(stderrBuf.ToString());
                }
            }, CancellationToken.None);

            // ── Step 4b: Collect Resource Stats (parallel) ──────────────
            // Fire-and-forget stats collection alongside the output demuxer.
            // Uses a separate CTS so stats stop when the container exits.
            using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var statsTask = CollectResourceStatsAsync(containerId, command, statsCts.Token);

            // ── Step 5: Wait for Exit ───────────────────────────────────
            // Capture log rank before potential thread switch
            var logRank = _currentLogLevelRank.Value;
            try
            {
                var wait = await _client.Containers.WaitContainerAsync(containerId, ct).ConfigureAwait(false);
                exitCode = wait.StatusCode;
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                // Distinguish between timeout-triggered and user-initiated cancellation
                if (logRank >= RankErrors)
                    command.ErrorHandler?.Invoke("[Docker SDK] Container execution was cancelled.");
            }

            // Stop stats collection now that the container has exited
            await statsCts.CancelAsync().ConfigureAwait(false);
            await readTask.ConfigureAwait(false);

            // Collect the resource profile (may be null if stats were unavailable)
            try { profile = await statsTask.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "Resource stats collection failed", ex);
            }

            // Definitive OOM detection: inspect container state instead of relying
            // solely on exit code 137, which can also be caused by manual docker kill,
            // docker stop --time 0, or host-level kill -9 (false positives).
            try
            {
                var inspect = await _client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
                if (inspect.State.OOMKilled && profile != null)
                    profile = profile with { OomKilled = true };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Fallback to exit code heuristic if inspect fails (container already removed, etc.)
                if (exitCode == 137 && profile != null)
                    profile = profile with { OomKilled = true };
            }

            containerStopwatch.Stop();

            // Log summary including peak resource usage
            var peakInfo = profile != null
                ? $", peak RAM: {profile.PeakMemoryBytes / (1024 * 1024)} MB, max CPU: {profile.MaxCpuPercent:F1}%"
                  + (profile.OomKilled ? " ⚠️ OOM KILLED" : "")
                : "";
            SdkLog(command, $"[Docker SDK] Container {containerId[..12]} stopped — exit code {exitCode}, ran {containerStopwatch.Elapsed.TotalSeconds:F2}s{peakInfo}.", RankInfo);
        }
        finally
        {
#pragma warning disable VSTHRD103  // Just unhooks callback, does not block
            cancelRegistration?.Dispose();
#pragma warning restore VSTHRD103
            ActiveContainers.TryRemove(containerId, out _);
        }

        string finalOutput;
        lock (outputBuilder) { finalOutput = outputBuilder.ToString(); }

        return (exitCode, finalOutput, wasCancelled, profile);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Core Execution (Orchestrator)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes an EDA tool command inside an ephemeral Docker container.
    /// <para>
    /// Orchestrates the full container lifecycle by delegating to:
    /// <list type="number">
    ///   <item><see cref="ResolveImage"/> — 4-level fallback image resolution</item>
    ///   <item><see cref="BuildContainerParameters"/> — container config with mounts, UID, env, limits</item>
    ///   <item><see cref="EnsureImageAsync"/> — auto-pull and digest pinning</item>
    ///   <item><see cref="RunContainerAsync"/> — create, attach, start, demux, wait</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="command">The tool command payload from the IDE.</param>
    /// <returns>A tuple of (success, captured_output).</returns>
    public async Task<(bool success, string output)> ExecuteAsync(ToolCommand command)
    {
        // === EARLY DEBUG TRACE — writes before ANY other code ===
        const string debugLogPath = "/Users/mtorun/.oneware/docker_debug.log";
        void DebugTrace(string msg)
        {
            try { File.AppendAllText(debugLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
            catch { /* never fail */ }
        }
        DebugTrace($">>> ExecuteAsync ENTERED. ToolName='{command.ToolName}', Executable='{command.Executable}'");
        DebugTrace($"    WorkingDirectory='{command.WorkingDirectory}'");
        DebugTrace($"    Arguments=[{string.Join(", ", command.Arguments ?? Array.Empty<string>())}]");

        // Note: \r stripping for the container command is done canonically in
        // BuildContainerParameters. This local copy is used only for logging/telemetry.
        var executable = command.Executable ?? command.ToolName;
        var stopwatch = Stopwatch.StartNew();
        string image = ContainerExtensionModule.FallbackImage;
        string? imageDigest = null;
        string? reconstructedDockerRun = null;
        long exitCode = -1;
        bool wasCancelled = false;
        ResourceProfile? resourceProfile = null;

        // Upstream ToolCommand does not yet expose a CancellationToken.
        // We use the Execution Timeout setting to create one if configured,
        // and spawn a background monitor to trigger it if the IDE sets IsCancellationRequested.
        var timeoutMinutes = SafeGetSetting<double>(ContainerExtensionModule.TimeoutSetting, 0);
        using var cts = timeoutMinutes > 0
            ? new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes))
            : new CancellationTokenSource();
        var ct = cts.Token;

        // Note: ToolCommand does not yet expose a CancellationToken or
        // IsCancellationRequested property. Cancellation is handled solely
        // via the execution timeout configured in settings.

        // Log level — cache once per execution to avoid per-line setting reads.
        _currentLogLevelRank.Value = LogLevelRank(SafeGetSetting<string>(ContainerExtensionModule.LogLevelSetting, "Verbose"));
        _currentShowTimestamps.Value = SafeGetSetting<bool>(ContainerExtensionModule.ShowTimestampsSetting, true);

        SdkLog(command, $"[Docker SDK] ExecuteAsync started for '{executable}'.", RankInfo);


        string? errorMessage = null;

        try
        {
            // Step 1: Resolve the container image
            DebugTrace("Step 1: Resolving image...");
            SdkLog(command, $"[Docker SDK] Step 1: Resolving image for tool '{executable}'...", RankInfo);
            image = ResolveImage(command.ToolName);
            DebugTrace($"Step 1: Resolved image: {image}");
            SdkLog(command, $"[Docker SDK] Step 1: Resolved image: {image}", RankInfo);

            // Step 2: Build container configuration
            DebugTrace("Step 2: Building container parameters...");
            SdkLog(command, $"[Docker SDK] Step 2: Building container parameters...", RankInfo);
            var createParams = BuildContainerParameters(image, command);
            DebugTrace($"Step 2: Cmd = [{string.Join(", ", createParams.Cmd ?? new List<string>())}]");
            SdkLog(command, $"[Docker SDK] Step 2: Cmd = [{string.Join(", ", createParams.Cmd ?? new List<string>())}]", RankInfo);
            SdkLog(command, $"[Docker SDK] Step 2: WorkingDir = {createParams.WorkingDir}, Binds = [{string.Join(", ", createParams.HostConfig?.Binds ?? new List<string>())}]", RankInfo);

            // Step 3: Ensure image is available (auto-pull if needed)
            DebugTrace($"Step 3: Ensuring image '{image}' is available...");
            SdkLog(command, $"[Docker SDK] Step 3: Ensuring image '{image}' is available...", RankInfo);
            imageDigest = await EnsureImageAsync(image, command, ct).ConfigureAwait(false);
            DebugTrace($"Step 3: Image ready. Digest = {imageDigest ?? "(none)"}");
            SdkLog(command, $"[Docker SDK] Step 3: Image ready. Digest = {imageDigest ?? "(none)"}", RankInfo);

            // Reconstruct the exact CLI command for telemetry
            reconstructedDockerRun = ReconstructDockerRunCommand(createParams);
            SdkLog(command, $"[Docker SDK] Equivalent CLI: {reconstructedDockerRun}", RankInfo);

            // Step 4: Run the container lifecycle
            SdkLog(command, $"[Docker SDK] Step 4: Creating and starting container...", RankInfo);
            var result = await RunContainerAsync(createParams, command, ct).ConfigureAwait(false);
            exitCode = result.exitCode;
            wasCancelled = result.wasCancelled;
            resourceProfile = result.profile;

            SdkLog(command, $"[Docker SDK] Container finished. Exit code: {exitCode}", RankInfo);
            return (exitCode == 0, result.output);
        }
        catch (OperationCanceledException oce)
        {
            DebugTrace($"CAUGHT OperationCanceledException: {oce.Message}");
            wasCancelled = true;
            errorMessage = timeoutMinutes > 0
                ? $"Execution timed out after {timeoutMinutes:N0} minute(s)."
                : "Operation cancelled.";
            command.ErrorHandler?.Invoke($"[Docker SDK] {errorMessage}");
            return (false, "Cancelled");
        }
        catch (Exception ex)
        {
            DebugTrace($"CAUGHT {ex.GetType().Name}: {ex.Message}");
            DebugTrace($"  Stack: {ex.StackTrace}");
            errorMessage = ex.Message;
            var err = $"[Docker SDK Error] {ex.GetType().Name}: {ex.Message}";
            if (ex.Message.Contains("No such image", StringComparison.OrdinalIgnoreCase))
                err += $"\n  Hint: Run 'docker pull {image}' to cache the image locally.";
            if (ex.Message.Contains("pull access denied", StringComparison.OrdinalIgnoreCase))
                err += $"\n  Hint: The image '{image}' does not exist on Docker Hub or requires authentication.";
            command.ErrorHandler?.Invoke(err);
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"ExecuteAsync failed for '{executable}'", ex);
            return (false, ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            var retentionStr = SafeGetSetting<string>(ContainerExtensionModule.TelemetryRetentionSetting, "100");
            if (retentionStr == "None")
            {
                // Delete telemetry file via ClearEntries() which acquires the Mutex properly
                ContainerTelemetry.ClearEntries();
            }
            else
            {
                var maxEntries = retentionStr == "Unlimited" ? 0 : int.TryParse(retentionStr, out var n) ? n : 100;
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

            // Notify the IDE console for long-running jobs (>30s) so users who switched away are alerted
            if (stopwatch.Elapsed.TotalSeconds > 30)
            {
                var status = exitCode == 0 ? "succeeded" : (wasCancelled ? "cancelled" : "failed");
                Console.WriteLine(
                    $"[ContainerExtension] {status}: {executable} completed in {stopwatch.Elapsed.TotalSeconds:F1}s (exit {exitCode})");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  IToolExecutionStrategy Interface
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public string GetStrategyName() => "Docker Container (DotNet API)";

    /// <inheritdoc />
    public string GetStrategyKey() => ToolKey;

    // ═══════════════════════════════════════════════════════════════════════
    //  IDisposable
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Releases the native Docker client connection.</summary>
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
            // Ensure orphans for this client are cleaned before it's disposed
            CleanupDanglingContainers(null, EventArgs.Empty);

            // To allow future clients to act as cleanup hosts, reset the executed flag
            Volatile.Write(ref _cleanupExecuted, 0);
        }

        _client.Dispose();
    }
}
