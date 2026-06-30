# Telemetry & Troubleshooting

## Execution Telemetry

Every containerized tool execution is recorded as a JSON Lines entry in `~/.oneware/container_telemetry.jsonl`.

### Entry Format

```json
{
  "ts": "2026-03-21T09:45:12.345Z",
  "image": "hdlc/ghdl:yosys",
  "digest": "sha256:abc123...",
  "tool": "ghdl",
  "duration_s": 4.23,
  "exit": 0,
  "docker_run": "docker run --rm -v /project:/workspace hdlc/ghdl:yosys ghdl -a test.vhd",
  "peak_mem": 536870912,
  "max_cpu": 89.2,
  "oom": false
}
```

| Field | Description |
| ----- | ----------- |
| `ts` | ISO 8601 UTC timestamp |
| `image` | Docker image used |
| `digest` | SHA256 image digest for reproducibility |
| `tool` | Tool name (e.g., ghdl, yosys, nextpnr) |
| `duration_s` | Wall-clock execution time in seconds |
| `exit` | Container exit code (0 = success) |
| `cancelled` | Present and `true` if user cancelled the execution |
| `docker_run` | Reconstructed CLI command for debugging |
| `peak_mem` | Peak container memory usage in bytes (null if unavailable) |
| `max_cpu` | Maximum CPU usage percentage during execution (null if unavailable) |
| `oom` | `true` if container was killed by the OOM killer |

### Dashboard Telemetry

The Container Dashboard's **Recent Executions** section shows the last 10 entries with:

- Color-coded exit status (green = success, red = failure, yellow = cancelled)
- Execution timing
- One-click "Copy Docker Run" for reproducing issues outside the IDE

### Telemetry Management

- **Export**: Click "Export Telemetry" in the dashboard to save a copy
- **Clear**: Click "Clear Recents" to delete all entries
- **Retention**: Configure via `Telemetry Retention` setting (None/25/50/100/250/500/1000/Unlimited)

## Troubleshooting

### Docker Daemon Not Detected

**Symptoms**: "Daemon not reachable" in the dashboard, red connection status.

**Solutions**:

1. Ensure Docker Desktop (or Podman/Colima/OrbStack) is **running**
2. Check the socket:

   ```bash
   # macOS/Linux
   ls -la /var/run/docker.sock

   # Or check Colima
   ls -la ~/.colima/default/docker.sock
   ```

3. If using a custom socket, set it in **Custom Daemon Socket** setting (e.g., `unix:///path/to/docker.sock`)

### Image Pull Fails

**Symptoms**: "Pull failed" error during tool execution.

**Solutions**:

1. Check internet connectivity
2. Verify the image name is correct: `docker pull hdlc/ghdl:yosys`
3. For private registries, run `docker login` first
4. Try setting **Image Pull Policy** to `always` to force a fresh pull

### Apple Silicon Compatibility

**Symptoms**: `exec format error` when running x86_64 tools on ARM64.

**Solutions**:

1. Set **Image Platform** to `linux/amd64` in settings
2. Ensure Rosetta 2 is installed: `softwareupdate --install-rosetta`
3. In Docker Desktop: Enable "Use Rosetta for x86_64/amd64 emulation"

### Container Runs Out of Memory

**Symptoms**: Container killed with exit code 137 (OOM killer).

**Solutions**:

1. Increase **Memory Limit** in settings
2. Check Docker Desktop's resource allocation
3. Close other memory-intensive applications

### Reproducing Issues Outside the IDE

Use the **Copy Docker Run** feature:

1. Open the Container Dashboard
2. Find the failed execution in **Recent Executions**
3. Click the copy button to get the exact `docker run` command
4. Paste and run in your terminal for debugging

```bash
# Example copied command:
docker run --rm \
  -v /path/to/project:/workspace \
  -w /workspace \
  --platform linux/amd64 \
  hdlc/ghdl:yosys \
  ghdl -a --std=08 test.vhd
```

### Orphan Containers After IDE Crash

The extension automatically cleans up dangling containers when the IDE process exits (via `AppDomain.ProcessExit` hook). If containers remain:

```bash
# List containers with the extension's prefix
docker ps -a --filter name=containerextension-

# Remove them
docker rm -f $(docker ps -aq --filter name=containerextension-)
```

## Data protection

### Recorded fields

An execution entry (`container_telemetry.jsonl`) carries the fields enumerated in
[Entry Format](#entry-format): timestamp, image reference, image digest, tool name,
duration, exit code, the cancellation flag, the reconstructed `docker run` command,
peak memory, maximum CPU, and the OOM flag; a redacted `error_msg` is appended when the
execution failed. Error entries (`container_errors.jsonl`) carry the originating
component, the action, the exception message, an optional stack trace (recorded only at
the `Verbose` log level), and an optional context string. None of these fields is
designed to hold credentials or identifiers; the free-text fields nonetheless pass
through the redaction pipeline below before they reach disk.

### Redaction before persistence

The `image`, `docker_run`, `error_msg`, and the error-log `act`, `ex_msg`, `stack`, and
`ctx` fields are scrubbed in `ContainerTelemetry.cs` prior to serialization. The
scrubbers, applied in composition by `ScrubSecrets` and `ScrubSensitiveInfo`, remove:

- `KEY=value` secret assignments — any token whose key matches `PASSWORD`, `PWD`,
  `CREDENTIALS`, `AUTH`, `PASS`, `TOKEN`, `SECRET`, or `KEY` has its value replaced with
  `***` (`SecretScrubRegex`);
- inline URI basic-auth credentials — `scheme://user:pass@host` collapses to
  `scheme://***:***@host` (`UriCredentialsRegex`), so tokens embedded in registry or
  daemon URLs never reach the log;
- home paths and the OS username — the user-profile path is rewritten to `~`
  (`ScrubHomePath`, covering both separator forms on Windows) and the account name is
  replaced with `***` at identifier boundaries (a 3-character floor, so a short or common
  account name cannot corrupt unrelated text);
- recognised cloud-provider access keys (`CloudKeyRegex`) and UNC shares
  (`UncShareRegex`);
- bare provider tokens and bearer credentials that a `KEY=value` match would miss — GitHub
  PAT/OAuth/installation tokens and Slack tokens (`ProviderTokenRegex`), JSON Web Tokens
  (`JwtRegex`), and PEM private-key blocks (`PemPrivateKeyRegex`);
- internal network identifiers — IPv4 and IPv6 literals and `.local`/`.lan` hostnames are
  replaced with `[REDACTED_NET_ADDR]` (`IpRedactRegex`).

The `Export Telemetry` action applies the same home-path rewrite to the exported copy, so
an exported file does not reintroduce the absolute profile path.

### Locality and retention control

Telemetry is local-only. It is written to `~/.oneware/` and is never transmitted off the
machine; the extension contains no network sink for these records. The user controls
retention through the `Telemetry Retention` setting: selecting `None` sets
`TelemetryOptedOutChecker`, after which `LogExecution` and `TrackError` return before
writing, and any prior on-disk history is **purged** immediately by a settings observer
when `None` is selected (and defensively again on the next read or export) — opting out
erases, not merely pauses, collection. `Export Telemetry` additionally requires an absolute destination. The numeric
values bound the retained history, and `Clear Recents` truncates both logs on demand. The
default level is privacy-conscious (`Errors Only`, retention `25`).

### At-rest protection

On POSIX systems the backing files are created atomically with mode `0600`
(`CreateAppendStreamOptions` sets `UnixCreateMode` to user read/write), closing the window
between creation under the default umask and a subsequent permission narrowing; the
directory is restricted to `0700` and exports are written `0600`. On Windows
confidentiality is enforced through the Encrypting File System (`File.Encrypt`) applied to
each log on first materialization. Cross-process writes are serialized by a named mutex
whose identifier embeds a SHA-256 hash of the username rather than the cleartext name, so
the kernel-object name does not disclose the account to other sessions on a shared host.

### GDPR-relevant note

No personal data leaves the machine: collection is confined to the user's profile
directory, the redaction pipeline removes credentials, home paths, usernames, and internal
network addresses before persistence, and the records are never transmitted. The data
subject is the local user, who retains full control over collection (opt-out via
`Telemetry Retention = None`, which stops collection and erases any existing history) and
on-demand truncation (`Clear Recents`). The redaction pipeline's coverage across the secret
classes enumerated above is exercised by the unit test suite (`TelemetryScrubbingTests`).
