using System;
using System.IO;
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
    private readonly SemaphoreSlim _listImagesSemaphore = new(1, 1);

    public DockerImageManager(DockerClient client, ISettingsService settingsService)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    private static bool CheckFreeDiskSpace(long requiredBytes, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(path))
            {
                path = OperatingSystem.IsWindows() ? "C:\\" : "/";
            }
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                root = OperatingSystem.IsWindows() ? "C:\\" : "/";
            }
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < requiredBytes)
            {
                errorMessage = $"Insufficient disk space on host system. Free: {drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0):N1} GB, Required: {requiredBytes / (1024.0 * 1024.0 * 1024.0):N1} GB.";
                return false;
            }
        }
        catch (Exception)
        {
            // Fail open
        }
        return true;
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
        FEntwumS.ContainerExtension.Registry.RegistryClient.InvalidateTagsCache();
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

        await _listImagesSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_cacheLock)
            {
                if (_cachedImages != null && Environment.TickCount64 < _cacheExpiration)
                {
                    return _cachedImages;
                }
            }

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
        finally
        {
            _listImagesSemaphore.Release();
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

        // Host disk space check: require at least 1 GB of free space
        if (!CheckFreeDiskSpace(1024 * 1024 * 1024, out var spaceError))
        {
            progress?.Invoke($"[ERROR] Pull aborted: {spaceError}");
            ContainerTelemetry.TrackError("DockerImageManager", "UpdateAllImagesAsync aborted due to low disk space", null, spaceError);
            return (0, 1);
        }

        try
        {
            var images = await ListImagesAsync(ct).ConfigureAwait(false);
            if (images == null || images.Count == 0)
            {
                return (0, 0);
            }

            var platformRaw = SafeGetSetting<string>(ContainerExtensionModule.PlatformSetting, "auto");
            var platform = string.IsNullOrWhiteSpace(platformRaw) ? "auto" : (platformRaw.Contains(' ') ? platformRaw.Trim() : platformRaw);

            var targets = new List<string>();
            var processedImageIds = new HashSet<string>(images.Count, StringComparer.Ordinal);
            foreach (var img in images)
            {
                if (img == null || img.RepoTags == null || !processedImageIds.Add(img.ID))
                {
                    continue;
                }

                foreach (var tag in img.RepoTags)
                {
                    if (tag != null && !tag.Contains("<none>") && !tag.Contains("..") && !tag.Contains("\\"))
                    {
                        targets.Add(tag);
                    }
                }
            }

            if (targets.Count == 0)
            {
                return (0, 0);
            }

            int pulledCount = 0;
            int failedCount = 0;

            var tasks = targets.Select(async targetTag =>
            {
                await PullSemaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke($"Pulling {targetTag}...");
                    var pullParams = new ImagesCreateParameters { FromImage = targetTag };
                    if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        pullParams.Platform = platform;
                    }

                    await _client.Images.CreateImageAsync(
                      pullParams, null, EmptyProgress<JSONMessage>.Instance, ct).ConfigureAwait(false);

                    Interlocked.Increment(ref pulledCount);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ContainerTelemetry.TrackError("DockerImageManager", $"Re-pull failed for '{targetTag}' due to connection loss or registry failure", ex);
                    Interlocked.Increment(ref failedCount);
                }
                finally
                {
                    PullSemaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return (pulledCount, failedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerImageManager", "UpdateAllImagesAsync failed", ex);
            return (0, 1);
        }
        finally
        {
            InvalidateCache();
        }
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
        var rawResult = await GetSystemDiskUsageRawAsync(ct).ConfigureAwait(false);
        if (rawResult != null)
        {
            return rawResult.Value;
        }

        var images = await ListImagesAsync(ct).ConfigureAwait(false);
        return ComputeDiskUsage(images, ct);
    }

#pragma warning disable IL2026, IL3050
    private async Task<(int imageCount, long totalSizeBytes, long reclaimableBytes)?> GetSystemDiskUsageRawAsync(CancellationToken ct)
    {
        var endpoint = _client?.Configuration?.EndpointBaseUri;
        if (endpoint == null) return null;

        var scheme = endpoint.Scheme.ToLowerInvariant();
        System.Net.Http.SocketsHttpHandler? handler = null;
        if (string.Equals(scheme, "npipe", StringComparison.Ordinal))
        {
            handler = new System.Net.Http.SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var pipeName = endpoint.LocalPath;
                    var serverName = ".";
                    if (pipeName.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        var parts = pipeName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            serverName = parts[0];
                            pipeName = parts[parts.Length - 1];
                        }
                    }
                    var pipe = new System.IO.Pipes.NamedPipeClientStream(
                        serverName,
                        pipeName,
                        System.IO.Pipes.PipeDirection.InOut,
                        System.IO.Pipes.PipeOptions.Asynchronous,
                        System.Security.Principal.TokenImpersonationLevel.Identification);
                    try
                    {
                        await pipe.ConnectAsync(token).ConfigureAwait(false);
                        return pipe;
                    }
                    catch
                    {
                        await pipe.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
            };
        }
        else if (string.Equals(scheme, "unix", StringComparison.Ordinal))
        {
            var socketPath = endpoint.LocalPath;
            handler = new System.Net.Http.SocketsHttpHandler
            {
                ConnectCallback = async (context, token) =>
                {
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), token).ConfigureAwait(false);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
        }

        using var httpClient = handler != null ? new System.Net.Http.HttpClient(handler, disposeHandler: true) : new System.Net.Http.HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        try
        {
            Uri url;
            if (string.Equals(scheme, "npipe", StringComparison.Ordinal) || string.Equals(scheme, "unix", StringComparison.Ordinal))
            {
                url = new Uri("http://localhost/system/df");
            }
            else
            {
                var httpScheme = string.Equals(scheme, "tcp", StringComparison.Ordinal) ? "http" : scheme;
                url = new UriBuilder(endpoint) { Scheme = httpScheme, Path = "system/df" }.Uri;
            }
            using var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var systemDf = await System.Text.Json.JsonSerializer.DeserializeAsync(contentStream, DockerImageJsonContext.Default.SystemDfResponse, cancellationToken: ct).ConfigureAwait(false);
                if (systemDf != null)
                {
                    long totalSize = systemDf.LayersSize;
                    if (totalSize == 0 && systemDf.Images != null)
                    {
                        totalSize = systemDf.Images.Sum(i => i.Size);
                    }
                    int imageCount = systemDf.Images?.Count ?? 0;
                    long reclaimable = 0;
                    if (systemDf.Images != null)
                    {
                        foreach (var img in systemDf.Images)
                        {
                            if (img.Containers == 0)
                            {
                                reclaimable += img.Size;
                            }
                        }
                    }
                    return (imageCount, totalSize, reclaimable);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // Fallback
        }
        return null;
    }
} // Close DockerImageManager class

internal sealed class SystemDfResponse
{
    public long LayersSize { get; set; } = 0;
    public List<SystemDfImage>? Images { get; set; } = null;
}

internal sealed class SystemDfImage
{
    public int Containers { get; set; } = 0;
    public long Size { get; set; } = 0;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SystemDfResponse))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SystemDfImage))]
internal partial class DockerImageJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
