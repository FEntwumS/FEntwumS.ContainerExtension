using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ContainerExtension;
using ContainerExtension.Validations;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.Models;
using OneWare.Essentials.Services;
using OneWare.Essentials.ToolEngine;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ContainerExtension.UnitTests;

/// <summary>
/// Unit tests for the OneWare Container Extension.
/// Validates setting constants and the real validation classes used by the extension,
/// leveraging InternalsVisibleTo to test the actual validators directly.
/// </summary>
[Collection("TelemetryTests")]
public sealed class ContainerExtensionTests : IDisposable
{
    private readonly DockerImageFormatValidation _imageValidator = new();
    private readonly DaemonSocketValidation _socketValidator = new();
    private readonly ContainerNameValidation _nameValidator = new();
    private readonly string _testTelemetryDir;

    // Builds a host-native absolute path from a POSIX-style relative spec so a single assertion
    // covers both platforms: the mapped container path is always POSIX regardless of host format.
    // Unix -> "/work/proj"; Windows -> "C:\work\proj".
    private static string HostAbs(string posixRelative) =>
        OperatingSystem.IsWindows()
            ? @"C:\" + posixRelative.Replace('/', '\\')
            : "/" + posixRelative;

    public ContainerExtensionTests()
    {
        // Isolate telemetry to a temporary directory strictly for this test lifecycle
        _testTelemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_testTelemetryDir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
    }

    public void Dispose()
    {
        // Clean up test environment physical files
        try
        {
            ContainerTelemetry.LogLevelChecker = () => "Verbose";
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_testTelemetryDir))
            {
                Directory.Delete(_testTelemetryDir, true);
            }
        }
#pragma warning disable CA1031
        catch { /* Best effort teardown */ }
#pragma warning restore CA1031
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void LoadLibrary()
    {
        var assembly = typeof(ContainerExtensionModule).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("ContainerExtension", assembly.GetName().Name, StringComparison.Ordinal);
    }










    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("short", "short")]
    [InlineData("123456789012", "123456789012")]
    [InlineData("123456789012345", "123456789012")] // Safely clips at 12
    public void ShortId_HandlesAllStringBoundsGracefully(string? input, string? expected)
    {
        var result = input.ShortId();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShortId_WhitespaceOnly_ReturnsInputVerbatim()
    {
        var result = "   ".ShortId();
        Assert.Equal("   ", result); // Preserves exact string structure up to 12
    }

    [Theory]
    [InlineData("hdlc/ghdl:yosys", true)]
    [InlineData("ubuntu", true)]
    [InlineData("ubuntu:latest", true)]
    [InlineData("ghcr.io/fentwums/container:v1.0", true)]
    [InlineData("registry.example.com/ns/repo:tag", true)]
    [InlineData("localhost:5000/myimage:tag", true)]                // Registry with port
    [InlineData("myregistry:443/ns/repo:latest", true)]            // Registry with port + namespace
    [InlineData("ubuntu@sha256:abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890", true)] // Digest reference
    [InlineData("", true)]       // Empty = use fallback, always valid
    [InlineData("   ", true)]    // Whitespace only = use fallback
    [InlineData("INVALID IMAGE!", false)]
    [InlineData("image with spaces", false)]
    [InlineData("@invalid", false)]
    [InlineData("a.registry.with.a.very.very.very.long.domain.name.example.com:5000/and/an/extremely/long/path/with/many/many/many/components/repo:very_long_tag_name_that_tests_limits", true)] // Extreme length
    public void DockerImageFormat_ValidatesCorrectly(string input, bool expectedValid)
    {
        var result = _imageValidator.Validate(input, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
            Assert.NotNull(warning);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("tcp://127.0.0.1:2375", true)]
    [InlineData("npipe://./pipe/docker_engine", true)]
    [InlineData("http://localhost:2375", true)]
    [InlineData("https://localhost:2375", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("just-a-path", false)]
    public void DaemonSocketFormat_ValidatesCorrectly(string input, bool expectedValid)
    {
        if (expectedValid)
        {
            bool isNamedPipe = input.StartsWith(@"\\.\", StringComparison.Ordinal) || input.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase);
            bool isUnixSocket = input.StartsWith("unix://", StringComparison.OrdinalIgnoreCase);
            if (isNamedPipe && !OperatingSystem.IsWindows()) expectedValid = false;
            if (isUnixSocket && OperatingSystem.IsWindows()) expectedValid = false;
        }

        var result = _socketValidator.Validate(input, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
            Assert.NotNull(warning);
    }

    [Theory]
    [InlineData(0.0, true, false)]       // 0 = no limit, always valid, no warning
    [InlineData(2048.0, true, false)]    // Below 75% of 16384 - valid, no warning
    [InlineData(14000.0, true, true)]    // Above 75% of 16384 -> valid with advisory warning
    [InlineData(16384.0, true, true)]    // 100% of total -> valid with advisory warning
    [InlineData(20000.0, false, true)]   // Above total -> rejected with error
    [InlineData(-100.0, false, true)]    // Negative limits -> rejected with error
    public void ResourceThreshold_WarnsAbove75Percent(double value, bool expectedValid, bool expectWarning)
    {
        // Uses the real validator with 75% threshold of 16384 MB total
        var validator = new ResourceThresholdValidation(16384.0 * 0.75, 16384.0, "RAM (MB)");
        var result = validator.Validate(value, out var warning);
        Assert.Equal(expectedValid, result);
        if (expectWarning)
            Assert.NotNull(warning);
        else
            Assert.Null(warning);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(256.0, false)] // Too small
    [InlineData(512.0, true)]
    [InlineData(1024.0, true)]
    public void MemoryLimitThreshold_EnforcesMin512MB(double value, bool expectedValid)
    {
        var validator = new ResourceThresholdValidation(8000.0, 16000.0, "memory");
        var result = validator.Validate(value, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
        {
            Assert.Contains("Memory limit must be at least 512 MB", warning, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.05, false)] // Too small
    [InlineData(0.5, true)]
    [InlineData(8.0, true)]
    [InlineData(33.0, false)] // Too large
    public void CpuLimitThreshold_EnforcesRange(double value, bool expectedValid)
    {
        var validator = new ResourceThresholdValidation(4.0, 8.0, "CPU");
        var result = validator.Validate(value, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
        {
            Assert.Contains("CPU cores limit must be between 0.1 and 32.0", warning, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SettingConstants_AreConsistentlyPrefixed()
    {
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DockerRuntimePathSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DefaultImageSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.MemoryLimitSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.CpuLimitSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.AutoRemoveSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DaemonSocketSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.PlatformSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.TimeoutSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.NetworkModeSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.LogLevelSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.ShowTimestampsSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.ContainerNamePrefixSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.TelemetryRetentionSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.PullPolicySetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.ExtraFlagsSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DashboardRefreshSetting, StringComparison.Ordinal);
        Assert.StartsWith("ContainerImage_", ContainerExtensionModule.PerToolImagePrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackImage_IsValidDockerReference()
    {
        var fallback = ContainerExtensionModule.FallbackImage;
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        var result = _imageValidator.Validate(fallback, out _);
        Assert.True(result, $"FallbackImage '{fallback}' should pass the DockerImageFormatValidation.");
    }

    [Theory]
    [InlineData("containerextension-", true)]
    [InlineData("my.prefix", true)]
    [InlineData("test_prefix", true)]
    [InlineData("a", true)]
    [InlineData("", true)]           // Empty = Docker random naming
    [InlineData("   ", true)]        // Whitespace only = Docker random naming
    [InlineData("invalid name!", false)]
    [InlineData("-starts-with-dash", false)]
    [InlineData(".starts-with-dot", false)]
    [InlineData("has spaces", false)]
    [InlineData("special@chars#", false)]
    public void ContainerNamePrefix_ValidatesCorrectly(string input, bool expectedValid)
    {
        var result = _nameValidator.Validate(input, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
            Assert.NotNull(warning);
    }

    [Fact]
    public void ContainerNamePrefix_RejectsOver64Characters()
    {
        var longPrefix = new string('a', 65); // 65 chars exceeds Docker's 64-char limit
        var result = _nameValidator.Validate(longPrefix, out var warning);
        Assert.False(result, "Prefix longer than 64 characters should be rejected.");
        Assert.NotNull(warning);
    }

    [Fact]
    public void ContainerNamePrefix_Accepts64Characters()
    {
        var maxPrefix = new string('a', 64); // Exactly 64 chars = OK
        var result = _nameValidator.Validate(maxPrefix, out var warning);
        Assert.True(result, "Prefix of exactly 64 characters should be accepted.");
    }

    [Fact]
    public void Telemetry_LogAndRetrieveRoundtrip()
    {
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.LogExecution(
            image: "test/image:latest",
            tool: "ghdl",
            durationSeconds: 1.2345,
            exitCode: 0,
            imageDigest: "sha256:abc123def456",
            dockerRunCommand: "docker run --rm test/image:latest ghdl -a test.vhd");

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Single(entries);
        Assert.Equal("test/image:latest", entries[0].Image);
        Assert.Equal("ghdl", entries[0].Tool);
        Assert.Equal(1.2345, entries[0].DurationSeconds);
        Assert.Equal(0, entries[0].ExitCode);
        Assert.Equal("sha256:abc123def456", entries[0].ImageDigest);
        Assert.False(entries[0].WasCancelled);

        ContainerTelemetry.ClearEntries();
    }

    [Fact]
    public void Telemetry_GetStats_CalculatesCorrectly()
    {
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.LogExecution("img", "tool1", 2.0, exitCode: 0);
        ContainerTelemetry.LogExecution("img", "tool2", 4.0, exitCode: 0);
        ContainerTelemetry.LogExecution("img", "tool3", 6.0, exitCode: 1);

        var (totalRuns, successRate, avgDuration) = ContainerTelemetry.GetStats();
        Assert.Equal(3, totalRuns);
        Assert.Equal(66.7, successRate);  // 2/3 = 66.7%
        Assert.Equal(4.0, avgDuration);   // (2+4+6)/3 = 4.0

        ContainerTelemetry.ClearEntries();
    }

    [Fact]
    public void Telemetry_ClearEntries_RemovesAllData()
    {
        ContainerTelemetry.LogExecution("img", "tool", 1.0, exitCode: 0);
        ContainerTelemetry.ClearEntries();

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Empty(entries);

        var (totalRuns, _, _) = ContainerTelemetry.GetStats();
        Assert.Equal(0, totalRuns);
    }

    [Fact]
    public void Telemetry_Trimming_RespectsMaxEntries()
    {
        ContainerTelemetry.ClearEntries();

        for (int i = 0; i < 15; i++)
        {
            ContainerTelemetry.LogExecution(
                "img", $"tool_{i}", i * 0.5, exitCode: 0, maxEntries: 5);
        }

        var entries = ContainerTelemetry.GetRecentEntries(100);
        Assert.True(entries.Count <= 5,
            $"Expected at most 5 entries after trimming, but got {entries.Count}");

        Assert.Equal("tool_14", entries[0].Tool);

        ContainerTelemetry.ClearEntries();
    }

    [Fact]
    public void Telemetry_CancelledExecution_ExcludedFromSuccessRate()
    {
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.LogExecution("img", "tool1", 1.0, exitCode: 0);
        ContainerTelemetry.LogExecution("img", "tool2", 2.0, exitCode: 0, wasCancelled: true);

        var (totalRuns, successRate, _) = ContainerTelemetry.GetStats();
        if (totalRuns != 2 || successRate != 50.0)
        {
            var entries = ContainerTelemetry.GetRecentEntries(100);
            var list = string.Join("; ", entries.Select(e => $"{e.Tool} (exit={e.ExitCode}, cancelled={e.WasCancelled})"));
            Assert.Fail($"Mismatch. totalRuns={totalRuns}, successRate={successRate}. Entries: {list}");
        }

        ContainerTelemetry.ClearEntries();
    }

    [Fact]
    public void DrainLines_SingleLine_InvokedWithoutNewline()
    {
        var buffer = new StringBuilder();
        var lines = new List<string>();
        DockerExecutionStrategy.DrainLines(buffer, "hello\n", s => { lines.Add(s); return true; });
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void DrainLines_MultiLine_SplitsCorrectly()
    {
        var buffer = new StringBuilder();
        var lines = new List<string>();
        DockerExecutionStrategy.DrainLines(buffer, "line1\nline2\nline3\n", s => { lines.Add(s); return true; });
        Assert.Equal(3, lines.Count);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line2", lines[1]);
        Assert.Equal("line3", lines[2]);
    }

    [Fact]
    public void DrainLines_CarryOver_BuffersIncompleteLines()
    {
        var buffer = new StringBuilder();
        var lines = new List<string>();

        DockerExecutionStrategy.DrainLines(buffer, "partial", s => { lines.Add(s); return true; });
        Assert.Empty(lines);
        Assert.Equal("partial", buffer.ToString());

        DockerExecutionStrategy.DrainLines(buffer, " line\n", s => { lines.Add(s); return true; });
        Assert.Single(lines);
        Assert.Equal("partial line", lines[0]);
    }

    [Fact]
    public void DrainLines_CrLf_StripsCarriageReturn()
    {
        var buffer = new StringBuilder();
        var lines = new List<string>();
        DockerExecutionStrategy.DrainLines(buffer, "windows\r\n", s => { lines.Add(s); return true; });
        Assert.Single(lines);
        Assert.Equal("windows", lines[0]);
    }

    [Fact]
    public void DrainLines_NullHandler_DoesNotThrow()
    {
        var buffer = new StringBuilder();
        var ex = Record.Exception(() => DockerExecutionStrategy.DrainLines(buffer, "text\n", null));
        Assert.Null(ex);
    }

    [Fact]
    public void DrainLines_EmptyInput_NoInvocation()
    {
        var buffer = new StringBuilder();
        var lines = new List<string>();
        DockerExecutionStrategy.DrainLines(buffer, "", s => { lines.Add(s); return true; });
        Assert.Empty(lines);
    }

    [Fact]
    public void DrainLines_NoTrailingNewline_BufferedNotEmitted()
    {
        var buffer = new StringBuilder();
        var lines = new List<string>();
        DockerExecutionStrategy.DrainLines(buffer, "no newline", s => { lines.Add(s); return true; });
        Assert.Empty(lines);
        Assert.Equal("no newline", buffer.ToString());
    }

    [Theory]
    [InlineData("C:\u0008in\u0009ools", "C:/bin/tools")]
    [InlineData("gh\tdl", "gh/tdl")]
    [InlineData("gh\bdl", "gh/bdl")]
    [InlineData("gh\ndl", "gh/ndl")]
    [InlineData("gh\rdl", "gh/rdl")]
    [InlineData("gh\vdl", "gh/vdl")]
    [InlineData("gh\fdl", "gh/fdl")]
    [InlineData("gh\adl", "gh/adl")]
    public void HealEscapedPaths_HealsControlCharacters(string input, string expected)
    {
        var healed = DockerCommandBuilder.HealEscapedPaths(input);
        var normalized = healed.Replace('\\', '/');
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void BuildContainerParameters_BasicCommand_ProducesCorrectConfig()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("-a"),
                new TestCommandArgument("file.vhd")
            }
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest",
            command,
            null!,
            "1000", "1000",
            (cmd, log) => { });

        Assert.Equal("test_image:latest", param.Image);
        Assert.Equal("/workspace", param.WorkingDir);
        Assert.NotNull(param.Cmd);
        Assert.Equal(new[] { "ghdl", "-a", "file.vhd" }, param.Cmd);

        Assert.NotNull(param.HostConfig);
        Assert.Contains(param.HostConfig.Binds, b => b.EndsWith(":/workspace", StringComparison.Ordinal));
        Assert.True(param.HostConfig.AutoRemove);
        Assert.Equal("bridge", param.HostConfig.NetworkMode);

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("1000:1000", param.User);
        }
    }

    [Theory]
    [InlineData("relative/project/dir")]
    [InlineData("MertVerilog")]
    [InlineData("./build")]
    public void BuildContainerParameters_RelativeWorkingDirectory_Throws(string workingDir)
    {
        // A non-absolute working directory would otherwise be resolved against the plugin's process
        // directory, mounting "<bin>/<relative>" into /workspace instead of the project root.
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "synth",
            WorkingDirectory = workingDir,
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument("-V") }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DockerCommandBuilder.BuildContainerParameters("img:latest", command, null!, null, null, (c, l) => { }));
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildContainerParameters_EmptyWorkingDirectory_Throws()
    {
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "synth",
            WorkingDirectory = "",
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument("-V") }
        };

        Assert.Throws<InvalidOperationException>(() =>
            DockerCommandBuilder.BuildContainerParameters("img:latest", command, null!, null, null, (c, l) => { }));
    }

    [Fact]
    public void BuildContainerParameters_DefaultHomeEnvVar_InjectedWhenMissing()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>()
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest",
            command,
            null!,
            "1000", "1000",
            (cmd, log) => { });

        Assert.NotNull(param.Env);
        Assert.Contains("HOME=/tmp", param.Env);
    }

    [Fact]
    public void BuildContainerParameters_DefaultHomeEnvVar_NotOverwrittenIfSet()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>(),
            EnvironmentVariables = new Dictionary<string, string>
            {
                { "HOME", "/custom/home" }
            }
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest",
            command,
            null!,
            "1000", "1000",
            (cmd, log) => { });

        Assert.NotNull(param.Env);
        Assert.Contains("HOME=/custom/home", param.Env);
        Assert.DoesNotContain("HOME=/tmp", param.Env);
    }

    [Fact]
    public void BuildContainerParameters_BindsCorrectlyBasedOnToolWriteAccess()
    {
        // Pack tool: write access, so bind without :ro
        var cmdGmpack = new ToolCommand
        {
            Executable = "gmpack",
            ToolName = "gmpack",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>()
        };
        var paramGmpack = DockerCommandBuilder.BuildContainerParameters("img", cmdGmpack, null!, null, null, (c, l) => { });
        Assert.Contains(paramGmpack.HostConfig.Binds, b => b.EndsWith(":/workspace", StringComparison.Ordinal));

        // Programmer: read-only, so bind with :ro
        var cmdLoader = new ToolCommand
        {
            Executable = "openFPGALoader",
            ToolName = "openFPGALoader",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>()
        };
        var paramLoader = DockerCommandBuilder.BuildContainerParameters("img", cmdLoader, null!, null, null, (c, l) => { });
        Assert.Contains(paramLoader.HostConfig.Binds, b => b.EndsWith(":/workspace:ro", StringComparison.Ordinal));

        var cmdIcepack = new ToolCommand
        {
            Executable = "icepack",
            ToolName = "icepack",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>()
        };
        var paramIcepack = DockerCommandBuilder.BuildContainerParameters("img", cmdIcepack, null!, null, null, (c, l) => { });
        Assert.Contains(paramIcepack.HostConfig.Binds, b => b.EndsWith(":/workspace", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildContainerParameters_CommandWithSpecialCharacters_ArePassedAsArgvTokensUnquoted()
    {
        var command = new ToolCommand
        {
            Executable = "my_tool",
            ToolName = "test",
            WorkingDirectory = "/workspace",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("file with space.vhd"),
                new TestCommandArgument("part1;part2"),
                new TestCommandArgument("echo \"hello\"")
            }
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest", command, null!, null, null, (c, l) => { });

        // Under the argv model each token is delivered verbatim as one argument; no shell quoting applies.
        Assert.Equal(new[] { "my_tool", "file with space.vhd", "part1;part2", "echo \"hello\"" }, param.Cmd);
    }

    [Fact]
    public void BuildContainerParameters_NoArguments_BuildsCorrectly()
    {
        var command = new ToolCommand
        {
            Executable = "tool_only",
            ToolName = "test",
            WorkingDirectory = "/dir",
            CommandArguments = Array.Empty<ICommandArgument>()
        };
        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image", command, null!, null, null, (c, l) => { });

        Assert.Equal(new[] { "tool_only" }, param.Cmd!);
    }

    [Fact]
    public void ParseEnvFile_BasicKeyValue_Parsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "KEY1=value1\nKEY2=value2\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Equal(2, result!.Count);
            Assert.Contains("KEY1=value1", result);
            Assert.Contains("KEY2=value2", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseEnvFile_CommentsAndBlanks_Skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "# comment\n\n  \nKEY=value\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Single(result!);
            Assert.Equal("KEY=value", result[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseEnvFile_MalformedLines_HandledGracefully()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "NO_EQUALS_HERE\nKEY_ONLY=\n=VALUE_ONLY\nVALID=1");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Contains("VALID=1", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseEnvFile_QuotedValues_Unquoted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "A=\"hello world\"\nB='single'\nC=bare # comment\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Equal(3, result!.Count);
            Assert.Contains("A=hello world", result);
            Assert.Contains("B=single", result);
            Assert.Contains("C=bare", result); // Inline comment stripped
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseEnvFile_InlineCommentAfterQuotes_StrippedCorrectly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "SECRET=\"xyz\" # inline comment\nKEY='abc' # another comment\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Equal(2, result!.Count);
            Assert.Contains("SECRET=xyz", result);
            Assert.Contains("KEY=abc", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("ubuntu", "", "", "ubuntu")]
    [InlineData("library/ubuntu", "", "library", "ubuntu")]
    [InlineData("ghcr.io/org/repo/sub:latest", "ghcr.io", "org/repo", "sub")]
    [InlineData("localhost:5000/my-app:1.0.0", "localhost:5000", "", "my-app")]
    [InlineData("ghcr.io/namespace/repository/subrepo", "ghcr.io", "namespace/repository", "subrepo")]
    public void RegistryClient_ParseImageReference_SplitsCorrectly(string input, string expectedRegistry, string expectedNs, string expectedRepo)
    {
        var tuple = ContainerExtension.Registry.RegistryClient.ParseImageReference(input);
        Assert.Equal(expectedRegistry, tuple.Registry);
        Assert.Equal(expectedNs, tuple.Namespace);
        Assert.Equal(expectedRepo, tuple.Repository);
    }

    [Fact]
    public void ParseEnvFile_MissingFile_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.Null(result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseEnvFile_HashInValue_PreservedWithoutSpace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "COLOR=#FF0000\nURL=http://host/#anchor\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Equal(2, result!.Count);
            Assert.Contains("COLOR=#FF0000", result);
            Assert.Contains("URL=http://host/#anchor", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseEnvFile_CommandInjectionSanitised()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "SAFE=value\nUNSAFE_BACKTICK=echo `whoami`\nUNSAFE_SUB=test$(rm -rf /)end\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Equal(3, result!.Count);
            Assert.Contains("SAFE=value", result);
            Assert.Contains("UNSAFE_BACKTICK=echo whoami", result);
            Assert.Contains("UNSAFE_SUB=testend", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Telemetry_ResourceProfile_RoundTrip()
    {
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.LogExecution(
            image: "hdlc/ghdl:yosys",
            tool: "nextpnr",
            durationSeconds: 45.67,
            exitCode: 0,
            peakMemoryBytes: 512 * 1024 * 1024L, // 512 MB
            maxCpuPercent: 89.2);

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Single(entries);
        Assert.Equal(512 * 1024 * 1024L, entries[0].PeakMemoryBytes);
        Assert.Equal(89.2, entries[0].MaxCpuPercent);
        Assert.False(entries[0].OomKilled);

        ContainerTelemetry.ClearEntries();
    }

    [Fact]
    public void Telemetry_OomKilled_SerializedCorrectly()
    {
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.LogExecution(
            image: "hdlc/ghdl:yosys",
            tool: "nextpnr",
            durationSeconds: 120.0,
            exitCode: 137, // SIGKILL from OOM
            peakMemoryBytes: 4096 * 1024 * 1024L, // 4 GB
            maxCpuPercent: 100.0,
            oomKilled: true);

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Single(entries);
        Assert.True(entries[0].OomKilled);
        Assert.Equal(137, entries[0].ExitCode);
        Assert.Equal(4096 * 1024 * 1024L, entries[0].PeakMemoryBytes);

        ContainerTelemetry.ClearEntries();
    }

    [Fact]
    public void Telemetry_NullProfile_BackwardCompatible()
    {
        ContainerTelemetry.ClearEntries();

        // Log without resource profile (simulates pre-OOM Analyzer entries)
        ContainerTelemetry.LogExecution(
            image: "ubuntu:latest",
            tool: "ghdl",
            durationSeconds: 2.5,
            exitCode: 0);

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Single(entries);
        Assert.Null(entries[0].PeakMemoryBytes);
        Assert.Null(entries[0].MaxCpuPercent);
        Assert.False(entries[0].OomKilled);

        ContainerTelemetry.ClearEntries();
    }

    [Fact]
    public void Telemetry_ExportTo_CreatesValidCopy()
    {
        ContainerTelemetry.ClearEntries();
        ContainerTelemetry.LogExecution("img", "tool", 1.0, exitCode: 0);

        var destDir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        var destPath = Path.Combine(destDir, "export.jsonl");
        try
        {
            var result = ContainerTelemetry.ExportTo(destPath);
            Assert.True(result, "Export should succeed when telemetry file exists.");
            Assert.True(File.Exists(destPath), "Exported file should exist on disk.");

            var content = File.ReadAllText(destPath);
            Assert.Contains("\"tool\":\"tool\"", content, StringComparison.Ordinal);
            Assert.Contains("\"image\":\"img\"", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            ContainerTelemetry.ClearEntries();
        }
    }

    [Fact]
    public void Telemetry_ExportTo_MissingSource_ReturnsFalse()
    {
        ContainerTelemetry.ClearEntries(); // Clears entries (truncates)
        if (File.Exists(ContainerTelemetry.TelemetryFilePath))
        {
            File.Delete(ContainerTelemetry.TelemetryFilePath);
        }

        var destPath = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}", "export.jsonl");
        var result = ContainerTelemetry.ExportTo(destPath);
        Assert.False(result, "Export should return false when no telemetry file exists.");
        Assert.False(File.Exists(destPath), "No file should be created when export fails.");
    }

    [Fact]
    public void SettingConstants_HaveExpectedValues()
    {
        // Verify critical constant values that other components depend on
        Assert.Equal("hdlc/ghdl:yosys", ContainerExtensionModule.FallbackImage);
        Assert.Equal("Container Dashboard", ContainerExtensionModule.DashboardTitle);
        Assert.Equal("#2496ED", ContainerExtensionModule.DockerBlueHex);
        Assert.StartsWith("M", ContainerExtensionModule.WhaleIconPath, StringComparison.Ordinal); // SVG path starts with M(ove)
        Assert.StartsWith("ContainerImage_", ContainerExtensionModule.PerToolImagePrefix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("my.registry:5000/org/repo:v1.2", true)]    // Registry with port and namespace
    [InlineData("UPPER/repo:tag", true)]                      // Uppercase allowed by validator regex
    [InlineData("image with spaces", false)]                   // Spaces rejected
    [InlineData("image::tag", false)]                          // Double colon
    [InlineData("image/", false)]                             // Trailing slash
    [InlineData("a", true)]                                   // Single char image name
    public void DockerImageFormat_EdgeCases(string input, bool expectedValid)
    {
        var result = _imageValidator.Validate(input, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
            Assert.NotNull(warning);
    }

    [Fact]
    public void DockerImageFormat_LongButValidName()
    {
        // 200-char valid image reference (within Docker's limits)
        var longName = new string('a', 100) + "/" + new string('b', 98) + ":v1";
        var result = _imageValidator.Validate(longName, out _);
        Assert.True(result, "Long but well-formed image references should be accepted.");
    }

    [Fact]
    public void MapPathToContainer_NullOrEmpty_ReturnsSame()
    {
        Assert.Null(DockerCommandBuilder.MapPathToContainer(null!, "/workspace"));
        Assert.Equal("", DockerCommandBuilder.MapPathToContainer("", "/workspace"));
    }

    [Fact]
    public void MapPathToContainer_RelativePath_MapsToWorkspace()
    {
        var curDir = Directory.GetCurrentDirectory();
        var relativeResult = DockerCommandBuilder.MapPathToContainer("somefile.txt", curDir);
        Assert.Equal("/workspace/somefile.txt", relativeResult);
    }

    [Fact]
    public void MapPathToContainer_AbsolutePathInWorkspace_MapsToWorkspace()
    {
        var curDir = Directory.GetCurrentDirectory();
        var fileInWorkspace = Path.Combine(curDir, "subdir", "anotherfile.txt");
        var result = DockerCommandBuilder.MapPathToContainer(fileInWorkspace, curDir);
        Assert.Equal("/workspace/subdir/anotherfile.txt", result.Replace('\\', '/'));
    }

#pragma warning disable CA1305, CA1307, CA1031, CA1822, CS8019, CA1308
    [Fact(Skip = "Diagnostic helper requiring OneWare Studio installed on the host")]
    public void ReflectOneWareServices()
    {
        string ResolvePath(string assemblyName)
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, assemblyName);
            if (File.Exists(localPath))
            {
                return localPath;
            }
            var macPath = Path.Combine("/Applications/OneWare Studio.app/Contents/MacOS", assemblyName);
            if (File.Exists(macPath))
            {
                return macPath;
            }
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, assemblyName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return macPath;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            try
            {
                var name = new System.Reflection.AssemblyName(args.Name).Name;
                if (!string.IsNullOrEmpty(name))
                {
                    var path = ResolvePath($"{name}.dll");
                    if (File.Exists(path))
                    {
                        return System.Reflection.Assembly.LoadFrom(path);
                    }
                }
            }
            catch
            {
            }
            return null;
        };

        var result = new StringBuilder();
        void ReflectAssembly(string fileName)
        {
            try
            {
                var path = ResolvePath(fileName);
                var assembly = System.Reflection.Assembly.LoadFrom(path);
                result.AppendLine($"Assembly: {assembly.FullName}");
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(OneWare.Essentials.ViewModels.ExtendedTool).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                    {
                        result.AppendLine($"  ExtendedTool Subclass: {type.FullName} (Base: {type.BaseType?.FullName})");
                        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                        {
                            result.AppendLine($"    Prop: {prop.PropertyType.Name} {prop.Name}");
                        }
                        foreach (var ctor in type.GetConstructors())
                        {
                            var pars = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                            result.AppendLine($"    Ctor: ({pars})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.AppendLine($"Fail to load {fileName}: {ex.Message}");
            }
            result.AppendLine();
        }

        ReflectAssembly("OneWare.Chat.dll");
        ReflectAssembly("OneWare.Copilot.dll");
        ReflectAssembly("OneWare.Core.dll");
        ReflectAssembly("OneWare.Studio.dll");

        Assert.Fail("Output: " + result.ToString());
    }
    [Fact]
    public async Task ExecuteAsync_RejectsControlCharactersInExecutable()
    {
        using var provider = new TestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var command = new ToolCommand
        {
            Executable = "gh\0dl",
            ToolName = "test",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>()
        };
        await Assert.ThrowsAsync<ArgumentException>(() => strategy.ExecuteAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsCommandSeparatorsInArguments()
    {
        using var provider = new TestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("file.vhd"),
                new TestCommandArgument("arg; rm -rf /")
            }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => strategy.ExecuteAsync(command, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_AllowsCommandSemicolonsInYosysArguments()
    {
        using var provider = new TestServiceProvider();
        using var strategy = new DockerExecutionStrategy(provider);
        var command = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("-p"),
                new TestCommandArgument("read_verilog Verilog_Blink.v; synth -top Verilog_Blink")
            }
        };
        var ex = await Record.ExceptionAsync(() => strategy.ExecuteAsync(command, TestContext.Current.CancellationToken));
        if (ex != null)
        {
            Assert.IsNotType<ArgumentException>(ex);
        }
    }

    [Fact]
    public void MapPathToContainer_ResolvesSymlinksCanonically()
    {
        // On macOS/Linux, we can create a temporary file and a symlink to test canonical path resolution.
        // On Windows, symlinks are supported but require privilege, so we test best effort or fallback.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var targetFile = Path.Combine(tempDir, "realfile.txt");
        File.WriteAllText(targetFile, "content");

        var linkFile = Path.Combine(tempDir, "linkfile.txt");
        try
        {
            File.CreateSymbolicLink(linkFile, targetFile);
            // Verify that resolving linkFile resolved targetFile path (or resolves to targetFile)
            var mappedLink = DockerCommandBuilder.MapPathToContainer(linkFile, tempDir);
            var mappedTarget = DockerCommandBuilder.MapPathToContainer(targetFile, tempDir);
            Assert.Equal(mappedTarget, mappedLink);
        }
        catch
        {
            // If creation of link fails (e.g. windows without developer mode), skip verification of link targets
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void MapPathToContainer_EscapesWorkspace_ReturnsInvalidEscapedPath()
    {
        var result = DockerCommandBuilder.MapPathToContainer("/etc/passwd", "/workspace/myproj");
        Assert.Equal("/workspace/invalid_escaped_path", result);
    }

    [Fact]
    public void MapPathToContainer_OptionWithEqualsSign_MapsPathCorrectly()
    {
        var result = DockerCommandBuilder.MapPathToContainer("--workdir=" + HostAbs("myproj/build"), HostAbs("myproj"));
        Assert.Equal("--workdir=/workspace/build", result.Replace('\\', '/'));
    }

    [Fact]
    public void MapPathToContainer_OptionWithP_MapsPathCorrectly()
    {
        var result = DockerCommandBuilder.MapPathToContainer("-P" + HostAbs("myproj/build"), HostAbs("myproj"));
        Assert.Equal("-P/workspace/build", result.Replace('\\', '/'));
    }

    [Fact]
    public void MapPathToContainer_OptionWithEqualsSignRelative_MapsPathCorrectly()
    {
        var result = DockerCommandBuilder.MapPathToContainer("--workdir=build", "/workspace/myproj");
        Assert.Equal("--workdir=/workspace/build", result.Replace('\\', '/'));
    }

    [Fact]
    public void MapPathToContainer_OptionWithPRelative_MapsPathCorrectly()
    {
        var result = DockerCommandBuilder.MapPathToContainer("-Pbuild", "/workspace/myproj");
        Assert.Equal("-P/workspace/build", result.Replace('\\', '/'));
    }

    [Fact]
    public void MapPathToContainer_WorkLibraryOption_ExtractsLibraryName()
    {
        var result1 = DockerCommandBuilder.MapPathToContainer("--work=/work/.oneware/ghdl-dummy/ghdl", "/workspace/myproj");
        Assert.Equal("--work=ghdl", result1);

        var result2 = DockerCommandBuilder.MapPathToContainer("-work=C:\\windows\\path\\libname", "/workspace/myproj");
        Assert.Equal("-work=libname", result2);
    }

    [Fact]
    public void BuildContainerParameters_SeparateWorkOption_ExtractsLibraryName()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace/myproj",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("-i"),
                new TestCommandArgument("--work"),
                new TestCommandArgument("/work/.oneware/ghdl-dummy/ghdl")
            }
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest",
            command,
            null!,
            "1000", "1000",
            (cmd, log) => { });

        Assert.Equal("ghdl -i --work ghdl", string.Join(" ", param.Cmd!));
    }

    [Fact]
    public void BuildContainerParameters_EqualsWorkOption_ExtractsLibraryName()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace/myproj",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("-i"),
                new TestCommandArgument("--work=/work/.oneware/ghdl-dummy/ghdl")
            }
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest",
            command,
            null!,
            "1000", "1000",
            (cmd, log) => { });

        Assert.Equal("ghdl -i --work=ghdl", string.Join(" ", param.Cmd!));
    }

    [Fact]
    public void BuildContainerParameters_PathsWithSpaces_DoesNotSplitPath()
    {
        // The working directory deliberately contains a space (the behavior under test), but it must be a
        // throwaway temp location: the builder pre-creates the mapped --workdir on the host, so a real
        // user path here would materialize directories on disk (e.g. an iCloud folder) as a side effect.
        var root = Path.Combine(Path.GetTempPath(), "OneWareTests_Spaces_" + Guid.NewGuid().ToString("N"));
        var baseDir = Path.Combine(root, "with space", "addressierungsbeispiel");
        try
        {
            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "test",
                WorkingDirectory = baseDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument($"--workdir={baseDir}/build"),
                    new TestCommandArgument($"{baseDir}/rtl/addressing.vhd")
                }
            };

            var param = DockerCommandBuilder.BuildContainerParameters(
                "test_image:latest",
                command,
                null!,
                "1000", "1000",
                (cmd, log) => { });

            Assert.Contains("--workdir=/workspace/build", string.Join(" ", param.Cmd!));
            Assert.Contains("/workspace/rtl/addressing.vhd", string.Join(" ", param.Cmd!));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void TokenizeExtraFlags_ParsesQuotedSegmentsCorrectly()
    {
        var method = typeof(DockerCommandBuilder).GetMethod("TokenizeExtraFlags", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { "--label=\"foo=bar baz\" -p 80:80" }) as List<string>;
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("--label=foo=bar baz", result[0]);
        Assert.Equal("-p", result[1]);
        Assert.Equal("80:80", result[2]);
    }

    [Fact]
    public void ParseEnvFile_StripsInlineCommentWithoutLeadingSpace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "VAL=secret#comment\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("VAL=secret", result[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParseEnvFile_HandlesEscapedQuotesAndBackslashes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "VAL=\"line1 \\\\\"\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("VAL=line1 \\\\", result[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParseEnvFile_InlineCommentWithEscapedQuotes_CorrectlyParsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "VAL=\"some \\\"value\\\"\" # comment\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("VAL=some \\\"value\\\"", result[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParseEnvFile_InlineCommentWithQuotes_CorrectlyParsed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "VAL=\"some value\" # comment\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("VAL=some value", result[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ParseImageReference_HandlesRegistryWithPortSpecs()
    {
        var method = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("ParseImageReference", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var res1 = ((string Registry, string Namespace, string Repository))method.Invoke(null, new object[] { "localhost:5000/myrepo:latest" })!;
        Assert.Equal("localhost:5000", res1.Registry);
        Assert.Equal("", res1.Namespace);
        Assert.Equal("myrepo", res1.Repository);

        var res2 = ((string Registry, string Namespace, string Repository))method.Invoke(null, new object[] { "registry.io:5000/mygroup/myrepo:latest" })!;
        Assert.Equal("registry.io:5000", res2.Registry);
        Assert.Equal("mygroup", res2.Namespace);
        Assert.Equal("myrepo", res2.Repository);
    }

    [Fact]
    public void ShortId_StripsSha256PrefixCorrectly()
    {
        var input = "sha256:abc123def4567890abcdef123456";
        var result = input.ShortId();
        Assert.Equal("abc123def456", result);
    }

    [Fact]
    public void IsTargetingEmptyGhdlLibrary_DetectsEmptyLibraryCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "ghdl-obj93.cf"), new byte[] { 0, 0, 0, 0 });

            var cmdEmpty = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("-m"),
                    new TestCommandArgument("--work=ghdl"),
                    new TestCommandArgument("VHDL_Blink")
                }
            };
            Assert.True(DockerExecutionStrategy.IsTargetingEmptyGhdlLibrary(cmdEmpty));

            var cmdNonEmpty = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("-m"),
                    new TestCommandArgument("--work=work"),
                    new TestCommandArgument("VHDL_Blink")
                }
            };
            Assert.False(DockerExecutionStrategy.IsTargetingEmptyGhdlLibrary(cmdNonEmpty));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("unix:///var/run/docker.sock with spaces", false)]
    [InlineData("tcp://127.0.0.1:2375 ", false)]
    [InlineData(" tcp://127.0.0.1:2375", false)]
    [InlineData("unix:///var/run/docker.sock", true)]
    public void DaemonSocketFormat_RejectsSpaces(string input, bool expectedValid)
    {
        // unix:// sockets are intentionally rejected on Windows (the daemon is reached via a named
        // pipe there), so a well-formed unix:// URI is valid only off-Windows.
        if (expectedValid && OperatingSystem.IsWindows() && input.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
        {
            expectedValid = false;
        }

        var result = _socketValidator.Validate(input, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
        {
            Assert.NotNull(warning);
        }
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    [InlineData(4096.0, true)]
    public void ResourceThreshold_RejectsNaNAndInfinity(double value, bool expectedValid)
    {
        var validator = new ResourceThresholdValidation(8192.0, 16384.0, "RAM (MB)");
        var result = validator.Validate(value, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
        {
            Assert.NotNull(warning);
        }
    }

    [Fact]
    public void ParseEnvFile_SkipsEmptyKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"container_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "=VALUE_ONLY\nKEY=val\n");
            var result = DockerCommandBuilder.ParseEnvFile(dir);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("KEY=val", result[0]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TokenizeExtraFlags_HandlesUnmatchedQuotesGracefully()
    {
        var method = typeof(DockerCommandBuilder).GetMethod("TokenizeExtraFlags", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { "--label=\"unmatched-quote-test -p 80:80" }) as List<string>;
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("--label=unmatched-quote-test -p 80:80", result[0]);
    }

    [Fact]
    public void DockerCommandBuilder_DeduplicatesPortMappings()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace",
            CommandArguments = new List<ICommandArgument>()
        };

        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, "--label=\"test\" -p 8080:80 -p 8080:80");

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image",
            command,
            settings,
            null, null,
            (c, l) => { });

        Assert.NotNull(param.HostConfig);
        Assert.NotNull(param.HostConfig.PortBindings);
        Assert.Single(param.HostConfig.PortBindings);
    }

    [Fact]
    public void ResourceThreshold_SupportsNumericConversions()
    {
        var validator = new ResourceThresholdValidation(75.0, 100.0, "Resource");

        Assert.True(validator.Validate(50.0, out _));
        Assert.True(validator.Validate(50f, out _));
        Assert.True(validator.Validate(50, out _));
        Assert.True(validator.Validate(50L, out _));
        Assert.True(validator.Validate("50.0", out _));
        Assert.False(validator.Validate("invalid", out _));
    }

    [Fact]
    public void DaemonSocketValidation_RejectsSpacesAndInvalidCharacters()
    {
        var validator = new DaemonSocketValidation();
        Assert.True(validator.Validate("tcp://localhost:2375", out _));
        Assert.False(validator.Validate(" tcp://localhost:2375", out _));
        Assert.False(validator.Validate("tcp://localhost:2375 ", out _));
    }

    [Fact]
    public void ShouldMapArgument_ExcludesUrls()
    {
        Assert.False(DockerCommandBuilder.ShouldMapArgument("http://localhost:8080/path"));
        Assert.False(DockerCommandBuilder.ShouldMapArgument("https://github.com/FEntwumS"));
        Assert.True(DockerCommandBuilder.ShouldMapArgument("/workspace/file.txt"));
    }

    [Fact]
    public void MapCommandScriptPaths_YosysScriptTest()
    {
        var workingDirFull = HostAbs("work/OneWareStudio/Projects/Verilog_Blink");
        var workingDirCanonical = workingDirFull;
        var script = $"synth_gatemate -top  Verilog_Blink -luttree -nomx8; write_json {HostAbs("work/OneWareStudio/Projects/Verilog_Blink/build/synth.json")}";

        var result = DockerCommandBuilder.MapCommandScriptPaths(script, workingDirFull, workingDirCanonical);
        Assert.Equal("synth_gatemate -top  Verilog_Blink -luttree -nomx8; write_json /workspace/build/synth.json", result);
    }

    [Fact]
    public void MapCommandScriptPaths_YosysScriptWithQuotesTest()
    {
        var workingDirFull = HostAbs("work/OneWareStudio/Projects/Verilog_Blink");
        var workingDirCanonical = workingDirFull;
        var script = $"\"synth_gatemate -top  Verilog_Blink -luttree -nomx8; write_json {HostAbs("work/OneWareStudio/Projects/Verilog_Blink/build/synth.json")}\"";

        var result = DockerCommandBuilder.MapCommandScriptPaths(script, workingDirFull, workingDirCanonical);
        Assert.Equal("\"synth_gatemate -top  Verilog_Blink -luttree -nomx8; write_json /workspace/build/synth.json\"", result);
    }

    [Fact]
    public void MapCommandScriptPaths_MultilineScriptTest()
    {
        var workingDirFull = HostAbs("work/OneWareStudio/Projects/Verilog_Blink");
        var workingDirCanonical = workingDirFull;
        var script = $"synth_gatemate -top Verilog_Blink\nwrite_json\t{HostAbs("work/OneWareStudio/Projects/Verilog_Blink/build/synth.json")}\r\n#comment";

        var result = DockerCommandBuilder.MapCommandScriptPaths(script, workingDirFull, workingDirCanonical);
        Assert.Equal("synth_gatemate -top Verilog_Blink\nwrite_json\t/workspace/build/synth.json\r\n#comment", result);
    }

    [Fact]
    public void MapCommandScriptPaths_NestedQuotesScriptTest()
    {
        var workingDirFull = HostAbs("work/OneWareStudio/Projects/Verilog_Blink");
        var workingDirCanonical = workingDirFull;
        var script = $"'\"synth_gatemate -top Verilog_Blink; write_json {HostAbs("work/OneWareStudio/Projects/Verilog_Blink/build/synth.json")}\"'";

        var result = DockerCommandBuilder.MapCommandScriptPaths(script, workingDirFull, workingDirCanonical);
        Assert.Equal("'\"synth_gatemate -top Verilog_Blink; write_json /workspace/build/synth.json\"'", result);
    }

    [Fact]
    public void BuildContainerParameters_GhdlMakeWithVhdlFile_MapsToUnitName()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "ghdl",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("-m"),
                new TestCommandArgument("--work=ghdl"),
                new TestCommandArgument("/work/OneWareStudio/Projects/VHDL_Blink/VHDL_Blink.vhd")
            }
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "img", command, null!, null, null, (c, l) => { });

        var shellCmd = string.Join(" ", param.Cmd!);
        Assert.Equal("ghdl -m --work=ghdl VHDL_Blink", shellCmd);
    }

    [Fact]
    public void ToolRequiresWriteAccess_DetectsRedirectionAndVariousOutputFlags()
    {
        // Shell Redirection
        var cmdRedir = new ToolCommand
        {
            Executable = "some-unknown-tool",
            ToolName = "some-unknown-tool",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument(">"), new TestCommandArgument("out.txt") }
        };
        var paramRedir = DockerCommandBuilder.BuildContainerParameters("img", cmdRedir, null!, null, null, (c, l) => { });
        Assert.Contains(paramRedir.HostConfig.Binds, b => b.EndsWith(":/workspace", StringComparison.Ordinal));

        // Output parameter format --write=
        var cmdWrite = new ToolCommand
        {
            Executable = "some-unknown-tool",
            ToolName = "some-unknown-tool",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument("--write=out.json") }
        };
        var paramWrite = DockerCommandBuilder.BuildContainerParameters("img", cmdWrite, null!, null, null, (c, l) => { });
        Assert.Contains(paramWrite.HostConfig.Binds, b => b.EndsWith(":/workspace", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortId_TrimsAndTruncatesCorrectly()
    {
        Assert.Equal("123456789012", "  sha256:12345678901234567890  ".ShortId());
        Assert.Equal("123456", " 123456 ".ShortId());
    }

    [Fact]
    public void PerToolImageSettingKey_IsRegisteredAsLowercase()
    {
        var key = "openFPGALoader";
        var lowerKey = key.ToLowerInvariant();
        var settingKey = $"{ContainerExtensionModule.PerToolImagePrefix}{lowerKey}";

        Assert.Equal("ContainerImage_openfpgaloader", settingKey);
    }

    [Fact]
    public void TrimTelemetryFileInternal_ShouldRotateAndTrimFileCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"telemetry_trim_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "test_telemetry.jsonl");

        try
        {
            var lines = new List<string>();
            for (int i = 0; i < 15; i++)
            {
                lines.Add($"{{\"val\": {i}}}");
            }
            File.WriteAllLines(filePath, lines);

            var method = typeof(ContainerTelemetry).GetMethod("TrimTelemetryFileInternal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            method.Invoke(null, new object[] { filePath, 5 });

            Assert.True(File.Exists(filePath));
            var remainingLines = File.ReadAllLines(filePath);
            Assert.Equal(5, remainingLines.Length);
            Assert.Contains("\"val\": 10", remainingLines[0]);
            Assert.Contains("\"val\": 14", remainingLines[4]);

            var tempPath = filePath + ".tmp";
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void ContainerTelemetry_RedactsSensitiveData()
    {
        var scrubMethod = typeof(ContainerTelemetry).GetMethod("ScrubSensitiveInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(scrubMethod);

        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            var rawCommand = $"user is {userName}";
            var scrubbed = scrubMethod.Invoke(null, new object[] { rawCommand }) as string;
            Assert.NotNull(scrubbed);
            Assert.DoesNotContain(userName, scrubbed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("***", scrubbed);
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            var pathString = $"path is {homeDir}/some/subfolder";
            var scrubbed = scrubMethod.Invoke(null, new object[] { pathString }) as string;
            Assert.NotNull(scrubbed);
            Assert.DoesNotContain(homeDir, scrubbed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("~", scrubbed);
        }

        var ipString = "connecting to 192.168.0.25 and 10.0.0.1 and 127.0.0.1";
        var scrubbedIp = scrubMethod.Invoke(null, new object[] { ipString }) as string;
        Assert.NotNull(scrubbedIp);
        Assert.Contains("[REDACTED_NET_ADDR]", scrubbedIp);
        Assert.DoesNotContain("192.168.0.25", scrubbedIp);
        Assert.DoesNotContain("10.0.0.1", scrubbedIp);
    }

    [Fact]
    public void RegistryClient_PrunesCacheToMaxLimit()
    {
        var tagsCacheField = typeof(ContainerExtension.Registry.RegistryClient).GetField("TagsCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(tagsCacheField);
        var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, (List<string> tags, long cacheTimeTicks)>)tagsCacheField.GetValue(null)!;

        cache.Clear();
        // Use very small sub-second offsets so none of them are considered expired (age >= 60 seconds),
        // triggering only the count-based oldest pruning logic.
        var baseTicks = Environment.TickCount64;
        for (int i = 0; i < 105; i++)
        {
            cache[$"image_{i}"] = (new List<string> { "latest" }, baseTicks - i * 100);
        }

        Assert.Equal(105, cache.Count);

        var addToCacheMethod = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("AddToCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(addToCacheMethod);

        addToCacheMethod.Invoke(null, new object[] { "new_image", new List<string> { "v1" } });

        Assert.Equal(100, cache.Count);
        Assert.True(cache.ContainsKey("new_image"));
        // Oldest entries (e.g., image_104, image_103, etc.) should be pruned
        Assert.False(cache.ContainsKey("image_104"));
        Assert.False(cache.ContainsKey("image_103"));
    }

    [Fact]
    public void BuildContainerParameters_SeparatorNormalization_WorksCorrectly()
    {
        var command = new ToolCommand
        {
            Executable = "ghdl\r",
            ToolName = "test",
            WorkingDirectory = "/workspace",
            CommandArguments = new List<ICommandArgument>
            {
                new TestCommandArgument("file\\name.vhd\r")
            }
        };

        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image", command, null!, null, null, (c, l) => { });

        var shellCmd = string.Join(" ", param.Cmd!);
        Assert.Equal("ghdl /workspace/file/name.vhd", shellCmd);
    }

    [Theory]
    [InlineData("unix:///var\\run/docker.sock", false)]
    [InlineData("unix:///var/run/docker.sock ", false)]
    [InlineData("unix:///var/run/../docker.sock", false)]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("tcp://127.0.0.1:2375", true)]
    [InlineData("npipe://./pipe/docker_engine", true)]
    [InlineData("\\\\.\\pipe\\docker_engine", true)]
    public void DaemonSocketValidation_EdgeCases(string input, bool expectedValid)
    {
        if (expectedValid)
        {
            bool isNamedPipe = input.StartsWith(@"\\.\", StringComparison.Ordinal) || input.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase);
            bool isUnixSocket = input.StartsWith("unix://", StringComparison.OrdinalIgnoreCase);
            if (isNamedPipe && !OperatingSystem.IsWindows()) expectedValid = false;
            if (isUnixSocket && OperatingSystem.IsWindows()) expectedValid = false;
        }

        var result = _socketValidator.Validate(input, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
            Assert.NotNull(warning);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("hello", true)]
    [InlineData("hello-world_123", true)]
    [InlineData("Hello", false)]
    [InlineData("heLlo", false)]
    [InlineData("HELLO", false)]
    [InlineData("helloW", false)]
    public void IsAllLowercase_ValidatesCorrectly(string input, bool expectedResult)
    {
        var method = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("IsAllLowercase", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = (bool)method.Invoke(null, new object[] { input })!;
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public async Task RegistryClient_DnsCache_EvictsCorrectly()
    {
        var method = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("ResolveDnsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var cacheField = typeof(ContainerExtension.Registry.RegistryClient).GetField("DnsCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(cacheField);
        var dnsCache = (System.Collections.Concurrent.ConcurrentDictionary<string, (System.Net.IPAddress[] ips, long cacheTimeTicks)>)cacheField.GetValue(null)!;

        dnsCache.Clear();

        for (int i = 0; i < 49; i++)
        {
            dnsCache[$"host{i}.com"] = (Array.Empty<System.Net.IPAddress>(), Environment.TickCount64);
        }
        Assert.Equal(49, dnsCache.Count);

        var task = (Task<System.Net.IPAddress[]>)method.Invoke(null, new object[] { "localhost", CancellationToken.None })!;
        var result = await task.ConfigureAwait(true);
        Assert.NotNull(result);
        Assert.Equal(50, dnsCache.Count);

        var task2 = (Task<System.Net.IPAddress[]>)method.Invoke(null, new object[] { "127.0.0.1", CancellationToken.None })!;
        var result2 = await task2.ConfigureAwait(true);
        Assert.NotNull(result2);

        Assert.True(dnsCache.Count < 50);
    }

    [Fact]
    public void ContainerTelemetry_Sanitization_Fuzzing()
    {
        var scrubSecretsMethod = typeof(ContainerTelemetry).GetMethod("ScrubSecrets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(scrubSecretsMethod);

        var scrubSensitiveInfoMethod = typeof(ContainerTelemetry).GetMethod("ScrubSensitiveInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(scrubSensitiveInfoMethod);

        var secretCmd = "docker run --env PASS=mypassword --env DB_KEY=\"secretKey\" --env OTHER_SECRET='someSecret' --env REGULAR_VAR=hello";
        var expectedSecretCmd = "docker run --env PASS=*** --env DB_KEY=\"***\" --env OTHER_SECRET='***' --env REGULAR_VAR=hello";
        var resultSecret = (string)scrubSecretsMethod.Invoke(null, new object[] { secretCmd })!;
        Assert.Equal(expectedSecretCmd, resultSecret);

        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var testPath = Path.Combine(homePath, "some", "sensitive", "file.txt");
        var resultSensitive = (string)scrubSensitiveInfoMethod.Invoke(null, new object[] { testPath })!;
        Assert.Contains("~", resultSensitive, StringComparison.Ordinal);
        Assert.DoesNotContain(homePath, resultSensitive, StringComparison.Ordinal);

        var username = Environment.UserName;
        if (!string.IsNullOrEmpty(username))
        {
            var userPath = $"/var/users/{username}/data";
            var resultUser = (string)scrubSensitiveInfoMethod.Invoke(null, new object[] { userPath })!;
            Assert.DoesNotContain(username, resultUser, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("***", resultUser, StringComparison.Ordinal);
        }

        var uncPath = @"\\host-name\share-name\folder\file.txt";
        var resultUnc = (string)scrubSensitiveInfoMethod.Invoke(null, new object[] { uncPath })!;
        Assert.Equal("[REDACTED_UNC_SHARE]", resultUnc);

        var awsKeyMsg = "Error: AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE failed";
        var resultCloudKey = (string)scrubSensitiveInfoMethod.Invoke(null, new object[] { awsKeyMsg })!;
        Assert.Contains("[REDACTED_KEY]", resultCloudKey, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", resultCloudKey, StringComparison.Ordinal);

        var logMessage = "Connected to 192.168.1.100 and 2001:0db8:85a3:0000:0000:8a2e:0370:7334 (node.local)";
        var resultLog = (string)scrubSensitiveInfoMethod.Invoke(null, new object[] { logMessage })!;
        Assert.DoesNotContain("192.168.1.100", resultLog, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:0db8:85a3:0000:0000:8a2e:0370:7334", resultLog, StringComparison.Ordinal);
        Assert.DoesNotContain("node.local", resultLog, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_NET_ADDR]", resultLog, StringComparison.Ordinal);
    }

    [Fact]
    public void FindLibraryForUnit_SuccessfullyResolvesLibrary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_lib_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectJson = @"{
  ""GHDL_Libraries"": [ ""neorv32"", ""iceduino"" ],
  ""GHDL-LIB_neorv32"": [
    ""rtl/core/neorv32_cpu.vhd"",
    ""rtl/core/neorv32_top.vhd""
  ],
  ""GHDL-LIB_iceduino"": [
    ""osflow/boards/iceduino/neorv32_iceduino_top.vhd""
  ]
}";
            File.WriteAllText(Path.Combine(tempDir, "test.fpgaproj"), projectJson);

            var type = typeof(DockerCommandBuilder);
            var method = type.GetMethod("FindLibraryForUnit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var libNeorv32 = (string?)method.Invoke(null, new object[] { tempDir, "neorv32_top" });
            Assert.Equal("neorv32", libNeorv32);

            var libIceduino = (string?)method.Invoke(null, new object[] { tempDir, "neorv32_iceduino_top" });
            Assert.Equal("iceduino", libIceduino);

            var libNotFound = (string?)method.Invoke(null, new object[] { tempDir, "non_existent_entity" });
            Assert.Null(libNotFound);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildContainerParameters_GhdlMakeWithLibraryAutoDetection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_make_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectJson = @"{
  ""GHDL-LIB_iceduino"": [
    ""osflow/boards/iceduino/neorv32_iceduino_top.vhd""
  ]
}";
            File.WriteAllText(Path.Combine(tempDir, "test.fpgaproj"), projectJson);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("-m"),
                    new TestCommandArgument("--workdir=build"),
                    new TestCommandArgument("neorv32_iceduino_top")
                }
            };

            var param = DockerCommandBuilder.BuildContainerParameters(
                "img", command, null!, null, null, (c, l) => { });

            var shellCmd = string.Join(" ", param.Cmd!);
            Assert.Equal("ghdl -m --work=iceduino --workdir=/workspace/build neorv32_iceduino_top", shellCmd);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildContainerParameters_GhdlMakeWithIncorrectLibraryOption_OverridesWithCorrectLibrary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_override_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectJson = @"{
  ""GHDL-LIB_iceduino"": [
    ""osflow/boards/iceduino/neorv32_iceduino_top.vhd""
  ]
}";
            File.WriteAllText(Path.Combine(tempDir, "test.fpgaproj"), projectJson);

            // Command passing incorrect library option: --work=neorv32
            var command1 = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("-m"),
                    new TestCommandArgument("--work=neorv32"),
                    new TestCommandArgument("neorv32_iceduino_top")
                }
            };

            var param1 = DockerCommandBuilder.BuildContainerParameters(
                "img", command1, null!, null, null, (c, l) => { });

            Assert.Equal("ghdl -m --work=iceduino neorv32_iceduino_top", string.Join(" ", param1.Cmd!));

            // Command passing incorrect library option: --work neorv32 (separate arguments)
            var command2 = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("-m"),
                    new TestCommandArgument("--work"),
                    new TestCommandArgument("neorv32"),
                    new TestCommandArgument("neorv32_iceduino_top")
                }
            };

            var param2 = DockerCommandBuilder.BuildContainerParameters(
                "img", command2, null!, null, null, (c, l) => { });

            Assert.Equal("ghdl -m --work iceduino neorv32_iceduino_top", string.Join(" ", param2.Cmd!));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildContainerParameters_GhdlSynthWithLibraryAutoDetection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_synth_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectJson = @"{
  ""GHDL-LIB_iceduino"": [
    ""osflow/boards/iceduino/neorv32_iceduino_top.vhd""
  ]
}";
            File.WriteAllText(Path.Combine(tempDir, "test.fpgaproj"), projectJson);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("--synth"),
                    new TestCommandArgument("--std=08"),
                    new TestCommandArgument("--workdir=build"),
                    new TestCommandArgument("neorv32_iceduino_top")
                }
            };

            var param = DockerCommandBuilder.BuildContainerParameters(
                "img", command, null!, null, null, (c, l) => { });

            var shellCmd = string.Join(" ", param.Cmd!);
            Assert.Equal("ghdl --synth --work=iceduino --std=08 --workdir=/workspace/build neorv32_iceduino_top", shellCmd);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FindLibraryForUnit_NormalizesWindowsBackslashes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_backslash_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectJson = @"{
  ""GHDL-LIB_neorv32"": [
    ""rtl\\core\\neorv32_cpu.vhd""
  ]
}";
            File.WriteAllText(Path.Combine(tempDir, "test.fpgaproj"), projectJson);

            var type = typeof(DockerCommandBuilder);
            var method = type.GetMethod("FindLibraryForUnit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var lib = (string?)method.Invoke(null, new object[] { tempDir, "neorv32_cpu" });
            Assert.Equal("neorv32", lib);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildContainerParameters_GhdlSynthWithTrailingOptions_CorrectlyResolvesUnitName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_trailing_opts_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectJson = @"{
  ""GHDL-LIB_iceduino"": [
    ""osflow/boards/iceduino/neorv32_iceduino_top.vhd""
  ]
}";
            File.WriteAllText(Path.Combine(tempDir, "test.fpgaproj"), projectJson);

            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("--synth"),
                    new TestCommandArgument("neorv32_iceduino_top"),
                    new TestCommandArgument("-o"),
                    new TestCommandArgument("output.v")
                }
            };

            var param = DockerCommandBuilder.BuildContainerParameters(
                "img", command, null!, null, null, (c, l) => { });

            var shellCmd = string.Join(" ", param.Cmd!);
            Assert.Equal("ghdl --synth --work=iceduino neorv32_iceduino_top -o output.v", shellCmd);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildContainerParameters_GhdlElabWithFileAndEntityName_ResolvesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ghdl_elab_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var projectJson = @"{
  ""GHDL-LIB_neorv32"": [
    ""rtl/core/neorv32_top.vhd""
  ]
}";
            File.WriteAllText(Path.Combine(tempDir, "test.fpgaproj"), projectJson);

            // Test case 1: Old style where file path is passed to ghdl -e
            var command1 = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("-e"),
                    new TestCommandArgument("rtl/core/neorv32_top.vhd")
                }
            };
            var param1 = DockerCommandBuilder.BuildContainerParameters("img", command1, null!, null, null, (c, l) => { });
            Assert.Equal("ghdl -e --work=neorv32 neorv32_top", string.Join(" ", param1.Cmd!));

            // Test case 2: New style where pure entity name is passed to ghdl -e
            var command2 = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "ghdl",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument("-e"),
                    new TestCommandArgument("neorv32_top")
                }
            };
            var param2 = DockerCommandBuilder.BuildContainerParameters("img", command2, null!, null, null, (c, l) => { });
            Assert.Equal("ghdl -e --work=neorv32 neorv32_top", string.Join(" ", param2.Cmd!));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RegistryClient_ParseImageReference_HandlesDigestsWithTagsCorrectly()
    {
        var result1 = ContainerExtension.Registry.RegistryClient.ParseImageReference("ubuntu:latest@sha256:45b23d8157d0e1b6567d0e1b6567d0e1b6567d0e1b6567d0e1b6567d0e1b6567");
        Assert.Equal("", result1.Registry);
        Assert.Equal("", result1.Namespace);
        Assert.Equal("ubuntu", result1.Repository);

        var result2 = ContainerExtension.Registry.RegistryClient.ParseImageReference("registry-1.docker.io/library/ubuntu:latest@sha256:45b23d8157d0e1b6567d0e1b6567d0e1b6567d0e1b6567d0e1b6567d0e1b6567");
        Assert.Equal("registry-1.docker.io", result2.Registry);
        Assert.Equal("library", result2.Namespace);
        Assert.Equal("ubuntu", result2.Repository);
    }

    [Fact]
    public void RegistryClient_ScrubSecrets_ScrubsTokensAndProfiles()
    {
        var method = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("ScrubSecrets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // Test token scrubbing
        var tokenInput = "GET /v2/token=abc123xyz HTTP/1.1";
        var tokenResult = method.Invoke(null, new object[] { tokenInput }) as string;
        Assert.Equal("GET /v2/token=*** HTTP/1.1", tokenResult);

        // Test user profile home directory scrubbing
        var homeField = typeof(ContainerExtension.Registry.RegistryClient).GetField("CachedUserProfile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(homeField);
        var originalHome = homeField.GetValue(null) as string;
        if (!string.IsNullOrEmpty(originalHome))
        {
            var homeInput = $"File path: {originalHome}/settings.json";
            var homeResult = method.Invoke(null, new object[] { homeInput }) as string;
            Assert.Equal("File path: ~/settings.json", homeResult);
        }

        // Test username scrubbing
        var userField = typeof(ContainerExtension.Registry.RegistryClient).GetField("CachedUserName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(userField);
        var originalUser = userField.GetValue(null) as string;
        if (!string.IsNullOrEmpty(originalUser))
        {
            var userInput = $"User is {originalUser}";
            var userResult = method.Invoke(null, new object[] { userInput }) as string;
            if (originalUser.Length >= 3)
            {
                Assert.Equal("User is ***", userResult);
            }
            else
            {
                Assert.Equal($"User is {originalUser}", userResult);
            }
        }
    }

    [Fact]
    public void DockerCommandBuilder_MemoryLimitClamp_ClampsBelow6MB()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.MemoryLimitSetting, 4.0); // 4MB

        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace",
            CommandArguments = new List<ICommandArgument>()
        };

        var param = DockerCommandBuilder.BuildContainerParameters("img", command, settings, null, null, (c, l) => { });
        Assert.Equal(6 * 1024 * 1024, param.HostConfig.Memory); // Clamped to 6MB
    }

    [Fact]
    public void DockerCommandBuilder_CpuLimitNanoCPUs_RoundsCorrectly()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.CpuLimitSetting, 0.5); // 0.5 cores

        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace",
            CommandArguments = new List<ICommandArgument>()
        };

        var param = DockerCommandBuilder.BuildContainerParameters("img", command, settings, null, null, (c, l) => { });
        Assert.Equal(500000000L, param.HostConfig.NanoCPUs); // Rounded/scaled core limit
    }

    [Fact]
    public void DockerCommandBuilder_BuildContainerParameters_PreventsDirectoryTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"traversal_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var outsideDirName = $"outside_dir_{Guid.NewGuid():N}";
        var outsidePath = Path.GetFullPath(Path.Combine(tempDir, "..", outsideDirName));

        try
        {
            var command = new ToolCommand
            {
                Executable = "ghdl",
                ToolName = "test",
                WorkingDirectory = tempDir,
                CommandArguments = new List<ICommandArgument>
                {
                    new TestCommandArgument($"../{outsideDirName}/output.v")
                }
            };

            _ = DockerCommandBuilder.BuildContainerParameters("img", command, null!, null, null, (c, l) => { });

            Assert.False(Directory.Exists(outsidePath), "The outside directory traversal path should not have been created.");
        }
        finally
        {
            Directory.Delete(tempDir, true);
            if (Directory.Exists(outsidePath))
            {
                Directory.Delete(outsidePath, true);
            }
        }
    }

    [Fact]
    public void DockerCommandBuilder_MapPathToContainerInternal_MapsPathsCorrectly()
    {
        var method = typeof(DockerCommandBuilder).GetMethod("MapPathToContainerInternal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var workingDir = HostAbs("work/Projects/MyProject");
        var path1 = HostAbs("work/Projects/MyProject/build/out.json");
        var result1 = method.Invoke(null, new object[] { path1, workingDir }) as string;
        Assert.Equal("/workspace/build/out.json", result1);

        var path2 = "src/main.v";
        var result2 = method.Invoke(null, new object[] { path2, workingDir }) as string;
        Assert.Equal("/workspace/src/main.v", result2);
    }

    [Fact]
    public async Task RegistryClient_ResolveDnsAsync_EvictsCacheWhenFull()
    {
        var cacheField = typeof(ContainerExtension.Registry.RegistryClient).GetField("DnsCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(cacheField);
        var dnsCache = (System.Collections.Concurrent.ConcurrentDictionary<string, (System.Net.IPAddress[] ips, long cacheTimeTicks)>)cacheField.GetValue(null)!;

        dnsCache.Clear();
        for (int i = 0; i < 50; i++)
        {
            dnsCache[$"host{i}.com"] = (Array.Empty<System.Net.IPAddress>(), Environment.TickCount64);
        }

        var method = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("ResolveDnsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var task = method.Invoke(null, new object[] { "localhost", CancellationToken.None }) as Task<System.Net.IPAddress[]>;
        Assert.NotNull(task);
        _ = await task.ConfigureAwait(true);

        Assert.Equal(46, dnsCache.Count);
        Assert.True(dnsCache.ContainsKey("localhost"));
    }

    [Fact]
    public void ContainerTelemetry_CountLinesSafe_CountsCorrectly()
    {
        var method = typeof(ContainerExtension.ContainerTelemetry).GetMethod("CountLinesSafe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var resultNonExistent = method.Invoke(null, new object[] { "does-not-exist.txt" });
        Assert.Equal(0, resultNonExistent);

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "line1\nline2\nline3\n");
            var resultCount = method.Invoke(null, new object[] { tempFile });
            Assert.Equal(3, resultCount);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ContainerTelemetry_ReadLastLinesSafe_ReadsCorrectly()
    {
        var method = typeof(ContainerExtension.ContainerTelemetry).GetMethod("ReadLastLinesSafe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var resultNonExistent = method.Invoke(null, new object[] { "does-not-exist.txt", 10 }) as List<string>;
        Assert.NotNull(resultNonExistent);
        Assert.Empty(resultNonExistent);

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "line1\nline2\nline3\nline4\nline5\n");
            var resultLines = method.Invoke(null, new object[] { tempFile, 3 }) as List<string>;
            Assert.NotNull(resultLines);
            Assert.Equal(3, resultLines.Count);
            Assert.Equal("line3", resultLines[0]);
            Assert.Equal("line4", resultLines[1]);
            Assert.Equal("line5", resultLines[2]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [FactIfNoCI("Live GitHub Releases API call; unauthenticated rate limits are exhausted on shared CI-runner egress IPs. Runs locally only.")]
    public async Task GitHubReleaseClient_GetLatestReleaseTagAsync_RetrievesValidTag()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tag = await ContainerExtension.Services.GitHubReleaseClient.GetLatestReleaseTagAsync(cts.Token);
            Assert.NotNull(tag);
            Assert.NotEmpty(tag);
            Assert.StartsWith("20", tag, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is System.Net.Sockets.SocketException || ex is TaskCanceledException || ex is System.IO.IOException || ex is InvalidOperationException)
        {
            // Tolerate offline/rate-limited runs locally: ThrowIfRateLimited surfaces a 403 as InvalidOperationException.
            // In CI the test is skipped outright (FactIfNoCI) so the live call never runs.
        }
    }

    [Fact]
    public void DockerCommandBuilder_ParseEnvFile_DeduplicatesKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"env_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var envFile = Path.Combine(tempDir, ".env");
        try
        {
            File.WriteAllText(envFile, "FOO=bar\nBAZ=qux\nFOO=override\n");
            var result = DockerCommandBuilder.ParseEnvFile(tempDir);
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Contains("FOO=override", result);
            Assert.Contains("BAZ=qux", result);
            Assert.Single(result, s => s.StartsWith("FOO=", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ContainerTelemetry_IsSubpath_ResolvesCanonicalSymlinks()
    {
        var method = typeof(ContainerExtension.ContainerTelemetry).GetMethod("IsSubpath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var tempDir = Path.Combine(Path.GetTempPath(), $"telemetry_sym_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var subDir = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(subDir);

        try
        {
            var isSub1 = method.Invoke(null, new object[] { subDir, tempDir }) as bool?;
            Assert.True(isSub1);

            var fileInSub = Path.Combine(subDir, "telemetry.json");
            var isSub2 = method.Invoke(null, new object[] { fileInSub, tempDir }) as bool?;
            Assert.True(isSub2);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DockerCommandBuilder_BuildContainerParameters_ClampsToRemoteCpuCores()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.CpuLimitSetting, 16.0); // Configure 16 cores

        var command = new ToolCommand
        {
            Executable = "ghdl",
            ToolName = "test",
            WorkingDirectory = "/workspace",
            CommandArguments = new List<ICommandArgument>()
        };

        var param1 = DockerCommandBuilder.BuildContainerParameters("img", command, settings, null, null, (c, l) => { }, 8.0);
        Assert.Equal(8000000000L, param1.HostConfig.NanoCPUs);

        var param2 = DockerCommandBuilder.BuildContainerParameters("img", command, settings, null, null, (c, l) => { }, null);
        Assert.Equal(16000000000L, param2.HostConfig.NanoCPUs);
    }

    [Fact]
    public void ContainerExtensionModule_ValidateRuntimePath_AcceptsEmptyOrValidExecutables()
    {
        var method = typeof(ContainerExtensionModule).GetMethod("ValidateRuntimePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var resultEmpty = method.Invoke(null, new object[] { "" }) as bool?;
        Assert.True(resultEmpty);

        var selfPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(selfPath))
        {
            var resultSelf = method.Invoke(null, new object[] { selfPath }) as bool?;
            Assert.True(resultSelf);
        }
    }

    [Fact]
    public void RegistryClient_ChallengeParameterRegex_ParsesCommasInQuotes()
    {
        var method = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("ChallengeParameterRegex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var regex = method.Invoke(null, null) as System.Text.RegularExpressions.Regex;
        Assert.NotNull(regex);

        var headerParam = "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:samalba/my-app:pull,push\"";
        var matches = regex.Matches(headerParam);
        Assert.Equal(3, matches.Count);

        string? realm = null, service = null, scope = null;
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var key = match.Groups["key"].Value;
            var val = match.Groups["value"].Value;
            if (key == "realm") realm = val;
            else if (key == "service") service = val;
            else if (key == "scope") scope = val;
        }

        Assert.Equal("https://auth.docker.io/token", realm);
        Assert.Equal("registry.docker.io", service);
        Assert.Equal("repository:samalba/my-app:pull,push", scope);
    }

    [Fact]
    public async Task RegistryClient_FetchTagsAsync_LinkedTokenSource_CancelsImmediately()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ContainerExtension.Registry.RegistryClient.FetchTagsAsync("ubuntu", cts.Token);
        });
    }

    [Fact]
    public async Task RegistryClient_SendWithRetryAsync_ImmediateAbortOnCancellation()
    {
        var method = typeof(ContainerExtension.Registry.RegistryClient).GetMethod("SendWithRetryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var task = (Task<HttpResponseMessage>)method.Invoke(null, new object[] { req, cts.Token })!;
            await task;
        });
    }

    [Fact]
    public void FindExecutableInPath_ResolvesGitOnSystem()
    {
        var gitPath = DockerExecutionStrategy.FindExecutableInPath("git");
        Assert.NotNull(gitPath);
        Assert.True(File.Exists(gitPath));
    }

    [Fact]
    public void Telemetry_RespectsLogLevelOff()
    {
        ContainerTelemetry.LogLevelChecker = () => "Off";
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.LogExecution("test-image", "test-tool", 1.5, 0);
        ContainerTelemetry.TrackError("test-component", "test-action", new InvalidOperationException("test exception"));

        System.Threading.Thread.Sleep(200);

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.Empty(entries);

        var errorLogFile = Path.Combine(_testTelemetryDir, "container_errors.jsonl");
        if (File.Exists(errorLogFile))
        {
            var errorsContent = File.ReadAllText(errorLogFile);
            Assert.True(string.IsNullOrWhiteSpace(errorsContent));
        }
    }

    [Fact]
    public void Telemetry_RespectsLogLevelErrorsOnly()
    {
        ContainerTelemetry.LogLevelChecker = () => "Errors Only";
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.LogExecution("test-image", "test-tool-success", 1.5, 0);
        ContainerTelemetry.LogExecution("test-image", "test-tool-fail", 1.5, 1, errorMessage: "Failed");
        ContainerTelemetry.TrackError("test-component", "test-action", new InvalidOperationException("test exception"));

        System.Threading.Thread.Sleep(200);

        var entries = ContainerTelemetry.GetRecentEntries(10);
        Assert.NotEmpty(entries);
        Assert.DoesNotContain(entries, e => e.Tool != null && e.Tool.Equals("test-tool-success", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Tool != null && e.Tool.Equals("test-tool-fail", StringComparison.Ordinal));

        var errorLogFile = Path.Combine(_testTelemetryDir, "container_errors.jsonl");
        Assert.True(File.Exists(errorLogFile));
        var errorsContent = File.ReadAllText(errorLogFile);
        Assert.Contains("test exception", errorsContent);
    }

    [Fact]
    public void Telemetry_OmitsStackTraceOnNonVerbose()
    {
        ContainerTelemetry.LogLevelChecker = () => "Info";
        ContainerTelemetry.ClearEntries();

        ContainerTelemetry.TrackError("test-component", "test-action", new InvalidOperationException("test stack trace exception"));

        System.Threading.Thread.Sleep(200);

        var errorLogFile = Path.Combine(_testTelemetryDir, "container_errors.jsonl");
        Assert.True(File.Exists(errorLogFile));
        var errorsContent = File.ReadAllText(errorLogFile);
        Assert.Contains("test stack trace exception", errorsContent);
        Assert.DoesNotContain("\"stack\"", errorsContent);
    }

    [Fact]
    public async Task DockerExecutionStrategy_FallsBackToNative_WhenOfflineAndAllowed()
    {
        using var provider = new TestServiceProvider();
        var settings = (MockSettingsService)provider.GetService(typeof(ISettingsService))!;

        settings.SetSettingValue(ContainerExtensionModule.AllowNativeFallbackSetting, true);
        settings.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, "unix:///invalid/offline/socket.sock");

        using var strategy = new DockerExecutionStrategy(provider);

        var outputLines = new List<string>();
        var command = new ToolCommand
        {
            Executable = "git",
            ToolName = "git",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument("--version") },
            OutputHandler = line => { outputLines.Add(line); return true; }
        };

        var (success, output) = await strategy.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(success);
        Assert.NotEmpty(outputLines);
        Assert.Contains(outputLines, line => line.Contains("git version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCanonicalPath_CircularSymlink_ThrowsDockerExecutionException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ContainerExtensionTests_Circular_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var pathA = Path.Combine(tempDir, "linkA");
            var pathB = Path.Combine(tempDir, "linkB");

            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(pathA, pathB);
                File.CreateSymbolicLink(pathB, pathA);

                var method = typeof(DockerExecutionStrategy).GetMethod("GetCanonicalPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.NotNull(method);

                var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object[] { pathA }));
                Assert.IsType<DockerExecutionException>(ex.InnerException);
                Assert.Contains("Circular", ex.InnerException.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void GetCanonicalPath_NonExistentSuffix_ResolvesLongestAncestor()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ContainerExtensionTests_Ancestor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var method = typeof(DockerExecutionStrategy).GetMethod("GetCanonicalPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            var canonicalTempDir = (string)method.Invoke(null, new object[] { tempDir })!;

            var testPath = Path.Combine(tempDir, "nonexistent", "subdir");
            var expectedPath = Path.Combine(canonicalTempDir, "nonexistent", "subdir");

            var result = (string)method.Invoke(null, new object[] { testPath })!;
            Assert.Equal(expectedPath, result);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void GetCanonicalPath_SymlinkTraversalBypass_FailsToCanonicalizeCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ContainerExtensionTests_Bypass_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var symlinkPath = Path.Combine(tempDir, "symlink_to_private");
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(symlinkPath, "/private");

                var method = typeof(DockerExecutionStrategy).GetMethod("GetCanonicalPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.NotNull(method);

                var inputPath = Path.Combine(tempDir, "symlink_to_private", "..", "etc");
                var result = (string)method.Invoke(null, new object[] { inputPath })!;

                // The actual OS path of inputPath is /private/etc (because symlink_to_private points to /private, and its parent is /)
                // But if the resolver resolves it textually first, it returns tempDir/etc.
                Assert.NotEqual(Path.Combine(tempDir, "etc"), result);
                var expectedPath = OperatingSystem.IsMacOS() ? "/private/etc" : "/etc";
                Assert.Equal(expectedPath, result);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
#pragma warning restore CA1305, CA1307, CA1031, CA1822, CS8019, CA1308
}


#pragma warning disable CA1822
internal sealed class MockSettingsService : ISettingsService
{
    private readonly Dictionary<string, object> _settings = new(StringComparer.Ordinal)
    {
        [ContainerExtensionModule.AutoRemoveSetting] = true,
        [ContainerExtensionModule.DefaultImageSetting] = ContainerExtensionModule.FallbackImage,
        [ContainerExtensionModule.DockerRuntimePathSetting] = "",
        [ContainerExtensionModule.MemoryLimitSetting] = 0.0,
        [ContainerExtensionModule.CpuLimitSetting] = 0.0,
        [ContainerExtensionModule.DaemonSocketSetting] = "",
        [ContainerExtensionModule.PlatformSetting] = "auto",
        [ContainerExtensionModule.TimeoutSetting] = 0.0,
        [ContainerExtensionModule.NetworkModeSetting] = "bridge",
        [ContainerExtensionModule.LogLevelSetting] = "Verbose",
        [ContainerExtensionModule.ShowTimestampsSetting] = true,
        [ContainerExtensionModule.PullPolicySetting] = "if-not-present",
        [ContainerExtensionModule.ExtraFlagsSetting] = "",
        [ContainerExtensionModule.DashboardRefreshSetting] = "Manual",
        [ContainerExtensionModule.ContainerNamePrefixSetting] = "containerextension-",
        [ContainerExtensionModule.TelemetryRetentionSetting] = "100",
        [ContainerExtensionModule.AllowNativeFallbackSetting] = false
    };

    public event EventHandler<SaveEventArgs>? Saved = delegate { };

    public bool HasSetting(string key)
    {
        return _settings.ContainsKey(key);
    }

    public T GetSettingValue<T>(string key)
    {
        if (_settings.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }
        return default(T)!;
    }

    public void SetSettingValue(string key, object value)
    {
        _settings[key] = value;
    }

    public void RegisterSettingCategory(string category, int order, string? icon) { }
    public void RegisterSettingSubCategory(string category, string subCategory, int order, string? icon) { }
    public void RegisterSettingSubCategory(string category, string subCategory) { }
    public void Register<T>(string key, T setting) { }
    public IObservable<T> Bind<T>(string key, IObservable<T> observable) => observable;
    public void RegisterTitled<T>(string category, string subCategory, string key, string title, string description, T defaultValue) { }
    public void RegisterTitledFolderPath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator) { }
    public void RegisterTitledFilePath(string category, string subCategory, string key, string title, string description, string defaultPath, string? icon, string? placeholder, Func<string, bool>? validator, params Avalonia.Platform.Storage.FilePickerFileType[] fileTypes) { }
    public void RegisterTitledSlider(string category, string subCategory, string key, string title, string description, double defaultValue, double min, double max, double tick) { }
    public void RegisterTitledCombo<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    public void RegisterTitledComboSearch<T>(string category, string subCategory, string key, string title, string description, T defaultValue, params T[] options) { }
    public void RegisterTitledListBox(string category, string subCategory, string key, string title, string description, params string[] options) { }
    public void RegisterSetting(string category, string subCategory, string key, OneWare.Essentials.Models.TitledSetting setting) { }
    public void RegisterSetting(string category, string subCategory, string key, object settingModule) { }
    public void UpdateSetting(string key, OneWare.Essentials.Models.TitledSetting setting) { }
    public void RegisterCustom(string category, string subCategory, string key, OneWare.Essentials.Models.CustomSetting setting) { }
    public OneWare.Essentials.Models.Setting GetSetting(string key) => null!;
    public T[] GetComboOptions<T>(string key) => Array.Empty<T>();
    public IObservable<T> GetSettingObservable<T>(string key) => System.Reactive.Linq.Observable.Empty<T>();
    public void Load(string path) { }
    public void Save(string path, bool overrideExisting) { }
    public void WhenLoaded(Action action) { }
    public void Reset(string key) { }
    public void ResetAll() { }
}

internal sealed class TestCommandArgument : ICommandArgument
{
    private readonly string _argument;
    public TestCommandArgument(string argument)
    {
        _argument = argument;
    }
    public void Prepare(System.Runtime.InteropServices.OSPlatform osPlatform, Func<string, string>? pathMapper = null) { }
    public string GetArgument() => _argument;
}

internal sealed class TestServiceProvider : IServiceProvider, IDisposable
{
    private readonly ISettingsService _settingsService = new MockSettingsService();

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ISettingsService))
        {
            return _settingsService;
        }
        return null;
    }

    public void Dispose()
    {
    }
}
