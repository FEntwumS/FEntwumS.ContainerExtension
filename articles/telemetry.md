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
