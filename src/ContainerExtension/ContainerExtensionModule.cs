using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
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

public class ContainerExtensionModule : OneWareModuleBase
{
    private static CancellationTokenSource? _workspaceCts;
    private static bool _processExitHooked;

    public const string DockerRuntimePathSetting = "ContainerExtension_DockerRuntimePath";
    public const string DefaultImageSetting = "ContainerExtension_DefaultImage";
    public const string MemoryLimitSetting = "ContainerExtension_MemoryLimit";

    public const string PlatformSetting = "ContainerExtension_Platform";
    public const string ContainerNamePrefixSetting = "ContainerExtension_ContainerNamePrefix";
    public const string DockerBlueHex = "#2496ED";
    public const string WhaleIconPath = "M13.983 11.078h2.119a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.119a.185.185 0 00-.185.185v1.888c0 .102.083.185.185.185m-2.954-5.43h2.118a.186.186 0 00.186-.186V3.574a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m0 2.716h2.118a.187.187 0 00.186-.186V6.29a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.887c0 .102.082.185.185.185m-2.93 0h2.12a.186.186 0 00.184-.186V6.29a.185.185 0 00-.185-.185H8.1a.185.185 0 00-.185.185v1.887c0 .102.083.185.185.185m-2.964 0h2.119a.186.186 0 00.185-.186V6.29a.185.185 0 00-.185-.185H5.136a.186.186 0 00-.186.185v1.887c0 .102.084.185.186.185m5.893 2.715h2.118a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m-2.93 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.083.185.185.185m-2.964 0h2.119a.185.185 0 00.185-.185V9.006a.185.185 0 00-.184-.186h-2.12a.186.186 0 00-.186.185v1.888c0 .102.084.185.186.185m-2.92 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.082.185.185.185M23.763 9.89c-.065-.051-.672-.51-1.954-.51-.338.001-.676.03-1.01.087-.248-1.7-1.653-2.534-1.716-2.566l-.344-.199-.198.337c-.135.227-.235.467-.294.717-.221-.061-.453-.092-.686-.092h-13.8v2.32c-.006 1.764.12 3.524.375 5.27.7 4.793 4.295 7.64 9.079 7.64 5.378 0 8.017-2.732 8.783-4.529.742.062 1.488.083 2.228.064l.278-.01.096-.282c.164-.492.316-1.127.359-1.9H24l-.185-.815c-.217-.96-.45-1.916-.85-2.827l-.058-.124";
    public const string CpuLimitSetting = "ContainerExtension_CpuLimit";
    public const string AutoRemoveSetting = "ContainerExtension_AutoRemove";
    public const string DaemonSocketSetting = "ContainerExtension_DaemonSocket";
    public const string TimeoutSetting = "ContainerExtension_Timeout";
    public const string NetworkModeSetting = "ContainerExtension_NetworkMode";
    public const string PullPolicySetting = "ContainerExtension_PullPolicy";
    public const string ExtraFlagsSetting = "ContainerExtension_ExtraFlags";
    public const string DashboardRefreshSetting = "ContainerExtension_DashboardRefresh";
    public const string LogLevelSetting = "ContainerExtension_LogLevel";
    public const string ShowTimestampsSetting = "ContainerExtension_ShowTimestamps";
    public const string TelemetryRetentionSetting = "ContainerExtension_TelemetryRetention";
    public const string PerToolImagePrefix = "ContainerImage_";
    public const string FallbackImage = "hdlc/ghdl:yosys";

    public static readonly FrozenDictionary<string, string> DefaultToolImages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ghdl"] = "hdlc/ghdl:yosys",
            ["nvc"] = "hdlc/nvc",
            ["iverilog"] = "hdlc/iverilog",
            ["verilator"] = "hdlc/verilator",
            ["yosys"] = "hdlc/ghdl:yosys",
            ["apicula"] = "hdlc/apicula",
            ["nextpnr-ecp5"] = "hdlc/impl/prjtrellis",
            ["nextpnr-generic"] = "hdlc/impl/generic",
            ["nextpnr-ice40"] = "hdlc/impl/icestorm",
            ["nextpnr-nexus"] = "hdlc/impl/prjoxide",
            ["nextpnr-himbaechel"] = "hdlc/impl",
            ["nextpnr-machxo2"] = "hdlc/impl",
            ["openFPGALoader"] = "hdlc/prog",
            ["iceprog"] = "hdlc/impl/icestorm",
            ["icepack"] = "hdlc/impl/icestorm",
            ["gowin_pack"] = "hdlc/impl",
            ["gmpack"] = "hdlc/impl",
            ["gmupack"] = "hdlc/impl",
            ["gtkwave"] = "hdlc/gtkwave",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public const string DashboardTitle = "Container Dashboard";

    internal static double GetHostMemoryMB() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);

    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<DockerExecutionStrategy>();
        services.AddSingleton<DockerDiagnosticsViewModel>();
    }

    public override void Initialize(IServiceProvider serviceProvider)
    {
        _workspaceCts?.Cancel();
        _workspaceCts?.Dispose();
        _workspaceCts = new CancellationTokenSource();
        var ct = _workspaceCts.Token;

        if (!_processExitHooked)
        {
            _processExitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _workspaceCts?.Cancel();
        }

        var settingsService = serviceProvider.Resolve<ISettingsService>();

        settingsService.RegisterSettingSubCategory("Binary Management", "Container Engine");
        settingsService.RegisterSetting("Binary Management", "Container Engine", DefaultImageSetting, new TextBoxSetting("Default Toolchain Image", FallbackImage, "The default container image to pull and use for all tools.") { Validator = new DockerImageFormatValidation() });
        settingsService.RegisterSetting("Binary Management", "Container Engine", PullPolicySetting, new ComboBoxSetting("Image Pull Policy", "if-not-present", ["always", "if-not-present", "never"]));
        settingsService.RegisterSetting("Binary Management", "Container Engine", PlatformSetting, new ComboBoxSetting("Image Platform", "auto", ["auto", "linux/amd64", "linux/arm64", "linux/arm/v7"]));

        var totalRamMb = GetHostMemoryMB();
        settingsService.RegisterSetting("Binary Management", "Container Engine", MemoryLimitSetting, new SliderSetting($"Memory Limit (0 = unlimited) — {totalRamMb:N0} MB available", 0, 0, totalRamMb, 256) { Validator = new ResourceThresholdValidation(totalRamMb * 0.75, totalRamMb, "memory") });

        var totalCores = (double)Environment.ProcessorCount;
        settingsService.RegisterSetting("Binary Management", "Container Engine", CpuLimitSetting, new SliderSetting($"CPU Cores Limit (0 = unlimited) — {totalCores:N0} cores available", 0, 0, totalCores, 1) { Validator = new ResourceThresholdValidation(totalCores * 0.75, totalCores, "CPU") });

        settingsService.RegisterSetting("Binary Management", "Container Engine", TimeoutSetting, new SliderSetting("Execution Timeout (0 = no timeout)", 0, 0, 480, 5));
        settingsService.RegisterSetting("Binary Management", "Container Engine", NetworkModeSetting, new ComboBoxSetting("Network Mode", "bridge", ["bridge", "host", "none"]));
        settingsService.RegisterSetting("Binary Management", "Container Engine", AutoRemoveSetting, new CheckBoxSetting("Auto-Remove Containers", true));
        settingsService.RegisterSetting("Binary Management", "Container Engine", LogLevelSetting, new ComboBoxSetting("Log Level", "Verbose", ["Off", "Errors Only", "Info", "Verbose"]));
        settingsService.RegisterSetting("Binary Management", "Container Engine", ShowTimestampsSetting, new CheckBoxSetting("Show Timestamps in Logs", true));
        settingsService.RegisterSetting("Binary Management", "Container Engine", ContainerNamePrefixSetting, new TextBoxSetting("Container Name Prefix", "containerextension-", "Prefix for container names.") { Validator = new ContainerNameValidation() });
        settingsService.RegisterSetting("Binary Management", "Container Engine", ExtraFlagsSetting, new TextBoxSetting("Extra Container Labels", "", "Additional key=value labels attached to the container."));
        settingsService.RegisterSetting("Binary Management", "Container Engine", DashboardRefreshSetting, new ComboBoxSetting("Dashboard Refresh", "Manual", ["Manual", "2s", "5s", "10s", "15s", "30s", "60s", "120s"]));
        settingsService.RegisterSetting("Binary Management", "Container Engine", TelemetryRetentionSetting, new ComboBoxSetting("Telemetry Retention", "100", ["None", "25", "50", "100", "250", "500", "1000", "Unlimited"]));
        settingsService.RegisterSetting("Binary Management", "Container Engine", DockerRuntimePathSetting, new FilePathSetting("Container Runtime Path", "", "Absolute path to the container runtime executable.", null, System.IO.File.Exists));
        settingsService.RegisterSetting("Binary Management", "Container Engine", DaemonSocketSetting, new TextBoxSetting("Custom Daemon Socket", "", "Optional: Override DOCKER_HOST.") { Validator = new DaemonSocketValidation() });

        var toolService = serviceProvider.Resolve<IToolService>();
        var dockerStrategy = serviceProvider.Resolve<DockerExecutionStrategy>();

        InjectStrategyIntoAllTools(toolService, dockerStrategy, settingsService);

        _ = Task.Run(async () =>
        {
            var knownToolCount = toolService.GetAllTools().Count;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    var currentToolCount = toolService.GetAllTools().Count;
                    if (currentToolCount != knownToolCount)
                    {
                        knownToolCount = currentToolCount;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                            InjectStrategyIntoAllTools(toolService, dockerStrategy, settingsService);
                        });
                    }
                }
            }
            catch (OperationCanceledException) { /* Ignore */ }
            catch (Exception ex) { ContainerTelemetry.TrackError("ContainerExtensionModule", "ToolPollingError", ex); }
        });

        var dockService = serviceProvider.Resolve<IMainDockService>();
        dockService.RegisterLayoutExtension<DockerDiagnosticsViewModel>(DockShowLocation.RightPinned);

        // Deduplicate and Register DataTemplate completely statelessly (Guarded on UI thread)
        var app = Avalonia.Application.Current;
        if (app != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                var existingTemplate = app.DataTemplates.FirstOrDefault(t => t is FuncDataTemplate<DockerDiagnosticsViewModel>);
                if (existingTemplate != null) app.DataTemplates.Remove(existingTemplate);

                app.DataTemplates.Insert(0, new FuncDataTemplate<DockerDiagnosticsViewModel>((vm, _) =>
                {
                    if (vm != null)
                    {
                        vm.ServiceProvider ??= serviceProvider;
                        vm.Strategy ??= dockerStrategy;
                        return new DockerDiagnosticsView(vm.ServiceProvider, vm.Strategy);
                    }
                    return new Avalonia.Controls.TextBlock { Text = "Loading...", Foreground = Avalonia.Media.Brushes.Gray, Margin = new Avalonia.Thickness(20) };
                }, true));
            });
        }

        _ = Task.Run(async () =>
        {
            const int maxRetries = 10;
            const int retryDelayMs = 3000;
            bool isReachable = false;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(5));
                    isReachable = await dockerStrategy.PingAsync(pingCts.Token).ConfigureAwait(false);
                    if (isReachable) break;
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("ContainerExtensionModule", "Daemon ping transient error", ex);
                }

                if (attempt < maxRetries)
                {
                    await Console.Error.WriteLineAsync($"[ContainerExtension] ⏳ Daemon not reachable (attempt {attempt}/{maxRetries}), retrying in {retryDelayMs / 1000}s...").ConfigureAwait(false);
                    await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested) return;

            if (isReachable)
            {
                await Console.Out.WriteLineAsync($"[ContainerExtension] ✅ Connected to {dockerStrategy.DetectedRuntime} daemon.").ConfigureAwait(false);

                try
                {
                    var prefix = settingsService.SafeGetSetting(ContainerNamePrefixSetting, (string?)null);
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        var containersToPrune = await dockerStrategy.Client.Containers.ListContainersAsync(
                            new Docker.DotNet.Models.ContainersListParameters
                            {
                                All = true,
                                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                                {
                                    { "name", new Dictionary<string, bool>(StringComparer.Ordinal) { { $"^{prefix}", true } } },
                                    { "status", new Dictionary<string, bool>(StringComparer.Ordinal) { { "exited", true }, { "dead", true }, { "created", true } } }
                                }
                            }, ct).ConfigureAwait(false);

                        foreach (var container in containersToPrune)
                        {
                            try
                            {
                                await dockerStrategy.Client.Containers.RemoveContainerAsync(container.ID, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
                                await Console.Out.WriteLineAsync($"[ContainerExtension] 🧹 Reaped dangling container: {string.Join(", ", container.Names)}").ConfigureAwait(false);
                            }
                            catch (Exception ex) { ContainerTelemetry.TrackError("ContainerExtensionModule", $"Failed to reap container {string.Join(", ", container.Names)}", ex); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("ContainerExtensionModule", "Failed to clean dangling containers", ex);
                }

                try
                {
                    var defaultImage = dockerStrategy.GetDefaultImage();
                    using var pullCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pullCts.CancelAfter(TimeSpan.FromMinutes(10));
                    await dockerStrategy.PrePullImageAsync(defaultImage, pullCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* Ignore */ }
                catch (Exception ex) { ContainerTelemetry.TrackError("ContainerExtensionModule", "Background pre-pull failed", ex); }
            }
            else
            {
                await Console.Error.WriteLineAsync($"[ContainerExtension] ⚠️ Docker daemon is not reachable after {maxRetries} attempts.").ConfigureAwait(false);
            }
        });
    }

    private static void InjectStrategyIntoAllTools(IToolService toolService, DockerExecutionStrategy dockerStrategy, ISettingsService settingsService)
    {
        foreach (var globalTool in toolService.GetAllTools())
        {
            toolService.RegisterStrategy(globalTool.Key, dockerStrategy);

            if (!settingsService.HasSetting($"{PerToolImagePrefix}{globalTool.Key}"))
            {
                settingsService.RegisterSetting(
                    "Binary Management", "Execution Strategy",
                    $"{PerToolImagePrefix}{globalTool.Key}",
                    new TextBoxSetting($"Container Image for {globalTool.Name}", "", DefaultToolImages.TryGetValue(globalTool.Key, out var defaultHint) ? defaultHint : FallbackImage)
                    {
                        HoverDescription = $"Overrides the Default Toolchain Image when '{globalTool.Name}' is executed via Docker.",
                        Validator = new DockerImageFormatValidation()
                    }
                );
            }

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