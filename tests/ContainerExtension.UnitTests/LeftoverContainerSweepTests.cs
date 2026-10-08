using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for the startup sweep of <see cref="ContainerReaper"/>, which removes the stopped containers that
/// an earlier session left behind. The container operations are a recording stand-in, so the tests see what
/// the sweep asks the daemon for and what it removes, without a daemon.
/// </summary>
public sealed class LeftoverContainerSweepTests
{
    [Fact]
    public async Task ReapLeftoverContainers_RemovesTheStoppedContainersWhoseNameStartsWithThePrefix()
    {
        // The daemon's name filter matches anywhere in the name, so it also returns the second container.
        var operations = RecordingContainerOperations.Create(
            Container("a1", "/containerextension-ghdl-120000000-1-abcd1234"),
            Container("b2", "/my-containerextension-db"));

        await ContainerReaper.ReapLeftoverContainersAsync(operations, new MockSettingsService(), TestContext.Current.CancellationToken);

        var recorder = RecordingContainerOperations.Of(operations);
        Assert.Equal(new[] { "a1" }, recorder.Removed);
        var query = Assert.Single(recorder.Queries);
        Assert.True(query.All);
        Assert.Equal(new[] { "containerextension-" }, query.Filters["name"].Keys);
        Assert.Equal(new[] { "created", "dead", "exited" }, query.Filters["status"].Keys.Order(StringComparer.Ordinal));
    }

    private static ContainerListResponse Container(string id, string name)
        => new() { ID = id, Names = [name], Labels = new Dictionary<string, string>(StringComparer.Ordinal) };
}

/// <summary>
/// A stand-in for Docker.DotNet's container operations: it lists a fixed set of containers and records every
/// query and removal; any other call fails the test.
/// </summary>
// DispatchProxy derives the proxy from this type and creates it by reflection, so the type can be neither
// sealed nor created directly.
#pragma warning disable CA1812, CA1852
internal class RecordingContainerOperations : DispatchProxy
{
    private IList<ContainerListResponse> _listed = [];

    public List<ContainersListParameters> Queries { get; } = [];

    public List<string> Removed { get; } = [];

    public static IContainerOperations Create(params ContainerListResponse[] listed)
    {
        var operations = Create<IContainerOperations, RecordingContainerOperations>();
        Of(operations)._listed = listed.ToList();
        return operations;
    }

    public static RecordingContainerOperations Of(IContainerOperations operations) => (RecordingContainerOperations)(object)operations;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(IContainerOperations.ListContainersAsync):
                Queries.Add((ContainersListParameters)args![0]!);
                return Task.FromResult(_listed);
            case nameof(IContainerOperations.RemoveContainerAsync):
                Removed.Add((string)args![0]!);
                return Task.CompletedTask;
            default:
                throw new NotSupportedException($"The sweep is not expected to call {targetMethod?.Name}.");
        }
    }
}
#pragma warning restore CA1812, CA1852
