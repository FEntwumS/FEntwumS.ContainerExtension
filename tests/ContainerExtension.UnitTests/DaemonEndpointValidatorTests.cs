using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for <see cref="DaemonEndpointValidator"/>. Focuses on the absolute-path resolution of
/// system utilities that defeats PATH hijacking. The checks of who serves a daemon's named pipe and who
/// owns its socket need a live endpoint and have no automated test; the hardening-challenge suite only
/// checks the impersonation level of the pipe client that SecureStreamOpenerAsync opens. The socket
/// probe's handling of a cancellation runs against a socket the test listens on itself.
/// </summary>
public sealed class DaemonEndpointValidatorTests
{
    [Theory]
    [InlineData("stat")]
    [InlineData("id")]
    [InlineData("open")]
    [InlineData("xdg-open")]
    public void ResolveTrustedUnixBinary_ReturnsRootedAbsolutePath(string name)
    {
        var resolved = DaemonEndpointValidator.ResolveTrustedUnixBinary(name);
        Assert.True(Path.IsPathRooted(resolved), $"'{resolved}' must be absolute, never resolved via PATH");
        Assert.EndsWith("/" + name, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTrustedUnixBinary_PrefersAnExistingTrustedLocation()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return; // POSIX-only: /usr/bin or /bin
        }
        var resolved = DaemonEndpointValidator.ResolveTrustedUnixBinary("stat");
        // stat is a coreutils/BSD staple present on every supported POSIX host.
        Assert.True(File.Exists(resolved), $"expected a real binary at '{resolved}'");
    }

    [Fact]
    public async Task IsUnixSocketLiveAndWritableAsync_CancelledByTheCaller_ThrowsInsteadOfReportingAFailure()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return; // POSIX-only: the probe runs for Unix domain sockets
        }
        var path = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => DaemonEndpointValidator.IsUnixSocketLiveAndWritableAsync(path, cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
