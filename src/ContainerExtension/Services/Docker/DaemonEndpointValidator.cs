using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ContainerExtension.Services.Docker;

/// <summary>
/// Verifies that a Docker daemon endpoint is safe to connect to and probes the host for a usable
/// runtime socket. On Windows it classifies the process behind a named pipe and rejects untrusted pipe
/// squatters; on Unix it probes candidate sockets, checks their ownership, and resolves the current
/// uid/gid used for container <c>--user</c> mapping. Extracted from <see cref="DockerExecutionStrategy"/>
/// so these security-critical endpoint checks can be reasoned about and tested in isolation.
/// </summary>
internal static partial class DaemonEndpointValidator
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle Pipe, out uint ServerProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle ProcessHandle, uint DesiredAccess, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle TokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "getegid")]
    private static partial uint getegid();

    private enum PipeServerTrust
    {
        Untrusted,
        CurrentUser,
        Elevated,
    }

    // Classify the process on the far end of a named pipe. Elevated (SYSTEM / Administrators) is the
    // Docker service itself. CurrentUser covers rootless / user-mode runtimes (podman, colima, ssh
    // proxies) that legitimately run as the caller, but which a same-user process could also impersonate,
    // so the caller gates CurrentUser on a known runtime name rather than trusting it outright. On a query
    // failure fail open (Elevated) to match the prior behaviour and avoid breaking connections whose
    // identity cannot be read; on an explicit denial fail closed (Untrusted).
    private static PipeServerTrust GetPipeServerTrust(uint pid)
    {
        if (!OperatingSystem.IsWindows()) return PipeServerTrust.Elevated;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_QUERY = 0x0008;

        try
        {
            using var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == null || hProcess.IsInvalid)
            {
                return PipeServerTrust.Untrusted; // Fail closed
            }

            if (OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
            {
                using (hToken)
                {
#pragma warning disable S3869
                    using var identity = new System.Security.Principal.WindowsIdentity(hToken.DangerousGetHandle());
#pragma warning restore S3869
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    bool isAdmin = false;
                    try
                    {
                        isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
                    {
                        isAdmin = false;
                    }
                    if (isAdmin || identity.IsSystem)
                    {
                        return PipeServerTrust.Elevated;
                    }
                    using var currentIdentity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    if (identity.User != null && currentIdentity.User != null && identity.User.Equals(currentIdentity.User))
                    {
                        return PipeServerTrust.CurrentUser;
                    }
                    return PipeServerTrust.Untrusted;
                }
            }
        }
        catch (PlatformNotSupportedException)
        {
            return PipeServerTrust.Elevated; // Fail-open where identity queries are not supported
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetPipeServerTrust failed for pid {pid}", ex);
        }
        return PipeServerTrust.Untrusted;
    }

    internal static async Task<bool> VerifyWindowsNamedPipeAsync(string pipeName, bool bypassCheck, int timeoutMs = 200, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        if (bypassCheck)
        {
            return true;
        }
        var connectTime = DateTime.Now;
        try
        {
            using var pipeStream = new System.IO.Pipes.NamedPipeClientStream(
                ".",
                pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.None,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipeStream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            var safeHandle = pipeStream.SafePipeHandle;
            if (safeHandle != null && !safeHandle.IsInvalid)
            {
                if (GetNamedPipeServerProcessId(safeHandle, out var pid))
                {
                    System.Diagnostics.Process? process = null;
                    try
                    {
                        process = System.Diagnostics.Process.GetProcessById((int)pid);
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                    catch (PlatformNotSupportedException)
                    {
                        return true; // Fail-open on platforms that do not support process by ID lookups
                    }

                    if (process != null)
                    {
                        using (process)
                        {
                            if (!process.HasExited)
                            {
                                try
                                {
                                    var startTime = process.StartTime;
                                    if (startTime > connectTime.AddMilliseconds(500))
                                    {
                                        return false; // PID reuse detected: process started after pipe connection
                                    }

                                    var name = process.ProcessName;
                                    var isNameWhitelisted = name.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("podman", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("wsl", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("vmmember", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("win-sshproxy", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("System", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("svchost", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("rancher", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("lima", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("com.docker", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("orbstack", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("socat", StringComparison.OrdinalIgnoreCase) ||
                                                            name.Contains("ssh", StringComparison.OrdinalIgnoreCase);

                                    var trust = GetPipeServerTrust(pid);
                                    if (trust == PipeServerTrust.Elevated)
                                    {
                                        return true;
                                    }
                                    if (trust == PipeServerTrust.CurrentUser)
                                    {
                                        if (isNameWhitelisted)
                                        {
                                            return true;
                                        }
                                        // The whitelist is a gate here, not merely advisory: a current-user
                                        // process whose name matches no known runtime could be a pipe squatter.
                                        ContainerTelemetry.TrackError("DockerExecutionStrategy",
                                            $"Named pipe host process '{name}' (PID: {pid}) runs as the current user but matches no known runtime; refusing the connection.", null);
                                    }
                                    else
                                    {
                                        ContainerTelemetry.TrackError("DockerExecutionStrategy",
                                            $"Named pipe verification failed for pipe '{pipeName}'. Host process: '{name}' (PID: {pid}) is NOT trusted.", null);
                                    }
                                }
                                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5 || ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Name/start-time were unreadable (access denied); fall back to the token
                                    // classification alone, staying lenient (elevated or current-user) as before.
                                    return GetPipeServerTrust(pid) != PipeServerTrust.Untrusted;
                                }
                                catch (PlatformNotSupportedException)
                                {
                                    return true;
                                }
                                catch (InvalidOperationException)
                                {
                                    return false;
                                }
                            }
                        }
                    }
                }
                return false;
            }
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (IOException ex) when (ex.InnerException is FileNotFoundException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static readonly SemaphoreSlim UnixIdSemaphore = new(1, 1);
    private static readonly ConcurrentDictionary<string, string?> OwnerCache = new(StringComparer.Ordinal);
    private static volatile string? _cachedUid;
    private static volatile string? _cachedGid;

    /// <summary>The resolved current-user uid used for container <c>--user</c> mapping, or null if not yet probed.</summary>
    internal static string? CachedUid => _cachedUid;

    /// <summary>The resolved current-user gid used for container <c>--user</c> mapping, or null if not yet probed.</summary>
    internal static string? CachedGid => _cachedGid;

    internal static async Task EnsureUnixIdsLoadedAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return;
        if (_cachedUid != null && _cachedGid != null) return;

        await UnixIdSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cachedUid ??= await GetUnixIdInternalAsync("-u", "1000", ct).ConfigureAwait(false);
            _cachedGid ??= await GetUnixIdInternalAsync("-g", "1000", ct).ConfigureAwait(false);
        }
        finally
        {
            UnixIdSemaphore.Release();
        }
    }

    internal static async Task<(bool live, string? errorMessage)> IsUnixSocketLiveAndWritableAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return (false, null);
        }
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            var ep = new System.Net.Sockets.UnixDomainSocketEndPoint(path);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1000);
            try
            {
                await socket.ConnectAsync(ep, timeoutCts.Token).ConfigureAwait(false);
                return (true, null);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return (false, $"Timeout connecting to UNIX socket '{path}'.");
            }
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            var nativeCode = ex.NativeErrorCode;
            var socketCode = ex.SocketErrorCode;
            string? errorMessage;
            if (socketCode == System.Net.Sockets.SocketError.AccessDenied ||
                nativeCode == 13 ||
                nativeCode == 1 ||
                nativeCode == 10013)
            {
                errorMessage = $"Access Denied: Current user does not have permission to access socket '{path}'. Ensure correct group membership (e.g. 'docker').";
            }
            else if (socketCode == System.Net.Sockets.SocketError.ConnectionRefused ||
                     nativeCode == 111 ||
                     nativeCode == 61)
            {
                errorMessage = $"Connection Refused: Docker daemon socket at '{path}' is not running or active.";
            }
            else
            {
                errorMessage = $"Socket Error ({socketCode}, Native: {nativeCode}): {ex.Message}";
            }
            return (false, errorMessage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, $"Unknown connection failure for socket '{path}': {ex.Message}");
        }
    }

#pragma warning disable S3011
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2075")]
    internal sealed class SecureNamedPipeCredentials : global::Docker.DotNet.Credentials
    {
        private readonly Uri _endpoint;

        public SecureNamedPipeCredentials(Uri endpoint)
        {
            _endpoint = endpoint;
        }

        public override bool IsTlsCredentials() => false;

        public override System.Net.Http.HttpMessageHandler GetHandler(System.Net.Http.HttpMessageHandler innerHandler)
        {
            if (string.Equals(innerHandler.GetType().FullName, "Microsoft.Net.Http.Client.ManagedHandler", StringComparison.Ordinal))
            {
                var field = innerHandler.GetType().GetField("_streamOpener", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    var delegateType = field.FieldType;
                    var method = typeof(SecureNamedPipeCredentials).GetMethod(nameof(SecureStreamOpenerAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (method != null)
                    {
                        var d = System.Delegate.CreateDelegate(delegateType, this, method);
                        field.SetValue(innerHandler, d);
                    }
                    else
                    {
                        // Do not fail open silently: if our own opener cannot be bound, the pipe would be
                        // dialled without the impersonation cap. Surface it rather than downgrade unseen.
                        ContainerTelemetry.TrackError("DockerExecutionStrategy",
                            "SecureNamedPipeCredentials: SecureStreamOpenerAsync not found; named-pipe impersonation cap NOT installed.", null);
                    }
                }
                else
                {
                    ContainerTelemetry.TrackError("DockerExecutionStrategy",
                        "SecureNamedPipeCredentials: ManagedHandler._streamOpener field not found (Docker.DotNet drift); named-pipe impersonation cap NOT installed.", null);
                }
            }
            return innerHandler;
        }

#pragma warning disable S1172
        private async System.Threading.Tasks.Task<System.IO.Stream> SecureStreamOpenerAsync(string host, int port, System.Threading.CancellationToken token)
        {
            var pipeName = _endpoint.LocalPath;
            var serverName = ".";
            if (pipeName.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = pipeName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    serverName = parts[0];
                    pipeName = parts[parts.Length - 1];
                }
            }
            else
            {
                if (pipeName.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase))
                {
                    pipeName = pipeName[5..];
                }
                else if (pipeName.StartsWith("/pipe/", StringComparison.OrdinalIgnoreCase))
                {
                    pipeName = pipeName[6..];
                }
            }

            var pipe = new System.IO.Pipes.NamedPipeClientStream(
                serverName,
                pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);

            try
            {
                await pipe.ConnectAsync(token).ConfigureAwait(false);
                // Verify the server on the SAME handle that carries traffic, not just the throwaway probe
                // in VerifyWindowsNamedPipeAsync — otherwise a squatter that lost the probe race could still
                // win the data connection. Fail open on any ambiguity (unreadable handle/pid) to match the
                // probe's posture; reject only a definitively untrusted server.
                if (OperatingSystem.IsWindows()
                    && pipe.SafePipeHandle is { IsInvalid: false } dataHandle
                    && GetNamedPipeServerProcessId(dataHandle, out var serverPid)
                    && GetPipeServerTrust(serverPid) == PipeServerTrust.Untrusted)
                {
                    // The catch below disposes the pipe on the way out.
                    throw new IOException($"Refusing to use Docker named pipe: data-stream server process (PID {serverPid}) is not trusted.");
                }
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
#pragma warning restore S1172
    }
#pragma warning restore S3011

    internal static async Task<(Uri uri, string runtime)> ProbeUnixSocketAsync(CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await EnsureUnixIdsLoadedAsync(ct).ConfigureAwait(false);
        var uid = _cachedUid ?? "1000";

        var candidates = new (string path, string name)[]
        {
            ("/var/run/docker.sock",                          "docker"),
            (Path.Combine(home, ".docker/run/docker.sock"),              "docker (user)"),
            ($"/run/user/{uid}/podman/podman.sock",                  "podman"),
            (Path.Combine(home, ".colima/default/docker.sock"),            "colima"),
            (Path.Combine(home, ".local/share/containers/podman/machine/podman.sock"), "podman (machine)"),
            (Path.Combine(home, ".orbstack/run/docker.sock"),             "orbstack"),
        };

        foreach (var (path, name) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var owner = await GetUnixFileOwnerAsync(path, ct).ConfigureAwait(false);
                if (owner != null && !string.Equals(owner, uid, StringComparison.Ordinal) && !string.Equals(owner, "0", StringComparison.Ordinal))
                {
                    Console.WriteLine($"[WARN] Insecure socket owner '{owner}' for socket '{path}'. Expected owner {uid} or 0.");
                    continue;
                }
            }
            ct.ThrowIfCancellationRequested();
            var (live, error) = await IsUnixSocketLiveAndWritableAsync(path, ct).ConfigureAwait(false);
            if (live)
            {
                return (new Uri($"unix://{path}"), RefineRuntimeLabel(path, name));
            }
            else if (error != null && error.StartsWith("Access Denied", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[WARN] {error}");
            }
        }

        // If no candidate is active/live, see if any candidate file exists on disk
        // Checked in reverse order to prefer specific runtimes (orbstack, colima, podman) over generic defaults.
        for (int i = candidates.Length - 1; i >= 0; i--)
        {
            var (path, name) = candidates[i];
            if (File.Exists(path))
            {
                // Re-apply the live-probe ownership gate here too: a socket the probe loop skipped as
                // insecurely owned must not be silently re-selected by the file-exists fallback. Null-tolerant
                // (stat unavailable / unresolved owner is accepted) to match the probe loop and avoid
                // regressing hosts where ownership cannot be determined.
                var owner = await GetUnixFileOwnerAsync(path, ct).ConfigureAwait(false);
                if (owner != null && !string.Equals(owner, uid, StringComparison.Ordinal) && !string.Equals(owner, "0", StringComparison.Ordinal))
                {
                    continue;
                }
                return (new Uri($"unix://{path}"), RefineRuntimeLabel(path, name));
            }
        }

        // If files are deleted when offline, check if the parent directories exist (specific to user home)
        for (int i = candidates.Length - 1; i >= 0; i--)
        {
            var (path, name) = candidates[i];
            if (!string.IsNullOrEmpty(home) && path.Contains(home, StringComparison.Ordinal))
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    return (new Uri($"unix://{path}"), name);
                }
            }
        }

        return (new Uri("unix:///var/run/docker.sock"), RefineRuntimeLabel("/var/run/docker.sock", "docker (default)"));
    }

    // The probe candidates carry a static label, but /var/run/docker.sock is commonly a symlink into a
    // specific runtime's directory (OrbStack, Colima, Podman). Resolve the link chain so DetectedRuntime —
    // and thus the dashboard's "Open Desktop" button, title, and offline guidance — names the real runtime
    // instead of the generic "docker" the path was merely reached through.
    private static string RefineRuntimeLabel(string socketPath, string defaultName)
    {
        try
        {
            var resolved = socketPath;
            for (int hop = 0; hop < 16; hop++)
            {
                var target = new FileInfo(resolved).LinkTarget;
                if (string.IsNullOrEmpty(target)) break;
                resolved = Path.IsPathRooted(target)
                    ? target
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resolved) ?? "/", target));
            }
            var r = resolved.Replace('\\', '/');
            if (r.Contains("/.orbstack/", StringComparison.OrdinalIgnoreCase)) return "orbstack";
            if (r.Contains("/.colima/", StringComparison.OrdinalIgnoreCase)) return "colima";
            if (r.Contains("podman", StringComparison.OrdinalIgnoreCase)) return "podman";
        }
        catch
        {
            // Resolution failed (missing file, permission, symlink loop) — fall back to the static label.
        }
        return defaultName;
    }

    // Resolve a Unix system utility to a trusted absolute path instead of a bare name (Sonar S4036).
    // A bare "stat"/"id"/"open" is resolved against $PATH, so a writable directory earlier on PATH could
    // shadow the real binary. Prefer /usr/bin then /bin (usr-merged on modern Linux; both fixed on macOS).
    // On a non-FHS layout where neither exists (e.g. NixOS) return the canonical absolute path anyway, so
    // the launch fails cleanly and degrades to the caller's fallback rather than resolving through PATH.
    internal static string ResolveTrustedUnixBinary(string name)
    {
        string[] candidates = [$"/usr/bin/{name}", $"/bin/{name}"];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return candidates[0];
    }

    private static async Task<string?> GetUnixFileOwnerAsync(string path, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        if (OwnerCache.TryGetValue(path, out var cachedOwner))
        {
            return cachedOwner;
        }
        try
        {
            var isMac = OperatingSystem.IsMacOS();
            using (var p = new Process())
            {
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveTrustedUnixBinary("stat"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (isMac)
                {
                    p.StartInfo.ArgumentList.Add("-f");
                    p.StartInfo.ArgumentList.Add("%u");
                }
                else
                {
                    p.StartInfo.ArgumentList.Add("-c");
                    p.StartInfo.ArgumentList.Add("%u");
                }
                p.StartInfo.ArgumentList.Add(path);

                ct.ThrowIfCancellationRequested();
                p.Start();
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                var output = (await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                _ = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                if (p.ExitCode != 0)
                {
                    OwnerCache[path] = null;
                    return null;
                }
                if (string.IsNullOrWhiteSpace(output))
                {
                    OwnerCache[path] = null;
                    return null;
                }
                OwnerCache[path] = output;
                return output;
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed with Win32Exception for '{path}'", ex);
            return null;
        }
        catch (IOException ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed with IOException for '{path}'", ex);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed with UnauthorizedAccessException for '{path}'", ex);
            return null;
        }
        catch (Exception ex)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"GetUnixFileOwner failed for '{path}'", ex);
            return null;
        }
    }

    private static async Task<string> GetUnixIdInternalAsync(string arg, string fallback, CancellationToken ct)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                if (string.Equals(arg, "-u", StringComparison.Ordinal))
                {
                    return geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                if (string.Equals(arg, "-g", StringComparison.Ordinal))
                {
                    return getegid().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Fall back
            }
        }

        Process? p = null;
        try
        {
            p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveTrustedUnixBinary("id"),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.StartInfo.ArgumentList.Add(arg);
            p.Start();

            var readOutTask = p.StandardOutput.ReadToEndAsync(ct);
            var readErrTask = p.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(1000);
            try
            {
                await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                var id = (await readOutTask.ConfigureAwait(false)).Trim();
                _ = await readErrTask.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(id) && int.TryParse(id, out _))
                {
                    return id;
                }
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill();
                    await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
                try
                {
                    await Task.WhenAny(Task.WhenAll(readOutTask, readErrTask), Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore
                }
                ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe for '{arg}' timed out", null);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 'id' binary could not be launched on this platform; the caller falls back to the
            // default 1000:1000 mapping.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ContainerTelemetry.TrackError("DockerExecutionStrategy", $"UID/GID probe failed for '{arg}'", ex);
        }
        finally
        {
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignore
                }
                try
                {
                    p.Dispose();
                }
                catch
                {
                    // Ignore
                }
            }
        }
        return fallback;
    }
}
