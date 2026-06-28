using System;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for the telemetry redaction pipeline (ContainerTelemetry.ScrubSensitiveInfo). The prior
/// scrubber caught KEY=value secrets and inline URI credentials but missed bare provider tokens, JWTs,
/// and PEM private-key blocks that can appear in a reconstructed command, an error message, or a path.
/// </summary>
public sealed class TelemetryScrubbingTests
{
    // The tokens below are synthetic, fixed test fixtures, not real credentials; the inline markers
    // keep the repository secret-scanner from flagging the deliberately secret-shaped literals.
    [Theory]
    [InlineData("ghp_0123456789abcdefghijABCDEFGHIJ")] // gitleaks:allow
    [InlineData("gho_0123456789abcdefghijABCDEFGHIJ")] // gitleaks:allow
    [InlineData("github_pat_11ABCDEFGHIJ0123456789_abcdefghijklmnop")] // gitleaks:allow
    [InlineData("xoxb-1234567890-abcdefghijklmnop")] // gitleaks:allow
    public void ScrubSensitiveInfo_RedactsProviderTokens(string token)
    {
        var scrubbed = ContainerTelemetry.ScrubSensitiveInfo($"docker run -e API_TOKEN_VALUE {token} image:latest");
        Assert.NotNull(scrubbed);
        Assert.DoesNotContain(token, scrubbed, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_TOKEN]", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubSensitiveInfo_RedactsJwt()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"; // gitleaks:allow
        var scrubbed = ContainerTelemetry.ScrubSensitiveInfo($"failed with Authorization: Bearer {jwt}");
        Assert.NotNull(scrubbed);
        Assert.DoesNotContain(jwt, scrubbed, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_JWT]", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubSensitiveInfo_RedactsPemPrivateKey()
    {
        const string pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIBOwIBAAJBAKj34GkxFhD90vcNLYLInFEX6Ppy1tPf9Cnzj4p4WGeKLs1Pt8Q\n-----END RSA PRIVATE KEY-----"; // gitleaks:allow
        var scrubbed = ContainerTelemetry.ScrubSensitiveInfo(pem);
        Assert.NotNull(scrubbed);
        Assert.DoesNotContain("MIIBOwIBAAJBAKj", scrubbed, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_PRIVATE_KEY]", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrubSensitiveInfo_LeavesBenignTextIntact()
    {
        const string benign = "ghdl -a --std=08 counter.vhd && yosys -p synth_ice40";
        var scrubbed = ContainerTelemetry.ScrubSensitiveInfo(benign);
        Assert.Equal(benign, scrubbed);
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::42")]
    [InlineData("fd00::abcd:1")]
    public void ScrubSensitiveInfo_RedactsCompressedIPv6(string addr)
    {
        var scrubbed = ContainerTelemetry.ScrubSensitiveInfo($"daemon at {addr} unreachable");
        Assert.Contains("[REDACTED_NET_ADDR]", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(addr, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cafe:latest")]
    [InlineData("oss-cad-suite:dev")]
    [InlineData("fentwums/oss-cad-suite:latest")]
    public void ScrubSensitiveInfo_DoesNotRedactImageTags(string imageRef)
    {
        // The compressed-IPv6 scrub must not corrupt image:tag references that share the single-colon
        // shape: the literal '::' requirement in the pattern is what keeps these intact.
        var scrubbed = ContainerTelemetry.ScrubSensitiveInfo($"docker run --rm {imageRef}");
        Assert.Contains(imageRef, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED_NET_ADDR]", scrubbed, StringComparison.Ordinal);
    }
}
