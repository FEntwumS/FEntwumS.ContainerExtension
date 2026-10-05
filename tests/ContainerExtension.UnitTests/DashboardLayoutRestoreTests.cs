using ContainerExtension.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace ContainerExtension.UnitTests;

/// <summary>
/// Regression coverage for restoring the Container Dashboard from a saved layout. OneWare Studio
/// restores a registered tool window through a contract resolver whose default creator returns the
/// registered instance. A <c>[JsonConstructor]</c> on the dashboard took precedence over that creator,
/// so each start built a new dashboard from the layout, and the menu entry, which shows the registered
/// instance, added a second one beside it.
/// </summary>
public sealed class DashboardLayoutRestoreTests
{
    [Fact]
    public void Deserialize_DashboardRegisteredWithHost_ReturnsRegisteredInstance()
    {
        var registered = new DockerDiagnosticsViewModel();
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new RegisteredInstanceContractResolver(registered),
        };

        var restored = JsonConvert.DeserializeObject<DockerDiagnosticsViewModel>("{}", settings);

        Assert.Same(registered, restored);
    }

    /// <summary>
    /// Supplies the registered instance as the default creator, as OneWare Studio's layout serializer
    /// does for every type its service provider can resolve.
    /// </summary>
    private sealed class RegisteredInstanceContractResolver(object registered) : DefaultContractResolver
    {
        protected override JsonObjectContract CreateObjectContract(Type objectType)
        {
            var contract = base.CreateObjectContract(objectType);
            if (objectType == registered.GetType())
            {
                contract.DefaultCreator = () => registered;
            }
            return contract;
        }
    }
}
