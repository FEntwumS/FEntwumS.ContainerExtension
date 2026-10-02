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
    public void FailureHints_RefusedPull_PointsToTheRegistryButNotToPermissions()
    {
        var refused = new InvalidOperationException(
            "Docker API responded with status code=NotFound, response={\"message\":\"pull access denied for hdlc/absent, repository does not exist or may require 'docker login'\"}");

        var hints = DockerExecutionStrategy.FailureHints(refused, "hdlc/absent:latest");

        Assert.Contains("does not exist on Docker Hub or requires authentication", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", hints, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Build Local Image", hints, StringComparison.Ordinal);
    }

    // The project's toolchain image is on no registry, so a pull is the wrong advice for it.
    [Theory]
    [InlineData("Docker API responded with status code=NotFound, response={\"message\":\"pull access denied for fentwums/oss-cad-suite, repository does not exist or may require 'docker login'\"}")]
    [InlineData("Image 'fentwums/oss-cad-suite:latest' not found locally and pull policy is 'never'.")]
    [InlineData("No such image: fentwums/oss-cad-suite:latest")]
    public void FailureHints_MissingToolchainImage_PointsToBuildLocalImage(string message)
    {
        var hints = DockerExecutionStrategy.FailureHints(new InvalidOperationException(message), ContainerExtensionModule.OssCadSuiteImage);

        Assert.Contains("Build it with Build Local Image in the Container Dashboard", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("Docker Hub", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("docker pull", hints, StringComparison.Ordinal);
        Assert.DoesNotContain("permission", hints, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailureHints_AccessDeniedOtherThanAPull_SuggestsCheckingPermissions()
    {
        var hints = DockerExecutionStrategy.FailureHints(new InvalidOperationException("connect: access denied"), RegistryImage);

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
