# tests

The `ContainerExtension.UnitTests` xUnit project: deterministic unit tests for validators, command building, path mapping, telemetry serialization/retention/concurrency, registry parsing and SSRF guards, and settings. Container E2E tests live here too, marked `[FactIfNoCI]` so they are skipped under CI; what they need to run locally is listed under [Running the container tests](../CONTRIBUTING.md#running-the-container-tests).

Run the tests with `dotnet test OneWare.ContainerExtension.slnx -c Release`.
