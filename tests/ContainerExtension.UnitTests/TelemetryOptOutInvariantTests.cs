using System;
using System.IO;
using System.Threading;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for the telemetry opt-out invariant hardening: a purge is only
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
        // The opt-out purge latches its "done" flag on this return value, so a real clear must
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
        // The checker reports opted-in on the first observation (LogExecution's entry gate) and
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

    [Fact]
    public void OptOut_FlippingMidErrorWrite_SuppressesTheChannelAppendUnderLock()
    {
        // The error channel writes asynchronously: TrackError vets opt-out at enqueue time, then a
        // background consumer performs the disk write. The checker reports opted-in on the first
        // observation (TrackError's entry gate) and opted-out on every later one (the consumer's
        // under-lock re-check), reproducing opt-out flipping after the entry was already queued. The
        // under-lock re-check must suppress the append rather than write into the just-purged file.
        int observations = 0;
        ContainerTelemetry.TelemetryOptedOutChecker = () => Interlocked.Increment(ref observations) >= 2;

        ContainerTelemetry.TrackError("DockerExecutionStrategy", "OptOutRaceProbe",
            new InvalidOperationException("boom"));

        // The re-check is the second checker observation; by construction it returns opted-out and must
        // return before writing. Wait for it to run rather than sleeping a fixed interval.
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref observations) < 2 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
        }
        Assert.True(Volatile.Read(ref observations) >= 2, "the error-channel consumer never re-checked opt-out");

        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
        var errorLog = Path.Combine(_dir, "container_errors.jsonl");
        Assert.True(
            !File.Exists(errorLog) || !File.ReadAllText(errorLog).Contains("OptOutRaceProbe", StringComparison.Ordinal),
            "the opt-out re-check should have suppressed the error-channel append");
    }
}
