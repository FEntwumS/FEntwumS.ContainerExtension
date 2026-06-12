using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ContainerExtension.Services;

internal sealed class GitHubReleaseResponse
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }
}

[JsonSerializable(typeof(GitHubReleaseResponse))]
internal partial class GitHubJsonContext : JsonSerializerContext { }

internal static class GitHubReleaseClient
{
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
        return true;
    }

    public static async Task<string?> GetLatestReleaseTagAsync(CancellationToken ct)
    {
        var client = HttpClientLazy.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/YosysHQ/oss-cad-suite-build/releases/latest");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == (System.Net.HttpStatusCode)429)
            {
                throw new InvalidOperationException("GitHub API rate limit exceeded or access forbidden. Please check your internet connection or try again later.");
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
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Network connection timed out while fetching the latest release from GitHub (10s limit exceeded).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Network connection failed while fetching the latest release from GitHub: " + ex.Message, ex);
        }
    }
}
