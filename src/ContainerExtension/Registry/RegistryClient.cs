using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FEntwumS.ContainerExtension.Registry;

public static class RegistryClient
{
    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), // Resolves DNS staleness
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),      // Prevents GHCR from silently dropping persistent TCP connections
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ContainerExtension/1.0");
        return client;
    }

    public static async Task<List<string>> FetchTagsAsync(string imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference)) return [];

        try
        {
            var parts = ParseImageReference(imageReference);

            if (string.Equals(parts.Registry, "ghcr.io", StringComparison.OrdinalIgnoreCase))
            {
                return await FetchGhcrTagsAsync(parts.Namespace, parts.Repository).ConfigureAwait(false);
            }
            string ns = string.IsNullOrEmpty(parts.Namespace) ? "library" : parts.Namespace;
            return await FetchDockerHubTagsAsync(ns, parts.Repository).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            global::ContainerExtension.ContainerTelemetry.TrackError("RegistryClient", "Global fetch trap triggered", ex);
            return [];
        }
    }

    private static (string Registry, string Namespace, string Repository) ParseImageReference(string imageReference)
    {
        string registry = "";
        string ns = "";
        string repo = imageReference;

        var parts = imageReference.Split('/');

        if (parts.Length == 3)
        {
            registry = parts[0];
            ns = parts[1];
            repo = parts[2].Split(':')[0];
        }
        else if (parts.Length == 2)
        {
            if (parts[0].Contains('.', StringComparison.Ordinal) || parts[0].Contains(':', StringComparison.Ordinal))
            {
                registry = parts[0];
                repo = parts[1].Split(':')[0];
            }
            else
            {
                ns = parts[0];
                repo = parts[1].Split(':')[0];
            }
        }
        else if (parts.Length == 1)
        {
            repo = parts[0].Split(':')[0];
        }

        return (registry, ns, repo);
    }

    private static async Task<List<string>> FetchDockerHubTagsAsync(string ns, string repo)
    {
        var url = $"https://hub.docker.com/v2/repositories/{ns}/{repo}/tags?page_size=20&ordering=last_updated";
        using var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var res = await JsonSerializer.DeserializeAsync(stream, RegistryJsonContext.Default.HubResponse).ConfigureAwait(false);
        
        return res?.Results?.Where(r => !string.IsNullOrEmpty(r.Name)).Select(r => r.Name!).ToList() ?? [];
    }

    private static async Task<List<string>> FetchGhcrTagsAsync(string ns, string repo)
    {
        var scopeRepo = string.IsNullOrEmpty(ns) ? repo : $"{ns}/{repo}";
        var tokenUrl = $"https://ghcr.io/token?scope=repository:{scopeRepo}:pull";
        using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);

        using var tokenResponse = await _httpClient.SendAsync(tokenReq).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();

        using var tokenStream = await tokenResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var tokenRes = await JsonSerializer.DeserializeAsync(tokenStream, RegistryJsonContext.Default.GhcrTokenResponse).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tokenRes?.Token)) return [];

        var token = tokenRes.Token;

        using var tagsReq = new HttpRequestMessage(HttpMethod.Get, $"https://ghcr.io/v2/{scopeRepo}/tags/list");
        tagsReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var tagsResponse = await _httpClient.SendAsync(tagsReq).ConfigureAwait(false);
        tagsResponse.EnsureSuccessStatusCode();

        using var tagsStream = await tagsResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var tagsRes = await JsonSerializer.DeserializeAsync(tagsStream, RegistryJsonContext.Default.GhcrTagsResponse).ConfigureAwait(false);
        
        if (tagsRes?.Tags != null)
        {
            var tags = tagsRes.Tags.Where(t => !string.IsNullOrEmpty(t)).ToList();
            tags.Reverse();
            return [.. tags.Take(20)];
        }

        return [];
    }
}

internal class HubResponse { [JsonPropertyName("results")] public List<HubTag>? Results { get; set; } }
internal class HubTag { [JsonPropertyName("name")] public string? Name { get; set; } }
internal class GhcrTagsResponse { [JsonPropertyName("tags")] public List<string>? Tags { get; set; } }
internal class GhcrTokenResponse { [JsonPropertyName("token")] public string? Token { get; set; } }

[JsonSerializable(typeof(HubResponse))]
[JsonSerializable(typeof(GhcrTagsResponse))]
[JsonSerializable(typeof(GhcrTokenResponse))]
internal partial class RegistryJsonContext : JsonSerializerContext { }
