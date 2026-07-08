using System.Collections.Generic;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for finding D.2: the "unused images" KPI counted images whose Containers field
/// equals zero, but the /images/json list endpoint leaves that field unpopulated (-1), so the count was
/// always zero. The count now uses the tag-based reclaimable test shared with the reclaimable-size metric.
/// </summary>
public sealed class ImageUsageTests
{
    private static ImagesListResponse Image(params string[]? repoTags)
        => new() { RepoTags = repoTags is null ? null : new List<string>(repoTags), Containers = -1 };

    [Fact]
    public void IsUnusedImage_TreatsUntaggedAndDanglingAsUnused()
    {
        Assert.True(DockerImageManager.IsUnusedImage(Image(null)));
        Assert.True(DockerImageManager.IsUnusedImage(Image()));                       // empty RepoTags
        Assert.True(DockerImageManager.IsUnusedImage(Image("<none>:<none>")));
    }

    [Fact]
    public void IsUnusedImage_TreatsTaggedImagesAsUsed()
    {
        Assert.False(DockerImageManager.IsUnusedImage(Image("fentwums/oss-cad-suite:latest")));
        // A real tag alongside a dangling one still counts as used.
        Assert.False(DockerImageManager.IsUnusedImage(Image("repo:tag", "<none>:<none>")));
    }

    [Fact]
    public void IsUnusedImage_DoesNotDependOnContainersField()
    {
        // Containers is -1 (unpopulated by the list endpoint); the tag test must still classify correctly.
        Assert.False(DockerImageManager.IsUnusedImage(Image("repo:tag")));
    }
}
