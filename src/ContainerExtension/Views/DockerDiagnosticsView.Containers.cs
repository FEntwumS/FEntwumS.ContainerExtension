#pragma warning disable MA0004, MA0006, S108
using static ContainerExtension.Views.UIBuilderHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using Avalonia.Threading;

namespace ContainerExtension.Views;

public partial class DockerDiagnosticsView
{
    private AsyncRelayCommand<string>? _logsCommand;
    private AsyncRelayCommand<string>? _stopCommand;
    private AsyncRelayCommand<string>? _startCommand;
    private AsyncRelayCommand<string>? _removeCommand;
    private AsyncRelayCommand<string>? _restartCommand;

    private void InitializeContainerCommands()
    {
        _logsCommand = new AsyncRelayCommand<string>(async (logContainerId) =>
        {
            if (string.IsNullOrEmpty(logContainerId)) return;
            if (IsDebounced($"logs_{logContainerId}", 800))
            {
                return;
            }

            if (_openLogWindows.TryGetValue(logContainerId, out var existingWindow))
            {
                if (existingWindow != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        existingWindow.Show();
                        existingWindow.Activate();
                        existingWindow.Focus();
                    });
                }
                return;
            }
            _openLogWindows[logContainerId] = null!;

            try
            {
                var logs = await _strategy.GetContainerLogsAsync(logContainerId);
                if (!_hasAttached)
                {
                    _openLogWindows.TryRemove(logContainerId, out _);
                    return;
                }

                string containerLabel = "";
                lock (_cachedDataLock)
                {
                    var c = _cachedContainers?.FirstOrDefault(x => string.Equals(x.ID, logContainerId, StringComparison.Ordinal));
                    if (c != null)
                    {
                        containerLabel = c.Names?.FirstOrDefault()?.TrimStart('/') ?? logContainerId.ShortId();
                    }
                }
                if (string.IsNullOrEmpty(containerLabel))
                {
                    containerLabel = logContainerId.ShortId();
                }

                var sanitizedLabel = new string(containerLabel.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
                if (string.IsNullOrEmpty(sanitizedLabel))
                {
                    sanitizedLabel = "container";
                }

                var formattedLogs = string.Empty;
                if (!string.IsNullOrWhiteSpace(logs))
                {
                    var sb = new StringBuilder();
                    using (var reader = new StringReader(logs))
                    {
                        string? l;
                        while ((l = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                        {
                            sb.AppendLine(AlignLogTimestamp(l));
                        }
                    }
                    formattedLogs = sb.ToString();
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var logTextBox = new TextBox
                    {
                        Text = "",
                        FontFamily = MonoFont,
                        FontSize = 11,
                        Foreground = FontColor,
                        TextWrapping = TextWrapping.Wrap,
                        IsReadOnly = true,
                        Margin = new Thickness(12),
                        AcceptsReturn = true
                    };
                    Avalonia.Input.DragDrop.SetAllowDrop(logTextBox, false);

                    var saveBtn = new Button
                    {
                        Content = "Save Logs",
                        FontSize = 10,
                        Padding = new Thickness(8, 4),
                        Margin = new Thickness(12, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    ToolTip.SetTip(saveBtn, "Save the full log output as a .txt file");

                    var scrollToEndBtn = new Button
                    {
                        Content = "Scroll to End ↓",
                        FontSize = 10,
                        Padding = new Thickness(8, 4),
                        Margin = new Thickness(12, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        IsVisible = false
                    };

                    var filterTextBox = new TextBox
                    {
                        Watermark = "Filter logs...",
                        Width = 200,
                        Margin = new Thickness(12, 4, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var detailsGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
                    var logWindow = new Window
                    {
                        Title = $"Container Logs — {containerLabel}",
                        Width = 700,
                        Height = 450,
                        Content = detailsGrid
                    };
                    var weakTextBox = new WeakReference<TextBox>(logTextBox);
                    var weakWindow = new WeakReference<Window>(logWindow);

                    var logLock = new System.Threading.Lock();
                    var logLines = new List<string>();
                    var filterText = "";

                    if (!string.IsNullOrWhiteSpace(formattedLogs))
                    {
                        var initialLines = formattedLogs.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                        foreach (var il in initialLines)
                        {
                            if (!string.IsNullOrEmpty(il))
                            {
                                var esc = System.Security.SecurityElement.Escape(il);
                                if (esc != null)
                                {
                                    logLines.Add(esc);
                                }
                            }
                        }
                    }

                    void UpdateLogUI()
                    {
                        string text;
                        lock (logLock)
                        {
                            if (string.IsNullOrEmpty(filterText))
                            {
                                text = logLines.Count == 0 ? "(no output)" : string.Join(Environment.NewLine, logLines);
                            }
                            else
                            {
                                var filtered = new List<string>(logLines.Count);
                                for (int i = 0; i < logLines.Count; i++)
                                {
                                    var line = logLines[i];
                                    if (line.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                                    {
                                        filtered.Add(line);
                                    }
                                }
                                text = filtered.Count == 0 ? "(no matching lines)" : string.Join(Environment.NewLine, filtered);
                            }
                        }

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                if (!weakWindow.TryGetTarget(out var w) || !w.IsVisible || !weakTextBox.TryGetTarget(out var tb) || tb.Parent == null)
                                {
                                    return;
                                }
                                var selectionStart = tb.SelectionStart;
                                var selectionEnd = tb.SelectionEnd;
                                var caretIndex = tb.CaretIndex;
                                var wasAtEnd = caretIndex >= (tb.Text?.Length ?? 0) - 2;

                                tb.Text = text;

                                if (!wasAtEnd || selectionStart != selectionEnd)
                                {
                                    var newLength = text.Length;
                                    var start = Math.Max(0, Math.Min(selectionStart, newLength));
                                    var end = Math.Max(0, Math.Min(selectionEnd, newLength));
                                    var caret = Math.Max(0, Math.Min(caretIndex, newLength));

                                    tb.SelectionStart = Math.Min(start, end);
                                    tb.SelectionEnd = Math.Max(start, end);
                                    tb.CaretIndex = caret;
                                }
                                else
                                {
                                    tb.CaretIndex = text.Length;
                                }
                            }
                            catch (ObjectDisposedException) { }
                            catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView", "UpdateLogUI", ex); }
                        });
                    }

                    UpdateLogUI();

                    filterTextBox.TextChanged += (sender, e) =>
                    {
                        var original = filterTextBox.Text ?? "";
                        var filtered = new string(original.Where(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)).ToArray());
                        if (original != filtered)
                        {
                            filterTextBox.Text = filtered;
                            filterTextBox.CaretIndex = filtered.Length;
                        }
                    };

                    filterTextBox.PropertyChanged += (sender, e) =>
                    {
                        if (e.Property == TextBox.TextProperty)
                        {
                            filterText = filterTextBox.Text ?? "";
                            UpdateLogUI();
                        }
                    };

                    scrollToEndBtn.Command = new RelayCommand(() =>
                    {
                        logTextBox.CaretIndex = logTextBox.Text?.Length ?? 0;
                        scrollToEndBtn.IsVisible = false;
                    });

                    logTextBox.PropertyChanged += (sender, e) =>
                    {
                        if (e.Property == TextBox.CaretIndexProperty)
                        {
                            var idx = logTextBox.CaretIndex;
                            var totalLen = logTextBox.Text?.Length ?? 0;
                            var isAtEnd = idx >= totalLen - 5;
                            scrollToEndBtn.IsVisible = !isAtEnd && totalLen > 0;
                        }
                    };

                    bool isSaving = false;
                    saveBtn.Command = new AsyncRelayCommand(async () =>
                    {
                        if (isSaving)
                        {
                            return;
                        }
                        isSaving = true;
                        var prevTip = ToolTip.GetTip(saveBtn);
                        try
                        {
                            saveBtn.Content = "Saving...";
                            saveBtn.IsEnabled = false;

                            var topLevel = TopLevel.GetTopLevel(logWindow);
                            if (topLevel?.StorageProvider != null)
                            {
                                var startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(Directory.GetCurrentDirectory())));
                                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                                {
                                    Title = "Save Container Logs",
                                    SuggestedFileName = $"container_logs_{sanitizedLabel}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.txt",
                                    SuggestedStartLocation = startFolder
                                });

                                if (file != null)
                                {
                                    var localPath = file.Path.LocalPath;
                                    if (string.IsNullOrEmpty(localPath) ||
                                        localPath.Contains("..", StringComparison.Ordinal) ||
                                        localPath.StartsWith("/System", StringComparison.OrdinalIgnoreCase) ||
                                        localPath.StartsWith("/etc", StringComparison.OrdinalIgnoreCase) ||
                                        localPath.StartsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
                                        localPath.StartsWith("/sbin", StringComparison.OrdinalIgnoreCase) ||
                                        localPath.Contains(":\\Windows", StringComparison.OrdinalIgnoreCase) ||
                                        localPath.Contains(":\\System32", StringComparison.OrdinalIgnoreCase))
                                    {
                                        throw new UnauthorizedAccessException("Saving logs to system directories is restricted for security.");
                                    }

                                    var liveLogText = logTextBox.Text ?? "";
                                    await Task.Run(async () =>
                                    {
                                        await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
                                        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                                        await writer.WriteAsync(liveLogText).ConfigureAwait(false);
                                    });

                                    saveBtn.Content = $"Saved ✓";
                                    return;
                                }
                            }

                            saveBtn.Content = "Save Logs";
                            saveBtn.IsEnabled = true;
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "LogsSaveToFile", ex);
                            saveBtn.Content = "Save failed ✗";
                            ToolTip.SetTip(saveBtn, $"Export failed: {ex.Message}");
                            await Task.Delay(3000);
                            saveBtn.Content = "Save Logs";
                            ToolTip.SetTip(saveBtn, prevTip);
                            saveBtn.IsEnabled = true;
                        }
                        finally
                        {
                            isSaving = false;
                        }
                    });

                    var copyMenuItem = new MenuItem { Header = "Copy" };
                    copyMenuItem.InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.C, OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control);
                    copyMenuItem.Command = new AsyncRelayCommand(async () =>
                    {
                        try
                        {
                            var selected = logTextBox.SelectedText;
                            if (!string.IsNullOrEmpty(selected))
                            {
                                var topLevel = TopLevel.GetTopLevel(logWindow);
                                if (topLevel?.Clipboard != null)
                                {
                                    await topLevel.Clipboard.SetTextAsync(selected);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "CopySelectedLogs", ex);
                        }
                    });

                    var copyAllMenuItem = new MenuItem { Header = "Copy All" };
                    copyAllMenuItem.InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.A, (OperatingSystem.IsMacOS() ? Avalonia.Input.KeyModifiers.Meta : Avalonia.Input.KeyModifiers.Control) | Avalonia.Input.KeyModifiers.Shift);
                    copyAllMenuItem.Command = new AsyncRelayCommand(async () =>
                    {
                        try
                        {
                            var topLevel = TopLevel.GetTopLevel(logWindow);
                            if (topLevel?.Clipboard != null)
                            {
                                await topLevel.Clipboard.SetTextAsync(logTextBox.Text);
                            }
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "CopyAllLogs", ex);
                        }
                    });

                    logTextBox.ContextMenu = new ContextMenu
                    {
                        ItemsSource = new List<MenuItem> { copyMenuItem, copyAllMenuItem }
                    };

                    var topPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(12, 4, 12, 4)
                    };
                    topPanel.Children.Add(saveBtn);
                    topPanel.Children.Add(scrollToEndBtn);

                    var filterLabel = new TextBlock
                    {
                        Text = "Filter:",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(24, 0, 0, 0),
                        FontSize = 11,
                        Foreground = MutedColor
                    };
                    topPanel.Children.Add(filterLabel);
                    topPanel.Children.Add(filterTextBox);

                    Grid.SetRow(topPanel, 0);
                    Grid.SetRow(logTextBox, 1);
                    detailsGrid.Children.Add(topPanel);
                    detailsGrid.Children.Add(logTextBox);

                    var cts = new System.Threading.CancellationTokenSource();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var needsUpdate = false;

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    while (!cts.IsCancellationRequested)
                                    {
                                        await Task.Delay(100, cts.Token).ConfigureAwait(false);
                                        bool shouldUpdate = false;
                                        lock (logLock)
                                        {
                                            if (needsUpdate)
                                            {
                                                shouldUpdate = true;
                                                needsUpdate = false;
                                            }
                                        }
                                        if (shouldUpdate)
                                        {
                                            UpdateLogUI();
                                        }
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                }
                            }, cts.Token);

                            var batch = new List<string>();
                            var lastBatchTime = Environment.TickCount64;

                            await foreach (var logLine in _strategy.StreamContainerLogsAsync(logContainerId, cts.Token).ConfigureAwait(false))
                            {
                                var alignedLine = AlignLogTimestamp(logLine);
                                var escapedLine = System.Security.SecurityElement.Escape(alignedLine);
                                batch.Add(escapedLine ?? string.Empty);

                                if (batch.Count >= 50 || (Environment.TickCount64 - lastBatchTime) > 100)
                                {
                                    lock (logLock)
                                    {
                                        logLines.AddRange(batch);
                                        if (logLines.Count > 2500)
                                        {
                                            logLines.RemoveRange(0, logLines.Count - 2000);
                                        }
                                        needsUpdate = true;
                                    }
                                    batch.Clear();
                                    lastBatchTime = Environment.TickCount64;
                                }
                            }

                            if (batch.Count > 0)
                            {
                                lock (logLock)
                                {
                                    logLines.AddRange(batch);
                                    if (logLines.Count > 2500)
                                    {
                                        logLines.RemoveRange(0, logLines.Count - 2000);
                                    }
                                    needsUpdate = true;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (weakTextBox.TryGetTarget(out var tb))
                                {
                                    tb.Text += $"\n[Log Stream Failed: {ex.Message}]";
                                }
                            });
                        }
                    });

                    logWindow.Closed += (_, _) =>
                    {
                        try
                        {
                            cts.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        _openLogWindows.TryRemove(logContainerId, out _);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(1000).ConfigureAwait(false);
                                cts.Dispose();
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        });
                    };
                    _openLogWindows[logContainerId] = logWindow;

                    var mainWindow = TopLevel.GetTopLevel(this) as Window;
                    if (mainWindow != null)
                    {
                        logWindow.Show(mainWindow);
                    }
                    else
                    {
                        logWindow.Show();
                    }
                });
            }
            catch
            {
                _openLogWindows.TryRemove(logContainerId, out _);
            }
        });

        _stopCommand = new AsyncRelayCommand<string>(async (containerId) =>
        {
            if (string.IsNullOrEmpty(containerId)) return;
            if (IsDebounced($"stop_{containerId}", 1000)) return;

            Grid? rowGrid = null;
            StackPanel? panel = null;
            Button? btn = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                rowGrid = _containersContent.Children.OfType<Grid>().FirstOrDefault(g => string.Equals(g.Tag as string, containerId, StringComparison.Ordinal));
                if (rowGrid != null)
                {
                    panel = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
                    if (panel != null)
                    {
                        btn = panel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, "stop", StringComparison.Ordinal));
                    }
                }
            });

            if (btn == null || panel == null) return;

            var prevTip = ToolTip.GetTip(btn);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    panel.IsEnabled = false;
                    var stopText = new TextBlock { Text = "Stopping...", VerticalAlignment = VerticalAlignment.Center };
                    var stopProgress = new ProgressBar { IsIndeterminate = true, Width = 40, Height = 4, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    var stopStack = new StackPanel { Orientation = Orientation.Horizontal };
                    stopStack.Children.Add(stopText);
                    stopStack.Children.Add(stopProgress);
                    btn.Content = stopStack;
                });

                await _strategy.StopContainerAsync(containerId);
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_StopContainer", ex);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!_hasAttached) return;
                    btn.Content = "Error ✗";
                    ToolTip.SetTip(btn, $"Failed to stop: {ex.Message}");
                    await Task.Delay(3000);
                    if (!_hasAttached) return;
                    btn.Content = "Stop";
                    ToolTip.SetTip(btn, prevTip);
                    panel.IsEnabled = true;
                });
            }
        });

        _startCommand = new AsyncRelayCommand<string>(async (containerId) =>
        {
            if (string.IsNullOrEmpty(containerId)) return;
            if (IsDebounced($"start_{containerId}", 1000)) return;

            Grid? rowGrid = null;
            StackPanel? panel = null;
            Button? btn = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                rowGrid = _containersContent.Children.OfType<Grid>().FirstOrDefault(g => string.Equals(g.Tag as string, containerId, StringComparison.Ordinal));
                if (rowGrid != null)
                {
                    panel = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
                    if (panel != null)
                    {
                        btn = panel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, "start", StringComparison.Ordinal));
                    }
                }
            });

            if (btn == null || panel == null) return;

            var prevTip = ToolTip.GetTip(btn);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    panel.IsEnabled = false;
                    var startText = new TextBlock { Text = "Starting...", VerticalAlignment = VerticalAlignment.Center };
                    var startProgress = new ProgressBar { IsIndeterminate = true, Width = 40, Height = 4, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    var startStack = new StackPanel { Orientation = Orientation.Horizontal };
                    startStack.Children.Add(startText);
                    startStack.Children.Add(startProgress);
                    btn.Content = startStack;
                });

                await _strategy.StartContainerAsync(containerId);
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_StartContainer", ex);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!_hasAttached) return;
                    btn.Content = "Error ✗";
                    ToolTip.SetTip(btn, $"Failed to start: {ex.Message}");
                    await Task.Delay(3000);
                    if (!_hasAttached) return;
                    btn.Content = "Start";
                    ToolTip.SetTip(btn, prevTip);
                    panel.IsEnabled = true;
                });
            }
        });

        _removeCommand = new AsyncRelayCommand<string>(async (containerId) =>
        {
            if (string.IsNullOrEmpty(containerId)) return;
            if (IsDebounced($"remove_{containerId}", 1000)) return;

            Grid? rowGrid = null;
            StackPanel? panel = null;
            Button? btn = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                rowGrid = _containersContent.Children.OfType<Grid>().FirstOrDefault(g => string.Equals(g.Tag as string, containerId, StringComparison.Ordinal));
                if (rowGrid != null)
                {
                    panel = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
                    if (panel != null)
                    {
                        btn = panel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, "remove", StringComparison.Ordinal));
                    }
                }
            });

            if (btn == null || panel == null) return;

            var prevTip = ToolTip.GetTip(btn);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    panel.IsEnabled = false;
                    var rmText = new TextBlock { Text = "Removing...", VerticalAlignment = VerticalAlignment.Center };
                    var rmProgress = new ProgressBar { IsIndeterminate = true, Width = 40, Height = 4, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    var rmStack = new StackPanel { Orientation = Orientation.Horizontal };
                    rmStack.Children.Add(rmText);
                    rmStack.Children.Add(rmProgress);
                    btn.Content = rmStack;
                });

                await _strategy.RemoveContainerAsync(containerId);
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_RemoveContainer", ex);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (!_hasAttached) return;
                    btn.Content = "Error ✗";
                    ToolTip.SetTip(btn, $"Failed to remove: {ex.Message}");
                    await Task.Delay(3000);
                    if (!_hasAttached) return;
                    btn.Content = "Remove";
                    ToolTip.SetTip(btn, prevTip);
                    panel.IsEnabled = true;
                });
            }
        });

        _restartCommand = new AsyncRelayCommand<string>(async (containerId) =>
        {
            if (string.IsNullOrEmpty(containerId)) return;
            if (IsDebounced($"restart_{containerId}", 1000)) return;
            try
            {
                await _strategy.Client.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters { WaitBeforeKillSeconds = 5 });
                await RefreshAllAsync();
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Context_RestartContainer", ex);
            }
        });
    }

    // ── Debouncer and Stats Cache State (F14, F15) ──────────────────────
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastActionTimes = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _liveStats = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeStatsQueries = new(StringComparer.Ordinal);

    private bool IsDebounced(string actionKey, int ms = 500)
    {
        var now = Environment.TickCount64;
        var last = _lastActionTimes.GetOrAdd(actionKey, now);
        if (last != now && (now - last) < ms)
        {
            return true;
        }
        _lastActionTimes[actionKey] = now;
        return false;
    }

    private sealed class StatelessProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private void QueryContainerStats(string containerId)
    {
        if (!_activeStatsQueries.TryAdd(containerId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var progress = new StatelessProgress<ContainerStatsResponse>(stats =>
                {
                    if (stats != null)
                    {
                        var mem = stats.MemoryStats?.Usage ?? 0;
                        var cpu = 0.0;
                        if (stats.CPUStats != null && stats.PreCPUStats != null)
                        {
                            var cpuDelta = (double)(stats.CPUStats.CPUUsage?.TotalUsage ?? 0UL) - (stats.PreCPUStats.CPUUsage?.TotalUsage ?? 0UL);
                            var systemDelta = (double)stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage;
                            if (systemDelta > 0 && stats.CPUStats.OnlineCPUs > 0)
                            {
                                cpu = (cpuDelta / systemDelta) * stats.CPUStats.OnlineCPUs * 100.0;
                            }
                        }
                        var ramStr = FormatBytes((long)mem);
                        var statsText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{cpu:F1}% CPU / {ramStr}");
                        _liveStats[containerId] = statsText;

                        UpdateContainerStatsUI(containerId, $" ({statsText})", isRunning: true);
                    }
                });

                await _strategy.Client.Containers.GetContainerStatsAsync(
                    containerId,
                    new ContainerStatsParameters { Stream = false },
                    progress,
                    cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Safely ignore exception: best-effort container stats gathering failed or was cancelled
                System.Diagnostics.Debug.WriteLine($"Stats collection failed: {ex.Message}");
            }
            finally
            {
                _activeStatsQueries.TryRemove(containerId, out _);
            }
        });
    }

    private void UpdateContainerStatsUI(string containerId, string statsStr, bool isRunning)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_hasAttached) return;

            var rowGrid = _containersContent.Children
                .OfType<Grid>()
                .FirstOrDefault(g => string.Equals(g.Tag as string, containerId, StringComparison.Ordinal));

            if (rowGrid != null)
            {
                Docker.DotNet.Models.ContainerListResponse? container = null;
                lock (_cachedDataLock)
                {
                    container = _cachedContainers?.FirstOrDefault(c => string.Equals(c.ID, containerId, StringComparison.Ordinal));
                }

                if (container != null)
                {
                    var statusColor = isRunning ? GreenColor : (string.Equals(container.State, "exited", StringComparison.Ordinal) ? MutedColor : YellowColor);
                    var status = Truncate((container.Status ?? container.State ?? "unknown") + statsStr, 35);
                    AddGridCell(rowGrid, 4, status, isHeader: false, statusColor);
                }
            }
        });
    }

    private void OnContainerSort(string key)
    {
        ToggleSort(ref _containerSort, key);
        IList<Docker.DotNet.Models.ContainerListResponse> localContainers;
        lock (_cachedDataLock)
        {
            localContainers = _cachedContainers;
        }
        PopulateContainers(localContainers);
    }

    private void PopulateContainers(IList<Docker.DotNet.Models.ContainerListResponse> containers)
    {
        lock (_cachedDataLock)
        {
            _cachedContainers = containers;
            foreach (var child in _containersContent.Children)
            {
                if (child is Grid grid && grid.Margin == RowMargin)
                {
                    _recycledContainerRows.Add(grid);
                }
            }
            if (_recycledContainerRows.Count > 100)
            {
                _recycledContainerRows.RemoveRange(100, _recycledContainerRows.Count - 100);
            }
        }
        _containersContent.Children.Clear();
        var newChildren = new List<Control>(containers.Count * 2);

        if (containers.Count == 0)
        {
            newChildren.Add(new TextBlock
            {
                Text = "No containers found.",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
            _containersContent.Children.AddRange(newChildren);
            return;
        }

        IList<Docker.DotNet.Models.ContainerListResponse> searchable;
        if (string.IsNullOrEmpty(_searchFilter))
        {
            searchable = containers;
        }
        else
        {
            var filtered = new List<Docker.DotNet.Models.ContainerListResponse>(containers.Count);
            for (int i = 0; i < containers.Count; i++)
            {
                var c = containers[i];
                var match = false;
                if (c.Names != null)
                {
                    for (int j = 0; j < c.Names.Count; j++)
                    {
                        if (c.Names[j] != null && c.Names[j].Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                            break;
                        }
                    }
                }
                if (!match && c.Image != null && c.Image.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                }
                if (!match)
                {
                    var status = c.Status ?? c.State ?? "";
                    if (status.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                    }
                }
                if (match)
                {
                    filtered.Add(c);
                }
            }
            searchable = filtered;
        }

        if (searchable.Count == 0)
        {
            newChildren.Add(new TextBlock
            {
                Text = $"No containers matching \"{Truncate(_searchFilter, 20)}\".",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
            _containersContent.Children.AddRange(newChildren);
            return;
        }

        newChildren.Add(CreateSortableHeaderRow(
          [("NAME", "name"), ("IMAGE", "image"), ("STATUS", "status")],
          _containerSort,
          OnContainerSort,
          "160,8,180,8,150,8,Auto",
          ThreeColumnIndices));
        newChildren.Add(CreateSeparator());

        var sorted = _containerSort.column switch
        {
            "image" => _containerSort.ascending
              ? searchable.OrderBy(c => c.Image, StringComparer.OrdinalIgnoreCase)
              : searchable.OrderByDescending(c => c.Image, StringComparer.OrdinalIgnoreCase),
            "status" => _containerSort.ascending
              ? searchable.OrderBy(c => c.Status ?? c.State ?? "", StringComparer.OrdinalIgnoreCase)
              : searchable.OrderByDescending(c => c.Status ?? c.State ?? "", StringComparer.OrdinalIgnoreCase),
            _ => _containerSort.ascending
              ? searchable.OrderBy(c => c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID.ShortId(), StringComparer.OrdinalIgnoreCase)
              : searchable.OrderByDescending(c => c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID.ShortId(), StringComparer.OrdinalIgnoreCase),
        };

        var itemsToShow = _showAllContainers ? sorted : sorted.Take(15);
        foreach (var c in itemsToShow)
        {
            var name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID.ShortId();
            var image = Truncate(c.Image, 30);
            var isRunning = c.State?.Equals("running", StringComparison.OrdinalIgnoreCase) ?? false;

            // Show live container CPU/RAM stats tags dynamically (F162)
            var statsStr = "";
            if (isRunning)
            {
                if (_liveStats.TryGetValue(c.ID, out var st))
                {
                    statsStr = $" ({st})";
                }
                else
                {
                    QueryContainerStats(c.ID);
                }
            }

            var status = Truncate((c.Status ?? c.State ?? "unknown") + statsStr, 35);
            var statusColor = isRunning ? GreenColor : (string.Equals(c.State, "exited", StringComparison.Ordinal) ? MutedColor : YellowColor);

            Grid? existingGrid = null;
            lock (_cachedDataLock)
            {
                if (_recycledContainerRows.Count > 0)
                {
                    existingGrid = _recycledContainerRows[^1];
                    _recycledContainerRows.RemoveAt(_recycledContainerRows.Count - 1);
                }
            }

            var row = CreateContainerRow(Truncate(name, 20), image, status, isHeader: false, statusColor: statusColor, existingGrid: existingGrid);
            row.Tag = c.ID;

            var btnPanel = row.Children.OfType<StackPanel>().FirstOrDefault();
            if (btnPanel == null)
            {
                btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
                Grid.SetColumn(btnPanel, 6);
                row.Children.Add(btnPanel);
            }

            // Detailed visual network diagram representing container ports mappings (F154)
            var diagram = BuildNetworkDiagram(c.Ports);
            var rowToolTip = new TextBlock
            {
                Text = $"Container: {name}\nState: {c.State}\n{diagram}",
                FontFamily = MonoFont,
                FontSize = 11
            };
            ToolTip.SetTip(row, rowToolTip);

            var logsBtn = btnPanel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, "logs", StringComparison.Ordinal));
            if (logsBtn == null)
            {
                logsBtn = new Button
                {
                    Tag = "logs",
                    Content = "Logs",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnPanel.Children.Add(logsBtn);
            }
            logsBtn.Command ??= _logsCommand;
            logsBtn.CommandParameter = c.ID;
            ToolTip.SetTip(logsBtn, "View the stdout/stderr output of this container");

            var stopBtn = btnPanel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, "stop", StringComparison.Ordinal));
            if (stopBtn == null)
            {
                stopBtn = new Button
                {
                    Tag = "stop",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnPanel.Children.Add(stopBtn);
            }
            stopBtn.Command ??= _stopCommand;
            stopBtn.CommandParameter = c.ID;
            stopBtn.Content = "Stop";
            stopBtn.IsEnabled = true;
            ToolTip.SetTip(stopBtn, "Send a graceful stop signal to this running container");
            stopBtn.IsVisible = isRunning;

            var startBtn = btnPanel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, "start", StringComparison.Ordinal));
            if (startBtn == null)
            {
                startBtn = new Button
                {
                    Tag = "start",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnPanel.Children.Add(startBtn);
            }
            startBtn.Command ??= _startCommand;
            startBtn.CommandParameter = c.ID;
            startBtn.Content = "Start";
            startBtn.IsEnabled = true;
            ToolTip.SetTip(startBtn, "Restart this stopped container");
            startBtn.IsVisible = !isRunning;

            var removeBtn = btnPanel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, "remove", StringComparison.Ordinal));
            if (removeBtn == null)
            {
                removeBtn = new Button
                {
                    Tag = "remove",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                btnPanel.Children.Add(removeBtn);
            }
            removeBtn.Command ??= _removeCommand;
            removeBtn.CommandParameter = c.ID;
            removeBtn.Content = "Remove";
            removeBtn.IsEnabled = true;
            ToolTip.SetTip(removeBtn, "Delete this stopped container and free its resources");
            removeBtn.IsVisible = !isRunning;

            // Add right-click context menu options to start, stop, restart, delete, and view logs (F163)
            var contextMenu = new ContextMenu();
            var menuItems = new List<MenuItem>();

            if (isRunning)
            {
                var stopItem = new MenuItem { Header = "Stop Container" };
                stopItem.Command = stopBtn.Command;
                stopItem.CommandParameter = c.ID;
                menuItems.Add(stopItem);

                var restartItem = new MenuItem { Header = "Restart Container" };
                restartItem.Command = _restartCommand;
                restartItem.CommandParameter = c.ID;
                menuItems.Add(restartItem);
            }
            else
            {
                var startItem = new MenuItem { Header = "Start Container" };
                startItem.Command = startBtn.Command;
                startItem.CommandParameter = c.ID;
                menuItems.Add(startItem);

                var removeItem = new MenuItem { Header = "Remove Container" };
                removeItem.Command = removeBtn.Command;
                removeItem.CommandParameter = c.ID;
                menuItems.Add(removeItem);
            }

            var logsItem = new MenuItem { Header = "View Logs" };
            logsItem.Command = logsBtn.Command;
            logsItem.CommandParameter = c.ID;
            menuItems.Add(logsItem);

            contextMenu.ItemsSource = menuItems;
            row.ContextMenu = contextMenu;

            newChildren.Add(row);
        }

        if (!_showAllContainers && searchable.Count > 15)
        {
            var remaining = searchable.Count - 15;
            var showAllBtn = new Button
            {
                Content = $"... and {remaining} more (click to show all)",
                Foreground = MutedColor,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Command = new RelayCommand(() =>
                {
                    _showAllContainers = true;
                    PopulateContainers(_cachedContainers);
                })
            };
            newChildren.Add(showAllBtn);
        }
        else if (_showAllContainers && searchable.Count > 15)
        {
            var showLessBtn = new Button
            {
                Content = "Show less",
                Foreground = MutedColor,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Command = new RelayCommand(() =>
                {
                    _showAllContainers = false;
                    PopulateContainers(_cachedContainers);
                })
            };
            newChildren.Add(showLessBtn);
        }

        _containersContent.Children.AddRange(newChildren);
    }

    private static Grid CreateContainerRow(string name, string image, string status,
      bool isHeader, IBrush? statusColor = null, Grid? existingGrid = null)
    {
        var grid = existingGrid ?? new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("160,8,180,8,150,8,Auto"),
            Margin = new Thickness(0, isHeader ? 0 : 1)
        };

        AddGridCell(grid, 0, name, isHeader, isHeader ? AccentColor : FontColor);
        AddGridCell(grid, 2, image, isHeader, isHeader ? AccentColor : MutedColor);
        AddGridCell(grid, 4, status, isHeader, isHeader ? AccentColor : (statusColor ?? MutedColor));

        return grid;
    }

    private static string AlignLogTimestamp(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length < 19)
        {
            return line;
        }

        // Fast-path: quick pattern check for "yyyy-MM-dd" to skip non-timestamp lines instantly
        if (char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]) && char.IsDigit(line[3]) &&
            line[4] == '-' &&
            char.IsDigit(line[5]) && char.IsDigit(line[6]) &&
            line[7] == '-' &&
            char.IsDigit(line[8]) && char.IsDigit(line[9]))
        {
            int firstSpace = line.IndexOf(' ');
            if (firstSpace > 10 && firstSpace < 35)
            {
                var potentialTs = line.AsSpan(0, firstSpace);
                if (DateTime.TryParse(potentialTs, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                {
                    var formattedTs = dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.CurrentCulture);
                    return $"[{formattedTs}] {line.Substring(firstSpace + 1)}";
                }
            }
        }
        return line;
    }

    private static string BuildNetworkDiagram(IList<Docker.DotNet.Models.Port> ports)
    {
        if (ports == null || ports.Count == 0) return "No active port mappings.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("╔═══════════════════════════════════════════╗");
        sb.AppendLine("║          PORT MAPPING DIAGRAM             ║");
        sb.AppendLine("╠═══════════════════════════════════════════╣");
        foreach (var p in ports)
        {
            if (p.PublicPort != 0)
            {
                var hostStr = $"{p.IP}:{p.PublicPort}".PadRight(18);
                var containerStr = $"{p.PrivatePort}/{p.Type}".PadLeft(10);
                sb.AppendLine($"║  [Host] {hostStr} ──► [Container] {containerStr}  ║");
            }
            else
            {
                sb.AppendLine($"║  [Container Exposure] {p.PrivatePort}/{p.Type}".PadRight(42) + "║");
            }
        }
        sb.AppendLine("╚═══════════════════════════════════════════╝");
        return sb.ToString();
    }
}
