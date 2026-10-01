using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.Enums;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

public sealed class HostRunFeedbackTests
{
    [Fact]
    public void WithDefaultHandlers_RoutesUnsetHandlersToTheHost()
    {
        var feedback = new RecordingRunFeedback();
        var command = new ToolCommand { ToolName = "vvp", CommandArguments = new List<ICommandArgument>() };

        var effective = command.WithDefaultHandlers(feedback);

        Assert.True(effective.OutputHandler!("to-stdout"));
        // An error line rejects the run, as it does under the native strategy without an error handler.
        Assert.False(effective.ErrorHandler!("to-stderr"));
        Assert.Equal(["to-stdout"], feedback.Output);
        Assert.Equal(["to-stderr"], feedback.Errors);
    }

    [Fact]
    public void WithDefaultHandlers_KeepsTheHandlerTheCallerSet()
    {
        var feedback = new RecordingRunFeedback();
        var seen = new List<string>();
        var command = new ToolCommand
        {
            ToolName = "yosys",
            CommandArguments = new List<ICommandArgument>(),
            OutputHandler = line =>
            {
                seen.Add(line);
                return true;
            }
        };

        var effective = command.WithDefaultHandlers(feedback);
        effective.OutputHandler!("to-stdout");
        effective.ErrorHandler!("to-stderr");

        Assert.Equal(["to-stdout"], seen);
        Assert.Empty(feedback.Output);
        Assert.Equal(["to-stderr"], feedback.Errors);
    }

    [Fact]
    public void WithDefaultHandlers_ReturnsACommandWithBothHandlersAsItIs()
    {
        var command = new ToolCommand
        {
            ToolName = "yosys",
            CommandArguments = new List<ICommandArgument>(),
            OutputHandler = _ => true,
            ErrorHandler = _ => true
        };

        Assert.Same(command, command.WithDefaultHandlers(new RecordingRunFeedback()));
    }

    [Fact]
    public void WithDefaultHandlers_CopiesEverySettableProperty()
    {
        // The copy has to carry every settable property of ToolCommand. One that a newer OneWare.Essentials
        // adds fails the first assertion until WithDefaultHandlers copies it as well.
        string[] copied =
        [
            nameof(ToolCommand.ToolName), nameof(ToolCommand.Executable), nameof(ToolCommand.ExposedPorts),
            nameof(ToolCommand.PortMappings), nameof(ToolCommand.CommandArguments), nameof(ToolCommand.ForcedStrategyKey),
            nameof(ToolCommand.WorkingDirectory), nameof(ToolCommand.StatusMessage), nameof(ToolCommand.State),
            nameof(ToolCommand.ShowTimer), nameof(ToolCommand.EnvironmentVariables),
            nameof(ToolCommand.StrategyConfigurationOverrides), nameof(ToolCommand.OutputHandler),
            nameof(ToolCommand.ErrorHandler)
        ];
        var settable = typeof(ToolCommand).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name);
        Assert.Equal(copied.Order(StringComparer.Ordinal), settable.Order(StringComparer.Ordinal));

        var command = new ToolCommand
        {
            ToolName = "tool",
            Executable = "/opt/bin/tool",
            ExposedPorts = [new ToolPort(8080)],
            PortMappings = [new ToolPortMapping(new ToolPort(1), new ToolPort(2))],
            CommandArguments = new List<ICommandArgument>(),
            ForcedStrategyKey = "DockerExecutionStrategy",
            WorkingDirectory = "/work",
            StatusMessage = "Running tool...",
            State = AppState.Idle,
            ShowTimer = true,
            EnvironmentVariables = new Dictionary<string, string> { ["A"] = "1" },
            StrategyConfigurationOverrides = new Dictionary<string, string> { ["docker.image"] = "img" }
        };

        var effective = command.WithDefaultHandlers(new RecordingRunFeedback());

        foreach (var name in copied.Where(n => n is not nameof(ToolCommand.OutputHandler) and not nameof(ToolCommand.ErrorHandler)))
        {
            var property = typeof(ToolCommand).GetProperty(name)!;
            Assert.Equal(property.GetValue(command), property.GetValue(effective));
        }
    }
}

/// <summary>
/// Records what a run hands to the host, in place of OneWare's output window and logger. Thread-safe, since
/// a container's stdout and stderr are read on separate threads.
/// </summary>
internal sealed class RecordingRunFeedback : IHostRunFeedback
{
    public ConcurrentQueue<string> Output { get; } = new();
    public ConcurrentQueue<string> Errors { get; } = new();

    public void WriteOutput(string line) => Output.Enqueue(line);

    public void WriteError(string line) => Errors.Enqueue(line);
}
