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

namespace FEntwumS.ContainerExtension.Registry;

/// <summary>
/// Represents an error that occurs during a registry connection or operation.
/// </summary>
public sealed class RegistryConnectionException : Exception
{
    public RegistryConnectionException(string message) : base(message) { }
    public RegistryConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Static client for interacting with Docker/OCI container registries (e.g., ghcr.io, Docker Hub).
/// Handles authentication logic, image manifest retrieval, and blob downloads.
/// </summary>
public static partial class RegistryClient
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<type>token|bearer)(?<sep>\s*=\s*|\s+)[a-zA-Z0-9_\-\.\+\/=]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex SecretScrubRegex();

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
            AutomaticDecompression = System.Net.DecompressionMethods.Brotli | System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ContainerExtension/1.0");
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return client;
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
                DnsCache.Clear();
            }
            DnsCache[host] = (ips, Environment.TickCount64);
        }
        return ips;
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
                return cached.tags;
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
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var ct = cts.Token;
                    try
                    {
                        var parts = ParseImageReference(key);

                        if (parts.Registry.Contains("http:", StringComparison.OrdinalIgnoreCase) ||
                            parts.Registry.Contains("http", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new RegistryConnectionException("Non-SSL / HTTP registries are strictly prohibited.");
                        }

                        if (!IsValidRegistryIdentifier(parts.Namespace) || !IsValidRegistryIdentifier(parts.Repository) || !IsValidRegistryIdentifier(parts.Registry))
                        {
                            AddToCache(key, []);
                            return [];
                        }

                        try
                        {
                            var hostToResolve = string.IsNullOrEmpty(parts.Registry) ? "hub.docker.com" : parts.Registry;
                            _ = await ResolveDnsAsync(hostToResolve, ct).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // Allow execution to fail downstream if DNS resolution fails completely
                        }

                        List<string> result;
                        if (GhcrRegistries.Contains(parts.Registry))
                        {
                            result = await FetchGhcrTagsAsync(parts.Namespace, parts.Repository, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            string ns = string.IsNullOrEmpty(parts.Namespace) ? "library" : parts.Namespace;
                            result = await FetchDockerHubTagsAsync(ns, parts.Repository, ct).ConfigureAwait(false);
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
                        AddToCache(key, []);
                        var scrubbedMsg = ScrubSecrets(ex.Message);
                        global::ContainerExtension.ContainerTelemetry.TrackError("RegistryClient", $"HTTP request timed out: {scrubbedMsg}", ex);
                        return [];
                    }
                    catch (JsonException ex)
                    {
                        AddToCache(key, []);
                        var scrubbedMsg = ScrubSecrets(ex.Message);
                        global::ContainerExtension.ContainerTelemetry.TrackError("RegistryClient", $"JSON deserialization failed: {scrubbedMsg}", ex);
                        return [];
                    }
                    catch (HttpRequestException ex)
                    {
                        AddToCache(key, []);
                        if (ex.StatusCode != System.Net.HttpStatusCode.NotFound && ex.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                        {
                            var scrubbedMsg = ScrubSecrets(ex.Message);
                            global::ContainerExtension.ContainerTelemetry.TrackError("RegistryClient", $"HTTP request failed: {scrubbedMsg}", null);
                        }
                        return [];
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        AddToCache(key, []);
                        var scrubbedMsg = ScrubSecrets(ex.Message);
                        var scrubbedEx = new RegistryConnectionException(scrubbedMsg, ex);
                        global::ContainerExtension.ContainerTelemetry.TrackError("RegistryClient", "Global fetch trap triggered", scrubbedEx);
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

        return cancellationToken.CanBeCanceled ? await task.WaitAsync(cancellationToken).ConfigureAwait(false) : await task.ConfigureAwait(false);
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
        tagsReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.docker.distribution.manifest.v2+json"));

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
            var rawTags = tagsRes.Tags;
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

        return [];
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
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("issued_at")] public string? IssuedAt { get; set; }
}

[JsonSerializable(typeof(HubResponse))]
[JsonSerializable(typeof(GhcrTagsResponse))]
[JsonSerializable(typeof(GhcrTokenResponse))]
internal partial class RegistryJsonContext : JsonSerializerContext { }
