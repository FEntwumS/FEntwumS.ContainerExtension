using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ContainerExtension.Services;

/// <summary>
/// Reads the body of an HTTP answer up to a cap, for the registry and GitHub clients, whose answers are small
/// JSON documents. A body that declares or reaches more than the cap fails with the caller's own exception, so
/// that a hostile or misbehaving server cannot make the extension allocate without bound.
/// </summary>
internal static class CappedHttpResponse
{
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
