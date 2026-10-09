using System;
using System.Collections.Generic;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// A port range given with <c>-p</c> in the Extra Container Labels publishes what it names: every port of the range,
/// at most 1024, and nothing when the host and container ranges differ in length.
/// </summary>
public sealed class PortRangeMappingTests
{
    private static CreateContainerParameters Build(string extraLabels, List<string> log)
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, extraLabels);
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>()
        };
        return DockerCommandBuilder.BuildContainerParameters("img", command, settings, null, null, (_, line) => log.Add(line));
    }

    [Fact]
    public void RangeWithinTheLimit_PublishesEveryPort()
    {
        var parameters = Build("-p 8000-8009:9000-9009", []);

        Assert.Equal(10, parameters.HostConfig.PortBindings.Count);
        Assert.Equal("8009", parameters.HostConfig.PortBindings["9009/tcp"][0].HostPort);
    }

    [Fact]
    public void RangeOverTheLimit_PublishesExactly1024Ports()
    {
        var log = new List<string>();

        var parameters = Build("-p 8000-9100:8000-9100", log);

        Assert.Equal(1024, parameters.HostConfig.PortBindings.Count);
        Assert.Equal(1024, parameters.ExposedPorts.Count);
        Assert.True(parameters.HostConfig.PortBindings.ContainsKey("9023/tcp"));
        Assert.False(parameters.HostConfig.PortBindings.ContainsKey("9024/tcp"));
        Assert.Contains(log, line => line.Contains("exceeds 1024 ports", StringComparison.Ordinal));
    }

    [Fact]
    public void RangesOfUnequalLength_AreRejectedWithAWarning()
    {
        var log = new List<string>();

        var parameters = Build("-p 8000-8010:9000-9005", log);

        Assert.Empty(parameters.HostConfig.PortBindings);
        Assert.Contains(log, line => line.Contains("'8000-8010:9000-9005'", StringComparison.Ordinal)
            && line.Contains("differ in length", StringComparison.Ordinal));
    }
}
