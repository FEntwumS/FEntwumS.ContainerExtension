using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ContainerExtension;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// A tool argument that contains the text <c>--privileged</c> goes to the tool inside the container and cannot make the
/// daemon start a privileged container, so a run with such an argument is not refused. The runs go against a stand-in
/// daemon on 127.0.0.1 and record their failures, so the class keeps telemetry in a folder of its own and shares the
/// telemetry collection.
/// </summary>
[Collection("TelemetryTests")]
public sealed class PrivilegedArgumentTests : IDisposable
{
    private readonly string _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_PrivilegedArgument", Guid.NewGuid().ToString("N"));

    public PrivilegedArgumentTests()
    {
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
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
        catch { /* best effort */ }
    }

    [Theory]
    [InlineData("x--privileged.v")]
    [InlineData("--privileged")]
    public async Task ExecuteAsync_ArgumentContainingPrivileged_ReachesTheImageLookup(string argument)
    {
        var port = DockerDaemonDouble.FreePort();
        using var daemon = DockerDaemonDouble.Start(port, "1.47");
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DaemonSocketSetting, $"http://127.0.0.1:{port}");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        using var strategy = new DockerExecutionStrategy(provider) { HostFeedback = new RecordingRunFeedback() };
        await strategy.EnsureInitializedAsync(TestContext.Current.CancellationToken);
        var command = new ToolCommand
        {
            Executable = "echo",
            ToolName = "echo",
            WorkingDirectory = Path.GetTempPath(),
            CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument(argument) }
        };

        await strategy.ExecuteAsync(command, TestContext.Current.CancellationToken);

        // The stand-in answers the image lookup with 404, so the run fails there, after every check that precedes it.
        Assert.Contains(daemon.Paths, path => path.Contains("/images/", StringComparison.Ordinal));
    }
}
