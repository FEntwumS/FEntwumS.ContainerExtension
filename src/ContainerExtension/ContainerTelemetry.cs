using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace ContainerExtension;

public static class ContainerTelemetry
{
    private static string _telemetryDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oneware");
    private static string _telemetryPath = Path.Combine(_telemetryDir, "container_telemetry.jsonl");
    private static string _errorTelemetryPath = Path.Combine(_telemetryDir, "container_errors.jsonl");

    public static string TelemetryFilePath => _telemetryPath;

    // ── Testing Hook ────────────────────────────────────────────────────────
    /// <summary>Isolates telemetry to a temporary directory during xUnit test execution.</summary>
    internal static void InitializeTestEnvironment(string tempDir)
    {
        _telemetryDir = tempDir;
        _telemetryPath = Path.Combine(_telemetryDir, "container_telemetry.jsonl");
        _errorTelemetryPath = Path.Combine(_telemetryDir, "container_errors.jsonl");
    }

    private static readonly System.Threading.Lock CacheLock = new();
    private static int _cachedLineCount = -1;
    private static int _cachedErrorLineCount = -1;
    private static DateTime _lastFileWriteTime = DateTime.MinValue;
    private static int _cachedCount = -1;
    private static (List<TelemetryEntry> entries, int totalRuns, double successRate, double avgDuration)? _cachedStatsResult;

    // LOCK ORDERING CONTRACT:
    // 1. ProcessMutex (cross-process sync, gracefully falls back to null)
    // 2. RwLock (in-process sync for platforms where Mutex is unavailable)
    private static readonly ReaderWriterLockSlim RwLock = new();
    private static readonly Mutex? ProcessMutex = CreateProcessMutex();

    private static Mutex? CreateProcessMutex()
    {
        try { return new Mutex(false, "Global\\OneWareContainerTelemetryLock"); }
        catch { return null; }
    }

    /// <summary>
    /// Eagerly reads all lines and closes the file handle immediately.
    /// Eliminates the 'yield return' lock contention issue on Windows where
    /// open Read handles can block FileMode.Create truncations.
    /// </summary>
    private static List<string> ReadAllLinesSafe(string path)
    {
        if (!File.Exists(path)) return [];
        var lines = new List<string>();
        
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null) 
            {
                lines.Add(line);
            }
        }
        catch { /* Gracefully handle extreme edge-case where file is truncated mid-read */ }
        
        return lines;
    }

    public static void LogExecution(
        string image, string tool, double durationSeconds, long exitCode, string? imageDigest = null,
        bool wasCancelled = false, string? dockerRunCommand = null, long? peakMemoryBytes = null,
        double? maxCpuPercent = null, bool oomKilled = false, int maxEntries = 0, string? errorMessage = null)
    {
        try
        {
            Directory.CreateDirectory(_telemetryDir);

            var entry = new TelemetryEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Image = image ?? string.Empty,
                ImageDigest = imageDigest,
                Tool = tool != null ? Path.GetFileNameWithoutExtension(tool) : string.Empty,
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
            
            bool acquired = false;
            try
            {
                try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;

                RwLock.EnterWriteLock();
                try
                {
                    using (var fs = new FileStream(_telemetryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.WriteLine(json);
                    }

                    if (maxEntries > 0)
                    {
                        if (Volatile.Read(ref _cachedLineCount) < 0)
                            Volatile.Write(ref _cachedLineCount, ReadAllLinesSafe(_telemetryPath).Count);
                        else
                            Interlocked.Increment(ref _cachedLineCount);

                        var trimThreshold = (int)(maxEntries * 1.2);
                        if (_cachedLineCount > trimThreshold)
                        {
                            var q = new Queue<string>(maxEntries);
                            foreach (var line in ReadAllLinesSafe(_telemetryPath))
                            {
                                q.Enqueue(line);
                                if (q.Count > maxEntries) q.Dequeue();
                            }
                            
                            var linesToKeep = q.ToArray();
                            using (var fs = new FileStream(_telemetryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            using (var writer = new StreamWriter(fs))
                            {
                                foreach (var line in linesToKeep) writer.WriteLine(line);
                            }
                            Volatile.Write(ref _cachedLineCount, maxEntries);
                        }
                    }
                    else
                    {
                        if (Volatile.Read(ref _cachedLineCount) < 0)
                            Volatile.Write(ref _cachedLineCount, 1);
                        else
                            Interlocked.Increment(ref _cachedLineCount);
                    }
                }
                finally { RwLock.ExitWriteLock(); }
            }
            finally { if (acquired) ProcessMutex?.ReleaseMutex(); }
        }
        catch { /* Best-effort execution */ }
    }

    public static void TrackError(string component, string action, Exception? ex = null, string? context = null)
    {
        try
        {
            Directory.CreateDirectory(_telemetryDir);
            var entry = new TelemetryErrorEntry
            {
                Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Component = component ?? string.Empty,
                Action = action ?? string.Empty,
                ExceptionMessage = ex?.Message,
                StackTrace = ex?.StackTrace,
                Context = context
            };

            var json = JsonSerializer.Serialize(entry, ErrorJsonContext.Default.TelemetryErrorEntry);

            bool acquired = false;
            try
            {
                try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;

                RwLock.EnterWriteLock();
                try
                {
                    using (var fs = new FileStream(_errorTelemetryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.WriteLine(json);
                    }

                    const int maxErrorEntries = 500;
                    const int errorTrimThreshold = 600;
                    
                    if (Volatile.Read(ref _cachedErrorLineCount) < 0)
                        Volatile.Write(ref _cachedErrorLineCount, ReadAllLinesSafe(_errorTelemetryPath).Count);
                    else
                        Interlocked.Increment(ref _cachedErrorLineCount);

                    if (_cachedErrorLineCount > errorTrimThreshold)
                    {
                        var q = new Queue<string>(maxErrorEntries);
                        foreach (var line in ReadAllLinesSafe(_errorTelemetryPath))
                        {
                            q.Enqueue(line);
                            if (q.Count > maxErrorEntries) q.Dequeue();
                        }
                        
                        var linesToKeep = q.ToArray();
                        using (var fs = new FileStream(_errorTelemetryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        using (var writer = new StreamWriter(fs))
                        {
                            foreach (var line in linesToKeep) writer.WriteLine(line);
                        }
                        Volatile.Write(ref _cachedErrorLineCount, maxErrorEntries);
                    }
                }
                finally { RwLock.ExitWriteLock(); }
            }
            finally { if (acquired) ProcessMutex?.ReleaseMutex(); }
        }
        catch { /* Ignored to ensure telemetry failure does not crash the host */ }
    }

    public static List<TelemetryEntry> GetRecentEntries(int count = 20)
    {
        var results = new List<TelemetryEntry>();
        try
        {
            bool acquired = false;
            try
            {
                try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired || !File.Exists(_telemetryPath)) return results;

                List<string> rawLines;
                RwLock.EnterReadLock();
                try
                {
                    rawLines = ReadAllLinesSafe(_telemetryPath);
                }
                finally { RwLock.ExitReadLock(); }

                // Deserialization happens outside the lock
                var q = new Queue<string>(count);
                foreach (var line in rawLines)
                {
                    q.Enqueue(line);
                    if (q.Count > count) q.Dequeue();
                }

                foreach (var line in q.Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEntry);
                        if (entry != null) results.Add(entry);
                    }
                    catch { /* Skip malformed partial writes cleanly */ }
                }
            }
            finally { if (acquired) ProcessMutex?.ReleaseMutex(); }
        }
        catch { /* Ignored to ensure telemetry read failure does not crash the host */ }
        return results;
    }

    public static (int totalRuns, double successRate, double avgDuration) GetStats()
    {
        var res = GetRecentEntriesWithStats(0);
        return (res.totalRuns, res.successRate, res.avgDuration);
    }

    public static bool ExportTo(string destinationPath)
    {
        try
        {
            bool acquired = false;
            try
            {
                try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired || !File.Exists(_telemetryPath)) return false;

                RwLock.EnterReadLock();
                try
                {
                    var destDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    
                    using var source = new FileStream(_telemetryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dest = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    source.CopyTo(dest);
                }
                finally { RwLock.ExitReadLock(); }
            }
            finally { if (acquired) ProcessMutex?.ReleaseMutex(); }
            return true;
        }
        catch { return false; }
    }

    public static void ClearEntries()
    {
        try
        {
            bool acquired = false;
            try
            {
                try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return; 

                RwLock.EnterWriteLock();
                try
                {
                    // FIXED: FileMode.Create safely truncates resolving IOException on 0-byte files natively.
                    if (File.Exists(_telemetryPath)) 
                        using (new FileStream(_telemetryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { /* Truncate file */ }
                    
                    if (File.Exists(_errorTelemetryPath)) 
                        using (new FileStream(_errorTelemetryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { /* Truncate file */ }
                    
                    Volatile.Write(ref _cachedLineCount, -1); 
                    Volatile.Write(ref _cachedErrorLineCount, -1); 
                }
                finally { RwLock.ExitWriteLock(); }
            }
            finally { if (acquired) ProcessMutex?.ReleaseMutex(); }
        }
        catch { /* ignore */ }
    }

    public static (List<TelemetryEntry> entries, int totalRuns, double successRate, double avgDuration) GetRecentEntriesWithStats(int count = 20)
    {
        try
        {
            bool acquired = false;
            try
            {
                try { acquired = ProcessMutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true; }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired || !File.Exists(_telemetryPath)) return ([], 0, 0, 0);

                List<string> rawLines;
                var currentWriteTime = File.GetLastWriteTimeUtc(_telemetryPath);

                lock (CacheLock)
                {
                    if (_cachedStatsResult.HasValue && _cachedCount == count && _lastFileWriteTime == currentWriteTime)
                        return _cachedStatsResult.Value;
                }

                RwLock.EnterReadLock();
                try
                {
                    rawLines = ReadAllLinesSafe(_telemetryPath);
                }
                finally { RwLock.ExitReadLock(); }

                // Statistics computation happens entirely outside the file/IO lock
                var results = new List<TelemetryEntry>();
                int total = 0, successes = 0;
                double totalDuration = 0;
                var q = new Queue<string>(count > 0 ? count : 1);

                foreach (var line in rawLines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    total++;
                    var span = line.AsSpan();
                    if (span.Contains("\"exit\":0", StringComparison.Ordinal) && !span.Contains("\"cancelled\":true", StringComparison.Ordinal))
                        successes++;

                    var durStart = span.IndexOf("\"duration_s\":", StringComparison.Ordinal);
                    if (durStart >= 0)
                    {
                        durStart += 13;
                        var durEnd = span.Slice(durStart).IndexOfAny(',', '}');
                        if (durEnd >= 0 && double.TryParse(span.Slice(durStart, durEnd), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
                            totalDuration += dur;
                    }

                    if (count > 0)
                    {
                        q.Enqueue(line);
                        if (q.Count > count) q.Dequeue();
                    }
                }

                foreach (var line in q.Reverse())
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEntry);
                        if (entry != null) results.Add(entry);
                    }
                    catch { continue; }
                }

                var successRate = total > 0 ? Math.Round((double)successes / total * 100, 1) : 0;
                var avgDuration = total > 0 ? Math.Round(totalDuration / total, 2) : 0;
                
                var newResult = (results, total, successRate, avgDuration);
                lock (CacheLock)
                {
                    _cachedStatsResult = newResult;
                    _cachedCount = count;
                    _lastFileWriteTime = currentWriteTime;
                }
                
                return newResult;
            }
            finally { if (acquired) ProcessMutex?.ReleaseMutex(); }
        }
        catch { return ([], 0, 0, 0); }
    }

    public class TelemetryEntry
    {
        [JsonPropertyName("ts")] public string Timestamp { get; set; } = string.Empty;
        [JsonPropertyName("image")] public string Image { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? ImageDigest { get; set; }
        [JsonPropertyName("tool")] public string Tool { get; set; } = string.Empty;
        [JsonPropertyName("duration_s")] public double DurationSeconds { get; set; }
        [JsonPropertyName("exit")] public long ExitCode { get; set; }
        [JsonPropertyName("cancelled")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool WasCancelled { get; set; }
        [JsonPropertyName("docker_run")] public string? DockerRunCommand { get; set; }
        [JsonPropertyName("peak_mem")] public long? PeakMemoryBytes { get; set; }
        [JsonPropertyName("max_cpu")] public double? MaxCpuPercent { get; set; }
        [JsonPropertyName("oom")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool OomKilled { get; set; }
        [JsonPropertyName("error_msg")] public string? ErrorMessage { get; set; }
    }

    public class TelemetryErrorEntry
    {
        [JsonPropertyName("ts")] public string Timestamp { get; set; } = string.Empty;
        [JsonPropertyName("cmp")] public string Component { get; set; } = string.Empty;
        [JsonPropertyName("act")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("ex_msg")] public string? ExceptionMessage { get; set; }
        [JsonPropertyName("stack")] public string? StackTrace { get; set; }
        [JsonPropertyName("ctx")] public string? Context { get; set; }
    }
}

[JsonSerializable(typeof(ContainerTelemetry.TelemetryEntry))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
internal partial class TelemetryJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(ContainerTelemetry.TelemetryErrorEntry))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
internal partial class ErrorJsonContext : JsonSerializerContext { }