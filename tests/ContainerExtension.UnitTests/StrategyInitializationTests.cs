using System;
using System.Threading.Tasks;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using Docker.DotNet;
using OneWare.Essentials.Services;
using Xunit;

namespace ContainerExtension.UnitTests;

// Constructing DockerExecutionStrategy triggers background telemetry init that touches the process-global
// sink, so this class shares the telemetry serialization collection.
[Collection("TelemetryTests")]
public sealed class StrategyInitializationTests
{
    [Fact]
    public async Task AdoptConnection_WhenArmingTheReaperThrows_LeavesTheStrategyOffline()
    {
        using var provider = new TestServiceProvider();
        // An unreachable socket lets the strategy's own initialization finish offline before the test adopts
        // a connection of its own.
        var settings = (MockSettingsService)provider.GetService(typeof(ISettingsService))!;
        settings.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, "unix:///invalid/offline/socket.sock");
        using var strategy = new DockerExecutionStrategy(provider);
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);

        var daemonUri = new Uri("http://127.0.0.1:9");
        using var client = new DockerClientConfiguration(daemonUri).CreateClient();
        var connection = new DockerConnectionFactory.Connection("docker", daemonUri, client, null, null, null);

        var ex = Record.Exception(() => strategy.AdoptConnection(connection,
            _ => throw new InvalidOperationException("Registering the Ctrl-C handler failed.")));

        Assert.Null(ex);
        Assert.Null(strategy.Client);
    }
}
