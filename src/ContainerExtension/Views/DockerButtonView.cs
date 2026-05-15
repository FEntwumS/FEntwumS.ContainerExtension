using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using ContainerExtension.ViewModels;
using OneWare.Essentials.Enums;
using OneWare.Essentials.Services;

namespace ContainerExtension.Views;

    public class DockerButtonView : UserControl
    {
        private bool _isPinned;

        public DockerButtonView(IMainDockService dockService, DockerDiagnosticsViewModel dashboardVm)
        {
            var textBlock = new TextBlock
            {
                Text = "🐳 Docker",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var button = new Button
            {
                Content = textBlock,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1),
                Padding = new Thickness(6, 4),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            ToolTip.SetTip(button, "Docker Dashboard");

            button.Command = new RelayCommand(() =>
            {
                var existing = dockService.SearchView<DockerDiagnosticsViewModel>().FirstOrDefault();
                if (existing != null)
                {
                    // Detect uninitialized instance created by layout deserialization
                    if (existing.ServiceProvider == null || existing.Strategy == null)
                    {
                        try
                        {
                            dockService.CloseDockable(existing);
                        }
                        catch
                        {
                            // Ignore dock service close errors to prevent crash
                        }
                        // Continue to show the fully initialized dashboardVm
                    }
                    else
                    {
                        dockService.Show(existing);
                        return;
                    }
                }

                if (!_isPinned)
                {
                    dockService.Show(dashboardVm, DockShowLocation.RightPinned);
                    _isPinned = true;
                }
                else
                {
                    dockService.Show(dashboardVm);
                }
            });

            Margin = new Thickness(5, 0, 0, 0);
            Content = button;
        }
    }
