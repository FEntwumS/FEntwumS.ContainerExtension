using System;
using System.Text.RegularExpressions;
using OneWare.Essentials.Models;

namespace ContainerExtension.Validations;

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
