# src

Source for the plugin and its developer harness.

| Directory | Description |
|---|---|
| `ContainerExtension` | The OneWare plugin. `DockerExecutionStrategy` is the `IToolExecutionStrategy` OneWare dispatches FPGA tool execution to, running it in a container via Docker.DotNet, with a docker-CLI and a native fallback. Also contains the registry client, settings layer, JSON-Lines telemetry, the input validators, and the Avalonia Docker dashboard (`Views/`, `ViewModels/`). Targets `net10.0`, `IsAotCompatible`; uses source-generated JSON and regex. |
| `ContainerBenchmarkHarness` | Console harness that runs a single tool through the real `DockerExecutionStrategy` without the GUI, plus a `stress-telemetry` mode. Consumed by `tests/benchmarking_suite` and `tests/integration/run_harness_smoke.sh`. |

Build settings (target framework, analyzers, warnings-as-errors, `NoWarn` justifications) are centralized
in the repository-root `Directory.Build.props`; package versions in `Directory.Packages.props`.
