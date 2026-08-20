using System.Collections.Generic;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Daemon-free coverage for <see cref="DockerRunCommandFormatter"/>: the settings-driven template and
/// the exact-command reconstruction, including the environment-value masking that keeps secrets out of
/// the persisted telemetry log.
/// </summary>
public sealed class DockerRunCommandFormatterTests
{
    [Fact]
    public void Generate_RendersExtraFlagsAsLabels()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, "--label custom=val");

        var runCommand = DockerRunCommandFormatter.Generate(settings, "docker", "myimage");

        Assert.StartsWith("docker run", runCommand);
        Assert.Contains("--label custom=val", runCommand);
        Assert.Contains("myimage <tool> <args>", runCommand);
    }

    [Fact]
    public void Reconstruct_MasksEnvValuesByDefault()
    {
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            Env = new List<string> { "LICENSE_KEY=super-secret-value" },
        };

        var masked = DockerRunCommandFormatter.Reconstruct(p, "docker");

        Assert.Contains("LICENSE_KEY=********", masked);
        Assert.DoesNotContain("super-secret-value", masked);
    }

    [Fact]
    public void Reconstruct_RendersRealEnvValuesWhenMaskingDisabled()
    {
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            Env = new List<string> { "LICENSE_KEY=super-secret-value" },
        };

        var verbatim = DockerRunCommandFormatter.Reconstruct(p, "docker", maskEnvValues: false);

        Assert.Contains("LICENSE_KEY=super-secret-value", verbatim);
    }

    [Fact]
    public void Reconstruct_QuotesArgumentsWithShellMetacharacters()
    {
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            Cmd = new List<string> { "sh", "-c", "echo hi; rm -rf /" },
        };

        var command = DockerRunCommandFormatter.Reconstruct(p, "docker");

        Assert.Contains("\"echo hi; rm -rf /\"", command);
    }
}
