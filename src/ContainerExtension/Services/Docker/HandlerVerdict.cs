using System;
using System.Threading;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Records whether an output or error handler rejected a line of the tool's output by returning
/// <see langword="false"/>, which fails the run as it does under OneWare's native strategy. Only the tool's
/// own lines pass through it, not the strategy's diagnostic lines.
/// </summary>
internal sealed class HandlerVerdict
{
    private int _rejected;

    /// <summary>Whether a handler rejected a line. Read it once the posted handler calls have run.</summary>
    internal bool Rejected => Volatile.Read(ref _rejected) != 0;

    /// <summary>Returns a handler that calls <paramref name="handler"/> and records a rejection.</summary>
    internal Func<string, bool>? Track(Func<string, bool>? handler)
    {
        if (handler == null)
        {
            return null;
        }

        return line =>
        {
            var accepted = handler(line);
            if (!accepted)
            {
                Volatile.Write(ref _rejected, 1);
            }
            return accepted;
        };
    }
}
