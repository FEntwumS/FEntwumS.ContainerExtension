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

    // Docker's reference grammar (distribution/reference, regexp.go): an optional registry host, whose letters may be
    // upper case, with an optional port; path components of lower-case letters and digits joined by '.', '_', "__"
    // or dashes; an optional tag of at most 128 characters that starts with a letter, a digit or '_'; an optional
    // digest, whose hex digits are lower case as go-digest requires. Stricter than Docker in three places, so that a
    // match holds no shell metacharacter and no unusual form: no IPv6 host in brackets, a port of at most five
    // digits, and only a sha256 digest.
    [GeneratedRegex(@"^(?:(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9])(?:\.(?:[a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9]))*(?::[0-9]{1,5})?/)?[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*(?::[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127})?(?:@sha256:[a-f0-9]{64})?$", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ImagePatternRegex();

    // Docker caps the path of a repository name at 255 characters, counted on the name as the daemon normalizes it
    // (distribution/reference, normalize.go): the first component is the registry host only when a path follows it and
    // it is localhost, holds a '.' or a ':', or has an upper-case letter; any other name lies on Docker Hub, and there a
    // name of one component gets the prefix library/. Tag and digest do not count.
    private const int RepositoryPathLengthMax = 255;
    private const string DockerHub = "docker.io";
    private const string LegacyDockerHub = "index.docker.io";
    private const string OfficialRepositoryPrefix = "library/";

    private static bool IsWithinPathLength(string image)
    {
        var at = image.IndexOf('@', StringComparison.Ordinal);
        var name = at < 0 ? image : image[..at];
        var tagColon = name.IndexOf(':', name.LastIndexOf('/') + 1);
        if (tagColon >= 0)
        {
            name = name[..tagColon];
        }

        var host = DockerHub;
        var path = name;
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            var first = name[..slash];
            if (string.Equals(first, "localhost", StringComparison.Ordinal) ||
                first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) ||
                !string.Equals(first, first.ToLowerInvariant(), StringComparison.Ordinal))
            {
                host = string.Equals(first, LegacyDockerHub, StringComparison.Ordinal) ? DockerHub : first;
                path = name[(slash + 1)..];
            }
        }
        if (string.Equals(host, DockerHub, StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal))
        {
            path = OfficialRepositoryPrefix + path;
        }
        return path.Length <= RepositoryPathLengthMax;
    }

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
            var trimmed = image.Trim();
            return ImagePatternRegex().IsMatch(trimmed) && IsWithinPathLength(trimmed);
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
            if (!IsWithinPathLength(str))
            {
                warningMessage = $"Invalid image format. The repository path is longer than the {RepositoryPathLengthMax} characters Docker allows.";
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
