using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// The Container Runtime Path setting as the dashboard's terminal commands and the copied <c>docker run</c>
/// commands read it.
/// </summary>
public sealed class RuntimePathTests
{
    [Fact]
    public void GetRuntimePath_FollowsAChangedSetting()
    {
        using var provider = new E2ETestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DockerRuntimePathSetting, "docker");
        Assert.Equal("docker", strategy.GetRuntimePath());

        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DockerRuntimePathSetting, "/usr/local/bin/podman");
        Assert.Equal("/usr/local/bin/podman", strategy.GetRuntimePath());

        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DockerRuntimePathSetting, "");
        Assert.Equal("docker", strategy.GetRuntimePath());
    }
}
