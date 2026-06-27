# Architecture Overview

The OneWare Container Extension implements the **Hybrid Strategy Pattern** for transparent containerized execution of FPGA toolchains within OneWare Studio.

## Component Diagram

```mermaid
graph TD
    subgraph IDE [OneWare Studio IDE]
        CE[ContainerExtension Module<br/>- 19 settings + per-tool images<br/>- Strategy Injection<br/>- UI Extensions]
        ITES[IToolExecutionStrategy<br/>Plugin Interface]
        DES[DockerExecutionStrategy<br/>- Socket Probing<br/>- Image Resolution<br/>- Container Lifecycle<br/>- Stream Demultiplexing<br/>- Telemetry Logging]
        SDK[Docker.DotNet SDK<br/>- DockerClient<br/>- Unix Socket / TCP]
        DDVM[DockerDiagnosticsViewModel<br/>ExtendedTool<br/>- DockerDiagnosticsView<br/>- Connection Status<br/>- Images & Disk Usage<br/>- Live Containers<br/>- Active Configuration<br/>- Quick Actions<br/>- Recent Executions]
        CT[ContainerTelemetry<br/>- JSON Lines Logger<br/>- Stats Aggregation<br/>- Export / Clear]
        VAL[Validators<br/>- DockerImageFormat<br/>- DaemonSocket<br/>- ResourceThreshold<br/>- ContainerName]

        CE --> ITES
        ITES --> DES
        DES --> SDK
        CE --> DDVM
    end
```

## Source Files

| File | Purpose |
| ------ | --------- |
| `ContainerExtensionModule` | Registers 19 settings (plus per-tool image overrides), injects Docker strategy, dockable DataTemplate, health check |
| `DockerExecutionStrategy` | Full container lifecycle: socket probing, auto-pull, stream demux, telemetry |
| `DockerDiagnosticsView` | Docker Desktop-style live dashboard UserControl |
| `DockerDiagnosticsViewModel` | `ExtendedTool` docking adapter for the dashboard |
| `DockerCommandBuilder` | Host-to-container path mapping, parameter assembly, `.env` parsing, shell escaping |
| `ContainerTelemetry` | JSON Lines logger with stats and export |

## Image Resolution Hierarchy

```text
1. ONEWARE_DOCKER_IMAGE env var        (highest - CI/CD override)
2. ContainerImage_{tool} per-tool      (settings UI)
3. ContainerExtension_DefaultImage     (global setting)
4. hdlc/ghdl:yosys                     (hardcoded fallback)
```

## Container Lifecycle

```text
ResolveImage -> EnsureImage (pull if needed) -> BuildContainerParameters
    -> CreateContainer -> AttachStreams -> StartContainer
    -> DrainLines (demultiplex stdout/stderr)
    -> WaitContainer -> Log Telemetry -> Cleanup
```

## Docking System Integration

The dashboard integrates with OneWare Studio's dock infrastructure via the `ExtendedTool` base class:

1. **`DockerDiagnosticsViewModel`** extends `ExtendedTool` with `CanFloat = true` and `CanPin = true`
2. **`FuncDataTemplate`** inserted at index 0 in `Application.DataTemplates` to bypass OneWare's `ViewLocator` (which requires parameterless constructors)
3. **`IMainDockService.RegisterLayoutExtension<DockerDiagnosticsViewModel>(DockShowLocation.RightPinned)`** registers the default layout slot
4. An **`IApplicationCommandService`** command and a **`View > Tool Windows > Container Dashboard`** menu item (registered via `IWindowService`) both open the singleton ViewModel via `IMainDockService.Show(vm, DockShowLocation.RightPinned)`, preventing duplicate tabs
