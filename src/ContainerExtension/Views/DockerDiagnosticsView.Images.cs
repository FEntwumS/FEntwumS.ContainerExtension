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
    /// <summary>Populates the Images &amp; Disk Usage section with the image inventory and storage summary.</summary>
    private void PopulateImages(IList<Docker.DotNet.Models.ImagesListResponse> images,
        (int imageCount, long totalSizeBytes, long reclaimableBytes) diskUsage)
    {
        _cachedImages = images;
        _cachedDiskUsage = diskUsage;
        _imagesContent.Children.Clear();

        if (images.Count == 0)
        {
            _imagesContent.Children.Add(new TextBlock
            {
                Text = "No images found.",
                Foreground = MutedColor,
                FontSize = 11,
                FontStyle = FontStyle.Italic
            });
            return;
        }

        // Filter out dangling (<none>:<none>) images — these are old layers replaced by newer pulls
        var taggedImages = images.Where(i => i.RepoTags != null && i.RepoTags.Any(t => !t.Contains("<none>"))).ToList();

        // Apply global search filter (case-insensitive substring match on repo:tag)
        if (!string.IsNullOrEmpty(_searchFilter))
            taggedImages = taggedImages.Where(i =>
                i.RepoTags?.Any(t => t.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)) ?? false
            ).ToList();

        // Sortable header row
        _imagesContent.Children.Add(CreateSortableHeaderRow(
            new[] { ("REPOSITORY:TAG", "repo"), ("SIZE", "size"), ("CREATED", "created") },
            _imageSort,
            key => { ToggleSort(ref _imageSort, key); PopulateImages(_cachedImages, _cachedDiskUsage); },
            "250,8,80,8,*,8,Auto",
            ThreeColumnIndices));
        _imagesContent.Children.Add(CreateSeparator());

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

        foreach (var img in sorted.Take(15))
        {
            var repoTag = Truncate(img.RepoTags?.FirstOrDefault() ?? "<none>:<none>", 35);
            var imageRow = CreateImageRow(repoTag, FormatBytes(img.Size), FormatTimeAgo(img.Created), isHeader: false);

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
                        removeBtn.Content = "Error ✗";
                        ToolTip.SetTip(removeBtn, $"Failed to remove: {ex.Message}");
                        await Task.Delay(3000);
                        removeBtn.Content = "Remove";
                        ToolTip.SetTip(removeBtn, prevTip);
                        removeBtn.IsEnabled = true;
                    }
                })
            };
            ToolTip.SetTip(removeBtn, "Delete this image from local storage (fails if a container is using it)");
            Grid.SetColumn(removeBtn, 6);
            (imageRow as Grid)!.Children.Add(removeBtn);

            _imagesContent.Children.Add(imageRow);
        }

        if (taggedImages.Count > 15)
            _imagesContent.Children.Add(CreateMoreText(taggedImages.Count - 15));

        // Show dangling image count if any exist
        var danglingCount = images.Count - taggedImages.Count;
        if (danglingCount > 0)
        {
            _imagesContent.Children.Add(new TextBlock
            {
                Text = $"{danglingCount} dangling image(s) hidden — use Prune System to clean up.",
                Foreground = MutedColor,
                FontSize = 10,
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        // ── Disk Usage Summary (merged from standalone section) ──────
        if (diskUsage.totalSizeBytes > 0)
        {
            _imagesContent.Children.Add(new Border
            {
                Height = 1,
                Background = MutedColor,
                Opacity = 0.2,
                Margin = new Thickness(0, 6)
            });

            var summaryText = $"Total: {FormatBytes(diskUsage.totalSizeBytes)}";
            if (diskUsage.reclaimableBytes > 0)
                summaryText += $" · Reclaimable: {FormatBytes(diskUsage.reclaimableBytes)}";

            _imagesContent.Children.Add(new TextBlock
            {
                Text = summaryText,
                FontFamily = MonoFont,
                FontSize = 10,
                Foreground = MutedColor,
                FontStyle = FontStyle.Italic
            });
        }
    }

    /// <summary>Creates a 4-column grid row for the images table (repo:tag, size, created, actions).</summary>
    private static Grid CreateImageRow(string repoTag, string size, string created, bool isHeader)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("250,8,80,8,*,8,Auto"),
            Margin = new Thickness(0, isHeader ? 0 : 1)
        };

        AddGridCell(grid, 0, repoTag, isHeader, isHeader ? AccentColor : FontColor);
        AddGridCell(grid, 2, size, isHeader, isHeader ? AccentColor : MutedColor, HorizontalAlignment.Right);
        AddGridCell(grid, 4, created, isHeader, isHeader ? AccentColor : MutedColor);

        return grid;
    }
}
