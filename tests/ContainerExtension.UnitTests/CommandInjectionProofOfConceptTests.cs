using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Adversarial regression suite for the host-to-container command model in
/// <see cref="DockerCommandBuilder.BuildContainerParameters"/>.
///
/// History and rationale. An earlier design emitted every container invocation as
/// <c>["sh","-c", &lt;reconstructed string&gt;]</c>, reconstructing a shell line from the argument vector
/// and defending it with denylist quoting. Adversarial testing of that design (a 17-payload battery
/// executed through a real POSIX <c>/bin/sh</c>) refuted argument injection — the escape-and-quote
/// layer held — but confirmed that the executable was appended unquoted, so a malicious executable name
/// injected at the construction boundary (gated end-to-end only by a separate sanitizer in
/// <c>DockerExecutionStrategy.ExecuteAsync</c>). Rather than rely on the perpetual correctness of two
/// independent denylist/quoting layers, the builder was migrated to an argv/exec model: the command is
/// emitted as a token vector <c>[executable, ...mapped args]</c> and delivered to the program through
/// <c>execve</c> (the container entrypoint <c>tini</c> plus <c>HostConfig.Init</c>), with no shell in the
/// path. This eliminates the host-to-container shell-injection class by construction.
///
/// These tests assert the resulting safety invariant: an attacker-controlled token (a crafted source
/// filename, project path, or tool argument arriving from an untrusted FPGA project) occupies exactly
/// one argv slot, is never reinterpreted as shell syntax, and cannot execute an injected command. The
/// payload attempts to create a uniquely named sentinel file; its absence after execution is the proof.
/// </summary>
public sealed class CommandInjectionProofOfConceptTests
{
    private static IReadOnlyList<string> EmitArgv(string executable, string argument, string workingDir)
    {
        var command = new ToolCommand
        {
            Executable = executable,
            ToolName = "poc",
            WorkingDirectory = workingDir,
            CommandArguments = new List<ICommandArgument> { new TestCommandArgument(argument) },
        };
        var p = DockerCommandBuilder.BuildContainerParameters("img:latest", command, null!, null, null, (_, _) => { });
        Assert.NotNull(p.Cmd);
        return (IReadOnlyList<string>)p.Cmd;
    }

    private static IReadOnlyList<string> EmitArgvNoArgs(string executable, string workingDir)
    {
        var command = new ToolCommand
        {
            Executable = executable,
            ToolName = "poc",
            WorkingDirectory = workingDir,
            CommandArguments = Array.Empty<ICommandArgument>(),
        };
        var p = DockerCommandBuilder.BuildContainerParameters("img:latest", command, null!, null, null, (_, _) => { });
        return (IReadOnlyList<string>)p.Cmd!;
    }

    /// <summary>Executes the emitted command as a real argv process (no shell) and reports whether the
    /// sentinel file was created. A launch failure (the OS could not resolve argv[0] to a program) is
    /// itself proof that no shell interpreted the payload.</summary>
    private static bool RunArgvAndDidInject(IReadOnlyList<string> cmd, string workingDir, string sentinel)
    {
        var psi = new ProcessStartInfo(cmd[0])
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < cmd.Count; i++) psi.ArgumentList.Add(cmd[i]);

        try
        {
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            // argv[0] did not resolve to an executable; nothing ran, nothing was interpreted.
        }
        return File.Exists(Path.Combine(workingDir, sentinel));
    }

    private static string NewWorkingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "poc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static IEnumerable<object[]> ArgumentPayloads()
    {
        var raw = new[]
        {
            "; touch SENTINEL",
            "&& touch SENTINEL",
            "| touch SENTINEL",
            "$(touch SENTINEL)",
            "`touch SENTINEL`",
            "file.vhd; touch SENTINEL",
            "file.vhd && touch SENTINEL",
            "\"; touch SENTINEL; \"",
            "'; touch SENTINEL; '",
            "\\\"; touch SENTINEL",
            "x\ntouch SENTINEL",
            "$IFS$9touch${IFS}SENTINEL",
            "-o=$(touch SENTINEL)",
            "a b; touch SENTINEL",
            ">SENTINEL",
            "x`touch SENTINEL`y",
            "${PATH:0:0}; touch SENTINEL",
        };
        foreach (var p in raw) yield return new object[] { p };
    }

    [Theory]
    [MemberData(nameof(ArgumentPayloads))]
    public void Arguments_AreNotInjectable_UnderArgvModel(string payloadTemplate)
    {
        var workingDir = NewWorkingDir();
        try
        {
            var sentinel = "pwned_" + Guid.NewGuid().ToString("N");
            var payload = payloadTemplate.Replace("SENTINEL", sentinel, StringComparison.Ordinal);

            var cmd = EmitArgv("true", payload, workingDir);

            // The malicious token must occupy exactly one argv slot beyond the executable: argv[0]="true"
            // plus a single argument. If it were split, the model would have shell semantics.
            Assert.Equal("true", cmd[0]);
            Assert.Equal(2, cmd.Count);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Assert.False(RunArgvAndDidInject(cmd, workingDir, sentinel),
                    $"Payload <{payloadTemplate}> executed under the argv model: [{string.Join("] [", cmd)}]");
            }
        }
        finally { Directory.Delete(workingDir, true); }
    }

    /// <summary>
    /// The pre-migration executable injection (an unquoted executable appended to the shell line) is
    /// eliminated: the malicious executable name is a single argv[0] token. The OS attempts to resolve it
    /// as one program path and fails; no shell ever parses the embedded separator.
    /// </summary>
    [Fact]
    public void Executable_Injection_IsEliminated_UnderArgvModel()
    {
        var workingDir = NewWorkingDir();
        try
        {
            var sentinel = "pwned_" + Guid.NewGuid().ToString("N");
            var cmd = EmitArgvNoArgs($"true; touch {sentinel}", workingDir);

            Assert.Single(cmd);
            Assert.Contains(sentinel, cmd[0], StringComparison.Ordinal); // intact as one token, not split
            Assert.False(RunArgvAndDidInject(cmd, workingDir, sentinel),
                $"Executable injection survived the argv model: [{string.Join("] [", cmd)}]");
        }
        finally { Directory.Delete(workingDir, true); }
    }

    /// <summary>Structural invariant: a normal command is emitted as an argv vector headed by the
    /// executable, not wrapped in <c>sh -c</c>. This is what removes the shell-injection surface.</summary>
    [Fact]
    public void Command_IsEmittedAsArgvVector_NotShellWrapped()
    {
        var workingDir = NewWorkingDir();
        try
        {
            var cmd = EmitArgv("ghdl", "-a", workingDir);
            Assert.Equal("ghdl", cmd[0]);
            Assert.DoesNotContain("-c", cmd.Take(1)); // argv[0] is the tool, never the shell flag
            Assert.NotEqual("sh", cmd[0]);
        }
        finally { Directory.Delete(workingDir, true); }
    }
}
