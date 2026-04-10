# Changelog

All notable changes to the OneWare Container Extension are documented here.
This format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] — 2026-04-10

### Added

- **Hybrid Strategy Pattern** — Transparent switching between native and Docker-based FPGA tool execution
- **Multi-Runtime Support** — Auto-detection of Docker, Podman, Colima, and OrbStack via socket probing
- **Automatic Image Pull** — Background pre-pull on startup with configurable pull policy (always / if-not-present / never)
- **Docker Dashboard** — Dockable panel with live daemon status, containers, images, disk usage, execution history, log export, digest pinning, copy docker run, and settings-at-a-glance
- **Standalone Dashboard Window** — Popup alternative with Mica transparency and keyboard shortcuts
- **Execution Telemetry** — JSON Lines logger with cross-process Mutex, stats computation, export, batch trimming, and configurable retention (None → Unlimited)
- **16 Configurable Settings** — Default image, platform override, memory/CPU limits, timeout, network mode, pull policy, log level, container name prefix, telemetry retention, dashboard refresh, runtime path, custom daemon socket
- **Image Resolution Hierarchy** — 4-level fallback: `ONEWARE_DOCKER_IMAGE` env → per-tool → global setting → hardcoded
- **UID/GID Injection** — Prevents root-owned output files on Linux
- **`.env` File Support** — Inject environment variables from the working directory into containers
- **Orphan Container Cleanup** — Kills dangling containers on IDE crash (Interlocked guard)
- **Copy Docker Run** — Generates equivalent CLI command for debugging and reproducibility
- **Completion Notification** — Console notification for long-running jobs (>30s)
- **Health Check** — Verifies daemon connectivity on plugin load with automatic retry (10×3s)
- **92 Unit Tests** — Validators, telemetry, stream processing, env file parsing, resource thresholds, edge cases
- **Benchmarking Suite** — Python harness with CLI + .NET Docker.DotNet backends, statistical analysis (mean, median, p95, CV), comparison mode, dry-run, outlier detection
- **CI/CD Workflows** — GitHub Actions for build/test on PR and release publishing
- **DocFX Documentation** — API reference with Getting Started, Configuration, Telemetry, and Architecture articles
