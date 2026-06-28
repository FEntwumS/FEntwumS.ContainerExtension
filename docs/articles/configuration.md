# Configuration Guide

All settings are registered under **Binary Management -> Container Engine** in OneWare Studio's settings panel.

## Settings Reference

### Container Runtime

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Container Runtime Path | File Path | *(auto-detect)* | Absolute path to `docker` or `podman` CLI. Leave empty for auto-detection |
| Custom Daemon Socket | Text (validated) | *(auto-detect)* | Override `DOCKER_HOST`. Accepts `unix://`, `tcp://`, `npipe://` |

### Image Management

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Default Toolchain Image | Text (validated) | `fentwums/oss-cad-suite:latest` | Default image for all tools — the project's full-flow image (build-only; produce it via Build Local Image) |
| Image Platform | ComboBox | *(auto)* | Force platform (e.g., `linux/amd64` on Apple Silicon) |
| Image Pull Policy | ComboBox | `if-not-present` | When to pull: `always`, `if-not-present`, `never` |

> [!TIP]
> On Apple Silicon Macs, set **Image Platform** to `linux/amd64` if your FPGA tool images don't have ARM builds.

### Resource Limits

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Memory Limit | Slider (512 MB step) | 0 (no limit) | Container memory cap. Auto-detects host max. When set, must be at least 512 MB |
| CPU Cores Limit | Slider (0.5 core step) | 0 (no limit) | Container CPU cap. Auto-detects host max. When set, must be at least 0.1 cores |
| Execution Timeout | Slider | 0 (no timeout) | Kill container after N minutes |

> [!WARNING]
> Setting resource limits above 75% of your system's capacity triggers a warning - this can starve the host OS.

### Container Behavior

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Auto-Remove Containers | CheckBox | On | Remove containers after execution |
| Network Mode | ComboBox | `bridge` | Docker network mode: `bridge`, `host`, `none` |
| Container Name Prefix | Text | `containerextension-` | Prefix for generated container names |
| Extra Container Labels | Text | *(empty)* | Space-separated `key=value` container labels for filtering |

### Security & Fallback

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Allow Privileged Containers | CheckBox | Off | Run containers with elevated (`--privileged`) capabilities. Keep off unless a tool requires direct hardware access |
| Bypass Named Pipe Security Check | CheckBox | On | Windows only. Skips the named-pipe server-process trust verification. On by default because the check produces false positives on common non-default daemon setups (WSL2 relays, rootless/remote engines); uncheck it on a hardened Windows host to re-enable the impersonation guard |
| Allow Native Fallback | CheckBox | Off | If the Docker daemon is unreachable, execute the tool natively from the host `PATH` instead of failing. **Note:** native execution bypasses container isolation |

### Logging & Telemetry

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Log Level | ComboBox | `Errors Only` | `Off`, `Errors Only`, `Info`, `Verbose`. Defaults to `Errors Only` (privacy-by-default); `Verbose` adds SDK messages and stack traces |
| Show Timestamps | CheckBox | On | Prepend `HH:mm:ss.fff` to SDK log messages |
| Telemetry Retention | ComboBox | `25` | Max entries: `None`, `25`, `50`, `100`, `250`, `500`, `1000`, `Unlimited`. Defaults to `25` (privacy-by-default); `None` opts out and **purges** existing history |
| Dashboard Refresh | ComboBox | `Manual` | Auto-refresh: `Manual`, `2s`, `5s`, `10s`, `15s`, `30s`, `60s`, `120s` |

## Per-Tool Image Overrides

Each tool registered in OneWare Studio gets its own image override setting, dynamically created as `ContainerImage_{toolName}`. This allows using different images for different tools:

```text
ContainerImage_ghdl          -> fentwums/oss-cad-suite:latest
ContainerImage_yosys         -> fentwums/oss-cad-suite:latest
ContainerImage_nextpnr-ecp5  -> hdlc/impl/prjtrellis   (key is ContainerImage_ + tool name, lowercased)
```

## Image Resolution Hierarchy

When the extension needs to determine which image to use, it checks (in order):

```text
1. ONEWARE_DOCKER_IMAGE env var        (highest - CI/CD override)
2. ContainerImage_{tool} per-tool      (settings UI)
3. ContainerExtension_DefaultImage     (global setting; defaults to fentwums/oss-cad-suite:latest)
4. DefaultToolImages[tool]             (built-in per-tool map)
5. hdlc/ghdl:yosys                     (hardcoded fallback)
```

## Environment Variables

The extension automatically loads environment variables from a `.env` file in your project's working directory. This is useful for CI/CD integration:

```env
# .env file in your project root
ONEWARE_DOCKER_IMAGE=ghcr.io/custom/image:v2.0
MY_LICENSE_KEY=abc123
```

These variables are injected into the container alongside the tool command.
