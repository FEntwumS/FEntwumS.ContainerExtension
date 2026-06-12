using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Handles container queries, process metrics, and lifecycle commands (start, stop, remove, restart).
/// Implements caching mechanisms to prevent UI-triggered SDK flooding.
/// </summary>
public sealed class DockerContainerManager
{
    private static readonly System.Diagnostics.ActivitySource ContainerActivitySource = new("OneWare.ContainerExtension.Container");
    private static readonly System.Buffers.SearchValues<char> InvalidContainerNameChars =
        System.Buffers.SearchValues.Create(";&|`$\0\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\v\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f\u007f");

    private readonly DockerClient _client;
    private readonly SemaphoreSlim _listSemaphore = new(1, 1);

    private IList<ContainerListResponse>? _cachedContainers;
    private long _containersCacheExpiration;
    private readonly System.Threading.Lock _containersCacheLock = new();

    private void InvalidateCache()
    {
        lock (_containersCacheLock)
        {
            _cachedContainers = null;
            _containersCacheExpiration = 0;
        }
    }

    private sealed class ContainerLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount { get; set; }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ContainerLock> _containerSemaphores = new(StringComparer.Ordinal);
    private readonly System.Threading.Lock _semaphoresLock = new();

    public DockerContainerManager(DockerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    private static void ValidateContainerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!System.Text.Ascii.IsValid(name))
        {
            throw new ArgumentException("Container name contains dangerous control or shell characters.", nameof(name));
        }
        if (name.AsSpan().ContainsAny(InvalidContainerNameChars))
        {
            throw new ArgumentException("Container name contains dangerous control or shell characters.", nameof(name));
        }
    }

    public async Task<IList<ContainerListResponse>> ListContainersAsync(CancellationToken ct = default)
    {
        using var activity = ContainerActivitySource.StartActivity("DockerContainerManager.ListContainers");
        lock (_containersCacheLock)
        {
            if (_cachedContainers != null && Environment.TickCount64 < _containersCacheExpiration)
            {
                return _cachedContainers;
            }
        }

        await _listSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_containersCacheLock)
            {
                if (_cachedContainers != null && Environment.TickCount64 < _containersCacheExpiration)
                {
                    return _cachedContainers;
                }
            }

            var containers = await _client.Containers.ListContainersAsync(
              new ContainersListParameters { All = true, Limit = 250 }, ct).ConfigureAwait(false);

            lock (_containersCacheLock)
            {
                _cachedContainers = containers;
                _containersCacheExpiration = Environment.TickCount64 + 500;
            }
            return containers;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_containersCacheLock)
            {
                if (_cachedContainers != null)
                {
                    return _cachedContainers;
                }
            }
            ContainerTelemetry.TrackError("DockerContainerManager", "ListContainersAsync failed", ex);
            return Array.Empty<ContainerListResponse>();
        }
        finally
        {
            _listSemaphore.Release();
        }
    }

    public async ValueTask<string> GetContainerStateAsync(string containerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId)) return "unknown";
        IList<ContainerListResponse>? cached = null;
        lock (_containersCacheLock)
        {
            if (_cachedContainers != null && Environment.TickCount64 < _containersCacheExpiration)
            {
                cached = _cachedContainers;
            }
        }
        if (cached != null)
        {
            foreach (var c in cached)
            {
                if (c != null && string.Equals(c.ID, containerId, StringComparison.Ordinal))
                {
                    return c.State ?? "unknown";
                }
            }
        }
        try
        {
            var inspect = await InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            return inspect?.State?.Status ?? "unknown";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            return "unknown";
        }
    }

    public async Task<ContainerInspectResponse?> InspectContainerAsync(string containerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId)) return null;
        try
        {
            ValidateContainerName(containerId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        try
        {
            var res = await _client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            if (res != null)
            {
                res.NetworkSettings ??= new NetworkSettings();
                res.NetworkSettings.Networks ??= new Dictionary<string, EndpointSettings>(4, StringComparer.Ordinal);
            }
            return res;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task StopContainerAsync(string containerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("Container ID cannot be null or whitespace.", nameof(containerId));
        }
        ValidateContainerName(containerId);
        try
        {
            await _client.Containers.StopContainerAsync(
              containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotModified || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already stopped or not found, consider success
        }
        finally
        {
            InvalidateCache();
        }
    }

    public async Task StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("Container ID cannot be null or whitespace.", nameof(containerId));
        }
        ValidateContainerName(containerId);

        ContainerLock containerLock;
        lock (_semaphoresLock)
        {
            containerLock = _containerSemaphores.GetOrAdd(containerId, _ => new ContainerLock());
            containerLock.RefCount++;
        }

        bool acquired = false;
        try
        {
            await containerLock.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            try
            {
                await _client.Containers.StartContainerAsync(
                  containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                // Already started, consider success
            }
            catch (DockerApiException ex) when (ex.ResponseBody != null && (ex.ResponseBody.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) || ex.ResponseBody.Contains("address already in use", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Failed to start container: One of the configured ports is already in use on the host system. Please verify your port mappings.", ex);
            }
        }
        finally
        {
            if (acquired)
            {
                containerLock.Semaphore.Release();
            }
            lock (_semaphoresLock)
            {
                containerLock.RefCount--;
                if (containerLock.RefCount == 0 && _containerSemaphores.TryRemove(containerId, out var removed))
                {
                    removed.Semaphore.Dispose();
                }
            }
            InvalidateCache();
        }
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("Container ID cannot be null or whitespace.", nameof(containerId));
        }
        ValidateContainerName(containerId);
        try
        {
            await _client.Containers.RemoveContainerAsync(
              containerId, new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already removed, consider success
        }
        finally
        {
            InvalidateCache();
        }
    }

    public async Task<string> GetContainerLogsAsync(string containerId, int tailLines = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("Container ID cannot be null or whitespace.", nameof(containerId));
        }
        ValidateContainerName(containerId);
        if (tailLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tailLines), "Tail lines cannot be negative.");
        }
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        var charBuf = System.Buffers.ArrayPool<char>.Shared.Rent(System.Text.Encoding.UTF8.GetMaxCharCount(buffer.Length));
        try
        {
            var tailStr = tailLines switch
            {
                0 => "0",
                50 => "50",
                100 => "100",
                500 => "500",
                1000 => "1000",
                _ => tailLines.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };

            using var stream = await _client.Containers.GetContainerLogsAsync(
              containerId,
              false,
              new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = tailStr },
              ct).ConfigureAwait(false);

            var output = new System.Text.StringBuilder();
            var decoder = System.Text.Encoding.UTF8.GetDecoder();

            while (!ct.IsCancellationRequested)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (result.EOF)
                {
                    break;
                }
                var charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                output.Append(charBuf, 0, charCount);
            }
            var remaining = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuf, 0, flush: true);
            if (remaining > 0)
            {
                output.Append(charBuf, 0, remaining);
            }
            return output.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerContainerManager", $"GetContainerLogsAsync failed for container {containerId}", ex);
            return $"Error fetching logs: {ex.Message}";
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            System.Buffers.ArrayPool<char>.Shared.Return(charBuf, clearArray: true);
        }
    }

    public IAsyncEnumerable<string> StreamContainerLogsAsync(string containerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("Container ID cannot be null or whitespace.", nameof(containerId));
        }
        ValidateContainerName(containerId);
        return StreamContainerLogsInternalAsync(containerId, ct);
    }

    private async IAsyncEnumerable<string> StreamContainerLogsInternalAsync(string containerId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ContainerLock containerLock;
        lock (_semaphoresLock)
        {
            containerLock = _containerSemaphores.GetOrAdd(containerId, _ => { return new ContainerLock(); });
            containerLock.RefCount++;
        }
        bool acquired = false;
        byte[]? buffer = null;
        char[]? charBuf = null;
        try
        {
            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
            charBuf = System.Buffers.ArrayPool<char>.Shared.Rent(System.Text.Encoding.UTF8.GetMaxCharCount(buffer.Length));

            await containerLock.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            using var stream = await _client.Containers.GetContainerLogsAsync(
              containerId,
              false,
              new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true, Tail = "500" },
              ct).ConfigureAwait(false);

            var decoder = System.Text.Encoding.UTF8.GetDecoder();
            var sb = new System.Text.StringBuilder(1024);

            while (!ct.IsCancellationRequested)
            {
                global::Docker.DotNet.MultiplexedStream.ReadResult result;
                try
                {
                    result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException && ex is not ObjectDisposedException)
                {
                    ContainerTelemetry.TrackError("DockerContainerManager", "StreamContainerLogsAsync read failed", ex);
                    throw;
                }

                if (result.EOF)
                {
                    break;
                }

                var charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                int start = 0;
                for (int i = 0; i < charCount; i++)
                {
                    if (charBuf[i] == '\n')
                    {
                        int length = i - start;
                        if (length > 0 && charBuf[start + length - 1] == '\r')
                        {
                            length--;
                        }

                        var slice = new ReadOnlySpan<char>(charBuf, start, length);
                        if (sb.Length > 0)
                        {
                            sb.Append(slice);
                            if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
                            {
                                sb.Length--;
                            }
                            yield return sb.ToString();
                            sb.Clear();
                        }
                        else
                        {
                            yield return new string(slice);
                        }
                        start = i + 1;
                    }
                }

                if (start < charCount)
                {
                    sb.Append(charBuf, start, charCount - start);
                }
            }

            var remaining = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuf, 0, flush: true);
            if (remaining > 0)
            {
                sb.Append(charBuf, 0, remaining);
            }

            if (sb.Length > 0)
            {
                int len = sb.Length;
                while (len > 0 && (sb[len - 1] == '\r' || sb[len - 1] == '\n'))
                {
                    len--;
                }
                if (len > 0)
                {
                    yield return sb.ToString(0, len);
                }
            }
        }
        finally
        {
            if (buffer != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
            if (charBuf != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(charBuf, clearArray: true);
            }
            if (acquired)
            {
                containerLock.Semaphore.Release();
            }
            lock (_semaphoresLock)
            {
                containerLock.RefCount--;
                if (containerLock.RefCount == 0 && _containerSemaphores.TryRemove(containerId, out var removed))
                {
                    removed.Semaphore.Dispose();
                }
            }
        }
    }
}
