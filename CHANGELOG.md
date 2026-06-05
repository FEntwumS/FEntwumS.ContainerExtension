# Changelog

All notable changes to the OneWare Container Extension are documented here.
This format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] - 2026-06-05

### Fixed

- **GHDL Path Mapping Bad Unit Name** — Resolved elaboration failure by stripping file paths to unit names for make (`-m`), elaborate (`-e`), and run (`-r`) options.
- **GHDL Work Library Argument Resolution** — Fixed `bad character in identifier` crash by extracting directory basename for separate and equals work library arguments (e.g., `--work=ghdl`).
- **Yosys Compound Command Execution** — Fixed synthesis exit code 133 crash by recursively stripping outer quotes from compound command scripts passed to `-p` before mapping and tokenizing.
- **gmpack Write Permission Denials** — Allowed read-write workspace binds for synthesis, simulation, and packing tools (e.g., `gmpack`, `icepack`) while maintaining read-only sandboxing for programmer tools.
- **Code Commentary Refactoring** — Cleansed code and unit tests of redundant or non-standard comment markings.

## [1.0.0] - 2026-06-05

### Security & Hardening (Production-Ready Modernization)

- **Supply Chain Security** — Enabled NuGet Auditing (`all`/`low`), deterministic builds, and continuous integration constraints in MSBuild properties.
- **SLSA Provenance** — Configured automated SBOM generation and OIDC artifact build-attestations during the release workflow.
- **Container Hardening** — Integrated `tini` for PID 1 Zombie Reaping, enforced non-root execution (`USER oneware`), stripped suid/sgid bits, and upgraded base to `ubuntu:24.04`.
- **Static Analysis** — Integrated GitHub CodeQL SAST scanning for the C# codebase and `Trivy` for the Dockerfiles. Removed technical-debt suppressions in `.editorconfig`.

### Added

- **Sparkline Trends** — Visualized CPU and RAM usage trends directly in the `DockerDiagnosticsView` history panel.

### Changed

- **Project Structure** — Consolidated documentation into `docs/`, moved scratch scripts to `scripts/`, and standardized tests under `tests/benchmarking_suite/`.
- **Telemetry Processing** — Migrated history I/O to a background asynchronous task to significantly improve UI responsiveness.
- **UI Layout Optimization** — Introduced a state-caching fingerprint mechanism to eliminate UI layout thrashing when telemetry updates occur.
- **Docker Wrapper Resiliency** — Strengthened Docker service exception handling to prevent runtime panics from being masked.

### Fixed

- **Deadlock Resolution** — Fixed `AbandonedMutexException` in `ContainerTelemetry` to prevent cross-process deadlocks during concurrent logging.
- **Detachment Race Conditions** — Fixed edge case race conditions in `DockerDiagnosticsView` view-model detachments.
- **Regex Memory Thrashing** — Updated regex-based setting validators to use `RegexOptions.Compiled` for optimized execution.

### Initial Features

- **Hybrid Strategy Pattern** — Transparent switching between native and Docker-based FPGA tool execution.
- **Multi-Runtime Support** — Auto-detection of Docker, Podman, Colima, and OrbStack via socket probing.
- **Execution Telemetry** — JSON Lines logger with cross-process Mutex.
- **UID/GID Injection** — Prevents root-owned output files on Linux.
- **Orphan Container Cleanup** — Kills dangling containers on IDE crash (Interlocked guard).
