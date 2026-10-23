using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Daemon-free coverage for <see cref="DockerRunCommandFormatter"/>: the settings-driven template and
/// the exact-command reconstruction, quoted for a POSIX shell and for PowerShell, including the
/// environment-value masking that keeps secrets out of the persisted telemetry log.
/// </summary>
public sealed class DockerRunCommandFormatterTests
{
    // Words a shell would change or split if they went unquoted, and a few it would leave alone.
    public static readonly TheoryData<string> AwkwardWords = new()
    {
        "$HOME_FILE.v", "`id`", "$(id)", "it's", "it\u2019s", "\u2018\u201A\u201B", "say \"hi\"", "a b", "", "größe.vhd", "日本",
        "-p", "8080:80", "-a.b", "--%", "@x", "=x", "1kb", "0x10", "a;b", "a&b", "a|b", "a#b", "~/x",
        "*.vhd", "[ab]", "{a,b}", "a,b", "back\\slash", "tab\there", "new\nline", "C:\\Users\\me\\proj:/workspace",
        "-", "--", "plain.vhd",
    };

    private static CreateContainerParameters Build(string extraLabels, ToolCommand? command = null)
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, extraLabels);
        command ??= new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>(),
        };
        return DockerCommandBuilder.BuildContainerParameters("img", command, settings, "1000", "1000", (_, _) => { });
    }

    [Fact]
    public void Generate_RendersExtraFlagsAsLabels()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, "--label custom=val");

        var runCommand = DockerRunCommandFormatter.Generate(settings, "docker", "myimage");

        Assert.StartsWith("docker run", runCommand);
        Assert.Contains("--label custom=val", runCommand);
        Assert.Contains("myimage <tool> <args>", runCommand);
    }

    [Fact]
    public void Generate_PublishesThePortOfTheExtraLabels_InsteadOfLabellingTheFlag()
    {
        var settings = new MockSettingsService();
        settings.SetSettingValue(ContainerExtensionModule.ExtraFlagsSetting, "-p 8080:80 team=fpga");

        var runCommand = DockerRunCommandFormatter.Generate(settings, "docker", "myimage", CommandShell.Posix);

        Assert.Contains(" -p 127.0.0.1:8080:80/tcp", runCommand, StringComparison.Ordinal);
        Assert.Contains(" --label team=fpga", runCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("--label -p", runCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("--label 8080:80", runCommand, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generate_CarriesTheHardeningFlagsOfARun(bool powerShell)
    {
        var shell = powerShell ? CommandShell.PowerShell : CommandShell.Posix;
        var runCommand = DockerRunCommandFormatter.Generate(new MockSettingsService(), "docker", "myimage", shell);

        Assert.Contains(" --cap-drop ALL", runCommand, StringComparison.Ordinal);
        Assert.Contains(" --security-opt no-new-privileges:true", runCommand, StringComparison.Ordinal);
        Assert.Contains(" --pids-limit 4096", runCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_ForPowerShell_MountsTheCurrentDirectoryWithoutPosixSyntax()
    {
        var runCommand = DockerRunCommandFormatter.Generate(new MockSettingsService(), "docker", "myimage", CommandShell.PowerShell);

        Assert.Contains(" -v \"${PWD}:/workspace\" -w /workspace", runCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("$(id -u)", runCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconstruct_MasksEnvValuesByDefault()
    {
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            Env = new List<string> { "LICENSE_KEY=super-secret-value" },
        };

        var masked = DockerRunCommandFormatter.Reconstruct(p, "docker");

        Assert.Contains("LICENSE_KEY=********", masked);
        Assert.DoesNotContain("super-secret-value", masked);
    }

    [Fact]
    public void Reconstruct_RendersRealEnvValuesWhenMaskingDisabled()
    {
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            Env = new List<string> { "LICENSE_KEY=super-secret-value" },
        };

        var verbatim = DockerRunCommandFormatter.Reconstruct(p, "docker", maskEnvValues: false);

        Assert.Contains("LICENSE_KEY=super-secret-value", verbatim);
    }

    [Fact]
    public void Reconstruct_QuotesArgumentsWithShellMetacharacters()
    {
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            Cmd = new List<string> { "sh", "-c", "echo hi; rm -rf /" },
        };

        var command = DockerRunCommandFormatter.Reconstruct(p, "docker", shell: CommandShell.Posix);

        Assert.Contains(" 'echo hi; rm -rf /'", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$HOME_FILE.v", "'$HOME_FILE.v'")]
    [InlineData("`id`", "'`id`'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("a b", "'a b'")]
    [InlineData("", "''")]
    [InlineData("größe.vhd", "'größe.vhd'")]
    [InlineData("=x", "'=x'")]
    [InlineData("8080:80", "8080:80")]
    [InlineData("plain.vhd", "plain.vhd")]
    public void Quote_ForPosix(string word, string expected)
    {
        Assert.Equal(expected, DockerRunCommandFormatter.Quote(word, CommandShell.Posix));
    }

    [Theory]
    [InlineData("$HOME_FILE.v", "'$HOME_FILE.v'")]
    [InlineData("`id`", "'`id`'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("it\u2019s", "'it\u2019\u2019s'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("a b", "'a b'")]
    [InlineData("", "''")]
    [InlineData("größe.vhd", "'größe.vhd'")]
    [InlineData("-a.b", "'-a.b'")]
    [InlineData("--%", "@('--%')")]
    [InlineData("@x", "'@x'")]
    [InlineData("--cap-drop", "--cap-drop")]
    [InlineData("4096", "4096")]
    [InlineData("C:\\Users\\me\\proj:/workspace", "C:\\Users\\me\\proj:/workspace")]
    [InlineData("plain.vhd", "plain.vhd")]
    public void Quote_ForPowerShell(string word, string expected)
    {
        Assert.Equal(expected, DockerRunCommandFormatter.Quote(word, CommandShell.PowerShell));
    }

    [Theory]
    [InlineData(false, "'$HOME_FILE.v' '`id`' ''")]
    [InlineData(true, "'$HOME_FILE.v' '`id`' ''")]
    public void Reconstruct_QuotesEveryArgument(bool powerShell, string expectedTail)
    {
        var shell = powerShell ? CommandShell.PowerShell : CommandShell.Posix;
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            Cmd = new List<string> { "$HOME_FILE.v", "`id`", "" },
        };

        var command = DockerRunCommandFormatter.Reconstruct(p, "docker", shell: shell);

        Assert.EndsWith(" myimage " + expectedTail, command, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconstruct_KeepsABindVerbatim_QuotedAsOneWord()
    {
        var p = new CreateContainerParameters
        {
            Image = "myimage",
            HostConfig = new HostConfig { Binds = new List<string> { "/tmp/my \"proj\"\\x:/workspace:ro" } },
        };

        var command = DockerRunCommandFormatter.Reconstruct(p, "docker", shell: CommandShell.Posix);

        Assert.Contains(" -v '/tmp/my \"proj\"\\x:/workspace:ro'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconstruct_ForPowerShell_CallsAQuotedRuntimePathWithTheCallOperator()
    {
        var p = new CreateContainerParameters { Image = "myimage" };

        var command = DockerRunCommandFormatter.Reconstruct(p, "C:\\Program Files\\Docker\\docker.exe", shell: CommandShell.PowerShell);

        Assert.StartsWith("& 'C:\\Program Files\\Docker\\docker.exe' run", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconstruct_OfABuiltRun_CarriesItsHardeningFlags()
    {
        var command = DockerRunCommandFormatter.Reconstruct(Build(""), "docker", shell: CommandShell.Posix);

        Assert.Contains(" --cap-drop ALL", command, StringComparison.Ordinal);
        Assert.Contains(" --security-opt no-new-privileges:true", command, StringComparison.Ordinal);
        Assert.Contains(" --pids-limit 4096", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconstruct_OfABuiltRun_PublishesExposesAndLabelsLikeTheRun_WithoutTheExtensionsOwnLabel()
    {
        var tool = new ToolCommand
        {
            Executable = "yosys",
            ToolName = "yosys",
            WorkingDirectory = "/workspace/dir",
            CommandArguments = new List<ICommandArgument>(),
            ExposedPorts = [new ToolPort(9000)],
        };

        var command = DockerRunCommandFormatter.Reconstruct(Build("-p 8080:80 team=fpga", tool), "docker", shell: CommandShell.Posix);

        Assert.Contains(" -p 127.0.0.1:8080:80/tcp", command, StringComparison.Ordinal);
        Assert.Contains(" --expose 9000/tcp", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--expose 80/tcp", command, StringComparison.Ordinal);
        Assert.Contains(" --label team=fpga", command, StringComparison.Ordinal);
        Assert.DoesNotContain(ContainerExtensionModule.ContainerOwnerLabel, command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/bin/sh", "-c")]
    [InlineData("/bin/bash", "-c")]
    [InlineData("/bin/zsh", "-fc")]
    public async Task PosixQuoting_IsReadBackByTheShellAsTheSameWords(string shellPath, string commandFlag)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(shellPath))
        {
            Assert.Skip($"{shellPath} is not on this host.");
        }
        var words = AwkwardWords.Select(row => (string)row.Data).ToList();
        var script = "set -- " + string.Join(' ', words.Select(w => DockerRunCommandFormatter.Quote(w, CommandShell.Posix)))
            + "; " + HexEchoScript;

        var echoed = await RunAsync(shellPath, [commandFlag, script]);

        Assert.Equal(words, echoed);
    }

    [Fact]
    public async Task PowerShellQuoting_IsPassedToANativeCommandAsTheSameWords()
    {
        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh == null || OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Assert.Skip("pwsh, or /bin/sh to echo the words it passes, is not on this host.");
        }
        var words = AwkwardWords.Select(row => (string)row.Data).ToList();
        var script = "& /bin/sh -c " + DockerRunCommandFormatter.Quote(HexEchoScript, CommandShell.PowerShell) + " x "
            + string.Join(' ', words.Select(w => DockerRunCommandFormatter.Quote(w, CommandShell.PowerShell)));

        var echoed = await RunAsync(pwsh, ["-NoProfile", "-NonInteractive", "-Command", script]);

        Assert.Equal(words, echoed);
    }

    // Prints each positional argument as one line of hex, so no byte of it can be lost on the way back.
    private const string HexEchoScript = "for a in \"$@\"; do printf '%s' \"$a\" | od -An -tx1 | tr -d ' \\n'; echo; done";

    private static async Task<List<string>> RunAsync(string fileName, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} did not start.");
        var ct = TestContext.Current.CancellationToken;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        Assert.True(process.ExitCode == 0, $"{fileName} exited with {process.ExitCode}: {await stderr}");
        var lines = (await stdout).Split('\n');
        return lines.Take(lines.Length - 1).Select(FromHex).ToList();
    }

    private static string FromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
