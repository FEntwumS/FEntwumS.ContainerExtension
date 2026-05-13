#pragma warning disable MA0048 // File name must match type name

using System;
using System.Linq;
using System.Globalization;
using OneWare.Essentials.Models;

namespace ContainerExtension.Validations;

/// <summary>
/// Validates resource limit settings against a threshold percentage of the host's capacity.
/// Shows a warning when the allocated value exceeds 75% of the total system resource,
/// helping users avoid accidentally starving the host OS.
/// </summary>
internal sealed class ResourceThresholdValidation : ISettingValidation
{
    private readonly double _threshold;
    private readonly double _total;
    private readonly string _resourceName;

    public ResourceThresholdValidation(double threshold, double total, string resourceName)
    {
        _threshold = threshold;
        _total = total;
        _resourceName = resourceName;
    }

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        if (value is not double numericValue || numericValue <= 0)
            return true; // 0 = no limit — always valid

        if (numericValue > _total)
        {
            warningMessage = $"Value {numericValue:N0} exceeds host {_resourceName} capacity ({_total:N0}). Use a value between 0 and {_total:N0}.";
            return false;
        }

        if (numericValue > _threshold)
        {
            var pct = (numericValue / _total * 100).ToString("F0", CultureInfo.InvariantCulture);
            warningMessage = $"Warning: Allocating {pct}% of host {_resourceName}. Values above 75% may starve the host OS.";
            return true; // Allow aggressive values — warning is advisory, not blocking
        }

        return true;
    }
}

/// <summary>
/// Validates Docker image references against the standard naming convention.
/// Accepts formats: <c>repo:tag</c>, <c>namespace/repo:tag</c>, <c>registry.io/namespace/repo:tag</c>.
/// Empty values pass validation (the fallback image will be used).
/// </summary>
internal sealed class DockerImageFormatValidation : ISettingValidation
{
    // Match: registry(:port)?/namespace/repo(:tag)?(@sha256:digest)?
    // Supports: repo, repo:tag, ns/repo:tag, host:port/repo:tag, image@sha256:digest
    private static readonly System.Text.RegularExpressions.Regex ImagePattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9._\-]*(:\d{1,5})?(/[a-zA-Z0-9._\-]+)*(:[a-zA-Z0-9._\-]+)?(@sha256:[a-fA-F0-9]{64})?$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.ExplicitCapture);

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        var str = value?.ToString();
        if (string.IsNullOrWhiteSpace(str)) return true; // Empty = use fallback

        if (!ImagePattern.IsMatch(str))
        {
            warningMessage = "Invalid image format. Expected: repo:tag, namespace/repo:tag, or registry.io/ns/repo:tag";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Validates Docker daemon socket URIs against known protocol schemes.
/// Accepts <c>unix://</c>, <c>tcp://</c>, and <c>npipe://</c> prefixes.
/// Empty values pass validation (auto-detection will be used).
/// </summary>
internal sealed class DaemonSocketValidation : ISettingValidation
{
    private static readonly string[] ValidSchemes = { "unix://", "tcp://", "npipe://" };

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        var str = value?.ToString();
        if (string.IsNullOrWhiteSpace(str)) return true; // Empty = auto-detect

        if (ValidSchemes.Any(scheme => str.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)))
            return true;

        warningMessage = "Invalid socket URI. Must start with unix://, tcp://, or npipe://";
        return false;
    }
}

/// <summary>
/// Validates container name prefixes against Docker's naming rules.
/// Accepts only alphanumeric characters, hyphens, underscores, and dots.
/// Empty values pass validation (Docker will assign a random name).
/// </summary>
internal sealed class ContainerNameValidation : ISettingValidation
{
    private static readonly System.Text.RegularExpressions.Regex NamePattern = new(
        @"^[a-zA-Z0-9][a-zA-Z0-9._\-]*$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.ExplicitCapture);

    public bool Validate(object? value, out string? warningMessage)
    {
        warningMessage = null;
        var str = value?.ToString();
        if (string.IsNullOrWhiteSpace(str)) return true; // Empty = Docker random naming

        if (str.Length > 64)
        {
            warningMessage = "Container name prefix is too long (max 64 characters).";
            return false;
        }

        if (!NamePattern.IsMatch(str))
        {
            warningMessage = "Invalid prefix. Use only letters, digits, hyphens (-), underscores (_), and dots (.). Must start with a letter or digit.";
            return false;
        }
        return true;
    }
}
