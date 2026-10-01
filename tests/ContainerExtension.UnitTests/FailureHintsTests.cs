using System;
using System.Net.Sockets;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for the hints a failed run adds below its error line, without a daemon: each failure is
/// stood in for by an exception that carries the daemon's or the extension's message.
/// </summary>
public sealed class FailureHintsTests
{
    private const string RegistryImage = "hdlc/ghdl:yosys";

    [Fact]
    public void FailureHints_MissingImage_SuggestsAPull()
    {
        var hints = DockerExecutionStrategy.FailureHints(new InvalidOperationException($"No such image: {RegistryImage}"), RegistryImage);

        Assert.Contains($"Run 'docker pull {RegistryImage}'", hints, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureHints_DeniedSocket_SuggestsCheckingPermissions()
    {
        var hints = DockerExecutionStrategy.FailureHints(new SocketException((int)SocketError.AccessDenied), RegistryImage);

        Assert.Contains("A permission error was encountered", hints, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureHints_PortInUse_SuggestsAPortConflict()
    {
        var hints = DockerExecutionStrategy.FailureHints(new InvalidOperationException("Bind for 0.0.0.0:8080 failed: port is already allocated"), RegistryImage);

        Assert.Contains("A host port conflict was detected", hints, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureHints_UnknownFailure_AddsNoHint()
    {
        Assert.Empty(DockerExecutionStrategy.FailureHints(new InvalidOperationException("Something else went wrong"), RegistryImage));
    }
}
