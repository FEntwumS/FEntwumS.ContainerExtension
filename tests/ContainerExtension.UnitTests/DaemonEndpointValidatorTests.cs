using System;
using System.IO;
using ContainerExtension.Services.Docker;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for <see cref="DaemonEndpointValidator"/>. Focuses on the absolute-path resolution of
/// system utilities that defeats PATH hijacking; the named-pipe/socket trust checks require a live
/// endpoint and are exercised by the integration and hardening-challenge suites.
/// </summary>
public sealed class DaemonEndpointValidatorTests
{
    [Theory]
    [InlineData("stat")]
    [InlineData("id")]
    [InlineData("open")]
    [InlineData("xdg-open")]
    public void ResolveTrustedUnixBinary_ReturnsRootedAbsolutePath(string name)
    {
        var resolved = DaemonEndpointValidator.ResolveTrustedUnixBinary(name);
        Assert.True(Path.IsPathRooted(resolved), $"'{resolved}' must be absolute, never resolved via PATH");
        Assert.EndsWith("/" + name, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTrustedUnixBinary_PrefersAnExistingTrustedLocation()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return; // POSIX-only: /usr/bin or /bin
        }
        var resolved = DaemonEndpointValidator.ResolveTrustedUnixBinary("stat");
        // stat is a coreutils/BSD staple present on every supported POSIX host.
        Assert.True(File.Exists(resolved), $"expected a real binary at '{resolved}'");
    }
}
