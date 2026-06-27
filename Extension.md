# OneWare Container Extension

![Icon](Icon.svg)

Runs FPGA toolchains inside containers from within OneWare Studio, without changing the user's workflow
or requiring a host toolchain install.

## Capabilities

- **Non-root execution:** injects the host UID/GID and runs as `USER oneware`, so container output is not
  root-owned. `tini` runs as PID 1 to reap children and forward signals.
- **Hybrid execution:** runs GHDL, Yosys, nextpnr, gmpack, Icarus, Verilator, and SymbiYosys in a
  container, with path and script mapping; falls back to a host-native tool when the daemon is offline.
- **Multi-runtime detection:** detects Docker, Podman, Colima, and OrbStack, with retry.
- **Execution telemetry:** JSON Lines log with statistics, export, image-digest pinning, and a
  per-execution "copy docker run" command.
- **Docker dashboard:** live container, image, and daemon status.
- **Orphan cleanup:** dangling containers are removed on IDE shutdown or crash.
- **Supply chain:** reproducible image build, SBOM and OIDC build attestations on releases, CodeQL and
  Trivy scans in CI.

## Getting started

1. Install via the OneWare Studio Extension Manager.
2. Ensure a container engine (Docker, Podman, OrbStack, or Colima) is running.
3. Open Settings > Binary Management > Container Engine to configure.
4. Select the container execution strategy for any tool.

Developed by [Mert Torun](https://mtorun0x7cd.com) as part of a Master's thesis at TH Köln.
