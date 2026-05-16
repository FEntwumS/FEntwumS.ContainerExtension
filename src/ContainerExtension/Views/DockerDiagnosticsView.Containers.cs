#pragma warning disable MA0004
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

namespace ContainerExtension.Views;

public partial class DockerDiagnosticsView
{
    private void PopulateContainers(IList<Docker.DotNet.Models.ContainerListResponse> containers)
    {
        _cachedContainers = containers;
        _containersContent.Children.Clear();

        if (containers.Count == 0)
        {
            _containersContent.Children.Add(new TextBlock
            {
                Text = "No containers found.",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
            return;
        }

        var searchable = string.IsNullOrEmpty(_searchFilter)
            ? (IList<Docker.DotNet.Models.ContainerListResponse>)containers
            : containers.Where(c =>
                (c.Names?.Any(n => n.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)) ?? false) ||
                c.Image.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                (c.Status ?? c.State ?? "").Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
            ).ToList();

        if (searchable.Count == 0)
        {
            _containersContent.Children.Add(new TextBlock
            {
                Text = $"No containers matching \"{Truncate(_searchFilter, 20)}\".",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
            return;
        }

        _containersContent.Children.Add(CreateSortableHeaderRow(
            [ ("NAME", "name"), ("IMAGE", "image"), ("STATUS", "status") ],
            _containerSort,
            key => { ToggleSort(ref _containerSort, key); PopulateContainers(_cachedContainers); },
            "160,8,180,8,150,8,Auto",
            ThreeColumnIndices));
        _containersContent.Children.Add(CreateSeparator());

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

        foreach (var c in sorted.Take(15))
        {
            var name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID.ShortId();
            var image = Truncate(c.Image, 30);
            var status = Truncate(c.Status ?? c.State ?? "unknown", 22);
            var isRunning = c.State?.Equals("running", StringComparison.OrdinalIgnoreCase) ?? false;

            var statusColor = isRunning ? GreenColor : (string.Equals(c.State, "exited", StringComparison.Ordinal) ? MutedColor : YellowColor);
            var row = CreateContainerRow(Truncate(name, 20), image, status, isHeader: false, statusColor: statusColor);

            var logContainerId = c.ID;
            var logsBtn = new Button
            {
                Content = "Logs",
                FontSize = 10,
                Padding = new Thickness(8, 2),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Command = new AsyncRelayCommand(async () =>
                {
                    if (_openLogWindows.TryGetValue(logContainerId, out var existingWindow))
                    {
                        existingWindow?.Activate();
                        return;
                    }

                    _openLogWindows[logContainerId] = null!;

                    try
                    {
                        var logs = await _strategy.GetContainerLogsAsync(logContainerId);
                        var containerLabel = c.Names?.FirstOrDefault()?.TrimStart('/') ?? logContainerId.ShortId();
                        var logText = string.IsNullOrWhiteSpace(logs) ? "(no output)" : logs;

                        var saveBtn = new Button
                        {
                            Content = "Save Logs",
                            FontSize = 10,
                            Padding = new Thickness(8, 4),
                            Margin = new Thickness(12, 4, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        ToolTip.SetTip(saveBtn, "Save the full log output as a .txt file");
                        Window logWindow = null!;
                        
                        saveBtn.Command = new AsyncRelayCommand(async () =>
                        {
                            var prevTip = ToolTip.GetTip(saveBtn);
                            try
                            {
                                saveBtn.Content = "Saving...";
                                saveBtn.IsEnabled = false;
                                
                                var topLevel = TopLevel.GetTopLevel(logWindow);
                                if (topLevel?.StorageProvider != null)
                                {
                                    var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                                    {
                                        Title = "Save Container Logs",
                                        SuggestedFileName = $"container_logs_{containerLabel}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                                    });

                                    if (file != null)
                                    {
                                        await Task.Run(async () =>
                                        {
                                            await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
                                            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                                            await writer.WriteAsync(logText).ConfigureAwait(false);
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
                        });

                        var logTextBox = new TextBox
                        {
                            Text = logText,
                            FontFamily = MonoFont,
                            FontSize = 11,
                            Foreground = FontColor,
                            TextWrapping = TextWrapping.Wrap,
                            IsReadOnly = true,
                            Margin = new Thickness(12),
                            AcceptsReturn = true
                        };
                        
                        var detailsGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
                        Grid.SetRow(saveBtn, 0);
                        Grid.SetRow(logTextBox, 1);
                        detailsGrid.Children.Add(saveBtn);
                        detailsGrid.Children.Add(logTextBox);

                        var cts = new System.Threading.CancellationTokenSource();
                        
                        // Buffer optimization to prevent UI-Thread freeze on fast output streams
                        _ = Task.Run(async () =>
                        {
                            try 
                            {
                                var logsBuffer = new StringBuilder(logTextBox.Text);
                                bool needsUpdate = false;

                                // Background flusher thread to prevent high-frequency UI manipulation locking
                                _ = Task.Run(async () => {
                                    while (!cts.IsCancellationRequested)
                                    {
                                        await Task.Delay(100, cts.Token).ConfigureAwait(false);
                                        if (needsUpdate)
                                        {
                                            string text;
                                            lock (logsBuffer) 
                                            { 
                                                text = logsBuffer.ToString(); 
                                                needsUpdate = false; 
                                            }
                                            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                                            {
                                                logTextBox.Text = text;
                                                logTextBox.CaretIndex = text.Length;
                                            });
                                        }
                                    }
                                }, cts.Token);

                                await foreach (var logLine in _strategy.StreamContainerLogsAsync(logContainerId, cts.Token).ConfigureAwait(false))
                                {
                                    lock (logsBuffer)
                                    {
                                        logsBuffer.AppendLine(logLine);
                                        if (logsBuffer.Length > 50000) logsBuffer.Remove(0, logsBuffer.Length - 25000);
                                        needsUpdate = true;
                                    }
                                }
                            }
                            catch (OperationCanceledException) { /* Ignore */ }
                            catch (Exception ex)
                            {
                                Avalonia.Threading.Dispatcher.UIThread.Post(() => logTextBox.Text += $"\n[Log Stream Failed: {ex.Message}]");
                            }
                        });

                        logWindow = new Window
                        {
                            Title = $"Container Logs — {containerLabel}",
                            Width = 700,
                            Height = 450,
                            Content = detailsGrid
                        };
                        logWindow.Closed += (_, _) => 
                        {
                            try { cts.Cancel(); cts.Dispose(); } catch { /* Ignore */ } 
                            _openLogWindows.Remove(logContainerId);
                        };
                        _openLogWindows[logContainerId] = logWindow;
                        
                        var mainWindow = TopLevel.GetTopLevel(this) as Window;
                        if (mainWindow != null) logWindow.Show(mainWindow);
                        else logWindow.Show();
                    }
                    catch
                    {
                        _openLogWindows.Remove(logContainerId);
                    }
                })
            };
            ToolTip.SetTip(logsBtn, "View the stdout/stderr output of this container");

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };

            if (isRunning)
            {
                var containerId = c.ID;
                Button stopBtn = null!;
                stopBtn = new Button
                {
                    Content = "Stop",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Command = new AsyncRelayCommand(async () =>
                    {
                        var prevTip = ToolTip.GetTip(stopBtn);
                        try
                        {
                            stopBtn.IsEnabled = false;
                            stopBtn.Content = "Stopping...";
                            await _strategy.StopContainerAsync(containerId);
                            await RefreshAllAsync();
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_StopContainer", ex);
                            stopBtn.Content = "Error ✗";
                            ToolTip.SetTip(stopBtn, $"Failed to stop: {ex.Message}");
                            await Task.Delay(3000);
                            stopBtn.Content = "Stop";
                            ToolTip.SetTip(stopBtn, prevTip);
                            stopBtn.IsEnabled = true;
                        }
                    })
                };
                ToolTip.SetTip(stopBtn, "Send a graceful stop signal to this running container");
                btnPanel.Children.Add(stopBtn);
            }
            else
            {
                var startContainerId = c.ID;
                Button startBtn = null!;
                startBtn = new Button
                {
                    Content = "Start",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Command = new AsyncRelayCommand(async () =>
                    {
                        var prevTip = ToolTip.GetTip(startBtn);
                        try
                        {
                            startBtn.IsEnabled = false;
                            startBtn.Content = "Starting...";
                            await _strategy.StartContainerAsync(startContainerId);
                            await RefreshAllAsync();
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_StartContainer", ex);
                            startBtn.Content = "Error ✗";
                            ToolTip.SetTip(startBtn, $"Failed to start: {ex.Message}");
                            await Task.Delay(3000);
                            startBtn.Content = "Start";
                            ToolTip.SetTip(startBtn, prevTip);
                            startBtn.IsEnabled = true;
                        }
                    })
                };
                ToolTip.SetTip(startBtn, "Restart this stopped container");
                btnPanel.Children.Add(startBtn);

                var rmContainerId = c.ID;
                Button removeBtn = null!;
                removeBtn = new Button
                {
                    Content = "Remove",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Command = new AsyncRelayCommand(async () =>
                    {
                        var prevTip = ToolTip.GetTip(removeBtn);
                        try
                        {
                            removeBtn.IsEnabled = false;
                            removeBtn.Content = "Removing...";
                            await _strategy.RemoveContainerAsync(rmContainerId);
                            await RefreshAllAsync();
                        }
                        catch (Exception ex)
                        {
                            ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_RemoveContainer", ex);
                            removeBtn.Content = "Error ✗";
                            ToolTip.SetTip(removeBtn, $"Failed to remove: {ex.Message}");
                            await Task.Delay(3000);
                            removeBtn.Content = "Remove";
                            ToolTip.SetTip(removeBtn, prevTip);
                            removeBtn.IsEnabled = true;
                        }
                    })
                };
                ToolTip.SetTip(removeBtn, "Delete this stopped container and free its resources");
                btnPanel.Children.Add(removeBtn);
            }

            btnPanel.Children.Add(logsBtn);
            Grid.SetColumn(btnPanel, 6);
            (row as Grid)!.Children.Add(btnPanel);

            _containersContent.Children.Add(row);
        }

        if (searchable.Count > 15)
            _containersContent.Children.Add(CreateMoreText(searchable.Count - 15));
    }

    private static Grid CreateContainerRow(string name, string image, string status,
        bool isHeader, SolidColorBrush? statusColor = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("160,8,180,8,150,8,Auto"),
            Margin = new Thickness(0, isHeader ? 0 : 1)
        };

        AddGridCell(grid, 0, name, isHeader, isHeader ? AccentColor : FontColor);
        AddGridCell(grid, 2, image, isHeader, isHeader ? AccentColor : MutedColor);
        AddGridCell(grid, 4, status, isHeader, isHeader ? AccentColor : (statusColor ?? MutedColor));

        return grid;
    }
}