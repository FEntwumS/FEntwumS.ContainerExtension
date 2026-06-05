# OneWare Container Extension (Production-Ready)

![Icon](Icon.svg)

Provides transparent, containerized execution of FPGA toolchains within OneWare Studio with an emphasis on security and operational resilience.

## Production-Ready Capabilities

- **Zero-Privilege Containers**: Automatically injects Host UID/GID and enforces `USER oneware` execution to drop root privileges entirely.
- **PID 1 Signal Management**: Wraps toolchains with `tini` to ensure faultless zombie process reaping and propagation of IDE kill signals.
- **Deterministic Toolchains**: Binaries guaranteed via SLSA SBOM generation, NuGet Audits, and GitHub CodeQL SAST.
- **Immutable Provenance**: GitHub Releases embed cryptographic OIDC build-attestations.

## Standard Features

- **Hybrid Strategy Execution**: Run GHDL, Yosys, nextpnr, gmpack, and other EDA tools seamlessly with robust path/script mapping and auto-write permission detection — no native installation required.
- **Multi-Runtime Detection**: Auto-detects Docker, Podman, Colima, and OrbStack runtimes with automatic retry.
- **Execution Telemetry**: JSON Lines log with stats, export, digest pinning, per-execution docker run copy.
- **Docker Dashboard**: Live container, image, and daemon status at a glance.
- **Orphan Container Cleanup**: Strict Interlocked-guarded teardown logic kills dangling containers on IDE crash.

## Getting Started

1. Install via OneWare Studio Extension Manager
2. Ensure Docker Desktop (or Podman) is running
3. Open Settings → Binary Management → Container Engine to configure
4. Select `DockerExecutionStrategy` as the execution strategy for any tool

Developed by **Mert Torun** as part of a Master's Thesis at TH Köln.
