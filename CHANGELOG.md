# Changelog

All notable changes to the OneWare Container Extension are documented here.
This format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- Native-fallback output capture appended to a `StringBuilder` from the concurrent stdout/stderr handlers without the lock used on the container path, risking corrupted output; both appends and the final read are now synchronized.
- The container cancellation flag was written from the cancellation-callback thread and read on the main path without a memory barrier, so the read could observe a stale value; it is now accessed via `Volatile`.
- `RegistryClient` tag lookups returned the cached `List` by reference, letting a caller mutate shared cache state; callers now receive a copy.
- An exception thrown in the dashboard visibility observer could escape the Rx `OnNext` and tear down the dispatcher; the observer body, including the timer calls, is now fully guarded.
- The host bind allow-list now enforces both the raw and canonical form of each blocked path, so a failed canonicalization cannot weaken the gate.
- An empty or non-absolute tool working directory is now rejected with an actionable error instead of being resolved against the plugin's process directory, which previously produced a broken bind mount.
- A unit test hardcoded an iCloud Downloads path; because the builder pre-creates the mapped working directory on the host, every test run materialized that folder. The test now uses a temporary directory.

### Changed
- Re-enabled the `CA1001` and `CA1849` analyzers (and fixed the code they flagged: the two Docker managers now implement `IDisposable`; a cancellation registration is disposed asynchronously). Every remaining `NoWarn` entry now carries a written justification.
- Restructured CI into separate `format`, `build-test`, and `codeql` jobs; added a Linux/Windows/macOS build-and-test matrix; CodeQL is now gating; reconciled the toolchain image tag across the build script, Dockerfile, and workflow.
- Moved the integration fixtures and smoke runners from the repository root into `tests/integration`; the cross-platform evaluation, smoke runner, and E2E fixture lookup follow the new path.

### Removed
- The unused `neorv32` and `picorv32` submodules, the tracked `.vscode` directory, and stale broken benchmark result files.

### Added
- `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, and per-directory `README` files under `src`, `tests`, and `docker`.
- Unit tests for the registry SSRF guards, release-tag validation, image disk-usage aggregation, container-name validation, settings fallback, and the resource-threshold advisory branch.

## [1.0.6] - 2026-06-27

### Added
- **oss-cad-suite version picker**: *Build Local Image* now offers a dropdown of releases fetched from GitHub (newest first) alongside the repository-pinned build, instead of a fixed pinned/latest choice. Selecting a dated release fetches that tarball's GitHub-published SHA-256 and passes it to the build, so any version stays integrity-checked.

### Security
- **Registry loopback gate**: The cleartext-HTTP / loopback decision now matches the host exactly (after stripping the port) and via `IPAddress.IsLoopback`, instead of a prefix match that treated `127.0.0.1.evil.com` as loopback and contacted it over plaintext.
- **Registry SSRF gate**: Registry hostnames that resolve to loopback, RFC1918, link-local, CGNAT, or IPv6 ULA addresses are now rejected for non-public registries; the previously-discarded DNS resolution is now an enforced check.

### Privacy
- **Telemetry field consistency**: The telemetry `ErrorMessage` and `Image` fields are now scrubbed on the same path as the docker-run command, so `KEY=value` secrets and internal registry hostnames/IPs cannot leak through one field while being redacted in another.
- **Activity tags**: OpenTelemetry `Activity` tags record only the leaf tool name and honour the telemetry opt-out, instead of emitting the raw executable host path (which embeds the username) to any listener regardless of opt-out.
- **Telemetry at rest**: Telemetry and error-log files are created `0600` atomically (closing the umask-default window) and are EFS-encrypted on Windows even when created after the first verification.

### Fixed
- **Local image build on Apple Silicon**: The local build is forced to `linux/amd64` (matching the build script and GHDL support) and uses the per-version checksum, so both the pinned and selected builds no longer fail on arm64 hosts from an arch/checksum mismatch.
- **Pull-failure detection**: `PrePullImageAsync`, the on-demand image pull, and *Update All Images* now detect in-band registry pull failures (streamed over HTTP 200) and surface the real reason instead of reporting success.
- **Copy Docker Run**: The sample command emits valid `--memory`/`--memory-swap` values (no thousands separator that the daemon rejects).
- **Null-tool guard**: `RunContainerAsync` no longer throws a `NullReferenceException` when both `Executable` and `ToolName` are null.
- **Cancellation handle**: When the per-invocation sentinel process fails to start, the run is cancelled so killing the returned handle still tears down the container.
- **Telemetry lock lifecycle**: Workspace re-init no longer disposes the telemetry lock out from under an in-flight background writer.
- **Port mapping**: A single (non-range) `-p` mapping in Extra Flags is now validated for numeric, in-range endpoints like the range form.
- **Late-tool wiring**: The strategy-injection poller survives a transient per-tick fault instead of dying silently for the session, and notifies once if it stops.
- **GitHub 404**: A missing release endpoint is reported distinctly from a connectivity failure.

### Changed
- **Bypass Named Pipe Security Check** now defaults to on; the Windows-only check produces false positives on common non-default daemon setups (WSL2 relays, rootless/remote engines). Uncheck it on a hardened Windows host to re-enable the impersonation guard.
- **Open Desktop** launches the correct macOS bundle (`Docker.app`, not the display name "Docker Desktop") and reports a failure to the user instead of silently doing nothing.
- **No-silent-failures pass**: daemon-unreachable, telemetry export, active-image change, and *Update All Images* partial failures now always surface a success or error to the user.
- **Dashboard layout**: Environment Info labels align with the Active Configuration fields; KPI card spacing is symmetric; the redundant settings-path footer line was removed (the *Configure Settings…* button opens the same panel).

### Removed
- **Dead code**: `VerifyNamedPipeSafe`, `ValidateSocketPath`, `GetContainerStateAsync`, `CreateMoreText`, the duplicate `CreateSparkline` overload, and the unused `OSArchitecture` field.
- **Authorship hygiene**: Internal tracker tags (`(F##)`/`(ui-##)`/`(vt-##)`/`(int-##)`/`(Fix 107)`) and a brace-narration comment were stripped from the source.

### Documentation
- Added `examples/`: eight graded, container-verified FPGA example projects across a GateMate / iCE40 / Gowin board matrix with a coverage index; an `examples/.gitignore` prevents the externally-provided reference projects from being committed.
- Refreshed the project icon (accessibility metadata, consistent container shading) and documented the new *Bypass Named Pipe* default.

## [1.0.5] - 2026-06-26

### Security
- **Registry token-endpoint validation**: The generic Docker V2 registry client now requires the `WWW-Authenticate` `realm` to be an absolute HTTPS URL and forwards the stored Docker credential only to a token endpoint on the *same host* as the registry, closing an SSRF / credential-exfiltration vector via a malicious authentication challenge.
- **Supply-chain integrity**: The oss-cad-suite tarball download in the container image is now verified against a pinned SHA-256 (`sha256sum -c`); the build fails closed on a mismatch.
- **Telemetry redaction hardening**: The error-telemetry `Action` field is now scrubbed (it previously persisted verbatim host paths and usernames); inline URI basic-auth credentials (`scheme://user:pass@`) are stripped; the persisted equivalent docker-run command masks every `-e` value, so secrets under non-obvious key names cannot leak; and Windows home paths are collapsed even after backslash normalisation.
- **Telemetry at rest**: Exported telemetry is restricted to the owner (0600) on POSIX rather than inheriting a world-readable umask, and the cross-process telemetry mutex no longer embeds the raw OS username in a `Global\` kernel object (a non-reversible hash is used instead).

### Fixed
- **Dispose-time container cleanup**: `DockerExecutionStrategy.Dispose()` no longer no-ops its dangling-container cleanup; previously the static cleanup client was nulled by the CAS before the cleanup read it, leaving tracked containers running on plugin unload/reload. Cleanup now runs against the captured client explicitly.
- **Trailing tool output loss**: On a normal container exit the attach stream is now drained to EOF instead of being cancelled immediately, and the UTF-8 stream decoders are flushed at EOF, so the final lines of tool output are no longer dropped.
- **Environment variable truncation**: The 500-variable cap now truncates only the bulk `.env` contribution; host-provided `EnvironmentVariables` and the `HOME` fallback are always preserved and retain their precedence, with a warning emitted when truncation occurs.
- **Native-fallback telemetry retention**: `Unlimited` telemetry retention is no longer silently capped at 100 entries on the native-fallback execution path.
- **Dangling container on failure**: A container that never reaches a clean auto-removing exit (start/attach/wait failure) is now force-removed.
- **Connection-provider dispose race**: `PingAsync` and `GetSystemInfoAsync` no longer surface an `ObjectDisposedException` when the provider is disposed concurrently while acquiring or releasing the connection gate.
- **Unbounded output capture**: The aggregated output string returned to the host is now capped (the live stream is still forwarded to the tool console in full), so a runaway or hostile container cannot exhaust IDE memory.
- **Telemetry trim data loss**: The trim routine no longer truncates the live telemetry file in place as a fallback; on a failed atomic replace the file is left intact, so a disk-full during trim can no longer discard the entire history.
- **OOM telemetry**: An OOM-kill is now recorded even when no resource-stats sample was captured for a very short-lived container.
- **Registry challenge ReDoS**: The `WWW-Authenticate` challenge parser is now linear-time (`NonBacktracking`), removing a backtracking-blowup vector on an attacker-controlled registry header.
- **Port-range expansion**: A malformed `-p` range in Extra Flags (for example `0-2000000000`) is now validated against `[0, 65535]` and clamped, preventing a near-unbounded loop during container creation.
- **Dashboard memory slider**: The in-dashboard quick-settings memory slider also steps in 512 MB increments, matching the host setting and the validator floor.
- **Memory limit slider**: The Memory Limit slider now steps in 512 MB increments so every selectable value satisfies the validator's 512 MB floor (the previous 256 MB step allowed a 256 MB selection that the validator then rejected).
- **CPU limit slider**: The CPU Cores slider now steps in 0.5-core increments, matching the documented fractional-core support and the validator's 0.1-core floor.
- **Release-tag validation**: The oss-cad-suite release-tag check now rejects structurally-valid but impossible dates (for example `2024-13-45`).
- **StartWeakProcess**: The placeholder process is disposed if its `Start()` fails before a replacement is created.

### Changed
- **Dashboard theming**: The diagnostics dashboard now resolves its colors from OneWare's theme brushes (`ThemeForegroundBrush`, `ThemeAccentBrush`, `ErrorBrush`, `ThemeControlLowBrush`, `ThemeBorderLowBrush`, `NotificationCard*BackgroundBrush`) instead of WinUI/UWP keys the host does not define. The dashboard now renders correctly in both light and dark themes and repaints on theme change without a Docker daemon re-query.
- **Dashboard accessibility & UX**: Interactive controls now expose `AutomationProperties` names for screen readers; the advertised Ctrl+F search shortcut is functional (plus F5/Ctrl+R refresh, Ctrl+, settings, Escape clears filter); destructive actions (container/image removal) require confirmation; status is conveyed with non-color redundancy; emoji used as iconography were removed in favour of vector icons and text.
- **Dashboard internals**: The ~420-line settings dialog was extracted into a dedicated partial; duplicated dialog chrome, container-action, and sub-card builders were factored into shared helpers; the theme-variant and per-refresh timer subscriptions are now properly torn down on detach, and a blocking `File.Exists` was moved off the UI thread.
- **Tool-injection polling**: The post-startup tool-injection poll interval was relaxed from 1 s to 5 s, eliminating a per-second scan of every tool's settings for the plugin lifetime.
- **Authorship pass**: Removed all emoji from console, log, and benchmark output and from documentation, and replaced self-referential, tutorial-toned, and superlative comments (and the "Step N:" SDK-log numbering) with terse technical phrasing, in line with the project's authorship standard.

### Removed
- **Dead code**: Removed three orphaned commented-out blocks (~313 lines) in `DockerExecutionStrategy` — superseded synchronous named-pipe verification, Unix-socket liveness, and UID/GID resolution helpers.

### Documentation
- Corrected `architecture.md` (removed the nonexistent `DockerButtonView`/`DockerDiagnosticsWindow`, fixed the settings count and docking description) and documented the previously-undocumented `Allow Privileged Containers`, `Bypass Named Pipe Security Check`, and `Allow Native Fallback` settings; corrected the Memory/CPU slider facts in the configuration guide.

## [1.0.4] - 2026-06-20

### Added
- **Dynamic Native Execution Fallback**: Implemented automatic fallback to local host binaries on environment `PATH` when the Docker daemon connection is offline (controlled via the new `Allow Native Fallback` setting).
- **Log Level Telemetry Filtering**: Added configurable log levels (`Off`, `Errors Only`, `Verbose`) to filter execution telemetry records and reduce disk logging bloat.

### Refactored
- **Isolated Settings in Benchmark Harness**: Replaced global static dictionaries in `MockSettingsService` with isolated instance-level dictionaries to prevent cross-test state pollution.
- **Enriched XML Comments**: Added complete triple-slash XML documentation comments across the benchmark harness classes (`Program`, `MockSettingsService`, `TestCommandArgument`) to support zero-warning DocFX site compilation.

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
- **Offline Startup & Socket Probing**: Throws correct exceptions when the daemon is offline on load to activate the dashboard offline state. Probes candidates in reverse order and prefers matching existing user-space socket directories (e.g. OrbStack or Colima) over `/var/run/docker.sock` to reconnect after a closed daemon is restarted.

## [1.0.1] - 2026-06-12

### Added
- **Workflow & Dependabot Bumps**: Pinned GitHub action runners to Ubuntu 24.04 and bumped all action versions (checkout@v6, setup-dotnet@v5, cache@v5, codeql@v4, release-action@v1.21.0) to their latest secure major releases.
- **Dependency Upgrades**: Upgraded core .NET dependencies including `Microsoft.Extensions.DependencyInjection` (to `10.0.9`), `System.IO.Pipelines` (to `10.0.0`), and code quality analyzers (`SonarAnalyzer.CSharp` to `10.27`, `Meziantou.Analyzer` to `3.0`, `Microsoft.CodeAnalysis.NetAnalyzers` to `10.0`).
- **DocFX API Documentation**: Resolved DocFX relative project path mapping warnings, added API index landing page, and cleared all documentation compilation warnings.

## [1.0.0] - 2026-06-12

### Added

- **Hybrid Strategy Pattern** - Transparent switching between native and Docker-based FPGA tool execution.
- **Multi-Runtime Support** - Auto-detection of Docker, Podman, Colima, and OrbStack via socket probing.
- **Execution Telemetry** - JSON Lines logger with cross-process Mutex.
- **UID/GID Injection** - Prevents root-owned output files on Linux.
- **Sparkline Trends** - Visualized CPU and RAM usage trends directly in the `DockerDiagnosticsView` history panel.
- **Project Structure** - Consolidated documentation into `docs/` and standardized tests under `tests/`.
- **Telemetry Processing** - Background asynchronous task architecture to keep the UI responsive.
- **UI Layout Optimization** - State-caching fingerprint mechanism to eliminate UI layout thrashing when telemetry updates occur.
- **Docker Wrapper Resiliency** - Strengthened Docker service exception handling to prevent runtime panics from being masked.
- **Code Commentary Refactoring** - Cleansed code and unit tests of redundant or non-standard comment markings.
- **GHDL Elaboration Compatibility** - Stripping file paths to unit names for make (`-m`), elaborate (`-e`), and run (`-r`) options.
- **GHDL Work Library Argument Resolution** - Extracting directory basename for separate and equals work library arguments (e.g., `--work=ghdl`).
- **Yosys Compound Command Execution** - Recursively stripping outer quotes from compound command scripts passed to `-p` before mapping and tokenizing.
- **gmpack Write Permission Denials** - Read-write workspace binds for synthesis, simulation, and packing tools (e.g., `gmpack`, `icepack`) while maintaining read-only sandboxing for programmer tools.
- **Orphan Container Cleanup** - Kills dangling containers on IDE crash (Interlocked guard).
- **Deadlock Resolution** - Added cross-process locking in `ContainerTelemetry` to prevent deadlocks during concurrent logging.
- **Detachment Race Conditions** - Handle view-model detachment races in `DockerDiagnosticsView`.
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

