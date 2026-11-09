using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// The container command is checked against the limits that execve(2) applies inside the container, counted in
/// UTF-8 bytes of the final arguments, never against a fixed number of characters. Joins the telemetry collection
/// and isolates the telemetry sink, because building a run may record an error there.
/// </summary>
[Collection("TelemetryTests")]
public sealed class ArgumentLimitTests : IDisposable
{
    // The limits as the kernel defines them on 4 KiB pages; the tests pin the values, not only the names.
    private const int MaxArgumentBytes = 131_072;
    private const int MaxTotalBytes = 1_048_576;

    private readonly string _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_ArgumentLimit", Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "al_" + Guid.NewGuid().ToString("N"));

    public ArgumentLimitTests()
    {
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_telemetryDir))
            {
                Directory.Delete(_telemetryDir, true);
            }
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, true);
            }
        }
        catch { /* best effort */ }
    }

    private CreateContainerParameters Build(IEnumerable<string> arguments, Dictionary<string, string>? environment = null)
    {
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = _workspace,
            CommandArguments = arguments.Select(argument => (ICommandArgument)new TestCommandArgument(argument)).ToList(),
            EnvironmentVariables = environment ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };
        return DockerCommandBuilder.BuildContainerParameters("img:latest", command, null!, null, null, (_, _) => { });
    }

    // A yosys script of the given length in characters, with relative source paths that stay as they are.
    private static string Script(int length)
    {
        var sb = new StringBuilder(length + 32);
        var i = 0;
        while (sb.Length < length)
        {
            sb.Append("read_verilog -sv src/module_").Append(i++).Append(".v; ");
        }
        return sb.ToString(0, length);
    }

    [Fact]
    public void ScriptOf4000Characters_ReachesTheContainerUnchanged()
    {
        var script = Script(4000);

        var parameters = Build(["-p", script]);

        Assert.Equal(["yosys", "-p", script], parameters.Cmd);
    }

    [Fact]
    public void ArgumentAtTheByteLimit_IsAccepted()
    {
        // With its terminating NUL the argument takes exactly MAX_ARG_STRLEN bytes.
        var argument = new string('a', MaxArgumentBytes - 1);

        var parameters = Build([argument]);

        Assert.Equal(argument, parameters.Cmd[1]);
    }

    [Fact]
    public void ArgumentOverTheByteLimit_FailsWithTheLimitAndItsLength()
    {
        var argument = new string('a', MaxArgumentBytes);

        var ex = Assert.Throws<DockerExecutionException>(() => Build(["-p", argument]));

        Assert.Contains("131072", ex.Message, StringComparison.Ordinal);
        Assert.Contains("131073", ex.Message, StringComparison.Ordinal);
        Assert.Contains("-s <file>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultibyteArgumentUnderTheLimitInCharacters_FailsOnItsBytes()
    {
        // 70000 characters of two bytes each: 140001 bytes with the NUL, though far fewer characters than the limit.
        var argument = new string((char)0x00E4, 70_000);
        Assert.True(argument.Length < MaxArgumentBytes);

        var ex = Assert.Throws<DockerExecutionException>(() => Build([argument]));

        Assert.Contains("140001", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizeEnvironmentString_FailsAndNamesOnlyItsKey()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BIG"] = new string('x', MaxArgumentBytes),
        };

        var ex = Assert.Throws<DockerExecutionException>(() => Build(["-q"], environment));

        Assert.Contains("BIG", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("xxxx", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsOverTheTotalLimit_Fail()
    {
        // Eleven arguments of 100000 bytes each: every one is within MAX_ARG_STRLEN, together they exceed 1 MiB.
        var arguments = Enumerable.Range(0, 11).Select(i => new string((char)('a' + i), 100_000)).ToList();

        var ex = Assert.Throws<DockerExecutionException>(() => Build(arguments));

        Assert.Contains(MaxTotalBytes.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentCountsTowardsTheTotalLimit()
    {
        // Ten arguments of 100000 bytes fit; a variable of 100000 bytes pushes the total over 1 MiB.
        var arguments = Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 100_000)).ToList();
        Assert.Equal(11, Build(arguments).Cmd.Count);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PAD"] = new string('y', 100_000),
        };

        Assert.Throws<DockerExecutionException>(() => Build(arguments, environment));
    }
}
