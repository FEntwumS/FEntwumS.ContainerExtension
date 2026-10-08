using System;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for how <see cref="DockerConnectionFactory"/> reads a daemon address. The address leads to a pipe
/// that nobody serves, so the factory creates the client but never reaches a daemon.
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
}
