using System;
using ContainerExtension.Validations;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// The image references the settings and the run accept follow Docker's reference grammar, so a reference the daemon
/// would reject fails in the settings, not when the run pulls it.
/// </summary>
public sealed class ImageReferenceGrammarTests
{
    private readonly DockerImageFormatValidation _validator = new();

    [Theory]
    [InlineData("Ubuntu")]
    [InlineData("ghcr.io/Owner/img:tag")]
    [InlineData("repo..x")]
    [InlineData("repo-")]
    [InlineData("repo_")]
    [InlineData("re___po")]
    [InlineData("repo:-tag")]
    [InlineData("repo:.tag")]
    public void Reference_OutsideTheGrammar_IsRejected(string image)
    {
        Assert.False(_validator.Validate(image, out var warning));
        Assert.NotNull(warning);
        Assert.False(DockerImageFormatValidation.IsValidReference(image));
    }

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("hdlc/ghdl:yosys")]
    [InlineData("fentwums/oss-cad-suite:latest")]
    [InlineData("localhost:5000/myimage:tag")]
    [InlineData("Registry.Example.com/ns/repo:Tag_1")]
    [InlineData("a__b/c--d.e")]
    [InlineData("ubuntu@sha256:abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890")]
    public void Reference_InTheGrammar_IsAccepted(string image)
    {
        Assert.True(_validator.Validate(image, out _));
        Assert.True(DockerImageFormatValidation.IsValidReference(image));
    }

    [Fact]
    public void Tag_IsCappedAt128Characters()
    {
        Assert.True(DockerImageFormatValidation.IsValidReference("repo:" + new string('a', 128)));
        Assert.False(DockerImageFormatValidation.IsValidReference("repo:" + new string('a', 129)));
    }

    // Docker counts the path of the name as the daemon normalizes it: without a registry host, and with library/ before
    // a name of one component on Docker Hub. A first component without '.', ':' or upper case is no registry host.
    [Theory]
    [InlineData("registry.example.com:5000/ns/", 252, true)]
    [InlineData("localhost/ns/", 252, true)]
    [InlineData("registry.example.com/ns/", 253, false)]
    [InlineData("ns/", 252, true)]
    [InlineData("ns/", 253, false)]
    [InlineData("", 247, true)]
    [InlineData("", 248, false)]
    [InlineData("docker.io/", 248, false)]
    public void Path_IsCappedAt255Characters_AsTheDaemonCountsIt(string prefix, int length, bool accepted)
    {
        var image = prefix + new string('a', length) + ":tag";

        Assert.Equal(accepted, DockerImageFormatValidation.IsValidReference(image));
        Assert.Equal(accepted, _validator.Validate(image, out var warning));
        if (!accepted)
        {
            Assert.Contains("255", warning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Digest_IsLowerCaseHex()
    {
        const string hex = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";

        Assert.True(DockerImageFormatValidation.IsValidReference("ubuntu@sha256:" + hex));
        Assert.False(DockerImageFormatValidation.IsValidReference("ubuntu@sha256:" + hex.ToUpperInvariant()));
    }
}
