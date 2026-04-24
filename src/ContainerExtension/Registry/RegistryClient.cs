using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ContainerExtension;

namespace FEntwumS.ContainerExtension.Registry;

public static class RegistryClient
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// Fetches available tags for a given remote image reference.
    /// Handles Docker Hub and GHCR generically.
    /// </summary>
    public static async Task<List<string>> FetchTagsAsync(string imageReference)
    {
        try
        {
            var parts = ParseImageReference(imageReference);

            if (parts.Registry == "ghcr.io")
            {
                return await FetchGhcrTagsAsync(parts.Namespace, parts.Repository).ConfigureAwait(false);
            }
            string ns = string.IsNullOrEmpty(parts.Namespace) ? "library" : parts.Namespace;
            return await FetchDockerHubTagsAsync(ns, parts.Repository).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail gracefully on private/unknown registries or network errors
            global::ContainerExtension.ContainerTelemetry.TrackError("RegistryClient", "Global fetch trap triggered", ex);
            return new List<string>();
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
            repo = parts[2].Split(':')[0]; // remove tag if present
        }
        else if (parts.Length == 2)
        {
            if (parts[0].Contains('.') || parts[0].Contains(':'))
            {
                // e.g. ghcr.io/repo or localhost:5000/repo
                registry = parts[0];
                repo = parts[1].Split(':')[0];
            }
            else
            {
                // e.g. hdlc/ghdl
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

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");

        var tags = new List<string>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var nameProp))
            {
                var val = nameProp.GetString();
                if (!string.IsNullOrEmpty(val))
                {
                    tags.Add(val);
                }
            }
        }

        return tags.Where(t => !string.IsNullOrEmpty(t)).ToList();
    }

    private static async Task<List<string>> FetchGhcrTagsAsync(string ns, string repo)
    {
        // Try requesting an anonymous token for GHCR
        // Note: this may fail with DENIED if the package is not completely public or repo doesn't exist
        var scopeRepo = string.IsNullOrEmpty(ns) ? repo : $"{ns}/{repo}";
        var tokenUrl = $"https://ghcr.io/token?scope=repository:{scopeRepo}:pull";
        using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);

        using var tokenResponse = await _httpClient.SendAsync(tokenReq).ConfigureAwait(false);
        tokenResponse.EnsureSuccessStatusCode();

        var tokenJson = await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        if (!tokenDoc.RootElement.TryGetProperty("token", out var tokenProp))
            return new List<string>();

        var token = tokenProp.GetString() ?? string.Empty;

        using var tagsReq = new HttpRequestMessage(HttpMethod.Get, $"https://ghcr.io/v2/{scopeRepo}/tags/list");
        tagsReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var tagsResponse = await _httpClient.SendAsync(tagsReq).ConfigureAwait(false);
        tagsResponse.EnsureSuccessStatusCode();

        var tagsJson = await tagsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var tagsDoc = JsonDocument.Parse(tagsJson);
        if (tagsDoc.RootElement.TryGetProperty("tags", out var tagsArray))
        {
            var tags = new List<string>();
            foreach (var item in tagsArray.EnumerateArray())
            {
                var val = item.GetString();
                if (!string.IsNullOrEmpty(val))
                {
                    tags.Add(val);
                }
            }
            // OCI tags/list returns tags in lexicographic order, not chronological.
            // Reversing gives z->a ordering, which is a rough approximation of
            // "latest first" for simple version tags but is NOT temporal.
            // Docker Hub provides an ordering=last_updated parameter; GHCR does not.
            tags.Reverse();
            return tags.Take(20).ToList();
        }

        return new List<string>();
    }
}
