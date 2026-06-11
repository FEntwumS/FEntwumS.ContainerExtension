#pragma warning disable MA0004
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
/// <see cref="PopulateTelemetryAsync"/> and <see cref="CreateHistoryRow"/>.
/// </summary>
public partial class DockerDiagnosticsView
{
    private int _lastTelemetryFingerprint;
    private int _currentTelemetryToken;

    /// <summary>Populates the Execution History section with a tabular display of the last 10 telemetry entries and aggregate stats.</summary>
    private async Task PopulateTelemetryAsync()
    {
        var localToken = System.Threading.Interlocked.Increment(ref _currentTelemetryToken);
        try
        {
            // Check if telemetry is disabled via the Retention = None setting
            var retentionStr = _settingsService.SafeGetSetting(ContainerExtensionModule.TelemetryRetentionSetting, "100");

            // Run I/O intensive operation on a background thread to prevent UI freezing
            var (entries, totalRuns, successRate, avgDuration) = await Task.Run(() => ContainerTelemetry.GetRecentEntriesWithStats(50)).ConfigureAwait(false);

            if (System.Threading.Volatile.Read(ref _currentTelemetryToken) != localToken)
            {
                return;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_hasAttached || System.Threading.Volatile.Read(ref _currentTelemetryToken) != localToken)
                {
                    return;
                }
                try
                {
                    // Compute a simple fingerprint to prevent layout thrashing on every tick
                    var fp = HashCode.Combine(totalRuns, _searchFilter ?? string.Empty, retentionStr);
                    if (entries.Count > 0)
                    {
                        fp = HashCode.Combine(fp, entries[0].Timestamp);
                    }

                    if (fp == _lastTelemetryFingerprint)
                    {
                        return;
                    }
                    _lastTelemetryFingerprint = fp;

                    _telemetryContent.Children.Clear();
                    var newChildren = new List<Control>(entries.Count * 2);

                    if (string.Equals(retentionStr, "None", StringComparison.Ordinal))
                    {
                        newChildren.Add(new TextBlock
                        {
                            Text = "Telemetry is disabled (Retention = None). Change the Telemetry Retention setting to enable execution history.",
                            Foreground = YellowColor,
                            FontSize = 11,
                            FontStyle = FontStyle.Italic,
                            TextWrapping = TextWrapping.Wrap
                        });
                        _telemetryContent.Children.AddRange(newChildren);
                        return;
                    }

                    var statsRow = BuildTelemetryStatsRow(totalRuns, successRate, avgDuration);
                    if (statsRow != null)
                    {
                        newChildren.Add(statsRow);
                    }

                    if (entries.Count == 0 && totalRuns == 0)
                    {
                        newChildren.Add(new TextBlock
                        {
                            Text = "No executions recorded yet. Run a tool to see history here.",
                            Foreground = MutedColor,
                            FontSize = 11,
                            FontStyle = FontStyle.Italic,
                            TextWrapping = TextWrapping.Wrap
                        });
                        _telemetryContent.Children.AddRange(newChildren);
                        return;
                    }

                    // Apply global search filter
                    if (!string.IsNullOrEmpty(_searchFilter))
                    {
                        entries.RemoveAll(e =>
                            !((e.Tool?.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                               (e.Image?.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        );
                    }

                    if (entries.Count > 0)
                    {
                        newChildren.Add(BuildHistoricalTrendsPanel(entries));

                        // Sortable header row
                        newChildren.Add(CreateSortableHeaderRow(
                            [("STATUS", "status"), ("TOOL", "tool"), ("IMAGE", "image"), ("DURATION", "duration"), ("PEAK RAM", "ram"), ("MAX CPU", "cpu"), ("TIME", "time")],
                            _historySort,
                            key => { ToggleSort(ref _historySort, key); _ = PopulateTelemetryAsync(); },
                            "55,8,140,8,190,8,65,8,75,8,60,8,90,8,Auto",
                            SevenColumnIndices,
                            "ACTIONS", 14));

                        newChildren.Add(CreateSeparator());

                        var sortedEntries = SortTelemetryEntries(entries, _historySort);

                        foreach (var entry in sortedEntries)
                        {
                            newChildren.Add(BuildTelemetryEntryRow(entry));
                        }

                        if (totalRuns > entries.Count)
                        {
                            newChildren.Add(new TextBlock
                            {
                                Text = $"... and {totalRuns - entries.Count} more run(s) — Open Log to see all entries",
                                Foreground = MutedColor,
                                FontSize = 10,
                                FontStyle = FontStyle.Italic,
                                Margin = new Thickness(0, 2, 0, 0),
                                TextWrapping = TextWrapping.Wrap
                            });
                        }

                        newChildren.Add(BuildTelemetryActionsRow());
                    }
                    else if (totalRuns == 0)
                    {
                        newChildren.Add(new TextBlock
                        {
                            Text = "No recent executions recorded.",
                            FontSize = 11,
                            Foreground = MutedColor,
                            FontStyle = FontStyle.Italic,
                            TextWrapping = TextWrapping.Wrap
                        });
                    }

                    _telemetryContent.Children.AddRange(newChildren);
                }
                catch (Exception ex)
                {
                    ContainerTelemetry.TrackError("DockerDiagnosticsView.History", "PopulateTelemetryAsync.UIThread", ex);
                }
            });
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView.History", "PopulateTelemetryAsync", ex);
        }
    }

    private StackPanel? BuildTelemetryStatsRow(int totalRuns, double successRate, double avgDuration)
    {
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
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
        }

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
            return statsRow;
        }
        return null;
    }

    private StackPanel BuildHistoricalTrendsPanel(List<ContainerTelemetry.TelemetryEntry> entries)
    {
        int cpuCount = 0;
        int ramCount = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].MaxCpuPercent.HasValue)
            {
                cpuCount++;
            }
            if (entries[i].PeakMemoryBytes.HasValue)
            {
                ramCount++;
            }
        }

        double[] cpuValues = cpuCount > 0 ? System.Buffers.ArrayPool<double>.Shared.Rent(cpuCount) : [];
        double[] ramValues = ramCount > 0 ? System.Buffers.ArrayPool<double>.Shared.Rent(ramCount) : [];

        try
        {
            int cpuIdx = cpuCount - 1;
            int ramIdx = ramCount - 1;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.MaxCpuPercent.HasValue && cpuIdx >= 0)
                {
                    cpuValues[cpuIdx--] = entry.MaxCpuPercent.Value;
                }
                if (entry.PeakMemoryBytes.HasValue && ramIdx >= 0)
                {
                    ramValues[ramIdx--] = (double)entry.PeakMemoryBytes.Value;
                }
            }

            var trendsPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, Margin = new Thickness(0, 4, 0, 12) };
            trendsPanel.Children.Add(new TextBlock { Text = "HISTORICAL RESOURCE TRENDS", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = AccentColor, TextWrapping = TextWrapping.Wrap });

            var trendsData = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            if (cpuCount > 0)
            {
                double cpuSum = 0;
                for (int i = 0; i < cpuCount; i++)
                {
                    cpuSum += cpuValues[i];
                }
                double cpuAvg = cpuSum / cpuCount;
                trendsData.Children.Add(new TextBlock { Text = $"CPU: {cpuAvg:F1}% avg  [{CreateSparkline(cpuValues, cpuCount)}]", FontFamily = MonoFont, FontSize = 11, Foreground = FontColor, TextWrapping = TextWrapping.Wrap });
            }
            if (ramCount > 0)
            {
                double ramMax = double.MinValue;
                for (int i = 0; i < ramCount; i++)
                {
                    if (ramValues[i] > ramMax)
                    {
                        ramMax = ramValues[i];
                    }
                }
                trendsData.Children.Add(new TextBlock { Text = $"RAM: {FormatBytes((long)ramMax)} peak  [{CreateSparkline(ramValues, ramCount)}]", FontFamily = MonoFont, FontSize = 11, Foreground = FontColor, TextWrapping = TextWrapping.Wrap });
            }

            if (trendsData.Children.Count > 0)
            {
                trendsPanel.Children.Add(trendsData);
            }
            else
            {
                trendsPanel.Children.Add(new TextBlock { Text = "No Trend Data", FontStyle = FontStyle.Italic, FontSize = 11, Foreground = MutedColor });
            }

            return trendsPanel;
        }
        finally
        {
            if (cpuCount > 0)
            {
                System.Buffers.ArrayPool<double>.Shared.Return(cpuValues);
            }
            if (ramCount > 0)
            {
                System.Buffers.ArrayPool<double>.Shared.Return(ramValues);
            }
        }
    }

    private static IEnumerable<ContainerTelemetry.TelemetryEntry> SortTelemetryEntries(List<ContainerTelemetry.TelemetryEntry> entries, (string column, bool ascending) sort)
    {
        return sort.column switch
        {
            "status" => sort.ascending
              ? entries.OrderBy(e => e.WasCancelled ? 1 : (e.ExitCode == 0 ? 0 : 2))
              : entries.OrderByDescending(e => e.WasCancelled ? 1 : (e.ExitCode == 0 ? 0 : 2)),
            "tool" => sort.ascending
              ? entries.OrderBy(e => e.Tool, StringComparer.OrdinalIgnoreCase)
              : entries.OrderByDescending(e => e.Tool, StringComparer.OrdinalIgnoreCase),
            "image" => sort.ascending
              ? entries.OrderBy(e => e.Image, StringComparer.OrdinalIgnoreCase)
              : entries.OrderByDescending(e => e.Image, StringComparer.OrdinalIgnoreCase),
            "duration" => sort.ascending
              ? entries.OrderBy(e => e.DurationSeconds)
              : entries.OrderByDescending(e => e.DurationSeconds),
            "ram" => sort.ascending
              ? entries.OrderBy(e => e.PeakMemoryBytes ?? 0)
              : entries.OrderByDescending(e => e.PeakMemoryBytes ?? 0),
            "cpu" => sort.ascending
              ? entries.OrderBy(e => e.MaxCpuPercent ?? 0)
              : entries.OrderByDescending(e => e.MaxCpuPercent ?? 0),
            _ => sort.ascending // "time"
                      ? entries.OrderBy(e => e.Timestamp)
              : entries.OrderByDescending(e => e.Timestamp),
        };
    }

    private Grid BuildTelemetryEntryRow(ContainerTelemetry.TelemetryEntry entry)
    {
        var statusLabel = entry.WasCancelled ? "CANCEL" : (entry.ExitCode == 0 ? "OK" : "FAIL");
        var statusColor = entry.WasCancelled ? YellowColor : (entry.ExitCode == 0 ? GreenColor : RedColor);

        var timeLabel = entry.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        var imageShort = Truncate(entry.Image ?? "", 24);

        var peakRamLabel = entry.PeakMemoryBytes.HasValue
          ? (entry.OomKilled ? "⚠️ " : "") + FormatBytes(entry.PeakMemoryBytes.Value)
          : "—";
        var maxCpuLabel = entry.MaxCpuPercent.HasValue && entry.MaxCpuPercent.Value > 0.0
          ? $"{entry.MaxCpuPercent.Value:F1}%"
          : "—";
        var peakRamColor = entry.OomKilled ? RedColor : MutedColor;

        var rowGrid = CreateHistoryRow(
          statusLabel, Truncate(entry.Tool ?? "unknown", 18), imageShort,
          FormatDuration(entry.DurationSeconds), peakRamLabel, maxCpuLabel, timeLabel,
          isHeader: false, statusColor: statusColor, peakRamColor: peakRamColor);

        var actionsPanel = BuildTelemetryRowActionsPanel(entry);
        if (actionsPanel.Children.Count > 0)
        {
            Grid.SetColumn(actionsPanel, 14);
            rowGrid.Children.Add(actionsPanel);
        }

        return rowGrid;
    }

    private StackPanel BuildTelemetryRowActionsPanel(ContainerTelemetry.TelemetryEntry entry)
    {
        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 4
        };

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

        if (!string.IsNullOrWhiteSpace(entry.DockerRunCommand))
        {
            var cmdText = entry.DockerRunCommand;
            try
            {
                var home = CachedUserProfile;
                if (!string.IsNullOrEmpty(home))
                {
                    cmdText = cmdText.Replace(home, "~", StringComparison.Ordinal);
                }
            }
            catch (Exception ex)
            {
                // Ignored to prevent clipboard failures from interrupting operation
                _ = ex;
            }

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
            var escapedCmd = cmdText.Replace("\"", "'", StringComparison.Ordinal);
            if (escapedCmd.Length > 200)
            {
                escapedCmd = string.Concat(escapedCmd.AsSpan(0, 200), "...");
            }
            ToolTip.SetTip(copyBtn, $"Copy Docker Run: {escapedCmd}");
            actionsPanel.Children.Add(copyBtn);
        }

        return actionsPanel;
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "0s";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}m {t.Seconds}s");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{seconds:F1}s");
    }

    private WrapPanel BuildTelemetryActionsRow()
    {
        var actionsRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        actionsRow.Children.Add(CreateActionButton("Clear Recents", () =>
        {
            ContainerTelemetry.ClearEntries();
            _ = PopulateTelemetryAsync();
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
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            return Task.CompletedTask;
        }, "Export the full telemetry log as a .jsonl file"));

        return actionsRow;
    }

    /// <summary>Creates a 7-column grid row for the execution history table (status, tool, image, duration, peak RAM, max CPU, time).</summary>
    private static Grid CreateHistoryRow(string status, string tool, string image, string duration,
    string peakRam, string maxCpu, string time,
    bool isHeader, IBrush? statusColor = null, IBrush? peakRamColor = null)
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
        if (isHeader)
        {
            AddGridCell(grid, 14, "ACTIONS", true, AccentColor);
        }

        return grid;
    }
}
