using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Verifies that the cross-process-safe telemetry writer tolerates many concurrent
/// in-process writers without torn lines or lost entries. The whole telemetry design
/// (a reader-writer lock plus a named mutex) rests on this property; the harness
/// `stress-telemetry` mode exercises the multi-process case, this locks in the
/// multi-thread case as a fast unit test.
/// </summary>
[Collection("TelemetryTests")]
public sealed class TelemetryConcurrencyTests : IDisposable
{
    private readonly string _dir;

    public TelemetryConcurrencyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OneWareTests_TelemConc", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_dir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
    }

    [Fact]
    public async Task ConcurrentWrites_AllEntriesPersisted_NoTornLines()
    {
        const int threads = 12;
        const int perThread = 200;
        const int total = threads * perThread;

        var tasks = Enumerable.Range(0, threads).Select(threadId => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                ContainerTelemetry.LogExecution(
                    image: "concurrency-test-image",
                    tool: $"tool-{threadId}",
                    durationSeconds: 0.01 * i,
                    exitCode: 0,
                    imageDigest: null,
                    wasCancelled: false,
                    dockerRunCommand: $"--thread {threadId} --iter {i}",
                    peakMemoryBytes: 1024 * 1024,
                    maxCpuPercent: 1.5,
                    oomKilled: false,
                    // High cap so retention trimming never fires and the count is exact.
                    maxEntries: total + 1000,
                    errorMessage: null);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var path = ContainerTelemetry.TelemetryFilePath;
        Assert.True(File.Exists(path), $"Telemetry file was not created at {path}");

        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        // Every line must parse independently: a torn/interleaved write would produce invalid JSON.
        foreach (var line in lines)
        {
            var ex = Record.Exception(() =>
            {
                using var _ = JsonDocument.Parse(line);
            });
            Assert.Null(ex);
        }

        // No entries lost: the writer serialises all concurrent appends.
        Assert.Equal(total, lines.Length);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the isolated test telemetry directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the isolated test telemetry directory.
        }
    }
}
