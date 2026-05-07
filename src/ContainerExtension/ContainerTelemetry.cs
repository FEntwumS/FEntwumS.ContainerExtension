using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace ContainerExtension;

/// <summary>
/// Lightweight telemetry logger that records container execution metrics
/// to a JSON Lines file (<c>~/.oneware/container_telemetry.jsonl</c>).
/// Each line is a self-contained JSON object representing one container execution,
/// supporting the thesis evaluation chapter with empirical metrics.
/// </summary>
/// <remarks>
/// All operations are best-effort — telemetry must never crash the host IDE.
/// File I/O uses append-only semantics for crash resilience.
/// JSON Lines format chosen for streaming compatibility and grep-friendliness.
/// </remarks>
public static class ContainerTelemetry
{
    private static readonly string TelemetryDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oneware");

    private static readonly string TelemetryPath =
        Path.Combine(TelemetryDir, "container_telemetry.jsonl");

    private static readonly string ErrorTelemetryPath =
        Path.Combine(TelemetryDir, "container_errors.jsonl");

    /// <summary>Absolute path to the telemetry .jsonl file, exposed for dashboard display.</summary>
    public static string TelemetryFilePath => TelemetryPath;

    /// LOCK ORDERING CONTRACT:
    ///   1. RwLock (in-process)  -->  2. ProcessMutex (cross-process)
    /// All methods MUST acquire RwLock first, then ProcessMutex.
    /// Never call a write method (LogExecution, TrackError, ClearEntries) from within
    /// a read lock -- ReaderWriterLockSlim does not support lock upgrading and will deadlock.
    private static readonly ReaderWriterLockSlim RwLock = new();

    /// <summary>
    /// Cached line count to avoid unconditional <c>File.ReadAllLines</c> in <see cref="LogExecution"/>.
    /// -1 = unknown (will be read from file on next write). Reset by <see cref="ClearEntries"/>.
    /// </summary>
    private static int _cachedLineCount = -1;

    /// <summary>
    /// Named Mutex for cross-process synchronization.
    /// Multiple IDE instances and the benchmark harness share the same .jsonl file;
    /// a process-local <c>lock</c> alone cannot prevent concurrent file corruption.
    /// Falls back to null on platforms where named mutexes are not supported or when access is denied (e.g., cross-user locks on macOS/Linux).
    /// </summary>
    private static readonly Mutex? ProcessMutex = CreateProcessMutex();

    /// <summary>Creates the cross-process Mutex, catching PlatformNotSupportedException and UnauthorizedAccessException to degrade gracefully.</summary>
    private static Mutex? CreateProcessMutex()
    {
        try { return new Mutex(false, "OneWareContainerTelemetryLock"); }
        catch (PlatformNotSupportedException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (Exception) { return null; } // Best effort, fallback to null (no cross-process sync)
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolverChain = { TelemetryJsonContext.Default, ErrorJsonContext.Default }
    };

    /// <summary>
    /// Appends a single execution record to the telemetry log.
    /// If <paramref name="maxEntries"/> is specified, trims the file to the last N entries.
    /// Failures are silently swallowed to avoid disrupting IDE operations.
    /// </summary>
    /// <param name="image">Docker image name (e.g. hdlc/ghdl:yosys).</param>
    /// <param name="tool">The tool or executable that was run inside the container.</param>
    /// <param name="durationSeconds">Total wall-clock execution time in seconds.</param>
    /// <param name="exitCode">Container exit code (0 = success).</param>
    /// <param name="imageDigest">Optional SHA256 image digest for reproducibility.</param>
    /// <param name="wasCancelled">Whether the execution was cancelled by the user or a timeout.</param>
    /// <param name="dockerRunCommand">Optional reconstructed docker run command for debugging.</param>
    /// <param name="peakMemoryBytes">Peak RSS memory usage in bytes during execution (null if stats unavailable).</param>
    /// <param name="maxCpuPercent">Highest CPU utilization sample as a percentage (null if stats unavailable).</param>
    /// <param name="oomKilled">Whether the container was killed by the kernel OOM killer.</param>
    /// <param name="maxEntries">Maximum entries to retain (0 = unlimited).</param>
    /// <param name="errorMessage">Optional error message if the execution failed before container start.</param>
    public static void LogExecution(
        string image,
        string tool,
        double durationSeconds,
        long exitCode,
        string? imageDigest = null,
        bool wasCancelled = false,
        string? dockerRunCommand = null,
        long? peakMemoryBytes = null,
        double? maxCpuPercent = null,
        bool oomKilled = false,
        int maxEntries = 0,
        string? errorMessage = null)
    {
        try
        {
            Directory.CreateDirectory(TelemetryDir);

            var entry = new TelemetryEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Image = image,
                ImageDigest = imageDigest,
                // Strip directory components and file extensions from the tool name.
                // The IDE SDK may pass full native paths like "/usr/local/bin/ghdl" or
                // "C:\Tools\yosys.exe" — Path.GetFileNameWithoutExtension handles both
                // '/' (macOS/Linux) and '\' (Windows) separators correctly.
                Tool = Path.GetFileNameWithoutExtension(tool),
                DurationSeconds = Math.Round(durationSeconds, 4),
                ExitCode = exitCode,
                WasCancelled = wasCancelled,
                DockerRunCommand = dockerRunCommand,
                PeakMemoryBytes = peakMemoryBytes,
                MaxCpuPercent = maxCpuPercent.HasValue ? Math.Round(maxCpuPercent.Value, 1) : null,
                OomKilled = oomKilled,
                ErrorMessage = errorMessage
            };

            var json = JsonSerializer.Serialize(entry, TelemetryJsonContext.Default.TelemetryEntry);
            // RwLock.EnterWriteLock provides in-process serialization;
            // the Mutex handles cross-process synchronization (multiple IDE instances).
            RwLock.EnterWriteLock();
            try
            {
                bool acquired = false;
                try
                {
                    try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return; // Cross-process lock failed — skip write to prevent corruption
                    File.AppendAllText(TelemetryPath, json + Environment.NewLine);

                    // Batch trimming with cached line count: only read the file when the
                    // cached count crosses the 120% threshold. This avoids an expensive
                    // file scan on every single execution (skips ~96% of file reads
                    // at the default 100-entry retention limit).
                    if (maxEntries > 0)
                    {
                        // Initialize cache from file if unknown (-1)
                        if (Volatile.Read(ref _cachedLineCount) < 0)
                        {
                            Volatile.Write(ref _cachedLineCount, System.Linq.Enumerable.Count(File.ReadLines(TelemetryPath)));
                        }
                        else
                        {
                            Volatile.Write(ref _cachedLineCount, Volatile.Read(ref _cachedLineCount) + 1);
                        }

                        var trimThreshold = (int)(maxEntries * 1.2);
                        if (_cachedLineCount > trimThreshold)
                        {
                            // Trim: Queue keeps at most maxEntries lines
                            // Use ReadLines for lazy enumeration instead of reading all into an array
                            var q = new Queue<string>(maxEntries);
                            foreach (var line in File.ReadLines(TelemetryPath))
                            {
                                q.Enqueue(line);
                                if (q.Count > maxEntries) q.Dequeue();
                            }
                            var tempFile = TelemetryPath + ".tmp";
                            using (var writer = new StreamWriter(tempFile))
                            {
                                foreach (var line in q)
                                    writer.WriteLine(line);
                            }
                            File.Move(tempFile, TelemetryPath, true);
                            Volatile.Write(ref _cachedLineCount, maxEntries);
                        }
                    }
                    else
                    {
                        // No retention limit -- just track count for future use
                        if (Volatile.Read(ref _cachedLineCount) < 0)
                            Volatile.Write(ref _cachedLineCount, 1);
                        else
                            Volatile.Write(ref _cachedLineCount, Volatile.Read(ref _cachedLineCount) + 1);
                    }
                }
                finally
                {
                    if (acquired) ProcessMutex?.ReleaseMutex();
                }
            }
            finally
            {
                RwLock.ExitWriteLock();
            }
        }
        catch
        {
            // Telemetry must never crash the host application
        }
    }

    /// <summary>
    /// Appends a single error record to the telemetry error log.
    /// Used to persist transient faults and background exceptions without crashing the IDE.
    /// Caps the error log at 500 entries to prevent unbounded disk growth.
    /// </summary>
    public static void TrackError(string component, string action, Exception? ex = null, string? context = null)
    {
        try
        {
            Directory.CreateDirectory(TelemetryDir);

            var entry = new TelemetryErrorEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                Component = component,
                Action = action,
                ExceptionMessage = ex?.Message,
                StackTrace = ex?.StackTrace,
                Context = context
            };

            var json = JsonSerializer.Serialize(entry, ErrorJsonContext.Default.TelemetryErrorEntry);

            RwLock.EnterWriteLock();
            try
            {
                bool acquired = false;
                try
                {
                    try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return;

                    File.AppendAllText(ErrorTelemetryPath, json + Environment.NewLine);

                    // Trim error log to prevent unbounded growth (cap at 500 entries)
                    const int maxErrorEntries = 500;
                    const int errorTrimThreshold = 600;
                    if (File.Exists(ErrorTelemetryPath))
                    {
                        var lineCount = System.Linq.Enumerable.Count(File.ReadLines(ErrorTelemetryPath));
                        if (lineCount > errorTrimThreshold)
                        {
                            var q = new Queue<string>(maxErrorEntries);
                            foreach (var line in File.ReadLines(ErrorTelemetryPath))
                            {
                                q.Enqueue(line);
                                if (q.Count > maxErrorEntries) q.Dequeue();
                            }
                            var tempFile = ErrorTelemetryPath + ".tmp";
                            using (var writer = new StreamWriter(tempFile))
                            {
                                foreach (var line in q)
                                    writer.WriteLine(line);
                            }
                            File.Move(tempFile, ErrorTelemetryPath, true);
                        }
                    }
                }
                finally
                {
                    if (acquired) ProcessMutex?.ReleaseMutex();
                }
            }
            finally
            {
                RwLock.ExitWriteLock();
            }
        }
        catch
        {
            // Telemetry must never crash the host application, even in error tracking
        }
    }

    /// <summary>
    /// Reads the most recent <paramref name="count"/> telemetry entries, returned newest-first.
    /// Returns an empty list if the file doesn't exist or is unreadable.
    /// </summary>
    /// <param name="count">Number of recent entries to retrieve (default: 20).</param>
    public static List<TelemetryEntry> GetRecentEntries(int count = 20)
    {
        var results = new List<TelemetryEntry>();
        try
        {
            RwLock.EnterReadLock();
            try
            {
                bool acquired = false;
                try
                {
                    try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!File.Exists(TelemetryPath)) return results;

                    var q = new Queue<string>(count);
                    foreach (var line in File.ReadLines(TelemetryPath))
                    {
                        q.Enqueue(line);
                        if (q.Count > count) q.Dequeue();
                    }

                    foreach (var line in q.Reverse())
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var entry = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEntry);
                        if (entry != null) results.Add(entry);
                    }
                }
                finally
                {
                    if (acquired) ProcessMutex?.ReleaseMutex();
                }
            }
            finally
            {
                RwLock.ExitReadLock();
            }
        }
        catch { /* Best effort */ }
        return results;
    }

    /// <summary>
    /// Returns aggregate statistics across all telemetry entries:
    /// total runs, success rate (0-100%), and average duration in seconds.
    /// </summary>
    public static (int totalRuns, double successRate, double avgDuration) GetStats()
    {
        try
        {
            RwLock.EnterReadLock();
            try
            {
                bool acquired = false;
                try
                {
                    try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!File.Exists(TelemetryPath)) return (0, 0, 0);

                    int total = 0, successes = 0;
                    double totalDuration = 0;

                    foreach (var line in File.ReadLines(TelemetryPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var entry = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEntry);
                        if (entry == null) continue;
                        total++;
                        if (entry.ExitCode == 0 && !entry.WasCancelled) successes++;
                        totalDuration += entry.DurationSeconds;
                    }

                    if (total == 0) return (0, 0, 0);
                    return (total, Math.Round((double)successes / total * 100, 1), Math.Round(totalDuration / total, 2));
                }
                finally
                {
                    if (acquired) ProcessMutex?.ReleaseMutex();
                }
            }
            finally
            {
                RwLock.ExitReadLock();
            }
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>
    /// Copies the telemetry file to the specified path for external analysis or backup.
    /// Returns <c>true</c> on success, <c>false</c> if the source file doesn't exist or the copy fails.
    /// </summary>
    /// <param name="destinationPath">Full path to write the exported file.</param>
    public static bool ExportTo(string destinationPath)
    {
        try
        {
            RwLock.EnterReadLock();
            try
            {
                bool acquired = false;
                try
                {
                    try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!File.Exists(TelemetryPath)) return false;
                    var destDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    File.Copy(TelemetryPath, destinationPath, overwrite: true);
                }
                finally
                {
                    if (acquired) ProcessMutex?.ReleaseMutex();
                }
            }
            finally
            {
                RwLock.ExitReadLock();
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Deletes the telemetry log file, clearing all recorded entries.
    /// Subsequent calls to <see cref="GetRecentEntries"/> will return an empty list.
    /// </summary>
    public static void ClearEntries()
    {
        try
        {
            RwLock.EnterWriteLock();
            try
            {
                bool acquired = false;
                try
                {
                    try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (File.Exists(TelemetryPath)) File.Delete(TelemetryPath);
                    Volatile.Write(ref _cachedLineCount, -1); // Force re-read from disk on next write (handles cross-process races)
                }
                finally
                {
                    if (acquired) ProcessMutex?.ReleaseMutex();
                }
            }
            finally
            {
                RwLock.ExitWriteLock();
            }
        }
        catch { /* Best effort */ }
    }

    /// <summary>
    /// Returns the most recent <paramref name="count"/> telemetry entries (newest-first)
    /// along with aggregate statistics, computed in a single file pass.
    /// Avoids the overhead of calling <see cref="GetRecentEntries"/> and <see cref="GetStats"/> separately
    /// (which would deserialize the entire file twice).
    /// </summary>
    /// <param name="count">Number of recent entries to retrieve (default: 20).</param>
    public static (List<TelemetryEntry> entries, int totalRuns, double successRate, double avgDuration) GetRecentEntriesWithStats(int count = 20)
    {
        var results = new List<TelemetryEntry>();
        int total = 0, successes = 0;
        double totalDuration = 0;
        try
        {
            RwLock.EnterReadLock();
            try
            {
                bool acquired = false;
                try
                {
                    try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!File.Exists(TelemetryPath))
                        return (results, 0, 0, 0);

                    // Sliding window of deserialized entries — avoids a second
                    // deserialization pass that would occur with raw string queuing.
                    var q = new Queue<TelemetryEntry>(count);

                    foreach (var line in File.ReadLines(TelemetryPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var entry = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEntry);
                        if (entry == null) continue;

                        // Stats accumulation (single pass)
                        total++;
                        if (entry.ExitCode == 0 && !entry.WasCancelled) successes++;
                        totalDuration += entry.DurationSeconds;

                        // Sliding window for recent entries
                        q.Enqueue(entry);
                        if (q.Count > count) q.Dequeue();
                    }

                    // Newest-first ordering
                    results.AddRange(q.Reverse());
                }
                finally
                {
                    if (acquired) ProcessMutex?.ReleaseMutex();
                }
            }
            finally
            {
                RwLock.ExitReadLock();
            }
        }
        catch { /* Best effort */ }

        var successRate = total > 0 ? Math.Round((double)successes / total * 100, 1) : 0;
        var avgDuration = total > 0 ? Math.Round(totalDuration / total, 2) : 0;
        return (results, total, successRate, avgDuration);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Data Model
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>A single container execution telemetry record.</summary>
    public class TelemetryEntry
    {
        /// <summary>ISO 8601 UTC timestamp of the execution.</summary>
        [JsonPropertyName("ts")]
        public string Timestamp { get; set; } = "";

        /// <summary>Docker image name (e.g., hdlc/ghdl:yosys).</summary>
        [JsonPropertyName("image")]
        public string Image { get; set; } = "";

        /// <summary>SHA256 image digest for reproducibility (null if unavailable).</summary>
        [JsonPropertyName("digest")]
        public string? ImageDigest { get; set; }

        /// <summary>Tool name or executable that was run inside the container.</summary>
        [JsonPropertyName("tool")]
        public string Tool { get; set; } = "";

        /// <summary>Total wall-clock execution time in seconds.</summary>
        [JsonPropertyName("duration_s")]
        public double DurationSeconds { get; set; }

        /// <summary>Container exit code (0 = success).</summary>
        [JsonPropertyName("exit")]
        public long ExitCode { get; set; }

        /// <summary>Whether the execution was cancelled by the user. Omitted when false.</summary>
        [JsonPropertyName("cancelled")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool WasCancelled { get; set; }

        /// <summary>Reconstructed docker run command for debugging (null if unavailable).</summary>
        [JsonPropertyName("docker_run")]
        public string? DockerRunCommand { get; set; }

        /// <summary>Peak RSS memory usage in bytes during container execution (null if stats were unavailable).</summary>
        [JsonPropertyName("peak_mem")]
        public long? PeakMemoryBytes { get; set; }

        /// <summary>Highest CPU utilization sample as a percentage, e.g. 89.2 (null if stats were unavailable).</summary>
        [JsonPropertyName("max_cpu")]
        public double? MaxCpuPercent { get; set; }

        /// <summary>Whether the container was killed by the kernel OOM killer (exit code 137). Omitted when false.</summary>
        [JsonPropertyName("oom")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool OomKilled { get; set; }

        /// <summary>Error message if execution failed before container start (null on success). Omitted when null.</summary>
        [JsonPropertyName("error_msg")]
        public string? ErrorMessage { get; set; }
    }

    /// <summary>A single exception/error telemetry record.</summary>
    public class TelemetryErrorEntry
    {
        [JsonPropertyName("ts")]
        public string Timestamp { get; set; } = "";

        [JsonPropertyName("cmp")]
        public string Component { get; set; } = "";

        [JsonPropertyName("act")]
        public string Action { get; set; } = "";

        [JsonPropertyName("ex_msg")]
        public string? ExceptionMessage { get; set; }

        [JsonPropertyName("stack")]
        public string? StackTrace { get; set; }

        [JsonPropertyName("ctx")]
        public string? Context { get; set; }
    }
}

/// <summary>
/// Source-generated JSON serialization context for <see cref="ContainerTelemetry.TelemetryEntry"/>.
/// Eliminates reflection-based serialization at runtime for improved startup performance and AOT compatibility.
/// </summary>
[JsonSerializable(typeof(ContainerTelemetry.TelemetryEntry))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal partial class TelemetryJsonContext : JsonSerializerContext { }

/// <summary>
/// Source-generated JSON serialization context for <see cref="ContainerTelemetry.TelemetryErrorEntry"/>.
/// </summary>
[JsonSerializable(typeof(ContainerTelemetry.TelemetryErrorEntry))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal partial class ErrorJsonContext : JsonSerializerContext { }
