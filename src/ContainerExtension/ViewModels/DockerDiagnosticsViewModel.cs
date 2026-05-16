using System;
using System.Runtime.Serialization; // Required for [OnDeserialized]
using OneWare.Essentials.ViewModels;

namespace ContainerExtension.ViewModels;

public class DockerDiagnosticsViewModel : ExtendedTool
{
    [Newtonsoft.Json.JsonIgnore]
    public IServiceProvider? ServiceProvider { get; internal set; }

    [Newtonsoft.Json.JsonIgnore]
    public DockerExecutionStrategy? Strategy { get; internal set; }

    [Newtonsoft.Json.JsonConstructor]
    public DockerDiagnosticsViewModel() : base((Avalonia.Media.IImage?)null!)
    {
        Id = "Container_Dashboard";
        Title = ContainerExtensionModule.DashboardTitle;
        CanFloat = true;
        CanPin = true;
        ShowInSelector = true; // Enables native text-only Dock button. Never disappears.
    }

    public DockerDiagnosticsViewModel(IServiceProvider serviceProvider, DockerExecutionStrategy strategy)
        : base((Avalonia.Media.IImage?)null!)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(strategy);

        Id = "Container_Dashboard";
        ServiceProvider = serviceProvider;
        Strategy = strategy;
        Title = ContainerExtensionModule.DashboardTitle;

        CanFloat = true;
        CanPin = true;
        ShowInSelector = true; // Enables native text-only Dock button. Never disappears.
    }

    // Intercept the JSON payload mapping to cleanly migrate the stale layout cache component
    [OnDeserialized]
    private void OnDeserializedMethod(StreamingContext context)
    {
        // Forcefully overwrite the stale cache payload properties (e.g., Id="Docker")
        // with the correct modern identity. This seamlessly merges the restored tool
        // with the programmatic layout registration and purges the Ghost icon fallback.
        Id = "Container_Dashboard";
        Title = ContainerExtensionModule.DashboardTitle;
        CanFloat = true;
        CanPin = true;
        ShowInSelector = true;
    }
}
