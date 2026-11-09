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
| Extra Container Labels | Text | *(empty)* | Space-separated `key=value` container labels for filtering; `-p host:container` (or `--publish`) publishes a port instead, on `127.0.0.1` unless the mapping names an address, such as `-p 0.0.0.0:8080:80` |

### Security & Fallback

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Bypass Named Pipe Security Check | CheckBox | On | Windows only. Skips the named-pipe server-process trust verification. On by default because the check produces false positives on common non-default daemon setups (WSL2 relays, rootless/remote engines); uncheck it on a hardened Windows host to re-enable the impersonation guard |
| Allow Native Fallback | CheckBox | Off | If the Docker daemon is unreachable, execute the tool natively from the host `PATH` instead of failing. **Note:** native execution bypasses container isolation |

### Logging & Telemetry

| Setting | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| Log Level | ComboBox | `Errors Only` | `Off`, `Errors Only`, `Info`, `Verbose`. Defaults to `Errors Only` (privacy-by-default); `Verbose` adds SDK messages and stack traces. It also decides which runs the telemetry records: see [Telemetry](telemetry.md#execution-telemetry) |
| Show Timestamps in Logs | CheckBox | On | Prepend `[HH:mm:ss.fff]` to SDK log messages |
| Telemetry Retention | ComboBox | `25` | Max entries: `None`, `25`, `50`, `100`, `250`, `500`, `1000`, `Unlimited`. Defaults to `25` (privacy-by-default); `None` opts out and **purges** existing history. `Unlimited` sets no count, but the size cap stays: a log over 10 MB is cut to its newest 200 entries, one over 50 MB to its newest 50 |
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

Each run mounts the project directory, the folder that holds the `.fpgaproj` file, read-only at `/workspace` in the container and starts the tool in its working directory below it, such as `/workspace/build/netlist`. Without a project file in the working directory or a folder above it, the working directory itself is mounted; so is it when the folder with the project file is the root of a file system, such as a drive on Windows, or is or contains the user's home folder, a shared temporary folder (on Linux and macOS `/tmp`, `/private/tmp`, `/var/tmp` and the temporary folder of the process; on Windows the user profile and the temporary folder) or a folder the bind check blocks (on Linux and macOS such as `/usr/bin` or `/var/run`, so `/usr` and `/var` as well), which are never mounted as a project. At the `Info` log level the run log names the project root when it mounts one above the working directory, says that it is mounted read-only and, for a tool that writes, that its working directory is writable. A path argument the extension recognises as a path, also one through a symbolic link that leads out of the mounted folder, fails the run when it lies outside that folder, with an error that names every such path and the folder, before the image is pulled. It recognises a path that stands on its own, the value after `=` of an option such as `--input=<path>` or `-DFOO=<path>`, a path attached to `-P`, a path attached to `-o` that is rooted or starts with `~/` or `~\`, and a path in an argument that a space, a comma or a semicolon splits, such as the script after `-p`. A path on its own or the value after `=` counts only when it holds a `/` or `\`, is rooted, starts with `~/` or `~\` or is the value after `--workdir=`; any other value, such as `x.json` or a bare `..`, also as the value after `-o` or in `--input=..`, is passed on unchanged and unchecked. Such a name lies in the working directory, except `..`, which names the folder above it and lies outside the mounted folder whenever the working directory is mounted alone: without a project file, in a working directory at the project root, or below a project folder that is never mounted, as described above. A host path into the mounted project root is mapped as a host path, also when the project lies on the host inside a folder literally named `/workspace`. Any other path that is `/workspace` or lies below it is read as a path in the container and kept, with `.` and `..` resolved, and fails the run when it leaves `/workspace`. A host project inside a folder literally named `/workspace` therefore works only for its own paths: a host path below that folder but outside the project is read as a path in the container, so it names a place inside the project there instead of the host file. Only `/dev/null`, `/dev/zero`, `/dev/urandom` and paths under the image's `/bin`, `/usr/bin`, `/lib` and `/lib64` pass through unchanged, as paths in the container. The extension does not expand a `~`: it checks `~/x` and `~\x` as a folder `~` in the working directory and passes the argument on as written, except that every `\` becomes `/`, as in any argument. A tool may expand a `~/` itself, as yosys does with its `HOME`, which the container sets to `/tmp` unless the run's environment sets one; the path then names a place in the container that the check never saw, and what the tool writes there is lost with the container. A relative path or an absolute host path is the reliable form. A path attached directly to an option, without `=` and other than these forms after `-P` and `-o`, such as `-obuild/out.json`, `-I/abs/include` or `+incdir+/abs/include`, is not recognised as a path and is not mapped. When it holds a `/` or `\`, the whole argument is checked as a path in the working directory, its option letters read as the first folder: it reaches the tool unchanged, apart from every `\` becoming `/`, while that path stays inside the mounted folder, and fails the run when it leaves it. A `..` right after the option letters therefore only leaves the folder they form, such as `-o..`, so the check climbs up to two levels fewer than the tool does, and where that path leads through a symbolic link, the argument comes back as the container path it resolves to, without its option letters. A path given as a separate argument, such as `-o build/out.json`, or after `=` is checked as the tool reads it when it counts as a path. A value that holds `://`, such as a URL, is not checked, also after `--workdir=` or attached to an option, unless it is rooted or starts with `~/` or `~\` and stands on its own, after `=` or attached to `-P` or `-o`; in an argument that a space, a comma or a semicolon splits, it is never checked. Two GHDL forms are rewritten instead of checked: the value of `--work` or `-work`, after `=` or as the next argument, becomes a library name, and with `ghdl` and one of `-m`, `-e`, `-r`, `--synth`, `--elab` or `--run`, an argument that ends in `.vhd` or `.vhdl` becomes its unit name. On a Windows host a path that begins with `\` is rooted, as `\src\top.v` names a file from the root of the current drive, so an argument, the value of an option or a part of an argument that a space, a comma or a semicolon splits, that the extension reads as such a path fails the run when it lies outside the mounted folder, also when it was meant as something else, such as a pattern or an escaped yosys name like `\top`.

A tool writes only where release 1.2.1 let it write, in its working directory. When the working directory lies below the project root, the project root is mounted read-only at `/workspace` for every tool, and for a tool that writes the working directory is bound a second time, writable, at its place in the container, such as `/workspace/build/netlist`; every other tool gets the read-only project root alone. When the working directory is the project root, or no project file lies in it or a folder above it, the working directory alone is mounted at `/workspace`, writable for a tool that writes and read-only for every other tool, as in release 1.2.1. When the working directory lies below the project root, `/workspace` therefore names the project root, not the working directory as in release 1.2.1: a script or argument that wrote to `/workspace/<file>` for a file in its working directory now reads or writes `<file>` in the read-only project root, so it must use a relative path or the working directory's place in the container, such as `/workspace/build/netlist/<file>`. A tool that writes can no longer write into a `.git` folder or `.git` file at the top of its writable folder, which release 1.2.1 allowed, as described below. The folders of output paths that a tool's arguments name are created only in the working directory, also for a path that OneWare already mapped to `/workspace`; elsewhere in the project the tool could not write to them. The extension decides by two names, the file name of the tool's executable and the tool's name in OneWare, never by the folders the executable lies in. Both are compared in lower case and without `.exe`, and when one names a read-only tool and the other a writing one, read-only wins. So the model that Verilator built, which OneWare runs from `build/sim/verilator/<bench>/simulation` as the tool `verilator`, may write its waveform.

As in release 1.2.1, a tool that writes may change anything in its working directory, a `.git` there included, with one exception. When the top-level `.git` of the writable working directory is a folder or the `.git` file of a git worktree or submodule, the run binds that path a second time, read-only, at the same place in the container, such as `/workspace/.git` or `/workspace/build/netlist/.git`, and the copied `docker run` command shows this bind. A `.git` that is a symbolic link, or any other reparse point such as a junction on Windows, is not bound, as in release 1.2.1, since binding it would put the place it leads to into the container. The `.git` of a read-only project root needs no bind and gets none; a symbolic link inside a read-only bind is followed inside the container and reaches nothing of the host. On a host with a case-insensitive file system, such as macOS by default, another spelling such as `/workspace/.GIT` reaches the same read-only folder; this was measured with OrbStack on macOS only. Only that one path is bound. When they lie inside the working directory, two kinds of repository metadata stay writable for a tool that writes there: the metadata folder that a `.git` file names in its `gitdir:` line, together with the shared metadata folder of a git worktree, and the metadata of a repository nested deeper in the working directory. The bind check rewrites every bind to its host path with symbolic links resolved; a run whose bind then no longer names exactly the folder it was made for, the project root at `/workspace`, read-only when the working directory lies below it, the working directory at its place in the container, or the `.git` of the working directory, fails with an error saying that the project changed while the run was prepared. This happens, for example, when another run that writes in the same project replaced a folder or a `.git` with a symbolic link meanwhile, also one to another place inside the project. The check runs once when the run is built and once more after the image is made available, right before the container is created, since a pull can take long; a short window between that last check and the mount by the engine remains.

- Read-only, whatever their flags mean: the programmers `openFPGALoader`, `iceprog`, `black-iceprog`, `iceprogduino`, `icesprog`, `openocd`, `dfu-util`, `ujprog` and `fujprog`, and `gtkwave`.
- Writable, whatever their flags mean: every tool whose name starts with `yosys`, `nextpnr-`, `sby`, `mcy`, `ghdl`, `iverilog` or `verilator`, every tool whose name ends in `pack`, the unpackers included, and `eqy`, `scy`, `nvc`, `vvp`, `prjoxide`, `icepll`, `ecppll`, `gowin_pll`, `ecpbram`, `icetime`, `vcd2fst`, `vcd2lxt`, `vcd2lxt2`, `vcd2vzt`, `bin2hex` and `hex2bin`, which can write through a flag or a file argument of their own.
- Any other tool: writable only when one of its arguments is an output flag the extension recognizes (`-o`, `-w`, `-a`, `-e`, `-r`, `--output`, `--write`) or contains a redirection (`>`).

A tool that needs another access sets `docker.workspace` to `rw` or `ro`, in upper or lower case, in the same places as `docker.image`: a caller for a single run with `WithStrategyConfiguration("docker.workspace", ...)`, a plugin for its tool in the strategy configuration of its `ToolContext`, with OneWare's stored override on top. The value of the call comes first. Any other value fails the run with a message naming its source.

## Environment Variables

The extension automatically loads environment variables from a `.env` file in the tool's working directory; when the working directory lies below the project root, a `.env` file in the root is not read. This is useful for CI/CD integration:

```env
# .env file in the tool's working directory
MY_LICENSE_KEY=abc123
```

These variables are injected into the container alongside the tool command. They do not reach the extension itself, so `ONEWARE_DOCKER_IMAGE`, which overrides the image of every run, takes effect only in the environment OneWare Studio is started with.
