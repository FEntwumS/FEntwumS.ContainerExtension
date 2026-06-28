# Native-vs-Container Baseline Protocol (linux/amd64, windows/amd64)

This protocol specifies how to collect the **valid** native-versus-container execution-overhead
baseline for the overhead study. It complements [the evaluation methodology](evaluation.md): that
chapter defines the workload matrix, the three backends, and the statistics; this one fixes the single
condition under which a *native* baseline is sound — that the native toolchain and the containerized
toolchain are the **same build on the same architecture, with no emulation in between**.

## Why a dedicated protocol

The reference image `fentwums/oss-cad-suite` is published `linux/amd64` only (GHDL is not compiled in
the upstream arm64 releases; see [Limitations](limitations.md)). On an Apple-Silicon (arm64) host the
container therefore runs under the daemon's binary-emulation layer (Rosetta/qemu), while any native
toolchain is arm64. A macOS/arm64 "native vs container" comparison thus measures *containerisation +
ISA emulation*, and the emulation term dominates — it does not isolate the cost of the interception
layer. The macOS/arm64 figures are reported only as an emulated upper bound.

A sound native baseline requires eliminating both confounds at once:

1. **Same architecture, no emulation** — run on a real `linux/amd64` (or `windows/amd64`) host, where
   the `linux/amd64` image executes natively.
2. **Same toolchain build** — the native binaries must be the *same* `oss-cad-suite` release the image
   is built from, so the only difference between the two arms is the container boundary, not tool
   versions or build flags.

## Toolchain-version parity (the load-bearing step)

The image pins a single `oss-cad-suite` release in `docker/oss-cad-suite/Dockerfile`
(`ARG RELEASE_TAG`, currently `2026-06-26`, with a checksum-verified `linux-x64` tarball). The native
host must install **the same dated release**, so that `yosys`, `ghdl`, `nextpnr-*`, `icepack`/`ecppack`
and `iverilog` are byte-for-byte the binaries that run in the container.

```bash
# 1. Read the pinned release the container is built from (single source of truth).
RELEASE_TAG=$(grep -m1 '^ARG RELEASE_TAG=' docker/oss-cad-suite/Dockerfile | cut -d= -f2)
DATE=${RELEASE_TAG//-/}          # e.g. 2026-06-26 -> 20260626

# 2. Install the matching native oss-cad-suite for the host architecture.
#    Linux/amd64:
URL="https://github.com/YosysHQ/oss-cad-suite-build/releases/download/${RELEASE_TAG}/oss-cad-suite-linux-x64-${DATE}.tgz"
curl -fL "$URL" -o /tmp/oss-cad-suite.tgz
sudo tar xzf /tmp/oss-cad-suite.tgz -C /opt          # -> /opt/oss-cad-suite
export PATH="/opt/oss-cad-suite/bin:$PATH"
export GHDL_PREFIX="/opt/oss-cad-suite/lib/ghdl"
#    Windows/amd64: download oss-cad-suite-windows-x64-${DATE}.exe, extract, and run
#    oss-cad-suite\environment.bat (which sets PATH/GHDL_PREFIX) before the run.

# 3. Verify parity against the container BEFORE measuring. Versions must match exactly.
docker run --rm fentwums/oss-cad-suite:${RELEASE_TAG} yosys --version
yosys --version
docker run --rm fentwums/oss-cad-suite:${RELEASE_TAG} ghdl --version | head -1
ghdl --version | head -1
```

If any native version differs from the container's, stop: the overhead number would conflate a
toolchain-version delta with the containerisation cost. Re-install the exact dated release before
proceeding. The image digest and the host CPU/RAM/OS are captured per run by `benchmark.py`, so the
parity conditions are recorded alongside every measurement.

## Running the baseline

Per the measurement-hygiene controls in [evaluation.md](evaluation.md) (quiesce the host, pin the CPU
governor to `performance`, keep the machine on AC power), then, on each amd64 host:

```bash
# Build the real-strategy harness once per machine.
dotnet publish src/ContainerBenchmarkHarness/ContainerBenchmarkHarness.csproj \
  -c Release -o tests/benchmarking_suite/harness_bin

# Run the full matrix WITH the native baseline (native + docker CLI + the real strategy).
python3 tests/benchmarking_suite/run_evaluation.py \
  --image fentwums/oss-cad-suite:${RELEASE_TAG} --with-native
```

`run_evaluation.py` writes `results/<platform-tag>/` (`linux-amd64`, `windows-amd64`). Collect every
host's tree into one `results/` directory and aggregate:

```bash
python3 tests/benchmarking_suite/aggregate.py
```

## What the amd64 baseline establishes

Only on these hosts is `overhead_x_vs_native` (with its Welch p-value and Holm-Bonferroni adjustment in
`results/summary.csv`) attributable to containerisation rather than emulation. The `cli`-vs-`strategy`
delta (`cli_vs_dotnet`) — the cost the extension adds over raw `docker run` — needs no native baseline
and is valid on every host, macOS included. The macOS/arm64 run remains useful for functional
transparency and output-determinism (identical artifact hashes across architectures), and for the
emulated upper bound, but its overhead figures must not be compared against the amd64 native numbers.
