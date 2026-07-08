using System;
using System.IO;
using System.Threading;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for the telemetry opt-out invariant hardening (findings C.1/C.2): a purge is only
/// latched as done once truncation is confirmed, and a write in flight when opt-out flips is suppressed
/// under the write lock rather than appended into the just-purged file.
/// </summary>
[Collection("TelemetryTests")]
public sealed class TelemetryOptOutInvariantTests : IDisposable
{
    private readonly string _dir;

    public TelemetryOptOutInvariantTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OneWareTests_OptOutInv", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_dir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.TelemetryOptedOutChecker = () => false;
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch { /* best-effort teardown */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ClearEntries_ReturnsTrueOnConfirmedTruncation()
    {
        // C.1: the opt-out purge latches its "done" flag on this return value, so a real clear must
        // report success (and a no-op clear of empty state is also success).
        ContainerTelemetry.LogExecution("img:latest", "ghdl", 1.0, exitCode: 0);
        Assert.NotEmpty(ContainerTelemetry.GetRecentEntries());

        Assert.True(ContainerTelemetry.ClearEntries());
        Assert.Empty(ContainerTelemetry.GetRecentEntries());
        Assert.True(ContainerTelemetry.ClearEntries());
    }

    [Fact]
    public void OptOut_FlippingMidWrite_SuppressesTheAppendUnderLock()
    {
        // C.2: the checker reports opted-in on the first observation (LogExecution's entry gate) and
        // opted-out on the second (the under-lock re-check), reproducing opt-out flipping after a write
        // began. The under-lock re-check must suppress the append.
        int observations = 0;
        ContainerTelemetry.TelemetryOptedOutChecker = () => Interlocked.Increment(ref observations) >= 2;

        ContainerTelemetry.LogExecution("img:latest", "ghdl", 1.0, exitCode: 0,
            dockerRunCommand: "docker run --rm img:latest ghdl -a x.vhd");

        // Steadily opted-in now: a suppressed append means reads surface nothing.
        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
        Assert.Empty(ContainerTelemetry.GetRecentEntries());
    }
}
