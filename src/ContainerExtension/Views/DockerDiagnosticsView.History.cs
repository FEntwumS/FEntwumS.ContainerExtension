using static ContainerExtension.Views.UIBuilderHelpers;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
namespace ContainerExtension.Views;

/// <summary>
/// Partial class containing the Execution History (telemetry) section logic:
/// <see cref="PopulateTelemetry"/> and <see cref="CreateHistoryRow"/>.
/// </summary>
public partial class DockerDiagnosticsView
{
    private int _lastTelemetryFingerprint;

    /// <summary>Populates the Execution History section with a tabular display of the last 10 telemetry entries and aggregate stats.</summary>
#pragma warning disable VSTHRD100 // Avoid async void methods
    private async void PopulateTelemetry()
#pragma warning restore VSTHRD100
    {
        try
        {
            // Check if telemetry is disabled via the Retention = None setting
            string? retentionStr;
            try { retentionStr = _settingsService.GetSettingValue<string>(ContainerExtensionModule.TelemetryRetentionSetting); }
            catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.History", "ParseRetentionSetting", ex); retentionStr = "100"; }
            
            // Run I/O intensive operation on a background thread to prevent UI freezing
            var (entries, totalRuns, successRate, avgDuration) = await Task.Run(() => ContainerTelemetry.GetRecentEntriesWithStats(50));

            // Compute a simple fingerprint to prevent layout thrashing on every tick
            var fp = HashCode.Combine(totalRuns, _searchFilter ?? string.Empty, retentionStr);
            if (entries.Count > 0)
                fp = HashCode.Combine(fp, entries[0].Timestamp);
            
            if (fp == _lastTelemetryFingerprint) return;
            _lastTelemetryFingerprint = fp;

            _telemetryContent.Children.Clear();

            if (retentionStr == "None")
            {
                _telemetryContent.Children.Add(new TextBlock
                {
                    Text = "Telemetry is disabled (Retention = None). Change the Telemetry Retention setting to enable execution history.",
                    Foreground = YellowColor,
                    FontSize = 11,
                    FontStyle = FontStyle.Italic
                });
                return;
            }

        var statsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (totalRuns > 0)
        {
            statsRow.Children.Add(new TextBlock
            {
                Text = $"{totalRuns} runs | {successRate}% success | avg {avgDuration}s",
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = FontColor,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // "Open Log" button — opens the telemetry JSONL file with the system default editor
        if (File.Exists(ContainerTelemetry.TelemetryFilePath))
        {
            var openLogBtn = new Button
            {
                Content = "Open Log",
                FontSize = 10,
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Command = new RelayCommand(() => OpenWithSystemDefault(ContainerTelemetry.TelemetryFilePath))
            };
            ToolTip.SetTip(openLogBtn, $"Open {ContainerTelemetry.TelemetryFilePath}");
            statsRow.Children.Add(openLogBtn);
        }

        if (statsRow.Children.Count > 0)
        {
            statsRow.Margin = new Thickness(0, 0, 0, 4);
            _telemetryContent.Children.Add(statsRow);
        }

        if (entries.Count == 0 && totalRuns == 0)
        {
            _telemetryContent.Children.Add(new TextBlock
            {
                Text = "No executions recorded yet. Run a tool to see history here.",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
            return;
        }

        // Apply global search filter (case-insensitive substring match on tool name or image)
        if (!string.IsNullOrEmpty(_searchFilter))
            entries = entries.Where(e =>
                (e.Tool?.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Image?.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();

        if (entries.Count > 0)
        {
            // Historical Resource Trends
            var cpuValues = entries.Where(e => e.MaxCpuPercent.HasValue).Select(e => e.MaxCpuPercent!.Value).Reverse().ToList();
            var ramValues = entries.Where(e => e.PeakMemoryBytes.HasValue).Select(e => (double)e.PeakMemoryBytes!.Value).Reverse().ToList();

            var trendsPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, Margin = new Thickness(0, 4, 0, 12) };
            trendsPanel.Children.Add(new TextBlock { Text = "HISTORICAL RESOURCE TRENDS", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = AccentColor });

            var trendsData = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            if (cpuValues.Count > 0)
            {
                trendsData.Children.Add(new TextBlock { Text = $"CPU: {cpuValues.Average():F1}% avg  [{CreateSparkline(cpuValues)}]", FontFamily = MonoFont, FontSize = 11, Foreground = FontColor });
            }
            if (ramValues.Count > 0)
            {
                trendsData.Children.Add(new TextBlock { Text = $"RAM: {FormatBytes((long)ramValues.Max())} peak  [{CreateSparkline(ramValues)}]", FontFamily = MonoFont, FontSize = 11, Foreground = FontColor });
            }
            if (trendsData.Children.Count > 0) trendsPanel.Children.Add(trendsData);

            _telemetryContent.Children.Add(trendsPanel);

            // Sortable header row
            _telemetryContent.Children.Add(CreateSortableHeaderRow(
                new[] { ("STATUS", "status"), ("TOOL", "tool"), ("IMAGE", "image"), ("DURATION", "duration"), ("PEAK RAM", "ram"), ("MAX CPU", "cpu"), ("TIME", "time") },
                _historySort,
                key => { ToggleSort(ref _historySort, key); PopulateTelemetry(); },
                "55,8,140,8,190,8,65,8,75,8,60,8,90,8,Auto",
                SevenColumnIndices,
                "ACTIONS", 14));

            // Separator
            _telemetryContent.Children.Add(CreateSeparator());

            // Sort entries by active column
            var sortedEntries = _historySort.column switch
            {
                "status" => _historySort.ascending
                    ? entries.OrderBy(e => e.WasCancelled ? 1 : (e.ExitCode == 0 ? 0 : 2))
                    : entries.OrderByDescending(e => e.WasCancelled ? 1 : (e.ExitCode == 0 ? 0 : 2)),
                "tool" => _historySort.ascending
                    ? entries.OrderBy(e => e.Tool, StringComparer.OrdinalIgnoreCase)
                    : entries.OrderByDescending(e => e.Tool, StringComparer.OrdinalIgnoreCase),
                "image" => _historySort.ascending
                    ? entries.OrderBy(e => e.Image, StringComparer.OrdinalIgnoreCase)
                    : entries.OrderByDescending(e => e.Image, StringComparer.OrdinalIgnoreCase),
                "duration" => _historySort.ascending
                    ? entries.OrderBy(e => e.DurationSeconds)
                    : entries.OrderByDescending(e => e.DurationSeconds),
                "ram" => _historySort.ascending
                    ? entries.OrderBy(e => e.PeakMemoryBytes ?? 0)
                    : entries.OrderByDescending(e => e.PeakMemoryBytes ?? 0),
                "cpu" => _historySort.ascending
                    ? entries.OrderBy(e => e.MaxCpuPercent ?? 0)
                    : entries.OrderByDescending(e => e.MaxCpuPercent ?? 0),
                _ => _historySort.ascending // "time"
                    ? entries.OrderBy(e => e.Timestamp)
                    : entries.OrderByDescending(e => e.Timestamp),
            };

            foreach (var entry in sortedEntries)
            {
                var statusLabel = entry.WasCancelled ? "CANCEL" : (entry.ExitCode == 0 ? "OK" : "FAIL");
                var statusColor = entry.WasCancelled ? YellowColor : (entry.ExitCode == 0 ? GreenColor : RedColor);

                // Parse timestamp for display (stored as ISO 8601 UTC)
                var timeLabel = "";
                if (DateTime.TryParse(entry.Timestamp, null, DateTimeStyles.RoundtripKind, out var ts))
                    timeLabel = ts.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

                var imageShort = Truncate(entry.Image ?? "", 24);

                // Format resource profile columns
                var peakRamLabel = entry.PeakMemoryBytes.HasValue
                    ? (entry.OomKilled ? "⚠️ " : "") + FormatBytes(entry.PeakMemoryBytes.Value)
                    : "—";
                var maxCpuLabel = entry.MaxCpuPercent.HasValue && entry.MaxCpuPercent.Value > 0.0
                    ? $"{entry.MaxCpuPercent.Value:F1}%"
                    : "—";
                var peakRamColor = entry.OomKilled ? RedColor : MutedColor;

                var rowGrid = CreateHistoryRow(
                    statusLabel, Truncate(entry.Tool ?? "unknown", 18), imageShort,
                    $"{entry.DurationSeconds:F2}s", peakRamLabel, maxCpuLabel, timeLabel,
                    isHeader: false, statusColor: statusColor, peakRamColor: peakRamColor);

                // Actions column — right-aligned buttons matching the Images table style
                var actionsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 4
                };

                // "Pin" button — copies image@sha256:digest reference for reproducibility
                if (!string.IsNullOrWhiteSpace(entry.ImageDigest))
                {
                    var pinRef = $"{entry.Image}@{entry.ImageDigest}";
                    Button? pinBtn = null;
                    pinBtn = new Button
                    {
                        Content = "Pin",
                        FontSize = 10,
                        Padding = new Thickness(8, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                        Command = new AsyncRelayCommand(async () =>
                        {
                            try
                            {
                                var topLevel = TopLevel.GetTopLevel(this);
                                if (topLevel?.Clipboard != null)
                                {
                                    await topLevel.Clipboard.SetTextAsync(pinRef);
                                    pinBtn!.Content = "Pinned!";
                                    _ = ResetButtonTextAsync(pinBtn!, "Pin", 2000);
                                }
                            }
                            catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.History", "ClipboardPin", ex); }
                        })
                    };
                    var shortDigest = entry.ImageDigest.Length > 19
                        ? string.Concat(entry.ImageDigest.AsSpan(7, 12), "...")
                        : entry.ImageDigest;
                    ToolTip.SetTip(pinBtn, $"Copy pinned reference: {entry.Image}@{shortDigest}");
                    actionsPanel.Children.Add(pinBtn);
                }

                // "Copy" button — copies the exact docker run command
                if (!string.IsNullOrWhiteSpace(entry.DockerRunCommand))
                {
                    var cmdText = entry.DockerRunCommand;
                    Button? copyBtn = null;
                    copyBtn = new Button
                    {
                        Content = "Copy",
                        FontSize = 10,
                        Padding = new Thickness(8, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                        Command = new AsyncRelayCommand(async () =>
                        {
                            try
                            {
                                var topLevel = TopLevel.GetTopLevel(this);
                                if (topLevel?.Clipboard != null)
                                {
                                    await topLevel.Clipboard.SetTextAsync(cmdText);
                                    copyBtn!.Content = "Copied!";
                                    _ = ResetButtonTextAsync(copyBtn!, "Copy", 2000);
                                }
                            }
                            catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.History", "ClipboardCopyRunCmd", ex); }
                        })
                    };
                    ToolTip.SetTip(copyBtn, "Copy the exact docker run command");
                    actionsPanel.Children.Add(copyBtn);
                }

                if (actionsPanel.Children.Count > 0)
                {
                    Grid.SetColumn(actionsPanel, 14);
                    rowGrid.Children.Add(actionsPanel);
                }

                _telemetryContent.Children.Add(rowGrid);
            }

            // Overflow indicator: show how many more entries exist beyond the displayed limit
            if (totalRuns > entries.Count)
            {
                _telemetryContent.Children.Add(new TextBlock
                {
                    Text = $"... and {totalRuns - entries.Count} more run(s) — Open Log to see all entries",
                    Foreground = MutedColor,
                    FontSize = 10,
                    FontStyle = FontStyle.Italic,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            var actionsRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

            actionsRow.Children.Add(CreateActionButton("Clear Recents", () =>
            {
                ContainerTelemetry.ClearEntries();
                PopulateTelemetry();
                return Task.CompletedTask;
            }, "Delete all recorded execution entries from the telemetry log"));

            actionsRow.Children.Add(CreateActionButton("Export Telemetry", () =>
            {
                var destPath = Path.Combine(
                    ResolveExportDirectory(),
                    $"container_telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
                var success = ContainerTelemetry.ExportTo(destPath);
                if (success)
                {
                    _telemetryContent.Children.Add(new TextBlock
                    {
                        Text = $"Exported to {destPath}",
                        FontSize = 10,
                        Foreground = GreenColor,
                        FontStyle = FontStyle.Italic,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }
                return Task.CompletedTask;
            }, "Export the full telemetry log as a .jsonl file"));

            _telemetryContent.Children.Add(actionsRow);
        }
        else if (totalRuns == 0)
        {
            _telemetryContent.Children.Add(new TextBlock
            {
                Text = "No recent executions recorded.",
                FontSize = 11,
                Foreground = MutedColor,
                FontStyle = FontStyle.Italic
            });
        }
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView.History", "PopulateTelemetryAsync", ex);
        }
    }

    /// <summary>Creates a 7-column grid row for the execution history table (status, tool, image, duration, peak RAM, max CPU, time).</summary>
    private static Grid CreateHistoryRow(string status, string tool, string image, string duration,
        string peakRam, string maxCpu, string time,
        bool isHeader, SolidColorBrush? statusColor = null, SolidColorBrush? peakRamColor = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("55,8,140,8,190,8,65,8,75,8,60,8,90,8,Auto"),
            Margin = new Thickness(0, isHeader ? 0 : 1)
        };

        AddGridCell(grid, 0, status, isHeader, isHeader ? AccentColor : (statusColor ?? FontColor));
        AddGridCell(grid, 2, tool, isHeader, isHeader ? AccentColor : FontColor);
        AddGridCell(grid, 4, image, isHeader, isHeader ? AccentColor : MutedColor);
        AddGridCell(grid, 6, duration, isHeader, isHeader ? AccentColor : MutedColor, HorizontalAlignment.Right);
        AddGridCell(grid, 8, peakRam, isHeader, isHeader ? AccentColor : (peakRamColor ?? MutedColor), HorizontalAlignment.Right);
        AddGridCell(grid, 10, maxCpu, isHeader, isHeader ? AccentColor : MutedColor, HorizontalAlignment.Right);
        AddGridCell(grid, 12, time, isHeader, isHeader ? AccentColor : MutedColor);
        if (isHeader) AddGridCell(grid, 14, "ACTIONS", true, AccentColor);

        return grid;
    }

    /// <summary>Creates a unicode sparkline string from a sequence of values.</summary>
    private static string CreateSparkline(IEnumerable<double> values)
    {
        var vals = values.ToList();
        if (vals.Count == 0) return "";
        var min = vals.Min();
        var max = vals.Max();
        string[] blocks = { " ", "▂", "▃", "▄", "▅", "▆", "▇", "█" };
        var sb = new System.Text.StringBuilder(vals.Count);
        foreach (var v in vals)
        {
            int idx = max == min ? 0 : (int)Math.Round((v - min) / (max - min) * (blocks.Length - 1));
            idx = Math.Max(0, Math.Min(blocks.Length - 1, idx));
            sb.Append(blocks[idx]);
        }
        return sb.ToString();
    }
}
