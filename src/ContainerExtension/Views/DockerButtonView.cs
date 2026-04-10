using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using ContainerExtension.ViewModels;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Services;

namespace ContainerExtension.Views;

/// <summary>
/// Native Avalonia <see cref="UserControl"/> representing the Docker "Whale" toolbar button.
/// Registered in the IDE's right toolbar extension point, it provides one-click access
/// to the <see cref="DockerDiagnosticsView"/> dashboard.
/// <para>
/// <b>Primary mode:</b> Opens the dashboard as a <b>dockable panel</b> (right dock)
/// via <see cref="IMainDockService"/>, following the same pattern as
/// Error List, Terminal, and Source Control panels.
/// </para>
/// </summary>
public class DockerButtonView : UserControl
{
    /// <summary>
    /// Constructs the toolbar button with a whale icon SVG path.
    /// Clicking the button shows/focuses the <see cref="DockerDiagnosticsView"/> dashboard
    /// as a dockable panel on the right side of the IDE.
    /// </summary>
    /// <param name="dockService">The OneWare dock service for panel management.</param>
    /// <param name="dashboardVm">The singleton dashboard ViewModel created by the module.</param>
    public DockerButtonView(IMainDockService dockService, DockerDiagnosticsViewModel dashboardVm)
    {
        var pathIcon = new PathIcon
        {
            Data = Geometry.Parse(ContainerExtensionModule.WhaleIconPath),
            Foreground = new SolidColorBrush(Color.Parse(ContainerExtensionModule.DockerBlueHex)),
            Width = 14,
            Height = 14
        };

        var button = new Button
        {
            Content = pathIcon,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(1),
            Padding = new Thickness(6)
        };

        ToolTip.SetTip(button, "Container Dashboard");

        button.Command = new RelayCommand(() =>
        {
            // Close any stale/ghost panels left from layout restore
            foreach (var stale in dockService.SearchView<DockerDiagnosticsViewModel>().ToList())
                dockService.CloseDockable(stale);

            // Show our singleton VM as a right-docked panel (matches AI Chat placement)
            dockService.Show(dashboardVm, DockShowLocation.RightPinned);
        });

        Margin = new Thickness(5, 0, 0, 0);
        Content = button;
    }
}
