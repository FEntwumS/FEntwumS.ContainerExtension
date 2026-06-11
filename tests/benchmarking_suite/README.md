# OneWare Hybrid Strategy Benchmarking Suite

This directory contains the tools necessary to reproduce the performance evaluation metrics presented in the Master's Thesis: *"Design and Implementation of a Modular Architecture for the Transparent Integration of Containerized Execution Environments for Heterogeneous Open-Source Binaries in OneWare Studio"*.

## Overview

The `benchmark.py` script is a high-precision Python utility designed to empirically measure the execution overhead introduced by the Hybrid Strategy Pattern (Native vs. Containerized execution).

It performs the following:

1. **Warmup Phase**: Executes the given command $N$ times to populate the host OS caches, CPU instruction caches, and I/O buffers.
2. **Native Measurement**: Executes the command iteratively on the host system, capturing high-resolution timings via `time.perf_counter()`.
3. **Container Measurement**: Wraps the exact same command in a reproducible Docker context (`docker run --rm -v $(pwd):/workspace ...`) and measures the execution time.
4. **Statistical Analysis**: Calculates Mean, Standard Deviation (±), and Absolute/Relative Overhead (e.g., `2.0x`).

## Requirements

- Python 3.8+ (Only standard libraries are used; no `pip install` required or `venv` needed)
- `docker` daemon running and accessible by the current user
- Tested EDA tools (e.g., `ghdl`, `yosys`) available in the specified Docker images (e.g., `hdlc/ghdl:yosys`)

## Usage

Run the script from the **repository root** (not from within `benchmarking_suite/`).

### Benchmark Mode

```bash
# Basic Test (using a simple local command)
python3 benchmarking_suite/benchmark.py --image ubuntu --iterations 10 --warmup 2 --cmd ls -la

# Real-World EDA Benchmark — Verilog (Picorv32)
python3 benchmarking_suite/benchmark.py \
  --image hdlc/ghdl:yosys \
  --iterations 10 --warmup 2 \
  --output results/docker_wrap/picorv32.json \
  --cmd yosys -p 'prep -top picorv32' benchmarking_suite/picorv32/picorv32.v

# Real-World EDA Benchmark — VHDL (Neorv32)
python3 benchmarking_suite/benchmark.py \
  --image hdlc/ghdl:yosys \
  --iterations 10 --warmup 2 \
  --output results/docker_wrap/neorv32.json \
  --cmd sh -c 'mkdir -p build && ghdl -i --workdir=build --work=neorv32 benchmarking_suite/neorv32/rtl/core/*.vhd && ghdl -m --workdir=build --work=neorv32 neorv32_cpu'

# Using the .NET Docker.DotNet backend (requires harness to be pre-built)
python3 benchmarking_suite/benchmark.py \
  --backend dotnet \
  --image hdlc/ghdl:yosys \
  --iterations 5 \
  --cmd ghdl --version

# Run both backends side-by-side
python3 benchmarking_suite/benchmark.py --backend all --cmd echo hello
```

### Compare Mode

Compare two previously exported JSON result files to detect regressions or improvements:

```bash
# Compare Neorv32 vs Picorv32 benchmark results
python3 benchmarking_suite/benchmark.py \
  --compare results/docker_wrap/neorv32.json results/docker_wrap/picorv32.json

# Compare before/after results to detect performance regressions
python3 benchmarking_suite/benchmark.py \
  --compare results/baseline.json results/after_optimization.json
```

The comparison output shows a table with delta percentages (⬆️ regressions, ⬇️ improvements) for mean, stdev, min, and max execution times.

### Arguments

| Argument | Default | Description |
| --- | --- | --- |
| `--image` | `hdlc/ghdl:yosys` | Docker image to emulate the toolchain |
| `--backend` | `cli` | Execution backend: `cli` (docker run), `dotnet` (C# Docker.DotNet API), or `all` |
| `--cmd` | *(required)* | The native command to benchmark (must be last argument) |
| `--iterations` | `10` | Number of measured runs for statistical stability |
| `--warmup` | `2` | Number of unmeasured runs to prime system caches |
| `--timeout` | *(none)* | Per-iteration timeout in seconds. Prevents hanging benchmarks |
| `--output` | *(none)* | Export benchmark results to a JSON file |
| `--workspace` | `.` | Working directory for command execution |
| `--platform` | *(none)* | Force Docker platform (e.g., `linux/amd64`). Mirrors the Container Engine setting |
| `--skip-native` | *(flag)* | Skip the native execution benchmark |
| `--skip-pull` | *(flag)* | Skip Docker image pre-pull before benchmarking |
| `--dry-run` | *(flag)* | Print all commands that would be executed without running them |
| `--compare` | *(none)* | Compare two JSON result files and print a regression/improvement table (does not run benchmarks) |
| `--verbose` | *(flag)* | Enable debug-level per-iteration logging |

### DotNet Backend Setup

The `--backend dotnet` mode requires the C# benchmark harness to be pre-compiled:

```bash
dotnet publish src/ContainerBenchmarkHarness/ContainerBenchmarkHarness.csproj \
  -c Release -o tests/benchmarking_suite/harness_bin
```

## Notes on Thesis Reproduction

If you are reproducing the **Neorv32 (VHDL)** metrics, note that the script is intentionally designed to handle the `FileNotFoundError` when `ghdl` is missing natively on the host (e.g., macOS or clean Ubuntu servers). The script will report "Native execution failed", but will **continue** to benchmark the Docker container, proving the core thesis claim of "Environment Preservation".

## Output Format

Benchmark results are exported as JSON with the following structure:

```json
{
  "timestamp": "2026-03-20T08:19:41.545614",
  "system": {
    "os": "Darwin", "release": "25.3.0",
    "machine": "arm64", "processor": "arm",
    "docker_engine": "28.5.2 (OrbStack)"
  },
  "experiment": {
    "workload": "yosys -p 'prep -top picorv32' ...",
    "image": "hdlc/ghdl:yosys",
    "backend": "cli",
    "iterations": 10, "warmup": 2
  },
  "results": {
    "native":  { "times": [...], "return_codes": [...], "statistics": { "mean", "stdev", "min", "max", "median", "p95", "cv_percent" } },
    "docker":  { "times": [...], "return_codes": [...], "statistics": { "mean", "stdev", "min", "max", "median", "p95", "cv_percent" } },
    "comparison": { "absolute_overhead_sec": 0.454, "relative_overhead_x": 3.48 }
  }
}
```

The `comparison.relative_overhead_x` value represents how many times slower the Docker execution is compared to native (e.g., `3.48` = Docker takes 3.48× as long).

## DotNet Backend Architecture

The `--backend dotnet` mode uses a standalone .NET benchmark harness (`ContainerBenchmarkHarness`) that:

1. Creates a `MockSettingsService` mirroring all 16 plugin defaults
2. Bootstraps DI and instantiates a real `DockerExecutionStrategy`
3. Invokes `ExecuteAsync()` — the same code path used inside OneWare Studio
4. Returns the container's exit code for validation

This ensures benchmarks measure the **actual SDK overhead**, not a simplified Docker CLI wrapper.

## CI/CD Integration

For automated benchmarking in CI pipelines:

```bash
# Skip native execution (tool may not be installed on CI runner)
python3 benchmarking_suite/benchmark.py \
  --skip-native \
  --image hdlc/ghdl:yosys \
  --iterations 5 \
  --output results/ci_run.json \
  --cmd yosys --version

# Compare against baseline to detect regressions
python3 benchmarking_suite/benchmark.py \
  --compare results/baseline.json results/ci_run.json
```

> **Tip:** Use `--skip-native` in CI environments where the EDA tools are not installed natively. The script will only measure Docker execution and skip the comparison section.
