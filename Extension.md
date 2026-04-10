# OneWare Container Extension

![Icon](https://raw.githubusercontent.com/FEntwumS/FEntwumS.ContainerExtension/main/Icon.svg)

Provides transparent, containerized execution of FPGA toolchains within OneWare Studio.

## Features

- Run GHDL, Yosys, nextpnr, and other EDA tools in Docker/Podman containers — no native installation required
- Auto-detects Docker, Podman, Colima, and OrbStack runtimes with automatic retry
- Automatic image pull on first use with background pre-pull on startup
- Multi-architecture support for Apple Silicon (M1/M2/M3)
- Execution telemetry with stats, image digest pinning, per-execution CLI copy, and export
- **Docker Dashboard** — Live container, image, and daemon status at a glance
- Copy Docker Run command for debugging and reproducibility
- Container log export with window deduplication
- Completion notification for long-running jobs (>30s)
- Orphan container cleanup on IDE crash (thread-safe, single-execution guard)
- Configurable resource limits, per-tool image overrides, and `.env` file support

## Getting Started

1. Install via OneWare Studio Extension Manager
2. Ensure Docker Desktop (or compatible runtime) is running
3. Open Settings → Binary Management → Container Engine to configure
4. Select "DockerExecutionStrategy" as the execution strategy for any tool

Developed by **Mert Torun** as part of a Master's Thesis at TH Köln.
