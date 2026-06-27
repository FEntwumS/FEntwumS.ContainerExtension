using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace ContainerExtension.UnitTests;

// Serializes the test classes that mutate the process-global ContainerTelemetry sink so their
// temporary log directories and shared static state do not race. xUnit requires the collection
// definition to be a public, discoverable type whose name matches the referenced collection.
[CollectionDefinition("TelemetryTests")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definitions must be public for discovery.")]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Conventional xUnit collection fixture name.")]
public sealed class TelemetryTestsCollection { }
