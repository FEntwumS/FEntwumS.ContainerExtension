using System;
using Avalonia;
using Avalonia.Media;
using OneWare.Essentials.ViewModels;

namespace ContainerExtension.ViewModels;

/// <summary>
/// ViewModel for the Docker Diagnostics Dashboard, extending <see cref="ExtendedTool"/>
/// to integrate with OneWare Studio's dockable panel infrastructure.
/// <para>
/// This ViewModel acts as the docking adapter. The actual dashboard UI is built by
/// <see cref="Views.DockerDiagnosticsView"/> (a <c>UserControl</c>), which is set as
/// this tool's content via the DataTemplate registered in
/// <see cref="ContainerExtensionModule.Initialize"/>.
/// </para>
/// </summary>
public class DockerDiagnosticsViewModel : ExtendedTool
{
    /// <summary>The DI service provider for resolving OneWare services.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public IServiceProvider? ServiceProvider { get; }

    /// <summary>The Docker execution strategy instance for live API queries.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public DockerExecutionStrategy? Strategy { get; }

    /// <summary>
    /// Default parameterless constructor required by the Dock layout deserializer.
    /// </summary>
    [Newtonsoft.Json.JsonConstructor]
    public DockerDiagnosticsViewModel() : base(string.Empty)
    {
        Id = "Container_Dashboard";
        Title = ContainerExtensionModule.DashboardTitle;
        CanFloat = true;
        CanPin = true;
        ShowInSelector = false;
    }

    /// <summary>
    /// Constructs the dockable Docker Dashboard ViewModel.
    /// </summary>
    /// <param name="serviceProvider">The OneWare Studio DI service provider.</param>
    /// <param name="strategy">The Docker execution strategy for API access.</param>
    public DockerDiagnosticsViewModel(IServiceProvider serviceProvider, DockerExecutionStrategy strategy)
        : base(string.Empty)
    {
        Id = "Container_Dashboard";
        ServiceProvider = serviceProvider;
        Strategy = strategy;
        Title = ContainerExtensionModule.DashboardTitle;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var geometry = Geometry.Parse(ContainerExtensionModule.WhaleIconPath);
                var drawing = new GeometryDrawing
                {
                    Geometry = geometry,
                    Brush = new SolidColorBrush(Color.Parse(ContainerExtensionModule.DockerBlueHex))
                };
                // Assuming the base class has an Icon property of type object or IImage
                this.GetType().GetProperty("Icon")?.SetValue(this, new DrawingImage(drawing));
            }
            catch
            {
                // Ignore exception to ensure stability
            }
        });
        CanFloat = true;
        CanPin = true;
        ShowInSelector = false;

    }
}
