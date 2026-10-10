using System;
using System.Collections.Generic;
using System.Linq;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Whether a run mounts the project writable: the tool's file name and output flags decide, never the folders the tool
/// lies in, unless a <c>docker.workspace</c> of the call or of the tool names rw or ro. Joins the telemetry collection
/// because the strategy tests construct a <see cref="DockerExecutionStrategy"/>, whose background initialization
/// touches the process-global telemetry sink.
/// </summary>
[Collection("TelemetryTests")]
public sealed class WorkspaceAccessTests
{
    private static ToolCommand Command(string executable, params string[] arguments) => new()
    {
        Executable = executable,
        ToolName = DockerCommandBuilder.ToolFileName(executable),
        WorkingDirectory = "/workspace/dir",
        CommandArguments = arguments.Select(argument => (ICommandArgument)new TestCommandArgument(argument)).ToList()
    };

    private static bool MountsWritable(ToolCommand command, bool? workspaceWritable = null)
    {
        var parameters = DockerCommandBuilder.BuildContainerParameters("img", command, null!, null, null, (_, _) => { }, workspaceWritable: workspaceWritable);
        if (parameters.HostConfig.Binds.Any(bind => bind.EndsWith(":/workspace:ro", StringComparison.Ordinal)))
        {
            return false;
        }
        Assert.Contains(parameters.HostConfig.Binds, bind => bind.EndsWith(":/workspace", StringComparison.Ordinal));
        return true;
    }

    // Every tool of a toolchain that OneWare installed lies under a folder named Packages, which contains "pack".
    [Theory]
    [InlineData("/Users/u/Documents/OneWare/Packages/oss-cad-suite/bin/icebram")]
    [InlineData(@"C:\Users\u\Documents\OneWare\Packages\oss-cad-suite\bin\icebram.exe")]
    [InlineData("/home/jpackard/oss-cad-suite/bin/icebram")]
    public void ReadingTool_UnderAFolderNamedLikeAWriter_GetsTheProjectReadOnly(string executable)
    {
        Assert.False(MountsWritable(Command(executable)));
    }

    [Theory]
    [InlineData("/Users/u/Documents/OneWare/Packages/oss-cad-suite/bin/icepack")]
    [InlineData("iceunpack")]
    [InlineData("gowin_pack")]
    [InlineData(@"C:\oss-cad-suite\bin\nextpnr-ice40.exe")]
    [InlineData("yosys")]
    [InlineData("yosys-smtbmc")]
    [InlineData("sby")]
    [InlineData("ghdl")]
    [InlineData("iverilog")]
    [InlineData("vvp")]
    [InlineData("verilator_bin")]
    [InlineData("icepll")]
    [InlineData("ecppll")]
    [InlineData("gowin_pll")]
    [InlineData("ecpbram")]
    [InlineData("icetime")]
    [InlineData("prjoxide")]
    [InlineData("vcd2fst")]
    [InlineData("hex2bin")]
    public void WritingTool_GetsTheProjectWritable(string executable)
    {
        Assert.True(MountsWritable(Command(executable)));
    }

    // OneWare runs the model that Verilator built from build/sim/verilator/<bench>/simulation as the tool verilator.
    [Theory]
    [InlineData("/home/u/proj/build/sim/verilator/tb/simulation")]
    [InlineData(@"C:\proj\build\sim\verilator\tb\simulation.exe")]
    public void ModelRunAsAWritingTool_GetsTheProjectWritable(string executable)
    {
        var command = new ToolCommand
        {
            Executable = executable,
            ToolName = "verilator",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = []
        };

        Assert.True(MountsWritable(command));
    }

    [Fact]
    public void ProgrammerNamedAsTheTool_GetsTheProjectReadOnly_UnderAnyFileName()
    {
        var command = new ToolCommand
        {
            Executable = "/opt/tools/flash",
            ToolName = "openFPGALoader",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = [new TestCommandArgument("-o"), new TestCommandArgument("0x100000")]
        };

        Assert.False(MountsWritable(command));
    }

    [Theory]
    [InlineData("/Users/u/Documents/OneWare/Packages/oss-cad-suite/bin/openFPGALoader")]
    [InlineData("fujprog")]
    public void Programmer_GetsTheProjectReadOnly_WhateverItsFlagsMean(string executable)
    {
        Assert.False(MountsWritable(Command(executable, "-o", "0x100000")));
    }

    [Fact]
    public void UnknownTool_GetsTheProjectWritable_OnlyWithAnOutputFlag()
    {
        Assert.False(MountsWritable(Command("some-unknown-tool", "in.txt")));
        Assert.True(MountsWritable(Command("some-unknown-tool", "-o", "out.txt")));
    }

    [Fact]
    public void WorkspaceAccessGiven_OverridesTheToolName()
    {
        Assert.True(MountsWritable(Command("icebram"), workspaceWritable: true));
        Assert.False(MountsWritable(Command("yosys"), workspaceWritable: false));
    }

    [Theory]
    [InlineData("rw", true)]
    [InlineData(" RO ", false)]
    public void ResolveWorkspaceAccess_TakesTheValueOfTheCall(string access, bool writable)
    {
        using var provider = ProviderWithToolAccess("ro");
        using var strategy = new DockerExecutionStrategy(provider);
        var command = new ToolCommand
        {
            Executable = "icebram",
            ToolName = "icebram",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>(),
            StrategyConfigurationOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ContainerExtensionModule.StrategyConfigurationWorkspaceKey] = access
            }
        };

        Assert.Equal(writable, strategy.ResolveWorkspaceAccess(command));
    }

    [Fact]
    public void ResolveWorkspaceAccess_TakesTheValueOfTheTool_WhenTheCallNamesNone()
    {
        using var provider = ProviderWithToolAccess("rw");
        using var strategy = new DockerExecutionStrategy(provider);

        Assert.True(strategy.ResolveWorkspaceAccess(Command("icebram")));
    }

    [Fact]
    public void ResolveWorkspaceAccess_WithoutAValue_LeavesTheDecisionToTheToolName()
    {
        using var provider = new TestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);

        Assert.Null(strategy.ResolveWorkspaceAccess(Command("icebram")));
    }

    [Fact]
    public void ResolveWorkspaceAccess_RejectsAValueOtherThanRwOrRo()
    {
        using var provider = ProviderWithToolAccess("writable");
        using var strategy = new DockerExecutionStrategy(provider);

        var error = Assert.Throws<DockerExecutionException>(() => strategy.ResolveWorkspaceAccess(Command("icebram")));
        Assert.Contains("tool 'icebram'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("writable", error.Message, StringComparison.Ordinal);
    }

    private static TestServiceProvider ProviderWithToolAccess(string access) => new()
    {
        ToolService = new StrategyConfigurationToolService()
            .WithConfiguration("icebram", ContainerExtensionModule.StrategyConfigurationWorkspaceKey, access)
    };
}
