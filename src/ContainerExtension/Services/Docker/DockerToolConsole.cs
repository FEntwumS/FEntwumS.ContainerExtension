using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Threading;
using OneWare.Essentials.ToolEngine;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Forwards container/tool output to the OneWare tool console with per-execution level gating and optional
/// timestamps, and provides the shared output-handling utilities (UI-thread dispatch, capture-size capping,
/// and newline-delimited line draining). One instance is shared by <see cref="DockerExecutionStrategy"/> and
/// the container run loop; the current log level flows via <see cref="AsyncLocal{T}"/>, so a
/// <see cref="BeginScope"/> call on the strategy is observed within the same execution.
/// </summary>
internal sealed class DockerToolConsole
{
    internal const int RankOff = 0, RankErrors = 1, RankInfo = 2, RankVerbose = 3;

    // Hard cap on the in-memory output string returned to the host. The live stream is still forwarded to the
    // tool console in full via the output/error handlers; only the aggregated return value is bounded, so a
    // runaway or hostile container cannot exhaust IDE memory.
    internal const int MaxCapturedOutputChars = 32 * 1024 * 1024;

    private readonly AsyncLocal<int> _currentLogLevelRank = new();
    private readonly AsyncLocal<bool> _currentShowTimestamps = new();

    internal static int LogLevelRank(string level) => level switch
    {
        "Verbose" => RankVerbose,
        "Info" => RankInfo,
        "Errors Only" => RankErrors,
        _ => RankOff
    };

    /// <summary>Configure the log level and timestamp preference for the current execution flow.</summary>
    internal void BeginScope(string logLevel, bool showTimestamps)
    {
        _currentLogLevelRank.Value = LogLevelRank(logLevel);
        _currentShowTimestamps.Value = showTimestamps;
    }

    internal bool IsLogEnabled(int minRank) => _currentLogLevelRank.Value >= minRank;

    /// <summary>The log-level rank in effect for the current execution flow (see the Rank* constants).</summary>
    internal int CurrentLevelRank => _currentLogLevelRank.Value;

    internal void SdkLog(ToolCommand command, string message, int minRank = RankVerbose)
    {
        if (IsLogEnabled(minRank))
        {
            var line = _currentShowTimestamps.Value
                ? string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:HH:mm:ss.fff}] {message}")
                : message;
            SafeInvoke(() => { (command.OutputHandler ?? command.ErrorHandler)?.Invoke(line); });
        }
    }

    internal static void SafeInvoke(Action action)
    {
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
        else
        {
            action();
        }
    }

    // Appends to the captured-output buffer up to the cap, then stops after a one-time marker.
    // The caller must hold the lock on <paramref name="sb"/>.
    internal static void AppendCapped(StringBuilder sb, ReadOnlySpan<char> text)
    {
        if (sb.Length >= MaxCapturedOutputChars) return;
        var remaining = MaxCapturedOutputChars - sb.Length;
        if (text.Length <= remaining)
        {
            sb.Append(text);
        }
        else
        {
            sb.Append(text[..remaining]);
            sb.Append("\n[output truncated: capture limit reached; full output was streamed to the tool console]\n");
        }
    }

    internal static void DrainLines(StringBuilder buffer, ReadOnlySpan<char> textSpan, Func<string, bool>? handler)
    {
        if (textSpan.IsEmpty)
        {
            return;
        }

        string[]? batchArray = null;
        int batchCount = 0;

        void AddLine(string line)
        {
            if (handler != null)
            {
                if (batchArray == null)
                {
                    batchArray = System.Buffers.ArrayPool<string>.Shared.Rent(16);
                }
                if (batchCount >= batchArray.Length)
                {
                    var newArray = System.Buffers.ArrayPool<string>.Shared.Rent(batchArray.Length * 2);
                    Array.Copy(batchArray, newArray, batchCount);
                    System.Buffers.ArrayPool<string>.Shared.Return(batchArray);
                    batchArray = newArray;
                }
                batchArray[batchCount++] = line;
            }
        }

        int start = 0;
        while (start < textSpan.Length)
        {
            int newlineIdx = textSpan[start..].IndexOf('\n');
            if (newlineIdx < 0)
            {
                break;
            }

            int lineEndRelative = newlineIdx;
            int absoluteLineEnd = start + lineEndRelative;

            int lineEndTrimmed = absoluteLineEnd;
            if (lineEndTrimmed > start && textSpan[lineEndTrimmed - 1] == '\r')
            {
                lineEndTrimmed--;
            }

            string completedLine;
            if (buffer.Length > 0)
            {
                buffer.Append(textSpan[start..lineEndTrimmed]);
                completedLine = buffer.ToString();
                buffer.Clear();
            }
            else
            {
                completedLine = textSpan[start..lineEndTrimmed].ToString();
            }

            AddLine(completedLine);
            start = absoluteLineEnd + 1;
        }

        if (start < textSpan.Length)
        {
            buffer.Append(textSpan[start..]);
        }

        if (batchCount > 0 && batchArray != null)
        {
            var finalCount = batchCount;
            var finalArray = batchArray;
            SafeInvoke(() =>
            {
                try
                {
                    for (int idx = 0; idx < finalCount; idx++)
                    {
                        try
                        {
                            handler!(finalArray[idx]);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            ContainerTelemetry.TrackError("DockerExecutionStrategy", "DrainLines callback handler failed", ex);
                        }
                    }
                }
                finally
                {
                    for (int idx = 0; idx < finalCount; idx++)
                    {
                        finalArray[idx] = null!;
                    }
                    System.Buffers.ArrayPool<string>.Shared.Return(finalArray);
                }
            });
        }

        // Defensive OOM Shield: If a container goes rogue and outputs endless text
        // without newlines, prevent the StringBuilder from crashing the host IDE.
        if (buffer.Length > 8 * 1024 * 1024) // 8 MB limit
        {
            buffer.Clear();
            ContainerTelemetry.TrackError("DockerExecutionStrategy", "OOM Protection triggered: buffer exceeded 8MB threshold without newlines", null);
        }
    }
}
