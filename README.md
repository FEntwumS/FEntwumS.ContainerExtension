# OneWare Container Extension

[![Build](https://github.com/FEntwumS/FEntwumS.ContainerExtension/actions/workflows/dotnet.yml/badge.svg)](https://github.com/FEntwumS/FEntwumS.ContainerExtension/actions/workflows/dotnet.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](License.md)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

A [OneWare Studio](https://github.com/one-ware/OneWare) plugin that runs FPGA toolchains (GHDL, Yosys,
nextpnr, gmpack, Icarus, Verilator, SymbiYosys) inside containers without changing the user's workflow.
It intercepts tool execution, maps the project into a container, runs the unmodified tool, and streams
output back to the IDE, so a build behaves identically across machines with no host toolchain install.

Developed as part of the Master's thesis *"Design and Implementation of a Modular Architecture for the
Transparent Integration of Containerized Execution Environments for Heterogeneous Open-Source Binaries in
OneWare Studio"* by [Mert Torun](https://mtorun0x7cd.com) at TH Köln.

![Icon](Icon.svg)

## Installation

- **From OneWare Studio:** open Extensions, search for "Container Extension", and install. The plugin
  ships as a managed `net10.0` assembly; OneWare provides the runtime.
- **Side-load a local build:** publish the plugin and copy it into the OneWare plugins directory
  (`~/OneWareStudio/Packages/Plugins/` on Linux/macOS). See [docs/articles/getting-started.md](docs/articles/getting-started.md).

A running container engine (Docker, Podman, OrbStack, or Colima) is required for containerized execution.

## Architecture

| Project | Role |
|---|---|
| `src/ContainerExtension` | Plugin core. Implements `IToolExecutionStrategy`/`DockerExecutionStrategy`, the registry client, settings, telemetry, and the Avalonia Docker dashboard. Targets `net10.0`, `IsAotCompatible`, source-generated JSON and regex. |
| `src/ContainerBenchmarkHarness` | Headless harness that drives a tool through the real strategy, plus a telemetry stress mode. Used by the benchmarking suite and the smoke runner. |
| `tests/ContainerExtension.UnitTests` | xUnit unit tests (validators, command building, path mapping, telemetry, registry parsing, SSRF guards) and gated container E2E tests. |
| `tests/integration` | HDL fixtures and shell smoke runners (`run_all.sh` against the image, `run_harness_smoke.sh` through the strategy). |
| `tests/benchmarking_suite` | Cross-platform evaluation pipeline (`benchmark.py`, `run_evaluation.py`, `aggregate.py`). |
| `docker/oss-cad-suite` | Hardened image build for the open-source toolchain. |

## Execution model

The plugin coordinates execution through a hybrid strategy:

- **Containerized (default):** pulls and runs the toolchain image, injecting the host UID/GID and running
  as a non-root `oneware` user so output files are not root-owned. `tini` runs as PID 1 to reap
  children and forward signals.
- **Native fallback:** if the daemon is unreachable and `Allow Native Fallback` is enabled, the tool is
  located on the host `PATH` and run natively so work is not blocked.
- **Telemetry:** execution records are written as JSON Lines with a configurable level (`Off`,
  `Errors Only`, `Verbose`) and retention. See [docs/articles/telemetry.md](docs/articles/telemetry.md).

## Security

- Non-root container execution with host UID/GID injection; `tini` as PID 1. The default
  (non-privileged) path drops all capabilities (`--cap-drop=ALL`), forbids privilege escalation
  (`--security-opt no-new-privileges`), and caps the task count as a fork-bomb backstop.
- Host paths that escape the mounted workspace are remapped to an in-workspace sentinel rather than
  their real location; an explicit device/library allowlist is the only pass-through. The behaviour is
  pinned by an adversarial path-containment test corpus that runs in CI.
- Mount allow-listing rejects binds of critical host paths (`/etc`, `/proc`, `/sys`, the Docker socket, …).
- The registry client is HTTPS-only, scopes forwarded credentials to the matching host, and rejects
  references that resolve to loopback or internal addresses (SSRF defense).
- Supply chain: `NuGetAudit`, an SBOM and OIDC build attestations on releases, and CodeQL plus Trivy scans in CI.

## Supported container runtimes

| Runtime | Endpoint | Notes |
|---|---|---|
| Docker | `/var/run/docker.sock`, `~/.docker/run/docker.sock` | Probed first |
| Podman | `/run/user/{uid}/podman/podman.sock`, podman-machine | Rootless |
| OrbStack / Colima | per-runtime sockets | Probed via a priority list |
| Custom | `Custom Daemon Socket` setting | Overrides probing |

On Windows the daemon is reached over the named pipe; the connection path is validated before use.

## Building and testing

```bash
git clone --recurse-submodules https://github.com/FEntwumS/FEntwumS.ContainerExtension.git
cd FEntwumS.ContainerExtension

dotnet format OneWare.ContainerExtension.slnx --verify-no-changes
dotnet build  OneWare.ContainerExtension.slnx -warnaserror -c Release
dotnet test   OneWare.ContainerExtension.slnx -c Release

# Optional: headless integration smoke (requires a container engine)
cd tests/integration && ./run_all.sh
```

The container E2E tests are skipped in CI (Docker Hub rate limits and image-pull flakiness); they run
locally when a daemon and the toolchain image are present.

## Documentation

Guides live under [docs/articles](docs/articles) (getting started, configuration, architecture,
telemetry, evaluation, limitations). The API reference is generated with
[DocFX](https://dotnet.github.io/docfx/).

## Citation

If you use this software, cite it via [CITATION.cff](CITATION.cff).

## License

[MIT](License.md) © 2025–2026 [Mert Torun](https://mtorun0x7cd.com)
