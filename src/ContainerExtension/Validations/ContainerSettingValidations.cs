#pragma warning disable MA0048 // File name must match type name

using System;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using OneWare.Essentials.Models;

namespace ContainerExtension.Validations;

internal sealed class ResourceThresholdValidation(double threshold, double total, string resourceName) : ISettingValidation
{
    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        if (value is not double numericValue || numericValue <= 0 || double.IsNaN(numericValue) || double.IsInfinity(numericValue))
            return true; 

        if (numericValue > total)
        {
            warningMessage = string.Create(CultureInfo.InvariantCulture, $"Value {numericValue:N0} exceeds host {resourceName} capacity ({total:N0}). Use a value between 0 and {total:N0}.");
            return false;
        }

        if (numericValue > threshold)
        {
            var pct = (numericValue / total * 100).ToString("F0", CultureInfo.InvariantCulture);
            warningMessage = $"Warning: Allocating {pct}% of host {resourceName}. Values above 75% may starve the host OS.";
            return true;
        }

        return true;
    }
}

internal sealed partial class DockerImageFormatValidation : ISettingValidation
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._\-]*(:\d{1,5})?(/[a-zA-Z0-9._\-]+)*(:[a-zA-Z0-9._\-]+)?(@sha256:[a-fA-F0-9]{64})?$", RegexOptions.Compiled | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 100)]
    private static partial Regex ImagePatternRegex();

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        var str = value?.ToString();
        if (string.IsNullOrWhiteSpace(str)) return true;

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

internal sealed class DaemonSocketValidation : ISettingValidation
{
    private static readonly string[] ValidSchemes = [ "unix://", "tcp://", "npipe://" ];

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        var str = value?.ToString();
        if (string.IsNullOrWhiteSpace(str)) return true;

        if (ValidSchemes.Any(scheme => str.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)))
            return true;

        warningMessage = "Invalid socket URI. Must start with unix://, tcp://, or npipe://";
        return false;
    }
}

internal sealed partial class ContainerNameValidation : ISettingValidation
{
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._\-]*$", RegexOptions.Compiled | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePatternRegex();

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        var str = value?.ToString();
        if (string.IsNullOrWhiteSpace(str)) return true;

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