using System;
using System.Collections.Generic;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// The user a container runs as. A rootful runtime gets the host uid:gid, so output files belong to the host user. A
/// rootless runtime gets root ("0:0"), which its user namespace maps to the invoking host user; the image's own user
/// would map to a subordinate host id that cannot write to the mounted project. Windows keeps the image's default user.
/// </summary>
public sealed class ContainerUserTests
{
    private static string? UserFor(string? uid, string? gid, bool isRootless)
    {
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument("-q") }
        };

        return DockerCommandBuilder.BuildContainerParameters(
            "img", command, null!, uid, gid, (_, _) => { }, isRootless: isRootless).User;
    }

    [Theory]
    [InlineData("501", "20")]
    [InlineData("1000", "1000")]
    [InlineData(null, null)]
    public void RootlessRuntime_RunsAsRoot_WhateverTheHostIds(string? uid, string? gid)
    {
        var user = UserFor(uid, gid, isRootless: true);

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(user);
        }
        else
        {
            Assert.Equal("0:0", user);
        }
    }

    [Theory]
    [InlineData("501", "20")]
    [InlineData("1000", "1000")]
    public void RootfulRuntime_RunsAsTheHostIds(string uid, string gid)
    {
        var user = UserFor(uid, gid, isRootless: false);

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(user);
        }
        else
        {
            Assert.Equal($"{uid}:{gid}", user);
        }
    }

    [Fact]
    public void RootfulRuntime_WithoutHostIds_KeepsTheImageUser()
    {
        Assert.Null(UserFor(null, null, isRootless: false));
    }
}
