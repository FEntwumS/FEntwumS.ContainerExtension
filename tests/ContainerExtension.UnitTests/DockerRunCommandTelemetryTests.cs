using System;
using System.Collections.Generic;
using System.IO;
using ContainerExtension.Services.Docker;
using Docker.DotNet.Models;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// The masked <c>docker run</c> command goes to the telemetry log, and the dashboard finds the exact one for a
/// run of this session through it. Both come from <see cref="DockerRunCommandFormatter"/>, so the quoted form
/// has to keep the pair together and keep the environment values out of the log.
/// </summary>
[Collection("TelemetryTests")]
public sealed class DockerRunCommandTelemetryTests : IDisposable
{
    private readonly string _telemetryDir;

    public DockerRunCommandTelemetryTests()
    {
        _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_RunCommand", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_telemetryDir))
            {
                Directory.Delete(_telemetryDir, true);
            }
        }
        catch { /* best-effort teardown */ }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoggedRun_KeepsTheValuesOutOfTheLog_AndLeadsBackToTheExactCommand(bool powerShell)
    {
        var shell = powerShell ? CommandShell.PowerShell : CommandShell.Posix;
        ContainerTelemetry.ClearEntries();
        var p = new CreateContainerParameters
        {
            Image = "img:latest",
            Name = $"containerextension-ghdl-{Guid.NewGuid():N}",
            Env = new List<string> { "LICENSE_FILE=it's $HOME/secret value" },
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["team"] = "fpga" },
            Cmd = new List<string> { "ghdl", "-a", "$HOME_FILE.v" },
        };
        var masked = DockerRunCommandFormatter.Reconstruct(p, "docker", shell: shell);
        var exact = DockerRunCommandFormatter.Reconstruct(p, "docker", maskEnvValues: false, shell: shell);

        ContainerTelemetry.LogExecution("img:latest", "ghdl", 1.0, exitCode: 1, dockerRunCommand: masked, rawDockerRunCommand: exact);

        var entry = Assert.Single(ContainerTelemetry.GetRecentEntries(10));
        Assert.NotNull(entry.DockerRunCommand);
        Assert.DoesNotContain("secret value", entry.DockerRunCommand, StringComparison.Ordinal);
        Assert.Contains("--label team=fpga", entry.DockerRunCommand, StringComparison.Ordinal);
        Assert.Equal(exact, ContainerTelemetry.TryGetRawCommand(entry.DockerRunCommand));
        ContainerTelemetry.ClearEntries();
    }
}
