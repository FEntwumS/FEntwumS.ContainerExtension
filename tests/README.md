# tests

The `ContainerExtension.UnitTests` xUnit project: deterministic unit tests for validators, command building, path mapping, telemetry serialization/retention/concurrency, registry parsing and SSRF guards, and settings. Container E2E tests live here too, marked `[FactIfNoCI]` so they are skipped under CI and run locally when a daemon and the toolchain image are present.

Run the tests with `dotnet test OneWare.ContainerExtension.slnx -c Release`.
