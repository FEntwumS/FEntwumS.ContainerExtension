# OneWare Container Extension

A **OneWare Studio** plugin that enables transparent, containerized execution of FPGA toolchains.

Developed as part of the Master's Thesis: "Design and Implementation of a Modular Architecture for the Transparent Integration of Containerized Execution Environments for Heterogeneous Open-Source Binary Toolchains in OneWare Studio" by **Mert Torun** at TH Koeln.

## Documentation

- [Getting Started](articles/getting-started.md) - Installation, first run, first containerized build
- [Configuration Guide](articles/configuration.md) - All 16 settings explained
- [Telemetry & Troubleshooting](articles/telemetry.md) - Debug execution issues
- [Architecture Overview](articles/architecture.md) - Internal design and component diagram
- [API Reference](api/index.md) - Auto-generated from XML doc comments

## Key Features

| Feature | Description |
| ------- | ----------- |
| Transparent Docker Execution | FPGA tools run inside containers without UI changes |
| Multi-Runtime Support | Auto-detects Docker, Podman, Colima, and OrbStack |
| Execution Telemetry | JSON Lines log with stats, export, and retention |
| Dockable Docker Dashboard | Live daemon status, containers, images, disk usage |
| Multi-Architecture | Platform override for Apple Silicon |
| UID/GID Injection | Prevents root-owned output files on Linux |
| Copy Docker Run | Reproduces executions outside the IDE |

## Quick Links

- [GitHub Repository](https://github.com/FEntwumS/FEntwumS.ContainerExtension)
- [MIT License](https://github.com/FEntwumS/FEntwumS.ContainerExtension/blob/main/License.md)
