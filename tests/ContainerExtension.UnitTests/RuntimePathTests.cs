using System;
using System.IO;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// The Container Runtime Path setting as the dashboard's terminal commands and the copied <c>docker run</c>
/// commands read it: the path the user configured, current at every call, and quoted only by the command
/// that uses it.
/// </summary>
public sealed class RuntimePathTests
{
    // Paths where Docker Desktop, Podman and Rancher Desktop install their command line tool.
    public static readonly TheoryData<string> InstalledRuntimePaths = new()
    {
        @"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
        @"C:\Program Files (x86)\RedHat\Podman\podman.exe",
        "/Applications/Rancher Desktop.app/Contents/Resources/resources/darwin/bin/docker",
    };

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

    [Theory]
    [MemberData(nameof(InstalledRuntimePaths))]
    public void GetRuntimePath_ReturnsTheConfiguredPathAsItIs(string configured)
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DockerRuntimePathSetting, configured);
        using var strategy = new DockerExecutionStrategy(provider);

        Assert.Equal(configured, strategy.GetRuntimePath());
    }

    [Theory]
    [MemberData(nameof(InstalledRuntimePaths))]
    public void CopiedCommand_StartsTheConfiguredRuntime(string configured)
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DockerRuntimePathSetting, configured);
        using var strategy = new DockerExecutionStrategy(provider);
        var p = new CreateContainerParameters { Image = "myimage" };

        var posix = DockerRunCommandFormatter.Reconstruct(p, strategy.GetRuntimePath(), shell: CommandShell.Posix);
        var powerShell = DockerRunCommandFormatter.Reconstruct(p, strategy.GetRuntimePath(), shell: CommandShell.PowerShell);

        Assert.StartsWith(DockerRunCommandFormatter.Quote(configured, CommandShell.Posix) + " run ", posix, StringComparison.Ordinal);
        Assert.StartsWith("& " + DockerRunCommandFormatter.Quote(configured, CommandShell.PowerShell) + " run ", powerShell, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRuntimePath_SeesARuntimeInstalledOrRemovedAfterTheFirstCheck()
    {
        var validate = typeof(ContainerExtensionModule).GetMethod("ValidateRuntimePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(validate);
        var folder = Path.Combine(Path.GetTempPath(), "runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var runtime = Path.Combine(folder, "docker");
        try
        {
            Assert.False((bool)validate.Invoke(null, [runtime])!);

            File.WriteAllText(runtime, "");
            Assert.True((bool)validate.Invoke(null, [runtime])!);

            File.Delete(runtime);
            Assert.False((bool)validate.Invoke(null, [runtime])!);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("docker; rm -rf ~")]
    [InlineData("docker && id")]
    [InlineData("$(id)")]
    [InlineData("docker`id`")]
    [InlineData("doc\"ker")]
    [InlineData("doc'ker")]
    [InlineData("docker\nid")]
    [InlineData("-H tcp://example.com")]
    public void GetRuntimePath_RefusesAPathAShellWouldRead(string configured)
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DockerRuntimePathSetting, configured);
        using var strategy = new DockerExecutionStrategy(provider);

        Assert.Throws<DockerExecutionException>(() => strategy.GetRuntimePath());
    }
}
