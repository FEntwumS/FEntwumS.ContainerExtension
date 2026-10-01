using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
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

    /// <summary>Logs one line as information and shows it in the output window in the given color.</summary>
    void WriteNotice(string line, IBrush brush);

    /// <summary>
    /// Shows a status entry for the run until it is disposed, with the elapsed time when
    /// <paramref name="showTimer"/> is set; cancelling the entry calls <paramref name="cancel"/>.
    /// </summary>
    Task<IHostRunStatus> BeginStatusAsync(string message, AppState state, bool showTimer, Action cancel);
}

/// <summary>A status entry of a running tool; disposing it removes the entry.</summary>
internal interface IHostRunStatus : IDisposable
{
    /// <summary>Whether the user cancelled the run from its status entry.</summary>
    bool Terminated { get; }
}

/// <summary>
/// <see cref="IHostRunFeedback"/> backed by OneWare's output window and logger. A service the host does not
/// provide turns the matching call into a no-op, so a strategy built outside a host stays silent.
/// </summary>
internal sealed class OneWareRunFeedback : IHostRunFeedback
{
    private readonly IOutputService? _outputService;
    private readonly ILogger? _logger;
    private readonly IApplicationStateService? _applicationStateService;

    internal OneWareRunFeedback(IServiceProvider serviceProvider)
    {
        _outputService = serviceProvider.GetService(typeof(IOutputService)) as IOutputService;
        _logger = serviceProvider.GetService(typeof(ILogger)) as ILogger;
        _applicationStateService = serviceProvider.GetService(typeof(IApplicationStateService)) as IApplicationStateService;
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

    public void WriteNotice(string line, IBrush brush)
    {
        _logger?.Log(line, true, brush);
    }

    public async Task<IHostRunStatus> BeginStatusAsync(string message, AppState state, bool showTimer, Action cancel)
    {
        if (_applicationStateService is not { } stateService)
        {
            return NoRunStatus.Instance;
        }

        // The status bar is bound to the application state, so its entries change on the UI thread.
        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            return new ApplicationStateRunStatus(stateService, message, state, showTimer, cancel);
        }
        return await Dispatcher.UIThread.InvokeAsync(() => (IHostRunStatus)new ApplicationStateRunStatus(stateService, message, state, showTimer, cancel));
    }
}

/// <summary>
/// A status entry in OneWare's status bar, as the native strategy shows one: the status message, followed by
/// the elapsed time as mm:ss when the caller asked for a timer. Create it on the UI thread.
/// </summary>
internal sealed class ApplicationStateRunStatus : IHostRunStatus
{
    private readonly IApplicationStateService _stateService;
    private readonly ApplicationProcess _process;
    private readonly DispatcherTimer? _timer;

    internal ApplicationStateRunStatus(IApplicationStateService stateService, string message, AppState state, bool showTimer, Action cancel)
    {
        _stateService = stateService;
        _process = stateService.AddState(message, state, cancel);
        if (showTimer && Avalonia.Application.Current != null)
        {
            var elapsed = Stopwatch.StartNew();
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Default,
                (_, _) => _process.StatusMessage = RunReport.StatusWithElapsed(message, elapsed.Elapsed));
            _timer.Start();
        }
    }

    public bool Terminated => _process.Terminated;

    public void Dispose()
    {
        SafeInvoke(() =>
        {
            _timer?.Stop();
            _stateService.RemoveState(_process);
        });
    }
}

/// <summary>The status of a run in a strategy built outside a host, where there is no status bar.</summary>
internal sealed class NoRunStatus : IHostRunStatus
{
    internal static readonly NoRunStatus Instance = new();

    public bool Terminated => false;

    public void Dispose()
    {
        // Nothing was shown, so nothing is removed.
    }
}

/// <summary>
/// The lines OneWare's native strategy writes around every foreground run, whatever handlers the caller
/// set: the command line before the run, and a cancellation or a non-zero exit code after it.
/// </summary>
internal static class RunReport
{
    internal static string CommandLine(ToolCommand command)
    {
        var arguments = string.Join(' ', command.Arguments.Select(x => x.Contains(' ') ? $"\"{x}\"" : x));
        return $"{Prefix(command)} {arguments}";
    }

    internal static string Cancelled(ToolCommand command) => $"{Prefix(command)} cancelled!";

    internal static string ExitedWith(ToolCommand command, long exitCode) => $"{Prefix(command)} exited with code {exitCode}";

    internal static string StatusWithElapsed(string status, TimeSpan elapsed) =>
        $"{status} {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";

    private static string Prefix(ToolCommand command) =>
        $"[{Path.GetFileName(command.WorkingDirectory)}]: {Path.GetFileNameWithoutExtension(command.Executable ?? command.ToolName)}";
}

/// <summary>How a foreground run ended, as far as the lines after it need to know.</summary>
internal sealed class RunOutcome
{
    /// <summary>The tool's exit code, once the tool ran to its end.</summary>
    internal long? ExitCode { get; private set; }

    /// <summary>Whether the run was cancelled or timed out.</summary>
    internal bool Cancelled { get; private set; }

    internal void RecordExit(long exitCode) => ExitCode = exitCode;

    internal void RecordCancellation() => Cancelled = true;
}

internal static class ToolCommandHandlerDefaults
{
    /// <summary>
    /// Returns <paramref name="command"/> with the host feedback in place of each handler the caller left
    /// unset, as the native strategy does: tool output goes to the output window, and errors go to the log
    /// and fail the run. A command that carries both handlers is returned unchanged.
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
                return false;
            })
        };
    }
}
