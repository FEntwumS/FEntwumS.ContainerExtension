# Getting Started

Installation, configuration, and a first containerized build with the Container Extension for OneWare Studio.

## Prerequisites

- [OneWare Studio](https://one-ware.com/) 1.0.40 or later
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

1. Open OneWare Studio and choose **Extras > Extensions**.
2. Search for **Container Extension** and click **Install**.
3. On OneWare Studio before 1.0.43, restart it once, so that the Container Dashboard's tab and menu entry
   appear. From 1.0.43 on, they appear right after the install.

If the search finds nothing, first add the manifest link
`https://raw.githubusercontent.com/FEntwumS/FEntwumS.ContainerExtension/main/oneware-extension.json` to
**Custom Package Sources** under **Settings > Package Manager > Sources**.

## First Run

1. **Launch OneWare Studio** - The plugin loads automatically
2. **Health Check** - On startup, the extension verifies Docker daemon connectivity in the background. If the daemon starts only after OneWare Studio, the next run or refresh of the dashboard connects to it, without a restart
3. **Open the Dashboard** - Choose **View > Tool Windows > Container Dashboard** (or run the *Container Dashboard* command). It opens as a right-pinned dockable panel with a whale icon
4. **Verify Connection** - The dashboard shows daemon health, Docker version, and OS info

## Your First Containerized Build

1. Open an FPGA project in OneWare Studio (e.g., a VHDL project)
2. Build the toolchain image once: in the Container Dashboard, choose **Build Local Image**, keep the pinned
   version and click **Build**. The build runs in OneWare's terminal and produces
   `fentwums/oss-cad-suite:latest`, the default image for all tools; it is on no registry, so no pull can fetch it
3. Under **Extras > Settings > Binary Management > Execution Strategy**, switch the tool from `NativeExecutionStrategy` to `DockerExecutionStrategy` (OneWare lists the raw strategy keys)
4. Run the tool (e.g., GHDL Analyze) - the extension will:
   - Pull the tool's image if it comes from a registry and is not cached yet
   - Mount your project directory into the container
   - Execute the tool inside the container
   - Stream output back to the IDE
5. View execution details in the Container Dashboard's **Execution History** section

## Dashboard Features

The Container Dashboard provides:

| Section | Description |
| ------- | ----------- |
| **Quick Actions** | One-click pull, prune, and hello-world test buttons |
| **Connection Status** | Daemon health, Docker version, OS, CPU/RAM info |
| **Containers** | Live container list with stop/remove/view-logs buttons |
| **Images & Disk** | Cached images with sizes, reclaimable space indicator |
| **Configuration** | Snapshot of all active Container Engine settings |
| **Execution History** | Up to 50 telemetry entries with timing and exit codes |

## Next Steps

- [Configuration Guide](configuration.md) - Fine-tune all 18 settings
- [Telemetry & Troubleshooting](telemetry.md) - Debug execution issues
- [Architecture Overview](architecture.md) - Understand the internal design
