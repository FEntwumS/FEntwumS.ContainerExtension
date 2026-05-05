using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using OneWare.Essentials.Services;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Handles image-related operations via the Docker.DotNet SDK.
/// </summary>
public sealed class DockerImageManager
{
    private readonly DockerClient _client;
    private readonly ISettingsService _settingsService;

    public DockerImageManager(DockerClient client, ISettingsService settingsService)
    {
        _client = client;
        _settingsService = settingsService;
    }

    private T SafeGetSetting<T>(string key, T fallback)
    {
        try
        {
            var value = _settingsService.GetSetting<T>(key);
            if (value == null) return fallback;
            if (typeof(T) == typeof(string) && string.IsNullOrWhiteSpace(value.ToString()))
                return fallback;
            return value;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Lists all locally cached Docker images.
    /// Used by the Docker Desktop dashboard images section.
    /// </summary>
    public async Task<IList<ImagesListResponse>> ListImagesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _client.Images.ListImagesAsync(
                new ImagesListParameters { All = false }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerImageManager", "ListImagesAsync failed", ex);
            return Array.Empty<ImagesListResponse>();
        }
    }

    /// <summary>
    /// Removes a specific image by ID. Used by the dashboard's remove button.
    /// </summary>
    public async Task RemoveImageAsync(string imageId, CancellationToken ct = default)
    {
        await _client.Images.DeleteImageAsync(
            imageId, new ImageDeleteParameters { Force = false }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-pulls all tagged (non-dangling) local images via the Docker SDK.
    /// Returns a summary of how many images were updated.
    /// </summary>
    public async Task<(int pulled, int failed)> UpdateAllImagesAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        var images = await _client.Images.ListImagesAsync(
            new ImagesListParameters { All = false }, ct).ConfigureAwait(false);

        var platform = SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
        int pulled = 0, failed = 0;

        foreach (var img in images)
        {
            if (img.RepoTags == null) continue;
            foreach (var tag in img.RepoTags)
            {
                if (tag.Contains("<none>")) continue;
                try
                {
                    progress?.Invoke($"Pulling {tag}...");
                    var pullParams = new ImagesCreateParameters { FromImage = tag };
                    if (!string.IsNullOrWhiteSpace(platform) && !platform.Equals("auto", StringComparison.OrdinalIgnoreCase))
                        pullParams.Platform = platform;
                    await _client.Images.CreateImageAsync(
                        pullParams, null, new Progress<JSONMessage>(_ => { }), ct).ConfigureAwait(false);
                    pulled++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ContainerTelemetry.TrackError("DockerImageManager", $"Re-pull failed for '{tag}'", ex);
                    failed++;
                }
            }
        }
        return (pulled, failed);
    }

    /// <summary>
    /// Prunes dangling (untagged) images from the local Docker daemon.
    /// </summary>
    public async Task<int> PruneDanglingImagesAsync(CancellationToken ct = default)
    {
        try
        {
            var filters = new Dictionary<string, IDictionary<string, bool>>
            {
                { "dangling", new Dictionary<string, bool> { { "true", true } } }
            };

            var response = await _client.Images.PruneImagesAsync(
                new ImagesPruneParameters { Filters = filters }, ct).ConfigureAwait(false);

            return response.ImagesDeleted?.Count ?? 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerImageManager", "Dangling image prune failed", ex);
            return 0;
        }
    }

    /// <summary>
    /// Aggregates disk usage statistics for local container images.
    /// Calculates total size and realistically reclaimable space (from dangling images).
    /// </summary>
    public static (int imageCount, long totalSizeBytes, long reclaimableBytes) ComputeDiskUsage(IList<ImagesListResponse> images)
    {
        if (images == null || images.Count == 0) return (0, 0, 0);

        int count = images.Count;
        long total = images.Sum(i => i.Size);
        long reclaimable = images.Where(i => i.RepoTags == null || !i.RepoTags.Any() || i.RepoTags.All(t => t.Contains("<none>")))
                                 .Sum(i => i.Size);

        return (count, total, reclaimable);
    }

    /// <summary>
    /// Retrieves disk usage summary from the daemon.
    /// </summary>
    public async Task<(int imageCount, long totalSizeBytes, long reclaimableBytes)> GetDiskUsageSummaryAsync(CancellationToken ct = default)
    {
        var images = await ListImagesAsync(ct).ConfigureAwait(false);
        return ComputeDiskUsage(images);
    }
}
