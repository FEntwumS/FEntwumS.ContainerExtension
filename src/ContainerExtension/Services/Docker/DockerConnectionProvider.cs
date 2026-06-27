using System;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Manages the connection lifecycle and health state of the Docker daemon.
/// Automatically detects disconnects, tracks system information, and provides a centralized 
/// IsConnected flag for UI and execution strategy checks.
/// </summary>
public sealed class DockerConnectionProvider : IDisposable
{
    private readonly DockerClient _client;
    private volatile bool _lastPingSucceeded = true;
    private volatile bool _lastSystemInfoSucceeded = true;
    private volatile bool _lastStateConnected = true;
    private readonly System.Threading.Lock _stateLock = new();

    private SystemInfoResponse? _cachedSystemInfo;
    private long _systemInfoCacheExpiration;
    private readonly System.Threading.Lock _systemInfoLock = new();

    private Task<SystemInfoResponse?>? _activeSystemInfoTask;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private volatile bool _disposed;

    public bool IsConnected => _lastPingSucceeded && _lastSystemInfoSucceeded && !_disposed;

    public SystemInfoResponse? CachedSystemInfo
    {
        get
        {
            lock (_systemInfoLock)
            {
                return _cachedSystemInfo;
            }
        }
    }

    public DockerConnectionProvider(DockerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async ValueTask<bool> PingAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_client.System == null)
        {
            lock (_stateLock)
            {
                _lastPingSucceeded = false;
                _lastStateConnected = false;
            }
            return false;
        }

        try
        {
            await _connectionSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Provider disposed concurrently while acquiring the connection gate.
            lock (_stateLock)
            {
                _lastPingSucceeded = false;
            }
            return false;
        }
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await _client.System.PingAsync(timeoutCts.Token).ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (!_lastStateConnected)
                    {
                        ContainerTelemetry.TrackError("DockerConnectionProvider", "Daemon ping recovered", null, "Connection restored");
                    }
                    _lastStateConnected = true;
                    _lastPingSucceeded = true;
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                lock (_stateLock)
                {
                    _lastPingSucceeded = false;
                }
                return false;
            }
            catch (ObjectDisposedException)
            {
                lock (_stateLock)
                {
                    _lastPingSucceeded = false;
                }
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_stateLock)
                {
                    if (_lastStateConnected)
                    {
                        _lastStateConnected = false;
                        _lastPingSucceeded = false;
                        ContainerTelemetry.TrackError("DockerConnectionProvider", "Daemon ping failed (first failure)", ex);
                    }
                    else
                    {
                        _lastPingSucceeded = false;
                    }
                }
                return false;
            }
        }
        finally
        {
            try { _connectionSemaphore.Release(); }
            catch (ObjectDisposedException) { /* provider disposed concurrently; nothing to release */ }
        }
    }

    public async Task<SystemInfoResponse?> GetSystemInfoAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task<SystemInfoResponse?> task;
        lock (_systemInfoLock)
        {
            if (_cachedSystemInfo != null && Environment.TickCount64 < _systemInfoCacheExpiration)
            {
                return _cachedSystemInfo;
            }
            if (_activeSystemInfoTask == null)
            {
                var t = RetrieveSystemInfoInternalAsync(CancellationToken.None);
                if (!t.IsCompleted)
                {
                    _activeSystemInfoTask = t;
                }
                task = t;
            }
            else
            {
                task = _activeSystemInfoTask;
            }
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<SystemInfoResponse?> RetrieveSystemInfoInternalAsync(CancellationToken ct)
    {
        if (_client.System == null)
        {
            lock (_stateLock)
            {
                _lastSystemInfoSucceeded = false;
            }
            lock (_systemInfoLock)
            {
                _activeSystemInfoTask = null;
            }
            return null;
        }

        try
        {
            await _connectionSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Provider disposed concurrently while acquiring the connection gate.
            lock (_stateLock)
            {
                _lastSystemInfoSucceeded = false;
            }
            return null;
        }
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                var info = await _client.System.GetSystemInfoAsync(timeoutCts.Token).ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (!_lastSystemInfoSucceeded)
                    {
                        ContainerTelemetry.TrackError("DockerConnectionProvider", "GetSystemInfoAsync recovered", null, "Connection restored");
                    }
                    _lastSystemInfoSucceeded = true;
                }
                lock (_systemInfoLock)
                {
                    _cachedSystemInfo = info;
                    _systemInfoCacheExpiration = Environment.TickCount64 + 10_000; // Cache for 10 seconds
                }
                return info;
            }
            catch (OperationCanceledException)
            {
                lock (_stateLock)
                {
                    _lastSystemInfoSucceeded = false;
                }
                return null;
            }
            catch (ObjectDisposedException)
            {
                lock (_stateLock)
                {
                    _lastSystemInfoSucceeded = false;
                }
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_stateLock)
                {
                    if (_lastSystemInfoSucceeded)
                    {
                        _lastSystemInfoSucceeded = false;
                        ContainerTelemetry.TrackError("DockerConnectionProvider", "GetSystemInfoAsync failed (first failure)", ex);
                    }
                }
                return null;
            }
        }
        finally
        {
            try { _connectionSemaphore.Release(); }
            catch (ObjectDisposedException) { /* provider disposed concurrently; nothing to release */ }
            lock (_systemInfoLock)
            {
                _activeSystemInfoTask = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connectionSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
