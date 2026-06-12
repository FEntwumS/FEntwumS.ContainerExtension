#pragma warning disable MA0048 // File name must match type name

using System;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using OneWare.Essentials.Models;

namespace ContainerExtension.Validations;

internal enum ResourceKind
{
    Memory,
    Cpu,
    Custom
}

/// <summary>
/// Validates numeric resource thresholds (Memory/CPU) ensuring they are positive, finite, 
/// and within safe operational limits relative to host capacity.
/// </summary>
internal sealed class ResourceThresholdValidation : ISettingValidation
{
    private readonly double _threshold;
    private readonly double _total;
    private readonly string _resourceName;
    private readonly ResourceKind _resourceKind;

    public ResourceThresholdValidation(double threshold, double total, string resourceName)
    {
        _resourceName = resourceName ?? throw new ArgumentNullException(nameof(resourceName));
        _resourceKind = _resourceName.Equals("memory", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Memory :
                        _resourceName.Equals("CPU", StringComparison.OrdinalIgnoreCase) ? ResourceKind.Cpu :
                        ResourceKind.Custom;

        if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total))
        {
            total = _resourceKind == ResourceKind.Memory ? 4096.0 :
                    _resourceKind == ResourceKind.Cpu ? 4.0 : 1.0;
        }
        if (threshold <= 0 || double.IsNaN(threshold) || double.IsInfinity(threshold))
        {
            threshold = total * 0.75;
        }

        _threshold = threshold;
        _total = total;
    }

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        double numericValue;

        if (value is string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return true;
            }
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                numericValue = parsed;
            }
            else
            {
                warningMessage = $"Value for {_resourceName} must be a valid number.";
                return false;
            }
        }
        else
        {
            switch (value)
            {
                case double d:
                    numericValue = d;
                    break;
                case float f:
                    numericValue = f;
                    break;
                case int i:
                    numericValue = i;
                    break;
                case long l:
                    numericValue = l;
                    break;
                case ulong ul:
                    numericValue = ul;
                    break;
                case decimal dec:
                    numericValue = (double)dec;
                    break;
                case uint ui:
                    numericValue = ui;
                    break;
                case ushort us:
                    numericValue = us;
                    break;
                case short sh:
                    numericValue = sh;
                    break;
                case byte b:
                    numericValue = b;
                    break;
                case sbyte sb:
                    numericValue = sb;
                    break;
                default:
                    return true;
            }
        }

        if (double.IsNaN(numericValue) || double.IsInfinity(numericValue))
        {
            warningMessage = $"Value for {_resourceName} must be a valid number.";
            return false;
        }

        if (numericValue < 0)
        {
            warningMessage = $"Value for {_resourceName} cannot be negative.";
            return false;
        }

        if (numericValue > 9_000_000_000_000_000.0)
        {
            warningMessage = $"Value for {_resourceName} exceeds safe integer boundaries.";
            return false;
        }

        if (numericValue == 0)
        {
            return true;
        }

        if (_resourceKind == ResourceKind.Memory && numericValue < 512.0)
        {
            warningMessage = "Memory limit must be at least 512 MB (or 0 for unlimited).";
            return false;
        }

        if (_resourceKind == ResourceKind.Cpu && (numericValue < 0.1 || numericValue > Math.Max(32.0, _total)))
        {
            var maxLimit = Math.Max(32.0, _total);
            warningMessage = string.Create(CultureInfo.InvariantCulture, $"CPU cores limit must be between 0.1 and {maxLimit.ToString("F1", CultureInfo.InvariantCulture)} (or 0 for unlimited).");
            return false;
        }

        if (numericValue > _total)
        {
            var format = _resourceKind == ResourceKind.Cpu ? "F1" : "N0";
            warningMessage = string.Create(CultureInfo.InvariantCulture, $"Value {numericValue.ToString(format, CultureInfo.InvariantCulture)} exceeds host {_resourceName} capacity ({_total.ToString(format, CultureInfo.InvariantCulture)}). Use a value between 0 and {_total.ToString(format, CultureInfo.InvariantCulture)}.");
            return false;
        }

        if (numericValue > _threshold)
        {
            warningMessage = string.Create(CultureInfo.InvariantCulture, $"Warning: Allocating {(numericValue / _total * 100):F0}% of host {_resourceName}. Values above 75% may starve the host OS.");
            return true;
        }

        return true;
    }
}

/// <summary>
/// Validates Docker image strings against standard repository/tag formats.
/// Supports optional allowances for empty strings depending on the setting requirement.
/// </summary>
internal sealed partial class DockerImageFormatValidation : ISettingValidation
{
    private readonly bool _allowEmpty;

    public DockerImageFormatValidation(bool allowEmpty = true)
    {
        _allowEmpty = allowEmpty;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._\-]*(:\d{1,5})?(/[a-zA-Z0-9._\-]+)*(:[a-zA-Z0-9._\-]+)?(@sha256:[a-fA-F0-9]{64})?$", RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ImagePatternRegex();

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        if (value == null)
        {
            if (_allowEmpty) return true;
            warningMessage = "Image format cannot be empty.";
            return false;
        }
        var str = value as string ?? value.ToString();
        if (str == null || string.IsNullOrWhiteSpace(str))
        {
            if (_allowEmpty)
            {
                return true;
            }
            warningMessage = "Image format cannot be empty.";
            return false;
        }

        str = str.Trim();

        try
        {
            if (!ImagePatternRegex().IsMatch(str))
            {
                warningMessage = "Invalid image format. Expected: repo:tag, namespace/repo:tag, or registry.io/ns/repo:tag";
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            warningMessage = "Image format validation timed out.";
            return false;
        }

        return true;
    }
}

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

/// <summary>
/// Validates container name prefixes to ensure compliance with Docker's naming conventions 
/// (letters, digits, hyphens, underscores) and length limits.
/// </summary>
internal sealed partial class ContainerNameValidation : ISettingValidation
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._\-]*$", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePatternRegex();

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        if (value == null) return true;
        var str = value as string ?? value.ToString();
        if (str == null || string.IsNullOrWhiteSpace(str)) return true;

        str = str.Trim();
        if (!System.Text.Ascii.IsValid(str))
        {
            warningMessage = "Container name prefix must contain ASCII characters only.";
            return false;
        }

        if (str.Length > 64)
        {
            warningMessage = "Container name prefix is too long (max 64 characters).";
            return false;
        }

        try
        {
            if (!NamePatternRegex().IsMatch(str))
            {
                warningMessage = "Invalid prefix. Use only letters, digits, hyphens (-), underscores (_), and dots (.). Must start with a letter or digit.";
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            warningMessage = "Container name validation timed out.";
            return false;
        }

        return true;
    }
}
