# OneWare Container Extension

A **OneWare Studio** plugin that enables transparent, containerized execution of FPGA toolchains. Built on a modernized infrastructure paradigm.
Developed as part of the Master's Thesis: *"Design and Implementation of a Modular Architecture for the Transparent Integration of Containerized Execution Environments for Heterogeneous Open-Source Binaries in OneWare Studio"* by **Mert Torun** at TH Köln.

![Icon](Icon.svg)

## Hardening & Security

This extension adheres to hardened security practices to eliminate "Environment Drift" and lateral escape risks:
- **Zero-Privilege Containers**: Automatically injects Host UID/GID and enforces `USER oneware` execution to drop root privileges entirely.
- **PID 1 Signal Management**: Wraps toolchains with `tini` to ensure faultless zombie process reaping and propagation of kill signals.
- **Deterministic Dependencies**: Pipeline guarded by `<NuGetAudit>`, SLSA SBOM generation, and GitHub CodeQL SAST scanning.
- **Immutable Provenance**: GitHub Releases embed cryptographic OIDC build-attestations.

## Architecture & Features

- **Transparent Execution** — FPGA tools (GHDL, Yosys, nextpnr, gmpack) run strictly inside ephemeral sandboxes with robust path mapping, GHDL unit-name translation, compound script tokenization, and automatic write-access binds for output/bitstream packers.
- **Multi-Runtime Support** — Auto-detects Docker, Podman, Colima, and OrbStack runtimes.
- **Automatic Image Pull** — Background pre-pull on startup with fallback logic and retry capabilities.
- **Execution Telemetry** — JSON Lines log with stats, export, and image digest verification.
- **Docker Dashboard** — Dockable panel with live daemon status, containers, images, disk usage, and sparkline trends.
- **Orphan Cleanup** — Strict Interlocked-guarded teardown logic kills dangling containers on IDE crash.

## Quick Start

```bash
# Clone (with submodules)
git clone --recurse-submodules [https://github.com/FEntwumS/FEntwumS.ContainerExtension.git](https://github.com/FEntwumS/FEntwumS.ContainerExtension.git)

# Verify Formatting & Determinism
dotnet format --verify-no-changes
dotnet build -warnaserror

# Execute Tests
dotnet test --verbosity normal

```

## Supported Container Runtimes

| Runtime | Protocol | Notes |
| --- | --- | --- |
| Docker Desktop | `/var/run/docker.sock` | Recommended, probed first |
| Podman | `/run/user/{uid}/podman/podman.sock` | Rootless enforcement |
| OrbStack / Colima | Custom Sockets | Probed via priority list |

## API Documentation

Auto-generated API documentation is available via [DocFX](https://dotnet.github.io/docfx/).

## License

[MIT](License.md) © 2025 - 2026 Mert Torun
