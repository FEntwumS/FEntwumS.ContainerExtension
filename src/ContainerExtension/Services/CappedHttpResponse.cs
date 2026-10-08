using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ContainerExtension.Services;

/// <summary>
/// Sends the requests of the registry and GitHub clients, whose answers are small JSON documents, and reads
/// each body up to a cap. A body that declares or reaches more than the cap fails with the caller's own
/// exception, so that a hostile or misbehaving server cannot make the extension allocate without bound.
/// </summary>
internal static class CappedHttpResponse
{
    /// <summary>
    /// Sends <paramref name="request"/> and returns once the headers are in, leaving the body to
    /// <see cref="ReadAsync"/>. By default HttpClient reads the whole body into memory, decompressed, before
    /// SendAsync returns, so the cap would be checked only after the allocation it is meant to prevent. The
    /// client's timeout then covers the headers only; the callers bound the whole exchange with their tokens.
    /// </summary>
    internal static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
        => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    internal static async Task<Stream> ReadAsync(HttpResponseMessage response, long maxBytes, Func<Exception> tooLarge, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw tooLarge();
        }
        using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                await buffer.DisposeAsync().ConfigureAwait(false);
                throw tooLarge();
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return buffer;
    }
}
