using System;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Docker.DotNet;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for idempotent disposal: DockerContainerManager.Dispose previously disposed its semaphores
/// unconditionally, faulting in-flight operations (and a second Dispose) with ObjectDisposedException.
/// Dispose is now idempotent and new operations fail fast with a clean ObjectDisposedException.
/// </summary>
public sealed class DockerContainerManagerDisposalTests
{
    // A tcp endpoint constructs a DockerClient without contacting any daemon; the disposal paths under
    // test never dial it.
    private static DockerContainerManager CreateManager()
        => new(new DockerClientConfiguration(new Uri("tcp://localhost:2375")).CreateClient());

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var manager = CreateManager();
        manager.Dispose();
        manager.Dispose(); // second dispose must be a no-op, not an ObjectDisposedException
    }

    [Fact]
    public async Task ListContainersAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var manager = CreateManager();
        manager.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await manager.ListContainersAsync(TestContext.Current.CancellationToken));
    }
}
