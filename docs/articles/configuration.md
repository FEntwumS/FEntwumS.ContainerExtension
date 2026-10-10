# Configuration Guide

The extension's settings are under **Binary Management > Container Engine** in OneWare Studio's settings (**Extras > Settings**). The settings of each tool, its execution strategy and its container image, are under **Binary Management > Execution Strategy**.

## Settings Reference

### Container Runtime

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Container Runtime Path | File Path | *(empty)* | Absolute path to the `docker` or `podman` CLI that the dashboard's terminal commands and the copied `docker run` commands use; empty means `docker` from the `PATH`. Runs do not use it: they reach the daemon through its socket or pipe |
| Custom Daemon Socket | Text (validated) | *(auto-detect)* | Override `DOCKER_HOST`. Accepts `unix://`, `tcp://`, `npipe://` |

### Image Management

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Default Toolchain Image | Text (validated) | `fentwums/oss-cad-suite:latest` | Default image for all tools — the project's full-flow image (build-only; produce it via Build Local Image) |
| Image Platform | ComboBox | *(auto)* | Platform to pull images for (e.g., `linux/amd64` on Apple Silicon). It applies to pulls only; a container is created from the image as it was pulled |
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
| Auto-Remove Containers | CheckBox | On | Remove containers after execution, and at the next start the stopped ones a crashed session left behind |
| Network Mode | ComboBox | `bridge` | Docker network mode: `bridge`, `host`, `none` |
| Container Name Prefix | Text | `containerextension-` | Prefix for generated container names |
| Extra Container Labels | Text | *(empty)* | Space-separated `key=value` container labels for filtering |

### Security & Fallback

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Bypass Named Pipe Security Check | CheckBox | On | Windows only. Skips the named-pipe server-process trust verification. On by default because the check produces false positives on common non-default daemon setups (WSL2 relays, rootless/remote engines); uncheck it on a hardened Windows host to re-enable the impersonation guard |
| Allow Native Fallback | CheckBox | Off | If the Docker daemon is unreachable, execute the tool natively from the host `PATH` instead of failing. **Note:** native execution bypasses container isolation |

### Logging & Telemetry

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Log Level | ComboBox | `Errors Only` | `Off`, `Errors Only`, `Info`, `Verbose`. Defaults to `Errors Only` (privacy-by-default); `Verbose` adds SDK messages and stack traces. It also decides which runs the telemetry records: see [Telemetry](telemetry.md#execution-telemetry) |
| Show Timestamps | CheckBox | On | Prepend `HH:mm:ss.fff` to SDK log messages |
| Telemetry Retention | ComboBox | `25` | Max entries: `None`, `25`, `50`, `100`, `250`, `500`, `1000`, `Unlimited`. Defaults to `25` (privacy-by-default); `None` opts out and **purges** existing history |
| Dashboard Refresh | ComboBox | `Manual` | Auto-refresh: `Manual`, `2s`, `5s`, `10s`, `15s`, `30s`, `60s`, `120s` |

## Per-Tool Image Overrides

Each tool registered in OneWare Studio gets its own image override setting, dynamically created as `ContainerImage_{toolName}` and shown as **Container Image for {tool}** under **Binary Management > Execution Strategy**. This allows using different images for different tools:

```text
ContainerImage_ghdl          -> fentwums/oss-cad-suite:latest
ContainerImage_yosys         -> fentwums/oss-cad-suite:latest
ContainerImage_nextpnr-ecp5  -> hdlc/impl/prjtrellis   (key is ContainerImage_ + tool name, lowercased)
```

An empty field falls back to the image its placeholder names: the `docker.image` the tool's plugin declares, or else the Default Toolchain Image.

## Image Resolution Hierarchy

When the extension needs to determine which image to use, it checks (in order):

```text
1. ONEWARE_DOCKER_IMAGE env var        (highest - CI/CD override)
2. docker.image of the call            (per-run override from the caller)
3. ContainerImage_{tool} per-tool      (settings UI)
4. docker.image of the tool            (declared by the tool's plugin)
5. ContainerExtension_DefaultImage     (global setting; defaults to fentwums/oss-cad-suite:latest)
6. hdlc/ghdl:yosys                     (hardcoded fallback when no default image is set)
```

Steps 2 and 4 come from OneWare's tool engine. A plugin declares `docker.image` for its tool in the strategy configuration of its `ToolContext`, and OneWare applies its own stored override of that value on top. A caller can override it for a single run with `WithStrategyConfiguration("docker.image", ...)`, which reaches the extension as `ToolCommand.StrategyConfigurationOverrides`. Both values pass the same format check as the image settings, and a value that fails it fails the run with a message naming its source. At the `Info` log level the run log shows the resolved image and where it came from.

## Workspace Access

Each run mounts the project directory at `/workspace` in the container, writable for a tool that writes there and read-only for every other tool. The extension decides by two names, the file name of the tool's executable and the tool's name in OneWare, never by the folders the executable lies in. Both are compared in lower case and without `.exe`, and when one names a read-only tool and the other a writing one, read-only wins. So the model that Verilator built, which OneWare runs from `build/sim/verilator/<bench>/simulation` as the tool `verilator`, may write its waveform.

- Read-only, whatever their flags mean: the programmers `openFPGALoader`, `iceprog`, `black-iceprog`, `iceprogduino`, `icesprog`, `openocd`, `dfu-util`, `ujprog` and `fujprog`, and `gtkwave`.
- Writable, whatever their flags mean: every tool whose name starts with `yosys`, `nextpnr-`, `sby`, `mcy`, `ghdl`, `iverilog` or `verilator`, every tool whose name ends in `pack`, the unpackers included, and `eqy`, `scy`, `nvc`, `vvp`, `prjoxide`, `icepll`, `ecppll`, `gowin_pll`, `ecpbram`, `icetime`, `vcd2fst`, `vcd2lxt`, `vcd2lxt2`, `vcd2vzt`, `bin2hex` and `hex2bin`, which can write through a flag or a file argument of their own.
- Any other tool: writable only when one of its arguments is an output flag the extension recognizes (`-o`, `-w`, `-a`, `-e`, `-r`, `--output`, `--write`) or contains a redirection (`>`).

A tool that needs another access sets `docker.workspace` to `rw` or `ro`, in upper or lower case, in the same places as `docker.image`: a caller for a single run with `WithStrategyConfiguration("docker.workspace", ...)`, a plugin for its tool in the strategy configuration of its `ToolContext`, with OneWare's stored override on top. The value of the call comes first. Any other value fails the run with a message naming its source.

## Environment Variables

The extension automatically loads environment variables from a `.env` file in your project's working directory. This is useful for CI/CD integration:

```env
# .env file in your project root
MY_LICENSE_KEY=abc123
```

These variables are injected into the container alongside the tool command. They do not reach the extension itself, so `ONEWARE_DOCKER_IMAGE`, which overrides the image of every run, takes effect only in the environment OneWare Studio is started with.
