using System;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for how the daemon address is read as this machine or another one, which decides whether plain http is
/// kept and whether a run warns about a remote daemon. No daemon is needed: nothing listens on the port.
/// </summary>
public sealed class DaemonAddressTests
{
    [Fact]
    public async Task CreateAsync_HttpOnTheIpv6Loopback_StaysOnHttp()
    {
        // Uri.Host gives [::1] with its brackets, while only ::1 counted as loopback, so the address was raised to
        // https and the TLS handshake with the daemon failed.
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, "http://[::1]:9");

        var connection = await DockerConnectionFactory.CreateAsync(settings, TestContext.Current.CancellationToken);
        using var client = connection.Client;
        using var provider = connection.ConnectionProvider;

        Assert.Equal(new Uri("http://[::1]:9"), connection.DaemonUri);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("[::1]", true)]
    [InlineData("::1", true)]
    [InlineData("127.0.0.1.example.com", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("[2001:db8::1]", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void IsLoopbackHost_ReadsOnlyThisMachineAsLoopback(string host, bool loopback)
    {
        Assert.Equal(loopback, DockerConnectionFactory.IsLoopbackHost(host));
    }
}
