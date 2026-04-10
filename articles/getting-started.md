# Getting Started

This guide walks you through installing, configuring, and using the Container Extension for OneWare Studio.

## Prerequisites

- [OneWare Studio](https://one-ware.com/) installed
- A container runtime:
  - [Docker Desktop](https://www.docker.com/products/docker-desktop/) (recommended)
  - [Podman](https://podman.io/)
  - [Colima](https://github.com/abiosoft/colima) (macOS)
  - [OrbStack](https://orbstack.dev/) (macOS)

## Installation

### From Source

```bash
# Clone the repository
git clone --recurse-submodules https://github.com/FEntwumS/FEntwumS.ContainerExtension.git
cd FEntwumS.ContainerExtension

# Build the plugin
dotnet build -c Release

# Deploy to OneWare Studio
cp -r src/ContainerExtension/bin/Release/net10.0/* \
  ~/OneWareStudio/Packages/Plugins/ContainerExtension/
```

### From OneWare Package Manager

> Coming soon — the plugin will be available via OneWare Studio's built-in package manager.

## First Run

1. **Launch OneWare Studio** — The plugin loads automatically
2. **Health Check** — On startup, the extension verifies Docker daemon connectivity. Look for the whale icon (🐳) in the right toolbar
3. **Click the Whale** — Opens the **Container Dashboard** as a dockable panel at the bottom of the IDE
4. **Verify Connection** — The dashboard shows daemon health, Docker version, and OS info

## Your First Containerized Build

1. Open an FPGA project in OneWare Studio (e.g., a VHDL project)
2. In any tool's settings, switch the **Execution Strategy** from `Native` to `Docker`
3. Run the tool (e.g., GHDL Analyze) — the extension will:
   - Pull the required image if not cached locally
   - Mount your project directory into the container
   - Execute the tool inside the container
   - Stream output back to the IDE in real-time
4. View execution details in the Container Dashboard's **Recent Executions** section

## Dashboard Features

The Container Dashboard provides:

| Section | Description |
| ------- | ----------- |
| **Quick Actions** | One-click pull, prune, and hello-world test buttons |
| **Connection Status** | Daemon health, Docker version, OS, CPU/RAM info |
| **Containers** | Live container list with stop/remove/view-logs buttons |
| **Images & Disk** | Cached images with sizes, reclaimable space indicator |
| **Configuration** | Snapshot of all active Container Engine settings |
| **Recent Executions** | Last 10 telemetry entries with timing and exit codes |

## Next Steps

- [Configuration Guide](configuration.md) — Fine-tune all 16 settings
- [Telemetry & Troubleshooting](telemetry.md) — Debug execution issues
- [Architecture Overview](architecture.md) — Understand the internal design
