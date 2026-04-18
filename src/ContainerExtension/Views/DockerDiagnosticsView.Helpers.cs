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
    //  UI Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static Border CreateCard(string title, Control content, bool defaultExpanded = true)
    {
        // Restore previous state if available, otherwise use default
        if (!SectionExpandedState.TryGetValue(title, out var isExpanded))
            isExpanded = defaultExpanded;

        var expander = new Expander
        {
            Header = new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.Bold,
                Foreground = AccentColor,
                FontSize = 13
            },
            Content = content,
            IsExpanded = isExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // Persist state changes
        expander.GetObservable(Expander.IsExpandedProperty).Subscribe(expanded =>
        {
            SectionExpandedState[title] = expanded;
        });

        return new Border
        {
            Background = CardBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = expander
        };
    }

    /// <summary>Adds a monospaced TextBlock to a grid cell at the specified column.</summary>
    private static void AddGridCell(Grid grid, int col, string text, bool isHeader,
        SolidColorBrush foreground, HorizontalAlignment halign = HorizontalAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text, FontFamily = MonoFont, FontSize = isHeader ? 12 : 11,
            Foreground = foreground,
            FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = halign
        };
        Grid.SetColumn(block, col);
        grid.Children.Add(block);
    }

    /// <summary>
    /// Toggles a table's sort state: clicking the same column reverses direction,
    /// clicking a different column resets to ascending.
    /// </summary>
    private static void ToggleSort(ref (string column, bool ascending) sort, string clickedColumn)
    {
        sort = sort.column == clickedColumn
            ? (clickedColumn, !sort.ascending)
            : (clickedColumn, true);
    }

    /// <summary>Creates a styled action button with an async click handler and optional tooltip.</summary>
    /// <param name="text">Button label text.</param>
    /// <param name="action">Async action to execute on click.</param>
    /// <param name="tooltip">Optional hover description for user guidance.</param>
    private static Button CreateActionButton(string text, Func<Task> action, string? tooltip = null)
    {
        var btn = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6),
            Command = new AsyncRelayCommand(action)
        };
        if (!string.IsNullOrEmpty(tooltip))
            ToolTip.SetTip(btn, tooltip);
        return btn;
    }

    /// <summary>Creates a muted italic loading placeholder text.</summary>
    private static TextBlock CreateLoadingText(string text) => new()
    {
        Text = text, Foreground = MutedColor, FontSize = 11, FontStyle = FontStyle.Italic
    };

    /// <summary>Creates a "... and N more" overflow indicator text.</summary>
    private static TextBlock CreateMoreText(int remaining) => new()
    {
        Text = $"  ... and {remaining} more",
        Foreground = MutedColor, FontSize = 11, FontStyle = FontStyle.Italic
    };

    /// <summary>Creates a subtle 1px horizontal separator line for table headers.</summary>
    private static Border CreateSeparator() => new()
    {
        Height = 1, Background = MutedColor, Opacity = 0.3,
        Margin = new Thickness(0, 0, 0, 2)
    };

    /// <summary>Replaces section content with a styled offline warning message.</summary>
    private static void SetOfflineContent(Panel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = "Daemon offline — start Docker to enable this section.",
            Foreground = YellowColor, FontSize = 11, FontStyle = FontStyle.Italic
        });
    }

    /// <summary>Adds a label-value pair to a 3-column info grid at the specified row.</summary>
    private static void AddInfoRow(Grid grid, int row, string label, string value)
    {
        var labelBlock = new TextBlock
        {
            Text = label, Foreground = MutedColor, FontFamily = MonoFont, FontSize = 11,
            Margin = new Thickness(18, 2, 0, 0)
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = value, Foreground = FontColor, FontFamily = MonoFont, FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 2);
        grid.Children.Add(valueBlock);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Formatting Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Resets a button's content text after a delay (e.g. "Copied!" → "Copy").</summary>
    private static async Task ResetButtonTextAsync(Button btn, string originalText, int delayMs)
    {
        await Task.Delay(delayMs);
        Dispatcher.UIThread.Post(() => btn.Content = originalText);
    }

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
