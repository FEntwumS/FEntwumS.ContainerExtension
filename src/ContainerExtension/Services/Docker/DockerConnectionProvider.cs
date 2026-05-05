using System;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Handles Docker daemon connectivity and system-level information queries.
/// </summary>
public sealed class DockerConnectionProvider
{
    private readonly DockerClient _client;

    public DockerConnectionProvider(DockerClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Pings the daemon to verify connectivity. Used by the health check on plugin load.
    /// </summary>
    /// <returns><c>true</c> if the daemon is reachable; <c>false</c> otherwise.</returns>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.System.PingAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerConnectionProvider", "Daemon ping failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Retrieves system-level information from the Docker daemon (version, OS, memory, CPUs).
    /// Used by the Docker Desktop dashboard status panel.
    /// </summary>
    public async Task<SystemInfoResponse?> GetSystemInfoAsync(CancellationToken ct = default)
    {
        try { return await _client.System.GetSystemInfoAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerConnectionProvider", "GetSystemInfoAsync failed", ex);
            return null;
        }
    }
}
