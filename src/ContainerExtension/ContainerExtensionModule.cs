using System;
using System.Collections.Frozen;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Templates;
using ContainerExtension.ViewModels;
using ContainerExtension.Views;
using Microsoft.Extensions.DependencyInjection;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using ContainerExtension.Validations;

namespace ContainerExtension;

/// <summary>
/// The main entry point for the OneWare Container Extension.
/// <para>
/// Responsibilities:
/// <list type="bullet">
///   <item>Register Docker/Podman settings under "Binary Management → Container Engine"</item>
///   <item>Inject the <see cref="DockerExecutionStrategy"/> into all IDE-registered tools</item>
///   <item>Append the strategy option to each tool's execution strategy ComboBox</item>
///   <item>Register the Docker toolbar button for quick-access diagnostics</item>
///   <item>Register the dockable <see cref="DockerDiagnosticsViewModel"/> DataTemplate for the dock system</item>
///   <item>Fire-and-forget health check to verify daemon connectivity on load</item>
/// </list>
/// </para>
/// </summary>
public class ContainerExtensionModule : OneWareModuleBase
{
    // To prevent multiple Dashboard buttons in the status bar across workspace reloads
    private static bool _isUiRegistered = false;
    // ── Setting Key Constants ────────────────────────────────────────────────
    // Centralized here to avoid magic strings across the codebase.
    // Referenced by DockerExecutionStrategy, DockerDiagnosticsView, and unit tests.

    /// <summary>Settings key for the container runtime CLI path (e.g., /usr/bin/docker).</summary>
    public const string DockerRuntimePathSetting = "ContainerExtension_DockerRuntimePath";

    /// <summary>Settings key for the default Docker image (e.g., hdlc/ghdl:yosys).</summary>
    public const string DefaultImageSetting = "ContainerExtension_DefaultImage";

    /// <summary>Settings key for the container memory limit in MB.</summary>
    public const string MemoryLimitSetting = "ContainerExtension_MemoryLimit";

    /// <summary>Settings key for the CPU cores limit (e.g., 2 or 4).</summary>
    public const string CpuLimitSetting = "ContainerExtension_CpuLimit";

    /// <summary>Settings key for the auto-remove flag (removes container after execution).</summary>
    public const string AutoRemoveSetting = "ContainerExtension_AutoRemove";

    /// <summary>Settings key for custom daemon socket override (e.g., unix:// or tcp://).</summary>
    public const string DaemonSocketSetting = "ContainerExtension_DaemonSocket";

    /// <summary>Settings key for platform pinning (e.g., linux/amd64 on Apple Silicon).</summary>
    public const string PlatformSetting = "ContainerExtension_Platform";

    /// <summary>Settings key for execution timeout in minutes (0 = no timeout).</summary>
    public const string TimeoutSetting = "ContainerExtension_Timeout";

    /// <summary>Settings key for Docker network mode (bridge, host, none).</summary>
    public const string NetworkModeSetting = "ContainerExtension_NetworkMode";

    /// <summary>Settings key for image pull policy (always, if-not-present, never).</summary>
    public const string PullPolicySetting = "ContainerExtension_PullPolicy";

    /// <summary>Settings key for extra container labels (free-form key=value pairs).</summary>
    public const string ExtraFlagsSetting = "ContainerExtension_ExtraFlags";

    /// <summary>Settings key for dashboard auto-refresh interval.</summary>
    public const string DashboardRefreshSetting = "ContainerExtension_DashboardRefresh";

    /// <summary>Settings key for container name prefix (e.g., containerextension-).</summary>
    public const string ContainerNamePrefixSetting = "ContainerExtension_ContainerNamePrefix";

    /// <summary>Settings key for log level (Off, Errors Only, Info, Verbose).</summary>
    public const string LogLevelSetting = "ContainerExtension_LogLevel";

    /// <summary>Settings key for showing timestamps on Docker SDK log messages.</summary>
    public const string ShowTimestampsSetting = "ContainerExtension_ShowTimestamps";

    /// <summary>Settings key for telemetry retention limit.</summary>
    public const string TelemetryRetentionSetting = "ContainerExtension_TelemetryRetention";

    /// <summary>Settings key prefix for per-tool image overrides (e.g., ContainerImage_yosys).</summary>
    public const string PerToolImagePrefix = "ContainerImage_";

    /// <summary>Hardcoded fallback image when no setting or env var is configured.</summary>
    public const string FallbackImage = "hdlc/ghdl:yosys";

    /// <summary>
    /// Maps known tool keys to their optimal per-tool Docker images from the
    /// <see href="https://hdl.github.io/containers/">hdl/containers</see> ecosystem.
    /// Tools not listed here fall back to <see cref="FallbackImage"/>.
    /// </summary>
    public static readonly FrozenDictionary<string, string> DefaultToolImages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Simulation — VHDL
            ["ghdl"] = "hdlc/ghdl:yosys",
            ["nvc"] = "hdlc/nvc",

            // Simulation — Verilog
            ["iverilog"] = "hdlc/iverilog",
            ["verilator"] = "hdlc/verilator",

            // Synthesis
            ["yosys"] = "hdlc/ghdl:yosys",
            ["apicula"] = "hdlc/apicula",

            // Place & Route — tools with dedicated hdlc images
            ["nextpnr-ecp5"] = "hdlc/impl/prjtrellis",
            ["nextpnr-generic"] = "hdlc/impl/generic",
            ["nextpnr-ice40"] = "hdlc/impl/icestorm",
            ["nextpnr-nexus"] = "hdlc/impl/prjoxide",

            // Place & Route — no standalone hdlc image; use the full impl stack
            ["nextpnr-himbaechel"] = "hdlc/impl",
            ["nextpnr-machxo2"] = "hdlc/impl",

            // Packing / Programming
            ["openFPGALoader"] = "hdlc/prog",
            ["iceprog"] = "hdlc/impl/icestorm",
            ["icepack"] = "hdlc/impl/icestorm",
            ["gowin_pack"] = "hdlc/impl",
            ["gmpack"] = "hdlc/impl",
            ["gmupack"] = "hdlc/impl",

            // Visualisation
            ["gtkwave"] = "hdlc/gtkwave",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // ── Shared UI Constants ─────────────────────────────────────────────────
    // Centralized to avoid duplication across DockerButtonView, DockerDiagnosticsViewModel, and DockerDiagnosticsView.

    /// <summary>SVG path data for the Docker whale icon used in the toolbar button and dock tab.</summary>
    public const string WhaleIconPath = "M13.983 11.078h2.119a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.119a.185.185 0 00-.185.185v1.888c0 .102.083.185.185.185m-2.954-5.43h2.118a.186.186 0 00.186-.186V3.574a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m0 2.716h2.118a.187.187 0 00.186-.186V6.29a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.887c0 .102.082.185.185.185m-2.93 0h2.12a.186.186 0 00.184-.186V6.29a.185.185 0 00-.185-.185H8.1a.185.185 0 00-.185.185v1.887c0 .102.083.185.185.185m-2.964 0h2.119a.186.186 0 00.185-.186V6.29a.185.185 0 00-.185-.185H5.136a.186.186 0 00-.186.185v1.887c0 .102.084.185.186.185m5.893 2.715h2.118a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m-2.93 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.083.185.185.185m-2.964 0h2.119a.185.185 0 00.185-.185V9.006a.185.185 0 00-.184-.186h-2.12a.186.186 0 00-.186.185v1.888c0 .102.084.185.186.185m-2.92 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.082.185.185.185M23.763 9.89c-.065-.051-.672-.51-1.954-.51-.338.001-.676.03-1.01.087-.248-1.7-1.653-2.534-1.716-2.566l-.344-.199-.198.337c-.135.227-.235.467-.294.717-.221-.061-.453-.092-.686-.092h-13.8v2.32c-.006 1.764.12 3.524.375 5.27.7 4.793 4.295 7.64 9.079 7.64 5.378 0 8.017-2.732 8.783-4.529.742.062 1.488.083 2.228.064l.278-.01.096-.282c.164-.492.316-1.127.359-1.9H24l-.185-.815c-.217-.96-.45-1.916-.85-2.827l-.058-.124";

    /// <summary>Docker brand color hex value (#2496ED) used for accent theming.</summary>
    public const string DockerBlueHex = "#2496ED";

    /// <summary>Title string for the Container Dashboard panel and window.</summary>
    public const string DashboardTitle = "Container Dashboard";

    /// <summary>
    /// Returns the process-visible host memory in MB.
    /// On desktop systems this equals physical RAM; respects cgroup limits if the IDE itself runs in a container.
    /// Shared by the settings registration slider and the runtime resource clamp in <see cref="DockerExecutionStrategy"/>.
    /// </summary>
    internal static double GetHostMemoryMB() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);

    // ═══════════════════════════════════════════════════════════════════════
    //  Module Lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called during plugin discovery. Registers the execution strategy and
    /// dashboard viewmodel as singletons so the Dock Layout framework can resolve them.
    /// </summary>
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<DockerExecutionStrategy>();
        services.AddSingleton<DockerDiagnosticsViewModel>();
    }

    /// <summary>
    /// Executes when the module is loaded by the IDE.
    /// Registers all settings, injects the Docker strategy, and starts the health check.
    /// </summary>
    /// <param name="serviceProvider">The root DI container from OneWare Studio.</param>
    public override void Initialize(IServiceProvider serviceProvider)
    {
        var settingsService = serviceProvider.Resolve<ISettingsService>();

        // ── Register Settings ───────────────────────────────────────────
        settingsService.RegisterSettingSubCategory("Binary Management", "Container Engine");

        // ── 1. Default Image (most commonly changed, validated) ──────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            DefaultImageSetting,
            new TextBoxSetting("Default Toolchain Image", FallbackImage,
                "The default container image to pull and use for all tools (e.g., hdlc/ghdl:yosys).")
            {
                HoverDescription = "Level 3 in the image resolution hierarchy. Override per-tool via Binary Management → Execution Strategy, or globally with ONEWARE_DOCKER_IMAGE env var.",
                Validator = new DockerImageFormatValidation()
            }
        );

        // ── 2. Pull Policy (image-related) ──────────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            PullPolicySetting,
            new ComboBoxSetting("Image Pull Policy", "if-not-present",
                ["always", "if-not-present", "never"])
            { HoverDescription = "Controls when Docker images are pulled. 'always' = pull on every execution (CI/CD), 'if-not-present' = only pull when image is missing (default), 'never' = fail if image is not cached locally." }
        );

        // ── 3. Image Platform (ComboBox with presets) ───────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            PlatformSetting,
            new ComboBoxSetting("Image Platform", "auto",
                ["auto", "linux/amd64", "linux/arm64", "linux/arm/v7"])
            { HoverDescription = "Force a specific platform for multi-arch image pulls. Useful on Apple Silicon when native ARM images are unavailable. 'auto' uses Docker's default platform detection." }
        );

        // ── 4. Memory Limit (Slider, auto-detected host max) ─────────────
        var totalRamMb = GetHostMemoryMB();
        var memWarnThreshold = totalRamMb * 0.75;
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            MemoryLimitSetting,
            new SliderSetting($"Memory Limit (0 = unlimited) — {totalRamMb:N0} MB available", 0, 0, totalRamMb, 256)
            {
                HoverDescription = $"Maximum memory the container can use in MB (host has {totalRamMb:N0} MB). Translates to Docker's --memory flag. Set to 0 (leftmost) for no limit — Docker will allow the container to use all available host memory.",
                Validator = new ResourceThresholdValidation(memWarnThreshold, totalRamMb, "memory")
            }
        );

        // ── 5. CPU Cores Limit (Slider, auto-detected host max) ─────────
        var totalCores = (double)Environment.ProcessorCount;
        var cpuWarnThreshold = totalCores * 0.75;
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            CpuLimitSetting,
            new SliderSetting($"CPU Cores Limit (0 = unlimited) — {totalCores:N0} cores available", 0, 0, totalCores, 1)
            {
                HoverDescription = $"Maximum CPU cores the container can use (host has {totalCores:N0} logical cores). Translates to Docker's --cpus flag. Set to 0 (leftmost) for no limit — Docker will allow the container to use all available host cores.",
                Validator = new ResourceThresholdValidation(cpuWarnThreshold, totalCores, "CPU")
            }
        );

        // ── 6. Execution Timeout (safety net) ───────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            TimeoutSetting,
            new SliderSetting("Execution Timeout (0 = no timeout)", 0, 0, 480, 5)
            { HoverDescription = "Maximum time in minutes a container can run before it is forcefully killed. Protects against hanging synthesis/routing jobs. Set to 0 (leftmost) for no timeout. Max: 480 min (8 hours)." }
        );

        // ── 7. Network Mode ─────────────────────────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            NetworkModeSetting,
            new ComboBoxSetting("Network Mode", "bridge",
                ["bridge", "host", "none"])
            { HoverDescription = "Docker network mode. 'bridge' (default, isolated), 'host' (shares host network — needed for license servers), 'none' (no networking)." }
        );

        // ── 8. Auto-Remove ──────────────────────────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            AutoRemoveSetting,
            new CheckBoxSetting("Auto-Remove Containers", true)
            { HoverDescription = "Automatically remove the container after execution finishes. Disable to inspect container state post-run (useful for debugging)." }
        );

        // ── 9. Log Level (replaces binary verbose toggle) ────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            LogLevelSetting,
            new ComboBoxSetting("Log Level", "Verbose",
                ["Off", "Errors Only", "Info", "Verbose"])
            { HoverDescription = "Controls Docker SDK output verbosity. 'Off' = silent (native feel), 'Errors Only' = show only errors, 'Info' = errors + command, start/stop, timing, 'Verbose' = full SDK output including pull progress, digests, etc." }
        );

        // ── 10. Show Timestamps ──────────────────────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            ShowTimestampsSetting,
            new CheckBoxSetting("Show Timestamps in Logs", true)
            { HoverDescription = "Prepend HH:mm:ss.fff timestamps to Docker SDK log messages. Visible when Log Level is 'Info' or 'Verbose'." }
        );

        // ── 11. Container Name Prefix ────────────────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            ContainerNamePrefixSetting,
            new TextBoxSetting("Container Name Prefix", "containerextension-",
                "Prefix for container names (e.g., containerextension-yosys).")
            {
                HoverDescription = "Helps identify extension-created containers in 'docker ps'. Leave empty for Docker's random naming. Only letters, digits, hyphens, underscores, and dots are allowed.",
                Validator = new ContainerNameValidation()
            }
        );

        // ── 12. Extra Container Labels (advanced) ────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            ExtraFlagsSetting,
            new TextBoxSetting("Extra Container Labels", "",
                "Additional key=value labels attached to the container (e.g., project=thesis env=dev).")
            { HoverDescription = "Space-separated key=value pairs injected as container labels. Useful for filtering and identification in 'docker ps --filter label=key'. For raw Docker CLI flags, use the runtime path setting with a wrapper script." }
        );

        // ── 13. Dashboard Refresh Interval ───────────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            DashboardRefreshSetting,
            new ComboBoxSetting("Dashboard Refresh", "Manual",
                ["Manual", "2s", "5s", "10s", "15s", "30s", "60s", "120s"])
            { HoverDescription = "Auto-refresh interval for the Container Dashboard. 'Manual' = refresh only on open/button click. ⚠️ 2s rebuilds the entire UI tree each tick — use only for debugging. 5s–10s is recommended for active monitoring, 30s+ for background use." }
        );

        // ── 14. Telemetry Retention ──────────────────────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            TelemetryRetentionSetting,
            new ComboBoxSetting("Telemetry Retention", "100",
                ["None", "25", "50", "100", "250", "500", "1000", "Unlimited"])
            { HoverDescription = "Maximum number of execution records stored in container_telemetry.jsonl. 'None' disables telemetry and deletes the log file. Older entries are automatically trimmed on each new execution. Higher values provide richer history for thesis evaluation." }
        );

        // ── 15. Container Runtime Path (advanced) ──────────────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            DockerRuntimePathSetting,
            new FilePathSetting("Container Runtime Path", "",
                "Absolute path to the container runtime executable (e.g., docker, podman, orb).",
                null, File.Exists)
            { HoverDescription = "Used by the Diagnostics window for CLI operations. Leave empty to use 'docker' from PATH." }
        );

        // ── 16. Custom Daemon Socket (advanced, validated) ──────────────
        settingsService.RegisterSetting(
            "Binary Management", "Container Engine",
            DaemonSocketSetting,
            new TextBoxSetting("Custom Daemon Socket", "",
                "Optional: Override DOCKER_HOST (e.g. unix:///var/run/docker.sock or tcp://127.0.0.1:2375).")
            {
                HoverDescription = "Highest-priority socket override. Leave empty for auto-detection (probes: Docker → Podman → Colima → OrbStack).",
                Validator = new DaemonSocketValidation()
            }
        );

        // ── Fire-and-Forget Health Check + Pre-Pull ────────────────────
        // Use a CTS that cancels on process exit to prevent ObjectDisposedException
        // if the IDE shuts down while the background pre-pull is still running.
        var startupCts = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => startupCts.Cancel();

        // ── Inject Strategy into All Tools ──────────────────────────────
        var toolService = serviceProvider.Resolve<IToolService>();
        var dockerStrategy = serviceProvider.Resolve<DockerExecutionStrategy>();

        // Initial injection for tools already loaded
        InjectStrategyIntoAllTools(toolService, dockerStrategy, settingsService);

        // Robust polling mechanism to detect dynamically loaded tools (Resolves Issue #19 race conditions)
        // Uses the startupCts linked to ProcessExit to avoid leaks
        _ = Task.Run(async () =>
        {
            var knownToolCount = toolService.GetAllTools().Count;
            while (!startupCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, startupCts.Token).ConfigureAwait(false);
                    var currentToolCount = toolService.GetAllTools().Count;
                    if (currentToolCount != knownToolCount)
                    {
                        knownToolCount = currentToolCount;
                        InjectStrategyIntoAllTools(toolService, dockerStrategy, settingsService);
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("ContainerExtensionModule", "ToolPollingError", ex);
                }
            }
        }, startupCts.Token);

        // ── Create Dashboard VM (singleton) ──────────────────────────────
        var dockService = serviceProvider.Resolve<IMainDockService>();

        // Register with the dock framework for persistent right-side layout (matches AI Chat pattern)
        dockService.RegisterLayoutExtension<DockerDiagnosticsViewModel>(DockShowLocation.RightPinned);

        if (!_isUiRegistered)
        {
            _isUiRegistered = true;

            // ── Register Docker Toolbar Button ──────────────────────────────
            var windowService = serviceProvider.Resolve<IWindowService>();
            windowService.RegisterUiExtension("MainWindow_RightToolBarExtension",
                new OneWareUiExtension(sp =>
                {
                    var provider = (IServiceProvider)sp!;
                    return new DockerButtonView(
                        provider.Resolve<IMainDockService>(), 
                        provider.Resolve<DockerDiagnosticsViewModel>());
                }));

            // ── Register Dockable Dashboard View ────────────────────────────
            // Register a DataTemplate so OneWare can resolve the view for our VM
            Avalonia.Application.Current!.DataTemplates.Insert(0,
                new FuncDataTemplate<DockerDiagnosticsViewModel>((vm, _) =>
                    new DockerDiagnosticsView(vm.ServiceProvider ?? serviceProvider, vm.Strategy ?? dockerStrategy), true));
        }

        _ = Task.Run(async () =>
        {
            var ct = startupCts.Token;
            const int maxRetries = 10;
            const int retryDelayMs = 3000;
            bool isReachable = false;

            // Retry loop: Docker Desktop may still be starting its VM
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    isReachable = await dockerStrategy.PingAsync(cts.Token).ConfigureAwait(false);
                    if (isReachable) break;
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("ContainerExtensionModule", "Daemon ping transient error", ex);
                }

                if (attempt < maxRetries)
                {
                    await Console.Error.WriteLineAsync(
                        $"[ContainerExtension] ⏳ Daemon not reachable (attempt {attempt}/{maxRetries}), retrying in {retryDelayMs / 1000}s...").ConfigureAwait(false);
                    await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested) return;

            if (isReachable)
            {
                await Console.Out.WriteLineAsync(
                    $"[ContainerExtension] ✅ Connected to {dockerStrategy.DetectedRuntime} daemon.").ConfigureAwait(false);

                // ── Prune Dangling Containers (IDE Crash Recovery) ──
                try
                {
                    var svc = serviceProvider.Resolve<ISettingsService>();
                    var prefix = svc.HasSetting(ContainerNamePrefixSetting)
                        ? svc.GetSettingValue<string>(ContainerNamePrefixSetting)
                        : null;
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        var containersToPrune = await dockerStrategy.Client.Containers.ListContainersAsync(
                            new Docker.DotNet.Models.ContainersListParameters
                            {
                                All = true,
                                Filters = new Dictionary<string, IDictionary<string, bool>>
(StringComparer.Ordinal)
                                {
                                    { "name", new Dictionary<string, bool>(StringComparer.Ordinal) { { $"^{prefix}", true } } },
                                    { "status", new Dictionary<string, bool>(StringComparer.Ordinal) { { "exited", true }, { "dead", true }, { "created", true } } }
                                }
                            }, ct).ConfigureAwait(false);

                        foreach (var container in containersToPrune)
                        {
                            try
                            {
                                await dockerStrategy.Client.Containers.RemoveContainerAsync(container.ID,
                                    new Docker.DotNet.Models.ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
                                await Console.Out.WriteLineAsync($"[ContainerExtension] 🧹 Reaped dangling container: {string.Join(", ", container.Names)}").ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                ContainerTelemetry.TrackError("ContainerExtensionModule", $"Failed to reap container {string.Join(", ", container.Names)}", ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("ContainerExtensionModule", "Failed to clean dangling containers", ex);
                    await Console.Error.WriteLineAsync($"[ContainerExtension] ⚠️ Failed to clean dangling containers: {ex.Message}").ConfigureAwait(false);
                }

                // Background pre-pull: cache the default image so the first compile is instant
                try
                {
                    var defaultImage = dockerStrategy.GetDefaultImage();
                    using var pullCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pullCts.CancelAfter(TimeSpan.FromMinutes(10));
                    await dockerStrategy.PrePullImageAsync(defaultImage, pullCts.Token).ConfigureAwait(false);
                    await Console.Out.WriteLineAsync(
                        $"[ContainerExtension] 📦 Default image '{defaultImage}' is cached and ready.").ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* Shutdown requested — abort gracefully */ }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("ContainerExtensionModule", "Background pre-pull failed", ex);
                    await Console.Error.WriteLineAsync(
                        $"[ContainerExtension] ⚠️ Background pre-pull failed (non-critical): {ex.Message}").ConfigureAwait(false);
                }
            }
            else
            {
                await Console.Error.WriteLineAsync(
                    $"[ContainerExtension] ⚠️ Docker daemon is not reachable after {maxRetries} attempts. Container execution will fail until the daemon is started.").ConfigureAwait(false);
            }
        }, startupCts.Token);
    }

    /// <summary>
    /// Injects the DockerExecutionStrategy into all tools currently discovered by the IDE.
    /// Defers settings creation for tool-specific images until the tool is first seen.
    /// Safe to call multiple times (e.g. on PackagesUpdated).
    /// </summary>
    private static void InjectStrategyIntoAllTools(IToolService toolService, DockerExecutionStrategy dockerStrategy, ISettingsService settingsService)
    {
        foreach (var globalTool in toolService.GetAllTools())
        {
            toolService.RegisterStrategy(globalTool.Key, dockerStrategy);

            // Register per-tool image override setting (only if not already registered)
            if (!settingsService.HasSetting($"{PerToolImagePrefix}{globalTool.Key}"))
            {
                settingsService.RegisterSetting(
                    "Binary Management", "Execution Strategy",
                    $"{PerToolImagePrefix}{globalTool.Key}",
                    new TextBoxSetting(
                        $"Container Image for {globalTool.Name}",
                        "",
                        DefaultToolImages.TryGetValue(globalTool.Key, out var defaultHint) ? defaultHint : FallbackImage)
                    {
                        HoverDescription = $"Overrides the Default Toolchain Image when '{globalTool.Name}' is executed via Docker. Leave empty to use the global default.",
                        Validator = new DockerImageFormatValidation()
                    }
                );
            }

            // Append the Docker strategy option to the tool's ComboBox in Settings
            if (settingsService.HasSetting(globalTool.Key) && settingsService.GetSetting(globalTool.Key) is ComboBoxSetting comboSetting)
            {
                var newOptions = comboSetting.Options.ToList();
                if (!newOptions.Contains(dockerStrategy.GetStrategyKey()))
                {
                    newOptions.Add(dockerStrategy.GetStrategyKey());
                    comboSetting.Options = newOptions.ToArray();
                }
            }
        }
    }
}

