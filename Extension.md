# OneWare Container Extension

Runs FPGA toolchains inside containers from within OneWare Studio, without changing the user's workflow
or requiring a host toolchain install.

## Capabilities

- **Non-root execution:** pins the container process to the host UID/GID via `--user`, so container output
  is not root-owned; on rootless runtimes, where `--user` is omitted, the image's `oneware` user applies.
  `tini` runs as PID 1 to reap children and forward signals.
- **Hybrid execution:** runs GHDL, Yosys, nextpnr, gmpack, Icarus, Verilator, and SymbiYosys in a
  container, with path and script mapping; with Allow Native Fallback turned on, runs the host's own tool
  when the daemon is offline.
- **Multi-runtime detection:** detects Docker, Podman, Colima, and OrbStack, with retry.
- **Execution telemetry:** JSON Lines log with statistics, export, image-digest pinning, and a
  per-execution "copy docker run" command.
- **Docker dashboard:** live container, image, and daemon status.
- **Orphan cleanup:** running containers are stopped when the IDE closes; stopped ones left behind,
  for instance by a crash, are removed at the next start.
- **Supply chain:** digest-pinned base image and a checksum-verified toolchain archive, SBOM and OIDC build attestations on releases, CodeQL and
  Trivy scans in CI.

## Getting started

1. Install via Extras > Extensions in OneWare Studio. On OneWare Studio before 1.0.43, restart it once
   so that the Container Dashboard's tab and menu entry appear.
2. Ensure a container engine (Docker, Podman, OrbStack, or Colima) is running.
3. Open Settings > Binary Management > Container Engine to configure.
4. Select the container execution strategy for any tool.

Developed by Mert Torun as part of a Master's thesis at TH Köln (2026), openly available at
[https://doi.org/10.57683/EPUB-3597](https://doi.org/10.57683/EPUB-3597).
