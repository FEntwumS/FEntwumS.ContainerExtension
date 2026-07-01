using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ContainerExtension.Registry;

/// <summary>
/// Represents an error that occurs during a registry connection or operation.
/// </summary>
public sealed class RegistryConnectionException : Exception
{
    public RegistryConnectionException(string message) : base(message) { }
    public RegistryConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Static client for listing tags from Docker/OCI registries (e.g. ghcr.io, Docker Hub). Resolves
/// Bearer-token challenges, scopes forwarded credentials to the matching host, and rejects references
/// that would steer requests at loopback or internal addresses (SSRF defense).
/// </summary>
public static partial class RegistryClient
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<type>token|bearer)(?<sep>\s*=\s*|\s+)[a-zA-Z0-9_\-\.\+\/=]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex SecretScrubRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<key>[a-zA-Z0-9_\-\.]+)\s*=\s*""(?<value>[^""]*)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture | System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex ChallengeParameterRegex();

    private static readonly string CachedUserName = Environment.UserName;
    private static readonly string CachedUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly Lazy<HttpClient> HttpClientLazy = new(CreateHttpClient, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly FrozenSet<string> GhcrRegistries = new[] { "ghcr.io" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (System.Net.IPAddress[] ips, long cacheTimeTicks)> DnsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Threading.Lock CacheEvictionLock = new();
    private static readonly System.Buffers.SearchValues<char> ControlChars = System.Buffers.SearchValues.Create(
        "\0\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\v\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f\u007f\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008a\u008b\u008c\u008d\u008e\u008f\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009a\u009b\u009c\u009d\u009e\u009f");

    private static HttpClient HttpClient
    {
        get
        {
            return HttpClientLazy.Value;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            AllowAutoRedirect = false,
            // Enforce the SSRF address gate at connection time (see ValidatedConnectAsync): resolve the
            // target, dial the vetted IP directly, and refuse internal ranges. This backstops the
            // reference-level check in FetchTagsAsync against a WWW-Authenticate realm on an internal
            // host or a DNS rebind between the pre-flight resolve and the socket connect.
            ConnectCallback = ValidatedConnectAsync,
            AutomaticDecompression = System.Net.DecompressionMethods.Brotli | System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ContainerExtension/1.0");
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return client;
    }

    // Resolve the target host and connect to a vetted address directly, refusing any loopback, private,
    // CGNAT, or link-local destination. Because the socket is dialled at the exact IP that passed the
    // gate, a DNS record that rebinds after resolution — or a server-supplied realm/redirect naming an
    // internal host — cannot steer the connection at the local network. Explicit loopback registries
    // (a developer's own localhost registry) stay reachable.
    private static async ValueTask<Stream> ValidatedConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        var allowLoopback = IsLoopbackRegistry(host);
        var addresses = await ResolveDnsAsync(host, token).ConfigureAwait(false);
        var target = Array.Find(addresses, ip => allowLoopback || !IsDisallowedAddress(ip));
        if (target is null)
        {
            throw new IOException($"Refusing to connect to '{host}': it resolves only to disallowed internal addresses.");
        }

        var socket = new System.Net.Sockets.Socket(target.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new System.Net.IPEndPoint(target, context.DnsEndPoint.Port), token).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<System.Net.IPAddress[]> ResolveDnsAsync(string host, CancellationToken ct)
    {
        if (DnsCache.TryGetValue(host, out var cached) && (Environment.TickCount64 - cached.cacheTimeTicks) < 60_000)
        {
            return cached.ips;
        }
        var ips = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        lock (CacheEvictionLock)
        {
            if (DnsCache.Count >= 50)
            {
                var currentTicks = Environment.TickCount64;
                var expiredKeys = DnsCache.Where(kvp => (currentTicks - kvp.Value.cacheTimeTicks) >= 60_000)
                                          .Select(kvp => kvp.Key)
                                          .ToList();
                foreach (var k in expiredKeys)
                {
                    DnsCache.TryRemove(k, out _);
                }

                if (DnsCache.Count >= 50)
                {
                    var oldestKeys = DnsCache.OrderBy(kvp => kvp.Value.cacheTimeTicks)
                                             .Take(DnsCache.Count - 45)
                                             .Select(kvp => kvp.Key)
                                             .ToList();
                    foreach (var k in oldestKeys)
                    {
                        DnsCache.TryRemove(k, out _);
                    }
                }
            }
            DnsCache[host] = (ips, Environment.TickCount64);
        }
        return ips;
    }

    // Treat a registry only as loopback when the host (port stripped) is exactly localhost or a
    // loopback IP. A prefix test would accept attacker-controlled names such as 127.0.0.1.evil.com,
    // which are publicly routable yet would otherwise be contacted over cleartext HTTP.
    private static bool IsLoopbackRegistry(string registryHost)
    {
        if (string.IsNullOrEmpty(registryHost))
        {
            return false;
        }
        var colon = registryHost.IndexOf(':', StringComparison.Ordinal);
        var hostOnly = colon < 0 ? registryHost : registryHost[..colon];
        if (string.Equals(hostOnly, "localhost", StringComparison.Ordinal) ||
            string.Equals(hostOnly, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(hostOnly, "::1", StringComparison.Ordinal))
        {
            return true;
        }
        return System.Net.IPAddress.TryParse(hostOnly, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    // SSRF gate: reject any address that targets the local host or a non-routable / internal range.
    private static bool IsDisallowedAddress(System.Net.IPAddress address)
    {
        // Collapse IPv4-mapped IPv6 (::ffff:a.b.c.d) to IPv4 so the private/CGNAT/link-local byte tests
        // below also catch mapped forms, and reject the unspecified/wildcard addresses outright. Without
        // this, ::ffff:10.0.0.1 or 0.0.0.0 would slip past the allowlist to an internal target.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any))
        {
            return true;
        }
        if (System.Net.IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
        {
            return true;
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10) return true;                                  // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;                 // 169.254.0.0/16 link-local
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;   // 100.64.0.0/10 CGNAT
        }
        return false;
    }

    private static bool IsValidRegistryIdentifier(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return true;
        }
        if (input.Length > 255)
        {
            return false;
        }
        foreach (var c in input)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-' && c != '_' && c != '/')
            {
                return false;
            }
        }

        var span = input.AsSpan();
        int start = 0;
        while (start < span.Length)
        {
            var idx = span[start..].IndexOf('/');
            var length = idx < 0 ? span.Length - start : idx;
            var seg = span.Slice(start, length);
            if (seg.IsEmpty || seg.Equals(".", StringComparison.Ordinal) || seg.Equals("..", StringComparison.Ordinal))
            {
                return false;
            }
            if (idx < 0) break;
            start += length + 1;
        }
        return true;
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        int maxAttempts = 3;
        int delayMs = 1000;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var reqClone = await CloneHttpRequestMessageAsync(request).ConfigureAwait(false);
            try
            {
                var response = await HttpClient.SendAsync(reqClone, ct).ConfigureAwait(false);
                if (attempt < maxAttempts)
                {
                    if (response.StatusCode == (System.Net.HttpStatusCode)429)
                    {
                        int retryAfterSeconds = 2;
                        if (response.Headers.RetryAfter != null)
                        {
                            if (response.Headers.RetryAfter.Delta.HasValue)
                            {
                                var seconds = response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                                retryAfterSeconds = seconds is >= 1.0 and <= 3600.0 ? (int)seconds : 2;
                            }
                            else if (response.Headers.RetryAfter.Date.HasValue)
                            {
                                var seconds = (response.Headers.RetryAfter.Date.Value.UtcDateTime - DateTime.UtcNow).TotalSeconds;
                                retryAfterSeconds = seconds is >= 1.0 and <= 3600.0 ? (int)seconds : 2;
                            }
                        }
                        response.Dispose();
                        retryAfterSeconds = Math.Clamp(retryAfterSeconds, 1, 10);
                        await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), ct).ConfigureAwait(false);
                        continue;
                    }

                    if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
                    {
                        response.Dispose();
                        await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        delayMs *= 2;
                        continue;
                    }
                }
                return response;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is HttpRequestException || ex is TaskCanceledException || ex is System.Net.Sockets.SocketException))
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
        }
        throw new RegistryConnectionException("HTTP request failed after maximum retry attempts.");
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestMessageAsync(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        clone.Version = req.Version;
        foreach (var header in req.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var prop in req.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(prop.Key), prop.Value);
        }
        if (req.Content != null)
        {
            var ms = new MemoryStream();
            await req.Content.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            foreach (var header in req.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return clone;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<string> tags, long cacheTimeTicks)> TagsCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Task<List<string>>> ActiveFetches = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<List<string>> FetchTagsAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return [];
        }

        foreach (var c in imageReference)
        {
            if (!char.IsAscii(c) || char.IsControl(c))
            {
                return [];
            }
        }

        if (TagsCache.TryGetValue(imageReference, out var cached))
        {
            var maxAgeMs = cached.tags.Count == 0 ? 10_000 : 60_000;
            if ((Environment.TickCount64 - cached.cacheTimeTicks) < maxAgeMs)
            {
                // Hand out a copy: callers (e.g. a ViewModel sorting a combo box) must not mutate the
                // shared cached list.
                return new List<string>(cached.tags);
            }
        }

        Task<List<string>> task;
        lock (ActiveFetches)
        {
            if (ActiveFetches.TryGetValue(imageReference, out var existingTask))
            {
                task = existingTask;
            }
            else
            {
                string key = imageReference;
                task = Task.Run(async () =>
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));
                    var ct = cts.Token;
                    try
                    {
                        var parts = ParseImageReference(key);

                        bool isLoopback = IsLoopbackRegistry(parts.Registry);

                        // A registry segment is a bare host identifier: IsValidRegistryIdentifier below
                        // rejects any ':' (so a scheme-bearing "http://host" is dropped to an empty result),
                        // and HTTPS is enforced by construction when the request URL is built. No separate
                        // substring "http" check is needed — and an unanchored one false-rejected legitimate
                        // hosts like "http.example.com".
                        if (!IsValidRegistryIdentifier(parts.Namespace) || !IsValidRegistryIdentifier(parts.Repository) || !IsValidRegistryIdentifier(parts.Registry))
                        {
                            AddToCache(key, []);
                            return [];
                        }

                        var hostToResolve = string.IsNullOrEmpty(parts.Registry) ? "hub.docker.com" : parts.Registry;
                        // SSRF gate. The well-known public endpoints and explicit loopback are exempt; any
                        // other host is rejected when DNS resolves it to a loopback or internal address,
                        // preventing a crafted reference from steering requests at the local network.
                        bool isKnownPublic = string.IsNullOrEmpty(parts.Registry) ||
                            string.Equals(parts.Registry, "docker.io", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(parts.Registry, "ghcr.io", StringComparison.OrdinalIgnoreCase);
                        if (!isKnownPublic && !isLoopback)
                        {
                            System.Net.IPAddress[] resolved;
                            try
                            {
                                resolved = await ResolveDnsAsync(hostToResolve, ct).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // Allow execution to fail downstream if DNS resolution fails completely
                                resolved = [];
                            }
                            if (Array.Exists(resolved, IsDisallowedAddress))
                            {
                                throw new InvalidOperationException($"Registry host '{hostToResolve}' resolves to a disallowed internal address.");
                            }
                        }

                        List<string> result;
                        if (string.IsNullOrEmpty(parts.Registry) ||
                            string.Equals(parts.Registry, "docker.io", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(parts.Registry, "registry-1.docker.io", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(parts.Registry, "index.docker.io", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(parts.Registry, "hub.docker.com", StringComparison.OrdinalIgnoreCase))
                        {
                            string ns = string.IsNullOrEmpty(parts.Namespace) ? "library" : parts.Namespace;
                            result = await FetchDockerHubTagsAsync(ns, parts.Repository, ct).ConfigureAwait(false);
                        }
                        else if (GhcrRegistries.Contains(parts.Registry))
                        {
                            result = await FetchGhcrTagsAsync(parts.Namespace, parts.Repository, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            result = await FetchGenericV2TagsAsync(parts.Registry, parts.Namespace, parts.Repository, ct).ConfigureAwait(false);
                        }

                        AddToCache(key, result);
                        return result;
                    }
                    catch (OperationCanceledException)
                    {
                        return [];
                    }
                    catch (TimeoutException ex)
                    {
                        var scrubbedMsg = ScrubSecrets(ex.Message);
                        ContainerTelemetry.TrackError("RegistryClient", $"HTTP request timed out: {scrubbedMsg}", ex);
                        return [];
                    }
                    catch (JsonException ex)
                    {
                        AddToCache(key, []);
                        var scrubbedMsg = ScrubSecrets(ex.Message);
                        ContainerTelemetry.TrackError("RegistryClient", $"JSON deserialization failed: {scrubbedMsg}", ex);
                        return [];
                    }
                    catch (HttpRequestException ex)
                    {
                        if (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            AddToCache(key, []);
                        }
                        else
                        {
                            var scrubbedMsg = ScrubSecrets(ex.Message);
                            ContainerTelemetry.TrackError("RegistryClient", $"HTTP request failed (transient): {scrubbedMsg}", null);
                        }
                        return [];
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        var scrubbedMsg = ScrubSecrets(ex.Message);
                        var scrubbedEx = new RegistryConnectionException(scrubbedMsg, ex);
                        ContainerTelemetry.TrackError("RegistryClient", "Global fetch trap triggered (transient)", scrubbedEx);
                        return [];
                    }
                    finally
                    {
                        lock (ActiveFetches)
                        {
                            ActiveFetches.Remove(key);
                        }
                    }
                });

                ActiveFetches[imageReference] = task;
            }
        }

        var fetched = cancellationToken.CanBeCanceled
            ? await task.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await task.ConfigureAwait(false);
        return new List<string>(fetched);
    }

    public static void InvalidateTagsCache()
    {
        lock (CacheEvictionLock)
        {
            TagsCache.Clear();
        }
    }

    private static void AddToCache(string key, List<string> tags)
    {
        lock (CacheEvictionLock)
        {
            TagsCache[key] = (tags, Environment.TickCount64);

            if (TagsCache.Count > 100)
            {
                var currentTicks = Environment.TickCount64;
                var keysToRemove = new List<string>();
                foreach (var kvp in TagsCache)
                {
                    if ((currentTicks - kvp.Value.cacheTimeTicks) >= 60_000)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                foreach (var k in keysToRemove)
                {
                    TagsCache.TryRemove(k, out _);
                }

                if (TagsCache.Count > 100)
                {
                    var oldest = TagsCache.OrderBy(kvp => kvp.Value.cacheTimeTicks).Take(TagsCache.Count - 100).Select(kvp => kvp.Key).ToList();
                    foreach (var k in oldest)
                    {
                        TagsCache.TryRemove(k, out _);
                    }
                }
            }
        }
    }

    private static string GetCachedString(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return string.Empty;
        if (span.Equals("library", StringComparison.OrdinalIgnoreCase)) return "library";
        if (span.Equals("ghcr.io", StringComparison.OrdinalIgnoreCase)) return "ghcr.io";
        return span.ToString();
    }

    internal static (string Registry, string Namespace, string Repository) ParseImageReference(string imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return ("", "", "");
        }

        var cleanImage = imageReference.AsSpan().Trim();
        var shaIdx = cleanImage.IndexOf('@');
        if (shaIdx >= 0)
        {
            cleanImage = cleanImage[..shaIdx];
        }
        var lastSlashIdx = cleanImage.LastIndexOf('/');
        var lastColonIdx = cleanImage.LastIndexOf(':');
        if (lastColonIdx >= 0 && lastColonIdx > lastSlashIdx)
        {
            cleanImage = cleanImage[..lastColonIdx];
        }

        if (cleanImage.IsEmpty)
        {
            return ("", "", "");
        }

        var firstSlash = cleanImage.IndexOf('/');
        ReadOnlySpan<char> registrySpan = default;
        ReadOnlySpan<char> pathSpan = cleanImage;

        if (firstSlash >= 0)
        {
            var firstSegment = cleanImage[..firstSlash];
            if (firstSegment.Contains('.') || firstSegment.Contains(':') || firstSegment.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                registrySpan = firstSegment;
                pathSpan = cleanImage[(firstSlash + 1)..];
            }
        }

        if (pathSpan.IsEmpty)
        {
            return (registrySpan.ToString(), "", "");
        }

        var lastSlash = pathSpan.LastIndexOf('/');
        ReadOnlySpan<char> nsSpan = default;
        ReadOnlySpan<char> repoSpan = pathSpan;

        if (lastSlash >= 0)
        {
            nsSpan = pathSpan[..lastSlash];
            repoSpan = pathSpan[(lastSlash + 1)..];
        }

        return (GetCachedString(registrySpan), GetCachedString(nsSpan), GetCachedString(repoSpan));
    }

    private static bool IsAllLowercase(string str)
    {
        if (string.IsNullOrEmpty(str)) return true;
        return !str.AsSpan().ContainsAnyInRange('A', 'Z');
    }

    private static async Task<List<string>> FetchDockerHubTagsAsync(string ns, string repo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(repo))
        {
            return [];
        }
        var nsLower = IsAllLowercase(ns) ? ns : ns.ToLowerInvariant();
        var repoLower = IsAllLowercase(repo) ? repo : repo.ToLowerInvariant();
        var encodedNs = Uri.EscapeDataString(nsLower);
        var encodedRepo = Uri.EscapeDataString(repoLower);
        var url = $"https://hub.docker.com/v2/repositories/{encodedNs}/{encodedRepo}/tags?page_size=20&ordering=last_updated";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new RegistryConnectionException("Invalid registry response: expected JSON media type.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        HubResponse? res;
        try
        {
            res = await JsonSerializer.DeserializeAsync(stream, RegistryJsonContext.Default.HubResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return [];
        }

        var results = res?.Results;
        if (results == null) return [];
        var list = new List<string>(results.Count);
        foreach (var r in results)
        {
            if (!string.IsNullOrEmpty(r.Name))
            {
                list.Add(r.Name);
            }
        }
        return list;
    }

    private static async Task<List<string>> FetchGhcrTagsAsync(string ns, string repo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(repo))
        {
            return [];
        }
        var nsLower = IsAllLowercase(ns) ? ns : ns.ToLowerInvariant();
        var repoLower = IsAllLowercase(repo) ? repo : repo.ToLowerInvariant();
        var escapedNs = string.IsNullOrEmpty(nsLower) ? "" : Uri.EscapeDataString(nsLower);
        var escapedRepo = Uri.EscapeDataString(repoLower);
        var scopeRepoEscaped = string.IsNullOrEmpty(escapedNs) ? escapedRepo : $"{escapedNs}/{escapedRepo}";

        var tokenUrl = $"https://ghcr.io/token?scope=repository:{scopeRepoEscaped}:pull";
        using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);

        using var tokenTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tokenTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        using var tokenResponse = await SendWithRetryAsync(tokenReq, tokenTimeoutCts.Token).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();

        var tokenContentType = tokenResponse.Content.Headers.ContentType?.MediaType;
        if (tokenContentType == null || !tokenContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        using var tokenStream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GhcrTokenResponse? tokenRes;
        try
        {
            tokenRes = await JsonSerializer.DeserializeAsync(tokenStream, RegistryJsonContext.Default.GhcrTokenResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return [];
        }

        if (string.IsNullOrEmpty(tokenRes?.Token))
        {
            return [];
        }

        var rawToken = tokenRes.Token;
        if (string.IsNullOrEmpty(rawToken))
        {
            return [];
        }
        string token;
        int firstControl = rawToken.AsSpan().IndexOfAny(ControlChars);
        if (firstControl < 0)
        {
            token = rawToken;
        }
        else
        {
            var sb = new System.Text.StringBuilder(rawToken.Length);
            foreach (var c in rawToken)
            {
                if (!char.IsControl(c))
                {
                    sb.Append(c);
                }
            }
            token = sb.ToString();
        }

        using var tagsReq = new HttpRequestMessage(HttpMethod.Get, $"https://ghcr.io/v2/{scopeRepoEscaped}/tags/list");
        tagsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        tagsReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var tagsResponse = await SendWithRetryAsync(tagsReq, cancellationToken).ConfigureAwait(false);
        tagsResponse.EnsureSuccessStatusCode();

        var tagsContentType = tagsResponse.Content.Headers.ContentType?.MediaType;
        if (tagsContentType == null || !tagsContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        using var tagsStream = await tagsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GhcrTagsResponse? tagsRes;
        try
        {
            tagsRes = await JsonSerializer.DeserializeAsync(tagsStream, RegistryJsonContext.Default.GhcrTagsResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return [];
        }

        if (tagsRes?.Tags != null)
        {
            return ProcessRawTags(tagsRes.Tags);
        }

        return [];
    }

    private static string? GetDockerAuthHeader(string registry)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configPath = Path.Combine(home, ".docker", "config.json");
            if (!File.Exists(configPath))
            {
                return null;
            }
            using (var fs = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var doc = JsonDocument.Parse(fs))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("auths", out var auths))
                {
                    foreach (var prop in auths.EnumerateObject())
                    {
                        var key = prop.Name;
                        bool match = false;
                        if (string.Equals(registry, "hub.docker.com", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(registry, "docker.io", StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrEmpty(registry))
                        {
                            match = key.Contains("docker.io") || key.Contains("docker.com");
                        }
                        else
                        {
                            var cleanKey = key;
                            if (cleanKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                cleanKey = cleanKey[8..];
                            }
                            else if (cleanKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                            {
                                cleanKey = cleanKey[7..];
                            }
                            cleanKey = cleanKey.TrimEnd('/');
                            match = string.Equals(cleanKey, registry, StringComparison.OrdinalIgnoreCase);
                        }
                        if (match && prop.Value.TryGetProperty("auth", out var authProp))
                        {
                            var authVal = authProp.GetString();
                            if (!string.IsNullOrEmpty(authVal))
                            {
                                return "Basic " + authVal;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Ignore
        }
        return null;
    }

    private static async Task<List<string>> FetchGenericV2TagsAsync(string registry, string ns, string repo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(repo)) return [];
        var registryHost = string.IsNullOrEmpty(registry) ? "registry-1.docker.io" : registry;
        var scheme = "https";
        if (IsLoopbackRegistry(registryHost))
        {
            scheme = "http";
        }
        var scopeRepo = string.IsNullOrEmpty(ns) ? repo : $"{ns}/{repo}";
        var url = $"{scheme}://{registryHost}/v2/{scopeRepo}/tags/list";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var authHeader = GetDockerAuthHeader(registryHost);
        if (authHeader != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var authHeaderVal = response.Headers.WwwAuthenticate.FirstOrDefault();
            if (authHeaderVal != null && authHeaderVal.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                var parameter = authHeaderVal.Parameter;
                if (!string.IsNullOrEmpty(parameter))
                {
                    string? realm = null;
                    string? service = null;
                    string? scope = null;
                    var matches = ChallengeParameterRegex().Matches(parameter);
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        var key = match.Groups["key"].Value;
                        var val = match.Groups["value"].Value;
                        if (key.Equals("realm", StringComparison.OrdinalIgnoreCase)) realm = val;
                        else if (key.Equals("service", StringComparison.OrdinalIgnoreCase)) service = val;
                        else if (key.Equals("scope", StringComparison.OrdinalIgnoreCase)) scope = val;
                    }

                    // Validate the server-supplied token endpoint before contacting it. Require an
                    // absolute HTTPS URL (blocks an http downgrade and SSRF to internal/plaintext
                    // services), and forward the local Docker credential only when the realm is on
                    // the same host as the registry. Otherwise a malicious registry could name an
                    // arbitrary realm host in its WWW-Authenticate challenge and exfiltrate the
                    // stored credential.
                    if (!string.IsNullOrEmpty(realm) &&
                        Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) &&
                        string.Equals(realmUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        var tokenUrl = realm;
                        var separator = realm.Contains('?') ? "&" : "?";
                        if (!string.IsNullOrEmpty(service))
                        {
                            tokenUrl += $"{separator}service={Uri.EscapeDataString(service)}";
                            separator = "&";
                        }
                        if (!string.IsNullOrEmpty(scope))
                        {
                            tokenUrl += $"{separator}scope={Uri.EscapeDataString(scope)}";
                        }

                        using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
                        var realmHostIsRegistry =
                            string.Equals(realmUri.Host, registryHost, StringComparison.OrdinalIgnoreCase) ||
                            registryHost.StartsWith(realmUri.Host + ":", StringComparison.OrdinalIgnoreCase);
                        if (authHeader != null && realmHostIsRegistry)
                        {
                            tokenReq.Headers.TryAddWithoutValidation("Authorization", authHeader);
                        }

                        using var tokenResponse = await SendWithRetryAsync(tokenReq, cancellationToken).ConfigureAwait(false);
                        if (tokenResponse.IsSuccessStatusCode)
                        {
                            using var tokenStream = await tokenResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                            var tokenRes = await JsonSerializer.DeserializeAsync(tokenStream, RegistryJsonContext.Default.GhcrTokenResponse, cancellationToken).ConfigureAwait(false);
                            var tokenVal = tokenRes?.Token ?? tokenRes?.AccessToken;
                            if (!string.IsNullOrEmpty(tokenVal))
                            {
                                using var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
                                retryRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenVal);
                                using var retryResponse = await SendWithRetryAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                                if (retryResponse.IsSuccessStatusCode)
                                {
                                    using var retryStream = await retryResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                                    var retryTagsRes = await JsonSerializer.DeserializeAsync(retryStream, RegistryJsonContext.Default.GhcrTagsResponse, cancellationToken).ConfigureAwait(false);
                                    if (retryTagsRes?.Tags != null)
                                    {
                                        return ProcessRawTags(retryTagsRes.Tags);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GhcrTagsResponse? tagsRes;
        try
        {
            tagsRes = await JsonSerializer.DeserializeAsync(stream, RegistryJsonContext.Default.GhcrTagsResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return [];
        }
        if (tagsRes?.Tags != null)
        {
            return ProcessRawTags(tagsRes.Tags);
        }
        return [];
    }

    private static List<string> ProcessRawTags(List<string> rawTags)
    {
        var list = new List<string>(Math.Min(20, rawTags.Count));
        for (int i = rawTags.Count - 1; i >= 0 && list.Count < 20; i--)
        {
            var t = rawTags[i];
            if (!string.IsNullOrEmpty(t))
            {
                list.Add(t);
            }
        }
        return list;
    }

    private static string ScrubSecrets(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }
        if (input.Contains("token=", StringComparison.OrdinalIgnoreCase) || input.Contains("bearer", StringComparison.OrdinalIgnoreCase))
        {
            input = SecretScrubRegex().Replace(input, m => $"{m.Groups["type"].Value}{m.Groups["sep"].Value}***");
        }
        var home = CachedUserProfile;
        if (!string.IsNullOrWhiteSpace(home) && input.Contains(home, StringComparison.OrdinalIgnoreCase))
        {
            input = input.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        }
        var user = CachedUserName;
        if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3 && input.Contains(user, StringComparison.OrdinalIgnoreCase))
        {
            input = input.Replace(user, "***", StringComparison.OrdinalIgnoreCase);
        }
        return input;
    }
}

internal class HubResponse { [JsonPropertyName("results")] public List<HubTag>? Results { get; set; } }
internal class HubTag { [JsonPropertyName("name")] public string? Name { get; set; } }
internal class GhcrTagsResponse { [JsonPropertyName("tags")] public List<string>? Tags { get; set; } }
internal class GhcrTokenResponse
{
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    // Mirror the GHCR token payload; serializer-bound and retained for forward use (token lifetime handling).
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("issued_at")] public string? IssuedAt { get; set; }
}

[JsonSerializable(typeof(HubResponse))]
[JsonSerializable(typeof(GhcrTagsResponse))]
[JsonSerializable(typeof(GhcrTokenResponse))]
internal partial class RegistryJsonContext : JsonSerializerContext { }
