using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ContainerExtension.Services;

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // GitHub returns the published asset digest as "sha256:<hex>".
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

internal sealed class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

[JsonSerializable(typeof(GitHubReleaseResponse))]
[JsonSerializable(typeof(GitHubReleaseResponse[]))]
internal partial class GitHubJsonContext : JsonSerializerContext { }

internal static class GitHubReleaseClient
{
    private const string ReleasesBase = "https://api.github.com/repos/YosysHQ/oss-cad-suite-build/releases";

    private static readonly Lazy<HttpClient> HttpClientLazy = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.Brotli | System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ContainerExtension/1.0");
        return client;
    });

    private static bool IsValidReleaseTag(string tag)
    {
        if (tag.Length != 10) return false;
        if (tag[4] != '-' || tag[7] != '-') return false;
        for (int i = 0; i < tag.Length; i++)
        {
            if (i == 4 || i == 7) continue;
            if (!char.IsAsciiDigit(tag[i])) return false;
        }
        // Reject structurally-valid but impossible dates (e.g. 2024-13-45).
        return DateOnly.TryParseExact(tag, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static InvalidOperationException Translate(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException when !ct.IsCancellationRequested =>
            new InvalidOperationException("Network connection timed out while contacting GitHub (10s limit exceeded).", ex),
        HttpRequestException =>
            new InvalidOperationException("Network connection failed while contacting GitHub: " + ex.Message, ex),
        _ => new InvalidOperationException("Failed to query GitHub: " + ex.Message, ex),
    };

    private static void ThrowIfRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == (System.Net.HttpStatusCode)429)
        {
            throw new InvalidOperationException("GitHub API rate limit exceeded or access forbidden. Please check your internet connection or try again later.");
        }
    }

    public static async Task<string?> GetLatestReleaseTagAsync(CancellationToken ct)
    {
        var client = HttpClientLazy.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesBase + "/latest");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            ThrowIfRateLimited(response);
            // Surface a missing repository/release distinctly; otherwise the HttpRequestException
            // from EnsureSuccessStatusCode is rewrapped by Translate as a generic connectivity failure.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException("GitHub release endpoint returned 404 — the oss-cad-suite-build repository or its latest release could not be found.");
            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var res = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubReleaseResponse, ct).ConfigureAwait(false);
            var tag = res?.TagName?.Trim();
            if (string.IsNullOrEmpty(tag))
            {
                throw new InvalidOperationException("GitHub API returned an empty release tag.");
            }
            if (!IsValidReleaseTag(tag))
            {
                throw new InvalidOperationException($"Invalid release tag format received from GitHub: '{tag}'. Expected YYYY-MM-DD.");
            }
            return tag;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            throw Translate(ex, ct);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> recent, structurally-valid release tags (newest first),
    /// for populating a version picker. Filters out malformed or impossible-date tags.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetRecentReleaseTagsAsync(int limit, CancellationToken ct)
    {
        var client = HttpClientLazy.Value;
        var perPage = Math.Clamp(limit, 1, 100);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReleasesBase}?per_page={perPage}");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            ThrowIfRateLimited(response);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubReleaseResponseArray, ct).ConfigureAwait(false);
            if (releases == null) return Array.Empty<string>();

            var tags = new List<string>(releases.Length);
            foreach (var r in releases)
            {
                var tag = r.TagName?.Trim();
                if (!string.IsNullOrEmpty(tag) && IsValidReleaseTag(tag))
                {
                    tags.Add(tag);
                }
            }
            return tags;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            throw Translate(ex, ct);
        }
    }

    /// <summary>
    /// Returns the GitHub-published SHA-256 (lowercase hex, no "sha256:" prefix) of the
    /// <c>oss-cad-suite-{arch}-{date}.tgz</c> asset for the given release tag, or null if the asset
    /// or its digest is absent. Used to pin a from-source build to a verified tarball.
    /// </summary>
    public static async Task<string?> GetAssetSha256Async(string tag, string arch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag) || !IsValidReleaseTag(tag))
        {
            throw new ArgumentException("Release tag must be a valid YYYY-MM-DD value.", nameof(tag));
        }

        var client = HttpClientLazy.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReleasesBase}/tags/{tag}");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            ThrowIfRateLimited(response);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubReleaseResponse, ct).ConfigureAwait(false);
            if (release?.Assets == null) return null;

            var assetName = $"oss-cad-suite-{arch}-{tag.Replace("-", "", StringComparison.Ordinal)}.tgz";
            foreach (var asset in release.Assets)
            {
                if (!string.Equals(asset.Name, assetName, StringComparison.Ordinal)) continue;
                return NormalizeSha256Digest(asset.Digest);
            }
            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            throw Translate(ex, ct);
        }
    }

    /// <summary>
    /// Normalize a registry asset digest to a bare 64-character lowercase hex string, or null when it is
    /// missing or not a well-formed SHA-256. The charset (not just the length) is enforced because the
    /// value is interpolated as a --build-arg into the build command; a length-only check would admit
    /// shell metacharacters if the digest field were ever attacker-influenced (e.g. a TLS-MitM).
    /// </summary>
    internal static string? NormalizeSha256Digest(string? digest)
    {
        if (string.IsNullOrEmpty(digest))
        {
            return null;
        }
        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }
        if (value.Length != 64)
        {
            return null;
        }
        var lower = value.ToLowerInvariant();
        foreach (var c in lower)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return null;
            }
        }
        return lower;
    }
}
