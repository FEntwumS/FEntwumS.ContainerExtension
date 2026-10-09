using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using OneWare.Essentials.Services;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Resolves the Docker daemon endpoint (custom socket / <c>DOCKER_HOST</c> / OS defaults), verifies it,
/// negotiates the Docker API version, and constructs a connected client together with its connection/image/
/// container managers. Extracted from <see cref="DockerExecutionStrategy"/> so daemon bootstrap is a single
/// self-contained step with an explicit result.
/// </summary>
internal static partial class DockerConnectionFactory
{
    /// <summary>
    /// The API version of a client whose daemon did not say which it speaks: no daemon answered the version
    /// request, or its answer carried no version. Of the two versions used for this before, 1.44 and 1.45, it is
    /// the one more daemons accept.
    /// </summary>
    internal static readonly System.Version FallbackApiVersion = new(1, 44);

    /// <summary>
    /// Outcome of a connection attempt. <see cref="Client"/> and the managers are non-null only on success;
    /// on failure they are null while <see cref="DetectedRuntime"/> and <see cref="DaemonUri"/> still reflect
    /// whatever was resolved before the failure, so the dashboard can report the intended runtime/endpoint.
    /// </summary>
    internal sealed record Connection(
        string DetectedRuntime,
        Uri? DaemonUri,
        DockerClient? Client,
        DockerConnectionProvider? ConnectionProvider,
        DockerImageManager? ImageManager,
        DockerContainerManager? ContainerManager)
    {
        /// <summary>
        /// Whether a daemon answered the version request while the connection was built. Without an answer the
        /// client uses <see cref="FallbackApiVersion"/> and may point at a socket that nobody serves yet.
        /// </summary>
        internal bool DaemonAnswered { get; init; }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][-a-zA-Z0-9.]*(?::\d{1,5})?$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostOnlyRegex();

    internal static async Task<Connection> CreateAsync(ISettingsService settings, CancellationToken ct)
    {
        string runtime = "";
        Uri? uri = null;
        DockerClient? client = null;
        DockerConnectionProvider? connectionProvider = null;
        try
        {
            var customSocket = settings.SafeGetSetting<string>(ContainerExtensionModule.DaemonSocketSetting, "");
            var envDockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

            var uriText = !string.IsNullOrWhiteSpace(customSocket) ? customSocket : (!string.IsNullOrWhiteSpace(envDockerHost) ? envDockerHost : null);
            var resolved = false;

            if (!string.IsNullOrWhiteSpace(uriText))
            {
                try
                {
                    if (uriText.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isLocal = uriText.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                                       uriText.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                       uriText.Contains("[::1]", StringComparison.Ordinal);
                        if (!isLocal)
                        {
                            await Console.Out.WriteLineAsync("[WARN] Insecure HTTP custom daemon socket requested. Upgrading to https://").ConfigureAwait(false);
                            uriText = "https" + uriText[4..];
                        }
                    }

                    // A Windows device-path pipe (\\.\pipe\<name>) is a valid daemon socket but not a valid
                    // URI, so new Uri() would throw and the value would silently fall through to the default
                    // docker_engine pipe below. Convert it to the equivalent npipe URI form so a custom pipe
                    // is honored. (DaemonSocketValidation accepts the device-path form.)
                    if (uriText.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
                    {
                        uriText = "npipe://./pipe/" + uriText[@"\\.\pipe\".Length..].Replace('\\', '/');
                    }

                    // Docker's own default address for the Windows pipe has four slashes,
                    // npipe:////./pipe/docker_engine. The URI parser reads that form as an empty host and the
                    // path //pipe/<name>, which Docker.DotNet rejects as no npipe URI, so creating the client
                    // failed. Rewrite it to the npipe://./pipe/<name> form.
                    if (uriText.StartsWith("npipe:////", StringComparison.OrdinalIgnoreCase))
                    {
                        uriText = "npipe://" + uriText["npipe:////".Length..];
                    }

                    uri = new Uri(uriText);
                    if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isLocal = uri.Host != null && (
                                       uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                                       uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                       uri.Host.Equals("::1", StringComparison.Ordinal));
                        if (!isLocal)
                        {
                            await Console.Out.WriteLineAsync("[WARN] Insecure HTTP custom daemon socket scheme. Upgrading to HTTPS.").ConfigureAwait(false);
                            uri = new UriBuilder(uri) { Scheme = "https" }.Uri;
                        }
                    }

                    if (uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
                    {
                        var hostOnly = uri.Host;
                        if (string.IsNullOrEmpty(hostOnly) || !HostOnlyRegex().IsMatch(hostOnly))
                        {
                            throw new UriFormatException("Insecure or invalid SSH tunnel hostname.");
                        }
                    }

                    var isNetworkScheme = uri.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
                                          uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                                          uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                    if (isNetworkScheme && uri.Host != null)
                    {
                        var hostType = Uri.CheckHostName(uri.Host);
                        if (hostType == UriHostNameType.Unknown)
                        {
                            throw new UriFormatException("Invalid remote daemon hostname.");
                        }

                        if (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                            !uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                            !uri.Host.Equals("::1", StringComparison.Ordinal))
                        {
                            var warningMsg = $"[SECURITY WARNING] Connecting to a remote Docker daemon at '{uri.Host}'. Outbound traffic may expose credentials.";
                            await Console.Error.WriteLineAsync(warningMsg).ConfigureAwait(false);
                            ContainerTelemetry.TrackError("DockerExecutionStrategy", "RemoteDaemonWarning", null, warningMsg);
                        }
                    }
                    runtime = uriText.Contains("podman", StringComparison.OrdinalIgnoreCase) ? "podman" : "docker (custom)";
                    resolved = true;
                }
                catch (UriFormatException)
                {
                    resolved = false;
                }
            }
            else
            {
                runtime = "";
            }

            if (!resolved)
            {
                if (OperatingSystem.IsWindows())
                {
                    uri = new Uri("npipe://./pipe/docker_engine");
                    runtime = "docker";
                }
                else
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        (uri, runtime) = await DaemonEndpointValidator.ProbeUnixSocketAsync(probeCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        uri = new Uri("unix:///var/run/docker.sock");
                        runtime = "docker (default)";
                        // A cancellation by the caller is no fault of the probe.
                        if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            ContainerTelemetry.TrackError("DockerExecutionStrategy", "ProbeUnixSocket failed, falling back to default", ex);
                        }
                    }
                }
            }

            if (uri is null)
            {
                throw new DockerExecutionException("Could not resolve a Docker daemon URI. Ensure Docker is installed and running, or set the DOCKER_HOST environment variable.");
            }

            if (uri.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase))
            {
                var pipeName = uri.AbsolutePath.TrimStart('/');
                if (pipeName.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase))
                {
                    pipeName = pipeName[5..];
                }
                if (string.IsNullOrEmpty(pipeName))
                {
                    pipeName = "docker_engine";
                }
                if (!await DaemonEndpointValidator.VerifyWindowsNamedPipeAsync(pipeName, settings.SafeGetSetting(ContainerExtensionModule.BypassNamedPipeCheckSetting, false), ct: ct).ConfigureAwait(false))
                {
                    throw new DockerExecutionException($"Insecure named pipe connection detected for '{pipeName}'. Connection aborted. If this is a false positive, you can bypass this check in OneWare Studio Settings under 'Binary Management' -> 'Container Engine' -> check 'Bypass Named Pipe Security Check'.");
                }
            }

            using var config = uri.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase)
                ? new DockerClientConfiguration(uri, new DaemonEndpointValidator.SecureNamedPipeCredentials(uri),
                    namedPipeConnectTimeout: DaemonEndpointValidator.SecureNamedPipeCredentials.ConnectTimeout)
                : new DockerClientConfiguration(uri);
            var (apiVersion, daemonAnswered) = await NegotiateApiVersionAsync(config, ct).ConfigureAwait(false);

            client = config.CreateClient(apiVersion);
            connectionProvider = new DockerConnectionProvider(client);
            var imageManager = new DockerImageManager(client, settings);
            var containerManager = new DockerContainerManager(client);

            return new Connection(runtime, uri, client, connectionProvider, imageManager, containerManager) { DaemonAnswered = daemonAnswered };
        }
        catch (Exception ex)
        {
            connectionProvider?.Dispose();
            client?.Dispose();
            // A cancellation by the caller is no fault of the connection.
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", "Asynchronous daemon connection initialization failed", ex);
            }
            return new Connection(runtime, uri, null, null, null, null);
        }
    }

    // Ask the daemon for its API version, bounded by a short timeout, and fall back to a safe default on any
    // genuine failure. A cold daemon can need well over the first-connect budget, so this is bounded at 3 s
    // (honouring shutdown) rather than misnegotiating a healthy-but-slow daemon down to the fallback version.
    // Also says whether a daemon answered at all.
    private static async Task<(System.Version Version, bool DaemonAnswered)> NegotiateApiVersionAsync(DockerClientConfiguration config, CancellationToken ct)
    {
        System.Version apiVersion = FallbackApiVersion;
        var daemonAnswered = false;
        var tempClient = config.CreateClient();
        try
        {
            using var verCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            verCts.CancelAfter(TimeSpan.FromSeconds(3));
            var version = await tempClient.System.GetVersionAsync(verCts.Token).ConfigureAwait(false);
            daemonAnswered = true;
            var apiVerStr = version?.APIVersion;
            if (!string.IsNullOrEmpty(apiVerStr))
            {
                int endIdx = 0;
                while (endIdx < apiVerStr.Length && (char.IsDigit(apiVerStr[endIdx]) || apiVerStr[endIdx] == '.'))
                {
                    endIdx++;
                }
                if (System.Version.TryParse(apiVerStr[..endIdx], out var parsedVersion))
                {
                    apiVersion = parsedVersion;
                }
            }
        }
        catch (Exception ex)
        {
            // A daemon that answers with an error status answers all the same; building the connection anew would not
            // change its answer.
            daemonAnswered = ex is DockerApiException;
            if (!IsDaemonOffline(ex))
            {
                ContainerTelemetry.TrackError("DockerExecutionStrategy", $"API version negotiation failed; falling back to {FallbackApiVersion}", ex);
            }
            apiVersion = FallbackApiVersion;
        }
        finally
        {
            tempClient.Dispose();
        }
        return (apiVersion, daemonAnswered);
    }

    // Whether a failed request shows only that no daemon answers, which is no error worth recording: a timeout
    // or cancellation, a refused or missing socket, or a named pipe that nobody serves, whose connect gives up
    // with a TimeoutException.
    internal static bool IsDaemonOffline(Exception ex) =>
        ex is OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException ||
        ex.InnerException is System.Net.Sockets.SocketException ||
        (ex is HttpRequestException httpEx && (httpEx.InnerException is System.Net.Sockets.SocketException || httpEx.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)));
}
