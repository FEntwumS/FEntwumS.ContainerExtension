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
    // -- Color Palette ---------------------------------------------------
    private static T GetResource<T>(string key, T defaultValue)
    {
        if (Application.Current != null && Application.Current.TryGetResource(key, out var res) && res is T val)
        {
            return val;
        }
        return defaultValue;
    }

    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultFontColor = new((uint)Color.Parse("#E0E0E0").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultMutedColor = new((uint)Color.Parse("#B0B0B0").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultAccentColor = new((uint)Color.Parse(ContainerExtensionModule.DockerBlueHex).ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultGreenColor = new((uint)Color.Parse("#4CAF50").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultRedColor = new((uint)Color.Parse("#FF6B6B").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultYellowColor = new((uint)Color.Parse("#FFD54F").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultCardBg = new((uint)Color.Parse("#1A2496ED").ToUInt32());

    private static IBrush? _cachedFontColor;
    private static IBrush? _cachedMutedColor;
    private static IBrush? _cachedAccentColor;
    private static IBrush? _cachedGreenColor;
    private static IBrush? _cachedRedColor;
    private static IBrush? _cachedYellowColor;
    private static IBrush? _cachedCardBg;

    public static void InitializeBrushes()
    {
        _cachedFontColor = GetResource<IBrush>("SystemControlForegroundBaseHighBrush", DefaultFontColor);
        _cachedMutedColor = GetResource<IBrush>("ThemeForegroundBrushLow", GetResource<IBrush>("SystemControlForegroundBaseMediumLowBrush", DefaultMutedColor));
        _cachedAccentColor = GetResource<IBrush>("SystemControlHighlightAccentBrush", DefaultAccentColor);
        _cachedGreenColor = GetResource<IBrush>("SystemControlSuccessAccentBrush", DefaultGreenColor);
        _cachedRedColor = GetResource<IBrush>("SystemControlErrorTextBrush", DefaultRedColor);
        _cachedYellowColor = GetResource<IBrush>("SystemControlWarningAccentBrush", DefaultYellowColor);
        _cachedCardBg = GetResource<IBrush>("SystemControlBackgroundListLowBrush", DefaultCardBg);
    }

    public static IBrush FontColor => _cachedFontColor ?? DefaultFontColor;
    public static IBrush MutedColor => _cachedMutedColor ?? DefaultMutedColor;
    public static IBrush AccentColor => _cachedAccentColor ?? DefaultAccentColor;
    public static IBrush GreenColor => _cachedGreenColor ?? DefaultGreenColor;
    public static IBrush RedColor => _cachedRedColor ?? DefaultRedColor;
    public static IBrush YellowColor => _cachedYellowColor ?? DefaultYellowColor;
    public static IBrush CardBg => _cachedCardBg ?? DefaultCardBg;
    public static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Menlo, monospace");

    // Global Constants for Standardized Fonts and Layout Configurations
    public const double HeaderFontSize = 13;
    public const double RowHeaderFontSize = 12;
    public const double RowFontSize = 11;
    public const double SmallFontSize = 10;

    public static readonly Thickness CardPadding = new(12, 8);
    public static readonly CornerRadius CardCornerRadius = new(6);
    public static readonly Thickness RowMargin = new(0, 1);

    // Session-persistent collapsed/expanded state (survives panel close/reopen within session)
    private static readonly Dictionary<string, bool> SectionExpandedState = new(StringComparer.Ordinal);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, System.Runtime.CompilerServices.StrongBox<int>> CopyButtonCounters = new();

    // =======================================================================
    //  UI Helpers
    // =======================================================================

    public static Border CreateCard(string title, Control content, bool defaultExpanded = true)
    {
        // Restore previous state if available, otherwise use default
        if (!SectionExpandedState.TryGetValue(title, out var isExpanded))
        {
            isExpanded = defaultExpanded;
        }

        var expander = new Expander
        {
            Header = new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.Bold,
                Foreground = AccentColor,
                FontSize = HeaderFontSize
            },
            Content = content,
            IsExpanded = isExpanded,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        expander.Expanded += (s, e) => SectionExpandedState[title] = true;
        expander.Collapsed += (s, e) => SectionExpandedState[title] = false;

        return new Border
        {
            Background = CardBg,
            CornerRadius = CardCornerRadius,
            Padding = CardPadding,
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

    public static void SetTipSafe(Control control, object? value)
    {
        if (value is string tooltipText && !string.IsNullOrEmpty(tooltipText))
        {
            var tb = new TextBlock
            {
                Text = tooltipText,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300
            };
            ToolTip.SetTip(control, tb);
        }
        else
        {
            ToolTip.SetTip(control, value);
        }
    }

    /// <summary>Adds a monospaced TextBlock to a grid cell at the specified column.</summary>
    public static void AddGridCell(Grid grid, int col, string text, bool isHeader,
    IBrush foreground, HorizontalAlignment halign = HorizontalAlignment.Left)
    {
        TextBlock? block = null;
        var children = grid.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child is TextBlock tb && Grid.GetColumn(tb) == col)
            {
                block = tb;
                break;
            }
        }

        if (block != null)
        {
            block.Text = text;
            block.Foreground = foreground;
            return;
        }

        block = new TextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = isHeader ? RowHeaderFontSize : RowFontSize,
            Foreground = foreground,
            FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = halign,
            TextWrapping = isHeader ? TextWrapping.Wrap : TextWrapping.NoWrap
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
        Avalonia.Automation.AutomationProperties.SetName(btn, text);
        if (!string.IsNullOrEmpty(tooltip))
        {
            SetTipSafe(btn, tooltip);
        }
        return btn;
    }

    /// <summary>Creates a muted italic loading placeholder text.</summary>
    public static TextBlock CreateLoadingText(string text) => new()
    {
        Text = text,
        Foreground = MutedColor,
        FontSize = RowFontSize,
        FontStyle = FontStyle.Italic
    };

    /// <summary>Creates a "... and N more" overflow indicator text.</summary>
    public static TextBlock CreateMoreText(int remaining) => new()
    {
        Text = $"  ... and {remaining} more",
        Foreground = MutedColor,
        FontSize = RowFontSize,
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
            FontSize = RowFontSize,
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
            FontSize = RowFontSize,
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
            FontSize = RowFontSize,
            Margin = new Thickness(0, 2, 0, 0)
        };
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 2);
        grid.Children.Add(valueBlock);
    }

    /// <summary>Resets a button's content text after a delay (e.g. "Copied!" -> "Copy").</summary>
    public static async Task ResetButtonTextAsync(Button btn, string originalText, int delayMs)
    {
        var box = CopyButtonCounters.GetOrCreateValue(btn);
        int currentId;
        lock (box)
        {
            box.Value++;
            currentId = box.Value;
        }

        await Task.Delay(delayMs).ConfigureAwait(false);

        Dispatcher.UIThread.Post(() =>
        {
            lock (box)
            {
                if (box.Value == currentId && btn.Parent != null && TopLevel.GetTopLevel(btn) != null)
                {
                    btn.Content = originalText;
                }
            }
        });
    }

    /// <summary>Creates a unicode sparkline string from a rented double array with absolute zero allocations.</summary>
    public static string CreateSparkline(double[] vals, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        int maxLen = 32;
        int len = Math.Min(count, maxLen);
        int startIndex = count - len;

        double min = double.MaxValue;
        double max = double.MinValue;
        for (int i = startIndex; i < count; i++)
        {
            var v = vals[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return string.Create(len, (vals, startIndex, min, max), static (span, state) =>
        {
            ReadOnlySpan<char> blocks = OperatingSystem.IsWindows() ? " .:-=+*#%" : " ▂▃▄▅▆▇█";
            var (values, start, minVal, maxVal) = state;
            double range = maxVal - minVal;

            for (int i = 0; i < span.Length; i++)
            {
                double ratio = range == 0 ? 0.5 : (values[start + i] - minVal) / range;
                double val = ratio * (blocks.Length - 1);
                double clampedVal = Math.Clamp(val, 0.0, blocks.Length - 1);
                int idx = (int)Math.Round(clampedVal);
                span[i] = blocks[idx];
            }
        });
    }

    /// <summary>Creates a unicode sparkline string from a sequence of values with absolute zero allocations.</summary>
    public static string CreateSparkline(IReadOnlyList<double> vals)
    {
        if (vals.Count <= 0)
        {
            return string.Empty;
        }

        int maxLen = 32;
        int len = Math.Min(vals.Count, maxLen);
        int startIndex = vals.Count - len;

        double min = double.MaxValue;
        double max = double.MinValue;
        for (int i = startIndex; i < vals.Count; i++)
        {
            var v = vals[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }

        return string.Create(len, (vals, startIndex, min, max), static (span, state) =>
        {
            // Literal string cast directly to ReadOnlySpan eliminates array allocations at JIT compilation boundaries.
            ReadOnlySpan<char> blocks = OperatingSystem.IsWindows() ? " .:-=+*#%" : " ▂▃▄▅▆▇█";
            var (values, start, minVal, maxVal) = state;
            double range = maxVal - minVal;

            for (int i = 0; i < span.Length; i++)
            {
                double ratio = range == 0 ? 0.5 : (values[start + i] - minVal) / range;
                double val = ratio * (blocks.Length - 1);
                double clampedVal = Math.Clamp(val, 0.0, blocks.Length - 1);
                int idx = (int)Math.Round(clampedVal);
                span[i] = blocks[idx];
            }
        });
    }
}
