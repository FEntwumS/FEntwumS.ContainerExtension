#pragma warning disable MA0004
using static ContainerExtension.Views.UIBuilderHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace ContainerExtension.Views;

/// <summary>
/// Partial class containing the Images &amp; Disk Usage section population logic:
/// <see cref="PopulateImages"/> and <see cref="CreateImageRow"/>.
/// </summary>
public partial class DockerDiagnosticsView
{
    private static readonly string[] BinaryUnits = { "B", "KiB", "MiB", "GiB", "TiB" };

    private static string FormatBytesBinary(long bytes)
    {
        if (bytes < 0) return "unknown";
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < BinaryUnits.Length - 1) { size /= 1024; i++; }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size:F1} {BinaryUnits[i]}");
    }

    /// <summary>Populates the Images &amp; Disk Usage section with the image inventory and storage summary.</summary>
    private void PopulateImages(IList<Docker.DotNet.Models.ImagesListResponse> images,
    (int imageCount, long totalSizeBytes, long reclaimableBytes) diskUsage)
    {
        if (images == null) return;
        lock (_cachedDataLock)
        {
            _cachedImages = images;
            _cachedDiskUsage = diskUsage;
            foreach (var child in _imagesContent.Children)
            {
                if (child is Grid grid && grid.Margin == new Thickness(0, 1))
                {
                    var btn = grid.Children.FirstOrDefault(c => c is Button);
                    if (btn != null)
                    {
                        grid.Children.Remove(btn);
                    }
                    _recycledImageRows.Add(grid);
                }
            }
            if (_recycledImageRows.Count > 100)
            {
                _recycledImageRows.RemoveRange(100, _recycledImageRows.Count - 100);
            }
        }
        _imagesContent.Children.Clear();
        var newChildren = new List<Control>(images.Count * 2);

        if (images.Count == 0)
        {
            newChildren.Add(new TextBlock
            {
                Text = "No images found.",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
            _imagesContent.Children.AddRange(newChildren);
            return;
        }

        var taggedImages = new List<Docker.DotNet.Models.ImagesListResponse>(images.Count);
        int danglingCount = 0;

        for (int i = 0; i < images.Count; i++)
        {
            var img = images[i];
            var hasValidTag = false;
            var matchSearch = string.IsNullOrEmpty(_searchFilter);

            if (img.RepoTags != null)
            {
                for (int j = 0; j < img.RepoTags.Count; j++)
                {
                    var tag = img.RepoTags[j];
                    if (tag != null)
                    {
                        if (!tag.Contains("<none>", StringComparison.Ordinal))
                        {
                            hasValidTag = true;
                        }
                        if (!matchSearch && tag.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            matchSearch = true;
                        }
                    }
                }
            }

            if (!hasValidTag)
            {
                danglingCount++;
            }
            else if (matchSearch)
            {
                taggedImages.Add(img);
            }
        }

        // Sortable header row
        newChildren.Add(CreateSortableHeaderRow(
          [("REPOSITORY:TAG", "repo"), ("SIZE", "size"), ("CREATED", "created")],
          _imageSort,
          key =>
          {
              ToggleSort(ref _imageSort, key);
              IList<Docker.DotNet.Models.ImagesListResponse> localImages;
              (int imageCount, long totalSizeBytes, long reclaimableBytes) localDiskUsage;
              lock (_cachedDataLock)
              {
                  localImages = _cachedImages;
                  localDiskUsage = _cachedDiskUsage;
              }
              PopulateImages(localImages, localDiskUsage);
          },
          "250,8,80,8,150,8,Auto",
          ThreeColumnIndices));
        newChildren.Add(CreateSeparator());

        // Sort images by active column
        var sorted = _imageSort.column switch
        {
            "size" => _imageSort.ascending
              ? taggedImages.OrderBy(i => i.Size)
              : taggedImages.OrderByDescending(i => i.Size),
            "created" => _imageSort.ascending
              ? taggedImages.OrderBy(i => i.Created)
              : taggedImages.OrderByDescending(i => i.Created),
            _ => _imageSort.ascending // "repo"
                      ? taggedImages.OrderBy(i => i.RepoTags?.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase)
              : taggedImages.OrderByDescending(i => i.RepoTags?.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase),
        };

        var itemsToShow = _showAllImages ? sorted : sorted.Take(15);
        foreach (var img in itemsToShow)
        {
            var repoTag = Truncate(img.RepoTags?.FirstOrDefault() ?? "<none>:<none>", 35);

            Grid? existingGrid = null;
            lock (_cachedDataLock)
            {
                if (_recycledImageRows.Count > 0)
                {
                    existingGrid = _recycledImageRows[^1];
                    _recycledImageRows.RemoveAt(_recycledImageRows.Count - 1);
                }
            }

            var imageRow = CreateImageRow(repoTag, FormatBytesBinary(img.Size), FormatTimeAgo(img.Created), isHeader: false, existingGrid: existingGrid);

            var imageId = img.ID;
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
                        await _strategy.RemoveImageAsync(imageId);
                        await RefreshAllAsync();
                    }
                    catch (Exception ex)
                    {
                        ContainerTelemetry.TrackError("DockerDiagnosticsView.Images", "Action_RemoveImage", ex);
                        if (!_hasAttached) return;
                        removeBtn.Content = "Error ✗";
                        ToolTip.SetTip(removeBtn, $"Failed to remove: {ex.Message}");
                        await Task.Delay(3000);
                        if (!_hasAttached) return;
                        removeBtn.Content = "Remove";
                        ToolTip.SetTip(removeBtn, prevTip);
                        removeBtn.IsEnabled = true;
                    }
                })
            };
            ToolTip.SetTip(removeBtn, "Delete this image from local storage (fails if a container is using it)");
            Grid.SetColumn(removeBtn, 6);
            (imageRow as Grid)!.Children.Add(removeBtn);

            newChildren.Add(imageRow);
        }

        if (!_showAllImages && taggedImages.Count > 15)
        {
            var remaining = taggedImages.Count - 15;
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
                    _showAllImages = true;
                    IList<Docker.DotNet.Models.ImagesListResponse> localImages;
                    (int imageCount, long totalSizeBytes, long reclaimableBytes) localDiskUsage;
                    lock (_cachedDataLock)
                    {
                        localImages = _cachedImages;
                        localDiskUsage = _cachedDiskUsage;
                    }
                    PopulateImages(localImages, localDiskUsage);
                })
            };
            newChildren.Add(showAllBtn);
        }
        else if (_showAllImages && taggedImages.Count > 15)
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
                    _showAllImages = false;
                    IList<Docker.DotNet.Models.ImagesListResponse> localImages;
                    (int imageCount, long totalSizeBytes, long reclaimableBytes) localDiskUsage;
                    lock (_cachedDataLock)
                    {
                        localImages = _cachedImages;
                        localDiskUsage = _cachedDiskUsage;
                    }
                    PopulateImages(localImages, localDiskUsage);
                })
            };
            newChildren.Add(showLessBtn);
        }

        // Show dangling image count if any exist (computed before search filter)
        if (danglingCount > 0)
        {
            newChildren.Add(new TextBlock
            {
                Text = $"{danglingCount} dangling image(s) hidden — use Prune System to clean up.",
                Foreground = MutedColor,
                FontSize = 10,
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        // -- Disk Usage Summary (merged from standalone section) ------
        if (diskUsage.totalSizeBytes > 0)
        {
            newChildren.Add(new Border
            {
                Height = 1,
                Background = MutedColor,
                Opacity = 0.2,
                Margin = new Thickness(0, 6)
            });

            var summaryText = $"Total: {FormatBytesBinary(diskUsage.totalSizeBytes)}";
            if (diskUsage.reclaimableBytes > 0)
            {
                summaryText += $" · Reclaimable: {FormatBytesBinary(diskUsage.reclaimableBytes)}";
            }

            newChildren.Add(new TextBlock
            {
                Text = summaryText,
                FontFamily = MonoFont,
                FontSize = 10,
                Foreground = MutedColor,
                FontStyle = FontStyle.Italic
            });

            if (diskUsage.reclaimableBytes > 2L * 1024 * 1024 * 1024)
            {
                newChildren.Add(new TextBlock
                {
                    Text = "⚠️ High volume of reclaimable space. Run Prune System to free up disk space.",
                    FontSize = 10,
                    Foreground = RedColor,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        _imagesContent.Children.AddRange(newChildren);
    }

    /// <summary>Creates a 4-column grid row for the images table (repo:tag, size, created, actions).</summary>
    private static Grid CreateImageRow(string repoTag, string size, string created, bool isHeader, Grid? existingGrid = null)
    {
        var grid = existingGrid ?? new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("250,8,80,8,150,8,Auto"),
            Margin = new Thickness(0, isHeader ? 0 : 1)
        };

        AddGridCell(grid, 0, repoTag, isHeader, isHeader ? AccentColor : FontColor);
        AddGridCell(grid, 2, size, isHeader, isHeader ? AccentColor : MutedColor, HorizontalAlignment.Right);
        AddGridCell(grid, 4, created, isHeader, isHeader ? AccentColor : MutedColor);

        return grid;
    }
}
