using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using static ContainerExtension.Services.Docker.DockerToolConsole;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Host-native fallback: locates an executable on PATH and runs the tool directly on the host when the
/// container runtime is unavailable and the user has opted into native fallback. Constructed without a
/// daemon connection so it remains usable precisely when the container path cannot be.
/// </summary>
internal sealed class NativeFallbackExecutor
{
    private readonly ISettingsService _settings;
    private readonly DockerToolConsole _console;

    internal NativeFallbackExecutor(ISettingsService settings, DockerToolConsole console)
    {
        _settings = settings;
        _console = console;
    }

    /// <summary>
    /// Searches the host system's environment PATH variable to locate the specified executable.
    /// Supports relative/absolute path checking and handles Windows-specific file extensions.
    /// </summary>
    /// <param name="executable">The file name or path of the executable to search for.</param>
    /// <returns>The resolved absolute path of the executable if found; otherwise, null.</returns>
    internal static string? FindExecutableInPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        if (Path.IsPathRooted(executable) || executable.Contains('/') || executable.Contains('\\'))
        {
            if (File.Exists(executable)) return executable;
            return null;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(OperatingSystem.IsWindows() ? ';' : ':');
        string[] extensions = OperatingSystem.IsWindows() ? ["", ".exe", ".bat", ".cmd", ".com"] : [""];

        foreach (var path in paths)
        {
            var cleanedPath = path.Trim('\"');
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(cleanedPath, executable + ext);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the tool natively on the host when the Docker daemon is unreachable. Captures stdout and stderr,
    /// forwards cancellation to a process kill, and returns the combined output.
    /// </summary>
    /// <param name="command">The tool command payload detailing working directory and arguments.</param>
    /// <param name="resolvedExecutable">The absolute host file path of the executable binary.</param>
    /// <param name="stopwatch">The stopwatch tracking elapsed execution duration.</param>
    /// <param name="ct">The token used to signal operation cancellation.</param>
    /// <returns>A tuple indicating success status and accumulated terminal output.</returns>
    internal async Task<(bool success, string output)> ExecuteNativelyAsync(ToolCommand command, string resolvedExecutable, Stopwatch stopwatch, CancellationToken ct)
    {
        var executableName = Path.GetFileNameWithoutExtension(resolvedExecutable);
        var args = command.Arguments != null ? string.Join(" ", command.Arguments) : string.Empty;
        // Unlike the container path (which rejects a non-absolute working directory because it becomes a
        // bind mount), the native fallback runs the tool as a host process, so the current directory is an
        // acceptable default when no working directory was supplied.
        var workingDir = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? Directory.GetCurrentDirectory() : command.WorkingDirectory;

        _console.SdkLog(command, $"[Docker SDK Fallback] Docker connection failed. Falling back to native execution of '{resolvedExecutable}'...", RankInfo);
        _console.SdkLog(command, $"[Docker SDK Fallback] Native command: {resolvedExecutable} {args}", RankInfo);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = resolvedExecutable,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (command.Arguments != null)
        {
            foreach (var arg in command.Arguments)
            {
                processStartInfo.ArgumentList.Add(arg);
            }
        }

        using var process = new Process { StartInfo = processStartInfo };
        var outputBuilder = new StringBuilder();

        // stdout and stderr fire on separate threadpool threads; StringBuilder is not thread-safe,
        // so guard both appends with the same lock the container path uses.
        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                lock (outputBuilder) { AppendCapped(outputBuilder, e.Data); AppendCapped(outputBuilder, "\n"); }
                SafeInvoke(() => command.OutputHandler?.Invoke(e.Data));
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                lock (outputBuilder) { AppendCapped(outputBuilder, e.Data); AppendCapped(outputBuilder, "\n"); }
                SafeInvoke(() => command.ErrorHandler?.Invoke(e.Data));
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }
                }
                catch
                {
                    // Ignore
                }
            }))
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }

            var success = process.ExitCode == 0;
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            _console.SdkLog(command, $"[Docker SDK Fallback] Native execution finished. Exit code: {process.ExitCode} (ran {elapsedSeconds:F2}s)", RankInfo);

            string finalOutput;
            lock (outputBuilder) { finalOutput = outputBuilder.ToString(); }

            // Mirror the container path's retention semantics: "Unlimited" (and the opted-out
            // "None") map to 0, which disables trimming (maxEntries > 0 gates the trim). A
            // numeric value is the entry cap; anything unparseable falls back to 100.
            var retentionStr = _settings.SafeGetSetting<string>(ContainerExtensionModule.TelemetryRetentionSetting, "25");
            var maxEntries = string.Equals(retentionStr, "Unlimited", StringComparison.Ordinal) ? 0
                : string.Equals(retentionStr, "None", StringComparison.Ordinal) ? 0
                : int.TryParse(retentionStr, out var parsedRetention) ? parsedRetention : 100;

            try
            {
                ContainerTelemetry.LogExecution(
                    image: "native-fallback",
                    tool: executableName,
                    durationSeconds: elapsedSeconds,
                    exitCode: process.ExitCode,
                    imageDigest: "host-native",
                    wasCancelled: ct.IsCancellationRequested,
                    dockerRunCommand: $"[Native] {resolvedExecutable} {args}",
                    maxEntries: maxEntries,
                    errorMessage: success ? null : "Native fallback execution failed."
                );
            }
            catch (Exception telemetryEx) when (telemetryEx is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine($"Telemetry logging failed: {telemetryEx.Message}");
            }

            return (success, finalOutput);
        }
        catch (Exception ex)
        {
            var errMsg = $"[Docker SDK Fallback Error] Native execution failed for '{resolvedExecutable}': {ex.Message}";
            SafeInvoke(() => command.ErrorHandler?.Invoke(errMsg));
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"Native fallback execution failed for '{resolvedExecutable}'", ex);
            return (false, errMsg);
        }
    }
}
