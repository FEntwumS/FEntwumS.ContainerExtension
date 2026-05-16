using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ContainerExtension;
using ContainerExtension.Validations;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.Models;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Unit tests for the OneWare Container Extension.
/// Validates setting constants and the real validation classes used by the extension,
/// leveraging InternalsVisibleTo to test the actual validators directly.
/// </summary>
public class ContainerExtensionTests : IDisposable
{
    private readonly DockerImageFormatValidation _imageValidator = new();
    private readonly DaemonSocketValidation _socketValidator = new();
    private readonly ContainerNameValidation _nameValidator = new();
    private readonly string _testTelemetryDir;

    public ContainerExtensionTests()
    {
        // Isolate telemetry to a temporary directory strictly for this test lifecycle
        _testTelemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_testTelemetryDir);
    }

    public void Dispose()
    {
        // Clean up test environment physical files
        try
        {
            if (Directory.Exists(_testTelemetryDir))
                Directory.Delete(_testTelemetryDir, true);
        }
        catch { /* Best effort teardown */ }
        GC.SuppressFinalize(this);
    }

    // ── Smoke Test ──────────────────────────────────────────────────────

    [Fact]
    public void LoadLibrary()
    {
        var assembly = typeof(ContainerExtensionModule).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("ContainerExtension", assembly.GetName().Name);
    }

    // ── String Extensions — Edge Cases ──────────────────────────────────
    
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
    public void ShortId_ReturnsEmptyOnWhitespaceIfEmpty()
    {
        var result = "   ".ShortId();
        Assert.Equal("   ", result); // Preserves exact string structure up to 12
    }

    // ── Docker Image Format Validation ──────────────────────────────────

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

    // ── Daemon Socket URI Validation ────────────────────────────────────

    [Theory]
    [InlineData("", true)]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("tcp://127.0.0.1:2375", true)]
    [InlineData("npipe://./pipe/docker_engine", true)]
    [InlineData("http://localhost:2375", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("just-a-path", false)]
    public void DaemonSocketFormat_ValidatesCorrectly(string input, bool expectedValid)
    {
        var result = _socketValidator.Validate(input, out var warning);
        Assert.Equal(expectedValid, result);
        if (!expectedValid)
            Assert.NotNull(warning);
    }

    // ── Resource Threshold Logic ────────────────────────────────────────

    [Theory]
    [InlineData(0.0, true, false)]       // 0 = no limit, always valid, no warning
    [InlineData(2048.0, true, false)]    // Below 75% of 16384 — valid, no warning
    [InlineData(14000.0, true, true)]    // Above 75% of 16384 → valid with advisory warning
    [InlineData(16384.0, true, true)]    // 100% of total → valid with advisory warning
    [InlineData(20000.0, false, true)]   // Above total → rejected with error
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

    // ── Setting Constants ───────────────────────────────────────────────

    [Fact]
    public void SettingConstants_AreConsistentlyPrefixed()
    {
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DockerRuntimePathSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DefaultImageSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.MemoryLimitSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.CpuLimitSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.AutoRemoveSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DaemonSocketSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.PlatformSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.TimeoutSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.NetworkModeSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.LogLevelSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.ShowTimestampsSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.ContainerNamePrefixSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.TelemetryRetentionSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.PullPolicySetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.ExtraFlagsSetting);
        Assert.StartsWith("ContainerExtension_", ContainerExtensionModule.DashboardRefreshSetting);
        Assert.StartsWith("ContainerImage_", ContainerExtensionModule.PerToolImagePrefix);
    }

    [Fact]
    public void FallbackImage_IsValidDockerReference()
    {
        var fallback = ContainerExtensionModule.FallbackImage;
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        var result = _imageValidator.Validate(fallback, out _);
        Assert.True(result, $"FallbackImage '{fallback}' should pass the DockerImageFormatValidation.");
    }

    // ── Container Name Prefix Validation ────────────────────────────────

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

    // ── Container Name Prefix Length Limit ──────────────────────────────

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

    // ── Container Telemetry ─────────────────────────────────────────────

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
        Assert.Equal(2, totalRuns);
        Assert.Equal(50.0, successRate);

        ContainerTelemetry.ClearEntries();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DrainLines Tests (DockerExecutionStrategy — stream demultiplexer)
    // ═══════════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════════
    //  BuildContainerParameters Tests (DockerCommandBuilder)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildContainerParameters_BasicCommand_ProducesCorrectConfig()
    {
        var command = new ToolCommand { Executable = "ghdl", ToolName = "test", WorkingDirectory = "/workspace/dir", Arguments = new List<string> { "-a", "file.vhd" } };
        
        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest",
            command,
            null!, 
            "1000", "1000",
            (cmd, log) => { });

        Assert.Equal("test_image:latest", param.Image);
        Assert.Equal("/workspace", param.WorkingDir);
        Assert.NotNull(param.Cmd);
        Assert.Equal(3, param.Cmd.Count);
        Assert.Equal("sh", param.Cmd[0]);
        Assert.Equal("-c", param.Cmd[1]);
        Assert.Equal("ghdl -a file.vhd", param.Cmd[2]);

        Assert.NotNull(param.HostConfig);
        Assert.Contains(param.HostConfig.Binds, b => b.EndsWith(":/workspace", StringComparison.Ordinal));
        Assert.True(param.HostConfig.AutoRemove);
        Assert.Equal("bridge", param.HostConfig.NetworkMode);

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("1000:1000", param.User);
        }
    }

    [Fact]
    public void BuildContainerParameters_CommandWithSpecialCharacters_IsEscaped()
    {
        var command = new ToolCommand { Executable = "my_tool", ToolName = "test", WorkingDirectory = "/workspace", Arguments = new List<string> { "file with space.vhd", "part1;part2", "echo \"hello\"" } };
        
        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image:latest", command, null!, null, null, (c, l) => { });

        var shellCmd = param.Cmd![2];
        Assert.Contains("\"file with space.vhd\"", shellCmd);
        Assert.Contains("\"part1;part2\"", shellCmd);
        Assert.Contains("\"echo \\\"hello\\\"\"", shellCmd);
    }

    [Fact]
    public void BuildContainerParameters_NoArguments_BuildsCorrectly()
    {
        var command = new ToolCommand { Executable = "tool_only", ToolName = "test", WorkingDirectory = "/dir", Arguments = new List<string>() };
        var param = DockerCommandBuilder.BuildContainerParameters(
            "test_image", command, null!, null, null, (c, l) => { });

        Assert.Equal("tool_only", param.Cmd![2]);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ParseEnvFile Tests (DockerExecutionStrategy — .env parsing)
    // ═══════════════════════════════════════════════════════════════════════


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

    // ═══════════════════════════════════════════════════════════════════════
    //  Resource Profile Telemetry Tests (OOM Analyzer)
    // ═══════════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════════
    //  Telemetry Export Tests
    // ═══════════════════════════════════════════════════════════════════════

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
            Assert.Contains("\"tool\":\"tool\"", content);
            Assert.Contains("\"image\":\"img\"", content);
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

    // ═══════════════════════════════════════════════════════════════════════
    //  Setting Constants Value Correctness
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void SettingConstants_HaveExpectedValues()
    {
        // Verify critical constant values that other components depend on
        Assert.Equal("hdlc/ghdl:yosys", ContainerExtensionModule.FallbackImage);
        Assert.Equal("Container Dashboard", ContainerExtensionModule.DashboardTitle);
        Assert.Equal("#2496ED", ContainerExtensionModule.DockerBlueHex);
        Assert.StartsWith("M", ContainerExtensionModule.WhaleIconPath); // SVG path starts with M(ove)
        Assert.StartsWith("ContainerImage_", ContainerExtensionModule.PerToolImagePrefix);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Docker Image Format — Additional Edge Cases
    // ═══════════════════════════════════════════════════════════════════════

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
}
