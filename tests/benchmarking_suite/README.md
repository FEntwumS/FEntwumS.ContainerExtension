# Cross-platform evaluation suite

Reproduces the empirical evaluation in the Master's thesis *"Design and Implementation of a Modular
Architecture for the Transparent Integration of Containerized Execution Environments for Heterogeneous
Open-Source Binaries in OneWare Studio"*. The evaluation has three axes:

1. **Functional transparency** — the unmodified FPGA toolchain (synthesis, place-and-route, bitstream,
   simulation) executes correctly through the containerized backends.
2. **Output determinism / portability** — the produced netlists and bitstreams are SHA-256 hashed on
   every machine; identical hashes across platforms are direct evidence that the architecture makes
   tool outputs machine-independent.
3. **Execution-overhead characterisation** — native vs. container, and raw `docker run` vs. the real
   `DockerExecutionStrategy` code path, reported with 95% confidence intervals.

See `docs/articles/evaluation.md` for the full methodology.

## Components

| Script | Purpose |
|---|---|
| `benchmark.py` | Single-workload driver: warmup, measured iterations, 95% Student-t confidence intervals, Welch unpaired and (interleaved) paired t-tests, Cohen's d, CV-adaptive repetition, full environment capture, separated image-pull cost, output-artifact hashing. |
| `run_evaluation.py` | Cross-platform orchestrator over the fixed workload matrix (iCE40 and ECP5 synthesis, place-and-route, and pack, plus Verilog simulation). One command per machine. |
| `aggregate.py` | Combines the per-platform results into `summary.csv`, `determinism.md` (cross-machine artifact-hash matrix), `summary.md`, and `figures/` (95% CI bar charts). |
| `harness_bin/` | Published `ContainerBenchmarkHarness` — runs a tool through the real `DockerExecutionStrategy`. Build artifact (git-ignored). |

## Requirements

- Python 3.8+ (standard library only for the tables; `pip install matplotlib` is needed for figures).
- A running container engine (Docker, Podman, OrbStack, ...).
- The toolchain image (default `fentwums/oss-cad-suite:latest`). For a native baseline (`--with-native`),
  the same tools must be on the host `PATH` at matching versions.

## Reproduce (run on each machine)

```bash
# Build the real-strategy backend once per machine.
dotnet publish ../../src/ContainerBenchmarkHarness/ContainerBenchmarkHarness.csproj -c Release -o harness_bin

# Run the full workload matrix (docker CLI + the real strategy path).
python3 run_evaluation.py --image fentwums/oss-cad-suite:latest

# On a host where the native tools are installed at the container versions, add the native baseline:
python3 run_evaluation.py --with-native
```

Each run writes `results/<platform>/<workload>.json` (for example `results/linux-amd64/synth_ice40_yosys.json`),
tagged automatically by OS and architecture, with full environment metadata, per-iteration timings, 95%
CIs, and the artifact hashes.

## Aggregate across machines

Copy every machine's `results/<platform>/` directory into one tree, then:

```bash
python3 aggregate.py
```

This produces, under `results/`:

- `summary.csv` — one row per platform × workload × backend (mean, 95% CI, CV, native overhead, p-value).
- `determinism.md` — the cross-platform artifact-hash matrix (do the bitstreams match across machines?).
- `summary.md` — a human-readable overview grouped by platform.
- `figures/time_<platform>.png` — mean execution time with 95% CI error bars (requires matplotlib).

## Single-workload use

```bash
# Native vs. container CLI vs. the real strategy, interleaved for a paired comparison, CV-adaptive.
python3 benchmark.py --backend all --interleave --target-cv 10 --max-iterations 50 \
  --artifacts ice40_blink.json --output out.json \
  --cmd yosys -p "synth_ice40 -top ice40_blink -json ice40_blink.json" ice40_blink.v

# Separate image-pull cost from steady-state execution.
python3 benchmark.py --measure-pull --backend cli --cmd yosys --version

# Compare two result files.
python3 benchmark.py --compare results/linux-amd64/synth_ice40_yosys.json results/macos-arm64/synth_ice40_yosys.json
```

## Notes

- The `DockerExecutionStrategy` runs **one tool per invocation**, mirroring how OneWare drives the
  toolchain; it is not a shell and does not execute `sh -c` pipelines. The workload matrix therefore
  uses single-tool phases in dependency order.
- On Apple Silicon the toolchain image runs as `linux/amd64` under emulation (upstream omits GHDL from
  the arm64 releases); overhead measured there reflects emulation and is reported as such.
