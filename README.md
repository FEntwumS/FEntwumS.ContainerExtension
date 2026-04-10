# OneWare Container Extension

A **OneWare Studio** plugin that enables transparent, containerized execution of FPGA toolchains.
Developed as part of the Master's Thesis: *"Design and Implementation of a Modular Architecture for the Transparent Integration of Containerized Execution Environments for Heterogeneous Open-Source Binaries in OneWare Studio"* by **Mert Torun** at TH Köln.

![Icon](https://raw.githubusercontent.com/FEntwumS/FEntwumS.ContainerExtension/main/Icon.svg)

## Overview

This extension implements a **Hybrid Strategy Pattern** that enables transparent switching between native and Docker-based tool execution within OneWare Studio. It solves the "Environment Drift" problem by virtualizing only the tool execution layer while preserving the native IDE experience.

### Key Features

- **Transparent Docker Execution** — FPGA tools (GHDL, Yosys, nextpnr) run inside containers without UI changes
- **Multi-Runtime Support** — Auto-detects Docker, Podman, Colima, and OrbStack
- **Automatic Image Pull** — Downloads missing images on first use with background pre-pull on startup
- **Multi-Architecture** — Platform override for Apple Silicon (e.g., `linux/amd64`)
- **UID/GID Injection** — Prevents root-owned output files on Linux
- **Execution Telemetry** — JSON Lines log with stats, export, digest pinning, per-execution docker run copy, and configurable retention
- **Docker Dashboard** — Dockable panel with live daemon status, containers, images, disk usage, log export, digest pinning, settings at a glance; also available as standalone popup window
- **Health Check** — Verifies daemon connectivity on plugin load with automatic retry
- **`.env` File Support** — Inject environment variables from working directory
- **Orphan Cleanup** — Kills dangling containers on IDE crash (Interlocked guard for thread-safe single execution)
- **Copy Docker Run** — Generates equivalent CLI command for debugging and reproducibility
- **Completion Notification** — Console notification for long-running jobs (>30s)

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) or compatible runtime (Podman, Colima, OrbStack)
- [OneWare Studio](https://one-ware.com/) with `OssCadSuiteIntegration` module

## Quick Start

```bash
# Clone (with submodules for benchmarking workloads)
git clone --recurse-submodules https://github.com/FEntwumS/FEntwumS.ContainerExtension.git

# Build all projects
dotnet build

# Run unit tests
dotnet test

# Build and publish the benchmark harness
dotnet publish src/ContainerBenchmarkHarness/ContainerBenchmarkHarness.csproj \
  -c Release -o benchmarking_suite/harness_bin
```

## Supported Container Runtimes

| Runtime | Platform | Socket Path | Notes |
| --------- | ---------- | ------------- | ------- |
| Docker Desktop | macOS, Windows, Linux | `/var/run/docker.sock` | Recommended, probed first |
| Docker (user) | macOS, Linux | `~/.docker/run/docker.sock` | Rootless Docker |
| Podman | Linux | `/run/user/{uid}/podman/podman.sock` | Rootless via user socket |
| Colima | macOS | `~/.colima/default/docker.sock` | Lightweight Docker on macOS |
| Podman Machine | macOS | `~/.local/share/containers/podman/machine/podman.sock` | Podman via VM |
| OrbStack | macOS | `~/.orbstack/run/docker.sock` | Fast alternative to Docker Desktop |

Socket auto-detection probes all of the above in priority order. Override with the **Custom Daemon Socket** setting.

## Architecture

| Component | Lines | Purpose |
| --- | --- | --- |
| `ContainerExtensionModule` | ~565 | Registers 16 settings, injects Docker strategy, dockable DataTemplate, retry health check (10×3s) + background pre-pull with CancellationToken |
| `DockerExecutionStrategy` | ~1449 | Full container lifecycle: socket probing, auto-pull, stream demux, telemetry, dashboard queries, docker run reconstruction, Interlocked cleanup guard |
| `DockerDiagnosticsView` | ~1705 | Docker Desktop-style live dashboard UserControl: daemon status, containers, images & disk usage, execution history table, log export with deduplication, digest pinning, copy docker run |
| `DockerDiagnosticsViewModel` | ~56 | ExtendedTool docking adapter — integrates dashboard into OneWare's dock system with whale icon |
| `DockerDiagnosticsWindow` | ~68 | Standalone Window wrapper — preserves popup experience with Mica transparency, keyboard shortcuts |
| `ContainerTelemetry` | ~431 | JSON Lines logger with cross-process Mutex, stats, export, batch trimming (120% threshold), retention |
| `DockerButtonView` | ~65 | Toolbar whale icon — opens dashboard as dockable panel (Bottom dock) with standalone window fallback |
| `ContainerBenchmarkHarness` | ~128 | CLI harness for benchmarking the .NET SDK backend |
| `ContainerExtensionTests` | ~840 | 92 unit tests: validators, settings constants, telemetry, stream processing, env file parsing, edge cases |

### Container Lifecycle

```text
ResolveImage (4-level fallback)
    → EnsureImage (pull policy: always / if-not-present / never)
        → BuildContainerParameters (mounts, UID/GID, env, resource limits, labels)
            → CreateContainer → AttachStreams → StartContainer
                → [parallel] DrainLines (demux stdout/stderr) + CollectResourceStats
                    → WaitContainer → Log Telemetry → Cleanup
```

## Testing

**92 test methods** across 10 categories:

| Category | Tests | Coverage |
| ---------- | ------: | ---------- |
| Docker image format validation | 20 | Registry:port, digest refs, uppercase, spaces, trailing slash, long names |
| Daemon socket validation | 7 | unix://, tcp://, npipe://, invalid schemes |
| Resource threshold validation | 8 | Below/at/above 75%, exceeds total, negative, non-double |
| Container name prefix | 18 | Length limits, unicode, digit-start, special chars, mixed separators |
| Setting constants | 3 | Prefix consistency, fallback image, value correctness |
| Telemetry CRUD & stats | 10 | Round-trip, stats, clear, trimming, export, ordering, cancelled, zero entries |
| DrainLines (stream demux) | 11 | Single/multi-line, carry-over, CRLF, large chunks, empty lines, handler returns |
| ParseEnvFile | 10 | Basic KV, comments, quotes, hash-in-value, empty values, unicode, duplicates, key-only |
| Resource profile telemetry | 3 | Round-trip, OOM killed, null backward compat |
| Smoke + memory | 2 | Assembly loading, host memory sanity |

```bash
dotnet test --verbosity normal
```

## Settings

All settings are registered under **Binary Management → Container Engine**:

| # | Setting | Type | Default | Description |
| --- | --- | --- | --- | --- |
| 1 | Default Toolchain Image | TextBox (validated) | `hdlc/ghdl:yosys` | Fallback image for all tools |
| 2 | Auto-Remove Containers | CheckBox | ✅ on | Remove containers after execution |
| 3 | Image Platform | ComboBox | *(auto)* | Force platform (e.g., `linux/amd64`) |
| 4 | Memory Limit | Slider (256 MB step) | 0 (no limit) | Container memory cap (auto-detected host max) |
| 5 | CPU Cores Limit | Slider | 0 (no limit) | Container CPU cap (auto-detected host max) |
| 6 | Execution Timeout | Slider | 0 (no timeout) | Kill container after N minutes |
| 7 | Network Mode | ComboBox | `bridge` | Docker network mode (bridge/host/none) |
| 8 | Image Pull Policy | ComboBox | `if-not-present` | When to pull images (always/if-not-present/never) |
| 9 | Log Level | ComboBox | `Verbose` | Off / Errors Only / Info / Verbose |
| 10 | Show Timestamps | CheckBox | ✅ on | Prepend HH:mm:ss.fff to SDK log messages |
| 11 | Container Name Prefix | TextBox | `containerextension-` | Prefix for generated container names |
| 12 | Telemetry Retention | ComboBox | `100` | Max telemetry entries (None/25/50/100/250/500/1000/Unlimited) |
| 13 | Extra Container Labels | TextBox | *(empty)* | Space-separated `key=value` container labels |
| 14 | Dashboard Refresh | ComboBox | `Manual` | Auto-refresh interval (Manual/2s/5s/10s/15s/30s/60s/120s) |
| 15 | Container Runtime Path | FilePath | *(empty)* | Absolute path to docker/podman CLI |
| 16 | Custom Daemon Socket | TextBox (validated) | *(empty)* | Override DOCKER_HOST |

Per-tool image overrides are dynamically registered as `ContainerImage_{toolName}`.

## Image Resolution Hierarchy

```text
1. ONEWARE_DOCKER_IMAGE env var        (highest — CI/CD override)
2. ContainerImage_{tool} per-tool      (settings UI)
3. ContainerExtension_DefaultImage     (global setting)
4. hdlc/ghdl:yosys                     (hardcoded fallback)
```

## Environment Variables

| Variable | Scope | Purpose |
| ---------- | ------- | --------- |
| `ONEWARE_DOCKER_IMAGE` | Host | Override the Docker image for all tools (level 1 in resolution hierarchy) |
| `DOCKER_HOST` | Host | Override the Docker daemon socket (bypasses auto-detection) |
| `.env` file | Per-project | Key-value pairs injected into the container. Supports quotes, comments, and inline `#` |

## Contributing

```bash
# Build
dotnet build

# Run tests
dotnet test --verbosity normal

# Publish plugin for OneWare Studio
dotnet build src/ContainerExtension/ContainerExtension.csproj -c Release -o publish

# Generate API documentation (requires docfx tool)
dotnet tool restore
dotnet docfx docfx.json
```

## API Documentation

Auto-generated API documentation is available via [DocFX](https://dotnet.github.io/docfx/). The documentation source is defined in [`docfx.json`](docfx.json) with articles in [`articles/`](articles/):

- [Getting Started](articles/getting-started.md)
- [Configuration Guide](articles/configuration.md)
- [Telemetry & Troubleshooting](articles/telemetry.md)
- [Architecture Overview](articles/architecture.md)

## License

[MIT](License.md) © 2026 Mert Torun
