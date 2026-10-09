using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ContainerExtension;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for the hints a failed run adds below its error line, without a daemon: each failure is
/// stood in for by an exception that carries the daemon's or the extension's message, and one run goes
/// to a named pipe that nobody serves. That run records its failure, so the class keeps telemetry in a
/// folder of its own and shares the telemetry collection.
/// </summary>
[Collection("TelemetryTests")]
public sealed class FailureHintsTests : IDisposable
{
    private const string RegistryImage = "hdlc/ghdl:yosys";
    private readonly string _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_FailureHints", Guid.NewGuid().ToString("N"));

    public FailureHintsTests()
    {
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_telemetryDir))
            {
                Directory.Delete(_telemetryDir, true);
            }
        }
        catch { /* best effort */ }
    }

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

    [Fact]
    public void FailureHints_ConnectTimeoutOnANamedPipe_SuggestsStartingTheRuntime()
    {
        var hints = DockerExecutionStrategy.FailureHints(new TimeoutException("The operation has timed out."), RegistryImage, namedPipe: true);

        Assert.Contains("No process serves the Docker named pipe. Start Docker Desktop", hints, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureHints_ConnectTimeoutWithoutANamedPipe_AddsNoHint()
    {
        Assert.Empty(DockerExecutionStrategy.FailureHints(new TimeoutException("The operation has timed out."), RegistryImage));
    }

    [Fact]
    public void FailureHints_RegexTimeoutOnANamedPipe_AddsNoHint()
    {
        Assert.Empty(DockerExecutionStrategy.FailureHints(new RegexMatchTimeoutException(), RegistryImage, namedPipe: true));
    }

    [Fact]
    public async Task ExecuteAsync_PipeNobodyServesWithTheCheckBypassed_HintsToStartTheRuntime()
    {
        // As on Windows with the default settings and Docker Desktop not running: the check is bypassed, so only the
        // connect finds that nobody serves the pipe. No daemon is needed, the connect gives up on every platform.
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"npipe://./pipe/missing-{Guid.NewGuid():N}");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;
        var command = new ToolCommand
        {
            Executable = "echo",
            ToolName = "echo",
            WorkingDirectory = Path.GetTempPath(),
            CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument("unreached") }
        };

        var (success, _) = await strategy.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.False(success);
        Assert.Contains(feedback.Errors, line => line.Contains("TimeoutException", StringComparison.Ordinal)
            && line.Contains("Start Docker Desktop", StringComparison.Ordinal));
    }
}
