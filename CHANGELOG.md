# Changelog

All notable changes to the OneWare Container Extension are documented here.
This format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.3] - 2026-06-12

### Added
- **Sanitized Environment Values**: Sanitize env values in `.env` files to prevent nested command injection (strips backticks and `$()` command substitutions).
- **fpgaproj-based GHDL Library Mapping**: Custom GHDL library mappings from `.fpgaproj` configuration files are now parsed and respected in `--work` arguments.
- **Deduplicated Disk Usage**: Integrated a raw socket HTTP client fallback querying the Docker daemon's `/system/df` endpoint to report accurate copy-on-write disk usage instead of raw summation.
- **Fractional CPU Cores Support**: CPU limits settings slider now supports snapping and saving in `0.5` fractional core steps (e.g. 1.5 cores).

### Fixed
- **Settings Dialog UI & DPI Polish**: Redesigned settings dialog layout with a dynamic resizable Grid (header, scroll area, error label, footer) and auto-size margins/heights to prevent clipping on High-DPI screens. Displays dialog modally via `ShowDialog(parent)`.
- **Auto-Hiding Validation Errors**: Hooked text-changed and value-changed events recursively on input controls to hide stale validation labels immediately as the user edits fields.
- **Parallel Image Pull Optimization**: Refactored the sequential update loop to fetch image updates in parallel (bounded by `PullSemaphore` limit 2).
- **Win32 Named Pipe Trust Validation**: Hardened Windows Named Pipe process trust validation to fail-closed on Win32 API errors.
- **Exit Teardown Robustness**: Replaced ThreadPool-based asynchronous calls in the process exit handler with a fast synchronous clean-up task to prevent hangs and orphan containers during IDE exit.

## [1.0.2] - 2026-06-12

### Added
- **Temporary Status Banner Integration**: Integrated start, stop, restart, remove, and logs loading states for containers directly into the dashboard temporary status banner.
- **Docker Daemon Connection Notifications**: Tracks and displays notification banners when the daemon goes offline or comes back online, with change-detection guardrails to prevent spam.

### Fixed
- **Container Extension Correctness (10 Bugs)**: Resolved infinite loop at EOF in telemetry, thread-safety deadlocks in file owner queries, indefinite caching of faulted connection state, regex credentials leakage scrubbing, restored ViewModels DI service location fallback, and unobserved log-streaming exceptions.
- **Offline Startup & Socket Probing**: Throws correct exceptions when the daemon is offline on load to activate the dashboard offline state. Probes candidates in reverse order and prefers matching existing user-space socket directories (e.g. OrbStack or Colima) over `/var/run/docker.sock` to enable seamless reconnection after starting a closed daemon.

## [1.0.1] - 2026-06-12

### Added
- **Workflow & Dependabot Bumps**: Upgraded GitHub action runners to Ubuntu 26.04 and bumped all action versions (checkout@v6, setup-dotnet@v5, cache@v5, codeql@v4, release-action@v1.21.0) to their latest secure major releases.
- **Dependency Upgrades**: Upgraded core .NET dependencies including `Microsoft.Extensions.DependencyInjection` (to `10.0.9`), `System.IO.Pipelines` (to `10.0.9`), and code quality analyzers (`SonarAnalyzer.CSharp` to `10.27`, `Meziantou.Analyzer` to `3.0`, `Microsoft.CodeAnalysis.NetAnalyzers` to `10.0`).
- **DocFX API Documentation**: Resolved DocFX relative project path mapping warnings, added API index landing page, and cleared all documentation compilation warnings.

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

