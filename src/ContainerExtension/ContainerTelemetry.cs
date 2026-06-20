using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ContainerExtension;

/// <summary>
/// Provides high-performance, cross-process synchronized JSON Lines telemetry logging.
/// Captures execution metrics, tool errors, and structural logs without large object heap (LOH) allocations.
/// </summary>
public static partial class ContainerTelemetry
{
    public static Func<bool> TelemetryOptedOutChecker { get; set; } = () => false;

    private static readonly string CachedUserName = Environment.UserName;
    private static readonly string CachedUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string CachedResolvedBaseDir = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oneware"));
    private static readonly string CachedResolvedTempBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OneWare", "ContainerExtension"));

    private static string _telemetryDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oneware");
    private static string _telemetryPath = Path.Combine(_telemetryDir, "container_telemetry.jsonl");
    private static string _errorTelemetryPath = Path.Combine(_telemetryDir, "container_errors.jsonl");

    [GeneratedRegex(@"\bAIza[0-9A-Za-z-_]{35}\b|\b(AKIA|ASIA|AGPA|AIDA|AROA|AIPA|ANPA|ANVA)[A-Z0-9]{16}\b", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex CloudKeyRegex();

    public static string TelemetryFilePath => _telemetryPath;
    
    public static Func<string> LogLevelChecker { get; set; } = () => "Verbose";

    private static int CurrentLogLevelRank
    {
        get
        {
            var level = LogLevelChecker?.Invoke() ?? "Verbose";
            return level switch
            {
                "Off" => 0,
                "Errors Only" => 1,
                "Info" => 2,
                "Verbose" => 3,
                _ => 3
            };
        }
    }

    public static bool IsVerbose => CurrentLogLevelRank >= 3;

    private static System.Threading.Channels.Channel<TelemetryErrorEntry> ErrorChannel =
        System.Threading.Channels.Channel.CreateUnbounded<TelemetryErrorEntry>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    static ContainerTelemetry()
    {
        _ = Task.Run(ProcessErrorChannelAsync);
    }

    private static async Task ProcessErrorChannelAsync()
    {
        var reader = ErrorChannel.Reader;
        await foreach (var entry in reader.ReadAllAsync().ConfigureAwait(false))
        {
            WriteErrorEntryToDisk(entry);
        }
    }

    internal static bool IsTestEnvironment { get; set; }

    private static readonly System.Threading.Lock MutexLock = new();

    // -- Testing Hook --------------------------------------------------------
    /// <summary>Isolates telemetry to a temporary directory during xUnit test execution.</summary>
    internal static void InitializeTestEnvironment(string tempDir)
    {
        _telemetryDir = tempDir;
        _telemetryPath = Path.Combine(_telemetryDir, "container_telemetry.jsonl");
        _errorTelemetryPath = Path.Combine(_telemetryDir, "container_errors.jsonl");
        IsTestEnvironment = true;
        lock (MutexLock)
        {
            ProcessMutexLazy = new Lazy<Mutex?>(CreateProcessMutex, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        Interlocked.Exchange(ref _isShutdown, 0);
        _cachedLineCount = -1;
        _cachedErrorLineCount = -1;
        Volatile.Write(ref _cachedStats, null);
        Volatile.Write(ref _telemetryDirVerified, false);

        ErrorChannel = System.Threading.Channels.Channel.CreateUnbounded<TelemetryErrorEntry>(new System.Threading.Channels.UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _ = Task.Run(ProcessErrorChannelAsync);
    }

    private sealed class CachedStats(
        List<TelemetryEntry> entries,
        int totalRuns,
        double successRate,
        double avgDuration,
        int cachedCount,
        DateTime lastFileWriteTime,
        long lastFileLength,
        string lastFilePath)
    {
        public List<TelemetryEntry> Entries { get; } = entries;
        public int TotalRuns { get; } = totalRuns;
        public double SuccessRate { get; } = successRate;
        public double AvgDuration { get; } = avgDuration;
        public int CachedCount { get; } = cachedCount;
        public DateTime LastFileWriteTime { get; } = lastFileWriteTime;
        public long LastFileLength { get; } = lastFileLength;
        public string LastFilePath { get; } = lastFilePath;
    }

    private static int _cachedLineCount = -1;
    private static int _cachedErrorLineCount = -1;
    private static CachedStats? _cachedStats;
    private static int _isShutdown = 0;
    private static bool _telemetryDirVerified;
    private static readonly System.Threading.Lock VerificationLock = new();

    private static readonly ReaderWriterLockSlim RwLock = new();
    private static Lazy<Mutex?> ProcessMutexLazy = new(CreateProcessMutex, LazyThreadSafetyMode.ExecutionAndPublication);
    private static Mutex? ProcessMutex
    {
        get
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return null;
            }
            lock (MutexLock)
            {
                if (Volatile.Read(ref _isShutdown) == 1)
                {
                    return null;
                }
                return ProcessMutexLazy.Value;
            }
        }
    }

    private static Mutex? CreateProcessMutex()
    {
        if (IsTestEnvironment)
        {
            return null;
        }
        try
        {
            var userName = CachedUserName;
            var safeUserName = string.IsNullOrWhiteSpace(userName) ? "Default" : string.Concat(userName.Where(char.IsLetterOrDigit));
            var prefix = OperatingSystem.IsWindows() ? "Global\\" : "";
            return new Mutex(false, $"{prefix}OneWareContainerTelemetryLock_{safeUserName}");
        }
        catch
        {
            return null;
        }
    }

    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _isShutdown, 1) == 1)
        {
            return;
        }
        try
        {
            ErrorChannel.Writer.TryComplete();
        }
        catch
        {
            // Ignore channel completion failures during shutdown
        }
        try
        {
            lock (MutexLock)
            {
                if (ProcessMutexLazy.IsValueCreated)
                {
                    var mutex = ProcessMutexLazy.Value;
                    mutex?.Dispose();
                }
                ProcessMutexLazy = new Lazy<Mutex?>(CreateProcessMutex, LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }
        catch (Exception)
        {
            // Mutex disposal exceptions can be ignored during shutdown
        }
    }

    public static void ResetShutdown()
    {
        Interlocked.Exchange(ref _isShutdown, 0);
        lock (MutexLock)
        {
            if (ProcessMutexLazy.IsValueCreated && ProcessMutexLazy.Value == null)
            {
                ProcessMutexLazy = new Lazy<Mutex?>(CreateProcessMutex, LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }
    }

    private static bool IsSubpath(string path, string basePath)
    {
        var resolvedPath = GetCanonicalPath(path);
        var resolvedBase = GetCanonicalPath(basePath);

        var comp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (resolvedPath.Equals(resolvedBase, comp))
        {
            return true;
        }

        var suffix = resolvedBase.EndsWith(Path.DirectorySeparatorChar) || resolvedBase.EndsWith(Path.AltDirectorySeparatorChar)
            ? "" : Path.DirectorySeparatorChar.ToString();

        return resolvedPath.StartsWith(resolvedBase + suffix, comp);
    }

    private static string GetCanonicalPath(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            var info = new System.IO.DirectoryInfo(resolved);
            if (info.Exists)
            {
                var target = info.LinkTarget;
                if (!string.IsNullOrEmpty(target))
                {
                    return info.ResolveLinkTarget(true)?.FullName ?? info.FullName;
                }
            }
            var fileInfo = new System.IO.FileInfo(resolved);
            if (fileInfo.Exists)
            {
                var target = fileInfo.LinkTarget;
                if (!string.IsNullOrEmpty(target))
                {
                    return fileInfo.ResolveLinkTarget(true)?.FullName ?? fileInfo.FullName;
                }
            }
            return resolved;
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static void EnsureDirectoryAndFileSecure(string dir, string filepath)
    {
        if (Volatile.Read(ref _telemetryDirVerified))
        {
            return;
        }

        lock (VerificationLock)
        {
            if (_telemetryDirVerified)
            {
                return;
            }

            var resolvedDir = Path.GetFullPath(dir);
            var resolvedFile = Path.GetFullPath(filepath);

            var rootDir = Path.GetPathRoot(resolvedDir);
            if (string.Equals(resolvedDir, rootDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Root directory path is strictly prohibited for telemetry configurations.", nameof(dir));
            }

            if (!IsTestEnvironment)
            {
                bool isTempFallback = IsSubpath(resolvedDir, CachedResolvedTempBase);

                if (!isTempFallback)
                {
                    if (!IsSubpath(resolvedDir, CachedResolvedBaseDir))
                    {
                        throw new ArgumentException("Telemetry directory must resolve to a subpath of the user profile .oneware directory.", nameof(dir));
                    }
                    if (!IsSubpath(resolvedFile, CachedResolvedBaseDir))
                    {
                        throw new ArgumentException("Telemetry file path must resolve to a subpath of the user profile .oneware directory.", nameof(filepath));
                    }
                }
            }

            try
            {
                Directory.CreateDirectory(resolvedDir);
                var probePath = Path.Combine(resolvedDir, ".write_probe_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probePath, "probe");
                File.Delete(probePath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                var fallbackDir = Path.Combine(Path.GetTempPath(), "OneWare", "ContainerExtension");
                try
                {
                    Directory.CreateDirectory(fallbackDir);
                    _telemetryDir = fallbackDir;
                    _telemetryPath = Path.Combine(_telemetryDir, "container_telemetry.jsonl");
                    _errorTelemetryPath = Path.Combine(_telemetryDir, "container_errors.jsonl");
                    resolvedDir = _telemetryDir;
                    resolvedFile = string.Equals(filepath, _errorTelemetryPath, StringComparison.Ordinal) ? _errorTelemetryPath : _telemetryPath;
                    Directory.CreateDirectory(resolvedDir);
                }
                catch
                {
                    return;
                }
            }

            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(resolvedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Best-effort
                    }
                    if (File.Exists(resolvedFile))
                    {
                        try
                        {
                            File.SetUnixFileMode(resolvedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Best-effort
                        }
                    }
                }
                else
                {
                    if (File.Exists(resolvedFile))
                    {
                        try
                        {
                            File.Encrypt(resolvedFile);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Best-effort
                        }
                        catch
                        {
                            // Best-effort encryption on Windows
                        }
                    }
                }
                Volatile.Write(ref _telemetryDirVerified, true);
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort
            }
            catch (Exception)
            {
                // Best-effort directory and file permission enforcement
            }
        }
    }

    [GeneratedRegex(@"(?<=\b)(?<key>[a-zA-Z0-9_\-]*?(?:PASSWORD|PWD|CREDENTIALS|AUTH|PASS|TOKEN|SECRET|KEY)[a-zA-Z0-9_\-]*?)=(?<quote>[""']?)(?:[^\s""']+|(?<=[""'])[^\n\r]*?)\k<quote>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretScrubRegex();

    [GeneratedRegex(@"\\\\[^\s\\/]+(?:\\[^\s\\/]+)+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UncShareRegex();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b|\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b|\b[a-zA-Z0-9\-]+(?:\.local|\.lan)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IpRedactRegex();

    private static string? ScrubSecrets(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return commandLine;
        }
        try
        {
            return SecretScrubRegex().Replace(commandLine,
                m => $"{m.Groups["key"].Value}={m.Groups["quote"].Value}***{m.Groups["quote"].Value}");
        }
        catch (Exception)
        {
            return commandLine;
        }
    }

    private static string? ScrubHomePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        try
        {
            var home = CachedUserProfile;
            if (!string.IsNullOrEmpty(home))
            {
                return path.Replace(home, "~");
            }
        }
        catch (Exception)
        {
            // Ignored to prevent telemetry failures from interrupting execution
        }
        return path;
    }

    private static string? ScrubSensitiveInfo(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var scrubbed = ScrubHomePath(input);
        if (string.IsNullOrEmpty(scrubbed)) return scrubbed;
        try
        {
            scrubbed = UncShareRegex().Replace(scrubbed, "[REDACTED_UNC_SHARE]");
        }
        catch { /* Ignore */ }
        try
        {
            scrubbed = CloudKeyRegex().Replace(scrubbed, "[REDACTED_KEY]");
        }
        catch { /* Ignore */ }
        try
        {
            var user = CachedUserName;
            if (!string.IsNullOrWhiteSpace(user))
            {
                scrubbed = scrubbed.Replace(user, "***", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* Ignore */ }
        try
        {
            scrubbed = IpRedactRegex().Replace(scrubbed, "[REDACTED_NET_ADDR]");
        }
        catch { /* Ignore */ }
        return scrubbed;
    }

    private static readonly SearchValues<byte> WhiteSpaceBytes = SearchValues.Create(" \t\r\n"u8);
    private static bool IsBytesWhiteSpace(ReadOnlySpan<byte> span)
    {
        return !span.ContainsAnyExcept(WhiteSpaceBytes);
    }

    private static int CountLinesSafe(string path)
    {
        if (!File.Exists(path)) return 0;
        int count = 0;
        bool success = false;
        int delay = 15;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16384);
                try
                {
                    int bytesRead;
                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var span = new ReadOnlySpan<byte>(buffer, 0, bytesRead);
                        int index;
                        int offset = 0;
                        while ((index = span[offset..].IndexOf((byte)'\n')) >= 0)
                        {
                            count++;
                            offset += index + 1;
                        }
                    }
                    success = true;
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (attempt == 4) break;
                Thread.Sleep(delay);
                delay *= 2;
            }
            if (success)
            {
                break;
            }
        }
        return count;
    }

    private static List<string> ReadLastLinesSafe(string path, int count)
    {
        if (!File.Exists(path)) return [];
        var results = new List<string>(count > 0 ? count : 1);
        int delay = 15;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long fileLength = stream.Length;
                if (fileLength == 0) return [];

                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
                var lineBytes = new List<byte>();
                try
                {
                    long position = fileLength;
                    int linesFound = 0;
                    while (position > 0 && linesFound < count)
                    {
                        int toRead = (int)Math.Min(buffer.Length, position);
                        position -= toRead;
                        stream.Position = position;
                        int read = stream.Read(buffer, 0, toRead);

                        for (int i = read - 1; i >= 0 && linesFound < count; i--)
                        {
                            byte b = buffer[i];
                            if (b == '\n')
                            {
                                if (lineBytes.Count > 0)
                                {
                                    lineBytes.Reverse();
                                    var line = System.Text.Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lineBytes));
                                    if (line.Length > 65536) line = line[..65536];
                                    results.Add(line);
                                    lineBytes.Clear();
                                    linesFound++;
                                }
                            }
                            else if (b != '\r')
                            {
                                lineBytes.Add(b);
                            }
                        }
                    }
                    if (lineBytes.Count > 0 && linesFound < count)
                    {
                        lineBytes.Reverse();
                        var line = System.Text.Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lineBytes));
                        if (line.Length > 65536) line = line[..65536];
                        results.Add(line);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }

                results.Reverse();
                return results;
            }
            catch (IOException)
            {
                if (attempt == 4) break;
                Thread.Sleep(delay);
                delay *= 2;
            }
            catch (Exception)
            {
                break;
            }
        }
        return results;
    }

    /// <summary>
    /// Writes a structured telemetry entry to the unified execution log.
    /// Handles PII redaction and thread-safe file appends automatically.
    /// </summary>
    public static void LogExecution(
      string image, string tool, double durationSeconds, long exitCode, string? imageDigest = null,
      bool wasCancelled = false, string? dockerRunCommand = null, long? peakMemoryBytes = null,
      double? maxCpuPercent = null, bool oomKilled = false, int maxEntries = 0, string? errorMessage = null,
      bool isDebug = false)
    {
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return;
        }
        var checker = TelemetryOptedOutChecker;
        if (checker != null && checker())
        {
            return;
        }
        var rank = CurrentLogLevelRank;
        if (rank <= 0)
        {
            return;
        }
        if (rank == 1 && exitCode == 0 && errorMessage == null && !wasCancelled)
        {
            return;
        }
        if (!IsVerbose && isDebug)
        {
            return;
        }
        try
        {
            EnsureDirectoryAndFileSecure(_telemetryDir, _telemetryPath);

            var entry = new TelemetryEntry
            {
                Timestamp = DateTime.UtcNow,
                Image = image ?? string.Empty,
                ImageDigest = imageDigest,
                Tool = tool != null ? Path.GetFileNameWithoutExtension(tool) : string.Empty,
                DurationSeconds = Math.Round(durationSeconds, 4),
                ExitCode = exitCode,
                WasCancelled = wasCancelled,
                DockerRunCommand = ScrubSensitiveInfo(ScrubSecrets(dockerRunCommand)),
                PeakMemoryBytes = peakMemoryBytes,
                MaxCpuPercent = maxCpuPercent.HasValue ? Math.Round(maxCpuPercent.Value, 1) : null,
                OomKilled = oomKilled,
                ErrorMessage = ScrubSensitiveInfo(errorMessage)
            };

            var mutex = ProcessMutex;
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex?.WaitOne(TimeSpan.FromSeconds(10)) ?? true;
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                catch (Exception)
                {
                    acquired = false;
                }
                if (!acquired)
                {
                    return;
                }

                RwLock.EnterWriteLock();
                try
                {
                    EnsureFileLimit(_telemetryPath);
                    bool written = false;
                    int delay = 15;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            using (var fs = new FileStream(_telemetryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            {
                                JsonSerializer.Serialize(fs, entry, TelemetryJsonContext.Default.TelemetryEntry);
                                fs.WriteByte((byte)'\n');
                            }
                            written = true;
                            break;
                        }
                        catch (IOException)
                        {
                            if (attempt == 4)
                            {
                                break;
                            }
                            Thread.Sleep(delay);
                            delay *= 2;
                        }
                        catch (JsonException ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Telemetry serialization failed: {ex.Message}");
                            break;
                        }
                    }

                    if (!OperatingSystem.IsWindows() && written)
                    {
                        try
                        {
                            File.SetUnixFileMode(_telemetryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        }
                        catch (Exception)
                        {
                            // Best-effort file permission enforcement 
                        }
                    }

                    if (maxEntries > 0)
                    {
                        if (_cachedLineCount < 0)
                        {
                            _cachedLineCount = CountLinesSafe(_telemetryPath);
                        }
                        else
                        {
                            _cachedLineCount++;
                        }

                        var trimThreshold = (int)(maxEntries * 1.2);
                        if (_cachedLineCount > trimThreshold)
                        {
                            var path = _telemetryPath;
                            var max = maxEntries;
                            if (IsTestEnvironment)
                            {
                                TrimTelemetryFileInternal(path, max);
                            }
                            else
                            {
                                ThreadPool.QueueUserWorkItem(_ =>
                                {
                                    TrimTelemetryFile(path, max);
                                });
                            }
                            _cachedLineCount = maxEntries;
                        }
                    }
                    else
                    {
                        if (_cachedLineCount < 0)
                        {
                            _cachedLineCount = CountLinesSafe(_telemetryPath);
                        }
                        else
                        {
                            _cachedLineCount++;
                        }
                    }
                }
                finally
                {
                    RwLock.ExitWriteLock();
                }
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore disposal race
                    }
                }
            }
        }
        catch (IOException ioEx) when (ioEx.HResult == unchecked((int)0x80070070) || ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase) || ioEx.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("[ContainerTelemetry] Error: Insufficient disk space to write telemetry.");
        }
        catch { /* Best-effort execution */ }
    }

    /// <summary>
    /// Asynchronously records a non-fatal error or exception to the error telemetry log.
    /// Errors are processed by a background channel to ensure UI/Execution threads remain unblocked.
    /// </summary>
    public static void TrackError(string component, string action, Exception? ex = null, string? context = null)
    {
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return;
        }
        var checker = TelemetryOptedOutChecker;
        if (checker != null && checker())
        {
            return;
        }
        if (CurrentLogLevelRank <= 0)
        {
            return;
        }
        try
        {
            var entry = new TelemetryErrorEntry
            {
                Timestamp = DateTime.UtcNow,
                Component = component ?? string.Empty,
                Action = action ?? string.Empty,
                ExceptionMessage = ScrubSensitiveInfo(ex?.Message),
                StackTrace = IsVerbose ? ScrubSensitiveInfo(ex?.StackTrace) : null,
                Context = ScrubSensitiveInfo(context)
            };

            ErrorChannel.Writer.TryWrite(entry);
        }
        catch
        {
            // Ignored to ensure telemetry failure does not crash the host
        }
    }

    private static void EnsureFileLimit(string filepath)
    {
        try
        {
            if (File.Exists(filepath))
            {
                var fileInfo = new FileInfo(filepath);
                if (fileInfo.Length > 50 * 1024 * 1024) // 50MB hard cap
                {
                    TrimTelemetryFileInternal(filepath, 50); // Aggressive trim to drop size immediately
                }
                else if (fileInfo.Length > 10 * 1024 * 1024) // 10MB standard limit
                {
                    TrimTelemetryFileInternal(filepath, 200);
                }
            }
        }
        catch
        {
            // Best-effort
        }
    }

    private static void WriteErrorEntryToDisk(TelemetryErrorEntry entry)
    {
        try
        {
            EnsureDirectoryAndFileSecure(_telemetryDir, _errorTelemetryPath);
            EnsureFileLimit(_errorTelemetryPath);

            var mutex = ProcessMutex;
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex?.WaitOne(TimeSpan.FromSeconds(10)) ?? true;
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                catch (Exception)
                {
                    acquired = false;
                }
                if (!acquired)
                {
                    return;
                }

                RwLock.EnterWriteLock();
                try
                {
                    bool written = false;
                    int delay = 15;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            using (var fs = new FileStream(_errorTelemetryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            {
                                JsonSerializer.Serialize(fs, entry, ErrorJsonContext.Default.TelemetryErrorEntry);
                                fs.WriteByte((byte)'\n');
                            }
                            written = true;
                            break;
                        }
                        catch (IOException)
                        {
                            if (attempt == 4)
                            {
                                break;
                            }
                            Thread.Sleep(delay);
                            delay *= 2;
                        }
                        catch (JsonException ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error telemetry serialization failed: {ex.Message}");
                            break;
                        }
                    }

                    if (!OperatingSystem.IsWindows() && written)
                    {
                        try
                        {
                            File.SetUnixFileMode(_errorTelemetryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            // Best-effort
                        }
                        catch (Exception)
                        {
                            // Best-effort
                        }
                    }

                    const int maxErrorEntries = 500;
                    const int errorTrimThreshold = 600;

                    if (_cachedErrorLineCount < 0)
                    {
                        _cachedErrorLineCount = CountLinesSafe(_errorTelemetryPath);
                    }
                    else
                    {
                        _cachedErrorLineCount++;
                    }

                    if (_cachedErrorLineCount > errorTrimThreshold)
                    {
                        var path = _errorTelemetryPath;
                        if (IsTestEnvironment)
                        {
                            TrimTelemetryFileInternal(path, maxErrorEntries);
                        }
                        else
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                TrimTelemetryFile(path, maxErrorEntries);
                            });
                        }
                        _cachedErrorLineCount = maxErrorEntries;
                    }
                }
                finally
                {
                    RwLock.ExitWriteLock();
                }
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
        }
        catch (IOException ioEx) when (ioEx.HResult == unchecked((int)0x80070070) || ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase) || ioEx.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("[ContainerTelemetry] Error: Insufficient disk space to write telemetry.");
        }
        catch
        {
            // Ignored to ensure telemetry failure does not crash the host
        }
    }

    public static List<TelemetryEntry> GetRecentEntries(int count = 20)
    {
        var results = new List<TelemetryEntry>(count > 0 ? count : 20);
        try
        {
            var mutex = ProcessMutex;
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true;
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                catch (Exception)
                {
                    acquired = false;
                }
                if (!acquired || !File.Exists(_telemetryPath))
                {
                    return results;
                }

                List<string> lastLines;
                RwLock.EnterReadLock();
                try
                {
                    lastLines = ReadLastLinesSafe(_telemetryPath, count);
                }
                finally
                {
                    RwLock.ExitReadLock();
                }

                for (int i = lastLines.Count - 1; i >= 0; i--)
                {
                    var line = lastLines[i];
                    if (line.AsSpan().IsWhiteSpace())
                    {
                        continue;
                    }
                    try
                    {
                        var entry = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEntry);
                        if (entry != null && entry.IsValid())
                        {
                            results.Add(entry);
                        }
                    }
                    catch (Exception ex)
                    {
                        TrackError("ContainerTelemetry", "Failed to deserialize telemetry line", ex, line);
                    }
                }
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore disposal race
                    }
                }
            }
        }
        catch
        {
            /* Ignored to ensure telemetry read failure does not crash the host */
        }
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
            var mutex = ProcessMutex;
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true;
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                catch (Exception)
                {
                    acquired = false;
                }
                if (!acquired || !File.Exists(_telemetryPath))
                {
                    return false;
                }

                RwLock.EnterReadLock();
                try
                {
                    var destDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    using var source = new FileStream(_telemetryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dest = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);

                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (string.IsNullOrEmpty(home))
                    {
                        source.CopyTo(dest);
                    }
                    else
                    {
                        byte[] homeBytes = System.Text.Encoding.UTF8.GetBytes(home);
                        byte[] replacementBytes = [(byte)'~'];
                        CopyStreamWithReplacement(source, dest, homeBytes, replacementBytes);
                    }
                }
                finally
                {
                    RwLock.ExitReadLock();
                }
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore disposal race
                    }
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyStreamWithReplacement(Stream source, Stream destination, byte[] target, byte[] replacement)
    {
        if (target.Length == 0)
        {
            source.CopyTo(destination);
            return;
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int bytesRead;
            int matchIndex = 0;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                int start = 0;
                int i = 0;
                while (i < bytesRead)
                {
                    if (buffer[i] == target[matchIndex])
                    {
                        matchIndex++;
                        if (matchIndex == target.Length)
                        {
                            int matchStartInBuf = i - target.Length + 1;
                            if (matchStartInBuf >= start)
                            {
                                destination.Write(buffer, start, matchStartInBuf - start);
                            }
                            destination.Write(replacement, 0, replacement.Length);
                            start = i + 1;
                            matchIndex = 0;
                        }
                    }
                    else
                    {
                        if (matchIndex > 0)
                        {
                            i -= matchIndex;
                            matchIndex = 0;
                        }
                    }
                    i++;
                }

                if (matchIndex > 0)
                {
                    if (source.Position >= source.Length)
                    {
                        int writeLen = bytesRead - start;
                        if (writeLen > 0)
                        {
                            destination.Write(buffer, start, writeLen);
                        }
                        matchIndex = 0;
                    }
                    else
                    {
                        int writeLen = bytesRead - start - matchIndex;
                        if (writeLen > 0)
                        {
                            destination.Write(buffer, start, writeLen);
                        }
                        source.Position -= matchIndex;
                        matchIndex = 0;
                    }
                }
                else
                {
                    int writeLen = bytesRead - start;
                    if (writeLen > 0)
                    {
                        destination.Write(buffer, start, writeLen);
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void ClearEntries()
    {
        try
        {
            var mutex = ProcessMutex;
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true;
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                catch (Exception)
                {
                    acquired = false;
                }
                if (!acquired)
                {
                    return;
                }

                RwLock.EnterWriteLock();
                try
                {
                    // FileMode.Create safely truncates resolving IOException on 0-byte files natively.
                    if (File.Exists(_telemetryPath))
                    {
                        int clearDelay = 15;
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                using (new FileStream(_telemetryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { /* Truncate file */ }
                                break;
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                if (attempt == 4)
                                {
                                    break;
                                }
                                Thread.Sleep(clearDelay);
                                clearDelay *= 2;
                            }
                        }
                    }

                    if (File.Exists(_errorTelemetryPath))
                    {
                        int clearErrorDelay = 15;
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                using (new FileStream(_errorTelemetryPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { /* Truncate file */ }
                                break;
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                if (attempt == 4)
                                {
                                    break;
                                }
                                Thread.Sleep(clearErrorDelay);
                                clearErrorDelay *= 2;
                            }
                        }
                    }

                    _cachedLineCount = -1;
                    _cachedErrorLineCount = -1;
                    Volatile.Write(ref _cachedStats, null);
                }
                finally
                {
                    RwLock.ExitWriteLock();
                }
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore disposal race
                    }
                }
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private static void ProcessLineStats(ReadOnlySpan<byte> lineSpan, ref int successes, ref double totalDuration, ref int durationCount, out bool isCancelled)
    {
        isCancelled = false;
        long exitCode = -1;
        double duration = 0;
        bool hasDuration = false;
        bool hasExitCode = false;

        try
        {
            var reader = new Utf8JsonReader(lineSpan);
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("cancelled"u8))
                        {
                            reader.Read();
                            isCancelled = reader.GetBoolean();
                        }
                        else if (reader.ValueTextEquals("exit"u8))
                        {
                            reader.Read();
                            exitCode = reader.GetInt64();
                            hasExitCode = true;
                        }
                        else if (reader.ValueTextEquals("duration_s"u8))
                        {
                            reader.Read();
                            duration = reader.GetDouble();
                            hasDuration = true;
                        }
                        else
                        {
                            reader.Read();
                            reader.Skip();
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback: if JSON parsing fails, treat as not cancelled
        }

        if (hasExitCode && exitCode == 0 && !isCancelled)
        {
            successes++;
        }

        if (!isCancelled && hasDuration)
        {
            totalDuration += duration;
            durationCount++;
        }
    }

    /// <summary>
    /// Reads and parses the most recent telemetry entries from disk, computing aggregated statistics 
    /// (success rate, average duration) over the history.
    /// </summary>
    /// <param name="count">The maximum number of recent entries to return.</param>
    /// <returns>A tuple containing the list of entries and pre-calculated statistics.</returns>
    public static (List<TelemetryEntry> entries, int totalRuns, double successRate, double avgDuration) GetRecentEntriesWithStats(int count = 20)
    {
        try
        {
            var mutex = ProcessMutex;
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex?.WaitOne(TimeSpan.FromSeconds(3)) ?? true;
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                catch (Exception)
                {
                    acquired = false;
                }
                var exists = File.Exists(_telemetryPath);
                if (!acquired || !exists)
                {
                    return ([], 0, 0, 0);
                }

                var results = new List<TelemetryEntry>(count > 0 ? count : 20);
                int total = 0, successes = 0, durationCount = 0;
                double totalDuration = 0;
                var lastLines = new List<string>(count > 0 ? count : 20);

                var initialWriteTime = exists ? File.GetLastWriteTimeUtc(_telemetryPath) : DateTime.MinValue;
                var initialLength = exists ? new FileInfo(_telemetryPath).Length : 0;
                var cache = Volatile.Read(ref _cachedStats);
                if (cache != null && cache.CachedCount == count && cache.LastFileWriteTime == initialWriteTime && cache.LastFileLength == initialLength && string.Equals(cache.LastFilePath, _telemetryPath, StringComparison.Ordinal))
                {
                    return (new List<TelemetryEntry>(cache.Entries), cache.TotalRuns, cache.SuccessRate, cache.AvgDuration);
                }

                RwLock.EnterReadLock();
                try
                {
                    if (exists)
                    {
                        bool readSuccess = false;
                        int readDelay = 15;
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                using var stream = new FileStream(_telemetryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(32768);
                                var lineBytes = new List<byte>();
                                try
                                {
                                    int bytesRead;
                                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        int start = 0;
                                        for (int i = 0; i < bytesRead; i++)
                                        {
                                            if (buffer[i] == '\n')
                                            {
                                                ReadOnlySpan<byte> lineSpan;
                                                if (lineBytes.Count > 0)
                                                {
                                                    lineBytes.AddRange(new ReadOnlySpan<byte>(buffer, start, i - start));
                                                    lineSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lineBytes);
                                                }
                                                else
                                                {
                                                    lineSpan = new ReadOnlySpan<byte>(buffer, start, i - start);
                                                }

                                                if (!IsBytesWhiteSpace(lineSpan))
                                                {
                                                    total++;
                                                    ProcessLineStats(lineSpan, ref successes, ref totalDuration, ref durationCount, out _);
                                                }

                                                lineBytes.Clear();
                                                start = i + 1;
                                            }
                                        }
                                        if (start < bytesRead)
                                        {
                                            lineBytes.AddRange(new ReadOnlySpan<byte>(buffer, start, bytesRead - start));
                                        }
                                    }
                                    if (lineBytes.Count > 0)
                                    {
                                        var lineSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lineBytes);
                                        if (!IsBytesWhiteSpace(lineSpan))
                                        {
                                            total++;
                                            ProcessLineStats(lineSpan, ref successes, ref totalDuration, ref durationCount, out _);
                                        }
                                    }
                                    readSuccess = true;
                                }
                                finally
                                {
                                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                                }
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                if (attempt == 4)
                                {
                                    break;
                                }
                                Thread.Sleep(readDelay);
                                readDelay *= 2;
                            }
                            if (readSuccess)
                            {
                                break;
                            }
                        }

                        if (readSuccess && count > 0)
                        {
                            lastLines = ReadLastLinesSafe(_telemetryPath, count);
                        }
                    }
                }
                finally
                {
                    RwLock.ExitReadLock();
                }

                for (int i = lastLines.Count - 1; i >= 0; i--)
                {
                    var line = lastLines[i];
                    try
                    {
                        var entry = JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEntry);
                        if (entry != null && entry.IsValid())
                        {
                            results.Add(entry);
                        }
                    }
                    catch (JsonException ex)
                    {
                        TrackError("ContainerTelemetry", "Failed to deserialize stats line", ex, line);
                    }
                }

                var successRate = total > 0 ? Math.Round((double)successes / total * 100, 1) : 0;
                var avgDuration = durationCount > 0 ? Math.Round(totalDuration / durationCount, 2) : 0;

                var finalWriteTime = exists ? File.GetLastWriteTimeUtc(_telemetryPath) : DateTime.MinValue;
                var finalLength = exists ? new FileInfo(_telemetryPath).Length : 0;
                var newCache = new CachedStats(results, total, successRate, avgDuration, count, finalWriteTime, finalLength, _telemetryPath);
                Volatile.Write(ref _cachedStats, newCache);

                return (results, total, successRate, avgDuration);
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                        // Ignore disposal race
                    }
                }
            }
        }
        catch
        {
            return ([], 0, 0, 0);
        }
    }

    private static void TrimTelemetryFile(string path, int maxEntries)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return;
        }
        var mutex = ProcessMutex;
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex?.WaitOne(TimeSpan.FromSeconds(5)) ?? true;
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            catch (Exception)
            {
                acquired = false;
            }
            if (!acquired)
            {
                return;
            }

            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return;
            }
            RwLock.EnterWriteLock();
            try
            {
                TrimTelemetryFileInternal(path, maxEntries);
            }
            finally
            {
                RwLock.ExitWriteLock();
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            if (acquired && mutex != null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }

    private static void TrimTelemetryFileInternal(string path, int maxEntries)
    {
        try
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return;
            }
            if (!File.Exists(path))
            {
                return;
            }

            var q = ReadLastLinesSafe(path, maxEntries);
            if (q.Count == 0)
            {
                return;
            }
            // ReadLastLinesSafe already returns chronological order (oldest first) because it calls results.Reverse() internally.

            var tempPath = path + ".tmp";
            try
            {
                int trimDelay = 15;
                bool moveSuccess = false;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    if (Volatile.Read(ref _isShutdown) == 1)
                    {
                        return;
                    }
                    try
                    {
                        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        using (var writer = new StreamWriter(fs))
                        {
                            foreach (var line in q)
                            {
                                writer.WriteLine(line);
                            }
                        }

                        try
                        {
                            if (File.Exists(path))
                            {
                                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                            }
                            else
                            {
                                File.Move(tempPath, path, overwrite: true);
                            }
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            File.Move(tempPath, path, overwrite: true);
                        }
                        if (!OperatingSystem.IsWindows())
                        {
                            try
                            {
                                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            }
                            catch (Exception)
                            {
                                // Best-effort file permission enforcement 
                            }
                        }
                        moveSuccess = true;
                        break;
                    }
                    catch (IOException)
                    {
                        try
                        {
                            if (File.Exists(tempPath))
                            {
                                File.Delete(tempPath);
                            }
                        }
                        catch
                        {
                            // Ignore
                        }
                        if (attempt == 4)
                        {
                            break;
                        }
                        Thread.Sleep(trimDelay);
                        trimDelay *= 2;
                    }
                }
                if (!moveSuccess)
                {
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        using var writer = new StreamWriter(fs);
                        foreach (var line in q)
                        {
                            writer.WriteLine(line);
                        }
                        if (!OperatingSystem.IsWindows())
                        {
                            try
                            {
                                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            }
                            catch (Exception)
                            {
                                // Ignore
                            }
                        }
                    }
                    catch
                    {
                        // Best-effort fallback
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }
        catch (IOException ioEx) when (ioEx.HResult == unchecked((int)0x80070070) || ioEx.Message.Contains("disk full", StringComparison.OrdinalIgnoreCase) || ioEx.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("[ContainerTelemetry] Error: Insufficient disk space during telemetry trim.");
        }
        catch (Exception ex)
        {
            TrackError("ContainerTelemetry", "TrimTelemetryFileInternal failed", ex, path);
        }
    }

    public class TelemetryEntry
    {
        [JsonPropertyName("ts")] public DateTime Timestamp { get; set; }
        [JsonPropertyName("image")] public string Image { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? ImageDigest { get; set; }
        [JsonPropertyName("tool")] public string Tool { get; set; } = string.Empty;
        [JsonPropertyName("duration_s")] public double DurationSeconds { get; set; }
        [JsonPropertyName("exit")] public long ExitCode { get; set; }
        [JsonPropertyName("cancelled")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool WasCancelled { get; set; }
        [JsonPropertyName("docker_run")] public string? DockerRunCommand { get; set; }
        [JsonPropertyName("peak_mem")] public long? PeakMemoryBytes { get; set; }
        [JsonPropertyName("max_cpu")] public double? MaxCpuPercent { get; set; }
        [JsonPropertyName("oom")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool OomKilled { get; set; }
        [JsonPropertyName("error_msg")] public string? ErrorMessage { get; set; }

        public bool IsValid() => Timestamp != default && !string.IsNullOrEmpty(Image) && !string.IsNullOrEmpty(Tool);
    }

    public class TelemetryErrorEntry
    {
        [JsonPropertyName("ts")] public DateTime Timestamp { get; set; }
        [JsonPropertyName("cmp")] public string Component { get; set; } = string.Empty;
        [JsonPropertyName("act")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("ex_msg")] public string? ExceptionMessage { get; set; }
        [JsonPropertyName("stack")] public string? StackTrace { get; set; }
        [JsonPropertyName("ctx")] public string? Context { get; set; }

        public bool IsValid() => Timestamp != default && !string.IsNullOrEmpty(Component) && !string.IsNullOrEmpty(Action);
    }
}

[JsonSerializable(typeof(ContainerTelemetry.TelemetryEntry))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
internal partial class TelemetryJsonContext : JsonSerializerContext { }

[JsonSerializable(typeof(ContainerTelemetry.TelemetryErrorEntry))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
internal partial class ErrorJsonContext : JsonSerializerContext { }
