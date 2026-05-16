using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerExtension.Services.Docker;

public sealed class DockerContainerManager
{
    private readonly DockerClient _client;

    public DockerContainerManager(DockerClient client)
    {
        _client = client;
    }

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

    public async Task StopContainerAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Containers.StopContainerAsync(
            containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 5 }, ct).ConfigureAwait(false);
    }

    public async Task StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Containers.StartContainerAsync(
            containerId, new ContainerStartParameters(), ct).ConfigureAwait(false);
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Containers.RemoveContainerAsync(
            containerId, new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
    }

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
            var decoder = System.Text.Encoding.UTF8.GetDecoder();
            var charBuf = new char[System.Text.Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            
            while (!ct.IsCancellationRequested)
            {
                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (result.EOF) break;
                var charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
                output.Append(charBuf, 0, charCount);
            }
            var remaining = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuf, 0, flush: true);
            if (remaining > 0) output.Append(charBuf, 0, remaining);
            return output.ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"Error fetching logs: {ex.Message}";
        }
    }
    
    /// <summary>
    /// Opens an endless stream from the native multiplexer yielding individual standard out/err lines.
    /// Completely avoids using the OS bash/process.Start to fetch follow logs.
    /// </summary>
    public async IAsyncEnumerable<string> StreamContainerLogsAsync(string containerId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var stream = await _client.Containers.GetContainerLogsAsync(
            containerId,
            false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = true, Tail = "500" },
            ct).ConfigureAwait(false);

        var buffer = new byte[8192];
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        var charBuf = new char[System.Text.Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var sb = new System.Text.StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            global::Docker.DotNet.MultiplexedStream.ReadResult result;
            try
            {
                result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (EndOfStreamException) { break; }
            catch (ObjectDisposedException) { break; }
            
            if (result.EOF) break;

            var charCount = decoder.GetChars(buffer, 0, result.Count, charBuf, 0, flush: false);
            var text = new string(charBuf, 0, charCount);
            
            sb.Append(text);
            int start = 0;
            for (int i = 0; i < sb.Length; i++)
            {
                if (sb[i] == '\n')
                {
                    int lineEnd = (i > start && sb[i - 1] == '\r') ? i - 1 : i;
                    yield return sb.ToString(start, lineEnd - start);
                    start = i + 1;
                }
            }
            
            if (start > 0)
            {
                if (start < sb.Length) sb.Remove(0, start);
                else sb.Clear();
            }
        }
    }
}