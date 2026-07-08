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

/// <summary>
/// Partial class containing the Container Lifecycle and Inspection logic:
/// Real-time monitoring, log tailing, container stopping/removing, and port mapping visualization.
/// </summary>
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
                    bool success = false;
                    try
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            existingWindow.Show();
                            existingWindow.Activate();
                            existingWindow.Focus();
                        });
                        success = true;
                    }
                    catch (Exception ex)
                    {
                        ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "LogsCommand_ReopenDisposedWindow", ex);
                        _openLogWindows.TryRemove(logContainerId, out _);
                    }
                    if (success)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }
            // Atomically claim the slot so a rapid double-click cannot open two windows for the same
            // container. The check above already focuses an already-open window; a failed claim here
            // means another click is mid-open, so this one bails rather than leaking a second window.
            if (!_openLogWindows.TryAdd(logContainerId, null!))
            {
                return;
            }

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
                    Avalonia.Automation.AutomationProperties.SetName(saveBtn, "Save container logs to file");

                    var scrollToEndBtn = new Button
                    {
                        Content = "Scroll to End",
                        FontSize = 10,
                        Padding = new Thickness(8, 4),
                        Margin = new Thickness(12, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        IsVisible = false
                    };
                    Avalonia.Automation.AutomationProperties.SetName(scrollToEndBtn, "Scroll to end of logs");

                    var filterTextBox = new TextBox
                    {
                        Watermark = "Filter logs...",
                        Width = 200,
                        Margin = new Thickness(12, 4, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Avalonia.Automation.AutomationProperties.SetName(filterTextBox, "Filter log lines");

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
                                logLines.Add(il);
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

                                    saveBtn.Content = "Saved";
                                    saveBtn.IsEnabled = true; // re-enable on success too, not only on cancel/error
                                    return;
                                }
                            }

                            saveBtn.Content = "Save Logs";
                            saveBtn.IsEnabled = true;
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "LogsSaveToFile", ex);
                            saveBtn.Content = "Save failed";
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
                    try
                    {
                        _ = Task.Run(async () =>
                        {
                            Task? updateTask = null;
                            try
                            {
                                var needsUpdate = false;

                                updateTask = Task.Run(async () =>
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
                                    catch (ObjectDisposedException)
                                    {
                                    }
                                }, cts.Token);

                                var batch = new List<string>();
                                var lastBatchTime = Environment.TickCount64;

                                await foreach (var logLine in _strategy.StreamContainerLogsAsync(logContainerId, cts.Token).ConfigureAwait(false))
                                {
                                    var alignedLine = AlignLogTimestamp(logLine);
                                    batch.Add(alignedLine ?? string.Empty);

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
                            finally
                            {
                                try
                                {
                                    await cts.CancelAsync().ConfigureAwait(false);
                                }
                                catch (ObjectDisposedException)
                                {
                                }
                                if (updateTask != null)
                                {
                                    try
                                    {
                                        await updateTask.ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                        // Ignore update task completion errors
                                    }
                                }
                                try
                                {
                                    cts.Dispose();
                                }
                                catch (ObjectDisposedException)
                                {
                                }
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
                                // Already disposed, ignore.
                            }
                            _openLogWindows.TryRemove(logContainerId, out _);
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
                    }
                    catch (Exception)
                    {
                        try { cts.Cancel(); } catch { /* Ignore cancellation errors during fallback cleanup */ }
                        try { cts.Dispose(); } catch { /* Ignore disposal errors during fallback cleanup */ }
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "LogsCommand", ex);
                _openLogWindows.TryRemove(logContainerId, out _);
                ShowTemporaryError($"Failed to load logs for container '{GetContainerDisplayName(logContainerId)}'", ex);
            }
        });

        _stopCommand = new AsyncRelayCommand<string>(containerId =>
            RunContainerActionAsync(containerId, "stop", "Stop", "Stopping", "stopped", id => _strategy.StopContainerAsync(id)));

        _startCommand = new AsyncRelayCommand<string>(containerId =>
            RunContainerActionAsync(containerId, "start", "Start", "Starting", "started", id => _strategy.StartContainerAsync(id)));

        _removeCommand = new AsyncRelayCommand<string>(async containerId =>
        {
            if (string.IsNullOrEmpty(containerId)) return;
            // Single-item deletion must match the confirmation rigor of the bulk prune actions.
            var confirmName = GetContainerDisplayName(containerId);
            if (!await ShowConfirmDialogAsync("Remove Container", $"Remove container '{confirmName}'? This is permanent and cannot be undone.", "Remove"))
            {
                return;
            }
            await RunContainerActionAsync(containerId, "remove", "Remove", "Removing", "removed", id => _strategy.RemoveContainerAsync(id));
        });

        _restartCommand = new AsyncRelayCommand<string>(async (containerId) =>
        {
            if (string.IsNullOrEmpty(containerId)) return;
            if (IsDebounced($"restart_{containerId}", 1000)) return;
            var displayName = GetContainerDisplayName(containerId);
            try
            {
                ShowTemporaryStatus($"Restarting container '{displayName}'...");
                await _strategy.RestartContainerAsync(containerId);
                await RefreshAllAsync();
                ShowTemporaryStatus($"Container '{displayName}' restarted successfully.");
            }
            catch (Exception ex)
            {
                ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Context_RestartContainer", ex);
                ShowTemporaryError($"Failed to restart container '{displayName}'", ex);
            }
        });
    }

    /// <summary>
    /// Shared lifecycle driver for the per-row Stop/Start/Remove actions: debounces, locates the
    /// row button by its buttonTag, swaps in a busy spinner, awaits the strategy call, refreshes,
    /// and on failure restores the button with a transient error state. Factors out ~180 lines of
    /// triplicated boilerplate so the behavior stays identical across actions. The verb
    /// arguments are the imperative button label (restVerb, e.g. "Stop"), the progressive form
    /// shown while busy (presentVerb, e.g. "Stopping"), and the past tense for the success banner
    /// (pastVerb, e.g. "stopped").
    /// </summary>
    private async Task RunContainerActionAsync(string? containerId, string buttonTag,
        string restVerb, string presentVerb, string pastVerb, Func<string, Task> op)
    {
        if (string.IsNullOrEmpty(containerId)) return;
        if (IsDebounced($"{buttonTag}_{containerId}", 1000)) return;

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
                    btn = panel.Children.OfType<Button>().FirstOrDefault(b => string.Equals(b.Tag as string, buttonTag, StringComparison.Ordinal));
                }
            }
        });

        if (btn == null || panel == null) return;

        var displayName = GetContainerDisplayName(containerId);
        var prevTip = ToolTip.GetTip(btn);
        try
        {
            ShowTemporaryStatus($"{presentVerb} container '{displayName}'...");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                panel.IsEnabled = false;
                var busyText = new TextBlock { Text = $"{presentVerb}...", VerticalAlignment = VerticalAlignment.Center };
                var busyProgress = new ProgressBar { IsIndeterminate = true, Width = 40, Height = 4, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var busyStack = new StackPanel { Orientation = Orientation.Horizontal };
                busyStack.Children.Add(busyText);
                busyStack.Children.Add(busyProgress);
                btn.Content = busyStack;
            });

            await op(containerId);
            await RefreshAllAsync();
            ShowTemporaryStatus($"Container '{displayName}' {pastVerb} successfully.");
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", $"Action_{restVerb}Container", ex);
            ShowTemporaryError($"Failed to {restVerb.ToLowerInvariant()} container '{displayName}'", ex);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!_hasAttached) return;
                btn.Content = "Error";
                ToolTip.SetTip(btn, $"Failed to {restVerb.ToLowerInvariant()}: {ex.Message}");
                await Task.Delay(3000);
                if (!_hasAttached) return;
                btn.Content = restVerb;
                ToolTip.SetTip(btn, prevTip);
                panel.IsEnabled = true;
            });
        }
    }

    private string GetContainerDisplayName(string containerId)
    {
        lock (_cachedDataLock)
        {
            var c = _cachedContainers?.FirstOrDefault(x => string.Equals(x.ID, containerId, StringComparison.Ordinal));
            if (c != null)
            {
                var name = c.Names?.FirstOrDefault()?.TrimStart('/');
                if (!string.IsNullOrEmpty(name))
                {
                    return $"{name} ({containerId.ShortId()})";
                }
            }
        }
        return containerId.ShortId() ?? containerId;
    }


    // Debouncer and Stats Cache State
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastActionTimes = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _liveStats = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activeStatsQueries = new(StringComparer.Ordinal);

    private bool IsDebounced(string actionKey, int ms = 500)
    {
        var now = Environment.TickCount64;
        // Atomic first-insert: the first action for a key is always allowed. Using TryAdd (not GetOrAdd +
        // last!=now) closes the same-tick double-click hole — two clicks within one TickCount64 quantum
        // would otherwise both pass because last==now made the old guard false.
        if (_lastActionTimes.TryAdd(actionKey, now))
        {
            return false;
        }
        if (_lastActionTimes.TryGetValue(actionKey, out var last) && (now - last) < ms)
        {
            return true; // inside the debounce window — reject WITHOUT sliding the window
        }
        _lastActionTimes[actionKey] = now;
        return false;
    }


    private sealed class StatefulProgress<TState, T>(TState state, Action<TState, T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(state, value);
    }

    private static readonly Action<(DockerDiagnosticsView View, string ContainerId), ContainerStatsResponse> StatsReportHandler =
        (state, stats) =>
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
                state.View._liveStats[state.ContainerId] = statsText;

                state.View.UpdateContainerStatsUI(state.ContainerId, $" ({statsText})", isRunning: true);
            }
        };

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
                var progress = new StatefulProgress<(DockerDiagnosticsView, string), ContainerStatsResponse>((this, containerId), StatsReportHandler);

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

    /// <summary>
    /// Per-refresh live update used when the container set is structurally unchanged (same count, ids and
    /// states), so the full row rebuild in <see cref="PopulateContainers"/> is skipped. Refreshes the
    /// cached list and re-polls stats for RUNNING containers, whose status/uptime and CPU/RAM cells are then
    /// updated in place by <see cref="UpdateContainerStatsUI"/>. An exited container's relative-time status
    /// legitimately freezes until the next structural change — the deliberate id+state fingerprint treats a
    /// stopped container as static. Cheaper than a rebuild and avoids the flicker a rebuild would cause.
    /// </summary>
    private void RefreshLiveContainerCells(IList<Docker.DotNet.Models.ContainerListResponse> containers)
    {
        if (containers == null) return;
        lock (_cachedDataLock)
        {
            _cachedContainers = containers;
        }
        foreach (var c in containers)
        {
            if (c.State?.Equals("running", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                QueryContainerStats(c.ID);
            }
        }
    }

    private void PopulateContainers(IList<Docker.DotNet.Models.ContainerListResponse> containers)
    {
        if (containers == null) return;
        lock (_cachedDataLock)
        {
            _cachedContainers = containers;
            // Evict live-stats entries for containers that have fully disappeared (ephemeral --rm churn),
            // so _liveStats cannot grow unbounded across a long session. The container list is All=true /
            // Limit=250, so a present-but-stopped container is retained; only vanished IDs are removed.
            if (_liveStats.Count > containers.Count)
            {
                var liveIds = new HashSet<string>(containers.Count, StringComparer.Ordinal);
                foreach (var c in containers)
                {
                    liveIds.Add(c.ID);
                }
                foreach (var key in _liveStats.Keys)
                {
                    if (!liveIds.Contains(key))
                    {
                        _liveStats.TryRemove(key, out _);
                    }
                }
            }
            foreach (var child in _containersContent.Children)
            {
                if (child is Grid grid && grid.Margin == RowMargin)
                {
                    _recycledContainerRows.Add(grid);
                }
            }
            if (_recycledContainerRows.Count > MaxRecycledRows)
            {
                _recycledContainerRows.RemoveRange(MaxRecycledRows, _recycledContainerRows.Count - MaxRecycledRows);
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

        var itemsToShow = _showAllContainers ? sorted : sorted.Take(MaxVisibleRows);
        foreach (var c in itemsToShow)
        {
            var name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID.ShortId();
            var image = Truncate(c.Image, 30);
            var isRunning = c.State?.Equals("running", StringComparison.OrdinalIgnoreCase) ?? false;

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
            // A row action disables this panel while it runs and re-enables it only on the error path; the
            // success path relies on this recycle. Reset it here so a recycled panel never carries a stale
            // disabled state onto a live row (Avalonia propagates a disabled parent to its buttons).
            btnPanel.IsEnabled = true;

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
            Avalonia.Automation.AutomationProperties.SetName(logsBtn, $"View logs for container {name}");

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
            Avalonia.Automation.AutomationProperties.SetName(stopBtn, $"Stop container {name}");
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
            Avalonia.Automation.AutomationProperties.SetName(startBtn, $"Start container {name}");
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
            Avalonia.Automation.AutomationProperties.SetName(removeBtn, $"Remove container {name}");
            removeBtn.IsVisible = !isRunning;

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

        if (!_showAllContainers && searchable.Count > MaxVisibleRows)
        {
            var remaining = searchable.Count - MaxVisibleRows;
            newChildren.Add(CreateToggleMoreButton(
                $"... and {remaining} more (click to show all)",
                () => { _showAllContainers = true; PopulateContainers(_cachedContainers); },
                $"Show all {remaining} additional containers"));
        }
        else if (_showAllContainers && searchable.Count > MaxVisibleRows)
        {
            newChildren.Add(CreateToggleMoreButton(
                "Show less",
                () => { _showAllContainers = false; PopulateContainers(_cachedContainers); },
                "Show fewer containers"));
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
        // Plain, layout-tolerant text (no box-drawing art): each line maps host -> container, and
        // alignment is left to the renderer so long IPv6/host strings cannot corrupt the layout.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Port mappings:");
        foreach (var p in ports)
        {
            if (p.PublicPort != 0)
            {
                sb.AppendLine($"  host {p.IP}:{p.PublicPort} -> container {p.PrivatePort}/{p.Type}");
            }
            else
            {
                sb.AppendLine($"  exposed {p.PrivatePort}/{p.Type}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
