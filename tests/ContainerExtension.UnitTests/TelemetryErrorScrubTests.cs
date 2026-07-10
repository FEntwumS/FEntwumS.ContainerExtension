using System;
using System.IO;
using ContainerExtension;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression suite for error-log secret scrubbing: TrackError must apply the KEY=value secret scrub (as LogExecution
/// does), not merely the path/host scrub, so a secret embedded in an exception message never reaches the
/// error log in the clear.
/// </summary>
[Collection("TelemetryTests")]
public sealed class TelemetryErrorScrubTests : IDisposable
{
    private readonly string _dir;

    public TelemetryErrorScrubTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OneWareTests_ErrScrub", Guid.NewGuid().ToString("N"));
        ContainerTelemetry.InitializeTestEnvironment(_dir);
        ContainerTelemetry.LogLevelChecker = () => "Verbose";
        ContainerTelemetry.TelemetryOptedOutChecker = () => false;
    }

    public void Dispose()
    {
        try
        {
            ContainerTelemetry.Shutdown();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch { /* best-effort teardown */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TrackError_RedactsKeyValueSecretInExceptionMessage()
    {
        // The value is a synthetic, secret-shaped literal, not a real credential.
        const string secretValue = "hunter2trustno1zzz"; // gitleaks:allow
        ContainerTelemetry.TrackError("DockerExecutionStrategy", "SecretScrubProbe",
            new InvalidOperationException($"connect failed password={secretValue} while dialing"));

        var errorLog = Path.Combine(_dir, "container_errors.jsonl");
        WaitForErrorLog(errorLog, "SecretScrubProbe", 5000);

        Assert.True(File.Exists(errorLog));
        var content = File.ReadAllText(errorLog);
        Assert.Contains("SecretScrubProbe", content, StringComparison.Ordinal); // the entry landed
        Assert.DoesNotContain(secretValue, content, StringComparison.Ordinal);  // and the secret was scrubbed
    }

    // TrackError only enqueues the write; a background reader drains it, so poll the file (shared read)
    // until the expected content lands rather than sleeping a fixed interval.
    private static void WaitForErrorLog(string path, string mustContain, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    if (reader.ReadToEnd().Contains(mustContain, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // Writer holds the file momentarily; retry until the deadline.
                }
            }
            System.Threading.Thread.Sleep(20);
        }
    }
}
