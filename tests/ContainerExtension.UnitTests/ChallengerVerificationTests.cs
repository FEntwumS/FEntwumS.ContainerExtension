using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ContainerExtension;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

[Collection("TelemetryTests")]
public sealed class ChallengerVerificationTests : IDisposable
{
    private readonly string _testTelemetryDir;

    public ChallengerVerificationTests()
    {
        _testTelemetryDir = Path.Combine(Path.GetTempPath(), "ChallengerTests", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_testTelemetryDir);
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_testTelemetryDir))
            {
                Directory.Delete(_testTelemetryDir, true);
            }
        }
        catch { }
    }

    private List<ICommandArgument> BuildArgs(params string[] args)
    {
        return args.Select(a => (ICommandArgument)new E2ETestCommandArgument(a)).ToList();
    }

    // 1. Verify that calling StartWeakProcess returns a valid started Process object.
    // Assert that we can read its properties (Id, HasExited, ExitCode) without throwing exceptions.
    [Fact]
    public void StartWeakProcess_ReturnsValidProcess_PropertiesAreReadable()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "ghdl",
            WorkingDirectory = AppContext.BaseDirectory,
            CommandArguments = BuildArgs("--version")
        };

        var weakRef = strategy.StartWeakProcess(command);
        Assert.NotNull(weakRef);

        bool retrieved = weakRef.TryGetTarget(out var process);
        Assert.True(retrieved, "Should be able to retrieve target Process from WeakReference");
        Assert.NotNull(process);

        // Read properties that are readable while running
        int id = -1;
        var idEx = Record.Exception(() => id = process.Id);
        Assert.Null(idEx);
        Assert.True(id > 0, $"Process ID should be positive, got {id}");

        // Wait for it to exit (since the dummy process is terminated in the finally block of the container task)
        bool exited = process.WaitForExit(15000);
        Assert.True(exited, "Process should have exited after the container execution completes");

        bool hasExited = false;
        int exitCode = -1;

        var hasExitedEx = Record.Exception(() => hasExited = process.HasExited);
        var exitCodeEx = Record.Exception(() => exitCode = process.ExitCode);

        Assert.Null(hasExitedEx);
        Assert.Null(exitCodeEx);

        Assert.True(hasExited);
    }

    // 2. Verify whether calling Kill() on the returned process triggers container execution cancellation.
    [Fact]
    public async Task StartWeakProcess_CallingKill_CancelsContainerExecution()
    {
        using var provider = new E2ETestServiceProvider();
        // Set setting to use docker fallback image
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, ContainerExtensionModule.FallbackImage);

        using var strategy = new DockerExecutionStrategy(provider);

        var stdoutList = new List<string>();
        var stderrList = new List<string>();
        var command = new ToolCommand
        {
            Executable = "sleep",
            ToolName = "sleep",
            WorkingDirectory = AppContext.BaseDirectory,
            CommandArguments = BuildArgs("5"),
            OutputHandler = msg => { lock (stdoutList) stdoutList.Add(msg); return true; },
            ErrorHandler = msg => { lock (stderrList) stderrList.Add(msg); return true; }
        };

        var weakRef = strategy.StartWeakProcess(command);
        Assert.NotNull(weakRef);

        bool retrieved = weakRef.TryGetTarget(out var process);
        Assert.True(retrieved);
        Assert.NotNull(process);

        // Verify that calling Kill() successfully cancels container execution
        var killException = Record.Exception(() => process.Kill());
        Assert.Null(killException);

        // Wait for the dummy process to exit (since it is killed immediately, it should exit instantly)
        bool exited = process.WaitForExit(10000);
        Assert.True(exited);

        // Wait a short delay to allow background cancellation task to run and propagate the cancellation
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Verify that the container was cancelled
        lock (stderrList)
        {
            Assert.NotEmpty(stderrList);
            Assert.Contains(stderrList, line => line.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        }
    }

    // 3. Verify that the asynchronous lazy initialization path in DockerExecutionStrategy.cs
    // indeed avoids UI blocking (i.e. checking strategy property getters does not cause thread blocking or hang on _initTask).
    [Fact]
    public void LazyInitialization_PropertyGetters_DoNotBlockCallingThread()
    {
        using var provider = new E2ETestServiceProvider();

        // We measure the time taken to construct the strategy and access its properties immediately.
        // If it blocks/hangs waiting for Docker daemon ping/negotiation (which takes time), it would take significant time.
        var stopwatch = Stopwatch.StartNew();

        var strategy = new DockerExecutionStrategy(provider);

        // Access properties immediately without waiting for _initTask to finish.
        var runtime = strategy.DetectedRuntime;
        var name = strategy.GetStrategyName();
        var key = strategy.GetStrategyKey();
        var path = strategy.GetRuntimePath();

        stopwatch.Stop();

        // Since constructor returns immediately and properties are non-blocking,
        // it should complete in a few milliseconds.
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Accessing properties took {stopwatch.ElapsedMilliseconds}ms, which suggests blocking behavior!");

        // At this point, the background init task _initTask is still running.
        // We verify that the thread accessing the properties was not blocked.
        strategy.Dispose();
    }
}
