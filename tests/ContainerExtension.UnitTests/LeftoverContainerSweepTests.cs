using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Coverage for the startup sweep of <see cref="ContainerReaper"/>, which removes the stopped containers that
/// an earlier session left behind, and for the label that marks a container as the extension's. In the unit
/// tests the container operations are a recording stand-in, so they see what the sweep asks the daemon for and
/// what it removes; one test runs the sweep against the daemon and is skipped in CI.
/// </summary>
public sealed class LeftoverContainerSweepTests
{
    [Fact]
    public async Task ReapLeftoverContainers_RemovesTheStoppedContainersWhoseNameStartsWithThePrefix()
    {
        // The daemon's name filter matches anywhere in the name, so it also returns the second container.
        var operations = RecordingContainerOperations.Create(
            Container("a1", "/containerextension-ghdl-120000000-1-abcd1234", ownedByTheExtension: true),
            Container("b2", "/my-containerextension-db", ownedByTheExtension: true));

        await ContainerReaper.ReapLeftoverContainersAsync(operations, new MockSettingsService(), TestContext.Current.CancellationToken);

        var recorder = RecordingContainerOperations.Of(operations);
        Assert.Equal(new[] { "a1" }, recorder.Removed);
        var query = Assert.Single(recorder.Queries);
        Assert.True(query.All);
        Assert.Equal(new[] { "containerextension-" }, query.Filters["name"].Keys);
        Assert.Equal(new[] { "created", "dead", "exited" }, query.Filters["status"].Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "io.github.fentwums.container-extension=true" }, query.Filters["label"].Keys);
    }

    [Fact]
    public async Task ReapLeftoverContainers_LeavesAContainerWithoutTheExtensionsLabel()
    {
        // Another tool's container, or one of an earlier version of the extension, under a name with the prefix.
        var operations = RecordingContainerOperations.Create(
            Container("c3", "/containerextension-postgres", ownedByTheExtension: false));

        await ContainerReaper.ReapLeftoverContainersAsync(operations, new MockSettingsService(), TestContext.Current.CancellationToken);

        Assert.Empty(RecordingContainerOperations.Of(operations).Removed);
    }

    [Fact]
    public async Task ReapLeftoverContainers_WithAutoRemoveOff_LeavesEveryContainer()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.AutoRemoveSetting, false);
        var operations = RecordingContainerOperations.Create(
            Container("a1", "/containerextension-ghdl-120000000-1-abcd1234", ownedByTheExtension: true));

        await ContainerReaper.ReapLeftoverContainersAsync(operations, settings, TestContext.Current.CancellationToken);

        var recorder = RecordingContainerOperations.Of(operations);
        Assert.Empty(recorder.Removed);
        Assert.Empty(recorder.Queries);
    }

    [Fact]
    public void BuildContainerParameters_LabelsTheContainerAsTheExtensions_WhateverTheExtraLabelsSay()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, "io.github.fentwums.container-extension=false team=fpga");
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument("-a"), new TestCommandArgument("file.vhd") }
        };

        var parameters = DockerCommandBuilder.BuildContainerParameters("test_image:latest", command, settings, "1000", "1000", (_, _) => { });

        Assert.Equal("true", parameters.Labels["io.github.fentwums.container-extension"]);
        Assert.Equal("fpga", parameters.Labels["team"]);
    }

    [FactIfNoCI]
    public async Task ReapLeftoverContainers_AgainstTheDaemon_RemovesTheExtensionsContainerAndLeavesTheOther()
    {
        // Both containers stay in the state "created", which the sweep covers, and carry a prefix of this test
        // alone, so the sweep cannot touch any other container on the daemon. The extension's container comes
        // from the builder that every run uses; the other one has the prefix but no label.
        var ct = TestContext.Current.CancellationToken;
        var prefix = $"sweeptest-{Guid.NewGuid().ToString("N")[..8]}-";
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ContainerNamePrefixSetting, prefix);
        var connection = await DockerConnectionFactory.CreateAsync(settings, ct);
        using var client = connection.Client ?? throw new InvalidOperationException("No Docker daemon is reachable.");
        using var provider = connection.ConnectionProvider;
        var workDir = Directory.CreateTempSubdirectory("sweeptest").FullName;
        var command = new ToolCommand
        {
            Executable = "echo",
            ToolName = "echo",
            WorkingDirectory = workDir,
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument("left-behind") }
        };
        var created = new List<string>();
        try
        {
            var extensions = await client.Containers.CreateContainerAsync(
                DockerCommandBuilder.BuildContainerParameters("busybox:latest", command, settings, null, null, (_, _) => { }), ct);
            created.Add(extensions.ID);
            var other = await client.Containers.CreateContainerAsync(
                new CreateContainerParameters { Image = "busybox:latest", Name = prefix + "other", Cmd = ["true"] }, ct);
            created.Add(other.ID);

            await ContainerReaper.ReapLeftoverContainersAsync(client.Containers, settings, ct);

            var left = await client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    { "name", new Dictionary<string, bool>(StringComparer.Ordinal) { { prefix, true } } },
                },
            }, ct);
            Assert.Equal(new[] { other.ID }, left.Select(c => c.ID));
        }
        finally
        {
            foreach (var id in created)
            {
                try
                {
                    await client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
                }
                catch (DockerContainerNotFoundException)
                {
                    // The sweep removed it.
                }
            }
            Directory.Delete(workDir, true);
        }
    }

    private static ContainerListResponse Container(string id, string name, bool ownedByTheExtension)
        => new()
        {
            ID = id,
            Names = [name],
            Labels = ownedByTheExtension
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["io.github.fentwums.container-extension"] = "true" }
                : new Dictionary<string, string>(StringComparer.Ordinal),
        };
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
