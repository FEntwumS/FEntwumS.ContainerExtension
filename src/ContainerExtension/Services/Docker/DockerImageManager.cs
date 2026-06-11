using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using OneWare.Essentials.Services;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Handles container image resolution, pulling, inspection, and pruning.
/// Integrates with <see cref="ISettingsService"/> to respect pull policies (Always, IfNotPresent, Never).
/// </summary>
public sealed class DockerImageManager
{
    private static readonly System.Diagnostics.ActivitySource ImageActivitySource = new("OneWare.ContainerExtension.Image");
    private static readonly SemaphoreSlim PullSemaphore = new(2, 2);
    private static readonly SemaphoreSlim PruneSemaphore = new(1, 1);
    private static readonly FrozenDictionary<string, IDictionary<string, bool>> DanglingFilters =
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            { "dangling", new Dictionary<string, bool>(StringComparer.Ordinal) { { "true", true } }.ToFrozenDictionary(StringComparer.Ordinal) }
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly DockerClient _client;
    private readonly ISettingsService _settingsService;



    public DockerImageManager(DockerClient client, ISettingsService settingsService)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    private T SafeGetSetting<T>(string key, T fallback)
    {
        return _settingsService.SafeGetSetting(key, fallback);
    }

    private IList<ImagesListResponse>? _cachedImages;
    private long _cacheExpiration;
    private readonly System.Threading.Lock _cacheLock = new();

    private void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedImages = null;
            _cacheExpiration = 0;
        }
    }

    public async Task<IList<ImagesListResponse>> ListImagesAsync(CancellationToken ct = default)
    {
        lock (_cacheLock)
        {
            if (_cachedImages != null && Environment.TickCount64 < _cacheExpiration)
            {
                return _cachedImages;
            }
        }

        try
        {
            var images = await _client.Images.ListImagesAsync(
              new ImagesListParameters { All = false }, ct).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _cachedImages = images;
                _cacheExpiration = Environment.TickCount64 + 2_000;
            }
            return images;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerImageManager", "ListImagesAsync failed", ex);
            lock (_cacheLock)
            {
                if (_cachedImages != null)
                {
                    return _cachedImages;
                }
            }
            return Array.Empty<ImagesListResponse>();
        }
    }

    public async Task RemoveImageAsync(string imageId, CancellationToken ct = default)
    {
        using var activity = ImageActivitySource.StartActivity("DockerImageManager.RemoveImage");
        activity?.SetTag("image.id", imageId);
        if (string.IsNullOrWhiteSpace(imageId))
        {
            throw new ArgumentException("Image ID cannot be null or whitespace.", nameof(imageId));
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            await _client.Images.DeleteImageAsync(
              imageId, new ImageDeleteParameters { Force = false }, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            ContainerTelemetry.TrackError("DockerImageManager", $"Conflict or not found during deletion of image '{imageId.ShortId()}'", ex);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            ContainerTelemetry.TrackError("DockerImageManager", $"Failed to delete image '{imageId.ShortId()}'", ex);
            throw;
        }
        finally
        {
            InvalidateCache();
        }
    }

    public async Task<(int pulled, int failed)> UpdateAllImagesAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        using var activity = ImageActivitySource.StartActivity("DockerImageManager.UpdateAllImages");
        int pulled = 0, failed = 0;
        try
        {
            var images = await ListImagesAsync(ct).ConfigureAwait(false);
            if (images == null || images.Count == 0)
            {
                return (0, 0);
            }

            var platformRaw = SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
            var platform = string.IsNullOrWhiteSpace(platformRaw) ? "auto" : (platformRaw.Contains(' ') ? platformRaw.Trim() : platformRaw);
            var processedImageIds = new HashSet<string>(images.Count, StringComparer.Ordinal);

            foreach (var img in images)
            {
                if (img == null || img.RepoTags == null || !processedImageIds.Add(img.ID))
                {
                    continue;
                }

                string? targetTag = null;
                foreach (var tag in img.RepoTags)
                {
                    if (tag != null && !tag.Contains("<none>") && !tag.Contains("..") && !tag.Contains("\\"))
                    {
                        targetTag = tag;
                        break;
                    }
                }

                if (targetTag != null)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Invoke($"Pulling {targetTag}...");
                        var pullParams = new ImagesCreateParameters { FromImage = targetTag };
                        if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
                        {
                            pullParams.Platform = platform;
                        }

                        await PullSemaphore.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            await _client.Images.CreateImageAsync(
                              pullParams, null, EmptyProgress<JSONMessage>.Instance, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            PullSemaphore.Release();
                        }
                        pulled++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        ContainerTelemetry.TrackError("DockerImageManager", $"Re-pull failed for '{targetTag}' due to connection loss or registry failure", ex);
                        failed++;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerImageManager", "UpdateAllImagesAsync failed", ex);
        }
        finally
        {
            InvalidateCache();
        }
        return (pulled, failed);
    }

    public async Task<int> PruneDanglingImagesAsync(CancellationToken ct = default)
    {
        await PruneSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var response = await _client.Images.PruneImagesAsync(
              new ImagesPruneParameters { Filters = DanglingFilters }, ct).ConfigureAwait(false);

            InvalidateCache();
            return response.ImagesDeleted?.Count ?? 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerImageManager", "Dangling image prune failed", ex);
            return 0;
        }
        finally
        {
            PruneSemaphore.Release();
        }
    }

    public static (int imageCount, long totalSizeBytes, long reclaimableBytes) ComputeDiskUsage(IList<ImagesListResponse>? images, CancellationToken ct = default)
    {
        if (images == null || images.Count == 0)
        {
            return (0, 0, 0);
        }

        if (images.Count == 1)
        {
            var single = images[0];
            if (single == null || string.IsNullOrEmpty(single.ID))
            {
                return (0, 0, 0);
            }
            long rec = (single.RepoTags is null || single.RepoTags.Count == 0 || single.RepoTags.All(t => t != null && t.Contains("<none>"))) ? single.Size : 0;
            return (1, single.Size, rec);
        }

        var seenIds = new HashSet<string>(images.Count, StringComparer.Ordinal);
        int count = 0;
        long total = 0;
        long reclaimable = 0;

        foreach (var i in images)
        {
            ct.ThrowIfCancellationRequested();
            if (i == null || string.IsNullOrEmpty(i.ID) || !seenIds.Add(i.ID))
            {
                continue;
            }

            count++;
            unchecked
            {
                total += i.Size;
            }

            if (i.RepoTags is null || i.RepoTags.Count == 0 || i.RepoTags.All(t => t != null && t.Contains("<none>")))
            {
                unchecked
                {
                    reclaimable += i.Size;
                }
            }
        }

        return (count, total, reclaimable);
    }

    public async Task<(int imageCount, long totalSizeBytes, long reclaimableBytes)> GetDiskUsageSummaryAsync(CancellationToken ct = default)
    {
        var images = await ListImagesAsync(ct).ConfigureAwait(false);
        return ComputeDiskUsage(images, ct);
    }
}
