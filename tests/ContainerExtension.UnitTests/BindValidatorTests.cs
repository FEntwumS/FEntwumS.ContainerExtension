using System;
using System.Collections.Generic;
using System.IO;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for <see cref="BindValidator"/>: rejection of critical host/container mount targets and
/// in-place canonicalization of benign binds. Reached directly through InternalsVisibleTo.
/// </summary>
public sealed class BindValidatorTests
{
    [Theory]
    [InlineData("/etc")]
    [InlineData("/proc")]
    [InlineData("/sys")]
    public void ValidateBinds_RejectsCriticalHostMounts(string hostPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The blocked-path set differs on Windows; these POSIX roots do not apply.
        }
        var binds = new List<string> { $"{hostPath}:/workspace:ro" };
        Assert.Throws<DockerExecutionException>(() => BindValidator.ValidateBinds(binds));
    }

    [Fact]
    public void ValidateBinds_RewritesBenignBindToCanonicalForm()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BindTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var canonical = PathCanonicalizer.GetCanonicalPath(tempDir);

            var binds = new List<string> { $"{tempDir}:/workspace:rw" };
            BindValidator.ValidateBinds(binds);
            Assert.Equal($"{canonical}:/workspace:rw", binds[0]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best-effort teardown */ }
        }
    }

    [Fact]
    public void ValidateBinds_NullList_NoOp()
    {
        BindValidator.ValidateBinds(null);
    }

    // udisks2 mounts removable drives under /run/media/<user>/ on Linux; /run around it stays blocked.
    [Fact]
    public void ValidateBinds_ProjectOnRemovableMedia_IsAllowed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The Unix list of blocked host paths does not apply on Windows.");
        }
        var binds = new List<string> { "/run/media/user/USB/project:/workspace" };

        BindValidator.ValidateBinds(binds);

        Assert.StartsWith("/run/media/user/USB/project:", binds[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/run/docker.sock")]
    [InlineData("/run/user/1000/docker.sock")]
    [InlineData("/run/media")]
    [InlineData("/run/mediaextra/project")]
    public void ValidateBinds_RunOutsideRemovableMedia_IsBlocked(string hostPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The Unix list of blocked host paths does not apply on Windows.");
        }
        var binds = new List<string> { $"{hostPath}:/workspace" };

        Assert.Throws<DockerExecutionException>(() => BindValidator.ValidateBinds(binds));
    }

    [Theory]
    [InlineData("/run/media/user/USB/project", true)]
    [InlineData("/run/media/user", true)]
    [InlineData("/run/media/", false)]
    [InlineData("/run/media", false)]
    [InlineData("/run/mediaextra/project", false)]
    [InlineData("/run/user/1000/docker.sock", false)]
    public void IsUnderRemovableMedia_TakesOnlyPathsBelowTheMediaRoot(string canonicalPath, bool expected)
    {
        Assert.Equal(expected, BindValidator.IsUnderRemovableMedia(canonicalPath));
    }
}
