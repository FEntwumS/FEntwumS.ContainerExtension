namespace ContainerExtension.Services.Docker;

/// <summary>
/// Validates and canonicalizes Docker bind mounts before they reach the daemon. Rewrites each host
/// path to its symlink-resolved canonical form and blocks mounts of (or under) critical host
/// directories, as well as mappings onto sensitive container paths. This is a security gate: a bind
/// that survives <see cref="ValidateBinds"/> has been proven not to touch a blocked location by either
/// its raw or its canonical form.
/// </summary>
internal static class BindValidator
{
    /// <summary>
    /// Canonicalizes every host path in <paramref name="binds"/> in place and throws
    /// <see cref="DockerExecutionException"/> if any bind targets a blocked host or container path.
    /// A null list is a no-op.
    /// </summary>
    internal static void ValidateBinds(IList<string>? binds)
    {
        if (binds == null) return;

        string[] blockedPaths;
        if (OperatingSystem.IsWindows())
        {
            blockedPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
                @"C:\Windows",
                @"\\.\pipe"
            };
        }
        else
        {
            blockedPaths = new[]
            {
                "/etc",
                "/var/run",
                "/var/run/docker.sock",
                "/var/run/containerd",
                "/proc",
                "/sys",
                "/dev",
                "/boot",
                "/bin",
                "/sbin",
                "/usr/bin",
                "/usr/sbin"
            };
        }

        // Enforce both the raw and the canonical form of each blocked path. If canonicalization of a
        // hardcoded blocked path fails, the raw form is still compared, so the gate cannot be weakened
        // by a symlinked or differently-cased equivalent slipping past an un-canonicalized entry.
        var blockedForms = new List<string>(blockedPaths.Length * 2);
        foreach (var blockedPath in blockedPaths)
        {
            blockedForms.Add(blockedPath);
            try
            {
                var canonical = PathCanonicalizer.GetCanonicalPath(blockedPath);
                if (!string.Equals(canonical, blockedPath, StringComparison.OrdinalIgnoreCase))
                {
                    blockedForms.Add(canonical);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The raw form is already enrolled above, so the gate stays effective.
            }
        }

        for (int i = 0; i < binds.Count; i++)
        {
            var bind = binds[i];
            if (string.IsNullOrWhiteSpace(bind)) continue;

            // Docker bind spec: HOST:CONTAINER[:OPTIONS]. Split drive-letter-aware so a Windows host
            // path such as "C:\proj" is not severed at its drive-letter colon (which would reduce the
            // host path to "C" and defeat both the rewrite and the critical-path security checks).
            var (hostPart, containerPart, optionsPart) = SplitDockerBind(bind);
            var hostPath = hostPart.Trim();
            if (!string.IsNullOrEmpty(hostPath))
            {
                string fullPath;
                try
                {
                    fullPath = PathCanonicalizer.GetCanonicalPath(hostPath);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    throw new DockerExecutionException($"Invalid mount path: '{hostPath}'. Details: {ex.Message}", ex);
                }

                var reconstructed = fullPath;
                if (containerPart != null)
                {
                    reconstructed += ":" + containerPart.Trim();
                }
                if (optionsPart != null)
                {
                    reconstructed += ":" + optionsPart.Trim();
                }
                binds[i] = reconstructed;

                foreach (var blocked in blockedForms)
                {
                    if (string.Equals(fullPath, blocked, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DockerExecutionException($"Mounting critical host path '{hostPath}' is blocked for security reasons.");
                    }

                    if (fullPath.StartsWith(blocked + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        fullPath.StartsWith(blocked + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DockerExecutionException($"Mounting paths under critical host directory '{blocked}' is blocked for security reasons.");
                    }
                }

                if (containerPart != null)
                {
                    var containerPath = containerPart.Trim();
                    if (containerPath.StartsWith("/sys", StringComparison.OrdinalIgnoreCase) ||
                        containerPath.StartsWith("/proc", StringComparison.OrdinalIgnoreCase) ||
                        containerPath.StartsWith("/dev", StringComparison.OrdinalIgnoreCase) ||
                        containerPath.StartsWith("/etc", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DockerExecutionException($"Mapping to container path '{containerPath}' is blocked for security reasons.");
                    }
                }
            }
        }
    }

    // Split a Docker bind spec "HOST:CONTAINER[:OPTIONS]" into its components. A leading Windows
    // drive-letter colon (e.g. "C:\path") is treated as part of the host path rather than the
    // host/container separator. The container path is always POSIX, so it carries no drive letter.
    internal static (string host, string? container, string? options) SplitDockerBind(string bind)
    {
        int hostStart = bind.Length >= 2 && char.IsLetter(bind[0]) && bind[1] == ':' ? 2 : 0;
        int firstSep = bind.IndexOf(':', hostStart);
        if (firstSep < 0)
        {
            return (bind, null, null);
        }

        var host = bind[..firstSep];
        var remainder = bind[(firstSep + 1)..];
        int secondSep = remainder.IndexOf(':');
        return secondSep < 0
            ? (host, remainder, null)
            : (host, remainder[..secondSep], remainder[(secondSep + 1)..]);
    }
}
