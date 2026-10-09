using System;
using System.Linq;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Whether a run mounts the project writable: the tool's file name and output flags decide, never the folders the tool
/// lies in.
/// </summary>
public sealed class WorkspaceAccessTests
{
    private static ToolCommand Command(string executable, params string[] arguments) => new()
    {
        Executable = executable,
        ToolName = DockerCommandBuilder.ToolFileName(executable),
        WorkingDirectory = "/workspace/dir",
        CommandArguments = arguments.Select(argument => (ICommandArgument)new TestCommandArgument(argument)).ToList()
    };

    private static bool MountsWritable(ToolCommand command)
    {
        var parameters = DockerCommandBuilder.BuildContainerParameters("img", command, null!, null, null, (_, _) => { });
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
}
