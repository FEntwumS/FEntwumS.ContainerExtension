using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ContainerExtension;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

public sealed class QualityVerificationTests
{
    private List<ICommandArgument> BuildArgs(params string[] args)
    {
        return args.Select(a => (ICommandArgument)new E2ETestCommandArgument(a)).ToList();
    }

    [Fact]
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

    [Fact]
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
        // Create the strategy. This starts InitializeInternalAsync in the background.
        using var strategy = new DockerExecutionStrategy(provider);

        // Instantly read property getters. Since it runs asynchronously, it should not block
        // the thread and return immediately.
        var stopwatch = Stopwatch.StartNew();

        var runtime = strategy.DetectedRuntime;
        var name = strategy.GetStrategyName();
        var key = strategy.GetStrategyKey();
        var path = strategy.GetRuntimePath();
        var settings = strategy.GetActiveSettingsSummary();
        var defaultImg = strategy.GetDefaultImage();

        stopwatch.Stop();

        // Ensure property retrieval takes very little time (e.g. less than 50 milliseconds)
        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Property getters blocked for {stopwatch.ElapsedMilliseconds}ms");
    }
}
