using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

// Constructing DockerExecutionStrategy (in several tests below) triggers background telemetry init that
// touches the process-global sink, so this class must share the telemetry serialization collection or it
// can flake when run in parallel with the telemetry suite.
[Collection("TelemetryTests")]
public sealed class QualityVerificationTests
{
    private List<ICommandArgument> BuildArgs(params string[] args)
    {
        return args.Select(a => (ICommandArgument)new E2ETestCommandArgument(a)).ToList();
    }

    // Exercises the real Docker execution path, so it is gated out of CI like the E2E suite.
    [FactIfNoCI]
    public void StartWeakProcess_ReturnsValidProcess_PropertiesReadable()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "echo",
            ToolName = "echo",
            CommandArguments = BuildArgs("hello")
        };

        var weakRef = strategy.StartWeakProcess(command);
        Assert.NotNull(weakRef);

        bool hasTarget = weakRef.TryGetTarget(out var process);
        Assert.True(hasTarget);
        Assert.NotNull(process);

        // Verify we can read properties without exceptions
        int pid = -1;
        var pidEx = Record.Exception(() => pid = process.Id);
        Assert.Null(pidEx);
        Assert.True(pid > 0);

        // Wait for it to exit (since the dummy process is terminated in the finally block of the container task)
        bool exited = process.WaitForExit(15000);
        Assert.True(exited);

        bool hasExited = false;
        int exitCode = -1;
        var exitedEx = Record.Exception(() => hasExited = process.HasExited);
        var exitCodeEx = Record.Exception(() => exitCode = process.ExitCode);

        Assert.Null(exitedEx);
        Assert.Null(exitCodeEx);

        Assert.True(hasExited);
    }

    // Requires a reachable Docker daemon, so it is gated out of CI like the E2E suite.
    [FactIfNoCI]
    public async Task StartWeakProcess_KillCall_CancelsExecution()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.FallbackImage);
        using var strategy = new DockerExecutionStrategy(provider);

        var stdoutList = new List<string>();
        var stderrList = new List<string>();
        // A command that will run for a short time
        var command = new ToolCommand
        {
            Executable = "sleep",
            ToolName = "sleep",
            CommandArguments = BuildArgs("5"),
            OutputHandler = msg => { lock (stdoutList) stdoutList.Add(msg); return true; },
            ErrorHandler = msg => { lock (stderrList) stderrList.Add(msg); return true; }
        };

        var weakRef = strategy.StartWeakProcess(command);
        Assert.True(weakRef.TryGetTarget(out var process));
        Assert.NotNull(process);

        // Call Kill on the returned process
        var killEx = Record.Exception(() => process.Kill());
        Assert.Null(killEx);

        // Wait for dummy process to exit
        bool exited = process.WaitForExit(10000);
        Assert.True(exited);

        // Wait a short delay to allow background cancellation task to run
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Verify that the container was cancelled
        lock (stderrList)
        {
            Assert.NotEmpty(stderrList);
            Assert.Contains(stderrList, line => line.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void LazyInitialization_DoesNotBlockUIOrPropertyGetters()
    {
        using var provider = new E2ETestServiceProvider();
        // Construction starts InitializeInternalAsync in the background; the getters must not join it.
        using var strategy = new DockerExecutionStrategy(provider);

        var ex = Record.Exception(() =>
        {
            _ = strategy.DetectedRuntime;
            _ = strategy.GetStrategyName();
            _ = strategy.GetStrategyKey();
            _ = strategy.GetRuntimePath();
            _ = strategy.GetActiveSettingsSummary();
            _ = strategy.GetDefaultImage();
        });

        Assert.Null(ex);
    }

    // A late-arriving resource-stats profile (the sampler always reports OomKilled=false) must never
    // overwrite the OOM correction RunContainerAsync applies after inspecting an OOM-killed container.
    [Fact]
    public void MergeLateResourceProfile_RetainsOomCorrection_OverLateStatsProfile()
    {
        var corrected = new ContainerRunner.ResourceProfile(0, 0, 0, OomKilled: true);
        var lateStats = new ContainerRunner.ResourceProfile(1024, 42.0, 5, OomKilled: false);

        var merged = ContainerRunner.MergeLateResourceProfile(corrected, lateStats);

        Assert.Same(corrected, merged);
        Assert.True(merged is { OomKilled: true });
    }

    [Fact]
    public void MergeLateResourceProfile_AdoptsLateProfile_WhenNothingCapturedYet()
    {
        var lateStats = new ContainerRunner.ResourceProfile(2048, 12.5, 3, OomKilled: false);

        Assert.Same(lateStats, ContainerRunner.MergeLateResourceProfile(null, lateStats));
    }

    [Fact]
    public void MergeLateResourceProfile_RetainsCapture_WhenLateProfileMissing()
    {
        var captured = new ContainerRunner.ResourceProfile(4096, 7.5, 9, OomKilled: false);

        Assert.Same(captured, ContainerRunner.MergeLateResourceProfile(captured, null));
    }
}
