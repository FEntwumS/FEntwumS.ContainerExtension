using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ContainerExtension;
using ContainerExtension.Services.Docker;
using OneWare.Essentials.ToolEngine;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Live-daemon integration exercise of the real container lifecycle against a trivial public image
/// (busybox — no FPGA fixtures required). These drive DockerExecutionStrategy through
/// create -> start -> attach/stream -> wait -> exit-code -> auto-remove, covering the paths the unit
/// tests only mock: teardown (B.1/B.2/B.3), the (success,output) contract, log streaming + UTF-8 decode
/// (H.2/H.3), and single-entry telemetry. [FactIfNoCI] skips them in CI (they need a daemon + Docker Hub).
/// </summary>
[Collection("TelemetryTests")]
public sealed class DockerLifecycleIntegrationTests : IDisposable
{
    private const string Image = "busybox:latest";
    private readonly string _telemetryDir;

    public DockerLifecycleIntegrationTests()
    {
        _telemetryDir = Path.Combine(Path.GetTempPath(), "OneWareTests_Lifecycle", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_telemetryDir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_telemetryDir)) Directory.Delete(_telemetryDir, true);
        }
        catch { /* best-effort teardown */ }
        GC.SuppressFinalize(this);
    }

    private static (E2ETestServiceProvider provider, DockerExecutionStrategy strategy) MakeStrategy(string tool)
    {
        var provider = new E2ETestServiceProvider();
        provider.SettingsService.SetSettingValue($"{ContainerExtensionModule.PerToolImagePrefix}{tool}", Image);
        provider.SettingsService.SetSettingValue(ContainerExtensionModule.AutoRemoveSetting, true);
        return (provider, new DockerExecutionStrategy(provider));
    }

    private static ToolCommand Cmd(string tool, string exe, string workDir, params string[] args)
        => new()
        {
            Executable = exe,
            ToolName = tool,
            WorkingDirectory = workDir,
            CommandArguments = args.Select(a => (ICommandArgument)new E2ETestCommandArgument(a)).ToList(),
        };

    [FactIfNoCI]
    public async Task Container_HappyPath_Runs_Streams_And_ExitsZero()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "lc_ok_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var (provider, strategy) = MakeStrategy("echo");
            using (provider)
            using (strategy)
            {
                var marker = "hello-from-container-" + Guid.NewGuid().ToString("N")[..8];
                var (success, output) = await strategy.ExecuteAsync(Cmd("echo", "echo", workDir, marker));
                Assert.True(success, "expected exit 0; output was: " + output);
                Assert.Contains(marker, output, StringComparison.Ordinal); // streamed back + correctly decoded
            }
            // Disposing the strategy above runs the reaper; auto-remove + teardown must leave nothing behind.
        }
        finally { try { Directory.Delete(workDir, true); } catch { /* best-effort */ } }
    }

    [FactIfNoCI]
    public async Task Container_NonZeroExit_ReportedAsFailure()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "lc_fail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var (provider, strategy) = MakeStrategy("false");
            using (provider)
            using (strategy)
            {
                var (success, _) = await strategy.ExecuteAsync(Cmd("false", "false", workDir));
                Assert.False(success); // busybox `false` exits 1
            }
        }
        finally { try { Directory.Delete(workDir, true); } catch { /* best-effort */ } }
    }

    [FactIfNoCI]
    public async Task Container_Run_LogsExactlyOneTelemetryEntry_WithExitZero()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "lc_tel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            ContainerTelemetry.ClearEntries();
            var (provider, strategy) = MakeStrategy("echo");
            using (provider)
            using (strategy)
            {
                var (success, _) = await strategy.ExecuteAsync(Cmd("echo", "echo", workDir, "tick"));
                Assert.True(success);
                // Read while the strategy is still alive: disposing it calls ContainerTelemetry.Shutdown(),
                // after which reads short-circuit.
                var entries = ContainerTelemetry.GetRecentEntries(10);
                Assert.Single(entries); // one entry per run — no phantom/duplicate
                Assert.Equal(0, entries[0].ExitCode);
            }
        }
        finally { try { Directory.Delete(workDir, true); } catch { /* best-effort */ } }
    }
}
