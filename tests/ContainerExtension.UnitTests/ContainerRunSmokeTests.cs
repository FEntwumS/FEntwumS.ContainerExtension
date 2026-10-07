using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using ContainerExtension;
using OneWare.Essentials.Enums;
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
    public async Task Busybox_CallImage_RunsWhileTheDefaultImageIsMissing()
    {
        using var provider = new E2ETestServiceProvider();
        // An image no daemon has, under a policy that never pulls: only the call's own image can run.
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "containerextension-tests/absent:none");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);

        using var strategy = new DockerExecutionStrategy(provider);
        var command = new ToolCommand
        {
            Executable = "echo",
            ToolName = "echo",
            WorkingDirectory = _workDir,
            CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument("from-the-call-image") },
            StrategyConfigurationOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContainerExtensionModule.StrategyConfigurationImageKey] = "busybox:latest"
            }
        };

        var (success, output) = await strategy.ExecuteAsync(command);

        Assert.True(success, $"expected the call's own image to run; output was: {output}");
        Assert.Contains("from-the-call-image", output, StringComparison.Ordinal);
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
        // Once, like the native strategy: neither an exit code nor a cancellation notice in the error log.
        Assert.Empty(feedback.Errors);
    }

    [FactIfNoCI]
    public async Task Busybox_RunCancelledBeforeTheContainerStarts_ReportsTheCancellationOnce()
    {
        using var provider = CreateBusyboxProvider();
        // From the Info level on, the extension's own diagnostics reach the output handler; the one about the
        // image marks a point after the daemon check and before the container starts.
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.LogLevelSetting, "Info");
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;
        using var cancellation = new CancellationTokenSource();
        var errors = new ConcurrentQueue<string>();
        var command = CreateShellCommand("echo to-stdout",
            outputHandler: line =>
            {
                if (line.Contains("[Docker SDK] Resolving image", StringComparison.Ordinal)) cancellation.Cancel();
                return true;
            },
            errorHandler: line =>
            {
                errors.Enqueue(line);
                return true;
            },
            workingDirectory: _workDir);

        var (success, _) = await strategy.ExecuteAsync(command, cancellation.Token);

        Assert.False(success);
        Assert.Contains(($"[{Path.GetFileName(_workDir)}]: sh cancelled!", (Color?)Colors.DarkOrange), feedback.Notices);
        Assert.Empty(errors);
    }

    [FactIfNoCI]
    public async Task Busybox_RunCancelledWithATimeoutSet_IsNotReportedAsTimedOut()
    {
        using var provider = CreateBusyboxProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.LogLevelSetting, "Info");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.TimeoutSetting, 5.0);
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;
        using var cancellation = new CancellationTokenSource();
        var errors = new ConcurrentQueue<string>();
        var command = CreateShellCommand("echo to-stdout",
            outputHandler: line =>
            {
                if (line.Contains("[Docker SDK] Resolving image", StringComparison.Ordinal)) cancellation.Cancel();
                return true;
            },
            errorHandler: line =>
            {
                errors.Enqueue(line);
                return true;
            },
            workingDirectory: _workDir);

        var (success, _) = await strategy.ExecuteAsync(command, cancellation.Token);

        Assert.False(success);
        Assert.Contains(($"[{Path.GetFileName(_workDir)}]: sh cancelled!", (Color?)Colors.DarkOrange), feedback.Notices);
        Assert.Empty(errors);
    }

    // A timeout that expires before the container starts lands in the cancellation handling and still says so.
    [FactIfNoCI]
    public async Task Busybox_RunTimingOutBeforeItStarts_ReportsTheTimeout()
    {
        using var provider = CreateBusyboxProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.LogLevelSetting, "Info");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.TimeoutSetting, 0.0001); // 6 ms
        using var strategy = new DockerExecutionStrategy(provider);
        strategy.HostFeedback = new RecordingRunFeedback();
        var errors = new ConcurrentQueue<string>();
        var command = CreateShellCommand("echo to-stdout",
            outputHandler: line =>
            {
                // Holds the run before its container starts until the timeout has surely expired.
                if (line.Contains("[Docker SDK] Resolving image", StringComparison.Ordinal)) Thread.Sleep(100);
                return true;
            },
            errorHandler: line =>
            {
                errors.Enqueue(line);
                return true;
            },
            workingDirectory: _workDir);

        var (success, _) = await strategy.ExecuteAsync(command);

        Assert.False(success);
        Assert.Contains(errors, line => line.Contains("timed out", StringComparison.Ordinal));
    }

    // Cancelled before the run even checks the daemon's socket, where the cancellation used to come back
    // as a connection failure.
    [FactIfNoCI]
    public async Task Busybox_RunCancelledBeforeItStarts_ReportsTheCancellationOnce()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var errors = new ConcurrentQueue<string>();
        var command = CreateShellCommand("echo to-stdout",
            outputHandler: _ => true,
            errorHandler: line =>
            {
                errors.Enqueue(line);
                return true;
            },
            workingDirectory: _workDir);

        var (success, _) = await strategy.ExecuteAsync(command, cancellation.Token);

        Assert.False(success);
        Assert.Contains(($"[{Path.GetFileName(_workDir)}]: sh cancelled!", (Color?)Colors.DarkOrange), feedback.Notices);
        Assert.Empty(errors);
    }

    [FactIfNoCI]
    public async Task Busybox_EveryRun_ShowsAStatusEntryUntilItEnds()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;
        var command = new ToolCommand
        {
            Executable = "sh",
            ToolName = "sh",
            WorkingDirectory = _workDir,
            CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument("-c"), new E2ETestCommandArgument("echo to-stdout") },
            StatusMessage = "Running sh...",
            ShowTimer = true
        };

        var (success, output) = await strategy.ExecuteAsync(command);

        Assert.True(success, $"expected container run to succeed; output was: {output}");
        var status = Assert.Single(feedback.Statuses);
        Assert.Equal("Running sh...", status.Message);
        Assert.Equal(AppState.Loading, status.State);
        Assert.True(status.ShowTimer);
        Assert.True(status.Disposed);
    }

    [FactIfNoCI]
    public async Task Busybox_CancellingTheStatusEntry_CancelsTheRun()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var feedback = new RecordingRunFeedback();
        strategy.HostFeedback = feedback;
        var elapsed = Stopwatch.StartNew();

        var run = strategy.ExecuteAsync(CreateShellCommand("sleep 30", workingDirectory: _workDir));
        var status = await feedback.FirstStatus.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        status.CancelFromStatusBar();
        var (success, _) = await run;

        Assert.False(success);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"the run ended only after {elapsed.Elapsed}");
        Assert.Contains(($"[{Path.GetFileName(_workDir)}]: sh cancelled!", (Color?)Colors.DarkOrange), feedback.Notices);
        Assert.True(status.Disposed);
    }

    [FactIfNoCI]
    public async Task Busybox_TerminatedStatusEntry_FailsAnOtherwiseSuccessfulRun()
    {
        using var provider = CreateBusyboxProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        // The user cancelled just as the run ended: too late to stop it, yet the run counts as failed.
        strategy.HostFeedback = new RecordingRunFeedback { StatusesStartTerminated = true };

        var (success, output) = await strategy.ExecuteAsync(CreateShellCommand("echo to-stdout", _ => true, _ => true, _workDir));

        Assert.False(success, $"expected the cancelled status entry to fail the run; output was: {output}");
    }

    [FactIfNoCI]
    public async Task BuildOnlyImage_MissingLocally_FailsWithoutAPull()
    {
        // The toolchain image is built locally and is on no registry: a pull could only fail, or fetch an image
        // that someone else has published under that name.
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, $"fentwums/oss-cad-suite:missing-{Guid.NewGuid():N}");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);

        using var strategy = new DockerExecutionStrategy(provider);
        var (success, output) = await strategy.ExecuteAsync(CreateShellCommand("echo unreachable", workingDirectory: _workDir));

        Assert.False(success, output);
        Assert.Contains("it is built, not pulled", output, StringComparison.Ordinal);
    }

    [FactIfNoCI]
    public async Task PrePull_LeavesTheBuildOnlyImageAlone()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);
        using var strategy = new DockerExecutionStrategy(provider);

        // A pull of this name would find nothing on a registry and throw.
        var error = await Record.ExceptionAsync(() => strategy.PrePullImageAsync($"fentwums/oss-cad-suite:missing-{Guid.NewGuid():N}", TestContext.Current.CancellationToken));

        Assert.Null(error);
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
