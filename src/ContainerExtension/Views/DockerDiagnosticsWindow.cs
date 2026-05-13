using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using OneWare.Essentials.Helpers;

namespace ContainerExtension.Views;

/// <summary>
/// Standalone window wrapper for the <see cref="DockerDiagnosticsView"/> dashboard.
/// Provides the Window chrome (title bar, size, transparency) while embedding
/// the UserControl that contains the actual dashboard UI.
/// <para>
/// This preserves the original popup-window experience alongside the new
/// dockable panel mode.
/// </para>
/// </summary>
public class DockerDiagnosticsWindow : Window
{
    /// <summary>
    /// Constructs a standalone dashboard window embedding the <see cref="DockerDiagnosticsView"/>.
    /// </summary>
    /// <param name="serviceProvider">The OneWare Studio DI service provider.</param>
    /// <param name="strategy">The Docker execution strategy for live API queries.</param>
    public DockerDiagnosticsWindow(IServiceProvider serviceProvider, DockerExecutionStrategy strategy)
    {
        Title = ContainerExtensionModule.DashboardTitle;
        Width = 660;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;

        // Attempt Mica > Acrylic > Blur transparency
        TransparencyLevelHint = [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur
        ];

        // Resolve the IDE's theme background brush
        object? bgRes = null;
        Application.Current?.TryFindResource(
            "ThemeBackgroundBrushOp",
            Application.Current.RequestedThemeVariant,
            out bgRes);
        bgRes ??= Application.Current?.FindResource("ThemeBackgroundBrushOp");
        Background = (bgRes as IBrush) ?? Brushes.Transparent;

        // Keyboard shortcuts: Cmd/Ctrl+W and Escape to close
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.W, PlatformHelper.ControlKey),
            Command = new RelayCommand(Close)
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(Close)
        });

        // Embed the dashboard UserControl
        Content = new DockerDiagnosticsView(serviceProvider, strategy);
    }
}
