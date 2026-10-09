using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ContainerExtension;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for building the daemon connection anew before a run, against a stand-in daemon on 127.0.0.1 that
/// starts only after the strategy did. The runs record their failures, so the class keeps telemetry in a folder of
/// its own and shares the telemetry collection.
/// </summary>
[Collection("TelemetryTests")]
public sealed class DaemonReconnectTests : IDisposable
{
    private readonly string _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_Reconnect", Guid.NewGuid().ToString("N"));

    public DaemonReconnectTests()
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
    public async Task ExecuteAsync_DaemonStartedAfterTheStrategy_IsReachedWithItsApiVersion()
    {
        // Nothing answers while the strategy starts, so its connection falls back to a fixed API version.
        var port = DockerDaemonDouble.FreePort();
        using var provider = ProviderFor($"http://127.0.0.1:{port}");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        using var daemon = DockerDaemonDouble.Start(port, "1.47");

        await strategy.ExecuteAsync(EchoCommand(), TestContext.Current.CancellationToken);

        Assert.Contains(daemon.Paths, path => path.StartsWith("/v1.47/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_AfterAStartWithoutConnection_BuildsItAnew()
    {
        // No client can be built for this address, so the strategy starts without a connection. Then the address is
        // set to one where a daemon answers.
        var port = DockerDaemonDouble.FreePort();
        using var provider = ProviderFor("ftp://127.0.0.1:1");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        Assert.Null(strategy.Client);
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"http://127.0.0.1:{port}");
        using var daemon = DockerDaemonDouble.Start(port, "1.47");

        await strategy.ExecuteAsync(EchoCommand(), TestContext.Current.CancellationToken);

        Assert.Contains(daemon.Paths, path => path.StartsWith("/v1.47/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_DaemonAnsweredAtStart_KeepsTheConnection()
    {
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47");
        using var provider = ProviderFor($"http://127.0.0.1:{port}");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };

        await strategy.ExecuteAsync(EchoCommand(), TestContext.Current.CancellationToken);
        await strategy.ExecuteAsync(EchoCommand(), TestContext.Current.CancellationToken);

        // Only the startup asked for the version; neither run built the connection anew.
        Assert.Single(daemon.Paths, path => path == "/version");
    }

    [Fact]
    public async Task ExecuteAsync_DaemonAnswersTheVersionWithAnError_KeepsTheConnection()
    {
        // A daemon that answers the version request with an error status answers all the same.
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47", versionStatus: "500 Internal Server Error");
        using var provider = ProviderFor($"http://127.0.0.1:{port}");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };

        await strategy.ExecuteAsync(EchoCommand(), TestContext.Current.CancellationToken);
        await strategy.ExecuteAsync(EchoCommand(), TestContext.Current.CancellationToken);

        Assert.Single(daemon.Paths, path => path == "/version");
    }

    private static E2ETestServiceProvider ProviderFor(string daemonSocket)
    {
        var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, daemonSocket);
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        return provider;
    }

    private static ToolCommand EchoCommand() => new()
    {
        Executable = "echo",
        ToolName = "echo",
        WorkingDirectory = Path.GetTempPath(),
        CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument("unreached") }
    };
}
