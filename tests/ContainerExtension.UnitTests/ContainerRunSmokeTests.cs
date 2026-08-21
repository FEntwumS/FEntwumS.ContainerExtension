using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ContainerExtension;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Lightweight real-container smoke test. Runs <c>echo</c> in the tiny, commonly-cached <c>busybox</c>
/// image through the full <see cref="DockerExecutionStrategy.ExecuteAsync(ToolCommand)"/> path against the
/// local Docker daemon. Unlike the HDL end-to-end suite it needs no fixtures and no image pull, so it can
/// validate the container run loop anywhere Docker is available — which makes it a fast regression anchor
/// for refactors of the execution engine.
/// </summary>
[Collection("TelemetryTests")]
public sealed class ContainerRunSmokeTests : IDisposable
{
    private readonly string _telemetryDir;

    public ContainerRunSmokeTests()
    {
        _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_Smoke", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
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

    [FactIfNoCI]
    public async Task Busybox_Echo_RunsInContainerAndCapturesOutput()
    {
        using var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.DefaultImageSetting, "busybox:latest");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.PullPolicySetting, "never");
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.BypassNamedPipeCheckSetting, true);

        using var strategy = new DockerExecutionStrategy(provider);
        var command = new ToolCommand
        {
            Executable = "echo",
            ToolName = "echo",
            WorkingDirectory = Path.GetTempPath(),
            CommandArguments = new List<ICommandArgument> { new E2ETestCommandArgument("hello-from-container") }
        };

        var (success, output) = await strategy.ExecuteAsync(command);

        Assert.True(success, $"expected container run to succeed; output was: {output}");
        Assert.Contains("hello-from-container", output, StringComparison.Ordinal);
    }
}
