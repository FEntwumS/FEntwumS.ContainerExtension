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
    // Resolves a host theme resource for the application's *current* theme variant.
    // Resolving against ActualThemeVariant (rather than the default variant) is what
    // lets a light/dark toggle re-pick the correct brush when InitializeBrushes() re-runs.
    private static T GetResource<T>(string key, T defaultValue)
    {
        var app = Application.Current;
        if (app != null)
        {
            object? res = null;
            if (app.TryGetResource(key, app.ActualThemeVariant, out res) && res is T variantVal)
            {
                return variantVal;
            }
            if (app.TryGetResource(key, out res) && res is T val)
            {
                return val;
            }
        }
        return defaultValue;
    }

    // Hardcoded fallbacks used only when the host theme cannot be resolved (e.g. design-time).
    // GreenColor and WarningColor have no host *text* brush, so they remain curated constants
    // chosen to clear WCAG AA contrast on both the light and dark OneWare backgrounds.
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultFontColor = new((uint)Color.Parse("#E0E0E0").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultMutedColor = new((uint)Color.Parse("#B0B0B0").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultAccentColor = new((uint)Color.Parse(ContainerExtensionModule.DockerBlueHex).ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultRedColor = new((uint)Color.Parse("#D32F2F").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultCardBg = new((uint)Color.Parse("#14808080").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultBorderColor = new((uint)Color.Parse("#33808080").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultSubCardBg = new((uint)Color.Parse("#0D808080").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush DefaultOnAccentColor = new((uint)Colors.White.ToUInt32());

    // Curated success/warning constants: legible on both #FFFFFF and the dark OneWare backdrop.
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush CuratedGreenColor = new((uint)Color.Parse("#2E9E4F").ToUInt32());
    private static readonly Avalonia.Media.Immutable.ImmutableSolidColorBrush CuratedWarningColor = new((uint)Color.Parse("#C77F0A").ToUInt32());

    private static IBrush? _cachedFontColor;
    private static IBrush? _cachedMutedColor;
    private static IBrush? _cachedAccentColor;
    private static IBrush? _cachedGreenColor;
    private static IBrush? _cachedRedColor;
    private static IBrush? _cachedYellowColor;
    private static IBrush? _cachedCardBg;
    private static IBrush? _cachedBorderColor;
    private static IBrush? _cachedSubCardBg;
    private static IBrush? _cachedOnAccentColor;
    private static IBrush? _cachedInfoBannerBg;
    private static IBrush? _cachedErrorBannerBg;

    /// <summary>
    /// Re-resolves every cached semantic brush against the host theme for the current variant.
    /// Idempotent and cheap; called once on theme change so colors track light/dark toggles
    /// without re-querying the daemon.
    /// </summary>
    public static void InitializeBrushes()
    {
        _cachedFontColor = GetResource<IBrush>("ThemeForegroundBrush", DefaultFontColor);
        _cachedMutedColor = GetResource<IBrush>("ThemeForegroundLowBrush", DefaultMutedColor);
        _cachedAccentColor = GetResource<IBrush>("ThemeAccentBrush", DefaultAccentColor);
        // OneWare has no green *text* brush (GreenAccent is a fill); keep a curated success color.
        _cachedGreenColor = CuratedGreenColor;
        _cachedRedColor = GetResource<IBrush>("ErrorBrush", DefaultRedColor);
        // No host warning *text* brush with guaranteed contrast in both themes; keep a curated amber.
        _cachedYellowColor = CuratedWarningColor;
        _cachedCardBg = GetResource<IBrush>("ThemeControlLowBrush", DefaultCardBg);
        _cachedBorderColor = GetResource<IBrush>("ThemeBorderLowBrush", DefaultBorderColor);
        _cachedSubCardBg = GetResource<IBrush>("ThemeBackgroundBrushOp", DefaultSubCardBg);
        _cachedOnAccentColor = GetResource<IBrush>("HighlightForegroundBrush", DefaultOnAccentColor);
        _cachedInfoBannerBg = GetResource<IBrush>("NotificationCardInformationBackgroundBrush", DefaultSubCardBg);
        _cachedErrorBannerBg = GetResource<IBrush>("NotificationCardErrorBackgroundBrush", DefaultRedColor);
    }

    public static IBrush FontColor => _cachedFontColor ?? DefaultFontColor;
    public static IBrush MutedColor => _cachedMutedColor ?? DefaultMutedColor;
    public static IBrush AccentColor => _cachedAccentColor ?? DefaultAccentColor;
    public static IBrush GreenColor => _cachedGreenColor ?? CuratedGreenColor;
    public static IBrush RedColor => _cachedRedColor ?? DefaultRedColor;
    public static IBrush YellowColor => _cachedYellowColor ?? CuratedWarningColor;
    public static IBrush CardBg => _cachedCardBg ?? DefaultCardBg;

    // Chrome brushes — single source of truth for borders, sub-cards, on-accent text and banners.
    public static IBrush BorderColor => _cachedBorderColor ?? DefaultBorderColor;
    public static IBrush SubCardBg => _cachedSubCardBg ?? DefaultSubCardBg;
    public static IBrush OnAccentColor => _cachedOnAccentColor ?? DefaultOnAccentColor;
    public static IBrush InfoBannerBg => _cachedInfoBannerBg ?? DefaultSubCardBg;
    public static IBrush ErrorBannerBg => _cachedErrorBannerBg ?? DefaultRedColor;

    public static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Menlo, monospace");

    // Global Constants for Standardized Fonts and Layout Configurations
    public const double TitleFontSize = 20;
    public const double MetricFontSize = 16;
    public const double HeaderFontSize = 13;
    public const double RowHeaderFontSize = 12;
    public const double RowFontSize = 11;
    public const double SmallFontSize = 10;

    public static readonly Thickness CardPadding = new(12, 8);
    public static readonly CornerRadius CardCornerRadius = new(6);
    public static readonly CornerRadius InnerCornerRadius = new(4);
    public static readonly CornerRadius PillCornerRadius = new(3);
    public static readonly Thickness HairlineThickness = new(1);
    public static readonly Thickness RowMargin = new(0, 1);
    public static readonly Thickness SubCardPadding = new(12, 10, 12, 10);

    // Row-list overflow and recycle-pool caps (kept identical across Containers/Images sections).
    public const int MaxVisibleRows = 15;
    public const int MaxRecycledRows = 100;

    // Session-persistent collapsed/expanded state (survives panel close/reopen within session)
    private static readonly Dictionary<string, bool> SectionExpandedState = new(StringComparer.Ordinal);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, System.Runtime.CompilerServices.StrongBox<int>> CopyButtonCounters = new();

    public static Border CreateCard(string title, Control content, bool defaultExpanded = true)
    {
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
            if (!isHeader && !string.IsNullOrEmpty(text))
            {
                ToolTip.SetTip(block, text);
            }
            else
            {
                ToolTip.SetTip(block, null);
            }
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
            TextWrapping = isHeader ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = isHeader ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        if (!isHeader && !string.IsNullOrEmpty(text))
        {
            ToolTip.SetTip(block, text);
        }
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

    /// <summary>Creates a subtle 1px horizontal separator line for table headers.</summary>
    public static Border CreateSeparator() => new()
    {
        Height = 1,
        Background = BorderColor,
        Margin = new Thickness(0, 0, 0, 2)
    };

    /// <summary>
    /// Wraps a child control in the standard inset "sub-card" chrome (subtle fill, hairline
    /// border, inner radius). Single source of truth for the config/metadata group cards.
    /// </summary>
    public static Border CreateSubCard(Control child) => new()
    {
        Background = SubCardBg,
        BorderBrush = BorderColor,
        BorderThickness = HairlineThickness,
        CornerRadius = InnerCornerRadius,
        Padding = SubCardPadding,
        Margin = new Thickness(0, 0, 0, 8),
        Child = child
    };

    /// <summary>
    /// Builds the link-style "... and N more" / "Show less" overflow toggle used by the
    /// Containers and Images sections. Rendered in the accent color to read as clickable.
    /// </summary>
    public static Button CreateToggleMoreButton(string text, Action onClick, string? automationName = null)
    {
        var btn = new Button
        {
            Content = text,
            Foreground = AccentColor,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            FontSize = RowFontSize,
            FontStyle = FontStyle.Italic,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Command = new RelayCommand(onClick)
        };
        Avalonia.Automation.AutomationProperties.SetName(btn, automationName ?? text);
        return btn;
    }

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
    public static void AddInfoRow(Grid grid, int row, string label, string value, double labelIndent = 18)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = MutedColor,
            FontFamily = MonoFont,
            FontSize = RowFontSize,
            Margin = new Thickness(labelIndent, 2, 0, 0)
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

    /// <summary>Restores a button's content text after a delay (for example "Copied" back to "Copy").</summary>
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

    /// <summary>Renders a Unicode sparkline from a value window, producing only the result string.</summary>
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
}
