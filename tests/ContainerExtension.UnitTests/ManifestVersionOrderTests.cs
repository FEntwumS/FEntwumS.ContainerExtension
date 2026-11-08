using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// The version list in oneware-extension.json runs oldest first and newest last, ordered by SemVer 2.0.0 precedence:
/// OneWare Studio installs the last entry when no version is chosen, and the packages in its package list order
/// their versions the same way.
/// </summary>
public sealed class ManifestVersionOrderTests
{
    private const string ManifestName = "oneware-extension.json";
    private const string SolutionName = "OneWare.ContainerExtension.slnx";

    private static readonly Regex SemVer = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)" +
        @"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Fact]
    public void ManifestVersions_AscendStrictlyBySemVerPrecedence()
    {
        var versions = ReadManifestVersions();

        Assert.NotEmpty(versions);
        for (var i = 1; i < versions.Length; i++)
        {
            Assert.True(CompareSemVer(versions[i - 1], versions[i]) < 0,
                $"{ManifestName} lists '{versions[i - 1]}' before '{versions[i]}'; " +
                "the versions must ascend, oldest first and newest last.");
        }
    }

    // The comparison the manifest test relies on, with the examples of the SemVer 2.0.0 specification.
    [Theory]
    [InlineData("1.0.9", "1.0.10")]
    [InlineData("1.0.14", "1.1.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.2.1", "2.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void CompareSemVer_OrdersByPrecedence(string lower, string higher)
    {
        Assert.True(CompareSemVer(lower, higher) < 0);
        Assert.True(CompareSemVer(higher, lower) > 0);
        Assert.Equal(0, CompareSemVer(lower, lower));
        Assert.Equal(0, CompareSemVer(lower + "+build.5", lower));
    }

    private static string[] ReadManifestVersions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), ManifestName)));
        return document.RootElement.GetProperty("versions").EnumerateArray()
            .Select(entry => entry.GetProperty("version").GetString()
                ?? throw new InvalidDataException("A version entry has no version."))
            .ToArray();
    }

    // The repository root is the folder that holds the solution, found by walking up from the test assembly, as the
    // end-to-end tests find tests/integration.
    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionName)))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException($"No folder above '{AppContext.BaseDirectory}' holds {SolutionName}.");
    }

    private static int CompareSemVer(string left, string right)
    {
        var a = ParseSemVer(left);
        var b = ParseSemVer(right);
        for (var i = 0; i < 3; i++)
        {
            var core = a.Core[i].CompareTo(b.Core[i]);
            if (core != 0)
            {
                return core;
            }
        }
        // A release ranks above any pre-release of the same version.
        if (a.PreRelease.Length == 0 || b.PreRelease.Length == 0)
        {
            return (a.PreRelease.Length == 0).CompareTo(b.PreRelease.Length == 0);
        }
        for (var i = 0; i < Math.Min(a.PreRelease.Length, b.PreRelease.Length); i++)
        {
            var identifier = CompareIdentifier(a.PreRelease[i], b.PreRelease[i]);
            if (identifier != 0)
            {
                return identifier;
            }
        }
        return a.PreRelease.Length.CompareTo(b.PreRelease.Length);
    }

    // Numeric identifiers compare as numbers and rank below alphanumeric ones, which compare in ASCII order. A numeric
    // identifier has no leading zero, so the longer one is the larger.
    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            return left.Length != right.Length
                ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(left, right);
        }
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }
        return string.CompareOrdinal(left, right);
    }

    private static (long[] Core, string[] PreRelease) ParseSemVer(string version)
    {
        var match = SemVer.Match(version);
        Assert.True(match.Success, $"'{version}' is not a SemVer 2.0.0 version.");
        var core = new[] { match.Groups[1], match.Groups[2], match.Groups[3] }
            .Select(group => long.Parse(group.Value, CultureInfo.InvariantCulture))
            .ToArray();
        var preRelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        return (core, preRelease);
    }
}
