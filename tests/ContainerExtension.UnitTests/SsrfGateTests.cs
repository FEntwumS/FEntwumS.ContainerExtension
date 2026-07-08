using System.Net;
using ContainerExtension.Registry;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for the registry SSRF address gate (finding F.2). Asserts that internal ranges are
/// refused across IPv4, IPv4-mapped, NAT64 (64:ff9b::/96), and deprecated IPv4-compatible (::a.b.c.d)
/// encodings, while genuinely public addresses are allowed.
/// </summary>
public sealed class SsrfGateTests
{
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.5")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]   // cloud metadata endpoint
    [InlineData("100.64.0.1")]        // CGNAT
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]           // IPv6 link-local
    [InlineData("fd00::1")]           // IPv6 unique-local
    [InlineData("::ffff:10.0.0.1")]   // IPv4-mapped
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 -> 169.254.169.254
    [InlineData("64:ff9b::a00:1")]     // NAT64 -> 10.0.0.1
    [InlineData("::a00:1")]            // IPv4-compatible -> 10.0.0.1
    public void IsDisallowedAddress_RejectsInternalRanges(string ip)
    {
        Assert.True(RegistryClient.IsDisallowedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.0.1")]         // just below 172.16/12
    [InlineData("172.32.0.1")]         // just above 172.16/12
    [InlineData("2606:4700:4700::1111")] // public IPv6
    [InlineData("64:ff9b::808:808")]     // NAT64 -> 8.8.8.8 (public, legitimate)
    public void IsDisallowedAddress_AllowsPublicAddresses(string ip)
    {
        Assert.False(RegistryClient.IsDisallowedAddress(IPAddress.Parse(ip)));
    }
}
