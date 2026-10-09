using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ContainerExtension.UnitTests;

/// <summary>
/// A stand-in for a Docker daemon on 127.0.0.1, so that a test can see which API version a client asks for
/// without a daemon. It answers <c>GET /version</c> with the API version it was given, or without one,
/// <c>GET /_ping</c> with <c>OK</c> and any other request with 404, closes each connection after its answer and
/// records the path of every request.
/// </summary>
internal sealed class DockerDaemonDouble : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string? _apiVersion;
    private readonly string _versionStatus;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _paths = new();
    private readonly Task _accepting;

    private DockerDaemonDouble(int port, string? apiVersion, string versionStatus)
    {
        _apiVersion = apiVersion;
        _versionStatus = versionStatus;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _accepting = AcceptAsync();
    }

    /// <summary>The paths of the requests received so far, in order.</summary>
    public IReadOnlyCollection<string> Paths => _paths;

    /// <summary>
    /// Starts a stand-in daemon on <paramref name="port"/> that reports <paramref name="apiVersion"/>, with
    /// <paramref name="versionStatus"/> as the status of its answer to the version request.
    /// </summary>
    public static DockerDaemonDouble Start(int port, string? apiVersion, string versionStatus = "200 OK") => new(port, apiVersion, versionStatus);

    /// <summary>A port on 127.0.0.1 that nothing listens on, so a connection to it is refused until a double starts there.</summary>
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = AnswerAsync(connection);
        }
    }

    private async Task AnswerAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                var stream = connection.GetStream();
                var request = await ReadHeadAsync(stream);
                var parts = request.Split(' ', 3);
                var path = parts.Length > 1 ? parts[1] : string.Empty;
                _paths.Enqueue(path);

                var (status, body) = path.EndsWith("/version", StringComparison.Ordinal)
                    ? (_versionStatus, _apiVersion is null ? "{}" : $"{{\"ApiVersion\":\"{_apiVersion}\",\"Version\":\"27.0.0\"}}")
                    : path.EndsWith("/_ping", StringComparison.Ordinal)
                        ? ("200 OK", "OK")
                        : ("404 Not Found", "{\"message\":\"not served by the double\"}");
                var content = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _stop.Token);
                await stream.WriteAsync(content, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The client went away or the double stopped; there is nobody left to answer.
            }
        }
    }

    // Reads the request line and headers up to the blank line; the requests a client sends here carry no body.
    private async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var received = new List<byte>();
        var buffer = new byte[1024];
        while (received.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, _stop.Token);
            if (read == 0)
            {
                break;
            }
            received.AddRange(buffer.AsSpan(0, read).ToArray());
            // Docker.DotNet ends its header lines with a bare line feed, so the head ends at the first empty line
            // whichever way its lines end.
            var text = Encoding.ASCII.GetString(received.ToArray()).Replace("\r", "", StringComparison.Ordinal);
            if (text.Contains("\n\n", StringComparison.Ordinal))
            {
                return text[..text.IndexOf('\n', StringComparison.Ordinal)];
            }
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try
        {
            _accepting.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends with the listener; how it ended does not matter here.
        }
        _stop.Dispose();
    }
}
