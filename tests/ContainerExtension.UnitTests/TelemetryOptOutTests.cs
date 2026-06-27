using System;
using System.IO;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Privacy-by-default behaviour for telemetry: opting out must not merely stop new writes — it must
/// also hide and erase any prior on-disk history across the read and export paths — and export must
/// reject a non-absolute destination.
/// </summary>
[Collection("TelemetryTests")]
public sealed class TelemetryOptOutTests : IDisposable
{
    private readonly string _dir;

    public TelemetryOptOutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OneWareTests_OptOut", Guid.NewGuid().ToString("N"));
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
    public void OptOut_GatesReadsAndExport_AndPurgesHistory()
    {
        ContainerTelemetry.LogExecution("img:latest", "ghdl", 1.0, exitCode: 0,
            dockerRunCommand: "docker run --rm img:latest ghdl -a x.vhd");
        Assert.NotEmpty(ContainerTelemetry.GetRecentEntries());

        // Opting out: read and export must surface nothing, and the prior history is purged.
        ContainerTelemetry.TelemetryOptedOutChecker = () => true;
        Assert.Empty(ContainerTelemetry.GetRecentEntries());
        Assert.Equal(0, ContainerTelemetry.GetRecentEntriesWithStats().totalRuns);
        Assert.False(ContainerTelemetry.ExportTo(Path.Combine(_dir, "export.jsonl")));

        // Opting back in must not resurrect the erased history.
        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
        Assert.Empty(ContainerTelemetry.GetRecentEntries());
    }

    [Fact]
    public void Export_RejectsRelativeDestination()
    {
        ContainerTelemetry.LogExecution("img:latest", "ghdl", 1.0, exitCode: 0);
        Assert.False(ContainerTelemetry.ExportTo("relative/path/export.jsonl"));
    }
}
