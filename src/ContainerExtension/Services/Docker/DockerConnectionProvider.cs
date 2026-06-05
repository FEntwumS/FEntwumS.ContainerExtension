using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerExtension.Services.Docker;

public sealed class DockerConnectionProvider
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

    public bool IsConnected => _lastPingSucceeded && _lastSystemInfoSucceeded;

    public DockerConnectionProvider(DockerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public static bool ValidateSocketPath(string path, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(path)) return true;

        if (path.Contains("..", StringComparison.Ordinal) || path.Contains("/../") || path.Contains("\\..\\"))
        {
            errorMessage = "Socket path cannot contain directory traversal (..).";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            if (path.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Unix sockets are not supported on Windows.";
                return false;
            }
        }
        else
        {
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "Windows named pipes are not supported on Unix.";
                return false;
            }
            if (path.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
            {
                var socketFilePath = path["unix://".Length..];
                if (string.IsNullOrWhiteSpace(socketFilePath))
                {
                    errorMessage = "Unix socket path cannot be empty.";
                    return false;
                }
            }
        }
        return true;
    }

    public static bool VerifyNamedPipeSafe(string pipeName, out string? errorMessage)
    {
        errorMessage = null;
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var normalized = pipeName.Replace("npipe://", "", StringComparison.OrdinalIgnoreCase).Replace("/", "\\");
            if (!normalized.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                errorMessage = "Windows named pipes must reside in the local pipe namespace (\\\\.\\pipe\\).";
                return false;
            }
            var fullPath = Path.GetFullPath(pipeName);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                errorMessage = "Named pipe path is empty after normalization.";
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            errorMessage = $"Socket configuration error: Failed to validate named pipe path. Details: {ex.Message}";
            return false;
        }

        return true;
    }

    public async ValueTask<bool> PingAsync(CancellationToken ct = default)
    {
        if (_client == null || _client.System == null)
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
            await _client.System.PingAsync(ct).ConfigureAwait(false);
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

    public Task<SystemInfoResponse?> GetSystemInfoAsync(CancellationToken ct = default)
    {
        lock (_systemInfoLock)
        {
            if (_cachedSystemInfo != null && Environment.TickCount64 < _systemInfoCacheExpiration)
            {
                return Task.FromResult<SystemInfoResponse?>(_cachedSystemInfo);
            }
            if (_activeSystemInfoTask != null)
            {
                return _activeSystemInfoTask;
            }
            _activeSystemInfoTask = RetrieveSystemInfoInternalAsync(ct);
            return _activeSystemInfoTask;
        }
    }

    private async Task<SystemInfoResponse?> RetrieveSystemInfoInternalAsync(CancellationToken ct)
    {
        try
        {
            var info = await _client.System.GetSystemInfoAsync(ct).ConfigureAwait(false);
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
        finally
        {
            lock (_systemInfoLock)
            {
                _activeSystemInfoTask = null;
            }
        }
    }
}
