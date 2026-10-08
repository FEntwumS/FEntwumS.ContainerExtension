using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ContainerExtension.Services;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for <see cref="CappedHttpResponse"/>, which reads the answers of registries and GitHub up to a cap.
/// </summary>
public sealed class CappedHttpResponseTests
{
    private const long Cap = 64 * 1024;

    [Fact]
    public async Task ReadAsync_BodyWithinTheCap_ReturnsIt()
    {
        using var response = new HttpResponseMessage { Content = new StringContent("{\"tags\":[]}") };

        using var body = await CappedHttpResponse.ReadAsync(response, Cap, TooLarge, TestContext.Current.CancellationToken);

        using var reader = new StreamReader(body);
        Assert.Equal("{\"tags\":[]}", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_BodyThatDeclaresMoreThanTheCap_Fails()
    {
        using var response = new HttpResponseMessage { Content = new ByteArrayContent(new byte[Cap + 1]) };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CappedHttpResponse.ReadAsync(response, Cap, TooLarge, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_BodyThatGrowsPastTheCap_Fails()
    {
        // No declared length, as with a compressed or chunked body: only the read finds the size.
        using var response = new HttpResponseMessage { Content = new StreamContent(new CountingZeroStream(Cap + 1)) };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CappedHttpResponse.ReadAsync(response, Cap, TooLarge, TestContext.Current.CancellationToken));
    }

    private static InvalidDataException TooLarge() => new("The answer exceeds the cap.");
}

/// <summary>
/// A body of a given number of zero bytes that cannot seek, so it declares no length, and that counts the
/// bytes read from it.
/// </summary>
internal sealed class CountingZeroStream(long length) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, length - BytesRead);
        buffer[..count].Clear();
        BytesRead += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
