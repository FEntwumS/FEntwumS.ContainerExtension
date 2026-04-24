using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace ContainerExtension.Views;

/// <summary>
/// Partial class containing the Containers section population logic:
/// <see cref="PopulateContainers"/> and <see cref="CreateContainerRow"/>.
/// </summary>
public partial class DockerDiagnosticsView
{
    /// <summary>Populates the Containers section with a live table of running and stopped containers.</summary>
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

        // Apply global search filter (case-insensitive substring match across name, image, status)
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

        // Sortable header row
        _containersContent.Children.Add(CreateSortableHeaderRow(
            new[] { ("NAME", "name"), ("IMAGE", "image"), ("STATUS", "status") },
            _containerSort,
            key => { ToggleSort(ref _containerSort, key); PopulateContainers(_cachedContainers); },
            "160,8,180,8,150,8,Auto",
            ThreeColumnIndices));
        _containersContent.Children.Add(CreateSeparator());

        // Sort filtered containers by active column
        var sorted = _containerSort.column switch
        {
            "image" => _containerSort.ascending
                ? searchable.OrderBy(c => c.Image, StringComparer.OrdinalIgnoreCase)
                : searchable.OrderByDescending(c => c.Image, StringComparer.OrdinalIgnoreCase),
            "status" => _containerSort.ascending
                ? searchable.OrderBy(c => c.Status ?? c.State ?? "", StringComparer.OrdinalIgnoreCase)
                : searchable.OrderByDescending(c => c.Status ?? c.State ?? "", StringComparer.OrdinalIgnoreCase),
            _ => _containerSort.ascending // "name"
                ? searchable.OrderBy(c => c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12], StringComparer.OrdinalIgnoreCase)
                : searchable.OrderByDescending(c => c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12], StringComparer.OrdinalIgnoreCase),
        };

        foreach (var c in sorted.Take(15))
        {
            var name = c.Names?.FirstOrDefault()?.TrimStart('/') ?? c.ID[..12];
            var image = Truncate(c.Image, 30);
            var status = Truncate(c.Status ?? c.State ?? "unknown", 22);
            var isRunning = c.State?.Equals("running", StringComparison.OrdinalIgnoreCase) ?? false;

            var statusColor = isRunning ? GreenColor : (c.State == "exited" ? MutedColor : YellowColor);
            var row = CreateContainerRow(Truncate(name, 20), image, status, isHeader: false, statusColor: statusColor);

            // Logs button — available for all containers (running and stopped)
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
                    // Deduplicate: if a log window for this container is already open, bring it to front
                    if (_openLogWindows.TryGetValue(logContainerId, out var existingWindow))
                    {
                        existingWindow.Activate();
                        return;
                    }

                    var logs = await _strategy.GetContainerLogsAsync(logContainerId);
                    var containerLabel = c.Names?.FirstOrDefault()?.TrimStart('/') ?? logContainerId[..12];
                    var logText = string.IsNullOrWhiteSpace(logs) ? "(no output)" : logs;

                    // Save Logs button for exporting to file
                    var saveBtn = new Button
                    {
                        Content = "Save Logs",
                        FontSize = 10,
                        Padding = new Thickness(8, 4),
                        Margin = new Thickness(12, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    ToolTip.SetTip(saveBtn, "Save the full log output as a .txt file");
                    saveBtn.Command = new AsyncRelayCommand(async () =>
                    {
                        var prevTip = ToolTip.GetTip(saveBtn);
                        try
                        {
                            saveBtn.Content = "Saving...";
                            saveBtn.IsEnabled = false;
                            var topLevel = TopLevel.GetTopLevel(this);
                            if (topLevel?.StorageProvider != null)
                            {
                                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                                {
                                    Title = "Save Container Logs",
                                    SuggestedFileName = $"container_logs_{containerLabel}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                                });
                                if (file != null)
                                {
                                    await using var stream = await file.OpenWriteAsync();
                                    using var writer = new StreamWriter(stream);
                                    await writer.WriteAsync(logText);
                                    saveBtn.Content = $"Saved ✓";
                                    return;
                                }
                            }

                            // Restore if cancelled
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
                    Grid.SetRow(saveBtn, 0);
                    Grid.SetRow(logTextBox, 1);

                    var logWindow = new Window
                    {
                        Title = $"Container Logs — {containerLabel}",
                        Width = 700,
                        Height = 450,
                        Content = new Grid
                        {
                            RowDefinitions = new RowDefinitions("Auto,*"),
                            Children = { saveBtn, logTextBox }
                        }
                    };
                    logWindow.Closed += (_, _) => _openLogWindows.Remove(logContainerId);
                    _openLogWindows[logContainerId] = logWindow;
                    logWindow.Show();
                })
            };
            ToolTip.SetTip(logsBtn, "View the stdout/stderr output of this container");

            // Action buttons — unified panel for both running and stopped containers
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
                var startBtn = new Button
                {
                    Content = "Start",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Command = new AsyncRelayCommand(async () =>
                    {
                        try
                        {
                            await _strategy.StartContainerAsync(startContainerId);
                            await RefreshAllAsync();
                        }
                        catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_StartContainer", ex); }
                    })
                };
                ToolTip.SetTip(startBtn, "Restart this stopped container");
                btnPanel.Children.Add(startBtn);

                var rmContainerId = c.ID;
                var removeBtn = new Button
                {
                    Content = "Remove",
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Command = new AsyncRelayCommand(async () =>
                    {
                        try
                        {
                            await _strategy.RemoveContainerAsync(rmContainerId);
                            await RefreshAllAsync();
                        }
                        catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.Containers", "Action_RemoveContainer", ex); }
                    })
                };
                ToolTip.SetTip(removeBtn, "Delete this stopped container and free its resources");
                btnPanel.Children.Add(removeBtn);
            }

            // Logs button — always available
            btnPanel.Children.Add(logsBtn);
            Grid.SetColumn(btnPanel, 6);
            (row as Grid)!.Children.Add(btnPanel);

            _containersContent.Children.Add(row);
        }

        if (searchable.Count > 15)
            _containersContent.Children.Add(CreateMoreText(searchable.Count - 15));
    }

    /// <summary>Creates a 4-column grid row for the containers table (name, image, status, actions).</summary>
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
