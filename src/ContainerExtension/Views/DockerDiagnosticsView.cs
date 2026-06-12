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

    // Cached brushes and geometries to avoid allocations during rebuild loops (F14)
    private static readonly Geometry WhaleGeometry = Geometry.Parse(ContainerExtensionModule.WhaleIconPath);
    private static readonly SolidColorBrush DockerBlueBrush = new(Color.Parse(ContainerExtensionModule.DockerBlueHex));
    private static readonly SolidColorBrush LightGreenBrush = new(Colors.LightGreen);

    // -- Instance State --------------------------------------------------
    private readonly DockerExecutionStrategy _strategy;
    private readonly ITerminalManagerService _terminalService;
    private readonly StackPanel _statusContent;
    private readonly StackPanel _configContent;
    private readonly StackPanel _containersContent;
    private readonly StackPanel _imagesContent;
    private readonly StackPanel _telemetryContent;
    private readonly StackPanel _toolchainContent;
    private readonly TextBlock _headerTitle;
    private string _pluginVersion;
    private readonly WrapPanel _quickActionsRow;
    private readonly ISettingsService _settingsService;
    private readonly TextBox _searchBox;
    private readonly IServiceProvider _serviceProvider;
    private string? _temporaryStatus;
    private readonly Border _statusBanner;
    private readonly TextBlock _statusBannerText;

    // -- KPI Metrics Controls ---------------------------------------------
    private TextBlock? _metricDaemonStatusText;
    private TextBlock? _metricDaemonDetailText;
    private Border? _metricDaemonBorder;
    private TextBlock? _metricContainersText;
    private TextBlock? _metricContainersDetailText;
    private TextBlock? _metricImagesText;
    private TextBlock? _metricImagesDetailText;
    private TextBlock? _metricDiskText;
    private TextBlock? _metricDiskDetailText;    // Tracks open container log windows to prevent duplicate spawning
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Window> _openLogWindows = new(StringComparer.Ordinal);

    // Auto-refresh state
    private CancellationTokenSource? _refreshCts;
    private DispatcherTimer? _autoRefreshTimer;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly Border _refreshIndicator;
    private readonly TextBlock _lastRefreshedText;
    private readonly TextBlock _countdownText;
    private int _refreshIntervalSeconds;
    private int _secondsUntilRefresh;
    private bool _hasAttached; // Guard against duplicate AttachedToVisualTree handlers (F15)
    private bool _justAttached;
    private IDisposable? _isVisibleSubscription;

    // -- Cached Data (for re-sorting without re-querying the daemon) --
    private readonly System.Threading.Lock _cachedDataLock = new();
    private readonly List<Grid> _recycledContainerRows = new();
    private readonly List<Grid> _recycledImageRows = new();
    private IList<Docker.DotNet.Models.ContainerListResponse> _cachedContainers = Array.Empty<Docker.DotNet.Models.ContainerListResponse>();
    private bool _showAllContainers;
    private IList<Docker.DotNet.Models.ImagesListResponse> _cachedImages = Array.Empty<Docker.DotNet.Models.ImagesListResponse>();
    private bool _showAllImages;
    private (int imageCount, long totalSizeBytes, long reclaimableBytes) _cachedDiskUsage;

    // -- Search/Filter State ------------------------------------------
    private string _searchFilter = "";

    // -- Sort State (column name + direction per table) ---------------
    private (string column, bool ascending) _containerSort = ("name", true);
    private (string column, bool ascending) _imageSort = ("repo", true);
    private (string column, bool ascending) _historySort = ("time", false); // newest-first default

    // -- Data Fingerprints (skip UI rebuild when data is unchanged) ----
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

        // Register to ActualThemeVariantChanged to dynamically repaint backgrounds when dark mode is toggled (F12)
        ActualThemeVariantChanged += (sender, args) => UpdateThemeColors();

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

        // -- Header ------------------------------------------------------
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
            FontSize = 20,
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

        // -- Global Search / Filter --------------------------------------
        _searchBox = new TextBox
        {
            Watermark = "🔍  Filter containers, images, history... (Ctrl+F)",
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TabIndex = 1
        };
        AutomationProperties.SetName(_searchBox, "Filter containers, images, history text search box");

        var clearBtn = new Button
        {
            Content = "×",
            Margin = new Thickness(2),
            Background = null,
            BorderBrush = null,
            IsVisible = false,
            Command = new RelayCommand(() => { _searchBox.Text = ""; })
        };
        _searchBox.InnerRightContent = clearBtn;

        // Use cached listener method instead of lambda to prevent delegate instantiation allocations (F16)
        _searchBox.TextChanged += OnSearchBoxTextChanged;

        // Implement drag-and-drop validation for setting and config file paths (F15)
        DragDrop.SetAllowDrop(_searchBox, true);
        _searchBox.AddHandler(DragDrop.DragOverEvent, OnSearchBoxDragOver);
        _searchBox.AddHandler(DragDrop.DropEvent, OnSearchBoxDrop);

        _refreshIndicator = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = LightGreenBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Opacity = 0.0
        };

        _lastRefreshedText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = MutedColor,
            VerticalAlignment = VerticalAlignment.Center
        };
        _countdownText = new TextBlock
        {
            Text = "",
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
        ToolTip.SetTip(refreshBtn, "Re-query the Docker daemon for live container, image, and system data (F5)");
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

        // -- Quick Actions (inline below header) -------------------------
        _quickActionsRow = BuildQuickActionsRow();
        _quickActionsRow.Opacity = 0.5;  // dimmed until daemon is confirmed reachable
        _quickActionsRow.IsEnabled = false;

        // -- KPI Row -----------------------------------------------------
        var kpiGrid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 8),
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*")
        };

        var daemonCard = CreateMetricCard("DAEMON STATUS", "Offline", "Not Connected", RedColor, out _metricDaemonStatusText, out _metricDaemonDetailText, out _metricDaemonBorder);
        var containersCard = CreateMetricCard("CONTAINERS", "0 Running", "0 total", AccentColor, out _metricContainersText, out _metricContainersDetailText, out _);
        var imagesCard = CreateMetricCard("IMAGES", "0 Images", "0 B total", AccentColor, out _metricImagesText, out _metricImagesDetailText, out _);
        var diskCard = CreateMetricCard("RECLAIMABLE SPACE", "0 B", "No dangling items", AccentColor, out _metricDiskText, out _metricDiskDetailText, out _);

        Grid.SetColumn(daemonCard, 0);
        Grid.SetColumn(containersCard, 1);
        Grid.SetColumn(imagesCard, 2);
        Grid.SetColumn(diskCard, 3);

        kpiGrid.Children.Add(daemonCard);
        kpiGrid.Children.Add(containersCard);
        kpiGrid.Children.Add(imagesCard);
        kpiGrid.Children.Add(diskCard);

        // -- Section 1: Connection Status --------------------------------
        _statusContent = new StackPanel { Spacing = 4 };
        _statusContent.Children.Add(CreateLoadingText("Connecting to daemon..."));
        var statusSection = CreateCard("Connection Status", _statusContent);

        // -- Section 2: Containers ---------------------------------------
        _containersContent = new StackPanel { Spacing = 2 };
        _containersContent.Children.Add(CreateLoadingText("Loading containers..."));
        var containersSection = CreateCard("Containers", _containersContent);

        // -- Section 3: Images & Disk Usage ------------------------------
        _imagesContent = new StackPanel { Spacing = 2 };
        _imagesContent.Children.Add(CreateLoadingText("Loading images..."));
        var imagesSection = CreateCard("Images & Disk Usage", _imagesContent);

        // -- Section 4: Active Configuration -----------------------------
        _configContent = new StackPanel { Spacing = 2 };
        _configContent.Children.Add(CreateLoadingText("Reading settings..."));
        var configSection = CreateCard("Active Configuration", _configContent);

        // -- Section 5: Recent Executions --------------------------------
        _telemetryContent = new StackPanel { Spacing = 2 };
        var telemetrySection = CreateCard("Execution History", _telemetryContent);

        // -- Section 6: Toolchain Environment ----------------------------
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

        var closeBannerBtn = new Button
        {
            Content = "×",
            Padding = new Thickness(6, 2),
            Background = null,
            BorderBrush = null,
            VerticalAlignment = VerticalAlignment.Center
        };

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
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            IsVisible = false,
            Child = bannerGrid,
            Margin = new Thickness(0, 4, 0, 4)
        };

        closeBannerBtn.Command = new RelayCommand(() => _statusBanner.IsVisible = false);

        // -- Layout Grid Columns -----------------------------------------
        var columnsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,16,*"),
            Margin = new Thickness(0, 8, 0, 0)
        };

        var leftColumnPanel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                containersSection,
                imagesSection,
                telemetrySection
            }
        };

        var rightColumnPanel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                statusSection,
                toolchainSection,
                configSection
            }
        };

        Grid.SetColumn(leftColumnPanel, 0);
        Grid.SetColumn(rightColumnPanel, 2);
        columnsGrid.Children.Add(leftColumnPanel);
        columnsGrid.Children.Add(rightColumnPanel);

        // -- Layout ------------------------------------------------------
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
                columnsGrid
            }
        };

        // Enable cycling tab focus within the dashboard layout boundary (F13)
        KeyboardNavigation.SetTabNavigation(mainPanel, KeyboardNavigationMode.Cycle);

        Content = new ScrollViewer { Content = mainPanel };

        // Resolve settings service for auto-refresh interval
        _settingsService = serviceProvider.Resolve<ISettingsService>();

        // Fire-and-forget: load all live data after the control renders + start auto-refresh.
        var attachCmd = new AsyncRelayCommand(async () =>
        {
            if (_hasAttached)
            {
                return; // Prevent duplicate handlers on dock/undock cycles (F15)
            }
            _hasAttached = true;
            _justAttached = true;
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
            _isVisibleSubscription = this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (visible)
                {
                    try
                    {
                        Dispatcher.UIThread.Post(() => { _searchBox.Focus(); });
                        if (_justAttached)
                        {
                            _justAttached = false;
                        }
                        else
                        {
                            _ = RefreshAllAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        ContainerTelemetry.TrackError("DockerDiagnosticsView", "AutoRefresh_IsVisible", ex);
                    }
                    StartAutoRefreshTimer();
                }
                else
                {
                    StopAutoRefreshTimer();
                }
            });
            StartAutoRefreshTimer();
        });
        AttachedToVisualTree += (_, _) => { attachCmd.Execute(null); };
        DetachedFromVisualTree += (_, _) =>
        {
            StopAutoRefreshTimer();
            _searchDebounceTimer.Stop();
            _hasAttached = false; // Allow re-attach to refresh again (F15)

            _isVisibleSubscription?.Dispose();
            _isVisibleSubscription = null;

            // Close any orphan container log windows spawned from this dashboard
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

        _ = RefreshAllAsync();
    }

    // -- Drag & Drop Handlers for Search Box (F15) -----------------------
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

    // -- TextChanged Event Handler (F16) ---------------------------------
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


    /// <summary>Updates the "Last refreshed" timestamp display in the header status bar.</summary>
    private void UpdateLastRefreshedTimestamp()
    {
        _lastRefreshedText.Text = $"Last refreshed: {DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture)}";

        int tickCount = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (s, e) =>
        {
            tickCount++;
            if (tickCount > 6)
            {
                _refreshIndicator.Opacity = 0.0;
                timer.Stop();
            }
            else
            {
                _refreshIndicator.Opacity = tickCount % 2 == 1 ? 1.0 : 0.2;
            }
        };
        timer.Start();
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

    // =======================================================================
    //  Live Data Refresh
    // =======================================================================

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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _refreshCts?.Token ?? CancellationToken.None,
                timeoutCts.Token);
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
                    throw new DockerExecutionException("Docker/OrbStack daemon is unreachable.");
                }

                // Compute disk usage from the already-fetched image list (avoids duplicate API call)
                var diskUsage = DockerExecutionStrategy.ComputeDiskUsage(images);

                // Update UI on the Avalonia dispatcher thread
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_hasAttached)
                    {
                        return;
                    }
                    _headerTitle.Text = $"{ContainerExtensionModule.DashboardTitle}";
                    ToolTip.SetTip(_headerTitle, null);
                    PopulateStatus(true, info);

                    // Update KPI Metrics (Online state)
                    if (_metricDaemonStatusText != null) _metricDaemonStatusText.Text = "Online";
                    if (_metricDaemonDetailText != null) _metricDaemonDetailText.Text = $"{info.Name ?? "Connected"} ({_strategy.DetectedRuntime})";
                    if (_metricDaemonBorder != null) _metricDaemonBorder.Background = GreenColor;

                    var running = containers.Count(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase));
                    if (_metricContainersText != null) _metricContainersText.Text = $"{running} Running";
                    if (_metricContainersDetailText != null) _metricContainersDetailText.Text = $"{containers.Count} total containers";

                    if (_metricImagesText != null) _metricImagesText.Text = $"{images.Count} Images";
                    if (_metricImagesDetailText != null) _metricImagesDetailText.Text = $"{FormatBytesBinary(diskUsage.totalSizeBytes)} total size";

                    if (_metricDiskText != null) _metricDiskText.Text = FormatBytesBinary(diskUsage.reclaimableBytes);
                    var unusedCount = images.Count(i => i.Containers == 0);
                    if (_metricDiskDetailText != null) _metricDiskDetailText.Text = $"{unusedCount} unused images";

                    if (_wasDockerOnline == false)
                    {
                        ShowTemporaryStatus("Docker Daemon is back online!");
                    }
                    else if (_wasDockerOnline == null)
                    {
                        _statusBanner.IsVisible = false;
                    }
                    _wasDockerOnline = true;

                    // Skip-if-unchanged: compare a lightweight fingerprint of container/image data
                    // to avoid full UI tree rebuild when nothing has changed (critical at 2s/5s refresh rates).
                    var containerFp = containers.Count;
                    foreach (var c in containers.Take(5))
                    {
                        containerFp = HashCode.Combine(containerFp, c.ID, c.State);
                    }
                    var imageFp = HashCode.Combine(images.Count, images.Sum(i => i.Size));

                    if (containerFp != _lastContainerFingerprint)
                    {
                        _lastContainerFingerprint = containerFp;
                        PopulateContainers(containers);
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
                });
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "RefreshAllAsync_ParallelQuery", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!_hasAttached)
                    {
                        return;
                    }
                    _headerTitle.Text = $"{ContainerExtensionModule.DashboardTitle} ⚠️ Offline / API Error";
                    ToolTip.SetTip(_headerTitle, ex.Message);
                    _quickActionsRow.IsEnabled = false;
                    _quickActionsRow.Opacity = 0.5;
                    PopulateStatus(false, null);

                    // Update KPI Metrics (Offline state)
                    if (_metricDaemonStatusText != null) _metricDaemonStatusText.Text = "Offline";
                    if (_metricDaemonDetailText != null) _metricDaemonDetailText.Text = "Daemon unreachable";
                    if (_metricDaemonBorder != null) _metricDaemonBorder.Background = RedColor;

                    if (_metricContainersText != null) _metricContainersText.Text = "—";
                    if (_metricContainersDetailText != null) _metricContainersDetailText.Text = "No active daemon";

                    if (_metricImagesText != null) _metricImagesText.Text = "—";
                    if (_metricImagesDetailText != null) _metricImagesDetailText.Text = "No active daemon";

                    if (_metricDiskText != null) _metricDiskText.Text = "—";
                    if (_metricDiskDetailText != null) _metricDiskDetailText.Text = "No active daemon";

                    PopulateConfig(settings);
                    PopulateOfflineSections(); // Clear stale lists and show offline sections
                    _ = PopulateTelemetryAsync();
                    UpdateHeaderBadge(0);
                    UpdateLastRefreshedTimestamp();

                    if (_wasDockerOnline == null || _wasDockerOnline == true)
                    {
                        ShowTemporaryError("Docker Daemon is offline", ex, isTemporary: false);
                    }
                    _wasDockerOnline = false;
                });
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isRefreshingFlag, 0);
        }
    }

    // =======================================================================
    //  Section Population
    // =======================================================================

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
        try
        {
            tags = await FEntwumS.ContainerExtension.Registry.RegistryClient.FetchTagsAsync(currentImage, _refreshCts?.Token ?? default).ConfigureAwait(false);
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

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

            row.Children.Add(new TextBlock
            {
                Text = "Active Image:",
                Foreground = FontColor,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (tags.Count > 0)
            {
                var comboBox = new ComboBox
                {
                    Width = 250,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12
                };
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
                            _ = RefreshAllAsync(); // refresh configuration display
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView", "ChangeActiveImageSettings", ex);
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
                    VerticalAlignment = VerticalAlignment.Center
                });

                row.Children.Add(new TextBlock
                {
                    Text = "(Tags unavailable)",
                    Foreground = MutedColor,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            Button btn = null!;
            btn = new Button
            {
                Content = "Check for Updates & Pull",
                Padding = new Thickness(8, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.Command = new AsyncRelayCommand(async () =>
        {
            var activeImg = tags.Count > 0 && row.Children[1] is ComboBox cb && cb.SelectedItem is string sel ? sel : currentImage;
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
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} pull \"{activeImg}\"", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

                // Prune dangling images to free disk space
                _ = _strategy.PruneDanglingImagesAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "UpdateAndPullImage", ex);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    btn.Content = "Error ✗";
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

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        statusRow.Children.Add(statusDot);
        statusRow.Children.Add(new TextBlock
        {
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
                    await Task.Run(() => { LaunchDesktopApp(_strategy.DetectedRuntime); }).ConfigureAwait(true);
                    await Task.Delay(1000).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("DockerDiagnosticsView", "OpenDesktopBtn_Click", ex);
                }
                finally
                {
                    openDesktopBtn.Content = "Open Desktop";
                    openDesktopBtn.IsEnabled = true;
                }
            });
            ToolTip.SetTip(openDesktopBtn, $"Launch {desktopAppName}");
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
            _statusContent.Children.Add(new TextBlock
            {
                Text = "Start Docker Desktop or the Docker daemon to enable container execution.",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(18, 4, 0, 0)
            });
        }
    }

    /// <summary>Populates the Active Configuration section with card-grouped settings layout.</summary>
    private void PopulateConfig(Dictionary<string, string> settings)
    {
        _configContent.Children.Clear();

        // -- Extension Metadata Card --------------------------------------
        var telemetryPath = ContainerTelemetry.TelemetryFilePath;
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                telemetryPath = telemetryPath.Replace(home, "~", StringComparison.Ordinal);
            }
        }
        catch (Exception ex)
        {
            _ = ex;
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
        AddInfoRow(metaGrid, 0, "Extension", $"Container Extension {_pluginVersion}");
        AddInfoRow(metaGrid, 1, "Runtime", $"{_strategy.DetectedRuntime} | .NET {Environment.Version}");
        AddInfoRow(metaGrid, 2, "Telemetry", telemetryPath);
        metaPanel.Children.Add(metaGrid);

        var metaCard = new Border
        {
            Background = Brush.Parse("#0DFFFFFF"), // Sub card card background
            BorderBrush = Brush.Parse("#2D2D30"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = metaPanel
        };
        _configContent.Children.Add(metaCard);

        // -- Grouped Settings --------------------------------------------
        var groups = new (string title, string[] keys)[]
        {
            ("IMAGE & EXECUTION", [ ContainerExtensionModule.SettingsKeyImage, ContainerExtensionModule.SettingsKeyPullPolicy, ContainerExtensionModule.SettingsKeyPlatform, ContainerExtensionModule.SettingsKeyNetwork ]),
            ("RESOURCE LIMITS",  [ ContainerExtensionModule.SettingsKeyMemory, ContainerExtensionModule.SettingsKeyCpu, ContainerExtensionModule.SettingsKeyTimeout ]),
            ("CONTAINER INFO",   [ ContainerExtensionModule.SettingsKeyAutoRemove, ContainerExtensionModule.SettingsKeyNamePrefix, ContainerExtensionModule.SettingsKeyExtraLabels ]),
            ("LOGGING CONFIG",   [ ContainerExtensionModule.SettingsKeyLogLevel, ContainerExtensionModule.SettingsKeyTimestamps ]),
            ("DASHBOARD DATA",   [ ContainerExtensionModule.SettingsKeyDashboardRefresh, ContainerExtensionModule.SettingsKeyRetention ]),
            ("ADVANCED PATHS",   [ ContainerExtensionModule.SettingsKeyRuntimePath ]),
        };

        foreach (var (title, keys) in groups)
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
                    "On" => GreenColor,
                    "Off" => RedColor,
                    "always" => GreenColor,
                    "if-not-present" => AccentColor,
                    "never" => RedColor,
                    "No limit" => MutedColor,
                    "None" => MutedColor,
                    "(none)" => MutedColor,
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

            var groupCard = new Border
            {
                Background = Brush.Parse("#0DFFFFFF"),
                BorderBrush = Brush.Parse("#2D2D30"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = groupPanel
            };

            _configContent.Children.Add(groupCard);
        }

        var configureBtn = new Button
        {
            Content = "⚙️ Configure Settings...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(4),
            Background = Brush.Parse("#1A2496ED"),
            Foreground = AccentColor,
            BorderBrush = Brush.Parse("#2D2D30"),
            BorderThickness = new Thickness(1)
        };
        configureBtn.Command = new AsyncRelayCommand(ShowSettingsDialogAsync);
        _configContent.Children.Add(configureBtn);

        _configContent.Children.Add(new TextBlock
        {
            Text = "Configure: Settings > Binary Management > Container Engine",
            Foreground = MutedColor,
            FontSize = 10,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(4, 4, 0, 0)
        });
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

    private void ShowTemporaryStatus(string message, bool isError = false, bool isTemporary = true)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _statusBannerText.Text = message;
            _statusBanner.Background = isError
                ? new SolidColorBrush(Color.FromArgb(30, 244, 67, 54))  // Subtle red for error
                : new SolidColorBrush(Color.FromArgb(30, 36, 150, 237)); // Subtle blue for info
            _statusBanner.BorderBrush = isError
                ? new SolidColorBrush(Color.FromArgb(80, 244, 67, 54))
                : new SolidColorBrush(Color.FromArgb(80, 36, 150, 237));
            _statusBanner.IsVisible = true;
            _temporaryStatus = isTemporary ? message : null;
            if (!isTemporary)
            {
                _lastPermanentMessage = message;
                _lastPermanentIsError = isError;
            }
        });

        if (isTemporary)
        {
            var weakSelf = new WeakReference<DockerDiagnosticsView>(this);
            _ = System.Threading.Tasks.Task.Delay(6000).ContinueWith(_ =>
            {
                if (weakSelf.TryGetTarget(out var self))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (self._temporaryStatus == message)
                        {
                            self._temporaryStatus = null;
                            if (self._wasDockerOnline == false && self._lastPermanentMessage != null)
                            {
                                self._statusBannerText.Text = self._lastPermanentMessage;
                                self._statusBanner.Background = self._lastPermanentIsError
                                    ? new SolidColorBrush(Color.FromArgb(30, 244, 67, 54))
                                    : new SolidColorBrush(Color.FromArgb(30, 36, 150, 237));
                                self._statusBanner.BorderBrush = self._lastPermanentIsError
                                    ? new SolidColorBrush(Color.FromArgb(80, 244, 67, 54))
                                    : new SolidColorBrush(Color.FromArgb(80, 36, 150, 237));
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

    private WrapPanel BuildQuickActionsRow()
    {
        var actionsRow = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 4)
        };

        actionsRow.Children.Add(CreateActionButton("Pull Image", async () =>
        {
            try
            {
                var runtimePath = _strategy.GetRuntimePath();
                var settings = _strategy.GetActiveSettingsSummary();
                var img = settings.GetValueOrDefault("Image", ContainerExtensionModule.FallbackImage);
                ShowTemporaryStatus($"Pulling default image '{img}' in terminal...");
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} pull \"{img}\"", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_PullImage", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "Download or update the configured default toolchain image"));

        actionsRow.Children.Add(CreateActionButton("Build Local Image", async () =>
        {
            try
            {
                var selection = await ShowBuildDialogAsync().ConfigureAwait(true);
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
                var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 
                    ? "linux-arm64" 
                    : "linux-x64";
                var extraArgs = $"--build-arg ARCH={arch} ";

                if (selection == "latest")
                {
                    ShowTemporaryStatus("Querying latest release tag from GitHub...");
                    
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var latestTag = await ContainerExtension.Services.GitHubReleaseClient.GetLatestReleaseTagAsync(cts.Token).ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(latestTag))
                    {
                        throw new InvalidOperationException("Failed to fetch the latest release tag from GitHub.");
                    }
                    
                    var dateStr = latestTag.Replace("-", "", StringComparison.Ordinal);
                    extraArgs += $"--build-arg RELEASE_TAG={latestTag} --build-arg RELEASE_DATE={dateStr} ";
                    ShowTemporaryStatus($"Building newest release tag '{latestTag}' ({arch}) in terminal...");
                }
                else
                {
                    ShowTemporaryStatus($"Building pinned release version ({arch}) in terminal...");
                }

                var commandLine = $"{runtimePath} build {extraArgs}-t {tag} -f \"{dockerfilePath}\" \"{buildContextDir}\"";
                await _terminalService.ExecuteInTerminalAsync(commandLine, ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(20)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_BuildLocalImage", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "Build the local FPGA toolchain Docker image from source"));

        actionsRow.Children.Add(CreateActionButton("Update All Images", async () =>
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _statusBannerText.Text = "Updating all local images...";
                    _statusBanner.Background = new SolidColorBrush(Color.FromArgb(30, 36, 150, 237));
                    _statusBanner.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 36, 150, 237));
                    _statusBanner.IsVisible = true;
                });

                var result = await _strategy.UpdateAllImagesAsync(
                    msg => Dispatcher.UIThread.Post(() =>
                    {
                        _statusBannerText.Text = $"Updating images: {msg}";
                    })
                ).ConfigureAwait(false);

                ShowTemporaryStatus($"Successfully updated {result.pulled} image(s)" + (result.failed > 0 ? $", {result.failed} failed" : ""));
                _ = RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_UpdateAllImages", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "Re-pull all local images to their latest tags (cross-platform, no shell required)"));

        actionsRow.Children.Add(CreateActionButton("⚠️ Prune All Images", async () =>
        {
            try
            {
                var confirm = await ShowConfirmDialogAsync("Prune All Images", "Are you sure you want to prune ALL unused images? This will delete all images not currently used by a container, and they will need to be re-pulled.");
                if (!confirm)
                {
                    return;
                }
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Pruning unused images in terminal...");
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} image prune -a -f", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_PruneAllImages", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "⚠️ Remove ALL unused images (not just dangling). This frees disk space but deleted images must be re-pulled."));

        actionsRow.Children.Add(CreateActionButton("⚠️ Prune System", async () =>
        {
            try
            {
                var confirm = await ShowConfirmDialogAsync("Prune System", "Are you sure you want to prune the system? This will delete all stopped containers, dangling images, and unused networks. This action cannot be undone.");
                if (!confirm)
                {
                    return;
                }
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Pruning system in terminal...");
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} system prune -f", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_PruneSystem", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "⚠️ Remove ALL stopped containers, dangling images, and unused networks. This cannot be undone."));

        actionsRow.Children.Add(CreateActionButton("Hello-World Test", async () =>
        {
            try
            {
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Running Hello-World test in terminal...");
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} run --rm hello-world", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_HelloWorldTest", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "Run a disposable hello-world container to verify Docker is working correctly"));

        actionsRow.Children.Add(CreateActionButton("Engine Info", async () =>
        {
            try
            {
                var runtimePath = _strategy.GetRuntimePath();
                ShowTemporaryStatus("Querying Engine Info in terminal...");
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} info", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_EngineInfo", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "Show detailed Docker engine configuration, storage driver, and runtime info"));

        actionsRow.Children.Add(CreateActionButton("Copy Docker Run", async () =>
        {
            try
            {
                var cmd = _strategy.GenerateDockerRunCommand();
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(cmd).ConfigureAwait(false);
                    ShowTemporaryStatus("📋 Copied equivalent 'docker run' command to clipboard!");
                    await Console.Out.WriteLineAsync($"[ContainerExtension] 📋 Copied to clipboard: {cmd}").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_CopyDockerRun", ex);
                ShowTemporaryError("⚠️ Copy failed", ex);
            }
        }, "Copy an equivalent 'docker run' command to the clipboard for manual debugging"));

        actionsRow.Children.Add(CreateActionButton("All to Docker", async () =>
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
                _ = RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_AllToDocker", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "Switch the execution strategy of all supported FPGA tools to Docker"));

        actionsRow.Children.Add(CreateActionButton("All to Native", async () =>
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
                _ = RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_AllToNative", ex);
                ShowTemporaryError("⚠️ Action failed", ex);
            }
        }, "Reset the execution strategy of all tools to their default native execution"));

        return actionsRow;
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
    private async Task<bool> ShowConfirmDialogAsync(string title, string message)
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
        
        var headerPanel = new StackPanel { Spacing = 6 };
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#E05252") // Warning/Danger Red
        };
        var separator = new Border
        {
            Height = 2,
            Background = Brush.Parse("#E05252"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 60,
            Margin = new Thickness(0, 2, 0, 8)
        };
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(separator);
        mainPanel.Children.Add(headerPanel);

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
            Content = "Yes, Prune",
            FontWeight = FontWeight.SemiBold,
            Background = Brush.Parse("#D32F2F"), // Accent Red
            Foreground = Brushes.White,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(4)
        };
        var noBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(4)
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

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        return await tcs.Task;
    }

    private async Task<string?> ShowBuildDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var dialog = new Window
        {
            Title = "Build Local Image",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var mainPanel = new StackPanel { Spacing = 16 };

        var headerPanel = new StackPanel { Spacing = 6 };
        var titleText = new TextBlock
        {
            Text = "Build Local FPGA Toolchain Image",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#007ACC") // Info Blue
        };
        var separator = new Border
        {
            Height = 2,
            Background = Brush.Parse("#007ACC"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 60,
            Margin = new Thickness(0, 2, 0, 8)
        };
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(separator);
        mainPanel.Children.Add(headerPanel);

        var msgText = new TextBlock
        {
            Text = "How would you like to build the local FPGA toolchain image?",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold
        };
        mainPanel.Children.Add(msgText);

        var detailsText = new TextBlock
        {
            Text = "• Build Pinned: Compiles the stable version defined locally in the repository.\n• Build Latest: Queries the GitHub API to fetch and compile the newest nightly release from YosysHQ.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 18,
            Opacity = 0.75
        };
        mainPanel.Children.Add(detailsText);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var pinnedBtn = new Button
        {
            Content = "Build Pinned",
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(4)
        };
        var latestBtn = new Button
        {
            Content = "Build Latest",
            FontWeight = FontWeight.SemiBold,
            Background = Brush.Parse("#007ACC"), // Accent Blue
            Foreground = Brushes.White,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(4)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(4)
        };

        pinnedBtn.Command = new RelayCommand(() =>
        {
            tcs.TrySetResult("pinned");
            dialog.Close();
        });
        latestBtn.Command = new RelayCommand(() =>
        {
            tcs.TrySetResult("latest");
            dialog.Close();
        });
        cancelBtn.Command = new RelayCommand(() =>
        {
            tcs.TrySetResult(null);
            dialog.Close();
        });

        buttonPanel.Children.Add(cancelBtn);
        buttonPanel.Children.Add(pinnedBtn);
        buttonPanel.Children.Add(latestBtn);
        mainPanel.Children.Add(buttonPanel);

        var wrapper = new Border
        {
            Padding = new Thickness(24),
            Child = mainPanel
        };

        dialog.Content = wrapper;
        dialog.Closed += (s, e) => { tcs.TrySetResult(null); };

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        return await tcs.Task;
    }

    private Panel CreateFormItem(string label, string desc, Control control)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = FontColor });
        if (!string.IsNullOrEmpty(desc))
        {
            panel.Children.Add(new TextBlock { Text = desc, FontSize = 10, Foreground = MutedColor, TextWrapping = TextWrapping.Wrap });
        }
        panel.Children.Add(control);
        return panel;
    }

    private Panel CreateFormSectionHeader(string title)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = AccentColor });
        panel.Children.Add(new Border { Height = 1, Background = MutedColor, Opacity = 0.2 });
        return panel;
    }

    private async Task ShowSettingsDialogAsync()
    {
        var dialog = new Window
        {
            Title = "Configure Container Engine Settings",
            MinWidth = 520,
            MinHeight = 500,
            Width = 520,
            Height = 650,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 16
        };

        var headerPanel = new StackPanel { Spacing = 6 };
        var titleText = new TextBlock
        {
            Text = "Container Engine Settings",
            FontSize = 15,
            FontWeight = FontWeight.Bold,
            Foreground = AccentColor
        };
        var separator = new Border
        {
            Height = 2,
            Background = AccentColor,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 60,
            Margin = new Thickness(0, 2, 0, 8)
        };
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(separator);
        Grid.SetRow(headerPanel, 0);
        mainGrid.Children.Add(headerPanel);

        var formPanel = new StackPanel { Spacing = 12 };

        // 1. IMAGE & EXECUTION
        formPanel.Children.Add(CreateFormSectionHeader("IMAGE & EXECUTION"));

        var defaultImage = _settingsService.SafeGetSetting(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.FallbackImage);
        var defaultImageTextBox = new TextBox { Text = defaultImage, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Default Toolchain Image", "The default container image to pull and use for all tools.", defaultImageTextBox));

        var pullPolicy = _settingsService.SafeGetSetting(ContainerExtensionModule.PullPolicySetting, "if-not-present");
        var pullPolicyComboBox = new ComboBox
        {
            ItemsSource = new[] { "always", "if-not-present", "never" },
            SelectedItem = pullPolicy,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Image Pull Policy", "Determines when the plugin should pull images from the registry.", pullPolicyComboBox));

        var platform = _settingsService.SafeGetSetting(ContainerExtensionModule.PlatformSetting, "auto");
        var platformComboBox = new ComboBox
        {
            ItemsSource = new[] { "auto", "linux/amd64", "linux/arm64", "linux/arm/v7" },
            SelectedItem = platform,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Image Platform", "Forces a specific system architecture platform when running containers.", platformComboBox));

        var networkMode = _settingsService.SafeGetSetting(ContainerExtensionModule.NetworkModeSetting, "bridge");
        var networkModeComboBox = new ComboBox
        {
            ItemsSource = new[] { "bridge", "host", "none" },
            SelectedItem = networkMode,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Network Mode", "The Docker network mode used for containerized tool executions.", networkModeComboBox));

        // 2. RESOURCE LIMITS
        formPanel.Children.Add(CreateFormSectionHeader("RESOURCE LIMITS"));

        var totalRam = ContainerExtensionModule.GetHostMemoryMB();
        var currentMem = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.MemoryLimitSetting, 0);
        var memValueText = new TextBlock { Text = currentMem == 0 ? "Unlimited" : $"{currentMem:N0} MB", Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontFamily = MonoFont };
        var memSlider = new Slider { Minimum = 0, Maximum = totalRam, Value = currentMem, SmallChange = 256, LargeChange = 1024, TickFrequency = 256, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        memSlider.ValueChanged += (s, e) => {
            var val = Math.Round(memSlider.Value);
            memValueText.Text = val == 0 ? "Unlimited" : $"{val:N0} MB";
        };
        var memGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        Grid.SetColumn(memSlider, 0);
        Grid.SetColumn(memValueText, 2);
        memGrid.Children.Add(memSlider);
        memGrid.Children.Add(memValueText);
        formPanel.Children.Add(CreateFormItem($"Memory Limit (0 = unlimited) — Max: {totalRam:N0} MB", "Restricts memory consumption of container tasks.", memGrid));

        var totalCores = (double)Environment.ProcessorCount;
        var currentCpu = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.CpuLimitSetting, 0);
        var cpuValueText = new TextBlock { Text = currentCpu == 0 ? "Unlimited" : $"{currentCpu:F1} Cores", Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontFamily = MonoFont };
        var cpuSlider = new Slider { Minimum = 0, Maximum = totalCores, Value = currentCpu, SmallChange = 0.5, LargeChange = 1.0, TickFrequency = 0.5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        cpuSlider.ValueChanged += (s, e) => {
            var val = Math.Round(cpuSlider.Value * 2.0) / 2.0;
            cpuValueText.Text = val == 0 ? "Unlimited" : $"{val:F1} Cores";
        };
        var cpuGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        Grid.SetColumn(cpuSlider, 0);
        Grid.SetColumn(cpuValueText, 2);
        cpuGrid.Children.Add(cpuSlider);
        cpuGrid.Children.Add(cpuValueText);
        formPanel.Children.Add(CreateFormItem($"CPU Cores Limit (0 = unlimited) — Max: {totalCores:N0} Cores", "Restricts CPU cores usage for container tasks.", cpuGrid));

        var currentTimeout = _settingsService.SafeGetSetting<double>(ContainerExtensionModule.TimeoutSetting, 0);
        var timeoutValueText = new TextBlock { Text = currentTimeout == 0 ? "No timeout" : $"{currentTimeout:N0} min", Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontFamily = MonoFont };
        var timeoutSlider = new Slider { Minimum = 0, Maximum = 480, Value = currentTimeout, SmallChange = 5, LargeChange = 30, TickFrequency = 5, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        timeoutSlider.ValueChanged += (s, e) => {
            var val = Math.Round(timeoutSlider.Value);
            timeoutValueText.Text = val == 0 ? "No timeout" : $"{val:N0} min";
        };
        var timeoutGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        Grid.SetColumn(timeoutSlider, 0);
        Grid.SetColumn(timeoutValueText, 2);
        timeoutGrid.Children.Add(timeoutSlider);
        timeoutGrid.Children.Add(timeoutValueText);
        formPanel.Children.Add(CreateFormItem("Execution Timeout (0 = no timeout)", "Maximum execution time for containers before cancellation.", timeoutGrid));

        // 3. CONTAINER CONFIG
        formPanel.Children.Add(CreateFormSectionHeader("CONTAINER CONFIG"));

        var autoRemoveSetting = _settingsService.SafeGetSetting(ContainerExtensionModule.AutoRemoveSetting, true);
        var autoRemoveCheckBox = new CheckBox { Content = "Auto-Remove Containers on Completion", IsChecked = autoRemoveSetting, FontSize = 12 };
        formPanel.Children.Add(CreateFormItem("Auto-Remove Containers", "Automatically delete containers once the executable process exits.", autoRemoveCheckBox));

        var allowPrivileged = _settingsService.SafeGetSetting(ContainerExtensionModule.AllowPrivilegedSetting, false);
        var allowPrivilegedCheckBox = new CheckBox { Content = "Allow Privileged Containers", IsChecked = allowPrivileged, FontSize = 12 };
        formPanel.Children.Add(CreateFormItem("Allow Privileged Mode", "Runs containers with privileged capabilities (required in some complex mounting setups).", allowPrivilegedCheckBox));

        var namePrefix = _settingsService.SafeGetSetting(ContainerExtensionModule.ContainerNamePrefixSetting, "containerextension-");
        var prefixTextBox = new TextBox { Text = namePrefix, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Container Name Prefix", "Prefix assigned to all containers spawned by this extension.", prefixTextBox));

        var extraFlags = _settingsService.SafeGetSetting(ContainerExtensionModule.ExtraFlagsSetting, "");
        var extraFlagsTextBox = new TextBox { Text = extraFlags, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Extra Container Labels", "Additional labels applied to containers (format: key=value,key2=value2).", extraFlagsTextBox));

        // 4. LOGGING & DASHBOARD
        formPanel.Children.Add(CreateFormSectionHeader("LOGGING & DASHBOARD"));

        var logLevel = _settingsService.SafeGetSetting(ContainerExtensionModule.LogLevelSetting, "Verbose");
        var logLevelComboBox = new ComboBox
        {
            ItemsSource = new[] { "Off", "Errors Only", "Info", "Verbose" },
            SelectedItem = logLevel,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Log Level", "Detail level for container task diagnostics logs.", logLevelComboBox));

        var showTimestamps = _settingsService.SafeGetSetting(ContainerExtensionModule.ShowTimestampsSetting, true);
        var showTimestampsCheckBox = new CheckBox { Content = "Include Timestamps in Logs", IsChecked = showTimestamps, FontSize = 12 };
        formPanel.Children.Add(CreateFormItem("Timestamps", "Prepend time signatures to stdout/stderr in log windows.", showTimestampsCheckBox));

        var dashboardRefresh = _settingsService.SafeGetSetting(ContainerExtensionModule.DashboardRefreshSetting, "Manual");
        var refreshComboBox = new ComboBox
        {
            ItemsSource = new[] { "Manual", "2s", "5s", "10s", "15s", "30s", "60s", "120s" },
            SelectedItem = dashboardRefresh,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Dashboard Refresh", "Auto-refresh frequency for container list, images, and metrics.", refreshComboBox));

        var telemetryRetention = _settingsService.SafeGetSetting(ContainerExtensionModule.TelemetryRetentionSetting, "100");
        var retentionComboBox = new ComboBox
        {
            ItemsSource = new[] { "None", "25", "50", "100", "250", "500", "1000", "Unlimited" },
            SelectedItem = telemetryRetention,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        formPanel.Children.Add(CreateFormItem("Telemetry Retention", "Number of recent executions to retain in history logs.", retentionComboBox));

        // 5. ADVANCED PATHS
        formPanel.Children.Add(CreateFormSectionHeader("ADVANCED PATHS"));

        var runtimePath = _settingsService.SafeGetSetting(ContainerExtensionModule.DockerRuntimePathSetting, "");
        var runtimePathTextBox = new TextBox { Text = runtimePath, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        var browseBtn = new Button { Content = "Browse...", FontSize = 11, Padding = new Thickness(10, 4) };
        browseBtn.Command = new AsyncRelayCommand(async () =>
        {
            try
            {
#pragma warning disable CS0618
                var openFileDialog = new OpenFileDialog
                {
                    Title = "Select Container Runtime Executable",
                    AllowMultiple = false
                };
                var result = await openFileDialog.ShowAsync(dialog);
                if (result != null && result.Length > 0)
                {
                    runtimePathTextBox.Text = result[0];
                }
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "BrowseRuntimePath", ex);
            }
        });
        var runtimeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        Grid.SetColumn(runtimePathTextBox, 0);
        Grid.SetColumn(browseBtn, 2);
        runtimeGrid.Children.Add(runtimePathTextBox);
        runtimeGrid.Children.Add(browseBtn);
        formPanel.Children.Add(CreateFormItem("Container Runtime Path", "Explicit path to docker or podman binary (leave empty for system auto-detection).", runtimeGrid));

        var customSocket = _settingsService.SafeGetSetting(ContainerExtensionModule.DaemonSocketSetting, "");
        var socketTextBox = new TextBox { Text = customSocket, FontSize = 12, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        formPanel.Children.Add(CreateFormItem("Custom Daemon Socket", "Overrides the standard DOCKER_HOST endpoint (e.g. unix:///var/run/docker.sock).", socketTextBox));

        var scroll = new ScrollViewer
        {
            Content = formPanel
        };
        Grid.SetRow(scroll, 1);
        mainGrid.Children.Add(scroll);

        // Validation Error Label
        var errorText = new TextBlock
        {
            Foreground = RedColor,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
            IsVisible = false
        };
        Grid.SetRow(errorText, 2);
        mainGrid.Children.Add(errorText);

        // Register error cleaner to auto-hide error text on any user edit
        RegisterErrorCleaner(formPanel, errorText);

        // Footer Grid
        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 10, 0, 0)
        };

        var resetBtn = new Button
        {
            Content = "Reset to Defaults",
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(4),
            Background = Brush.Parse("#1AFFFFFF"),
            Foreground = FontColor,
            BorderBrush = Brush.Parse("#2D2D30"),
            BorderThickness = new Thickness(1)
        };

        var rightButtonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var saveBtn = new Button
        {
            Content = "Save Settings",
            FontWeight = FontWeight.SemiBold,
            Background = AccentColor,
            Foreground = Brushes.White,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(4)
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(4)
        };

        rightButtonPanel.Children.Add(cancelBtn);
        rightButtonPanel.Children.Add(saveBtn);

        Grid.SetColumn(resetBtn, 0);
        Grid.SetColumn(rightButtonPanel, 2);
        footerGrid.Children.Add(resetBtn);
        footerGrid.Children.Add(rightButtonPanel);
        Grid.SetRow(footerGrid, 3);
        mainGrid.Children.Add(footerGrid);

        resetBtn.Command = new RelayCommand(() =>
        {
            defaultImageTextBox.Text = ContainerExtensionModule.FallbackImage;
            pullPolicyComboBox.SelectedItem = "if-not-present";
            platformComboBox.SelectedItem = "auto";
            networkModeComboBox.SelectedItem = "bridge";

            memSlider.Value = 0;
            cpuSlider.Value = 0;
            timeoutSlider.Value = 0;

            autoRemoveCheckBox.IsChecked = true;
            allowPrivilegedCheckBox.IsChecked = false;
            prefixTextBox.Text = "containerextension-";
            extraFlagsTextBox.Text = "";

            logLevelComboBox.SelectedItem = "Verbose";
            showTimestampsCheckBox.IsChecked = true;
            refreshComboBox.SelectedItem = "Manual";
            retentionComboBox.SelectedItem = "100";

            runtimePathTextBox.Text = "";
            socketTextBox.Text = "";
            
            errorText.IsVisible = false;
        });

        saveBtn.Command = new RelayCommand(() =>
        {
            // Perform validations
            var imageVal = new ContainerExtension.Validations.DockerImageFormatValidation(allowEmpty: false);
            var prefixVal = new ContainerExtension.Validations.ContainerNameValidation();
            var socketVal = new ContainerExtension.Validations.DaemonSocketValidation();
            var memVal = new ContainerExtension.Validations.ResourceThresholdValidation(totalRam * 0.75, totalRam, "memory");
            var cpuVal = new ContainerExtension.Validations.ResourceThresholdValidation(totalCores * 0.75, totalCores, "CPU");

            string? warn;
            if (!imageVal.Validate(defaultImageTextBox.Text, out warn))
            {
                errorText.Text = $"Image Error: {warn}";
                errorText.IsVisible = true;
                return;
            }
            if (!prefixVal.Validate(prefixTextBox.Text, out warn))
            {
                errorText.Text = $"Prefix Error: {warn}";
                errorText.IsVisible = true;
                return;
            }
            if (!socketVal.Validate(socketTextBox.Text, out warn))
            {
                errorText.Text = $"Socket Error: {warn}";
                errorText.IsVisible = true;
                return;
            }
            if (!memVal.Validate(memSlider.Value, out warn))
            {
                errorText.Text = $"Memory Limit Error: {warn}";
                errorText.IsVisible = true;
                return;
            }
            if (!cpuVal.Validate(cpuSlider.Value, out warn))
            {
                errorText.Text = $"CPU limit Error: {warn}";
                errorText.IsVisible = true;
                return;
            }

            try
            {
                _settingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, defaultImageTextBox.Text?.Trim() ?? "");
                _settingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, pullPolicyComboBox.SelectedItem as string ?? "");
                _settingsService.SetSettingValue(ContainerExtensionModule.PlatformSetting, platformComboBox.SelectedItem as string ?? "");
                _settingsService.SetSettingValue(ContainerExtensionModule.NetworkModeSetting, networkModeComboBox.SelectedItem as string ?? "");

                _settingsService.SetSettingValue(ContainerExtensionModule.MemoryLimitSetting, Math.Round(memSlider.Value));
                _settingsService.SetSettingValue(ContainerExtensionModule.CpuLimitSetting, Math.Round(cpuSlider.Value * 2.0) / 2.0);
                _settingsService.SetSettingValue(ContainerExtensionModule.TimeoutSetting, Math.Round(timeoutSlider.Value));

                _settingsService.SetSettingValue(ContainerExtensionModule.AutoRemoveSetting, autoRemoveCheckBox.IsChecked == true);
                _settingsService.SetSettingValue(ContainerExtensionModule.AllowPrivilegedSetting, allowPrivilegedCheckBox.IsChecked == true);
                _settingsService.SetSettingValue(ContainerExtensionModule.ContainerNamePrefixSetting, prefixTextBox.Text?.Trim() ?? "");
                _settingsService.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, extraFlagsTextBox.Text?.Trim() ?? "");

                _settingsService.SetSettingValue(ContainerExtensionModule.LogLevelSetting, logLevelComboBox.SelectedItem as string ?? "");
                _settingsService.SetSettingValue(ContainerExtensionModule.ShowTimestampsSetting, showTimestampsCheckBox.IsChecked == true);
                _settingsService.SetSettingValue(ContainerExtensionModule.DashboardRefreshSetting, refreshComboBox.SelectedItem as string ?? "");
                _settingsService.SetSettingValue(ContainerExtensionModule.TelemetryRetentionSetting, retentionComboBox.SelectedItem as string ?? "");

                _settingsService.SetSettingValue(ContainerExtensionModule.DockerRuntimePathSetting, runtimePathTextBox.Text?.Trim() ?? "");
                _settingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, socketTextBox.Text?.Trim() ?? "");

                _ = RefreshAllAsync(); // Refresh dashboard display
                dialog.Close();
            }
            catch (Exception ex)
            {
                errorText.Text = $"Save Error: {ex.Message}";
                errorText.IsVisible = true;
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "SaveSettings", ex);
            }
        });

        cancelBtn.Command = new RelayCommand(() => dialog.Close());

        var wrapper = new Border
        {
            Padding = new Thickness(24),
            Child = mainGrid
        };

        dialog.Content = wrapper;

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

    private static void RegisterErrorCleaner(Avalonia.Controls.Control control, Avalonia.Controls.TextBlock errorText)
    {
        if (control is Avalonia.Controls.TextBox tb)
        {
            tb.TextChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.ComboBox cb)
        {
            cb.SelectionChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.Slider sl)
        {
            sl.ValueChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.CheckBox chk)
        {
            chk.IsCheckedChanged += (s, e) => errorText.IsVisible = false;
        }
        else if (control is Avalonia.Controls.Panel panel)
        {
            foreach (var child in panel.Children)
            {
                RegisterErrorCleaner(child, errorText);
            }
        }
        else if (control is Avalonia.Controls.ContentControl cc && cc.Content is Avalonia.Controls.Control childControl)
        {
            RegisterErrorCleaner(childControl, errorText);
        }
        else if (control is Avalonia.Controls.ScrollViewer sv && sv.Content is Avalonia.Controls.Control svChild)
        {
            RegisterErrorCleaner(svChild, errorText);
        }
        else if (control is Avalonia.Controls.Border border && border.Child is Avalonia.Controls.Control borderChild)
        {
            RegisterErrorCleaner(borderChild, errorText);
        }
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
            FontSize = 16,
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

        return new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush.Parse("#2D2D30"),
            Child = grid,
            Margin = new Thickness(0, 0, 6, 0)
        };
    }
}
