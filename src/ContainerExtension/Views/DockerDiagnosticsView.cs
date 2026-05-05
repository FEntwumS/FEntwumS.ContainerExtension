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
using Avalonia.Threading;

namespace ContainerExtension.Views;

/// <summary>
/// Docker Desktop-style dashboard <see cref="UserControl"/> providing live insight
/// into the local container ecosystem. Queries the Docker.DotNet SDK directly
/// (via <see cref="DockerExecutionStrategy"/>) for real-time data.
/// <para>
/// Can be embedded inside a <see cref="ViewModels.DockerDiagnosticsViewModel"/>
/// (dockable panel at the bottom of the IDE) or a standalone
/// <see cref="DockerDiagnosticsWindow"/> (popup fallback).
/// </para>
/// <para>
/// The entire UI tree is constructed in C# (no XAML) to bypass macOS sandbox
/// restrictions on dynamically-compiled XAML markup.
/// </para>
/// <para>
/// <b>Sections:</b>
/// <list type="bullet">
///   <item>Quick Actions — inline header buttons: pull, prune, hello-world test</item>
///   <item>Connection Status — daemon health, version, OS, resource summary</item>
///   <item>Containers — live container list with status and actions</item>
///   <item>Images &amp; Disk Usage — cached image inventory with sizes and reclaimable space</item>
///   <item>Active Configuration — current settings snapshot from Container Engine</item>
///   <item>Execution History — last 10 telemetry entries with status, tool, image, duration, and actions</item>
/// </list>
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "UserControl lifecycle manages _refreshCts disposal via DetachedFromVisualTree handler.")]
public partial class DockerDiagnosticsView : UserControl
{
    // ── Color Palette ───────────────────────────────────────────────────
    private static readonly SolidColorBrush FontColor = new(Color.Parse("#E0E0E0"));
    private static readonly SolidColorBrush MutedColor = new(Color.Parse("#888888"));
    private static readonly SolidColorBrush AccentColor = new(Color.Parse(ContainerExtensionModule.DockerBlueHex)); // Docker blue
    private static readonly SolidColorBrush GreenColor = new(Color.Parse("#4CAF50"));
    private static readonly SolidColorBrush RedColor = new(Color.Parse("#FF6B6B"));
    private static readonly SolidColorBrush YellowColor = new(Color.Parse("#FFD54F"));
    private static readonly SolidColorBrush CardBg = new(Color.Parse("#1A2496ED"));
    private static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Menlo, monospace");

    // Grid column indices for sortable header rows (CA1861 — avoid per-call allocation)
    private static readonly int[] ThreeColumnIndices = { 0, 2, 4 };
    private static readonly int[] SevenColumnIndices = { 0, 2, 4, 6, 8, 10, 12 };

    // Session-persistent collapsed/expanded state (survives panel close/reopen within session)
    private static readonly Dictionary<string, bool> SectionExpandedState = new();

    // ── Instance State ──────────────────────────────────────────────────
    private readonly DockerExecutionStrategy _strategy;
    private readonly ITerminalManagerService _terminalService;
    private readonly StackPanel _statusContent;
    private readonly StackPanel _configContent;
    private readonly StackPanel _containersContent;
    private readonly StackPanel _imagesContent;
    private readonly StackPanel _telemetryContent;
    private readonly StackPanel _toolchainContent;
    private readonly TextBlock _headerTitle;
    private readonly string _pluginVersion;
    private readonly WrapPanel _quickActionsRow;
    private readonly ISettingsService _settingsService;

    // Tracks open container log windows to prevent duplicate spawning
    private readonly Dictionary<string, Window> _openLogWindows = new();

    // Auto-refresh state
    private CancellationTokenSource? _refreshCts;
    private readonly TextBlock _lastRefreshedText;
    private readonly TextBlock _countdownText;
    private int _refreshIntervalSeconds;
    private int _secondsUntilRefresh;
    private bool _hasAttached; // Guard against duplicate AttachedToVisualTree handlers (F15)

    // ── Cached Data (for re-sorting without re-querying the daemon) ──
    private IList<Docker.DotNet.Models.ContainerListResponse> _cachedContainers = Array.Empty<Docker.DotNet.Models.ContainerListResponse>();
    private IList<Docker.DotNet.Models.ImagesListResponse> _cachedImages = Array.Empty<Docker.DotNet.Models.ImagesListResponse>();
    private (int imageCount, long totalSizeBytes, long reclaimableBytes) _cachedDiskUsage;

    // ── Search/Filter State ──────────────────────────────────────────
    private string _searchFilter = "";

    // ── Sort State (column name + direction per table) ───────────────
    private (string column, bool ascending) _containerSort = ("name", true);
    private (string column, bool ascending) _imageSort = ("repo", true);
    private (string column, bool ascending) _historySort = ("time", false); // newest-first default

    // ── Data Fingerprints (skip UI rebuild when data is unchanged) ────
    // Simple fingerprints avoid tearing down and recreating the full UI tree
    // on every auto-refresh tick when nothing has changed (important at 2s/5s intervals).
    private int _lastContainerFingerprint;
    private int _lastImageFingerprint;

    /// <summary>
    /// Constructs the Docker Desktop-style dashboard as a <see cref="UserControl"/>.
    /// Can be embedded inside a dockable panel (<see cref="ViewModels.DockerDiagnosticsViewModel"/>)
    /// or a standalone <see cref="DockerDiagnosticsWindow"/>.
    /// </summary>
    /// <param name="serviceProvider">The OneWare Studio DI service provider.</param>
    /// <param name="strategy">The strategy instance for Docker API queries and settings access.</param>
    public DockerDiagnosticsView(IServiceProvider serviceProvider, DockerExecutionStrategy strategy)
    {
        _strategy = strategy;
        _terminalService = serviceProvider.Resolve<ITerminalManagerService>();

        // Resolve the IDE's theme background brush
        Application.Current!.TryFindResource("ThemeBackgroundBrushOp", Application.Current!.RequestedThemeVariant, out var bgRes);
        bgRes ??= Application.Current!.FindResource("ThemeBackgroundBrushOp");
        Background = (bgRes as IBrush) ?? Brushes.Transparent;

        // Read plugin version from assembly metadata (set in .csproj <Version>)
        var asm = typeof(DockerDiagnosticsView).Assembly;
        var ver = asm.GetName().Version;
        _pluginVersion = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";

        // ── Header ──────────────────────────────────────────────────────
        var whaleIcon = new PathIcon
        {
            Data = Geometry.Parse(ContainerExtensionModule.WhaleIconPath),
            Foreground = new SolidColorBrush(Color.Parse(ContainerExtensionModule.DockerBlueHex)),
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

        var headerTitlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerTitlePanel.Children.Add(whaleIcon);
        headerTitlePanel.Children.Add(_headerTitle);

        // ── Global Search / Filter ──────────────────────────────────────
        // Placed as a full-width row below the header to ensure visibility
        // in narrow docked panels (the inline StackPanel clips overflow).
        var searchBox = new TextBox
        {
            Watermark = "🔍  Filter containers, images, history...",
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        CancellationTokenSource? searchCts = null;
#pragma warning disable VSTHRD101 // Avoid using async lambda for a void returning delegate type
        searchBox.TextChanged += async (_, _) =>
        {
            try
            {
                searchCts?.Cancel();
                searchCts?.Dispose();
                searchCts = new CancellationTokenSource();
                var token = searchCts.Token;

                _searchFilter = searchBox.Text ?? "";
                
                try { await Task.Delay(250, token); }
                catch (TaskCanceledException) { return; }

                if (!token.IsCancellationRequested)
                    ApplySearchFilter();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "SearchBox_TextChanged", ex);
            }
        };
#pragma warning restore VSTHRD101

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
            Command = new AsyncRelayCommand(RefreshAllAsync)
        };
        ToolTip.SetTip(refreshBtn, "Re-query the Docker daemon for live container, image, and system data");

        var statusBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
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

        // ── Quick Actions (inline below header) ─────────────────────────
        _quickActionsRow = BuildQuickActionsRow();
        _quickActionsRow.Opacity = 0.5;  // dimmed until daemon is confirmed reachable
        _quickActionsRow.IsEnabled = false;

        // ── Section 1: Connection Status ────────────────────────────────
        _statusContent = new StackPanel { Spacing = 4 };
        _statusContent.Children.Add(CreateLoadingText("Connecting to daemon..."));
        var statusSection = CreateCard("Connection Status", _statusContent);

        // ── Section 2: Containers ───────────────────────────────────────
        _containersContent = new StackPanel { Spacing = 2 };
        _containersContent.Children.Add(CreateLoadingText("Loading containers..."));
        var containersSection = CreateCard("Containers", _containersContent);

        // ── Section 3: Images & Disk Usage ──────────────────────────────
        _imagesContent = new StackPanel { Spacing = 2 };
        _imagesContent.Children.Add(CreateLoadingText("Loading images..."));
        var imagesSection = CreateCard("Images & Disk Usage", _imagesContent);

        // ── Section 4: Active Configuration ─────────────────────────────
        _configContent = new StackPanel { Spacing = 2 };
        _configContent.Children.Add(CreateLoadingText("Reading settings..."));
        var configSection = CreateCard("Active Configuration", _configContent);

        // ── Section 5: Recent Executions ────────────────────────────────
        _telemetryContent = new StackPanel { Spacing = 2 };
        var telemetrySection = CreateCard("Execution History", _telemetryContent);

        // ── Section 6: Toolchain Environment ────────────────────────────
        _toolchainContent = new StackPanel { Spacing = 4 };
        _toolchainContent.Children.Add(CreateLoadingText("Loading available versions..."));
        var toolchainSection = CreateCard("Toolchain Environment", _toolchainContent);

        // ── Layout ──────────────────────────────────────────────────────
        // Live data first → toolchain → config → history
        var mainPanel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                header,
                searchBox,
                _quickActionsRow,
                statusSection,
                toolchainSection,
                containersSection,
                imagesSection,
                configSection,
                telemetrySection
            }
        };

        Content = new ScrollViewer { Content = mainPanel };

        // Resolve settings service for auto-refresh interval
        _settingsService = serviceProvider.Resolve<ISettingsService>();

        // Fire-and-forget: load all live data after the control renders + start auto-refresh.
        // Avalonia event handlers require void return type; try/catch prevents unobserved exceptions.
#pragma warning disable VSTHRD101
        AttachedToVisualTree += async (_, _) =>
        {
            if (_hasAttached) return; // Prevent duplicate handlers on dock/undock cycles (F15)
            _hasAttached = true;
            try { await RefreshAllAsync(); }
            catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView", "RefreshAllAsync_Attach", ex); }
            try { await PopulateToolchainEnvironmentAsync(); }
            catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView", "PopulateToolchainEnvironment_Attach", ex); }
            StartAutoRefreshTimer();
        };
#pragma warning restore VSTHRD101
        DetachedFromVisualTree += (_, _) =>
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
            _hasAttached = false; // Allow re-attach to refresh again (F15)

            // Close any orphan container log windows spawned from this dashboard
            var windowsToClose = _openLogWindows.Values.ToList();
            _openLogWindows.Clear();
            foreach (var w in windowsToClose)
            {
                try { w.Close(); } catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView", "CloseOrphanLogWindow", ex); }
            }
        };
    }

    /// <summary>Updates the "Last refreshed" timestamp display in the header status bar.</summary>
    private void UpdateLastRefreshedTimestamp()
    {
        _lastRefreshedText.Text = $"Last refreshed: {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>
    /// Starts the auto-refresh and countdown timers based on the Dashboard Refresh setting.
    /// Called once after the initial data load completes.
    /// Stops any existing timers first to prevent GC pressure from rapid dock/undock cycles.
    /// </summary>
    private void StartAutoRefreshTimer()
    {
        // Stop any previously created timers to prevent accumulation on re-attach
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;

        string? interval;
        try { interval = _settingsService.GetSettingValue<string>(ContainerExtensionModule.DashboardRefreshSetting); }
        catch { interval = "Manual"; }
        if (string.IsNullOrEmpty(interval) || interval == "Manual")
        {
            _countdownText.Text = "";
            return;
        }

        _refreshIntervalSeconds = interval.EndsWith('s') &&
            int.TryParse(interval.AsSpan(0, interval.Length - 1), out var secs)
            ? secs : 0;
        if (_refreshIntervalSeconds <= 0) return;

        _secondsUntilRefresh = _refreshIntervalSeconds;
        _countdownText.Text = $"| next in {_secondsUntilRefresh}s";

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token);
                    if (token.IsCancellationRequested) break;

                    _secondsUntilRefresh--;

                    if (_secondsUntilRefresh <= 0)
                    {
                        _secondsUntilRefresh = _refreshIntervalSeconds;
                        Dispatcher.UIThread.Post(() => _countdownText.Text = "Refreshing...");
                        await Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            try { await RefreshAllAsync(); }
                            catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView", "AutoRefresh", ex); }
                        });
                    }

                    Dispatcher.UIThread.Post(() => _countdownText.Text = $"| next in {_secondsUntilRefresh}s");
                }
                catch (TaskCanceledException) { break; }
            }
        }, token);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Live Data Refresh
    // ═══════════════════════════════════════════════════════════════════════

    private bool _isRefreshing;

    /// <summary>
    /// Creates a sortable header row for a table. Each column label becomes a clickable button.
    /// Clicking a column sorts the table by that column; clicking the same column toggles direction.
    /// The active sort column shows a ▲ or ▼ indicator.
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
            var isActive = currentSort.column == sortKey;
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
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            // Reset countdown so manual refresh pushes out the next auto-refresh
            _secondsUntilRefresh = _refreshIntervalSeconds;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = cts.Token;

            // Ping first to determine reachability
            var isReachable = await _strategy.PingAsync(ct);

            // Read settings snapshot (always available, synchronous)
            var settings = _strategy.GetActiveSettingsSummary();

            if (!isReachable)
            {
                // Daemon unreachable — show offline state for all daemon-dependent sections
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    PopulateStatus(false, null);
                    PopulateConfig(settings);
                    PopulateOfflineSections();
                    _quickActionsRow.IsEnabled = false;
                    _quickActionsRow.Opacity = 0.5;
                    PopulateTelemetry();
                    UpdateHeaderBadge(0);
                    UpdateLastRefreshedTimestamp();
                });
                return;
            }

            // Daemon reachable — run all API queries in parallel
            var infoTask = _strategy.GetSystemInfoAsync(ct);
            var containersTask = _strategy.ListContainersAsync(ct);
            var imagesTask = _strategy.ListImagesAsync(ct);

            Docker.DotNet.Models.SystemInfoResponse? info = null;
            IList<Docker.DotNet.Models.ContainerListResponse> containers = Array.Empty<Docker.DotNet.Models.ContainerListResponse>();
            IList<Docker.DotNet.Models.ImagesListResponse> images = Array.Empty<Docker.DotNet.Models.ImagesListResponse>();

            try
            {
                await Task.WhenAll(infoTask, containersTask, imagesTask);
                info = await infoTask;
                containers = await containersTask;
                images = await imagesTask;
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "RefreshAllAsync_ParallelQuery", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _headerTitle.Text = $"{ContainerExtensionModule.DashboardTitle} ⚠️ API Error";
                    ToolTip.SetTip(_headerTitle, ex.Message);
                    _quickActionsRow.IsEnabled = true;
                    _quickActionsRow.Opacity = 1.0;
                    PopulateStatus(false, null);
                    PopulateConfig(settings);
                    PopulateTelemetry();
                    UpdateHeaderBadge(0);
                    UpdateLastRefreshedTimestamp();
                });
                return;
            }

            // Compute disk usage from the already-fetched image list (avoids duplicate API call)
            var diskUsage = DockerExecutionStrategy.ComputeDiskUsage(images);

            // Update UI on the Avalonia dispatcher thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                PopulateStatus(true, info);

                // Skip-if-unchanged: compare a lightweight fingerprint of container/image data
                // to avoid full UI tree rebuild when nothing has changed (critical at 2s/5s refresh rates).
                var containerFp = containers.Count;
                foreach (var c in containers.Take(5))
                    containerFp = HashCode.Combine(containerFp, c.ID[..8], c.State);
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
                PopulateTelemetry();
                UpdateHeaderBadge(containers.Count);
                UpdateLastRefreshedTimestamp();
            });
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Section Population
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Populates the Toolchain Environment section by checking the remote registry for default image updates.</summary>
    private async Task PopulateToolchainEnvironmentAsync()
    {
        var settings = _strategy.GetActiveSettingsSummary();
        var currentImage = settings.GetValueOrDefault("Image", ContainerExtensionModule.FallbackImage);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
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
            tags = await FEntwumS.ContainerExtension.Registry.RegistryClient.FetchTagsAsync(currentImage);
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView", "FetchTagsAsync", ex);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_toolchainContent == null) return;
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
                comboBox.ItemsSource = tags.Select(t => $"{currentImage.Split(':')[0]}:{t}").ToList();
                comboBox.SelectedItem = tags.Any(t => currentImage.EndsWith($":{t}", StringComparison.OrdinalIgnoreCase)) ? currentImage : currentImage;

                // Only allow switching to tags for the current image
                comboBox.SelectionChanged += (s, e) =>
                {
                    if (comboBox.SelectedItem is string newImage && newImage != currentImage)
                    {
                        try
                        {
                            _settingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, newImage);
                            _ = RefreshAllAsync(); // refresh configuration display
                        }
                        catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView", "ChangeActiveImageSettings", ex); }
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
                    btn.IsEnabled = false;
                    btn.Content = "Pulling...";
                    var runtimePath = _strategy.GetRuntimePath();
                    await _terminalService.ExecuteInTerminalAsync($"{runtimePath} pull \"{activeImg}\"", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(5));

                    // Prune dangling images to free disk space
                    _ = _strategy.PruneDanglingImagesAsync();
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("DockerDiagnosticsView", "UpdateAndPullImage", ex);
                    btn.Content = "Error ✗";
                    ToolTip.SetTip(btn, $"Update failed: {ex.Message}");
                    await Task.Delay(3000);
                    ToolTip.SetTip(btn, prevTip);
                }
                finally
                {
                    btn.Content = "Check for Updates & Pull";
                    btn.IsEnabled = true;
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

        // "Open Desktop" button — launches the container runtime's GUI app
        var desktopAppName = GetDesktopAppName(_strategy.DetectedRuntime);
        if (desktopAppName != null)
        {
            var openDesktopBtn = new Button
            {
                Content = "Open Desktop",
                FontSize = 10,
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Command = new RelayCommand(() => LaunchDesktopApp(_strategy.DetectedRuntime))
            };
            ToolTip.SetTip(openDesktopBtn, $"Launch {desktopAppName}");
            statusRow.Children.Add(openDesktopBtn);
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

        // ── Extension Metadata ──────────────────────────────────────────
        var metaGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,12,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };
        AddInfoRow(metaGrid, 0, "Extension", $"Container Extension {_pluginVersion}");
        AddInfoRow(metaGrid, 1, "Runtime", $"{_strategy.DetectedRuntime} | .NET {Environment.Version}");
        AddInfoRow(metaGrid, 2, "Telemetry", ContainerTelemetry.TelemetryFilePath);
        _configContent.Children.Add(metaGrid);

        _configContent.Children.Add(CreateSeparator());

        // ── Grouped Settings ────────────────────────────────────────────
        var groups = new (string title, string[] keys)[]
        {
            ("Image & Execution", new[] { "Image", "Pull Policy", "Platform", "Network" }),
            ("Resource Limits",   new[] { "Memory", "CPU", "Timeout" }),
            ("Container",         new[] { "Auto-Remove", "Name Prefix", "Extra Labels" }),
            ("Logging",           new[] { "Log Level", "Timestamps" }),
            ("Dashboard",         new[] { "Dashboard Refresh", "Retention" }),
            ("Advanced",          new[] { "Runtime Path" }),
        };

        foreach (var (title, keys) in groups)
        {
            // Collect only keys that exist in the settings dictionary
            var pairs = keys
                .Where(k => settings.ContainsKey(k))
                .Select(k => (key: k, value: settings[k]))
                .ToArray();
            if (pairs.Length == 0) continue;

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

            // Single-column key=value grid — clean and predictable layout
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
                    Margin = new Thickness(0, 2, 0, 0)
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

    private void ShowTemporaryError(string titlePrefix, Exception ex)
    {
        var msg = $"{titlePrefix}: {ex.Message}";
        Dispatcher.UIThread.Post(() => _headerTitle.Text = msg);
        _ = System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_headerTitle.Text != null && _headerTitle.Text.StartsWith(titlePrefix, System.StringComparison.Ordinal))
                UpdateHeaderBadge(_cachedContainers.Count);
        }), System.Threading.Tasks.TaskScheduler.Default);
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
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} pull \"{img}\"", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(5));
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
                var result = await _strategy.UpdateAllImagesAsync(
                    msg => Dispatcher.UIThread.Post(() =>
                    {
                        _headerTitle.Text = $"{ContainerExtensionModule.DashboardTitle} — {msg}";
                    }));
                Dispatcher.UIThread.Post(() =>
                {
                    _headerTitle.Text = $"{ContainerExtensionModule.DashboardTitle} — Updated {result.pulled} image(s)" +
                        (result.failed > 0 ? $", {result.failed} failed" : "");
                    _ = RefreshAllAsync(); // Fire-and-forget; RefreshAllAsync has its own error handling
                });
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
                var runtimePath = _strategy.GetRuntimePath();
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} image prune -a -f", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2));
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
                var runtimePath = _strategy.GetRuntimePath();
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} system prune -f", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2));
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
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} run --rm hello-world", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(2));
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
                await _terminalService.ExecuteInTerminalAsync($"{runtimePath} info", ContainerExtensionModule.DashboardTitle, showInUi: true, timeout: TimeSpan.FromMinutes(1));
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
                    await topLevel.Clipboard.SetTextAsync(cmd);
                    await Console.Out.WriteLineAsync($"[ContainerExtension] 📋 Copied to clipboard: {cmd}");
                }
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView", "Action_CopyDockerRun", ex);
                ShowTemporaryError("⚠️ Copy failed", ex);
            }
        }, "Copy an equivalent 'docker run' command to the clipboard for manual debugging"));

        return actionsRow;
    }

    /// <summary>Updates the header title to include the container count badge.</summary>
    private void UpdateHeaderBadge(int containerCount)
    {
        _headerTitle.Text = containerCount > 0
            ? $"{ContainerExtensionModule.DashboardTitle} ({containerCount})"
            : ContainerExtensionModule.DashboardTitle;
    }

    /// <summary>
    /// Re-populates all data sections using the current <see cref="_searchFilter"/>.
    /// Called when the global search TextBox content changes.
    /// </summary>
    private void ApplySearchFilter()
    {
        PopulateContainers(_cachedContainers);
        PopulateImages(_cachedImages, _cachedDiskUsage);
        PopulateTelemetry();
    }

}
