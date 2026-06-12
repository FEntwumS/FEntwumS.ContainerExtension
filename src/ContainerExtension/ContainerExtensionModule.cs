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

/// <summary>
/// Main entry point for the Container Extension module in OneWare Studio.
/// Handles settings registration, Docker execution strategy injection, and Dashboard UI mounting.
/// </summary>
public sealed class ContainerExtensionModule : OneWareModuleBase, IDisposable
{
    private static CancellationTokenSource? _workspaceCts;
    private static EventHandler? _processExitHandler;
    private static readonly System.Threading.Lock InitializeLock = new();
    private static System.ComponentModel.PropertyChangedEventHandler? _propertyChangedHandler;
    private static DockerDiagnosticsViewModel? _cachedDashboardVm;
    internal static IServiceProvider? GlobalServiceProvider { get; private set; }

    // Settings category and subcategory constants to prevent multiple string literal references
    public const string SettingsCategoryBinary = "Binary Management";
    public const string SettingsSubCategoryEngine = "Container Engine";
    public const string SettingsSubCategoryStrategy = "Execution Strategy";

    // Static cached validator instances to avoid allocations inside loops
    private static readonly ContainerNameValidation ContainerNameValidatorInstance = new();
    private static readonly DaemonSocketValidation DaemonSocketValidatorInstance = new();
    private static readonly DockerImageFormatValidation ImageFormatValidatorAllowEmpty = new(allowEmpty: true);
    private static readonly DockerImageFormatValidation ImageFormatValidatorNoEmpty = new(allowEmpty: false);

    public const string DockerRuntimePathSetting = "ContainerExtension_DockerRuntimePath";
    public const string DefaultImageSetting = "ContainerExtension_DefaultImage";
    public const string MemoryLimitSetting = "ContainerExtension_MemoryLimit";

    public const string PlatformSetting = "ContainerExtension_Platform";
    public const string ContainerNamePrefixSetting = "ContainerExtension_ContainerNamePrefix";
    public const string DockerBlueHex = "#2496ED";
    public const string WhaleIconPath = "M13.983 11.078h2.119a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.119a.185.185 0 00-.185.185v1.888c0 .102.083.185.185.185m-2.954-5.43h2.118a.186.186 0 00.186-.186V3.574a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m0 2.716h2.118a.187.187 0 00.186-.186V6.29a.186.186 0 00-.186-.185h-2.118a.185.185 0 00-.185.185v1.887c0 .102.082.185.185.185m-2.93 0h2.12a.186.186 0 00.184-.186V6.29a.185.185 0 00-.185-.185H8.1a.185.185 0 00-.185.185v1.887c0 .102.083.185.185.185m-2.964 0h2.119a.186.186 0 00.185-.186V6.29a.185.185 0 00-.185-.185H5.136a.186.186 0 00-.186.185v1.887c0 .102.084.185.186.185m5.893 2.715h2.118a.186.186 0 00.186-.185V9.006a.186.186 0 00-.186-.186h-2.118a.185.185 0 00-.185.185v1.888c0 .102.082.185.185.185m-2.93 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.083.185.185.185m-2.964 0h2.119a.185.185 0 00.185-.185V9.006a.185.185 0 00-.184-.186h-2.12a.186.186 0 00-.186.185v1.888c0 .102.084.185.186.185m-2.92 0h2.12a.185.185 0 00.184-.185V9.006a.185.185 0 00-.184-.186h-2.12a.185.185 0 00-.184.185v1.888c0 .102.082.185.185.185M23.763 9.89c-.065-.051-.672-.51-1.954-.51-.338.001-.676.03-1.01.087-.248-1.7-1.653-2.534-1.716-2.566l-.344-.199-.198.337c-.135.227-.235.467-.294.717-.221-.061-.453-.092-.686-.092h-13.8v2.32c-.006 1.764.12 3.524.375 5.27.7 4.793 4.295 7.64 9.079 7.64 5.378 0 8.017-2.732 8.783-4.529.742.062 1.488.083 2.228.064l.278-.01.096-.282c.164-.492.316-1.127.359-1.9H24l-.185-.815c-.217-.96-.45-1.916-.85-2.827l-.058-.124";
    public const string CpuLimitSetting = "ContainerExtension_CpuLimit";
    public const string AutoRemoveSetting = "ContainerExtension_AutoRemove";
    public const string AllowPrivilegedSetting = "ContainerExtension_AllowPrivileged";
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

    // Summary keys constants
    public const string SettingsKeyImage = "Image";
    public const string SettingsKeyPullPolicy = "Pull Policy";
    public const string SettingsKeyPlatform = "Platform";
    public const string SettingsKeyMemory = "Memory";
    public const string SettingsKeyCpu = "CPU";
    public const string SettingsKeyTimeout = "Timeout";
    public const string SettingsKeyNetwork = "Network";
    public const string SettingsKeyAutoRemove = "Auto-Remove";
    public const string SettingsKeyLogLevel = "Log Level";
    public const string SettingsKeyTimestamps = "Timestamps";
    public const string SettingsKeyNamePrefix = "Name Prefix";
    public const string SettingsKeyExtraLabels = "Extra Labels";
    public const string SettingsKeyDashboardRefresh = "Dashboard Refresh";
    public const string SettingsKeyRetention = "Retention";
    public const string SettingsKeyRuntimePath = "Runtime Path";

    public static readonly FrozenDictionary<string, string> DefaultToolImages =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
          ["ghdl"] = FallbackImage,
          ["nvc"] = "hdlc/nvc",
          ["iverilog"] = "hdlc/iverilog",
          ["verilator"] = "hdlc/verilator",
          ["yosys"] = FallbackImage,
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

    private static double _cachedHostMemoryMb = -1.0;

    internal static double GetHostMemoryMB()
    {
        var mem = Volatile.Read(ref _cachedHostMemoryMb);
        if (mem < 0)
        {
            mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0);
            Volatile.Write(ref _cachedHostMemoryMb, mem);
        }
        return mem;
    }

    /// <summary>
    /// Registers singleton services used across the Container Extension into the Dependency Injection container.
    /// </summary>
    /// <param name="services">The service collection builder.</param>
    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<DockerExecutionStrategy>();
        services.AddSingleton<DockerDiagnosticsViewModel>();
    }

    /// <summary>
    /// Initializes the module, populates UI settings, hooks up tool interceptors to redirect execution 
    /// through the Container API, and starts daemon connectivity polling.
    /// </summary>
    /// <param name="serviceProvider">The root service provider.</param>
    public override void Initialize(IServiceProvider serviceProvider)
    {
        GlobalServiceProvider = serviceProvider;
        CancellationToken ct;
        lock (InitializeLock)
        {
            _workspaceCts?.Cancel();
            _workspaceCts?.Dispose();
            _workspaceCts = new CancellationTokenSource();
            ct = _workspaceCts.Token;

            if (_processExitHandler != null)
            {
                AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
            }
            _processExitHandler = (_, _) =>
            {
                lock (InitializeLock)
                {
                    _workspaceCts?.Cancel();
                }
            };
            AppDomain.CurrentDomain.ProcessExit += _processExitHandler;

            if (_cachedDashboardVm != null && _propertyChangedHandler != null)
            {
                _cachedDashboardVm.PropertyChanged -= _propertyChangedHandler;
            }
            _cachedDashboardVm = null;
            _propertyChangedHandler = null;

            ContainerTelemetry.ResetShutdown();
            Views.UIBuilderHelpers.InitializeBrushes();
        }

        var settingsService = serviceProvider.Resolve<ISettingsService>();
        ContainerTelemetry.TelemetryOptedOutChecker = () => { return string.Equals(settingsService.SafeGetSetting<string>(ContainerExtensionModule.TelemetryRetentionSetting, "100"), "None", StringComparison.Ordinal); };

        settingsService.RegisterSettingSubCategory(SettingsCategoryBinary, SettingsSubCategoryEngine);
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, DefaultImageSetting, new TextBoxSetting("Default Toolchain Image", FallbackImage, "The default container image to pull and use for all tools.") { Validator = ImageFormatValidatorNoEmpty });
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, PullPolicySetting, new ComboBoxSetting("Image Pull Policy", "if-not-present", ["always", "if-not-present", "never"]));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, PlatformSetting, new ComboBoxSetting("Image Platform", "auto", ["auto", "linux/amd64", "linux/arm64", "linux/arm/v7"]));

        var totalRamMb = GetHostMemoryMB();
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, MemoryLimitSetting, new SliderSetting($"Memory Limit (0 = unlimited) — {totalRamMb:N0} MB available", 0, 0, totalRamMb, 256) { Validator = new ResourceThresholdValidation(totalRamMb * 0.75, totalRamMb, "memory") });

        var totalCores = (double)Environment.ProcessorCount;
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, CpuLimitSetting, new SliderSetting($"CPU Cores Limit (0 = unlimited) — {totalCores:N0} cores available", 0, 0, totalCores, 1) { Validator = new ResourceThresholdValidation(totalCores * 0.75, totalCores, "CPU") });

        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, TimeoutSetting, new SliderSetting("Execution Timeout (0 = no timeout)", 0, 0, 480, 5));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, NetworkModeSetting, new ComboBoxSetting("Network Mode", "bridge", ["bridge", "host", "none"]));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, AutoRemoveSetting, new CheckBoxSetting("Auto-Remove Containers", true));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, AllowPrivilegedSetting, new CheckBoxSetting("Allow Privileged Containers", false));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, LogLevelSetting, new ComboBoxSetting("Log Level", "Verbose", ["Off", "Errors Only", "Info", "Verbose"]));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, ShowTimestampsSetting, new CheckBoxSetting("Show Timestamps in Logs", true));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, ContainerNamePrefixSetting, new TextBoxSetting("Container Name Prefix", "containerextension-", "Prefix for container names.") { Validator = ContainerNameValidatorInstance });
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, ExtraFlagsSetting, new TextBoxSetting("Extra Container Labels", "", "Additional key=value labels attached to the container."));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, DashboardRefreshSetting, new ComboBoxSetting("Dashboard Refresh", "Manual", ["Manual", "2s", "5s", "10s", "15s", "30s", "60s", "120s"]));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, TelemetryRetentionSetting, new ComboBoxSetting("Telemetry Retention", "100", ["None", "25", "50", "100", "250", "500", "1000", "Unlimited"]));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, DockerRuntimePathSetting, new FilePathSetting("Container Runtime Path", "", "Absolute path to the container runtime executable.", null, ValidateRuntimePath));
        settingsService.RegisterSetting(SettingsCategoryBinary, SettingsSubCategoryEngine, DaemonSocketSetting, new TextBoxSetting("Custom Daemon Socket", "", "Optional: Override DOCKER_HOST.") { Validator = DaemonSocketValidatorInstance });

        IToolService toolService;
        DockerExecutionStrategy dockerStrategy;
        IMainDockService dockService;
        DockerDiagnosticsViewModel? dashboardVm = null;
        IApplicationCommandService? appCommandService = null;
        IWindowService? windowService = null;

        try
        {
            toolService = serviceProvider.Resolve<IToolService>() ?? throw new InvalidOperationException("IToolService is not registered.");
            dockerStrategy = serviceProvider.Resolve<DockerExecutionStrategy>() ?? throw new InvalidOperationException("DockerExecutionStrategy is not registered. (Fix 107)");
            dockService = serviceProvider.Resolve<IMainDockService>() ?? throw new InvalidOperationException("IMainDockService is not registered.");
            dashboardVm = serviceProvider.Resolve<DockerDiagnosticsViewModel>() ?? throw new InvalidOperationException("DockerDiagnosticsViewModel is not registered.");
            appCommandService = serviceProvider.Resolve<IApplicationCommandService>();
            windowService = serviceProvider.Resolve<IWindowService>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[ContainerExtension] DI Resolution Error: {ex.Message}");
            ContainerTelemetry.TrackError("ContainerExtensionModule", "DependencyInjectionResolutionError", ex);
            return;
        }

        InjectStrategyIntoAllTools(toolService, dockerStrategy, settingsService);

        _ = Task.Run(async () =>
        {
            var knownToolCount = toolService.GetAllTools().Count;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                var strategyKey = dockerStrategy.GetStrategyKey();
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;
                    var currentTools = toolService.GetAllTools();
                    var needsInjection = false;
                    foreach (var tool in currentTools)
                    {
                        if (settingsService.HasSetting(tool.Key) && settingsService.GetSetting(tool.Key) is ComboBoxSetting comboSetting && (comboSetting.Options == null || comboSetting.Options.Length == 0 || !OptionsContains(comboSetting.Options, strategyKey)))
                        {
                            needsInjection = true;
                            break;
                        }
                    }

                    var currentToolCount = currentTools.Count;
                    if (currentToolCount != knownToolCount || needsInjection)
                    {
                        knownToolCount = currentToolCount;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            InjectStrategyIntoAllTools(toolService, dockerStrategy, settingsService);
                        });
                    }
                }
            }
            catch (OperationCanceledException) { /* Ignore */ }
            catch (Exception ex) { ContainerTelemetry.TrackError("ContainerExtensionModule", "ToolPollingError", ex); }
        });

        try
        {
            dockService.RegisterLayoutExtension<DockerDiagnosticsViewModel>(DockShowLocation.RightPinned);
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("ContainerExtensionModule", "LayoutRegistrationError", ex);
        }

        if (appCommandService != null && dashboardVm != null)
        {
            var showDashboardCommand = new OneWare.Essentials.Commands.SimpleApplicationCommand(
                "Container Dashboard",
                () =>
                {
                    dockService.Show(dashboardVm, DockShowLocation.RightPinned);
                    dashboardVm.IsOpen = true;
                    dashboardVm.IsActive = true;
                },
                () => true
            );
            appCommandService.RegisterCommand(showDashboardCommand);
        }

        if (windowService != null && dashboardVm != null)
        {
            var menuItem = new OneWare.Essentials.Models.MenuItemModel("ContainerDashboardMenu")
            {
                Header = "Container Dashboard",
                Command = new RelayCommand(() =>
                {
                    dockService.Show(dashboardVm, DockShowLocation.RightPinned);
                    dashboardVm.IsOpen = true;
                    dashboardVm.IsActive = true;
                }),
                Icon = new OneWare.Essentials.Models.IconModel
                {
                    Icon = dashboardVm.Icon
                }
            };
            windowService.RegisterMenuItem("MainWindow_MainMenu/View/Tool Windows", menuItem);
        }

#pragma warning disable IDE0031
        var settingsLock = new System.Threading.Lock();
        if (dashboardVm != null)
        {
            _cachedDashboardVm = dashboardVm;
            _propertyChangedHandler = (sender, e) =>
            {
                lock (settingsLock)
                {


                    if (string.Equals(e.PropertyName, nameof(DockerDiagnosticsViewModel.IsOpen), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(DockerDiagnosticsViewModel.CanClose), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(DockerDiagnosticsViewModel.ShowInSelector), StringComparison.Ordinal) ||
                        string.Equals(e.PropertyName, nameof(DockerDiagnosticsViewModel.KeepPinnedDockableVisible), StringComparison.Ordinal))
                    {
                        if (!dashboardVm.CanClose || !dashboardVm.ShowInSelector || !dashboardVm.KeepPinnedDockableVisible)
                        {
                            dashboardVm.CanClose = true;
                            dashboardVm.ShowInSelector = true;
                            dashboardVm.KeepPinnedDockableVisible = true;
                        }
                    }
                    else if (string.Equals(e.PropertyName, nameof(DockerDiagnosticsViewModel.Owner), StringComparison.Ordinal) &&
                             dashboardVm.Owner == null &&
                             dashboardVm.IsOpen)
                    {
                        dockService.Show(dashboardVm, DockShowLocation.RightPinned);
                    }
                }
            };
            dashboardVm.PropertyChanged += _propertyChangedHandler;
        }
#pragma warning restore IDE0031

        // Deduplicate and Register DataTemplate completely statelessly (Guarded on UI thread)
        var app = Avalonia.Application.Current;
        if (app != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var existingTemplate = app.DataTemplates.FirstOrDefault(t => t is FuncDataTemplate<DockerDiagnosticsViewModel>);
                if (existingTemplate != null)
                {
                    app.DataTemplates.Remove(existingTemplate);
                }
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
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(5));
                    isReachable = await dockerStrategy.PingAsync(pingCts.Token).ConfigureAwait(false);
                    if (isReachable)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || ex is ObjectDisposedException)
                    {
                        return;
                    }
                    ContainerTelemetry.TrackError("ContainerExtensionModule", "Daemon ping transient error", ex);
                }

                if (attempt < maxRetries)
                {
                    await Console.Error.WriteLineAsync($"[ContainerExtension] ⏳ Daemon not reachable (attempt {attempt}/{maxRetries}), retrying in {retryDelayMs / 1000}s...").ConfigureAwait(false);
                    await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (isReachable)
            {
                await Console.Out.WriteLineAsync($"[ContainerExtension] ✅ Connected to {dockerStrategy.DetectedRuntime} daemon.").ConfigureAwait(false);

                try
                {
                    var prefix = settingsService.SafeGetSetting(ContainerNamePrefixSetting, (string?)null);
                    if (!string.IsNullOrWhiteSpace(prefix) && dockerStrategy.Client?.Containers != null)
                    {
                        var containersToPrune = await dockerStrategy.Client.Containers.ListContainersAsync(
                      new Docker.DotNet.Models.ContainersListParameters
                      {
                          All = true,
                          Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                    {
                  { "name", new Dictionary<string, bool>(StringComparer.Ordinal) { { prefix, true } } },
                  { "status", new Dictionary<string, bool>(StringComparer.Ordinal) { { "exited", true }, { "dead", true }, { "created", true } } }
                    }
                      }, ct).ConfigureAwait(false);

                        if (containersToPrune != null)
                        {
                            foreach (var container in containersToPrune)
                            {
                                if (container == null || string.IsNullOrEmpty(container.ID)) continue;
                                var matchesPrefix = container.Names != null && container.Names.Any(n =>
                                    n != null && (n.StartsWith(prefix, StringComparison.Ordinal) ||
                                                 n.StartsWith($"/{prefix}", StringComparison.Ordinal)));
                                if (!matchesPrefix) continue;
                                var names = container.Names != null ? string.Join(", ", container.Names) : container.ID;
                                try
                                {
                                    await dockerStrategy.Client.Containers.RemoveContainerAsync(container.ID, new Docker.DotNet.Models.ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
                                    await Console.Out.WriteLineAsync($"[ContainerExtension] 🧹 Reaped dangling container: {names}").ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    if (ct.IsCancellationRequested) return;
                                    ContainerTelemetry.TrackError("ContainerExtensionModule", $"Failed to reap container {names}", ex);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || ex is ObjectDisposedException) return;
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
        var allTools = toolService.GetAllTools();
        if (allTools == null) return;

        foreach (var globalTool in allTools)
        {
            if (globalTool == null || string.IsNullOrEmpty(globalTool.Key)) continue;

            toolService.RegisterStrategy(globalTool.Key, dockerStrategy);

            var settingKey = $"{PerToolImagePrefix}{globalTool.Key.ToLowerInvariant()}";
            if (!settingsService.HasSetting(settingKey))
            {
                settingsService.RegisterSetting(
                  SettingsCategoryBinary, SettingsSubCategoryStrategy,
                  settingKey,
                  new TextBoxSetting($"Container Image for {globalTool.Name}", "", DefaultToolImages.TryGetValue(globalTool.Key, out var defaultHint) ? defaultHint : FallbackImage)
                  {
                      HoverDescription = $"Overrides the Default Toolchain Image when '{globalTool.Name}' is executed via Docker.",
                      Validator = ImageFormatValidatorAllowEmpty
                  }
                );
            }

            if (settingsService.HasSetting(globalTool.Key) && settingsService.GetSetting(globalTool.Key) is ComboBoxSetting comboSetting)
            {
                var strategyKey = dockerStrategy.GetStrategyKey();
                if (!OptionsContains(comboSetting.Options, strategyKey))
                {
                    var newOptions = new object[comboSetting.Options.Length + 1];
                    Array.Copy(comboSetting.Options, newOptions, comboSetting.Options.Length);
                    newOptions[^1] = strategyKey;
                    comboSetting.Options = newOptions;
                }
            }
        }
    }

    private static bool OptionsContains(object[] options, string value)
    {
        if (options == null || options.Length == 0) return false;
        for (int idx = 0; idx < options.Length; idx++)
        {
            if (options[idx] is string str && string.Equals(str, value, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Safely terminates all background execution threads, UI handlers, and releases container process subscriptions.
    /// </summary>
    public void Dispose()
    {
        lock (InitializeLock)
        {
            try
            {
                _workspaceCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Token source is already disposed, safe to ignore
            }

            _workspaceCts?.Dispose();
            _workspaceCts = null;

            if (_processExitHandler != null)
            {
                try
                {
                    AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
                }
                catch (Exception)
                {
                    // Fail-safe domain unregistration on disposal
                }
                _processExitHandler = null;
            }

            if (_cachedDashboardVm != null && _propertyChangedHandler != null)
            {
                try
                {
                    _cachedDashboardVm.PropertyChanged -= _propertyChangedHandler;
                }
                catch (Exception)
                {
                    // Fail-safe property-change listener unregistration on disposal
                }
                _propertyChangedHandler = null;
                _cachedDashboardVm = null;
            }
        }
    }

    private static bool ValidateRuntimePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        if (System.IO.File.Exists(path))
        {
            return true;
        }

        var fileName = Path.GetFileName(path);
        if ((string.Equals(fileName, path, StringComparison.Ordinal) || !Path.IsPathRooted(path)) && ExistsOnPath(path))
        {
            return true;
        }

        return false;
    }

    private static bool ExistsOnPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var extensions = OperatingSystem.IsWindows()
            ? new[] { "", ".exe", ".cmd", ".bat" }
            : new[] { "" };

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return false;

        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var path in paths)
        {
            var cleanPath = path.Trim(' ', '"');
            if (string.IsNullOrEmpty(cleanPath)) continue;

            foreach (var ext in extensions)
            {
                try
                {
                    var fullPath = Path.Combine(cleanPath, fileName + ext);
                    if (System.IO.File.Exists(fullPath))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore path combinability issues
                }
            }
        }

        return false;
    }
}

internal class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action)
    {
        _action = action;
    }
    public bool CanExecute(object? parameter)
    {
        return true;
    }
    public void Execute(object? parameter)
    {
        _action();
    }
    public event EventHandler? CanExecuteChanged
    {
        add { /* Command execution state changes are not raised dynamically */ }
        remove { /* Command execution state changes are not raised dynamically */ }
    }
}
