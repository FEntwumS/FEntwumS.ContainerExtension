// MA0004 (ConfigureAwait) is suppressed file-wide: this is Avalonia UI code whose awaits must resume on
// the UI thread, so ConfigureAwait(false) would be wrong. MA0006 (reference vs value equality) and S108
// (empty block) cover pervasive UI-event-handler style — control reference comparisons and intentionally
// empty best-effort catch blocks.
#pragma warning disable MA0004, MA0006, S108
using static ContainerExtension.Views.UIBuilderHelpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using OneWare.Essentials.Services;
using OneWare.Essentials.Models;
using Avalonia.Threading;
using Avalonia.Automation;
using Avalonia.Platform.Storage;

namespace ContainerExtension.Views;

/// <summary>
/// Docker Desktop-style dashboard <see cref="UserControl"/> providing live insight
/// into the local container ecosystem. Queries the Docker.DotNet SDK directly
/// (via <see cref="DockerExecutionStrategy"/>) for real-time data.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
  Justification = "UserControl lifecycle manages _refreshCts disposal via DetachedFromVisualTree handler.")]
public partial class DockerDiagnosticsView : UserControl
{
    // Grid column indices for sortable header rows (CA1861 - avoid per-call allocation)
    private static readonly int[] ThreeColumnIndices = { 0, 2, 4 };
    private static readonly int[] SevenColumnIndices = { 0, 2, 4, 6, 8, 10, 12 };

    // Cached brushes and geometries to avoid allocations during rebuild loops.
    // DockerBlueBrush is reserved for the brand whale PathIcon only; every other accent
    // usage routes through the themeable AccentColor.
    private static readonly Geometry WhaleGeometry = Geometry.Parse(ContainerExtensionModule.WhaleIconPath);
    private static readonly SolidColorBrush DockerBlueBrush = new(Color.Parse(ContainerExtensionModule.DockerBlueHex));

    // Instance State
    private readonly DockerExecutionStrategy _strategy;
    private readonly ITerminalManagerService _terminalService;

    // Prepended to every command injected into the interactive terminal so a non-empty input line (e.g. a
    // half-typed "v") cannot corrupt it into "vdocker ...". Ctrl-E moves the cursor to the end of the line
    // and Ctrl-U discards the whole line, leaving a clean prompt before the command is typed.
    private const string TerminalLineReset = "\u0005\u0015";

    // The project's toolchain image is produced locally (Build Local Image / build_oss_cad_suite.sh) and is
    // NOT published to a registry, so Pull / Check-for-Updates cannot fetch it. Used to redirect those
    // actions to Build Local Image instead of attempting a doomed registry pull (which 404s).
    private static bool IsBuildOnlyImage(string image) =>
        !string.IsNullOrEmpty(image) && image.StartsWith("fentwums/oss-cad-suite", StringComparison.OrdinalIgnoreCase);
    private readonly StackPanel _statusContent;
    private readonly StackPanel _configContent;
    private readonly StackPanel _containersContent;
    private readonly StackPanel _imagesContent;
    private readonly StackPanel _telemetryContent;
    private readonly StackPanel _toolchainContent;
    private readonly TextBlock _headerTitle;
    private string _pluginVersion;
    private readonly StackPanel _quickActionsRow;
    private readonly ISettingsService _settingsService;
    private readonly TextBox _searchBox;
    private readonly IServiceProvider _serviceProvider;
    private readonly Border _statusBanner;
    private readonly TextBlock _statusBannerText;
    // Monotonic token identifying the current banner message; the auto-hide timer dismisses by token, not by
    // message text, so a newer banner is never cleared early by a previous message's still-pending timer.
    private long _bannerToken;

    // KPI Metrics Controls (assigned via CreateMetricCard in the constructor)
    private readonly TextBlock _metricDaemonStatusText;
    private readonly TextBlock _metricDaemonDetailText;
    private readonly Border _metricDaemonBorder;
    private readonly TextBlock _metricContainersText;
    private readonly TextBlock _metricContainersDetailText;
    private readonly TextBlock _metricImagesText;
    private readonly TextBlock _metricImagesDetailText;
    private readonly TextBlock _metricDiskText;
    private readonly TextBlock _metricDiskDetailText;
    // Tracks open container log windows to prevent duplicate spawning
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Window> _openLogWindows = new(StringComparer.Ordinal);

    // Auto-refresh state
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _detachCleanupCts;
    private DispatcherTimer? _autoRefreshTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly DispatcherTimer _indicatorBlinkTimer;
    private int _indicatorBlinkTicks;
    private readonly EventHandler _themeChangedHandler;
    private readonly Border _refreshIndicator;
    private readonly TextBlock _lastRefreshedText;
    private readonly TextBlock _countdownText;
    private int _refreshIntervalSeconds;
    private int _secondsUntilRefresh;
    private bool _hasAttached; // Guard against duplicate AttachedToVisualTree handlers
    private bool _hasFocusedOnce; // First-appearance focus flag; set once, never reset on detach
    private IDisposable? _isVisibleSubscription;

    // Cached Data (for re-sorting without re-querying the daemon)
    private readonly System.Threading.Lock _cachedDataLock = new();
    private readonly List<Grid> _recycledContainerRows = new();
    private readonly List<Grid> _recycledImageRows = new();
    private IList<Docker.DotNet.Models.ContainerListResponse> _cachedContainers = Array.Empty<Docker.DotNet.Models.ContainerListResponse>();
    private bool _showAllContainers;
    private IList<Docker.DotNet.Models.ImagesListResponse> _cachedImages = Array.Empty<Docker.DotNet.Models.ImagesListResponse>();
    private bool _showAllImages;
    private (int imageCount, long totalSizeBytes, long reclaimableBytes) _cachedDiskUsage;
    private Docker.DotNet.Models.SystemInfoResponse? _cachedSystemInfo; // last non-null snapshot, for theme repaints

    // Search/Filter State
    private string _searchFilter = "";

    // Sort State (column name + direction per table)
    private (string column, bool ascending) _containerSort = ("name", true);
    private (string column, bool ascending) _imageSort = ("repo", true);
    private (string column, bool ascending) _historySort = ("time", false); // newest-first default

    // Data Fingerprints (skip UI rebuild when data is unchanged)
    private int _lastContainerFingerprint;
    private int _lastImageFingerprint;
    private bool? _wasDockerOnline;
    private string? _lastPermanentMessage;
    private bool _lastPermanentIsError;

    /// <summary>
    /// Constructs the Docker Desktop-style dashboard as a <see cref="UserControl"/>.
    /// </summary>
    public DockerDiagnosticsView(IServiceProvider serviceProvider, DockerExecutionStrategy strategy)
    {
        _strategy = strategy;
        _serviceProvider = serviceProvider;
        _terminalService = serviceProvider.Resolve<ITerminalManagerService>();
        InitializeContainerCommands();

        // Resolve the IDE's theme background brush
        UpdateBackgroundBrush();

        // Register to ActualThemeVariantChanged to repaint when the host theme is toggled.
        // Held in a field so it can be detached in DetachedFromVisualTree (lifecycle hygiene).
        _themeChangedHandler = (sender, args) => UpdateThemeColors();
        ActualThemeVariantChanged += _themeChangedHandler;

        _pluginVersion = CachedPluginVersion;

        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _searchDebounceTimer.Tick += (s, e) =>
        {
            _searchDebounceTimer.Stop();
            ApplySearchFilter();
        };

        // Single reusable timer drives the refresh-pulse blink; recreated per-refresh previously,
        // which leaked a live timer if the control detached mid-blink. The Tick handler is
        // wired after _refreshIndicator is constructed below.
        _indicatorBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        // Header
        var whaleIcon = new PathIcon
        {
            Data = WhaleGeometry,
            Foreground = DockerBlueBrush,
            Width = 20,
            Height = 20
        };

        _headerTitle = new TextBlock
        {
            Text = ContainerExtensionModule.DashboardTitle,
            FontSize = TitleFontSize,
            FontWeight = FontWeight.Bold,
            Foreground = FontColor,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(_headerTitle, "Docker Diagnostics Dashboard Header");

        var headerTitlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerTitlePanel.Children.Add(whaleIcon);
        headerTitlePanel.Children.Add(_headerTitle);

        // Global Search / Filter
        var searchShortcut = OperatingSystem.IsMacOS() ? "Cmd+F" : "Ctrl+F";
        _searchBox = new TextBox
        {
            Watermark = $"Filter containers, images, history...  ({searchShortcut})",
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TabIndex = 1
        };
        AutomationProperties.SetName(_searchBox, "Filter containers, images, history text search box");
        AutomationProperties.SetHelpText(_searchBox, $"Type to filter all sections. Press {searchShortcut} to focus, Escape to clear.");

        var clearBtn = new Button
        {
            Content = "×",
            Margin = new Thickness(2),
            Background = null,
            BorderBrush = null,
            IsVisible = false,
            Command = new RelayCommand(() => { _searchBox.Text = ""; })
        };
        AutomationProperties.SetName(clearBtn, "Clear search filter");
        ToolTip.SetTip(clearBtn, "Clear filter (Esc)");
        _searchBox.InnerRightContent = clearBtn;

        // Use cached listener method instead of lambda to prevent delegate instantiation allocations
        _searchBox.TextChanged += OnSearchBoxTextChanged;

        // Implement drag-and-drop validation for setting and config file paths
        DragDrop.SetAllowDrop(_searchBox, true);
        _searchBox.AddHandler(DragDrop.DragOverEvent, OnSearchBoxDragOver);
        _searchBox.AddHandler(DragDrop.DropEvent, OnSearchBoxDrop);

        _refreshIndicator = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = PillCornerRadius,
            Background = GreenColor,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Opacity = 0.0
        };
        _indicatorBlinkTimer.Tick += (s, e) =>
        {
            _indicatorBlinkTicks++;
            if (_indicatorBlinkTicks > 6)
            {
                _refreshIndicator.Opacity = 0.0;
                _indicatorBlinkTimer.Stop();
            }
            else
            {
                _refreshIndicator.Opacity = _indicatorBlinkTicks % 2 == 1 ? 1.0 : 0.2;
            }
        };

        _lastRefreshedText = new TextBlock
        {
            Text = "",
            FontFamily = MonoFont,
            FontSize = 11,
            Foreground = MutedColor,
            VerticalAlignment = VerticalAlignment.Center
        };
        _countdownText = new TextBlock
        {
            Text = "",
            FontFamily = MonoFont,
            FontSize = 11,
            Foreground = MutedColor,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var refreshBtn = new Button
        {
            Content = "Refresh",
            Padding = new Thickness(12, 6),
            Command = new AsyncRelayCommand(RefreshAllAsync),
            TabIndex = 2
        };
        ToolTip.SetTip(refreshBtn, "Re-query the Docker daemon for live container, image, and system data");
        AutomationProperties.SetName(refreshBtn, "Refresh dashboard data button");

        var statusBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        statusBar.Children.Add(_refreshIndicator);
        statusBar.Children.Add(_lastRefreshedText);
        statusBar.Children.Add(_countdownText);
        statusBar.Children.Add(refreshBtn);

        var header = new Grid
        {
            Margin = new Thickness(0, 0, 0, 4),
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        Grid.SetColumn(headerTitlePanel, 0);
        Grid.SetColumn(statusBar, 1);
        header.Children.Add(headerTitlePanel);
        header.Children.Add(statusBar);

        // Quick Actions (inline below header)
        _quickActionsRow = BuildQuickActionsRow();
        _quickActionsRow.Opacity = 0.5;  // dimmed until daemon is confirmed reachable
        _quickActionsRow.IsEnabled = false;

        // KPI Row
        var kpiGrid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 8),
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 6,
            ColumnSpacing = 6
        };

        var daemonCard = CreateMetricCard("DAEMON STATUS", "Offline", "Not Connected", RedColor, out _metricDaemonStatusText, out _metricDaemonDetailText, out _metricDaemonBorder);
        var containersCard = CreateMetricCard("CONTAINERS", "0 Running", "0 total", AccentColor, out _metricContainersText, out _metricContainersDetailText, out _);
        var imagesCard = CreateMetricCard("IMAGES", "0 Images", "0 B total", AccentColor, out _metricImagesText, out _metricImagesDetailText, out _);
        var diskCard = CreateMetricCard("RECLAIMABLE SPACE", "0 B", "No dangling items", AccentColor, out _metricDiskText, out _metricDiskDetailText, out _);

        Grid.SetColumn(daemonCard, 0); Grid.SetRow(daemonCard, 0);
        Grid.SetColumn(containersCard, 1); Grid.SetRow(containersCard, 0);
        Grid.SetColumn(imagesCard, 0); Grid.SetRow(imagesCard, 1);
        Grid.SetColumn(diskCard, 1); Grid.SetRow(diskCard, 1);

        kpiGrid.Children.Add(daemonCard);
        kpiGrid.Children.Add(containersCard);
        kpiGrid.Children.Add(imagesCard);
        kpiGrid.Children.Add(diskCard);

        _statusContent = new StackPanel { Spacing = 4 };
        _statusContent.Children.Add(CreateLoadingText("Connecting to daemon..."));
        var statusSection = CreateCard("Connection Status", _statusContent);

        _containersContent = new StackPanel { Spacing = 2 };
        _containersContent.Children.Add(CreateLoadingText("Loading containers..."));
        var containersScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _containersContent
        };
        var containersSection = CreateCard("Containers", containersScroll);

        _imagesContent = new StackPanel { Spacing = 2 };
        _imagesContent.Children.Add(CreateLoadingText("Loading images..."));
        var imagesScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _imagesContent
        };
        var imagesSection = CreateCard("Images & Disk Usage", imagesScroll);

        _configContent = new StackPanel { Spacing = 2 };
        _configContent.Children.Add(CreateLoadingText("Reading settings..."));
        var configSection = CreateCard("Active Configuration", _configContent);

        _telemetryContent = new StackPanel { Spacing = 2 };
        var telemetryScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _telemetryContent
        };
        var telemetrySection = CreateCard("Execution History", telemetryScroll);

        _toolchainContent = new StackPanel { Spacing = 4 };
        _toolchainContent.Children.Add(CreateLoadingText("Loading available versions..."));
        var toolchainSection = CreateCard("Toolchain Environment", _toolchainContent);

        _statusBannerText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FontColor
        };
        // Announce status/operation results to assistive technology as they appear.
        AutomationProperties.SetLiveSetting(_statusBannerText, AutomationLiveSetting.Polite);

        var closeBannerBtn = new Button
        {
            Content = "×",
            Padding = new Thickness(6, 2),
            Background = null,
            BorderBrush = null,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(closeBannerBtn, "Dismiss status message");
        ToolTip.SetTip(closeBannerBtn, "Dismiss");

        var bannerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        Grid.SetColumn(_statusBannerText, 0);
        Grid.SetColumn(closeBannerBtn, 1);
        bannerGrid.Children.Add(_statusBannerText);
        bannerGrid.Children.Add(closeBannerBtn);

        _statusBanner = new Border
        {
            BorderThickness = HairlineThickness,
            CornerRadius = InnerCornerRadius,
            Padding = new Thickness(12, 8),
            IsVisible = false,
            Child = bannerGrid,
            Margin = new Thickness(0, 4, 0, 4)
        };

        // Bump the token so any in-flight auto-hide/restore timer for the current message is invalidated and
        // cannot re-show the banner after the user has explicitly dismissed it.
        closeBannerBtn.Command = new RelayCommand(() => { System.Threading.Interlocked.Increment(ref _bannerToken); _statusBanner.IsVisible = false; });

        // Sections Panel (1-Grid Layout)
        var sectionsPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                statusSection,
                containersSection,
                imagesSection,
                telemetrySection,
                toolchainSection,
                configSection
            }
        };

        // Layout
        var mainPanel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                header,
                _statusBanner,
                _searchBox,
                _quickActionsRow,
                kpiGrid,
                sectionsPanel
            }
        };

        // Enable cycling tab focus within the dashboard layout boundary
        KeyboardNavigation.SetTabNavigation(mainPanel, KeyboardNavigationMode.Cycle);

        Content = new ScrollViewer { Content = mainPanel };

        // Dashboard-wide keyboard map (tunnelling so it wins before child controls consume keys):
        //   Ctrl/Cmd+F -> focus + select the search box (fulfils the advertised watermark shortcut)
        //   F5 / Ctrl+R -> refresh now
        //   Ctrl/Cmd+, -> open settings
        //   Escape (while the search box holds text) -> clear the filter
        AddHandler(KeyDownEvent, OnDashboardKeyDown, RoutingStrategies.Tunnel);

        // Resolve settings service for auto-refresh interval
        _settingsService = serviceProvider.Resolve<ISettingsService>();

        // Fire-and-forget: load all live data after the control renders + start auto-refresh.
        var attachCmd = new AsyncRelayCommand(async () =>
        {
            if (_hasAttached)
            {
                return; // Prevent duplicate handlers on dock/undock cycles
            }
            _hasAttached = true;
            try
            {
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "RefreshAllAsync_Attach", ex);
            }
            try
            {
                await PopulateToolchainEnvironmentAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "PopulateToolchainEnvironment_Attach", ex);
            }
            // If the control detached during the awaits above, DetachedFromVisualTree already ran and reset
            // _hasAttached; do not create a subscription that would then never be disposed (leaking the view).
            if (!_hasAttached)
            {
                return;
            }
            _isVisibleSubscription?.Dispose();
            _isVisibleSubscription = this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                // The entire body must be guarded: an exception thrown here escapes the Rx OnNext
                // and can tear down the dispatcher. Keep the timer calls inside the catch.
                try
                {
                    if (visible)
                    {
                        if (!_hasFocusedOnce)
                        {
                            // Focus the search box only on the first appearance, never on subsequent
                            // dock/undock or tab re-shows — yanking the caret otherwise steals focus
                            // from whatever the user was doing in the IDE. The flag is never reset on
                            // detach, so a re-attach does not re-focus.
                            Dispatcher.UIThread.Post(() => { _searchBox.Focus(); });
                            _hasFocusedOnce = true;
                        }
                        else
                        {
                            _ = RefreshAllSafeAsync();
                        }
                        StartAutoRefreshTimer();
                    }
                    else
                    {
                        StopAutoRefreshTimer();
                    }
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("DockerDiagnosticsView", "AutoRefresh_IsVisible", ex);
                }
            });
            StartAutoRefreshTimer();
        });
        AttachedToVisualTree += (_, _) =>
        {
            try
            {
                _detachCleanupCts?.Cancel();
                _detachCleanupCts?.Dispose();
            }
            catch (Exception)
            {
                // Ignore errors during cleanup of pending detach task
            }
            _detachCleanupCts = null;

            // Re-arm the theme handler detached on the previous teardown (idempotent: detach first).
            ActualThemeVariantChanged -= _themeChangedHandler;
            ActualThemeVariantChanged += _themeChangedHandler;

            attachCmd.Execute(null);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StopAutoRefreshTimer();
            _searchDebounceTimer.Stop();
            _indicatorBlinkTimer.Stop();
            _hasAttached = false; // Allow re-attach to refresh again

            // Detach the theme handler so a detached control does not react to host theme toggles.
            ActualThemeVariantChanged -= _themeChangedHandler;

            _isVisibleSubscription?.Dispose();
            _isVisibleSubscription = null;

            try
            {
                _detachCleanupCts?.Cancel();
                _detachCleanupCts?.Dispose();
            }
            catch (Exception)
            {
                // Ignore errors during cancellation/disposal of the previous detach task
            }

            var cts = new CancellationTokenSource();
            _detachCleanupCts = cts;
            var token = cts.Token;

            _ = Task.Delay(1500, token).ContinueWith(t =>
            {
                try
                {
                    if (t.IsCanceled) return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        var windowsToClose = _openLogWindows.Values.ToList();
                        _openLogWindows.Clear();
                        foreach (var w in windowsToClose)
                        {
                            if (w != null)
                            {
                                try
                                {
                                    w.Close();
                                }
                                catch (Exception ex)
                                {
                                    ContainerTelemetry.TrackError("DockerDiagnosticsView", "CloseOrphanLogWindow", ex);
                                }
                            }
                        }
                    });
                }
                finally
                {
                    cts.Dispose();
                }
            }, TaskScheduler.Default);
        };
    }

    private void UpdateBackgroundBrush()
    {
        object? bgRes = null;
        Application.Current?.TryFindResource("ThemeBackgroundBrushOp", Application.Current.ActualThemeVariant, out bgRes);
        bgRes ??= Application.Current?.FindResource("ThemeBackgroundBrushOp");
        Background = (bgRes as IBrush) ?? Brushes.Transparent;
    }

    private void UpdateThemeColors()
    {
        UpdateBackgroundBrush();
        UIBuilderHelpers.InitializeBrushes();

        // Repaint static elements using the new brushes
        _headerTitle.Foreground = FontColor;
        _searchBox.Foreground = FontColor;
        _lastRefreshedText.Foreground = MutedColor;
        _countdownText.Foreground = MutedColor;
        _refreshIndicator.Background = GreenColor;

        // Repaint the data sections from cached state rather than re-querying the daemon: a
        // light/dark toggle must never trigger network I/O just to recolor the UI.
        RepaintSectionsFromCache();
    }

    /// <summary>
    /// Rebuilds the data-driven sections using the last cached daemon snapshot so a theme change
    /// recolors every row without a Docker round-trip. Fingerprints are reset so the populate
    /// methods do not short-circuit on unchanged data.
    /// </summary>
    private void RepaintSectionsFromCache()
    {
        IList<Docker.DotNet.Models.ContainerListResponse> containers;
        IList<Docker.DotNet.Models.ImagesListResponse> images;
        (int imageCount, long totalSizeBytes, long reclaimableBytes) diskUsage;
        lock (_cachedDataLock)
        {
            containers = _cachedContainers;
            images = _cachedImages;
            diskUsage = _cachedDiskUsage;
        }

        // Force a rebuild even though the underlying data is unchanged.
        _lastContainerFingerprint = 0;
        _lastImageFingerprint = 0;
        _lastTelemetryFingerprint = 0;

        try
        {
            PopulateStatus(_wasDockerOnline == true, _cachedSystemInfo);
            if (_wasDockerOnline == true)
            {
                PopulateContainers(containers);
                PopulateImages(images, diskUsage);
            }
            else
            {
                PopulateOfflineSections();
            }
            PopulateConfig(_strategy.GetActiveSettingsSummary());
            _ = PopulateTelemetryAsync();
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView", "RepaintSectionsFromCache", ex);
        }
    }

    // Drag & Drop Handlers for Search Box
    private static void OnSearchBoxDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnSearchBoxDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                foreach (var file in files)
                {
                    var path = file.Path.LocalPath;
                    if (IsValidLocalPath(path))
                    {
                        _searchBox.Text = path;
                        break;
                    }
                }
            }
        }
    }

    private static bool IsValidLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // Block path traversal
        if (path.Contains("..", StringComparison.Ordinal)) return false;

        // Restrict system files traversal
        if (path.StartsWith("/System", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/etc", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/sbin", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(":\\Windows", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(":\\System32", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    // TextChanged Event Handler
    private void OnSearchBoxTextChanged(object? sender, EventArgs e)
    {
        if (_searchBox.InnerRightContent is Button clearBtn)
        {
            clearBtn.IsVisible = !string.IsNullOrEmpty(_searchBox.Text);
        }
        try
        {
            _searchFilter = _searchBox.Text ?? "";
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView", "SearchBox_TextChanged", ex);
        }
    }


    // Dashboard Keyboard Shortcuts
    private void OnDashboardKeyDown(object? sender, KeyEventArgs e)
    {
        var primaryModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        // Escape clears the filter when the search box holds text and has focus.
        if (e.Key == Key.Escape && _searchBox.IsFocused && !string.IsNullOrEmpty(_searchBox.Text))
        {
            _searchBox.Text = "";
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == primaryModifier)
        {
            switch (e.Key)
            {
                case Key.F:
                    _searchBox.Focus();
                    _searchBox.SelectAll();
                    e.Handled = true;
                    return;
                case Key.R:
                    _ = RefreshAllSafeAsync();
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    _ = ShowSettingsDialogAsync();
                    e.Handled = true;
                    return;
                default:
                    break;
            }
        }

        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            _ = RefreshAllSafeAsync();
            e.Handled = true;
        }
    }

    /// <summary>Updates the "Last refreshed" timestamp display in the header status bar.</summary>
    private void UpdateLastRefreshedTimestamp()
    {
        _lastRefreshedText.Text = $"Last refreshed: {DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture)}";

        // Restart the single shared blink timer instead of allocating a throwaway one each refresh.
        _indicatorBlinkTicks = 0;
        _indicatorBlinkTimer.Stop();
        _indicatorBlinkTimer.Start();
    }

    /// <summary>
    /// Starts the auto-refresh and countdown timers based on the Dashboard Refresh setting.
    /// Called once after the initial data load completes.
    /// Stops any existing timers first to prevent GC pressure from rapid dock/undock cycles.
    /// </summary>
    private void StartAutoRefreshTimer()
    {
        // Stop any previously created timers to prevent accumulation on re-attach
        StopAutoRefreshTimer();
        _refreshCts = new CancellationTokenSource();

        var interval = _settingsService.HasSetting(ContainerExtensionModule.DashboardRefreshSetting)
          ? _settingsService.GetSettingValue<string>(ContainerExtensionModule.DashboardRefreshSetting)
          : "Manual";
        if (string.IsNullOrEmpty(interval) || string.Equals(interval, "Manual", StringComparison.Ordinal))
        {
            _countdownText.Text = "";
            return;
        }

        if (interval.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(interval.AsSpan(0, interval.Length - 1), out var secs))
        {
            _refreshIntervalSeconds = secs;
        }
        else if (interval.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(interval.AsSpan(0, interval.Length - 1), out var mins))
        {
            _refreshIntervalSeconds = mins * 60;
        }
        else
        {
            _refreshIntervalSeconds = 0;
        }

        if (_refreshIntervalSeconds <= 0)
        {
            _countdownText.Text = "";
            return;
        }

        _secondsUntilRefresh = _refreshIntervalSeconds;
        _countdownText.Text = $"| next in {_secondsUntilRefresh}s";

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoRefreshTimer.Tick += (s, e) =>
        {
            if (_refreshCts == null || _refreshCts.IsCancellationRequested)
            {
                StopAutoRefreshTimer();
                return;
            }

            if (Volatile.Read(ref _isRefreshingFlag) == 1)
            {
                _countdownText.Text = "Refreshing...";
                return;
            }

            _secondsUntilRefresh--;
            if (_secondsUntilRefresh <= 0)
            {
                _secondsUntilRefresh = _refreshIntervalSeconds;
                _countdownText.Text = "Refreshing...";
                _ = RefreshAllSafeAsync();
            }
            else if (_refreshCts != null && !_refreshCts.IsCancellationRequested)
            {
                _countdownText.Text = $"| next in {_secondsUntilRefresh}s";
            }
        };
        _autoRefreshTimer.Start();
    }

    private async Task RefreshAllSafeAsync()
    {
        try
        {
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView", "AutoRefresh", ex);
        }
    }

    /// <summary>
    /// Stops and disposes of the active auto-refresh timer.
    /// </summary>
    private void StopAutoRefreshTimer()
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = null;
        SafeCancelAndDisposeCts(ref _refreshCts);
    }

    private int _isRefreshingFlag;

    /// <summary>
    /// Creates a sortable header row for a table. Each column label becomes a clickable button.
    /// Clicking a column sorts the table by that column; clicking the same column toggles direction.
    /// The active sort column shows an Up or Down indicator.
    /// </summary>
    /// <param name="columns">Array of (label, sortKey) tuples for each column.</param>
    /// <param name="currentSort">The current sort state (column, ascending).</param>
    /// <param name="onSort">Callback invoked with the clicked column's sort key.</param>
    /// <param name="columnDefinitions">Grid column definitions matching the data row layout.</param>
    /// <param name="gridColumns">The Grid column indices for each sortable column.</param>
    /// <param name="trailingActionLabel">Optional label for a non-sortable trailing "ACTIONS" column.</param>
    /// <param name="trailingActionCol">Grid column index for the actions label.</param>
    private static Grid CreateSortableHeaderRow(
    (string label, string sortKey)[] columns,
    (string column, bool ascending) currentSort,
    Action<string> onSort,
    string columnDefinitions,
    int[] gridColumns,
    string? trailingActionLabel = null,
    int trailingActionCol = -1)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(columnDefinitions),
            Margin = new Thickness(0, 0)
        };

        for (int i = 0; i < columns.Length; i++)
        {
            var (label, sortKey) = columns[i];
            var col = gridColumns[i];
            var isActive = string.Equals(currentSort.column, sortKey, StringComparison.Ordinal);
            var arrow = isActive ? (currentSort.ascending ? " ▲" : " ▼") : "";
            var capturedKey = sortKey;

            var content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children =
        {
          new TextBlock
          {
            Text = label,
            FontFamily = MonoFont, FontSize = 12, FontWeight = FontWeight.Bold,
            Foreground = isActive ? FontColor : AccentColor
          },
          new TextBlock
          {
            Text = arrow,
            FontFamily = MonoFont, FontSize = 10, FontWeight = FontWeight.Bold,
            Foreground = isActive ? FontColor : AccentColor,
            VerticalAlignment = VerticalAlignment.Center
          }
        }
            };

            var btn = new Button
            {
                Content = content,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = false,
                Command = new RelayCommand(() => onSort(capturedKey))
            };
            // Announce the sort affordance and current direction in text (the ▲/▼ glyph is
            // visual-only and the active column is otherwise distinguished by color alone).
            Avalonia.Automation.AutomationProperties.SetName(btn, isActive
                ? $"{label}, sorted {(currentSort.ascending ? "ascending" : "descending")}, activate to sort {(currentSort.ascending ? "descending" : "ascending")}"
                : $"{label}, activate to sort");
            Grid.SetColumn(btn, col);
            grid.Children.Add(btn);
        }

        if (trailingActionLabel != null && trailingActionCol >= 0)
        {
            AddGridCell(grid, trailingActionCol, trailingActionLabel, true, AccentColor);
        }

        return grid;
    }

    /// <summary>
    /// Refreshes all dashboard sections by querying the Docker daemon.
    /// Parallelizes API calls for containers, images, and system info.
    /// </summary>
    private async Task RefreshAllAsync()
    {
        if (!IsVisible || !_hasAttached)
        {
            return;
        }

        if (System.Threading.Interlocked.CompareExchange(ref _isRefreshingFlag, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Reset countdown so manual refresh pushes out the next auto-refresh
            _secondsUntilRefresh = _refreshIntervalSeconds;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var refreshCts = _refreshCts;
            CancellationToken refreshCt = default;
            try
            {
                refreshCt = refreshCts?.Token ?? default;
            }
            catch (ObjectDisposedException)
            {
                refreshCt = default;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(refreshCt, timeoutCts.Token);
            var ct = cts.Token;

            // Read settings snapshot (always available, synchronous)
            var settings = _strategy.GetActiveSettingsSummary();

            // Run all API queries in parallel directly, avoiding redundant Ping call
            var infoTask = _strategy.GetSystemInfoAsync(ct);
            var containersTask = _strategy.ListContainersAsync(ct);
            var imagesTask = _strategy.ListImagesAsync(ct);

            Docker.DotNet.Models.SystemInfoResponse? info = null;
            IList<Docker.DotNet.Models.ContainerListResponse> containers = Array.Empty<Docker.DotNet.Models.ContainerListResponse>();
            IList<Docker.DotNet.Models.ImagesListResponse> images = Array.Empty<Docker.DotNet.Models.ImagesListResponse>();

            try
            {
                await Task.WhenAll(infoTask, containersTask, imagesTask).ConfigureAwait(false);
                info = await infoTask.ConfigureAwait(false);
                containers = await containersTask.ConfigureAwait(false);
                images = await imagesTask.ConfigureAwait(false);

                if (info == null)
                {
                    // A null /info response does NOT by itself mean the daemon is down: some runtimes
                    // (notably OrbStack) can fail or stall the /info endpoint while /containers, /images
                    // and /_ping all succeed and containerized execution works. Probe liveness directly
                    // before blanking the dashboard, so a transient /info fault is not misreported as
                    // "Daemon offline" (which contradicts a working execution path).
                    var alive = await _strategy.PingAsync(ct).ConfigureAwait(false);
                    if (!alive)
                    {
                        throw new DockerExecutionException("Docker/OrbStack daemon is unreachable.");
                    }
                    // Daemon reachable; render online with the data we have, in a degraded state where
                    // only the system-info card reports "unavailable" (info stays null below).
                }

                // Compute disk usage from the already-fetched image list (avoids duplicate API call)
                var diskUsage = DockerExecutionStrategy.ComputeDiskUsage(images);

                await Dispatcher.UIThread.InvokeAsync(() => ApplyDaemonOnlineUi(info, containers, images, diskUsage, settings));
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "RefreshAllAsync_ParallelQuery", ex);
                await Dispatcher.UIThread.InvokeAsync(() => ApplyDaemonOfflineUi(ex, settings));
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isRefreshingFlag, 0);
        }
    }

    /// <summary>Applies the daemon-online snapshot to the header, KPI cards, and data sections.</summary>
    private void ApplyDaemonOnlineUi(
        Docker.DotNet.Models.SystemInfoResponse? info,
        IList<Docker.DotNet.Models.ContainerListResponse> containers,
        IList<Docker.DotNet.Models.ImagesListResponse> images,
        (int imageCount, long totalSizeBytes, long reclaimableBytes) diskUsage,
        Dictionary<string, string> settings)
    {
        if (!_hasAttached)
        {
            return;
        }
        _headerTitle.Text = ContainerExtensionModule.DashboardTitle;
        ToolTip.SetTip(_headerTitle, null);
        PopulateStatus(true, info);

        // KPI metrics (online state). The daemon is reachable here even when info is null (degraded:
        // /info failed but the liveness ping in RefreshAllAsync succeeded), so the card stays green.
        // Cache the last non-null system info so a theme repaint (RepaintSectionsFromCache) can render it
        // without a daemon round-trip, instead of falsely reporting it temporarily unavailable.
        if (info != null)
        {
            _cachedSystemInfo = info;
        }
        _metricDaemonStatusText.Text = "Online";
        _metricDaemonDetailText.Text = info != null
            ? $"{info.Name ?? "Connected"} ({_strategy.DetectedRuntime})"
            : $"System info unavailable ({_strategy.DetectedRuntime})";
        _metricDaemonBorder.Background = GreenColor;

        var running = containers.Count(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase));
        _metricContainersText.Text = $"{running} Running";
        _metricContainersDetailText.Text = $"{containers.Count} total containers";

        _metricImagesText.Text = $"{images.Count} Images";
        _metricImagesDetailText.Text = $"{FormatBytesBinary(diskUsage.totalSizeBytes)} total size";

        _metricDiskText.Text = FormatBytesBinary(diskUsage.reclaimableBytes);
        // Count untagged/dangling images (matching the reclaimable-size metric): the /images/json list
        // endpoint leaves i.Containers unpopulated (-1), so the prior "Containers == 0" count was always 0.
        _metricDiskDetailText.Text = $"{images.Count(ContainerExtension.Services.Docker.DockerImageManager.IsUnusedImage)} unused images";

        if (_wasDockerOnline == false)
        {
            ShowTemporaryStatus("Docker daemon is back online.");
        }
        else if (_wasDockerOnline == null)
        {
            _statusBanner.IsVisible = false;
        }

        // Transitioning into the online state (from offline, or the first render): the data sections may
        // still hold the offline placeholder that PopulateOfflineSections wrote. Invalidate the fingerprints
        // so the skip-if-unchanged guard below cannot suppress the repaint — critically for an empty list,
        // whose fingerprint is 0, the same value PopulateOfflineSections resets to. Without this, coming
        // online with zero containers leaves "Daemon offline" stuck in the Containers section.
        if (_wasDockerOnline != true)
        {
            _lastContainerFingerprint = -1;
            _lastImageFingerprint = -1;
        }
        _wasDockerOnline = true;

        // Skip-if-unchanged: compare a lightweight fingerprint of container/image data to avoid a
        // full UI tree rebuild when nothing has changed (critical at 2s/5s refresh rates).
        var containerFp = DashboardFingerprint.ForContainers(containers);
        var imageFp = HashCode.Combine(images.Count, images.Sum(i => i.Size));

        if (containerFp != _lastContainerFingerprint)
        {
            _lastContainerFingerprint = containerFp;
            PopulateContainers(containers);
        }
        else
        {
            // Structure unchanged (same count/ids/states): skip the full row rebuild, but keep the live
            // status/uptime and CPU/RAM cells current via in-place updates so they do not freeze between
            // structural changes (e.g. a long-running container whose uptime keeps advancing).
            RefreshLiveContainerCells(containers);
        }
        if (imageFp != _lastImageFingerprint)
        {
            _lastImageFingerprint = imageFp;
            PopulateImages(images, diskUsage);
        }

        PopulateConfig(settings);
        _quickActionsRow.IsEnabled = true;
        _quickActionsRow.Opacity = 1.0;
        _ = PopulateTelemetryAsync();
        UpdateHeaderBadge(containers.Count);
        UpdateLastRefreshedTimestamp();
    }

    /// <summary>Applies the daemon-offline state to the header, KPI cards, and data sections.</summary>
    private void ApplyDaemonOfflineUi(Exception ex, Dictionary<string, string> settings)
    {
        if (!_hasAttached)
        {
            return;
        }
        _headerTitle.Text = $"{ContainerExtensionModule.DashboardTitle} — Offline / API Error";
        ToolTip.SetTip(_headerTitle, ex.Message);
        _quickActionsRow.IsEnabled = false;
        _quickActionsRow.Opacity = 0.5;
        PopulateStatus(false, null);

        // KPI metrics (offline state)
        _metricDaemonStatusText.Text = "Offline";
        _metricDaemonDetailText.Text = "Daemon unreachable";
        _metricDaemonBorder.Background = RedColor;
        _metricContainersText.Text = "—";
        _metricContainersDetailText.Text = "No active daemon";
        _metricImagesText.Text = "—";
        _metricImagesDetailText.Text = "No active daemon";
        _metricDiskText.Text = "—";
        _metricDiskDetailText.Text = "No active daemon";

        PopulateConfig(settings);
        PopulateOfflineSections(); // Clear stale lists and show offline sections
        _ = PopulateTelemetryAsync();
        UpdateHeaderBadge(0);
        UpdateLastRefreshedTimestamp();

        if (_wasDockerOnline == null || _wasDockerOnline == true)
        {
            ShowTemporaryError("Docker daemon is offline", ex, isTemporary: false);
        }
        _wasDockerOnline = false;
    }

    /// <summary>Populates the Toolchain Environment section by checking the remote registry for default image updates.</summary>
    private async Task PopulateToolchainEnvironmentAsync()
    {
        var settings = _strategy.GetActiveSettingsSummary();
        var currentImage = settings.GetValueOrDefault("Image", ContainerExtensionModule.FallbackImage);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_hasAttached)
            {
                return;
            }
            _toolchainContent.Children.Clear();
            _toolchainContent.Children.Add(new TextBlock
            {
                Text = $"Checking versions for: {currentImage}...",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
        });

        List<string> tags = new();
        var refreshCts = _refreshCts;
        CancellationToken refreshCt = default;
        try
        {
            refreshCt = refreshCts?.Token ?? default;
        }
        catch (ObjectDisposedException)
        {
            refreshCt = default;
        }
        try
        {
            tags = await ContainerExtension.Registry.RegistryClient.FetchTagsAsync(currentImage, refreshCt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView", "FetchTagsAsync", ex);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_hasAttached || _toolchainContent == null)
            {
                return;
            }
            _toolchainContent.Children.Clear();

            var row = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Children.Add(new TextBlock
            {
                Text = "Active Image:",
                Foreground = FontColor,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4)
            });

            if (tags.Count > 0)
            {
                var comboBox = new ComboBox
                {
                    Width = 250,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 8, 4)
                };
                AutomationProperties.SetName(comboBox, "Active toolchain image and tag");
                var lastColonIdx = currentImage.LastIndexOf(':');
                var lastSlashIdx = currentImage.LastIndexOf('/');
                string baseImage;
                if (lastColonIdx > lastSlashIdx)
                {
                    baseImage = currentImage.Substring(0, lastColonIdx);
                }
                else
                {
                    baseImage = currentImage;
                }
                var items = tags.Select(t => $"{baseImage}:{t}").ToList();
                comboBox.ItemsSource = items;
                comboBox.SelectedItem = items.FirstOrDefault(i => i.Equals(currentImage, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();

                // Only allow switching to tags for the current image
                comboBox.SelectionChanged += (s, e) =>
                {
                    if (comboBox.SelectedItem is string newImage && !string.Equals(newImage, currentImage, StringComparison.Ordinal))
                    {
                        try
                        {
                            _settingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, newImage);
                            ShowTemporaryStatus($"Active toolchain image set to '{newImage}'.");
                            _ = RefreshAllSafeAsync(); // refresh configuration display
                        }
                        catch (Exception ex)
                        {
                            ShowTemporaryError("Failed to change active image", ex);
                        }
                    }
                };

                row.Children.Add(comboBox);
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = currentImage,
                    Foreground = AccentColor,
                    FontFamily = MonoFont,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 4, 8, 4)
                });

                row.Children.Add(new TextBlock
                {
                    Text = IsBuildOnlyImage(currentImage)
                        ? "(local-only image — build via Build Local Image; not on a registry)"
                        : "(No registry tags — local-only image or registry unavailable)",
                    Foreground = MutedColor,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 8, 4)
                });
            }

            Button btn = null!;
            btn = new Button
            {
                Content = "Check for Updates & Pull",
                Padding = new Thickness(8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            };
            btn.Command = new AsyncRelayCommand(async () =>
        {
            var activeImg = tags.Count > 0 && row.Children[1] is ComboBox cb && cb.SelectedItem is string sel ? sel : currentImage;
            if (IsBuildOnlyImage(activeImg))
            {
                ShowTemporaryStatus($"'{activeImg}' is built locally, not pulled — use Build Local Image to produce or update it.", isError: false, isTemporary: false);
                return;
            }
            // Defense in depth: registry tags are grammar-validated at the source (RegistryClient), but never
            // interpolate an image reference into the interactive terminal without re-checking it here.
            if (!ContainerExtension.Validations.DockerImageFormatValidation.IsValidReference(activeImg))
            {
                ShowTemporaryStatus($"Refusing to pull '{activeImg}': not a valid image reference.", isError: true, isTemporary: false);
                return;
            }
            var prevTip = ToolTip.GetTip(btn);
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    btn.IsEnabled = false;
                    var pullText = new TextBlock { Text = "Pulling...", VerticalAlignment = VerticalAlignment.Center };
                    var pullProgress = new ProgressBar { IsIndeterminate = true, Width = 50, Height = 4, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    var pullStack = new StackPanel { Orientation = Orientation.Horizontal };
                    pullStack.Children.Add(pullText);
                    pullStack.Children.Add(pullProgress);
                    btn.Content = pullStack;
                });

                var runtimePath = _strategy.GetRuntimePath();
                var pull = await _terminalService.ExecuteInTerminalAsync(TerminalLineReset + $"{runtimePath} pull \"{activeImg}\"", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

                if (pull.TimedOut || pull.ExitCode != 0)
                {
                    // A failed pull must not trigger a prune; surface the failure and keep existing layers intact.
                    var detail = pull.TimedOut ? "the operation timed out" : $"exit code {pull.ExitCode}";
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        btn.Content = "Error";
                        ToolTip.SetTip(btn, $"Update failed: {detail}.");
                    });
                    await Task.Delay(3000).ConfigureAwait(false);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ToolTip.SetTip(btn, prevTip);
                    });
                    return;
                }

                // Prune dangling images to free disk space
                _ = _strategy.PruneDanglingImagesAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "UpdateAndPullImage", ex);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    btn.Content = "Error";
                    ToolTip.SetTip(btn, $"Update failed: {ex.Message}");
                });
                await Task.Delay(3000).ConfigureAwait(false);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ToolTip.SetTip(btn, prevTip);
                });
            }
            finally
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    btn.Content = "Check for Updates & Pull";
                    btn.IsEnabled = true;
                });
            }
        });
            ToolTip.SetTip(btn, "Pulls the selected version of the toolchain and safely cleans up old dangling layers.");
            AutomationProperties.SetName(btn, "Check for toolchain updates and pull");

            row.Children.Add(btn);
            _toolchainContent.Children.Add(row);
        });
    }

    /// <summary>Populates the Connection Status section with daemon health and system info.</summary>
    private void PopulateStatus(bool isReachable, Docker.DotNet.Models.SystemInfoResponse? info)
    {
        _statusContent.Children.Clear();

        var statusDot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = isReachable ? GreenColor : RedColor,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(statusDot, isReachable ? "Daemon online" : "Daemon offline");

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        statusRow.Children.Add(statusDot);
        statusRow.Children.Add(new TextBlock
        {
            // Leading glyph conveys state without relying on the dot color alone (WCAG 1.4.1).
            Text = isReachable
            ? $"{_strategy.DetectedRuntime} daemon — connected"
            : $"{_strategy.DetectedRuntime} daemon — unreachable",
            Foreground = FontColor,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });

        // "Open Desktop" button - launches the container runtime's GUI app
        var desktopAppName = GetDesktopAppName(_strategy.DetectedRuntime);
        if (desktopAppName != null)
        {
            var openDesktopBtn = new Button
            {
                Content = "Open Desktop",
                FontSize = 10,
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            openDesktopBtn.Command = new AsyncRelayCommand(async () =>
            {
                openDesktopBtn.IsEnabled = false;
                openDesktopBtn.Content = "Opening...";
                try
                {
                    var launched = await Task.Run(() => LaunchDesktopApp(_strategy.DetectedRuntime)).ConfigureAwait(true);
                    if (launched)
                    {
                        await Task.Delay(1000).ConfigureAwait(true);
                    }
                    else
                    {
                        ShowTemporaryStatus($"Could not open {desktopAppName}. Make sure it is installed.", isError: true);
                    }
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("DockerDiagnosticsView", "OpenDesktopBtn_Click", ex);
                    ShowTemporaryStatus($"Could not open {desktopAppName}: {ex.Message}", isError: true);
                }
                finally
                {
                    openDesktopBtn.Content = "Open Desktop";
                    openDesktopBtn.IsEnabled = true;
                }
            });
            ToolTip.SetTip(openDesktopBtn, $"Launch {desktopAppName}");
            AutomationProperties.SetName(openDesktopBtn, $"Open {desktopAppName}");
            statusRow.Children.Add(openDesktopBtn);
        }

        if (!isReachable)
        {
            var reconnectBtn = new Button
            {
                Content = "Retry Connection",
                FontSize = 10,
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Command = new AsyncRelayCommand(RefreshAllAsync)
            };
            ToolTip.SetTip(reconnectBtn, "Re-run the daemon reachability test now");
            AutomationProperties.SetName(reconnectBtn, "Retry daemon connection");
            statusRow.Children.Add(reconnectBtn);
        }

        _statusContent.Children.Add(statusRow);

        if (info != null)
        {
            var detailsGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,12,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto")
            };

            AddInfoRow(detailsGrid, 0, "Server Version", info.ServerVersion ?? "unknown");
            AddInfoRow(detailsGrid, 1, "Operating System", info.OperatingSystem ?? "unknown");
            AddInfoRow(detailsGrid, 2, "Total Memory", FormatBytes(info.MemTotal));
            AddInfoRow(detailsGrid, 3, "CPUs / Containers", $"{info.NCPU} cores / {info.Containers} containers ({info.ContainersRunning} running)");

            _statusContent.Children.Add(detailsGrid);
        }
        else if (!isReachable)
        {
            // Distinguish "no runtime installed" from "installed but stopped" so the user gets
            // the right next step instead of a generic message.
            var runtime = _strategy.DetectedRuntime;
            var hint = string.IsNullOrEmpty(runtime)
                ? "No container runtime detected. Install Docker Desktop, OrbStack, or Podman, then click Retry Connection. FPGA tools will run natively until a runtime is available."
                : $"{runtime} is installed but its daemon is not running. Start {GetDesktopAppName(runtime) ?? runtime}, then click Retry Connection.";
            _statusContent.Children.Add(new TextBlock
            {
                Text = hint,
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18, 4, 0, 0)
            });
        }
        else
        {
            // Reachable, but /info was unavailable this cycle (degraded online). State it plainly so
            // the green "Online" indicator is not mistaken for a complete, healthy system-info read.
            _statusContent.Children.Add(new TextBlock
            {
                Text = $"{_strategy.DetectedRuntime} daemon is reachable; system information is temporarily unavailable.",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18, 4, 0, 0)
            });
        }
    }

    /// <summary>Populates the Active Configuration section with card-grouped settings layout.</summary>
    private void PopulateConfig(Dictionary<string, string> settings)
    {
        _configContent.Children.Clear();

        // Extension Metadata Card
        var telemetryPath = ContainerTelemetry.TelemetryFilePath;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                telemetryPath = telemetryPath.Replace(home, "~", StringComparison.Ordinal);
            }
        }
        catch (Exception)
        {
            /* telemetry-path scrub failure is non-fatal */
        }

        var metaPanel = new StackPanel { Spacing = 6 };
        metaPanel.Children.Add(new TextBlock
        {
            Text = "ENVIRONMENT INFO",
            Foreground = AccentColor,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var metaGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,12,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };
        // labelIndent 0 aligns these labels flush with the grouped-settings labels below; the default
        // 18 px indent is reserved for the daemon-details rows that sit under the status banner.
        AddInfoRow(metaGrid, 0, "Extension", $"Container Extension {_pluginVersion}", labelIndent: 0);
        AddInfoRow(metaGrid, 1, "Runtime", $"{_strategy.DetectedRuntime} | .NET {Environment.Version}", labelIndent: 0);
        AddInfoRow(metaGrid, 2, "Telemetry", telemetryPath, labelIndent: 0);
        metaPanel.Children.Add(metaGrid);

        _configContent.Children.Add(CreateSubCard(metaPanel));

        // Grouped settings — the panel layout lives in ActiveConfigLayout so the display-coverage invariant
        // (every key GetActiveSettingsSummary emits is rendered here) is unit-testable without constructing
        // this Avalonia control.
        foreach (var (title, keys) in ActiveConfigLayout.Groups)
        {
            var pairs = keys
                .Where(k => settings.ContainsKey(k))
                .Select(k => (key: k, value: settings[k]))
                .ToArray();
            if (pairs.Length == 0)
            {
                continue;
            }

            var groupPanel = new StackPanel { Spacing = 6 };
            groupPanel.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = AccentColor,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            var rowDef = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", pairs.Length)));
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,12,*"),
                RowDefinitions = rowDef
            };

            for (int i = 0; i < pairs.Length; i++)
            {
                var keyBlock = new TextBlock
                {
                    Text = pairs[i].key,
                    Foreground = MutedColor,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                Grid.SetRow(keyBlock, i);
                Grid.SetColumn(keyBlock, 0);
                grid.Children.Add(keyBlock);

                var val = pairs[i].value;
                var valColor = val switch
                {
                    "always" => GreenColor,
                    "if-not-present" => AccentColor,
                    "never" => RedColor,
                    "No limit" => MutedColor,
                    "None" => MutedColor,
                    "(none)" => MutedColor,
                    // Security posture: warn when a guard is relaxed, reassure when it is enforced.
                    // Benign on/off preferences (auto-remove, timestamps) keep the default colour.
                    "Allowed" => RedColor,
                    "Bypassed" => YellowColor,
                    "Enabled" => YellowColor,
                    "Active" => GreenColor,
                    "Disabled" => GreenColor,
                    _ => FontColor
                };

                var valBlock = new TextBlock
                {
                    Text = val,
                    Foreground = valColor,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2)
                };
                Grid.SetRow(valBlock, i);
                Grid.SetColumn(valBlock, 2);
                grid.Children.Add(valBlock);
            }
            groupPanel.Children.Add(grid);

            _configContent.Children.Add(CreateSubCard(groupPanel));
        }

        var configureBtn = new Button
        {
            Content = "Configure Settings...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
            Padding = new Thickness(12, 8),
            CornerRadius = InnerCornerRadius,
            Background = SubCardBg,
            Foreground = AccentColor,
            BorderBrush = BorderColor,
            BorderThickness = HairlineThickness
        };
        configureBtn.Command = new AsyncRelayCommand(ShowSettingsDialogAsync);
        ToolTip.SetTip(configureBtn, "Opens Settings > Binary Management > Container Engine");
        AutomationProperties.SetName(configureBtn, "Configure container engine settings");
        _configContent.Children.Add(configureBtn);
    }

    /// <summary>
    /// Populates all daemon-dependent sections with offline messages.
    /// Called when the Docker daemon is unreachable.
    /// </summary>
    private void PopulateOfflineSections()
    {
        SetOfflineContent(_containersContent);
        SetOfflineContent(_imagesContent);
        _lastContainerFingerprint = 0;
        _lastImageFingerprint = 0;
    }

    /// <summary>
    /// Applies the themed fill/border for the status banner. Single source of truth shared by
    /// the temporary-status, deferred-reshow, and long-operation banner paths.
    /// Uses the host NotificationCard backgrounds so the banner matches OneWare toast styling.
    /// </summary>
    private void ApplyBannerStyle(bool isError)
    {
        _statusBanner.Background = isError ? ErrorBannerBg : InfoBannerBg;
        _statusBanner.BorderBrush = isError ? RedColor : AccentColor;
    }

    private void ShowTemporaryStatus(string message, bool isError = false, bool isTemporary = true)
    {
        var token = System.Threading.Interlocked.Increment(ref _bannerToken);
        Dispatcher.UIThread.Post(() =>
        {
            _statusBannerText.Text = message;
            ApplyBannerStyle(isError);
            _statusBanner.IsVisible = true;
            if (!isTemporary)
            {
                _lastPermanentMessage = message;
                _lastPermanentIsError = isError;
            }
        });

        if (isTemporary)
        {
            // Errors linger longer than info so they can actually be read before the next refresh tick
            // overwrites them. Dismissal is gated on TOKEN identity, not message text: a stale timer for a
            // superseded (or identical) message can no longer clear the banner early — the bug behind status
            // messages "disappearing almost instantly" after a few rapid clicks.
            var holdMs = isError ? 12000 : 6000;
            var weakSelf = new WeakReference<DockerDiagnosticsView>(this);
            _ = System.Threading.Tasks.Task.Delay(holdMs).ContinueWith(_ =>
            {
                if (weakSelf.TryGetTarget(out var self))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (System.Threading.Interlocked.Read(ref self._bannerToken) == token)
                        {
                            if (self._wasDockerOnline == false && self._lastPermanentMessage != null)
                            {
                                self._statusBannerText.Text = self._lastPermanentMessage;
                                self.ApplyBannerStyle(self._lastPermanentIsError);
                            }
                            else
                            {
                                self._statusBanner.IsVisible = false;
                            }
                        }
                    });
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    private void ShowTemporaryError(string titlePrefix, Exception ex, bool isTemporary = true)
    {
        ShowTemporaryStatus($"{titlePrefix}: {ex.Message}", isError: true, isTemporary: isTemporary);
    }

    private StackPanel BuildQuickActionsRow()
    {
        var actionsPanel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 4)
        };

        // Group 1: Toolchain Settings
        var settingsRow = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        settingsRow.Children.Add(CreateActionButton("All to Docker", async () =>
        {
            try
            {
                var toolService = _serviceProvider.Resolve<IToolService>();
                if (toolService == null) throw new InvalidOperationException("IToolService is not registered.");

                var allTools = toolService.GetAllTools();
                var strategyKey = _strategy.GetStrategyKey();
                int updatedCount = 0;

                foreach (var tool in allTools)
                {
                    if (_settingsService.HasSetting(tool.Key) &&
                        _settingsService.GetSetting(tool.Key) is ComboBoxSetting comboSetting &&
                        comboSetting.Options.Any(opt => opt is string str && string.Equals(str, strategyKey, StringComparison.Ordinal)))
                    {
                        _settingsService.SetSettingValue(tool.Key, strategyKey);
                        updatedCount++;
                    }
                }

                ShowTemporaryStatus($"{ContainerExtensionModule.DashboardTitle} — Configured {updatedCount} tools to Docker");
                _ = RefreshAllSafeAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_AllToDocker", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Switch the execution strategy of all supported FPGA tools to Docker"));

        settingsRow.Children.Add(CreateActionButton("All to Native", async () =>
        {
            try
            {
                var toolService = _serviceProvider.Resolve<IToolService>();
                if (toolService == null) throw new InvalidOperationException("IToolService is not registered.");

                var allTools = toolService.GetAllTools();
                var strategyKey = _strategy.GetStrategyKey();
                int updatedCount = 0;

                foreach (var tool in allTools)
                {
                    if (_settingsService.HasSetting(tool.Key) &&
                        _settingsService.GetSetting(tool.Key) is ComboBoxSetting comboSetting)
                    {
                        var defaultOption = comboSetting.Options.FirstOrDefault(opt => opt is string str && !string.Equals(str, strategyKey, StringComparison.Ordinal)) as string;
                        if (defaultOption != null)
                        {
                            _settingsService.SetSettingValue(tool.Key, defaultOption);
                            updatedCount++;
                        }
                    }
                }

                ShowTemporaryStatus($"{ContainerExtensionModule.DashboardTitle} — Reset {updatedCount} tools to Native");
                _ = RefreshAllSafeAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_AllToNative", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Reset the execution strategy of all tools to their default native execution"));

        settingsRow.Children.Add(CreateActionButton("Copy Docker Run", async () =>
        {
            try
            {
                // Prefer the exact command from the most recent real execution this session (verbatim and
                // runnable, with real env/paths); fall back to the generic template before anything has run.
                var cmd = _strategy.LastRawDockerRunCommand ?? _strategy.GenerateDockerRunCommand();
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(cmd).ConfigureAwait(false);
                    ShowTemporaryStatus("Copied equivalent 'docker run' command to clipboard.");
                }
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_CopyDockerRun", ex);
                ShowTemporaryError("Copy failed", ex);
            }
        }, "Copy an equivalent 'docker run' command to the clipboard for manual debugging"));

        var settingsCard = CreateCard("Quick Settings", settingsRow, defaultExpanded: false);
        actionsPanel.Children.Add(settingsCard);

        // Group 2: Image Operations
        var imageRow = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        imageRow.Children.Add(CreateActionButton("Pull Image", async () =>
        {
            try
            {
                var runtimePath = _strategy.GetRuntimePath();
                var settings = _strategy.GetActiveSettingsSummary();
                var img = settings.GetValueOrDefault("Image", ContainerExtensionModule.FallbackImage);
                if (IsBuildOnlyImage(img))
                {
                    ShowTemporaryStatus($"'{img}' is built locally, not pulled — use Build Local Image to produce or update it.", isError: false, isTemporary: false);
                    return;
                }
                if (!ContainerExtension.Validations.DockerImageFormatValidation.IsValidReference(img))
                {
                    ShowTemporaryStatus($"Refusing to pull '{img}': not a valid image reference.", isError: true, isTemporary: false);
                    return;
                }
                ShowTemporaryStatus($"Pulling default image '{img}' in terminal...");
                await _terminalService.ExecuteInTerminalAsync(TerminalLineReset + $"{runtimePath} pull \"{img}\"", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_PullImage", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Download or update the configured default toolchain image"));

        imageRow.Children.Add(CreateActionButton("Build Local Image", async () =>
        {
            try
            {
                var (selection, setAsDefault) = await ShowBuildDialogAsync().ConfigureAwait(true);
                if (selection == null)
                {
                    return;
                }

                var runtimePath = _strategy.GetRuntimePath();
#pragma warning disable IL3000
                var assemblyDir = Path.GetDirectoryName(typeof(ContainerExtensionModule).Assembly.Location);
#pragma warning restore IL3000
                if (string.IsNullOrEmpty(assemblyDir))
                {
                    throw new InvalidOperationException("Could not determine executing plugin directory.");
                }

                string? current = assemblyDir;
                string? dockerfilePath = null;
                for (int i = 0; i < 6; i++)
                {
                    if (string.IsNullOrEmpty(current)) break;
                    var candidate = Path.Combine(current, "docker", "oss-cad-suite", "Dockerfile");
                    if (File.Exists(candidate))
                    {
                        dockerfilePath = candidate;
                        break;
                    }
                    current = Path.GetDirectoryName(current);
                }

                if (dockerfilePath == null)
                {
                    throw new FileNotFoundException($"Local Dockerfile not found in plugin directory or parent workspace paths. (Checked from base '{assemblyDir}')");
                }

                var buildContextDir = Path.GetDirectoryName(dockerfilePath);
                if (string.IsNullOrEmpty(buildContextDir))
                {
                    throw new InvalidOperationException("Could not determine build context directory.");
                }
                var tag = "fentwums/oss-cad-suite:local";
                // Always build linux/amd64: the upstream arm64 releases omit GHDL, and the pinned
                // checksum is for the amd64 tarball. This matches docker/build_oss_cad_suite.sh.
                const string arch = "linux-x64";
                var extraArgs = $"--platform=linux/amd64 --build-arg ARCH={arch} ";

                if (selection != PinnedBuildSelection)
                {
                    // A specific dated release: pull its tarball and the GitHub-published checksum so
                    // the build stays integrity-verified instead of silently dropping the pin.
                    var releaseTag = selection;
                    ShowTemporaryStatus($"Fetching checksum for oss-cad-suite {releaseTag}...");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var sha256 = await ContainerExtension.Services.GitHubReleaseClient.GetAssetSha256Async(releaseTag, arch, cts.Token).ConfigureAwait(true);
                    if (string.IsNullOrEmpty(sha256))
                    {
                        throw new InvalidOperationException($"No published amd64 checksum found for oss-cad-suite {releaseTag}. The release may not include a linux-x64 asset.");
                    }

                    var dateStr = releaseTag.Replace("-", "", StringComparison.Ordinal);
                    // The digest is now charset-validated at the source (GitHubReleaseClient) and the tag is
                    // grammar-gated; quote the values as defense in depth so no build-arg can ever break out
                    // of its token in the terminal command.
                    extraArgs += $"--build-arg RELEASE_TAG=\"{releaseTag}\" --build-arg RELEASE_DATE=\"{dateStr}\" --build-arg OSS_CAD_SUITE_SHA256=\"{sha256}\" ";
                    ShowTemporaryStatus($"Building oss-cad-suite {releaseTag} (linux/amd64) in terminal...");
                }
                else
                {
                    ShowTemporaryStatus("Building the repository-pinned version (linux/amd64) in terminal...");
                }

                var commandLine = $"{runtimePath} build {extraArgs}-t {tag} -f \"{dockerfilePath}\" \"{buildContextDir}\"";
                var build = await _terminalService.ExecuteInTerminalAsync(TerminalLineReset + commandLine, ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(20)).ConfigureAwait(false);

                if (setAsDefault)
                {
                    // Only promote the freshly-built image to the default once the build actually succeeded —
                    // the terminal reports a real exit code, so a failed/timed-out build leaves the default intact.
                    if (!build.TimedOut && build.ExitCode == 0)
                    {
                        _settingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, tag);
                        ShowTemporaryStatus($"Built and set '{tag}' as the default toolchain image.", isError: false, isTemporary: false);
                        _ = RefreshAllSafeAsync();
                    }
                    else
                    {
                        var why = build.TimedOut ? "timed out" : $"exit code {build.ExitCode}";
                        ShowTemporaryStatus($"Build {why}; default image left unchanged.", isError: true);
                    }
                }
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_BuildLocalImage", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Build the local FPGA toolchain Docker image from source"));

        imageRow.Children.Add(CreateActionButton("Update All Images", async () =>
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _statusBannerText.Text = "Updating all local images...";
                    ApplyBannerStyle(isError: false);
                    _statusBanner.IsVisible = true;
                });

                var result = await _strategy.UpdateAllImagesAsync(
                    msg => Dispatcher.UIThread.Post(() =>
                    {
                        _statusBannerText.Text = $"Updating images: {msg}";
                    })
                ).ConfigureAwait(false);

                if (result.failed > 0)
                {
                    var names = result.failedImages.Count > 0 ? ": " + string.Join(", ", result.failedImages) : "";
                    ShowTemporaryStatus($"Updated {result.pulled} image(s), {result.failed} failed{names}", isError: true);
                }
                else
                {
                    ShowTemporaryStatus($"Successfully updated {result.pulled} image(s)");
                }
                _ = RefreshAllSafeAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_UpdateAllImages", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Re-pull all local images to their latest tags (cross-platform, no shell required)"));

        imageRow.Children.Add(CreateActionButton("Prune All Images", async () =>
        {
            try
            {
                var confirm = await ShowConfirmDialogAsync("Prune All Images", "Are you sure you want to prune ALL unused images? This will delete all images not currently used by a container, and they will need to be re-pulled.", "Prune");
                if (!confirm)
                {
                    return;
                }
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Pruning unused images in terminal...");
                await _terminalService.ExecuteInTerminalAsync(TerminalLineReset + $"{runtimePath} image prune -a -f", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_PruneAllImages", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Remove ALL unused images (not just dangling). This frees disk space but deleted images must be re-pulled."));

        var imageCard = CreateCard("Image Operations", imageRow, defaultExpanded: false);
        actionsPanel.Children.Add(imageCard);

        // Group 3: Engine Diagnostics & Cleanup
        var engineRow = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        engineRow.Children.Add(CreateActionButton("Hello-World Test", async () =>
        {
            try
            {
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Running Hello-World test in terminal...");
                await _terminalService.ExecuteInTerminalAsync(TerminalLineReset + $"{runtimePath} run --rm hello-world", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_HelloWorldTest", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Run a disposable hello-world container to verify Docker is working correctly"));

        engineRow.Children.Add(CreateActionButton("Engine Info", async () =>
        {
            try
            {
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Querying Engine Info in terminal...");
                await _terminalService.ExecuteInTerminalAsync(TerminalLineReset + $"{runtimePath} info", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_EngineInfo", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Show detailed Docker engine configuration, storage driver, and runtime info"));

        engineRow.Children.Add(CreateActionButton("Prune System", async () =>
        {
            try
            {
                var confirm = await ShowConfirmDialogAsync("Prune System", "Are you sure you want to prune the system? This will delete all stopped containers, dangling images, and unused networks. This action cannot be undone.", "Prune");
                if (!confirm)
                {
                    return;
                }
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Pruning system in terminal...");
                await _terminalService.ExecuteInTerminalAsync(TerminalLineReset + $"{runtimePath} system prune -f", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_PruneSystem", ex);
                ShowTemporaryError("Action failed", ex);
            }
        }, "Remove ALL stopped containers, dangling images, and unused networks. This cannot be undone."));

        var engineCard = CreateCard("Engine & Cleanup", engineRow, defaultExpanded: false);
        actionsPanel.Children.Add(engineCard);

        return actionsPanel;
    }

    /// <summary>Updates the header title to include the container count badge.</summary>
    private void UpdateHeaderBadge(int containerCount)
    {
        var badgeText = containerCount > 99 ? "99+" : containerCount.ToString();
        _headerTitle.Text = containerCount > 0
          ? $"{ContainerExtensionModule.DashboardTitle} ({badgeText})"
          : ContainerExtensionModule.DashboardTitle;

        if (DataContext is ViewModels.DockerDiagnosticsViewModel vm)
        {
            vm.Title = containerCount > 0
              ? $"{ContainerExtensionModule.DashboardTitle} ({badgeText})"
              : ContainerExtensionModule.DashboardTitle;
        }
    }

    /// <summary>
    /// Re-populates all data sections using the current <see cref="_searchFilter"/>.
    /// Called when the global search TextBox content changes.
    /// </summary>
    private void ApplySearchFilter()
    {
        try
        {
            IList<Docker.DotNet.Models.ContainerListResponse> localContainers;
            IList<Docker.DotNet.Models.ImagesListResponse> localImages;
            (int imageCount, long totalSizeBytes, long reclaimableBytes) localDiskUsage;

            lock (_cachedDataLock)
            {
                localContainers = _cachedContainers;
                localImages = _cachedImages;
                localDiskUsage = _cachedDiskUsage;
            }

            PopulateContainers(localContainers);
            PopulateImages(localImages, localDiskUsage);
            _ = PopulateTelemetryAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView", "ApplySearchFilter failed", ex);
        }
    }

    private static void SafeCancelAndDisposeCts(ref CancellationTokenSource? cts)
    {
        var target = Interlocked.Exchange(ref cts, null);
        if (target != null)
        {
            try
            {
                target.Cancel();
            }
            catch (Exception)
            {
                // Ignored: Cancellation of old token source is best-effort.
            }
            try
            {
                target.Dispose();
            }
            catch (Exception)
            {
                // Ignored: Disposal of old token source is best-effort.
            }
        }
    }
    /// <summary>
    /// Builds the shared dialog header (bold title + short accent underline) used by every modal
    /// dialog, so the three dialogs do not each re-implement identical chrome.
    /// </summary>
    private static StackPanel CreateDialogHeader(string title, IBrush accent)
    {
        var headerPanel = new StackPanel { Spacing = 6 };
        headerPanel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = accent
        });
        headerPanel.Children.Add(new Border
        {
            Height = 2,
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 60,
            Margin = new Thickness(0, 2, 0, 8)
        });
        return headerPanel;
    }

    /// <summary>
    /// Shows a modal dialog centered on the dashboard's owning window, falling back to a non-modal
    /// Show() when no owner can be resolved. Centralizes the owner-resolution logic the three
    /// dialogs previously duplicated.
    /// </summary>
    private async Task ShowDialogWithOwnerAsync(Window dialog)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    private async Task<bool> ShowConfirmDialogAsync(string title, string message, string confirmLabel = "Confirm")
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var mainPanel = new StackPanel { Spacing = 16 };

        mainPanel.Children.Add(CreateDialogHeader(title, RedColor));

        var msgText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 18,
            Opacity = 0.9
        };
        mainPanel.Children.Add(msgText);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var yesBtn = new Button
        {
            Content = confirmLabel,
            FontWeight = FontWeight.SemiBold,
            Background = RedColor,
            Foreground = OnAccentColor,
            Padding = new Thickness(16, 8),
            CornerRadius = InnerCornerRadius
        };
        var noBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8),
            CornerRadius = InnerCornerRadius,
            // Cancel is the safe default on a destructive prompt: Escape dismisses, Enter does NOT confirm.
            IsCancel = true
        };

        yesBtn.Command = new RelayCommand(() =>
        {
            tcs.TrySetResult(true);
            dialog.Close();
        });
        noBtn.Command = new RelayCommand(() =>
        {
            tcs.TrySetResult(false);
            dialog.Close();
        });

        buttonPanel.Children.Add(noBtn); // Cancel on the left
        buttonPanel.Children.Add(yesBtn); // Dangerous action on the right
        mainPanel.Children.Add(buttonPanel);

        var wrapper = new Border
        {
            Padding = new Thickness(24),
            Child = mainPanel
        };

        dialog.Content = wrapper;
        dialog.Closed += (s, e) => { tcs.TrySetResult(false); };

        await ShowDialogWithOwnerAsync(dialog);

        return await tcs.Task;
    }

    // Sentinel returned by the build dialog for the repository-pinned, checksum-verified build.
    private const string PinnedBuildSelection = "pinned";
    private const string PinnedBuildLabel = "Pinned (recommended)";

    // Reads the repository-pinned oss-cad-suite release tag (ARG RELEASE_TAG=) from the bundled Dockerfile so
    // the Build dialog can show the concrete version (e.g. "Pinned 2026-06-30") instead of a bare "Pinned".
    // Returns false if the Dockerfile cannot be located or parsed; the dialog then uses the generic label.
    // The version is NOT duplicated into C# — the Dockerfile remains the single source of truth.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "The plugin is loaded by OneWare as a loose assembly file from the Packages directory, not embedded in a single-file bundle, so Assembly.Location returns a valid path.")]
    private static bool TryReadPinnedReleaseTag(out string? tag)
    {
        tag = null;
        try
        {
            var current = Path.GetDirectoryName(typeof(DockerDiagnosticsView).Assembly.Location);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(current); i++)
            {
                var candidate = Path.Combine(current, "docker", "oss-cad-suite", "Dockerfile");
                if (File.Exists(candidate))
                {
                    foreach (var line in File.ReadLines(candidate))
                    {
                        var trimmed = line.TrimStart();
                        if (trimmed.StartsWith("ARG RELEASE_TAG=", StringComparison.Ordinal))
                        {
                            tag = trimmed["ARG RELEASE_TAG=".Length..].Trim();
                            return !string.IsNullOrEmpty(tag);
                        }
                    }
                    return false;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch { /* fall back to the generic label */ }
        return false;
    }

    /// <summary>
    /// Prompts for the oss-cad-suite version to build locally. Returns <see cref="PinnedBuildSelection"/>
    /// for the repository-pinned build, a YYYY-MM-DD release tag for a specific GitHub release, or null
    /// if cancelled. The version list is fetched from GitHub up front; if that fails, only the pinned
    /// build is offered.
    /// </summary>
    private async Task<(string? selection, bool setAsDefault)> ShowBuildDialogAsync()
    {
        IReadOnlyList<string> versions = Array.Empty<string>();
        string? fetchNote = null;
        try
        {
            ShowTemporaryStatus("Querying available oss-cad-suite versions from GitHub...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Show the 100 most recent valid releases (GitHub single-page max; oss-cad-suite publishes
            // ~daily, so this spans ~3 months rather than the ~19 days a count of 20 covered). Not a date policy.
            versions = await ContainerExtension.Services.GitHubReleaseClient.GetRecentReleaseTagsAsync(100, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            fetchNote = "Could not reach GitHub; only the repository-pinned build is available.";
            ContainerTelemetry.TrackError("DockerDiagnosticsView", "ShowBuildDialog_FetchVersions", ex);
        }

        var tcs = new TaskCompletionSource<(string? selection, bool setAsDefault)>();
        var dialog = new Window
        {
            Title = "Build Local Image",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var mainPanel = new StackPanel { Spacing = 16 };
        mainPanel.Children.Add(CreateDialogHeader("Build Local FPGA Toolchain Image", AccentColor));
        mainPanel.Children.Add(new TextBlock
        {
            Text = "Select the oss-cad-suite version to compile locally from source:",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold
        });

        var pinnedLabel = TryReadPinnedReleaseTag(out var pinnedTag) && pinnedTag != null
            ? $"Pinned {pinnedTag} (recommended)"
            : PinnedBuildLabel;
        var versionCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        versionCombo.Items.Add(pinnedLabel);
        foreach (var v in versions)
        {
            versionCombo.Items.Add(v);
        }
        versionCombo.SelectedIndex = 0;
        AutomationProperties.SetName(versionCombo, "oss-cad-suite version to build");
        mainPanel.Children.Add(versionCombo);

        mainPanel.Children.Add(new TextBlock
        {
            Text = "Pinned builds the version verified in the repository. Choosing a dated release fetches that "
                 + "tarball and its GitHub-published checksum so the build stays integrity-checked. Builds run as "
                 + "linux/amd64 because GHDL is not compiled in the upstream arm64 releases.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 18,
            Opacity = 0.75
        });

        if (fetchNote != null)
        {
            mainPanel.Children.Add(new TextBlock
            {
                Text = fetchNote,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Foreground = RedColor
            });
        }

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8),
            CornerRadius = InnerCornerRadius,
            IsCancel = true
        };
        var buildBtn = new Button
        {
            Content = "Build",
            FontWeight = FontWeight.SemiBold,
            Background = AccentColor,
            Foreground = OnAccentColor,
            Padding = new Thickness(16, 8),
            CornerRadius = InnerCornerRadius,
            // Enter confirms the primary build action; safe here (non-destructive).
            IsDefault = true
        };
        string ResolveSelection()
        {
            var selected = versionCombo.SelectedItem as string;
            return string.IsNullOrEmpty(selected) || selected == PinnedBuildLabel || selected.StartsWith("Pinned ", StringComparison.Ordinal) ? PinnedBuildSelection : selected;
        }
        cancelBtn.Command = new RelayCommand(() => { tcs.TrySetResult((null, false)); dialog.Close(); });
        buildBtn.Command = new RelayCommand(() =>
        {
            tcs.TrySetResult((ResolveSelection(), false));
            dialog.Close();
        });

        var buildSetDefaultBtn = new Button
        {
            Content = "Build & Set Default",
            FontWeight = FontWeight.SemiBold,
            Background = AccentColor,
            Foreground = OnAccentColor,
            Padding = new Thickness(16, 8),
            CornerRadius = InnerCornerRadius
        };
        ToolTip.SetTip(buildSetDefaultBtn, "Build the image, then set it as the default toolchain image once the build succeeds.");
        buildSetDefaultBtn.Command = new RelayCommand(() =>
        {
            tcs.TrySetResult((ResolveSelection(), true));
            dialog.Close();
        });

        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(buildBtn);
        buttonPanel.Children.Add(buildSetDefaultBtn);
        mainPanel.Children.Add(buttonPanel);

        dialog.Content = new Border { Padding = new Thickness(24), Child = mainPanel };
        dialog.Closed += (s, e) => { tcs.TrySetResult((null, false)); };

        await ShowDialogWithOwnerAsync(dialog);
        return await tcs.Task;
    }

    private static readonly string CachedPluginVersion = GetPluginVersionString();

    private static string GetPluginVersionString()
    {
        try
        {
            var ver = typeof(DockerDiagnosticsView).Assembly.GetName().Version;
            return ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
        }
        catch { return ""; }
    }

    private Border CreateMetricCard(string label, string initialVal, string initialDetail, IBrush initialAccent,
        out TextBlock valText, out TextBlock detailText, out Border accentBar)
    {
        var mainPanel = new StackPanel { Spacing = 4 };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = MutedColor
        };

        valText = new TextBlock
        {
            Text = initialVal,
            FontFamily = MonoFont, // tabular monospace for the most-glanced numeric KPIs
            FontSize = MetricFontSize,
            FontWeight = FontWeight.Bold,
            Foreground = FontColor
        };

        detailText = new TextBlock
        {
            Text = initialDetail,
            FontSize = 10,
            Foreground = MutedColor
        };

        mainPanel.Children.Add(labelText);
        mainPanel.Children.Add(valText);
        mainPanel.Children.Add(detailText);

        accentBar = new Border
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = initialAccent,
            CornerRadius = new CornerRadius(2, 0, 0, 2)
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(mainPanel, 1);

        mainPanel.Margin = new Thickness(14, 10, 10, 10);

        grid.Children.Add(accentBar);
        grid.Children.Add(mainPanel);

        var card = new Border
        {
            Background = CardBg,
            CornerRadius = CardCornerRadius,
            BorderThickness = HairlineThickness,
            BorderBrush = BorderColor,
            Child = grid
        };
        AutomationProperties.SetName(card, label);
        AutomationProperties.SetLiveSetting(valText, AutomationLiveSetting.Polite);
        return card;
    }
}
