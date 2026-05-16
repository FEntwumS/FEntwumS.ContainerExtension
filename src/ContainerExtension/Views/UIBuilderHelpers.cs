using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace ContainerExtension.Views;

/// <summary>
/// Static helper class containing generic UI builder methods, color palettes,
/// formatting utilities, and shared layout components.
/// </summary>
public static class UIBuilderHelpers
{
    // ── Color Palette ───────────────────────────────────────────────────
    public static readonly SolidColorBrush FontColor = new(Color.Parse("#E0E0E0"));
    public static readonly SolidColorBrush MutedColor = new(Color.Parse("#888888"));
    public static readonly SolidColorBrush AccentColor = new(Color.Parse(ContainerExtensionModule.DockerBlueHex)); // Docker blue
    public static readonly SolidColorBrush GreenColor = new(Color.Parse("#4CAF50"));
    public static readonly SolidColorBrush RedColor = new(Color.Parse("#FF6B6B"));
    public static readonly SolidColorBrush YellowColor = new(Color.Parse("#FFD54F"));
    public static readonly SolidColorBrush CardBg = new(Color.Parse("#1A2496ED"));
    public static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Menlo, monospace");

    // Session-persistent collapsed/expanded state (survives panel close/reopen within session)
    private static readonly Dictionary<string, bool> SectionExpandedState = new(StringComparer.Ordinal);

    // ═══════════════════════════════════════════════════════════════════════
    //  UI Helpers
    // ═══════════════════════════════════════════════════════════════════════

    public static Border CreateCard(string title, Control content, bool defaultExpanded = true)
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

    /// <summary>
    /// Toggles a table's sort state: clicking the same column reverses direction,
    /// clicking a different column resets to ascending.
    /// </summary>
    public static void ToggleSort(ref (string column, bool ascending) sort, string clickedColumn)
    {
        sort = string.Equals(sort.column, clickedColumn
, StringComparison.Ordinal) ? (clickedColumn, !sort.ascending)
            : (clickedColumn, true);
    }

    /// <summary>Adds a monospaced TextBlock to a grid cell at the specified column.</summary>
    public static void AddGridCell(Grid grid, int col, string text, bool isHeader,
        SolidColorBrush foreground, HorizontalAlignment halign = HorizontalAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = isHeader ? 12 : 11,
            Foreground = foreground,
            FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = halign
        };
        Grid.SetColumn(block, col);
        grid.Children.Add(block);
    }

    /// <summary>Creates a styled action button with an async click handler and optional tooltip.</summary>
    /// <param name="text">Button label text.</param>
    /// <param name="action">Async action to execute on click.</param>
    /// <param name="tooltip">Optional hover description for user guidance.</param>
    public static Button CreateActionButton(string text, Func<Task> action, string? tooltip = null)
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
    public static TextBlock CreateLoadingText(string text) => new()
    {
        Text = text,
        Foreground = MutedColor,
        FontSize = 11,
        FontStyle = FontStyle.Italic
    };

    /// <summary>Creates a "... and N more" overflow indicator text.</summary>
    public static TextBlock CreateMoreText(int remaining) => new()
    {
        Text = $"  ... and {remaining} more",
        Foreground = MutedColor,
        FontSize = 11,
        FontStyle = FontStyle.Italic
    };

    /// <summary>Creates a subtle 1px horizontal separator line for table headers.</summary>
    public static Border CreateSeparator() => new()
    {
        Height = 1,
        Background = MutedColor,
        Opacity = 0.3,
        Margin = new Thickness(0, 0, 0, 2)
    };

    /// <summary>Replaces section content with a styled offline warning message.</summary>
    public static void SetOfflineContent(Panel panel)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = "Daemon offline — start Docker to enable this section.",
            Foreground = YellowColor,
            FontSize = 11,
            FontStyle = FontStyle.Italic
        });
    }

    /// <summary>Adds a label-value pair to a 3-column info grid at the specified row.</summary>
    public static void AddInfoRow(Grid grid, int row, string label, string value)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = MutedColor,
            FontFamily = MonoFont,
            FontSize = 11,
            Margin = new Thickness(18, 2, 0, 0)
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = FontColor,
            FontFamily = MonoFont,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 2);
        grid.Children.Add(valueBlock);
    }

    /// <summary>Resets a button's content text after a delay (e.g. "Copied!" → "Copy").</summary>
    public static async Task ResetButtonTextAsync(Button btn, string originalText, int delayMs)
    {
        await Task.Delay(delayMs).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() => btn.Content = originalText);
    }

    /// <summary>Creates a unicode sparkline string from a sequence of values with absolute zero allocations.</summary>
    public static string CreateSparkline(IReadOnlyList<double> vals)
    {
        if (vals.Count == 0) return string.Empty;
        
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (var v in vals)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return string.Create(vals.Count, (vals, min, max), static (span, state) =>
        {
            // FIX: Literal string cast directly to ReadOnlySpan eliminates array allocations at JIT compilation boundaries.
            ReadOnlySpan<char> blocks = " ▂▃▄▅▆▇█"; 
            var (values, minVal, maxVal) = state;
            double range = maxVal - minVal;
            
            for (int i = 0; i < span.Length; i++)
            {
                int idx = range == 0 ? 0 : (int)Math.Round((values[i] - minVal) / range * (blocks.Length - 1));
                idx = Math.Max(0, Math.Min(blocks.Length - 1, idx));
                span[i] = blocks[idx];
            }
        });
    }
}