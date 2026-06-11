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
    private static readonly string CachedUserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string CachedDesktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    private static readonly string[] MetricUnits = { "B", "KB", "MB", "GB", "TB" };

    // =======================================================================
    //  Formatting Helpers
    // =======================================================================

    /// <summary>Truncates a string to <paramref name="maxLen"/> characters, appending "..." if needed.</summary>
    private static string Truncate(string? s, int maxLen) =>
    string.IsNullOrEmpty(s) ? "" : s.Length <= maxLen ? s : string.Concat(s.AsSpan(0, maxLen - 3), "...");

    /// <summary>
    /// Resolves a reliable export directory for file saves.
    /// Falls back to <c>~/.oneware/exports/</c> when the Desktop path is unavailable
    /// (e.g., Linux without a Desktop folder, or macOS sandbox restrictions).
    /// </summary>
    private static string ResolveExportDirectory()
    {
        try
        {
            var desktop = CachedDesktop;
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
            {
                return desktop;
            }
        }
        catch (Exception)
        {
            // Fallback for sandboxed or headless systems
        }

        var fallback = Path.Combine(CachedUserProfile, ".oneware", "exports");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>Formats a byte count as a human-readable size string (e.g. "1.2 GB").</summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "unknown";
        double size = bytes;
        int i = 0;
        while (size >= 1024 && i < MetricUnits.Length - 1) { size /= 1024; i++; }
        return string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{size:F1} {MetricUnits[i]}");
    }

    /// <summary>Formats a <see cref="DateTime"/> as a relative time string (e.g. "3h ago", "5d ago").</summary>
    private static string FormatTimeAgo(DateTime created)
    {
        var diff = DateTime.UtcNow - created.ToUniversalTime();
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{diff.Minutes}m ago");
        if (diff.TotalDays < 1) return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{diff.Hours}h ago");
        if (diff.TotalDays < 30) return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{diff.Days}d ago");
        if (diff.TotalDays < 365) return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{diff.Days / 30}mo ago");
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{diff.Days / 365}y ago");
    }

    // ====================================================================
    //  Cross-platform launch helpers
    // ====================================================================

    /// <summary>
    /// Opens a file or URL with the system's default application.
    /// Uses <c>open</c> on macOS, <c>xdg-open</c> on Linux, and <c>UseShellExecute</c> on Windows.
    /// </summary>
    private static void OpenWithSystemDefault(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                var psi = new ProcessStartInfo("xdg-open");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
        }
        catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.Helpers", "OpenWithSystemDefault", ex); }
    }

    /// <summary>
    /// Resolves the detected runtime name to a launchable desktop application name.
    /// Returns null for CLI-only runtimes (colima) or unknown runtimes.
    /// </summary>
    private static string? GetDesktopAppName(string runtime)
    {
        if (string.IsNullOrEmpty(runtime)) return null;
        if (runtime.Contains("orbstack", StringComparison.OrdinalIgnoreCase)) return "OrbStack";
        if (runtime.Contains("podman", StringComparison.OrdinalIgnoreCase)) return "Podman Desktop";
        if (runtime.Contains("docker", StringComparison.OrdinalIgnoreCase)) return "Docker Desktop";
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
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", new[] { "-a", appName });
            }
            else if (OperatingSystem.IsWindows())
            {
                // Docker Desktop and Podman Desktop register URI schemes on Windows
                var scheme = appName.Contains("Docker", StringComparison.OrdinalIgnoreCase) ? "docker-desktop:" : "podman-desktop:";
                Process.Start(new ProcessStartInfo(scheme) { UseShellExecute = true });
            }
            else // Linux
            {
                // Try common flatpak/snap/native executable names
                var cmd = appName.Contains("Docker", StringComparison.OrdinalIgnoreCase) ? "docker-desktop"
          : appName.Contains("Podman", StringComparison.OrdinalIgnoreCase) ? "podman-desktop"
          : appName.Contains("OrbStack", StringComparison.OrdinalIgnoreCase) ? "orbstack" : null;
                if (cmd != null)
                    Process.Start(cmd);
            }
        }
        catch (Exception ex) { ContainerTelemetry.TrackError("DockerDiagnosticsView.Helpers", "LaunchDesktopApp", ex); }
    }
}
