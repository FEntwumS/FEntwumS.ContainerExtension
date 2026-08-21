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
}
