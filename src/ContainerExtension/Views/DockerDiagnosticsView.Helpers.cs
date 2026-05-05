using static ContainerExtension.Views.UIBuilderHelpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace ContainerExtension.Views;

/// <summary>
/// Partial class containing shared UI helper methods, formatting utilities,
/// and cross-platform launch helpers.
/// </summary>
public partial class DockerDiagnosticsView
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Formatting Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Truncates a string to <paramref name="maxLen"/> characters, appending "..." if needed.</summary>
    private static string Truncate(string? s, int maxLen) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";

    /// <summary>
    /// Resolves a reliable export directory for file saves.
    /// Falls back to <c>~/.oneware/exports/</c> when the Desktop path is unavailable
    /// (e.g., Linux without a Desktop folder, or macOS sandbox restrictions).
    /// </summary>
    private static string ResolveExportDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
            return desktop;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".oneware", "exports");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>Formats a byte count as a human-readable size string (e.g. "1.2 GB").</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "unknown";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }

    /// <summary>Formats a <see cref="DateTime"/> as a relative time string (e.g. "3h ago", "5d ago").</summary>
    private static string FormatTimeAgo(DateTime created)
    {
        var diff = DateTime.UtcNow - created.ToUniversalTime();
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{diff.Minutes}m ago";
        if (diff.TotalDays < 1) return $"{diff.Hours}h ago";
        if (diff.TotalDays < 30) return $"{diff.Days}d ago";
        if (diff.TotalDays < 365) return $"{diff.Days / 30}mo ago";
        return $"{diff.Days / 365}y ago";
    }

    // ════════════════════════════════════════════════════════════════════
    //  Cross-platform launch helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens a file or URL with the system's default application.
    /// Uses <c>open</c> on macOS, <c>xdg-open</c> on Linux, and <c>UseShellExecute</c> on Windows.
    /// </summary>
    private static void OpenWithSystemDefault(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", $"\"{path}\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Process.Start("xdg-open", $"\"{path}\"");
        }
        catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.Helpers", "OpenWithSystemDefault", ex); }
    }

    /// <summary>
    /// Resolves the detected runtime name to a launchable desktop application name.
    /// Returns null for CLI-only runtimes (colima) or unknown runtimes.
    /// </summary>
    private static string? GetDesktopAppName(string runtime)
    {
        var rt = runtime.ToLowerInvariant();
        if (rt.Contains("orbstack")) return "OrbStack";
        if (rt.Contains("podman")) return "Podman Desktop";
        if (rt.Contains("docker")) return "Docker Desktop";
        return null; // colima, unknown, etc.
    }

    /// <summary>
    /// Launches the container runtime's desktop GUI application.
    /// Best-effort: silently fails if the app is not installed.
    /// </summary>
    private static void LaunchDesktopApp(string runtime)
    {
        var appName = GetDesktopAppName(runtime);
        if (appName == null) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"-a \"{appName}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Docker Desktop and Podman Desktop register URI schemes on Windows
                var scheme = appName.Contains("Docker") ? "docker-desktop:" : "podman-desktop:";
                Process.Start(new ProcessStartInfo(scheme) { UseShellExecute = true });
            }
            else // Linux
            {
                // Try common flatpak/snap/native executable names
                var cmd = appName.Contains("Docker") ? "docker-desktop"
                    : appName.Contains("Podman") ? "podman-desktop"
                    : appName.Contains("OrbStack") ? "orbstack" : null;
                if (cmd != null)
                    Process.Start(cmd);
            }
        }
        catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.Helpers", "LaunchDesktopApp", ex); }
    }
}
