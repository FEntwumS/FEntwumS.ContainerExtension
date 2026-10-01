using System;
using Microsoft.Extensions.Logging;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using static ContainerExtension.Services.Docker.DockerToolConsole;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// The host's own channels for the feedback of a tool run: where OneWare's native strategy writes a run's
/// output and errors itself when the caller passes no handler for them.
/// </summary>
internal interface IHostRunFeedback
{
    /// <summary>Writes one line of tool output to the output window.</summary>
    void WriteOutput(string line);

    /// <summary>Logs one line as an error, which the host also shows in red in the output window.</summary>
    void WriteError(string line);
}

/// <summary>
/// <see cref="IHostRunFeedback"/> backed by OneWare's output window and logger. A service the host does not
/// provide turns the matching call into a no-op, so a strategy built outside a host stays silent.
/// </summary>
internal sealed class OneWareRunFeedback : IHostRunFeedback
{
    private readonly IOutputService? _outputService;
    private readonly ILogger? _logger;

    internal OneWareRunFeedback(IServiceProvider serviceProvider)
    {
        _outputService = serviceProvider.GetService(typeof(IOutputService)) as IOutputService;
        _logger = serviceProvider.GetService(typeof(ILogger)) as ILogger;
    }

    public void WriteOutput(string line)
    {
        if (_outputService is { } outputService)
        {
            SafeInvoke(() => outputService.WriteLine(line));
        }
    }

    public void WriteError(string line)
    {
        _logger?.Error(line);
    }
}

internal static class ToolCommandHandlerDefaults
{
    /// <summary>
    /// Returns <paramref name="command"/> with the host feedback in place of each handler the caller left
    /// unset: tool output goes to the output window and errors to the log, as the native strategy does.
    /// A command that carries both handlers is returned unchanged.
    /// </summary>
    internal static ToolCommand WithDefaultHandlers(this ToolCommand command, IHostRunFeedback feedback)
    {
        if (command.OutputHandler != null && command.ErrorHandler != null)
        {
            return command;
        }

        return new ToolCommand
        {
            ToolName = command.ToolName,
            Executable = command.Executable,
            ExposedPorts = command.ExposedPorts,
            PortMappings = command.PortMappings,
            CommandArguments = command.CommandArguments,
            ForcedStrategyKey = command.ForcedStrategyKey,
            WorkingDirectory = command.WorkingDirectory,
            StatusMessage = command.StatusMessage,
            State = command.State,
            ShowTimer = command.ShowTimer,
            EnvironmentVariables = command.EnvironmentVariables,
            StrategyConfigurationOverrides = command.StrategyConfigurationOverrides,
            OutputHandler = command.OutputHandler ?? (line =>
            {
                feedback.WriteOutput(line);
                return true;
            }),
            ErrorHandler = command.ErrorHandler ?? (line =>
            {
                feedback.WriteError(line);
                return true;
            })
        };
    }
}
