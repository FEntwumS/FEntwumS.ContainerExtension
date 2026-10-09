using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Docker.DotNet;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for <see cref="DaemonEndpointValidator"/>. Focuses on the absolute-path resolution of
/// system utilities that defeats PATH hijacking. The checks of who serves a daemon's named pipe and who
/// owns its socket need a live endpoint and have no automated test; the hardening-challenge suite only
/// checks the impersonation level of the pipe client that SecureStreamOpenerAsync opens. The socket
/// probe's handling of a cancellation runs against a socket the test listens on itself, and both a request
/// through the secure pipe credentials and the probe of the pipe check against a pipe that nobody serves;
/// the failure for each result of that check needs no pipe.
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
    public void UnixSocketCandidates_OnLinux_IncludeTheSocketsOfDockerDesktopAndRootlessDocker()
    {
        var paths = DaemonEndpointValidator.UnixSocketCandidates("/home/dev", "1000", linux: true).Select(c => c.path).ToList();

        Assert.Contains(Path.Combine("/home/dev", ".docker/desktop/docker.sock"), paths);
        Assert.Contains("/run/user/1000/docker.sock", paths);
        // The system-wide socket stays the first one tried.
        Assert.Equal("/var/run/docker.sock", paths[0]);
    }

    [Fact]
    public void UnixSocketCandidates_ElsewhereThanLinux_LeaveTheLinuxSocketsOut()
    {
        var paths = DaemonEndpointValidator.UnixSocketCandidates("/Users/dev", "501", linux: false).Select(c => c.path).ToList();

        Assert.DoesNotContain(Path.Combine("/Users/dev", ".docker/desktop/docker.sock"), paths);
        Assert.DoesNotContain("/run/user/501/docker.sock", paths);
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

    [Fact]
    public async Task SecureNamedPipeCredentials_PipeNobodyServes_GivesUpAfterTheConnectTimeout()
    {
        // The request goes through the opener that the credentials install in Docker.DotNet's handler, to a
        // pipe that does not exist, as when Docker Desktop is not running. The cancellation stands in for the
        // request timeout and only ends the wait of an opener without a limit.
        var endpoint = new Uri($"npipe://./pipe/missing-{Guid.NewGuid():N}");
        using var configuration = new DockerClientConfiguration(endpoint, new DaemonEndpointValidator.SecureNamedPipeCredentials(endpoint));
        using var client = configuration.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() => client.System.PingAsync(cancellation.Token));
    }

    [Fact]
    public async Task SecureNamedPipeCredentials_PipeNobodyServes_ReadsAsAnOfflineDaemon()
    {
        // The API version negotiation records an error unless a failure reads as an offline daemon. A pipe that
        // nobody serves is the offline daemon of a Windows host, so its failure must read so.
        var endpoint = new Uri($"npipe://./pipe/missing-{Guid.NewGuid():N}");
        using var configuration = new DockerClientConfiguration(endpoint, new DaemonEndpointValidator.SecureNamedPipeCredentials(endpoint));
        using var client = configuration.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));

        var failure = await Record.ExceptionAsync(() => client.System.GetVersionAsync(cancellation.Token));

        Assert.NotNull(failure);
        Assert.True(DockerConnectionFactory.IsDaemonOffline(failure), $"{failure.GetType()} should read as an offline daemon");
    }

    [Fact]
    public async Task ProbeNamedPipeServer_PipeNobodyServes_ReadsAsNotServed()
    {
        // As when Docker Desktop is not running: the connect gives up, which says nothing about who would serve the pipe.
        var check = await DaemonEndpointValidator.ProbeNamedPipeServerAsync($"missing-{Guid.NewGuid():N}", 200, TestContext.Current.CancellationToken);

        Assert.Equal(DaemonEndpointValidator.NamedPipeCheck.NotServed, check);
    }

    [Fact]
    public async Task ProbeNamedPipeServer_CancelledByTheCaller_ThrowsInsteadOfReadingAsUntrusted()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DaemonEndpointValidator.ProbeNamedPipeServerAsync($"missing-{Guid.NewGuid():N}", 200, cancellation.Token));
    }

    [Fact]
    public void NamedPipeCheckFailure_NotServed_PointsToTheRuntimeAndNotToTheBypass()
    {
        var failure = DaemonEndpointValidator.NamedPipeCheckFailure(DaemonEndpointValidator.NamedPipeCheck.NotServed, "docker_engine");

        Assert.NotNull(failure);
        Assert.Contains("'docker_engine'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Start Docker Desktop", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedPipeCheckFailure_Untrusted_PointsToTheBypass()
    {
        var failure = DaemonEndpointValidator.NamedPipeCheckFailure(DaemonEndpointValidator.NamedPipeCheck.Untrusted, "docker_engine");

        Assert.NotNull(failure);
        Assert.Contains("Insecure named pipe connection detected for 'docker_engine'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'Bypass Named Pipe Security Check'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedPipeCheckFailure_Trusted_StopsNothing()
    {
        Assert.Null(DaemonEndpointValidator.NamedPipeCheckFailure(DaemonEndpointValidator.NamedPipeCheck.Trusted, "docker_engine"));
    }
}
