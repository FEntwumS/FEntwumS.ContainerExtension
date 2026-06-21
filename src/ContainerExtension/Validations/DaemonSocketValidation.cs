using System;
using OneWare.Essentials.Models;

namespace ContainerExtension.Validations;

/// <summary>
/// Validates custom daemon socket URIs for correct scheme prefixes (unix://, tcp://, npipe://)
/// and restricts unsafe characters.
/// </summary>
internal sealed class DaemonSocketValidation : ISettingValidation
{
    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        if (value == null)
        {
            return true;
        }
        var raw = value as string ?? value.ToString();
        if (raw == null || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (raw.Contains("..", StringComparison.Ordinal))
        {
            warningMessage = "Socket path cannot contain directory traversal elements (..).";
            return false;
        }

        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                warningMessage = "Socket URI contains invalid characters (spaces or control characters).";
                return false;
            }
        }

        bool isNamedPipe = raw.StartsWith(@"\\.\", StringComparison.Ordinal) || raw.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase);

        if (isNamedPipe && !OperatingSystem.IsWindows())
        {
            warningMessage = "Windows named pipes are not supported on Unix.";
            return false;
        }

        if (raw.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return true;
        }

        var span = raw.AsSpan();
        bool valid = false;
        if (span.StartsWith("unix://", StringComparison.OrdinalIgnoreCase) && span.Length > 7)
        {
            if (OperatingSystem.IsWindows())
            {
                warningMessage = "Unix domain sockets are not supported on Windows.";
                return false;
            }
            if (raw.Contains('\\'))
            {
                warningMessage = "Unix socket path cannot contain backslashes.";
                return false;
            }
            valid = true;
        }
        else if (span.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) && span.Length > 6) valid = true;
        else if (span.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) && span.Length > 8) valid = true;
        else if (span.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && span.Length > 7) valid = true;
        else if (span.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && span.Length > 8) valid = true;

        if (valid)
        {
            return true;
        }

        warningMessage = "Invalid socket URI. Must start with unix://, tcp://, npipe://, http://, https:// or \\\\.\\";
        return false;
    }
}
