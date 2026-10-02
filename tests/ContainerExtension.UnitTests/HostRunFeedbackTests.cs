using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Media;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
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

public sealed class RunReportTests
{
    private static ToolCommand Command(string executable, string workingDirectory, params string[] arguments) => new()
    {
        ToolName = Path.GetFileNameWithoutExtension(executable),
        Executable = executable,
        WorkingDirectory = workingDirectory,
        CommandArguments = arguments.Select(a => (ICommandArgument)new TestCommandArgument(a)).ToList()
    };

    [Fact]
    public void CommandLine_ReadsAsTheNativeStrategyWritesIt()
    {
        var command = Command("iverilog", "/home/user/VerilogBlink", "-o", "build/blink tb.vvp", "-g2012");

        Assert.Equal("[VerilogBlink]: iverilog -o \"build/blink tb.vvp\" -g2012", RunReport.CommandLine(command));
    }

    [Fact]
    public void CommandLine_NamesAnExecutablePathByItsFileName()
    {
        var command = Command("/opt/oss-cad-suite/bin/vvp", "/home/user/VerilogBlink", "Verilog_Blink_tb.vvp");

        Assert.Equal("[VerilogBlink]: vvp Verilog_Blink_tb.vvp", RunReport.CommandLine(command));
    }

    [Fact]
    public void CancelledAndExitedWith_ReadAsTheNativeStrategyWritesThem()
    {
        var command = Command("vvp", "/home/user/VerilogBlink", "Verilog_Blink_tb.vvp");

        Assert.Equal("[VerilogBlink]: vvp cancelled!", RunReport.Cancelled(command));
        Assert.Equal("[VerilogBlink]: vvp exited with code 2", RunReport.ExitedWith(command, 2));
    }

    [Fact]
    public void StatusWithElapsed_AppendsMinutesAndSeconds()
    {
        Assert.Equal("Running IVerilog.. 00:07", RunReport.StatusWithElapsed("Running IVerilog..", TimeSpan.FromSeconds(7)));
        Assert.Equal("Running IVerilog.. 01:15", RunReport.StatusWithElapsed("Running IVerilog..", TimeSpan.FromSeconds(75)));
        Assert.Equal("Running IVerilog.. 125:00", RunReport.StatusWithElapsed("Running IVerilog..", TimeSpan.FromMinutes(125)));
    }
}

public sealed class ApplicationStateRunStatusTests
{
    [Fact]
    public void Status_AddsAnEntryThatCancelsTheRunAndRemovesItOnDispose()
    {
        var stateService = new RecordingApplicationStateService();
        var cancelled = false;

        var status = new ApplicationStateRunStatus(stateService, "Running IVerilog..", AppState.Loading, showTimer: true, () => cancelled = true);

        var entry = Assert.Single(stateService.Added);
        Assert.Equal("Running IVerilog..", entry.StatusMessage);
        Assert.Equal(AppState.Loading, entry.State);
        Assert.False(status.Terminated);

        // What OneWare's status bar does when the user cancels the run.
        entry.Terminated = true;
        entry.Terminate!();
        Assert.True(cancelled);
        Assert.True(status.Terminated);

        status.Dispose();
        Assert.Same(entry, Assert.Single(stateService.Removed));
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
    public ConcurrentQueue<(string Line, Color? Color)> Notices { get; } = new();

    public void WriteOutput(string line) => Output.Enqueue(line);

    public void WriteError(string line) => Errors.Enqueue(line);

    public void WriteNotice(string line, IBrush brush) => Notices.Enqueue((line, (brush as ISolidColorBrush)?.Color));

    public ConcurrentQueue<RecordingRunStatus> Statuses { get; } = new();

    /// <summary>Completes with the first status entry a run shows.</summary>
    public Task<RecordingRunStatus> FirstStatus => _firstStatus.Task;

    /// <summary>Hands out status entries the user has already cancelled, as if the click came as the run ended.</summary>
    public bool StatusesStartTerminated { get; init; }

    private readonly TaskCompletionSource<RecordingRunStatus> _firstStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IHostRunStatus> BeginStatusAsync(string message, AppState state, bool showTimer, Action cancel)
    {
        var status = new RecordingRunStatus(message, state, showTimer, cancel) { Terminated = StatusesStartTerminated };
        Statuses.Enqueue(status);
        _firstStatus.TrySetResult(status);
        return Task.FromResult<IHostRunStatus>(status);
    }
}

/// <summary>A status entry as <see cref="RecordingRunFeedback"/> hands it out.</summary>
internal sealed class RecordingRunStatus(string message, AppState state, bool showTimer, Action cancel) : IHostRunStatus
{
    public string Message { get; } = message;
    public AppState State { get; } = state;
    public bool ShowTimer { get; } = showTimer;
    public bool Terminated { get; set; }
    public bool Disposed { get; private set; }

    /// <summary>What OneWare's status bar does when the user cancels a run.</summary>
    public void CancelFromStatusBar()
    {
        Terminated = true;
        cancel();
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Records the status entries added and removed, in place of OneWare's application state.</summary>
internal sealed class RecordingApplicationStateService : IApplicationStateService
{
    public List<ApplicationProcess> Added { get; } = [];
    public List<ApplicationProcess> Removed { get; } = [];

    public ApplicationProcess AddState(string status, AppState state, Action? terminate = null)
    {
        var process = new ApplicationProcess { StatusMessage = status, State = state, Terminate = terminate };
        Added.Add(process);
        return process;
    }

    public void RemoveState(ApplicationProcess key, string finishMessage = "Done") => Removed.Add(key);

    public bool ShutdownComplete => false;
    public ObservableCollection<ApplicationNotification> CurrentNotifications { get; } = new();
    public ApplicationProcess ActiveProcess { get; } = new();
    public Task TerminateActiveDialogAsync() => throw new NotSupportedException();
    public void RegisterAutoLaunchAction(Action<string?> action) => throw new NotSupportedException();
    public void RegisterPathLaunchAction(Action<string?> action) => throw new NotSupportedException();
    public void RegisterUrlLaunchAction(string key, Action<string?> action) => throw new NotSupportedException();
    public void RegisterShutdownAction(Action action) => throw new NotSupportedException();
    public void RegisterShutdownTask(Func<Task<bool>> task) => throw new NotSupportedException();
    public void ExecuteAutoLaunchActions(string? value) => throw new NotSupportedException();
    public void ExecutePathLaunchActions(string? value) => throw new NotSupportedException();
    public void ExecuteUrlLaunchActions(Uri uri) => throw new NotSupportedException();
    public Task<bool> TryShutdownAsync() => throw new NotSupportedException();
    public Task<bool> TryRestartAsync() => throw new NotSupportedException();
    public void AddNotification(ApplicationNotification notification) => throw new NotSupportedException();
    public void ClearNotifications() => throw new NotSupportedException();
}
