using System;
using System.Text.RegularExpressions;
using OneWare.Essentials.Models;

namespace ContainerExtension.Validations;

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

    /// <summary>
    /// Defense-in-depth guard for call sites that interpolate an image reference into the interactive
    /// terminal. The grammar admits no shell metacharacters, so a reference that matches cannot carry an
    /// injection even if it reached a setting or a registry tag list unvetted.
    /// </summary>
    internal static bool IsValidReference(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return false;
        }
        try
        {
            return ImagePatternRegex().IsMatch(image.Trim());
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

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
