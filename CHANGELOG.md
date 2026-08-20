# Changelog

All notable changes to the OneWare Container Extension are documented here.
This format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.14] - 2026-08-20

An internal maintainability pass over 1.0.13. The monolithic `DockerExecutionStrategy` is decomposed
into focused, independently-testable collaborators. Container execution, telemetry, and security-gate
behavior are unchanged: method bodies were preserved and only their dependencies re-targeted.

### Changed

- `DockerExecutionStrategy` is refactored from a single ~3,300-line class into a coordinator that composes dedicated collaborators, each owning one concern: bind-mount validation and canonicalization (`BindValidator`), `docker run` command rendering with environment-value masking (`DockerRunCommandFormatter`), daemon endpoint verification — named-pipe trust and Unix socket probing (`DaemonEndpointValidator`), dangling-container reaping on exit/Ctrl-C/dispose (`ContainerReaper`), daemon bootstrap and API-version negotiation (`DockerConnectionFactory`), the container run loop (`ContainerRunner`), host-native fallback execution (`NativeFallbackExecutor`), and level-gated tool-console logging/output handling (`DockerToolConsole`). The public surface, container-execution semantics, and all security gates are unchanged.

### Tests

- Per-collaborator unit tests accompany the extracted `BindValidator`, `DockerRunCommandFormatter`, and `DaemonEndpointValidator`, moved from reflection-based access to direct calls; `DockerRunCommandFormatter` gains explicit coverage of environment-value masking. A new daemon-backed smoke test runs a real container end-to-end through `ExecuteAsync` using a small cached image and no toolchain fixtures, providing a fast regression anchor for the execution engine.

## [1.0.13] - 2026-07-15

A follow-up hardening and maintenance pass over 1.0.12: the remaining low-severity items from the
post-release audit, dependency pin advances, and dashboard and telemetry polish. The container
execution and evaluation-path semantics are unchanged.

### Security

- The symlink-following path canonicalizer that backs the mount-blocking, telemetry-directory, and host-to-container mount-mapping containment gates existed as three byte-identical private copies; it is consolidated into a single authoritative helper so a future change to the containment logic cannot leave one gate behind.
- The asynchronous error-channel writer now re-checks the telemetry opt-out under the write lock, closing a window in which an error entry queued before opt-out could be written into a log the purge had already truncated.
- The `KEY=value` secret scrub now fails closed — the affected field is redacted — when its match times out rather than returning the unscrubbed input; the pattern carries a lookbehind and a backreference and so can time out on a pathological input.
- The GitHub release client is hardened to match the registry client: automatic redirects and proxy use are disabled, every JSON response is read through an 8 MB cap, and the negotiated protocol is floored at TLS 1.2/1.3.

### Fixed

- The OOM-killed flag was overwritten to false by the late resource-statistics merge (the sampler never observes the kill), so a container terminated by the OOM killer was reported and logged as a clean exit; the post-inspect correction is now preserved.
- The dashboard's Active Configuration panel omitted the privileged-mode toggle, hiding the only setting that permits `--privileged` and leaving its warning branch unreachable; it is now listed.
- A floating SDK against the pinned SDK-injected linker package caused the locked restore to fail in CI; the SDK is now pinned exactly so locked restore cannot drift.

### Changed

- The pinned `oss-cad-suite` toolchain image is advanced to the 2026-07-15 release (Dockerfile build arguments, the fail-closed SHA-256, the CI image tag, the build-dialog label, and the submodule pointer), and the in-app build-local-image dialog recommends it.
- The OneWare host-shared `OneWare.Essentials` pin is advanced from 1.0.19 to 1.0.22 for the current OneWare Studio 1.0.23 host — the newest published version that does not exceed the host, so the plugin loader accepts it. It still targets Avalonia 11.3.17, so no host-shared transitive dependency changes.
- The build-time analyzer SonarAnalyzer.CSharp is updated from 10.28 to 10.29.
- The Extra Container Labels help text is corrected to the space-separated format the parser accepts (it had advertised a comma-separated form that mis-parses); the over-75% resource-allocation advisory is now surfaced on save with a confirm step rather than computed and discarded; the telemetry export and clear file I/O is moved off the UI thread; and the documented dashboard section name is aligned with the UI ("Execution History").

### Tests

- A deterministic regression test covers the error-channel opt-out re-check, and the quality-verification tests join the telemetry serialization collection to remove a latent parallel-run flake.

## [1.0.12] - 2026-07-09

A post-release security and correctness hardening pass over the 1.0.11 baseline. The changes
below are confined to security, error, and edge-case paths; the container execution, telemetry,
and CLI-parity semantics on the measured evaluation path are unchanged.

### Security

- A registry-supplied image tag was interpolated verbatim into a command typed at OneWare's interactive terminal (a real shell), so a hostile or compromised registry could inject shell metacharacters through the tag list of any non-default image. The OCI/Docker tag grammar is now enforced at the source across all three registry fetch paths, and the composed image reference is re-validated at both terminal pull sinks as defence in depth.
- The GitHub asset digest was validated only for length before being interpolated unquoted into the build command at the interactive terminal; the SHA-256 hex charset is now enforced at the source and the build-argument values are quoted. The owner/uid probes (`stat`, `id`) and the system-open helpers (`open`, `xdg-open`) ran as bare names resolved against `$PATH` (Sonar S4036), so a writable directory earlier on `PATH` could shadow them; each now resolves to a trusted absolute path (`/usr/bin` then `/bin`), degrading cleanly on non-FHS layouts rather than falling back to `PATH`.
- Two SSRF-gate bypasses on the registry client are closed. The `SocketsHttpHandler` installed a connect-time address gate but did not set `UseProxy=false`, so a configured proxy would `CONNECT`-tunnel the request and the gate would vet only the proxy address; proxy use is now disabled so the gate always sees the real destination. The disallowed-address test collapsed only IPv4-mapped IPv6, so NAT64 (`64:ff9b::/96`) and deprecated IPv4-compatible (`::a.b.c.d`) forms embedding a blocked IPv4 (for example `169.254.169.254`) slipped through unblocked; the embedded IPv4 is now extracted and re-tested against every range.
- Windows named-pipe daemon trust is hardened. The pipe server is classified as untrusted, current-user, or elevated rather than a bare boolean: SYSTEM and Administrators are trusted outright, while a current-user server is trusted only if its process name matches a known runtime, so an arbitrarily-named same-user squatter is refused. The server identity is verified on the same handle that carries traffic (closing a verify-versus-use TOCTOU), and a drifted impersonation-cap reflection target now emits telemetry instead of silently dialling the pipe without the cap. Rootless runtimes and the trust-bypass setting keep legitimate setups working. Windows-only path: verified by reasoning and compilation — a live-token smoke test is recommended before relying on the gate.
- `TrackError` applied only the path/host scrub to exception messages, context, and stack traces, so a secret embedded in an exception (for example `password=...`) could reach the error log in the clear; those free-text fields are now routed through the secret scrubber first, matching `LogExecution`. The registry secret-scrub gated redaction on the literal `token=` while its pattern also matched a whitespace separator, so whitespace-separated tokens bypassed scrubbing; the gate is now the bare word `token`. Registry tag and token responses were fully deserialized before the 20-tag cap applied, letting a hostile registry force unbounded allocation; every response body is now buffered through an 8 MB-capped reader before parsing.

### Fixed

- The telemetry opt-out purge is now race-safe: truncation reports whether it actually completed and the "purged" latch is set only on that confirmation (a failed purge retries on a later observation rather than leaving prior data un-erased), `LogExecution` re-checks opt-out under the write lock, and the error-log trim runs under the same mutex and write lock as the execution log so its `File.Replace` cannot resurrect purged lines nor race a cross-process append.
- When the daemon is offline and native fallback engages, the `finally` block still logged a container entry with the never-updated exit code -1 and, under retention `None`, wiped the real host-native entry the fallback had just written. The fallback is now flagged and the container telemetry and summary are skipped on that path.
- Container teardown races are closed. `DockerContainerManager.Dispose` disposed its semaphores unconditionally, faulting in-flight operations (and a second `Dispose`) with `ObjectDisposedException`; `Dispose` is now idempotent and reference-count aware, and every release path tolerates a racing teardown. A container created in the window between `CreateContainerAsync` returning and its ID being tracked was invisible to the exit reaper and could orphan; its unique name is now pre-registered so stop/remove can reach it before the ID is known. The `/system/df` disk probe's timeout `OperationCanceledException` escaped its fallback catch instead of degrading; it is now caught unless the caller's own token is signalled.
- A recycled container-row action panel carried a stale disabled state onto a live row (Avalonia propagates a disabled parent to its children, leaving the buttons inert); the panel is reset to enabled when it is reacquired. The "unused images" KPI counted images with `Containers == 0`, but the list endpoint leaves that field at -1 so the count was always zero; untagged and dangling images are now counted via a shared helper, matching the reclaimable-size metric.
- A malformed telemetry tail line on the hot dashboard-stats poll (~250 ms) persisted an error entry on every poll; it is now traced with `Debug.WriteLine` like the sibling read path.
- Interleaved stdout/stderr log frames were decoded with one shared UTF-8 decoder, corrupting a multibyte character split across a frame boundary; per-stream decoders are now keyed on the frame target. A caller cancellation during log retrieval was swallowed into an error string plus spurious telemetry; it now propagates. The live-log stream accumulated carriage-return-only output (progress bars) without bound because it flushed only on a newline; the pending buffer is now capped. The free-disk check measured the host volume, but on macOS and Windows the daemon runs in a VM whose image store is unrelated, so it spuriously aborted pulls; it is now skipped off Linux, leaving genuine out-of-space errors to the daemon. A status-carrying `HttpRequestException` (a GitHub 4xx/5xx) was reported as "network connection failed"; it is now reported as a server-side error.
- `ExecuteAsync` validated the executable and arguments before its execution try block and threw `ArgumentException` on rejection, escaping the documented `(success, output)` contract; it now returns `(false, message)`. The CPU-threshold validation advertised a maximum of `max(32, host cores)` while the capacity check enforced the host core count, so the stated bound was wrong and the 32-core ceiling was dead; the host core count is now reported as the bound.
- A theme toggle repainted from a cache holding a null system-info snapshot, falsely reporting "system information temporarily unavailable" while the daemon was online; the last non-null snapshot is now cached and reused. The Save Logs button was re-enabled only on the cancel and error paths, so a successful save left it permanently disabled. First-appearance focus used a flag reset by the attach cycle, so every dock/undock re-stole focus to the search box; the flag is now set once and never reset on detach. A visibility subscription created after two awaits leaked the view when a detach raced the initial load; it is now skipped when the control has since detached, and any prior subscription is disposed first. The exit reaper stopped tracked containers sequentially with a 2 s budget each, blocking the caller (`Dispose` may run on the UI thread) for up to 2 s × N; they are now stopped concurrently under a single shared 2 s budget.

### Changed

- The file-wide `VSTHRD002`/`VSTHRD105`/`VSTHRD110` suppression over the 3300-line `DockerExecutionStrategy` — which had hidden the synchronous-over-asynchronous reaper defect fixed above — is narrowed to a single scoped disable at the one intentional teardown block, so the whole-file blind spot is gone. The remaining legitimate file-wide pragmas (`MA0004`/`MA0006`/`S108` in the Avalonia view files, `MA0051` in the argument/mount builders) now carry why-comments. The centralized `NoWarn` set is unchanged.
- Dependency maintenance: the build-time analyzers Meziantou.Analyzer (3.0.117 to 3.0.122) and SonarAnalyzer.CSharp (10.27 to 10.28) are updated; the SHA-pinned CI actions (`setup-qemu-action`, `setup-buildx-action`, `build-push-action`, `github/codeql-action`) are bumped to current releases; and the `oss-cad-suite` image's Ubuntu base is refreshed to the current digest. The pinned toolchain release and every OneWare host-shared runtime dependency (Avalonia, OneWare.Essentials 1.0.19, System.IO.Pipelines 10.0.0) are unchanged.

### Tests

- A fixture-free integration harness drives `DockerExecutionStrategy` through the real container lifecycle against a trivial public image (create, start, attach and stream, wait, exit code, auto-remove), covering the teardown, `(success, output)` contract, log streaming with UTF-8 decode, and one-entry-per-run telemetry paths that the unit tests only mock. It is gated behind `[FactIfNoCI]` (it needs a daemon and Docker Hub) and was verified locally against OrbStack with no leaked containers.

## [1.0.11] - 2026-07-02

### Security

- The workspace-containment scan that pre-creates a tool's output directories on the host now canonicalizes each candidate through the same symlink-resolving routine used for bind mounts before the boundary test, and gates `Directory.CreateDirectory` on the canonical path. Previously an intermediate symlink in an untrusted project (for example a checked-in `out` link pointing outside the workspace) was left unresolved by the pre-creation scan, so directory creation could follow it and create a directory outside the mounted workspace. The impact was bounded to empty-directory creation, but it broke the workspace-containment invariant.

### Fixed

- The marketplace manifest advertised five versions (1.0.4–1.0.8) that were never published as releases, so selecting one in the OneWare Package Manager produced a hard, silent download failure. Only versions with a published artifact are now advertised.
- The dashboard search filter mutated the cached telemetry snapshot in place, so after a filter had been applied the history view, trend sparklines, and resource aggregates continued to show only the filtered subset until the next reload. The cached snapshot is now returned as a copy.
- Native-fallback execution accumulated tool output without the 32 MB cap enforced on the container path, so a host tool emitting an unbounded stream could exhaust IDE memory; both native-path output handlers now honour the same cap.
- On the Windows named-pipe path the daemon-side auto-remove override added in 1.0.10 was also recorded as the shutdown reaper's removal decision, so a named-pipe container still tracked at teardown was stopped but not removed. The reaper now uses the user's configured auto-remove intent, restoring parity with the socket path.

### Changed

- The offline image-cache helper (`docker/pull_all_images.sh`) derives the Ubuntu base digest from the Dockerfile `FROM` line rather than a hard-coded copy, so a base-image bump can no longer leave the pre-pull cache pointing at a stale digest.

### Docs

- `CITATION.cff` records the current release version and date.

## [1.0.10] - 2026-07-02

### Fixed

- Containerized execution over the Windows named pipe (`npipe://`, the Docker Desktop default endpoint) failed with `NotSupportedException: "Cannot shutdown write on this transport"` before the container could run. Docker.DotNet's `AttachContainerAsync` requires a write-closable transport to half-close the hijacked stream, which the named-pipe stream does not provide, so every tool invocation threw. When the daemon endpoint is a named pipe the strategy now streams the container's stdout/stderr through the non-hijacked logs-follow endpoint (`GetContainerLogsAsync` with `Follow`), which carries the same multiplexed framing without the write-close requirement; auto-remove is disabled on that path so a fast-exiting container is not reaped before its output drains, and the container is force-removed explicitly afterwards. Socket transports (Unix domain socket, TCP) are unchanged. Verified on Windows 11 with Docker Desktop over the named pipe across the full FPGA toolchain workload set.

## [1.0.9] - 2026-07-01

### Fixed

- The registry client enforces the SSRF address gate at connection time: it resolves the target host and dials the vetted IP directly, refusing any loopback, private, CGNAT, or link-local address. This closes a bearer-token realm-follow vector — a hostile registry naming an internal host in its `WWW-Authenticate` challenge — and the DNS-rebinding window between the pre-flight check and the socket connect. Explicit loopback registries remain reachable.

### Changed

- The release workflow derives the release tag, artifact filename, and attestation subject from the published Git tag rather than the project `<Version>`, so the SBOM and build-provenance attestation always attach to the correct release. The project `<Version>` is bumped to 1.0.9.
- Corrected the 1.0.7 entry's description of the container-scan gate to match the workflow: Trivy gates on fixable CRITICAL (fixable HIGH is surfaced but not gated), and the toolchain checksum is pinned as a Dockerfile `ARG` default rather than passed as a workflow build argument.

### Tests

- Telemetry error-log tests wait for the asynchronous error channel to drain via a bounded poll instead of a fixed sleep, removing a latent flake on loaded CI runners.

## [1.0.8] - 2026-06-30

### Changed

- Bumped the SHA-pinned CI actions to current releases (`actions/checkout` v7, `actions/cache` v6, `docker/setup-qemu-action` v4, `docker/setup-buildx-action` v4, `docker/build-push-action` v7, `aquasecurity/trivy-action`), clearing the Node 20 runner deprecation. Build infrastructure only; the extension artifact is unchanged from 1.0.7.

## [1.0.7] - 2026-06-30

### Fixed

- Native-fallback output capture appended to a `StringBuilder` from the concurrent stdout/stderr handlers without the lock used on the container path, risking corrupted output; both appends and the final read are now synchronized.
- The container cancellation flag was written from the cancellation-callback thread and read on the main path without a memory barrier, so the read could observe a stale value; it is now accessed via `Volatile`.
- `RegistryClient` tag lookups returned the cached `List` by reference, letting a caller mutate shared cache state; callers now receive a copy.
- An exception thrown in the dashboard visibility observer could escape the Rx `OnNext` and tear down the dispatcher; the observer body, including the timer calls, is now fully guarded.
- The host bind allow-list now enforces both the raw and canonical form of each blocked path, so a failed canonicalization cannot weaken the gate.
- An empty or non-absolute tool working directory is now rejected with an actionable error instead of being resolved against the plugin's process directory, which previously produced a broken bind mount.
- A unit test hardcoded an iCloud Downloads path; because the builder pre-creates the mapped working directory on the host, every test run materialized that folder. The test now uses a temporary directory.
- The diagnostics dashboard falsely reported "Daemon offline" when the runtime's `/info` endpoint failed (observed on OrbStack) even though containerized execution and the liveness ping succeeded; a `null` `/info` is now distinguished from an unreachable daemon and rendered as a degraded-online state. A follow-on case where the Containers section stayed stuck on the offline placeholder after the daemon returned with zero containers (an empty-list fingerprint colliding with the offline reset value) is also fixed.
- Workspace path containment now collapses `.`/`..` in container-space paths and re-checks containment, closing a `/workspace/../etc/passwd` traversal that previously passed through to the container argv unmodified.
- Rootless Podman/Docker: every tool-output write failed with `EACCES` because `--user` pinned the host uid into the container; `--user` is now omitted when `/info` reports a rootless runtime (rootful runtimes are unchanged).
- The SSRF allow-list now normalizes IPv4-mapped IPv6 and rejects the unspecified/wildcard address; telemetry scrubbing now redacts compressed and link-local IPv6 addresses.
- The fallback socket-selection loops no longer return a socket whose owner the live-probe deliberately skipped; the attach stream is disposed only after the read task drains; and a dispose race in the execution prologue no longer escapes unhandled.
- "Open Desktop" opened (or failed to find) Docker Desktop under OrbStack/Colima/Podman because the runtime was labeled by the socket path rather than its resolved symlink target; it now identifies the real runtime. "Open Log" opened the `.jsonl` telemetry log in the default text editor instead of silently failing.
- Commands injected into the integrated terminal (prune, hello-world, engine info, image pulls, local build) are prefixed with a line-reset so leftover input on the prompt can no longer corrupt them (e.g. `vdocker image prune`).
- Status-banner messages no longer vanish almost immediately after rapid actions: dismissal is keyed to a monotonic token rather than the message text, errors linger longer, and a manual close cancels pending timers. Running containers' uptime/CPU/RAM cells now refresh in place between structural changes.
- "Reset to Defaults" in the dashboard settings restored Verbose logging and 100-entry retention; it now restores the privacy-by-default values (Errors Only / 25).

### Changed

- Re-enabled the `CA1001` and `CA1849` analyzers (and fixed the code they flagged: the two Docker managers now implement `IDisposable`; a cancellation registration is disposed asynchronously). Every remaining `NoWarn` entry now carries a written justification.
- Restructured CI into separate `format`, `build-test`, and `codeql` jobs; added a Linux/Windows/macOS build-and-test matrix; CodeQL is now gating; reconciled the toolchain image tag across the build script, Dockerfile, and workflow.
- Moved the integration fixtures and smoke runners from the repository root into `tests/integration`; the cross-platform evaluation, smoke runner, and E2E fixture lookup follow the new path.
- All tools now default to the project's full-flow `fentwums/oss-cad-suite` image — a single image covering synthesis, place-and-route, packing, and simulation across the supported device families — replacing the per-tool `hdlc/*` defaults, several of which referenced a non-existent `hdlc/impl` image. The image is build-only (not published to a registry); Pull, Check-for-Updates, and the registry-tag display now state this and direct the user to Build Local Image.
- The dashboard copy actions place the exact, runnable `docker run` command on the clipboard for the current session (real paths and environment values), while the on-disk telemetry log remains scrubbed.
- Build Local Image now surfaces the pinned release version (read live from the Dockerfile), widened the release picker from ~20 to ~100 entries, and added a "Build & Set Default" action that promotes the freshly-built image to the default image only after the build exits successfully.
- Re-enabled the AOT/single-file analyzers (removed the blanket suppressions, with a justified per-site suppression where the plugin is loaded as a loose assembly file); the Trivy image scan now fails the docker-build workflow on fixable CRITICAL findings (fixable HIGH is surfaced in the scan output but not gated) and emits its report as JSON, and the toolchain tarball checksum is pinned once as a Dockerfile `ARG` default rather than duplicated as a workflow build argument.
- "Update All Images" now reports which images failed, not just the count; the dashboard search-box shortcut hint is platform-correct (`Cmd+F` on macOS).

### Removed

- The unused `neorv32` and `picorv32` submodules, the tracked `.vscode` directory, and stale broken benchmark result files.
- Extracted the evaluation apparatus to the thesis repository: the `ContainerBenchmarkHarness` console driver, the Python benchmarking suite (`benchmark.py`, `run_evaluation.py`, `aggregate.py`) with its tracked results, the shell integration tests and HDL fixtures, and the eight FPGA example projects. The plugin, its unit tests (including the gated container E2E suite), the Docker build inputs, and the DocFX site remain.
- Relocated the `evaluation.md` and `amd64-native-baseline-protocol.md` articles to the thesis, as evaluation methodology is research output rather than extension usage.

### Added

- `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, and per-directory `README` files under `src`, `tests`, and `docker`.
- Unit tests for the registry SSRF guards, release-tag validation, image disk-usage aggregation, container-name validation, settings fallback, and the resource-threshold advisory branch.
- Unit tests for container-space path-traversal containment and compressed/link-local IPv6 telemetry scrubbing.

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

- Added `examples/`: eight graded, container-verified GateMate FPGA example projects (VHDL and Verilog) with a coverage index; an `examples/.gitignore` prevents the externally-provided reference projects from being committed.
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
