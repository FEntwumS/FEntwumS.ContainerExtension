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

    // Tracks open container log windows to prevent duplicate spawning
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

        // -- Layout ------------------------------------------------------
        var mainPanel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                header,
                _statusBanner,
                _searchBox,
                _quickActionsRow,
                statusSection,
                toolchainSection,
                containersSection,
                imagesSection,
                configSection,
                telemetrySection
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

    /// <summary>Populates the Active Configuration section with grouped, color-coded settings.</summary>
    private void PopulateConfig(Dictionary<string, string> settings)
    {
        _configContent.Children.Clear();

        // -- Extension Metadata ------------------------------------------
        var metaGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,12,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };
        AddInfoRow(metaGrid, 0, "Extension", $"Container Extension {_pluginVersion}");
        AddInfoRow(metaGrid, 1, "Runtime", $"{_strategy.DetectedRuntime} | .NET {Environment.Version}");

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
            // Ignored to prevent metadata display failures
            _ = ex;
        }
        AddInfoRow(metaGrid, 2, "Telemetry", telemetryPath);
        _configContent.Children.Add(metaGrid);

        _configContent.Children.Add(CreateSeparator());

        // -- Grouped Settings --------------------------------------------
        var groups = new (string title, string[] keys)[]
        {
            ("Image & Execution", [ ContainerExtensionModule.SettingsKeyImage, ContainerExtensionModule.SettingsKeyPullPolicy, ContainerExtensionModule.SettingsKeyPlatform, ContainerExtensionModule.SettingsKeyNetwork ]),
            ("Resource Limits",  [ ContainerExtensionModule.SettingsKeyMemory, ContainerExtensionModule.SettingsKeyCpu, ContainerExtensionModule.SettingsKeyTimeout ]),
            ("Container",     [ ContainerExtensionModule.SettingsKeyAutoRemove, ContainerExtensionModule.SettingsKeyNamePrefix, ContainerExtensionModule.SettingsKeyExtraLabels ]),
            ("Logging",      [ ContainerExtensionModule.SettingsKeyLogLevel, ContainerExtensionModule.SettingsKeyTimestamps ]),
            ("Dashboard",     [ ContainerExtensionModule.SettingsKeyDashboardRefresh, ContainerExtensionModule.SettingsKeyRetention ]),
            ("Advanced",     [ ContainerExtensionModule.SettingsKeyRuntimePath ]),
        };

        foreach (var (title, keys) in groups)
        {
            // Collect only keys that exist in the settings dictionary
            var pairs = keys
        .Where(k => settings.ContainsKey(k))
        .Select(k => (key: k, value: settings[k]))
        .ToArray();
            if (pairs.Length == 0)
            {
                continue;
            }

            // Category label
            _configContent.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = AccentColor,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 2),
                Opacity = 0.8
            });

            // Single-column key=value grid - clean and predictable layout
            var rowDef = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", pairs.Length)));
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,8,*"),
                RowDefinitions = rowDef
            };

            for (int i = 0; i < pairs.Length; i++)
            {
                var keyBlock = new TextBlock
                {
                    Text = pairs[i].key,
                    Foreground = MutedColor,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Margin = new Thickness(8, 2, 0, 0)
                };
                Grid.SetRow(keyBlock, i);
                Grid.SetColumn(keyBlock, 0);
                grid.Children.Add(keyBlock);

                // Color-code values: green=enabled, red=disabled, muted=default/none
                var val = pairs[i].value;
                var valColor = val switch
                {
                    "On" => GreenColor,
                    "Off" => RedColor,
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
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                };
                Grid.SetRow(valBlock, i);
                Grid.SetColumn(valBlock, 2);
                grid.Children.Add(valBlock);
            }

            _configContent.Children.Add(grid);

            // Subtle separator between groups
            _configContent.Children.Add(CreateSeparator());
        }

        _configContent.Children.Add(new TextBlock
        {
            Text = "Settings: Binary Management > Container Engine",
            Foreground = MutedColor,
            FontSize = 10,
            FontStyle = FontStyle.Italic,
            Margin = new Thickness(0, 2, 0, 0)
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
            Width = 350,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12 });

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var yesBtn = new Button { Content = "Yes", Width = 60, Padding = new Thickness(8, 4) };
        var noBtn = new Button { Content = "No", Width = 60, Padding = new Thickness(8, 4) };

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

        buttonPanel.Children.Add(yesBtn);
        buttonPanel.Children.Add(noBtn);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;

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
}
