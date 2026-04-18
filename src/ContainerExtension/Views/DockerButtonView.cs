using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ContainerExtension.ViewModels;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Services;
using OneWare.Essentials.ViewModels;
using System;

namespace ContainerExtension.Views;

public class DockerButtonView : UserControl
{
    public DockerButtonView(IMainDockService dockService, DockerDiagnosticsViewModel dashboardVm)
    {
        var button = new Button
        {
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(1),
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = new ToolTip { Content = "Container Diagnostics" }
        };

        // UI initializations for Geometry can fail if invoked too early before
        // the Avalonia Renderer is initialized. Moving this inside a dispatcher
        // post block ensures the platform is ready.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var pathIcon = new PathIcon
                {
                    Data = Geometry.Parse(ContainerExtensionModule.WhaleIconPath),
                    Foreground = new SolidColorBrush(Color.Parse(ContainerExtensionModule.DockerBlueHex)),
                    Width = 14,
                    Height = 14
                };
                button.Content = pathIcon;
            }
            catch (Exception ex)
            {
                // Better slightly broken UI than a hard crash on IDE startup
                Console.WriteLine($"[ContainerExtension] Warning: Failed to render Docker button icon: {ex.Message}");
            }
        });

        button.Click += (_, _) =>
        {
            if (dockService.SearchView(dashboardVm.Id) == null)
            {
                dockService.RegisterLayoutExtension<DockerDiagnosticsViewModel>(DockShowLocation.RightPinned);
            }
            dockService.ShowView(dashboardVm.Id);
        };

        Content = button;
    }
}
