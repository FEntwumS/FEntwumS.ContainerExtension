using System;
using System.Runtime.Serialization;
using OneWare.Essentials.ViewModels;
using Avalonia;
using OneWare.Essentials.Services;

namespace ContainerExtension.ViewModels;

/// <summary>
/// ViewModel representing the Docker Diagnostics dashboard tool within the OneWare environment.
/// </summary>
public class DockerDiagnosticsViewModel : ExtendedTool
{
    private IServiceProvider? _serviceProvider;
    private DockerExecutionStrategy? _strategy;

    [Newtonsoft.Json.JsonIgnore]
    public IServiceProvider? ServiceProvider
    {
        get => _serviceProvider ?? ContainerExtensionModule.GlobalServiceProvider;
        internal set => _serviceProvider = value;
    }

    [Newtonsoft.Json.JsonIgnore]
    public DockerExecutionStrategy? Strategy
    {
        get => _strategy ?? ServiceProvider?.Resolve<DockerExecutionStrategy>();
        internal set => _strategy = value;
    }

    private static readonly Avalonia.Media.IImage DashboardIcon = CreateDashboardIcon() ?? CreateFallbackIcon();

    private static Avalonia.Media.IImage? CreateDashboardIcon()
    {
        try
        {
            var geometry = Avalonia.Media.Geometry.Parse(ContainerExtensionModule.WhaleIconPath);
            var drawing = new Avalonia.Media.GeometryDrawing
            {
                Geometry = geometry,
                Brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush((uint)Avalonia.Media.Color.Parse(ContainerExtensionModule.DockerBlueHex).ToUInt32())
            };
            return new Avalonia.Media.DrawingImage { Drawing = drawing };
        }
        catch { return null; }
    }

    private static Avalonia.Media.IImage CreateFallbackIcon()
    {
        var drawing = new Avalonia.Media.GeometryDrawing
        {
            Geometry = new Avalonia.Media.RectangleGeometry(new Rect(0, 0, 16, 16)),
            Brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush((uint)Avalonia.Media.Colors.Gray.ToUInt32())
        };
        return new Avalonia.Media.DrawingImage { Drawing = drawing };
    }

    [Newtonsoft.Json.JsonConstructor]
    public DockerDiagnosticsViewModel() : base(DashboardIcon)
    {
        InitializeDockIdentity();
    }

    public DockerDiagnosticsViewModel(IServiceProvider serviceProvider, DockerExecutionStrategy strategy)
        : base(DashboardIcon)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(strategy);

        ServiceProvider = serviceProvider;
        Strategy = strategy;
        InitializeDockIdentity();
    }

    // Migrate a deserialized layout cache to the current dock identity.
    [OnDeserialized]
    private void OnDeserializedMethod(StreamingContext context)
    {
        // Restored layouts carry the legacy Id="Docker"; rewrite to the current identity so the
        // tool binds to the programmatic registration instead of falling back to a placeholder.
        InitializeDockIdentity();
    }

    // The dock identity (id, title, docking affordances) shared by both constructors and the
    // post-deserialization migration, kept in one place so the three paths cannot drift.
    private void InitializeDockIdentity()
    {
        Id = "Container_Dashboard";
        Title = ContainerExtensionModule.DashboardTitle;
        CanFloat = true;
        CanPin = true;
        CanClose = true;
        ShowInSelector = true;
        KeepPinnedDockableVisible = true;
    }
}
