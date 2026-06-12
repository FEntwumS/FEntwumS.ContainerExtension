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
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ContainerExtension/1.0");
        return client;
    });

    public static async Task<string?> GetLatestReleaseTagAsync(CancellationToken ct)
    {
        var client = HttpClientLazy.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/YosysHQ/oss-cad-suite-build/releases/latest");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var res = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubReleaseResponse, ct).ConfigureAwait(false);
        return res?.TagName?.Trim();
    }
}
