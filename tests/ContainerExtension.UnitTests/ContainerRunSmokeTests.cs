using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using ContainerExtension;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Lightweight real-container smoke test. Runs <c>echo</c> in the tiny, commonly-cached <c>busybox</c>
/// image through the full <see cref="DockerExecutionStrategy.ExecuteAsync(ToolCommand)"/> path against the
/// local Docker daemon. Unlike the HDL end-to-end suite it needs no fixtures and no image pull, so it can
/// validate the container run loop anywhere Docker is available — which makes it a fast regression anchor
/// for refactors of the execution engine.
/// </summary>
[Collection("TelemetryTests")]
public sealed class ContainerRunSmokeTests : IDisposable
{
    private readonly string _telemetryDir;
    // A working directory whose name the run report lines carry, unlike the temp root with its trailing separator.
    private readonly string _workDir;

    public ContainerRunSmokeTests()
    {
        _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_Smoke", Guid.NewGuid().ToString("N"));
        _workDir = Path.Combine(Path.GetTempPath(), "OneWareTests_SmokeWork_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, true);
            }
            if (Directory.Exists(_telemetryDir))
            {
                Directory.Delete(_telemetryDir, true);
            }
        }
        catch { /* best effort */ }
    }

    [FactIfNoCI]
    public async Task Busybox_Echo_RunsInContainerAndCapturesOutput()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "busybox:latest");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);

        using var strategy = new DockerExecutionStrategy(provider);
        var command = new ToolCommand
        {
            Executable = "echo",
            ToolName = "echo",
            WorkingDirectory = Path.GetTempPath(),
            CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument("hello-from-container") }
        };

        var (success, output) = await strategy.ExecuteAsync(command);

        Assert.True(success, $"expected container run to succeed; output was: {output}");
        Assert.Contains("hello-from-container", output, StringComparison.Ordinal);
    }

    [FactIfNoCI]
    public async Task Busybox_WithoutHandlers_WritesOutputToTheHost()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;

        var (success, output) = await strategy.ExecuteAsync(CreateShellCommand("echo to-stdout"));

        Assert.True(success, $"expected container run to succeed; output was: {output}");
        Assert.Contains("to-stdout", feedback.Output);
        Assert.DoesNotContain("to-stdout", feedback.Errors);
    }

    [FactIfNoCI]
    public async Task Busybox_WithoutHandlers_WritesErrorsToTheHost()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;

        var (_, output) = await strategy.ExecuteAsync(CreateShellCommand("echo to-stderr >&2"));

        Assert.Contains("to-stderr", feedback.Errors);
        Assert.DoesNotContain("to-stderr", feedback.Output);
        Assert.Contains("to-stderr", output, StringComparison.Ordinal);
    }

    [FactIfNoCI]
    public async Task Busybox_WithoutErrorHandler_FailsARunThatWritesToStderr()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        strategy.HostFeedback = new RecordingRunFeedback();

        var (success, output) = await strategy.ExecuteAsync(CreateShellCommand("echo to-stderr >&2"));

        Assert.False(success, $"expected the line on stderr to fail the run despite exit code 0; output was: {output}");
    }

    [FactIfNoCI]
    public async Task Busybox_ErrorHandlerAcceptingStderr_KeepsTheRunSuccessful()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        strategy.HostFeedback = new RecordingRunFeedback();

        var (success, output) = await strategy.ExecuteAsync(CreateShellCommand("echo to-stderr >&2", errorHandler: _ => true));

        Assert.True(success, $"expected container run to succeed; output was: {output}");
    }

    [FactIfNoCI]
    public async Task Busybox_HandlerRejectingALine_FailsTheRun()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        // Like OneWare's Yosys service, which rejects the lines it recognizes as errors.
        var command = CreateShellCommand("echo ERROR: synthesis failed",
            outputHandler: line => !line.StartsWith("ERROR", StringComparison.Ordinal),
            errorHandler: _ => true);

        var (success, output) = await strategy.ExecuteAsync(command);

        Assert.False(success, $"expected the rejected line to fail the run; output was: {output}");
    }

    [FactIfNoCI]
    public async Task Busybox_HandlerAcceptingEveryLine_KeepsTheRunSuccessful()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var command = CreateShellCommand("echo Info: synthesis done",
            outputHandler: line => !line.StartsWith("ERROR", StringComparison.Ordinal),
            errorHandler: _ => true);

        var (success, output) = await strategy.ExecuteAsync(command);

        Assert.True(success, $"expected container run to succeed; output was: {output}");
    }

    [FactIfNoCI]
    public async Task Busybox_EveryRun_EchoesTheCommandLine()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;

        // With handlers set as well: the native strategy echoes every call.
        var (success, output) = await strategy.ExecuteAsync(CreateShellCommand("echo to-stdout", _ => true, _ => true, _workDir));

        Assert.True(success, $"expected container run to succeed; output was: {output}");
        Assert.Contains(($"[{Path.GetFileName(_workDir)}]: sh -c \"echo to-stdout\"", (Color?)Colors.CornflowerBlue), feedback.Notices);
    }

    [FactIfNoCI]
    public async Task Busybox_NonZeroExit_ReportsTheExitCode()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;

        var (success, _) = await strategy.ExecuteAsync(CreateShellCommand("exit 3", workingDirectory: _workDir));

        Assert.False(success);
        Assert.Contains($"[{Path.GetFileName(_workDir)}]: sh exited with code 3", feedback.Errors);
    }

    [FactIfNoCI]
    public async Task Busybox_CancelledRun_ReportsTheCancellation()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var (success, _) = await strategy.ExecuteAsync(CreateShellCommand("sleep 30", workingDirectory: _workDir), cancellation.Token);

        Assert.False(success);
        Assert.Contains(($"[{Path.GetFileName(_workDir)}]: sh cancelled!", (Color?)Colors.DarkOrange), feedback.Notices);
        Assert.DoesNotContain(feedback.Errors, e => e.Contains("exited with code", StringComparison.Ordinal));
    }

    private static E2ETestServiceProvider CreateBusyboxProvider()
    {
        var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "busybox:latest");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);
        return provider;
    }

    // Without handlers, the command is called the way OneWare's Icarus Verilog and Verilator simulators call.
    private static ToolCommand CreateShellCommand(string script,
        Func<string, bool>? outputHandler = null, Func<string, bool>? errorHandler = null, string? workingDirectory = null) => new()
        {
            Executable = "sh",
            ToolName = "sh",
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument("-c"), new E2ETestCommandArgument(script) },
            OutputHandler = outputHandler,
            ErrorHandler = errorHandler
        };
}
