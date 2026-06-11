# Changelog

All notable changes to the OneWare Container Extension are documented here.
This format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-06-12

### Added

- **Hybrid Strategy Pattern** - Transparent switching between native and Docker-based FPGA tool execution.
- **Multi-Runtime Support** - Auto-detection of Docker, Podman, Colima, and OrbStack via socket probing.
- **Execution Telemetry** - JSON Lines logger with cross-process Mutex.
- **UID/GID Injection** - Prevents root-owned output files on Linux.
- **Sparkline Trends** - Visualized CPU and RAM usage trends directly in the `DockerDiagnosticsView` history panel.
- **Project Structure** - Consolidated documentation into `docs/`, moved scratch scripts to `scripts/`, and standardized tests under `tests/benchmarking_suite/`.
- **Telemetry Processing** - Background asynchronous task architecture to significantly improve UI responsiveness.
- **UI Layout Optimization** - State-caching fingerprint mechanism to eliminate UI layout thrashing when telemetry updates occur.
- **Docker Wrapper Resiliency** - Strengthened Docker service exception handling to prevent runtime panics from being masked.
- **Code Commentary Refactoring** - Cleansed code and unit tests of redundant or non-standard comment markings.
- **GHDL Elaboration Compatibility** - Stripping file paths to unit names for make (`-m`), elaborate (`-e`), and run (`-r`) options.
- **GHDL Work Library Argument Resolution** - Extracting directory basename for separate and equals work library arguments (e.g., `--work=ghdl`).
- **Yosys Compound Command Execution** - Recursively stripping outer quotes from compound command scripts passed to `-p` before mapping and tokenizing.
- **gmpack Write Permission Denials** - Read-write workspace binds for synthesis, simulation, and packing tools (e.g., `gmpack`, `icepack`) while maintaining read-only sandboxing for programmer tools.
- **Orphan Container Cleanup** - Kills dangling containers on IDE crash (Interlocked guard).
- **Deadlock Resolution** - Implemented robust locking in `ContainerTelemetry` to prevent cross-process deadlocks during concurrent logging.
- **Detachment Race Conditions** - Robust edge case handling in `DockerDiagnosticsView` view-model detachments.
- **Regex Memory Thrashing** - Regex-based setting validators use `RegexOptions.Compiled` for optimized execution.
- **Supply Chain Security** - Enabled NuGet Auditing (`all`/`low`), deterministic builds, and continuous integration constraints in MSBuild properties.
- **SLSA Provenance** - Configured automated SBOM generation and OIDC artifact build-attestations during the release workflow.
- **Container Hardening** - Integrated `tini` for PID 1 Zombie Reaping, enforced non-root execution (`USER oneware`), stripped suid/sgid bits, and upgraded base to `ubuntu:24.04`.
- **Static Analysis** - Integrated GitHub CodeQL SAST scanning for the C# codebase and `Trivy` for the Dockerfiles.

### Fixed

- **Container Permissions**: Defaulted container environment `HOME=/tmp` to resolve write permission failures in layout tools (like nextpnr) when running under custom host UIDs.
- **Concurrency & Re-entrancy**: Resolved a setting version write collision and unawaited file deserialization race condition in the Netlist Viewer's `StorageService`.
- **UI Diagnostics & VM Restoration**: Added global service location fallbacks to support ViewModel lifecycle recovery after workspace layout restoration.
- **Logs Streaming & Disposal**: Fixed lifecycle leakage of container log `CancellationTokenSource` and suppressed unobserved `ObjectDisposedException`/`OperationCanceledException` on view detachment.
- **Diagnostics Log Cleanup**: Removed verbose `[DashboardDebug]` console traces from view initialization.
- **Sensitive Credentials Scrubbing**: Upgraded regex pattern matching in `RegistryClient` to support space-separated authentication headers and base64 payloads.
- **Non-Gregorian Locales**: Standardized container naming timestamps to `CultureInfo.InvariantCulture` to prevent Eastern Arabic digit failures.
- **Resource Threshold Checks**: Added support for `ulong` settings validation in custom threshold checks.

