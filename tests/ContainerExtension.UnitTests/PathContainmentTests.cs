using System;
using System.Collections.Generic;
using System.IO;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Adversarial corpus for the host→container path-containment boundary. A host path that escapes the
/// mounted workspace — whether by an absolute system path or by `..` traversal — must be collapsed to a
/// sentinel *inside* the workspace, never mapped to the real out-of-tree location; only an explicit
/// allowlist of device and system-library paths passes through verbatim. The companion fixture asserts
/// the non-privileged HostConfig hardening. These are the evidence base for the container-security study.
/// </summary>
public sealed class PathContainmentTests
{
    private const string Sentinel = "/workspace/invalid_escaped_path";

    private static string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void PathUnderWorkspace_MapsIntoWorkspace()
    {
        var wd = NewWorkspace();
        try
        {
            Directory.CreateDirectory(Path.Combine(wd, "rtl"));
            var src = Path.Combine(wd, "rtl", "top.vhd");
            File.WriteAllText(src, "-- vhdl");
            var mapped = DockerCommandBuilder.MapPathToContainer(src, wd);
            Assert.Equal("/workspace/rtl/top.vhd", mapped);
        }
        finally { Directory.Delete(wd, true); }
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/etc/shadow")]
    [InlineData("/root/.ssh/id_rsa")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/proc/self/environ")]
    public void AbsoluteOutOfTreePath_IsContainedToSentinel(string escape)
    {
        var wd = NewWorkspace();
        try
        {
            var mapped = DockerCommandBuilder.MapPathToContainer(escape, wd);
            Assert.Equal(Sentinel, mapped);
        }
        finally { Directory.Delete(wd, true); }
    }

    [Fact]
    public void DotDotTraversalEscape_IsContainedToSentinel()
    {
        var wd = NewWorkspace();
        try
        {
            var traversal = Path.Combine(wd, "..", "..", "..", "..", "..", "etc", "passwd");
            var mapped = DockerCommandBuilder.MapPathToContainer(traversal, wd);
            Assert.Equal(Sentinel, mapped);
        }
        finally { Directory.Delete(wd, true); }
    }

    [Theory]
    [InlineData("/workspace/../etc/passwd")]
    [InlineData("/workspace/../../root/.ssh/id_rsa")]
    [InlineData("/workspace/sub/../../etc/shadow")]
    [InlineData("/workspace/./../../var/run/docker.sock")]
    public void ContainerSpaceTraversalEscape_IsContainedToSentinel(string escape)
    {
        var wd = NewWorkspace();
        try
        {
            // A path already expressed in container space (/workspace/...) must still be collapsed and
            // contained: "/workspace/../etc/passwd" must not escape the bind mount via the early-return path.
            Assert.Equal(Sentinel, DockerCommandBuilder.MapPathToContainer(escape, wd));
        }
        finally { Directory.Delete(wd, true); }
    }

    [Theory]
    [InlineData("/workspace/rtl/top.vhd", "/workspace/rtl/top.vhd")]
    [InlineData("/workspace/sub/../top.vhd", "/workspace/top.vhd")]
    [InlineData("/workspace/./build/out.json", "/workspace/build/out.json")]
    public void ContainerSpacePathWithinWorkspace_CollapsesButStaysContained(string input, string expected)
    {
        var wd = NewWorkspace();
        try
        {
            Assert.Equal(expected, DockerCommandBuilder.MapPathToContainer(input, wd));
        }
        finally { Directory.Delete(wd, true); }
    }

    [Theory]
    [InlineData("/dev/null")]
    [InlineData("/usr/bin/yosys")]
    [InlineData("/bin/sh")]
    [InlineData("/lib/x86_64-linux-gnu/libc.so.6")]
    public void AllowlistedSystemPath_PassesThroughVerbatim(string p)
    {
        var wd = NewWorkspace();
        try
        {
            Assert.Equal(p, DockerCommandBuilder.MapPathToContainer(p, wd));
        }
        finally { Directory.Delete(wd, true); }
    }

    [Fact]
    public void NonPrivilegedHostConfig_IsHardened()
    {
        var wd = NewWorkspace();
        try
        {
            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = wd,
                CommandArguments = new List<ICommandArgument> { new TestCommandArgument("--version") },
            };
            var p = DockerCommandBuilder.BuildContainerParameters("img:latest", command, null!, null, null, (_, _) => { });
            var hc = p.HostConfig;
            Assert.NotNull(hc);
            Assert.Contains("ALL", hc.CapDrop);
            Assert.Contains("no-new-privileges:true", hc.SecurityOpt);
            Assert.Equal(4096, hc.PidsLimit);
            Assert.True(hc.Init);
            Assert.False(hc.Privileged);
        }
        finally { Directory.Delete(wd, true); }
    }
}
