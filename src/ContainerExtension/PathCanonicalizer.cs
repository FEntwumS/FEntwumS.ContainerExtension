using System;
using System.Collections.Generic;
using System.IO;

namespace ContainerExtension;

/// <summary>
/// Resolves a path to its fully canonical, symlink-followed absolute form for the security containment
/// checks in <see cref="ContainerTelemetry"/> and <see cref="DockerExecutionStrategy"/>, and for the
/// DockerCommandBuilder host-to-container mount-mapping gate. Each previously carried a byte-identical
/// private copy of this routine; a single authoritative definition keeps the telemetry-directory,
/// mount-blocking, and mount-mapping gates from drifting apart.
/// </summary>
internal static class PathCanonicalizer
{
    /// <summary>
    /// Returns the canonical absolute path with every symbolic-link component resolved, "." and ".."
    /// collapsed, and separators normalized. Throws <see cref="DockerExecutionException"/> on a circular
    /// or excessively deep symlink chain, and <see cref="ArgumentException"/> on a null/empty path.
    /// </summary>
    internal static string GetCanonicalPath(string path)
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
}
