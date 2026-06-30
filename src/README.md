# src

Source for the plugin.

| Directory | Description |
|---|---|
| `ContainerExtension` | The OneWare plugin. `DockerExecutionStrategy` is the `IToolExecutionStrategy` OneWare dispatches FPGA tool execution to, running it in a container via Docker.DotNet, with a native fallback. Also contains the registry client, settings layer, JSON-Lines telemetry, the input validators, and the Avalonia Docker dashboard (`Views/`, `ViewModels/`). Targets `net10.0`, `IsAotCompatible`; uses source-generated JSON and regex. |

Build settings (target framework, analyzers, warnings-as-errors, `NoWarn` justifications) are centralized
in the repository-root `Directory.Build.props`; package versions in `Directory.Packages.props`.
