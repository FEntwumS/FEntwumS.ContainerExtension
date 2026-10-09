using System;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for how <see cref="DockerConnectionFactory"/> reads a daemon address and which API version its client
/// asks for. One address leads to a pipe that nobody serves, so the factory creates the client but never reaches a
/// daemon; the others lead to a stand-in daemon on 127.0.0.1, which records the version in each request's path.
/// </summary>
public sealed class DockerConnectionFactoryTests
{
    [Fact]
    public async Task CreateAsync_PipeAddressWithFourSlashes_ConnectsAsTheTwoSlashForm()
    {
        // Docker's own default address for the Windows pipe has four slashes: npipe:////./pipe/docker_engine.
        var name = $"missing-{Guid.NewGuid():N}";
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"npipe:////./pipe/{name}");
        settings.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);

        var connection = await DockerConnectionFactory.CreateAsync(settings, TestContext.Current.CancellationToken);
        using var client = connection.Client;
        using var provider = connection.ConnectionProvider;

        Assert.NotNull(client);
        Assert.Equal(new Uri($"npipe://./pipe/{name}"), connection.DaemonUri);
    }

    [Fact]
    public async Task CreateAsync_DaemonAnswers_UsesItsApiVersion()
    {
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47");

        var connection = await DockerConnectionFactory.CreateAsync(SettingsFor(port), TestContext.Current.CancellationToken);
        using var client = connection.Client;
        using var provider = connection.ConnectionProvider;
        await client!.System.PingAsync(TestContext.Current.CancellationToken);

        Assert.Contains("/v1.47/_ping", daemon.Paths);
    }

    [Fact]
    public async Task CreateAsync_DaemonAnswersWithoutAVersion_UsesTheFallbackApiVersion()
    {
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, apiVersion: null);

        var connection = await DockerConnectionFactory.CreateAsync(SettingsFor(port), TestContext.Current.CancellationToken);
        using var client = connection.Client;
        using var provider = connection.ConnectionProvider;
        await client!.System.PingAsync(TestContext.Current.CancellationToken);

        Assert.Contains($"/v{DockerConnectionFactory.FallbackApiVersion}/_ping", daemon.Paths);
    }

    [Fact]
    public async Task CreateAsync_NoDaemonAnswers_UsesTheSameFallbackApiVersion()
    {
        // Nothing listens on the port while the connection is built; the double starts only afterwards, to see which
        // version the client asks for.
        var port = DockerDaemonDouble.FreePort();

        var connection = await DockerConnectionFactory.CreateAsync(SettingsFor(port), TestContext.Current.CancellationToken);
        using var client = connection.Client;
        using var provider = connection.ConnectionProvider;
        using var daemon = DockerDaemonDouble.Start(port, "1.47");
        await client!.System.PingAsync(TestContext.Current.CancellationToken);

        Assert.Contains($"/v{DockerConnectionFactory.FallbackApiVersion}/_ping", daemon.Paths);
    }

    private static MockSettingsService SettingsFor(int port)
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"http://127.0.0.1:{port}");
        return settings;
    }
}
