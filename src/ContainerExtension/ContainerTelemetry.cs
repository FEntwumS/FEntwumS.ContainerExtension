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

    // 1 once the on-disk history has been purged for the current opt-out; reset when collection resumes,
    // so the purge fires once per opt-out transition rather than on every read.
    private static int _purgedForOptOut;

    private static bool IsOptedOut()
    {
        try { return TelemetryOptedOutChecker?.Invoke() == true; }
        catch { return false; }
    }

    /// <summary>
    /// Enforces opt-out on the read/export paths. Opting out must also erase prior history, not merely
    /// stop new writes, so the first observation of an opt-out best-effort purges the logs. Returns true
    /// when telemetry is opted out, in which case callers must surface nothing.
    /// </summary>
    private static bool PurgeIfOptedOut()
    {
        if (!IsOptedOut())
        {
            Volatile.Write(ref _purgedForOptOut, 0);
            return false;
        }
        if (Interlocked.CompareExchange(ref _purgedForOptOut, 1, 0) == 0)
        {
            try { ClearEntries(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* best-effort purge */ }
        }
        return true;
    }

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

    /// <summary>
    /// Gets or sets the delegate function used to dynamically resolve the current logging level configuration.
    /// Default resolves to "Verbose".
    /// </summary>
    public static Func<string> LogLevelChecker { get; set; } = () => "Verbose";

    /// <summary>
    /// Evaluates the rank of the current log level based on the configuration checked by <see cref="LogLevelChecker"/>.
    /// Ranks map as: "Off" => 0, "Errors Only" => 1, "Info" => 2, "Verbose" => 3.
    /// </summary>
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

    /// <summary>
    /// Gets a value indicating whether verbose telemetry logging is currently active (rank level 3).
    /// </summary>
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

    // Testing Hook
    /// <summary>Isolates telemetry to a temporary directory during xUnit test execution.</summary>
    internal static void InitializeTestEnvironment(string tempDir)
    {
        _telemetryDir = tempDir;
        _telemetryPath = Path.Combine(_telemetryDir, "container_telemetry.jsonl");
        _errorTelemetryPath = Path.Combine(_telemetryDir, "container_errors.jsonl");
        IsTestEnvironment = true;
        lock (MutexLock)
        {
            if (ProcessMutexLazy.IsValueCreated)
            {
                try { ProcessMutexLazy.Value?.Dispose(); } catch { /* Ignore */ }
            }
            ProcessMutexLazy = new Lazy<Mutex?>(CreateProcessMutex, LazyThreadSafetyMode.ExecutionAndPublication);
            try { RwLock.Dispose(); } catch { /* Ignore */ }
            RwLock = new ReaderWriterLockSlim();
        }
        Interlocked.Exchange(ref _isShutdown, 0);
        _cachedLineCount = -1;
        _cachedErrorLineCount = -1;
        Volatile.Write(ref _cachedStats, null);
        Volatile.Write(ref _telemetryDirVerified, false);

        try
        {
            ErrorChannel?.Writer.TryComplete();
        }
        catch
        {
            // Ignore channel completion failures
        }

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
    private static int _activeOperations = 0;
    private static bool _telemetryDirVerified;
    private static readonly System.Threading.Lock VerificationLock = new();

    private static ReaderWriterLockSlim RwLock = new();
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
            // Derive a stable, non-reversible per-user suffix so the Global\ kernel-object name does
            // not expose the OS username to other sessions on a shared machine, while still keeping
            // the lock unique per user (cross-session serialization of telemetry writes).
            var userKey = string.IsNullOrWhiteSpace(userName)
                ? "default"
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userName)))[..16];
            var prefix = OperatingSystem.IsWindows() ? "Global\\" : "";
            return new Mutex(false, $"{prefix}OneWareContainerTelemetryLock_{userKey}");
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _activeOperations) > 0 && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Yield();
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
                try { RwLock.Dispose(); } catch { /* Ignore */ }
                RwLock = new ReaderWriterLockSlim();
            }
        }
        catch (Exception)
        {
            // Mutex/Lock disposal exceptions can be ignored during shutdown
        }
    }

    public static void ResetShutdown()
    {
        Interlocked.Exchange(ref _isShutdown, 0);
        lock (MutexLock)
        {
            // Do not dispose the old lock: in-flight writers may still hold a reference to it.
            // ReaderWriterLockSlim owns no unmanaged handle, so the orphaned instance is reclaimed by GC.
            RwLock = new ReaderWriterLockSlim();
            if (ProcessMutexLazy.IsValueCreated)
            {
                try { ProcessMutexLazy.Value?.Dispose(); } catch { /* Ignore */ }
            }
            ProcessMutexLazy = new Lazy<Mutex?>(CreateProcessMutex, LazyThreadSafetyMode.ExecutionAndPublication);
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
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        var seenSymlinks = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        string ResolveCanonicalInternal(string currentPath, int depth)
        {
            if (depth > 40)
            {
                throw new ContainerExtension.DockerExecutionException("Too many levels of symbolic links.");
            }

            string absolutePath = currentPath;
            if (!Path.IsPathRooted(absolutePath))
            {
                absolutePath = Path.Combine(Directory.GetCurrentDirectory(), absolutePath);
            }

            string root = Path.GetPathRoot(absolutePath) ?? (OperatingSystem.IsWindows() ? @"C:\" : "/");
            if (string.IsNullOrEmpty(root))
            {
                root = OperatingSystem.IsWindows() ? @"C:\" : "/";
            }

            string remainder = absolutePath.Substring(root.Length);
            var separatorChars = new char[] { '/', '\\' };
            var components = remainder.Split(separatorChars, StringSplitOptions.RemoveEmptyEntries);

            string current = root;

            foreach (var component in components)
            {
                if (string.Equals(component, ".", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(component, "..", StringComparison.Ordinal))
                {
                    var parent = Path.GetDirectoryName(current);
                    current = parent ?? root;
                    continue;
                }

                string next = Path.Combine(current, component);

                bool isSymlink = false;
                string? target = null;

                try
                {
                    if (Directory.Exists(next))
                    {
                        var info = new DirectoryInfo(next);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            target = info.LinkTarget;
                            isSymlink = !string.IsNullOrEmpty(target);
                        }
                    }
                    else if (File.Exists(next))
                    {
                        var info = new FileInfo(next);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            target = info.LinkTarget;
                            isSymlink = !string.IsNullOrEmpty(target);
                        }
                    }
                    else
                    {
                        var info = new DirectoryInfo(next);
                        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            target = info.LinkTarget;
                            isSymlink = !string.IsNullOrEmpty(target);
                        }
                        else
                        {
                            var fInfo = new FileInfo(next);
                            if (fInfo.Exists && fInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                target = fInfo.LinkTarget;
                                isSymlink = !string.IsNullOrEmpty(target);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Ignore and treat as non-symlink
                }

                if (isSymlink && target != null)
                {
                    string canonicalSymlink = Path.GetFullPath(next);
                    if (!seenSymlinks.Add(canonicalSymlink))
                    {
                        throw new ContainerExtension.DockerExecutionException($"Circular symbolic link detected: '{next}'");
                    }

                    try
                    {
                        string resolvedTarget;
                        if (Path.IsPathRooted(target))
                        {
                            resolvedTarget = ResolveCanonicalInternal(target, depth + 1);
                        }
                        else
                        {
                            resolvedTarget = ResolveCanonicalInternal(Path.Combine(current, target), depth + 1);
                        }
                        current = resolvedTarget;
                    }
                    finally
                    {
                        seenSymlinks.Remove(canonicalSymlink);
                    }
                }
                else
                {
                    current = next;
                }
            }

            return Path.GetFullPath(current);
        }

        return ResolveCanonicalInternal(path, 0);
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

            var resolvedDir = GetCanonicalPath(dir);
            var resolvedFile = GetCanonicalPath(filepath);

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

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b|\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b|\b(?:[0-9a-fA-F]{1,4}:){1,7}:(?:[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{1,4})*)?\b|\b::(?:[0-9a-fA-F]{1,4}:){0,6}[0-9a-fA-F]{1,4}\b|\b[a-zA-Z0-9\-]+(?:\.local|\.lan)\b", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IpRedactRegex();

    // Redacts inline URI basic-auth credentials (scheme://user:pass@host) so tokens embedded in
    // registry or daemon URLs never reach the telemetry log.
    [GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex UriCredentialsRegex();

    // Provider-issued access tokens that a KEY=value scrub misses when they appear bare in a path,
    // URL, argument, or free-text field: GitHub PATs/OAuth/installation tokens and Slack tokens.
    [GeneratedRegex(@"\b(?:gh[a-z]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{10,})\b", RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProviderTokenRegex();

    // JSON Web Tokens (header.payload.signature, each base64url) — bearer credentials in plain sight.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JwtRegex();

    // PEM-encoded private key blocks. Lazy across newlines, so it cannot be NonBacktracking; bounded by a
    // match timeout instead.
    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----[\s\S]*?-----END (?:[A-Z]+ )?PRIVATE KEY-----", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PemPrivateKeyRegex();

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
                var scrubbed = path.Replace(home, "~");
                // The reconstructed docker-run command normalises Windows backslashes to forward
                // slashes, so also collapse the forward-slash form of the profile path; otherwise
                // C:/Users/<name> survives unredacted on Windows.
                if (home.Contains('\\', StringComparison.Ordinal))
                {
                    scrubbed = scrubbed.Replace(home.Replace('\\', '/'), "~");
                }
                return scrubbed;
            }
        }
        catch (Exception)
        {
            // Ignored to prevent telemetry failures from interrupting execution
        }
        return path;
    }

    internal static string? ScrubSensitiveInfo(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var scrubbed = ScrubHomePath(input);
        if (string.IsNullOrEmpty(scrubbed)) return scrubbed;
        try
        {
            scrubbed = UriCredentialsRegex().Replace(scrubbed, "${scheme}***:***@");
        }
        catch { /* Ignore */ }
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
            scrubbed = ProviderTokenRegex().Replace(scrubbed, "[REDACTED_TOKEN]");
            scrubbed = JwtRegex().Replace(scrubbed, "[REDACTED_JWT]");
            scrubbed = PemPrivateKeyRegex().Replace(scrubbed, "[REDACTED_PRIVATE_KEY]");
        }
        catch { /* Ignore */ }
        try
        {
            var user = CachedUserName;
            // Replace the account name only at identifier boundaries: a bare substring replace corrupts
            // unrelated text when the username is short or a common token (the prior behaviour). Guard a
            // 3-character floor and escape the name so it is matched literally, never as a pattern.
            if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3)
            {
                scrubbed = Regex.Replace(scrubbed, $@"(?<![A-Za-z0-9]){Regex.Escape(user)}(?![A-Za-z0-9])", "***",
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(1000));
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
    /// Builds append-mode stream options that, on POSIX, create the backing file with 0600
    /// in a single syscall. This closes the window between creation at the umask default and
    /// the subsequent SetUnixFileMode narrowing, and survives a failed write loop. On Windows
    /// UnixCreateMode is unsupported and left unset; confidentiality there is enforced via EFS.
    /// </summary>
    private static FileStreamOptions CreateAppendStreamOptions()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return options;
    }

    /// <summary>
    /// Writes a structured telemetry entry to the unified execution log.
    /// Handles PII redaction and thread-safe file appends automatically.
    /// </summary>
    // In-memory (never persisted) map from a scrubbed docker-run command to its exact, unmasked form, so the
    // dashboard can copy a verbatim, runnable command for THIS session's executions while the on-disk log
    // stays scrubbed. Bounded; the oldest entries are evicted.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _rawCommandByScrubbed = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _rawCommandOrder = new();
    private const int MaxRawCommands = 256;

    private static void RememberRawCommand(string scrubbed, string raw)
    {
        if (_rawCommandByScrubbed.TryAdd(scrubbed, raw))
        {
            _rawCommandOrder.Enqueue(scrubbed);
            while (_rawCommandOrder.Count > MaxRawCommands && _rawCommandOrder.TryDequeue(out var oldest))
            {
                _rawCommandByScrubbed.TryRemove(oldest, out _);
            }
        }
    }

    /// <summary>
    /// Returns the exact, unmasked docker run command for a scrubbed command logged THIS session, or null.
    /// The dashboard copy actions use it so the user gets a verbatim, runnable command; entries from a prior
    /// session (loaded from disk) have no in-memory raw form and fall back to the scrubbed text.
    /// </summary>
    public static string? TryGetRawCommand(string? scrubbed) =>
        scrubbed != null && _rawCommandByScrubbed.TryGetValue(scrubbed, out var raw) ? raw : null;

    public static void LogExecution(
      string image, string tool, double durationSeconds, long exitCode, string? imageDigest = null,
      bool wasCancelled = false, string? dockerRunCommand = null, long? peakMemoryBytes = null,
      double? maxCpuPercent = null, bool oomKilled = false, int maxEntries = 0, string? errorMessage = null,
      string? rawDockerRunCommand = null, bool isDebug = false)
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

        Interlocked.Increment(ref _activeOperations);
        try
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return;
            }
            EnsureDirectoryAndFileSecure(_telemetryDir, _telemetryPath);

            var entry = new TelemetryEntry
            {
                Timestamp = DateTime.UtcNow,
                Image = ScrubSensitiveInfo(image) ?? string.Empty,
                ImageDigest = imageDigest,
                Tool = tool != null ? Path.GetFileNameWithoutExtension(tool) : string.Empty,
                DurationSeconds = Math.Round(durationSeconds, 4),
                ExitCode = exitCode,
                WasCancelled = wasCancelled,
                DockerRunCommand = ScrubSensitiveInfo(ScrubSecrets(dockerRunCommand)),
                PeakMemoryBytes = peakMemoryBytes,
                MaxCpuPercent = maxCpuPercent.HasValue ? Math.Round(maxCpuPercent.Value, 1) : null,
                OomKilled = oomKilled,
                ErrorMessage = ScrubSensitiveInfo(ScrubSecrets(errorMessage))
            };

            // Keep the exact (unmasked) command in memory ONLY, keyed by its scrubbed form, so the dashboard
            // can copy a verbatim runnable command for this session. This is never written to disk.
            if (rawDockerRunCommand != null && entry.DockerRunCommand != null)
            {
                RememberRawCommand(entry.DockerRunCommand, rawDockerRunCommand);
            }

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

                var localLock = RwLock;
                localLock.EnterWriteLock();
                try
                {
                    EnsureFileLimit(_telemetryPath);
                    var streamOptions = CreateAppendStreamOptions();
                    bool written = false;
                    int delay = 15;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            using (var fs = new FileStream(_telemetryPath, streamOptions))
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
                        // Defensive no-op: the stream is already created with 0600 via UnixCreateMode.
                        try
                        {
                            File.SetUnixFileMode(_telemetryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        }
                        catch (Exception)
                        {
                            // Best-effort file permission enforcement
                        }
                    }
                    else if (OperatingSystem.IsWindows() && written)
                    {
                        // The EFS latch in EnsureDirectoryAndFileSecure only encrypts a pre-existing file;
                        // files first materialised here would otherwise never be encrypted.
                        try
                        {
                            File.Encrypt(_telemetryPath);
                        }
                        catch (Exception)
                        {
                            // Best-effort encryption on Windows
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
                    localLock.ExitWriteLock();
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
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
        }
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
                Action = ScrubSensitiveInfo(action) ?? string.Empty,
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
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return;
        }
        Interlocked.Increment(ref _activeOperations);
        try
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return;
            }
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

                var localLock = RwLock;
                localLock.EnterWriteLock();
                try
                {
                    var streamOptions = CreateAppendStreamOptions();
                    bool written = false;
                    int delay = 15;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            using (var fs = new FileStream(_errorTelemetryPath, streamOptions))
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
                        // Defensive no-op: the stream is already created with 0600 via UnixCreateMode.
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
                    else if (OperatingSystem.IsWindows() && written)
                    {
                        // The EFS latch in EnsureDirectoryAndFileSecure only encrypts a pre-existing file;
                        // files first materialised here would otherwise never be encrypted.
                        try
                        {
                            File.Encrypt(_errorTelemetryPath);
                        }
                        catch (Exception)
                        {
                            // Best-effort encryption on Windows
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
                    localLock.ExitWriteLock();
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
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
        }
    }

    public static List<TelemetryEntry> GetRecentEntries(int count = 20)
    {
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return [];
        }
        if (PurgeIfOptedOut())
        {
            return [];
        }
        Interlocked.Increment(ref _activeOperations);
        try
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return [];
            }
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
                    var localLock = RwLock;
                    localLock.EnterReadLock();
                    try
                    {
                        lastLines = ReadLastLinesSafe(_telemetryPath, count);
                    }
                    finally
                    {
                        localLock.ExitReadLock();
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
                            // A malformed tail line must not itself spawn a persisted error on every
                            // (2 s) dashboard read — that would grow container_errors.jsonl without bound.
                            System.Diagnostics.Debug.WriteLine($"[ContainerTelemetry] Skipping malformed telemetry line: {ex.Message}");
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
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
        }
    }

    public static (int totalRuns, double successRate, double avgDuration) GetStats()
    {
        var res = GetRecentEntriesWithStats(0);
        return (res.totalRuns, res.successRate, res.avgDuration);
    }

    public static bool ExportTo(string destinationPath)
    {
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return false;
        }
        // Nothing to export once opted out; the gate also purges any prior history.
        if (PurgeIfOptedOut())
        {
            return false;
        }
        // Containment: require a deliberate absolute destination. A relative path would resolve against
        // the process working directory and could write the execution history to an unintended location.
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathRooted(destinationPath))
        {
            return false;
        }
        Interlocked.Increment(ref _activeOperations);
        try
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return false;
            }
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

                    var localLock = RwLock;
                    localLock.EnterReadLock();
                    try
                    {
                        var destDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        using (var source = new FileStream(_telemetryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var dest = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
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

                        // The export carries the same execution history as the 0600 source; restrict
                        // it to the owner so it does not land world-readable (umask 0644) on a shared host.
                        if (!OperatingSystem.IsWindows())
                        {
                            try
                            {
                                File.SetUnixFileMode(destinationPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            }
                            catch (Exception)
                            {
                                // Best-effort owner-only restriction on the export.
                            }
                        }
                    }
                    finally
                    {
                        localLock.ExitReadLock();
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
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
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
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return;
        }
        Interlocked.Increment(ref _activeOperations);
        try
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return;
            }
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

                    var localLock = RwLock;
                    localLock.EnterWriteLock();
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
                        localLock.ExitWriteLock();
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
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
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
        if (Volatile.Read(ref _isShutdown) == 1)
        {
            return ([], 0, 0, 0);
        }
        if (PurgeIfOptedOut())
        {
            return ([], 0, 0, 0);
        }
        Interlocked.Increment(ref _activeOperations);
        try
        {
            if (Volatile.Read(ref _isShutdown) == 1)
            {
                return ([], 0, 0, 0);
            }
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

                    var localLock = RwLock;
                    localLock.EnterReadLock();
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
                                        int start = 0;
                                        int bytesRead;
                                        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                                        {
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
                        localLock.ExitReadLock();
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
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
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
        Interlocked.Increment(ref _activeOperations);
        try
        {
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
                var localLock = RwLock;
                localLock.EnterWriteLock();
                try
                {
                    TrimTelemetryFileInternal(path, maxEntries);
                }
                finally
                {
                    localLock.ExitWriteLock();
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
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
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
                    // The temp-write-then-atomic-replace path failed after all retries. Do NOT
                    // truncate the live file in place as a fallback: a write that fails after the
                    // truncation (for example disk-full) would discard the entire telemetry history.
                    // Leaving the file untrimmed is safe; the next successful trim reclaims the size.
                    Console.Error.WriteLine("[ContainerTelemetry] Telemetry trim deferred: atomic replace failed; file left intact.");
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
