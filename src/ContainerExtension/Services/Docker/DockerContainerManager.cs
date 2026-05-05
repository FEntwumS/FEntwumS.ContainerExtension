using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Handles container lifecycle operations (List, Start, Stop, Remove) via the Docker.DotNet SDK.
/// </summary>
public sealed class DockerContainerManager
{
    private readonly DockerClient _client;

    public DockerContainerManager(DockerClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Lists all containers (running and stopped) from the Docker daemon.
    /// Used by the Docker Desktop dashboard containers section.
    /// </summary>
    public async Task<IList<ContainerListResponse>> ListContainersAsync(CancellationToken ct = default)
    {
        try
        {
            return await _client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Limit = 50 }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerContainerManager", "ListContainersAsync failed", ex);
            return Array.Empty<ContainerListResponse>();
        }
    }

    /// <summary>
    /// Stops a specific container by ID. Used by the dashboard's stop button.
    /// </summary>
    public async Task StopContainerAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Containers.StopContainerAsync(
            containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a specific stopped container by ID. Used by the dashboard's start button.
    /// </summary>
    public async Task StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Containers.StartContainerAsync(
            containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a specific container by ID. Used by the dashboard's remove button.
    /// </summary>
    public async Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Containers.RemoveContainerAsync(
            containerId, new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the last <paramref name="tailLines"/> lines of a container's logs.
    /// Used by the dashboard's log viewer button.
    /// </summary>
    public async Task<string> GetContainerLogsAsync(string containerId, int tailLines = 50, CancellationToken ct = default)
    {
        try
        {
            using var stream = await _client.Containers.GetContainerLogsAsync(
                containerId,
                false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = tailLines.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ct).ConfigureAwait(false);

            var output = new System.Text.StringBuilder();
            var buffer = new byte[8192];
            // Stateful decoder: caches trailing incomplete UTF-8 bytes across 8KB chunk
            // boundaries, preventing multi-byte characters (emoji, CJK, Cyrillic) from
            // being split and replaced with \uFFFD replacement characters.
            var decoder = System.Text.Encoding.UTF8.GetDecoder();
            var charBuf = new char[System.Text.Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            while (!ct.IsCancellationRequested)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (result.EOF) break;
                var charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                output.Append(charBuf, 0, charCount);
            }
            return output.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Error fetching logs: {ex.Message}";
        }
    }
}
