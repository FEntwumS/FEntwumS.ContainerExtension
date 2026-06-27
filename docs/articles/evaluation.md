# Evaluation Methodology

This chapter specifies the empirical evaluation of the transparent-integration
architecture. The central claim under test is functional: the
`IToolExecutionStrategy` hybrid intercepts FPGA-tool execution and runs it inside
a container without altering the result the user would otherwise obtain natively.
Execution overhead and output reproducibility are supporting evidence and are
reported as such, not as the contribution itself.

The methodology mirrors the harness exactly. Every number that appears in the
thesis is produced by the code described here; this chapter does not restate
results, it documents how they are obtained and where they are written. The
relevant sources are:

| Concern | File |
| --- | --- |
| Per-workload measurement and statistics | `tests/benchmarking_suite/benchmark.py` |
| Cross-platform workload matrix | `tests/benchmarking_suite/run_evaluation.py` |
| Cross-platform aggregation and reporting | `tests/benchmarking_suite/aggregate.py` |
| Real strategy code path (the OneWare path) | `src/ContainerBenchmarkHarness/Program.cs` |
| Headless toolchain coverage smoke test | `tests/integration/run_all.sh` |

## Evaluation Axes

The evaluation is organised along three independent axes. Each isolates one
property of the architecture and is measured by a distinct part of the harness.

### Functional transparency and toolchain coverage

The architecture is transparent if a tool invocation that succeeds natively also
succeeds when intercepted and run in a container, across the breadth of the
open-source FPGA toolchain rather than a single tool. Coverage is exercised by
`tests/integration/run_all.sh`, which drives the toolchain end to end through
`docker run` over the workload corpus in `tests/integration/`: GHDL analysis,
elaboration and simulation (`VHDL_Blink`); Icarus Verilog compile and execute,
Verilator build and execute, and Yosys synthesis (`Verilog_Blink`); SymbiYosys
formal verification (`Formal_Verification`); the iCE40 synthesis to bitstream
flow with Yosys, nextpnr-ice40 and icepack (`iCE40_Flow`); and the ECP5 flow with
Yosys, nextpnr-ecp5 and ecppack (`ECP5_Flow`). Each phase is recorded as
`SUCCESS` or `FAILED` with a wall-clock duration in `tests/integration/test_report.md`.
This establishes that the represented categories — analysis, simulation,
synthesis, place-and-route, bitstream packing, and formal verification — all
execute under containerisation.

### Output determinism and portability across machines

Transparency at the level of exit codes is necessary but not sufficient; the
artifacts produced must also be machine-independent. For every workload that
yields a single stable output (a netlist or a bitstream), `benchmark.py` computes
the SHA-256 of that artifact after the run via the `--artifacts` flag
(`hash_artifacts`). `run_evaluation.py` attaches the artifact list to each
workload, and `aggregate.py` (`write_determinism`) builds a cross-machine matrix:
for each artifact it compares the recorded hashes across all platforms present
and labels the row `YES` when every platform produced an identical digest,
`partial` when the digests that exist agree but not all platforms are present,
and `NO` otherwise. Identical hashes across heterogeneous hosts are direct
evidence that the container makes tool output independent of the host machine.

### Execution-overhead characterisation

Containerisation is only viable if its cost is bounded and quantified. The
overhead axis measures the wall-clock cost of running a workload through a
container relative to running it natively, with confidence intervals and a
hypothesis test rather than point estimates. This is the role of `benchmark.py`'s
per-iteration timing and statistics, detailed below. Overhead is reported both in
absolute seconds and as a relative factor (`relative_overhead_x`), and the
one-time cost of acquiring the image is measured separately so it does not
contaminate the steady-state figure.

## Experimental Design

### Cross-platform matrix

The architecture targets heterogeneous hosts, so the evaluation is run on three
machine classes: macOS on arm64, Linux on amd64, and Windows on amd64.
`run_evaluation.py` derives the platform tag from `platform.system()` and
`platform.machine()` (`platform_tag`, normalising to `macos-arm64`,
`linux-amd64`, `windows-amd64`) and writes that machine's results into
`results/<platform-tag>/`. The orchestration is one command per machine: the
operator runs the same invocation on each host, and the per-platform result trees
are later merged by `aggregate.py`. No machine needs to know about the others;
cross-platform comparison happens entirely at aggregation time from the captured
metadata.

### Workload set

The workload matrix is defined declaratively in `run_evaluation.py` (`WORKLOADS`)
and spans the toolchain categories:

| Workload | Category | Directory | Artifact hashed |
| --- | --- | --- | --- |
| `synth_ice40_yosys` | synthesis | `iCE40_Flow` | `ice40_blink.json` |
| `pnr_ice40_nextpnr` | place-and-route | `iCE40_Flow` | `ice40_blink.asc` |
| `pack_ice40_icepack` | bitstream | `iCE40_Flow` | `ice40_blink.bin` |
| `synth_ecp5_yosys` | synthesis | `ECP5_Flow` | `ecp5_blink.json` |
| `pnr_ecp5_nextpnr` | place-and-route | `ECP5_Flow` | `ecp5_blink.config` |
| `pack_ecp5_ecppack` | bitstream | `ECP5_Flow` | `ecp5_blink.bit` |
| `sim_iverilog` | simulation | `Verilog_Blink` | `Blink.vvp` |

Each workload invokes a single FPGA tool, mirroring how OneWare drives the toolchain
— one tool per interception — so the `DockerExecutionStrategy` dispatches an argv
vector, never a `sh -c` pipeline. The per-board workloads are ordered by dependency
(synthesis → place-and-route → bitstream packing) so each phase's artifact persists as
the next phase's input. Each workload is self-contained in its directory under
`tests/integration/`; the benchmarked input files are SHA-256 hashed and recorded so the
exact inputs are pinned (see *Reproducibility*). End-to-end full-flow and formal
coverage is exercised separately by the `tests/integration/run_all.sh` smoke runner.

### Native, container CLI, and the real strategy path

Three execution backends are compared, selected by `benchmark.py --backend`:

1. **Native.** The tool is invoked directly on the host as the baseline. When the
   tool is absent natively — common on a clean macOS or CI host — the baseline is
   simply skipped and the containerised backends still run; the native baseline
   is optional (`run_evaluation.py --with-native`).
2. **Docker CLI (`cli`).** The workload is wrapped in `docker run --rm` with the
   workspace bind-mounted at `/workspace` (`build_docker_cli_cmd`). This isolates
   the cost of containerisation itself, independent of the extension.
3. **Strategy (`dotnet`).** The workload runs through `ContainerBenchmarkHarness`
   (`src/ContainerBenchmarkHarness/Program.cs`), which constructs a real
   `DockerExecutionStrategy` over a `MockSettingsService` carrying the plugin's
   default settings and invokes `ExecuteAsync` — the identical code path OneWare
   Studio uses in production, including image resolution, container lifecycle and
   stream demultiplexing. Telemetry and SDK logging are disabled in the harness so
   their writes do not run on the measured thread and confound the figure. This is
   the load-bearing backend: it measures the architecture as shipped, not a CLI
   approximation of it.

`run_evaluation.py` defaults to `--backend all`, exercising both the CLI and the
real strategy path. The overhead attributable to the extension over and above raw
containerisation is computed directly as the cli-vs-strategy delta — a Welch t-test
with Cohen's *d* and a 95 % confidence interval, exported as `cli_vs_dotnet` in the
comparison block — and, needing no native baseline, it is available on every host,
including those where no native toolchain is installed.

## Statistical Method

The statistics are implemented in `benchmark.py` with the Python standard library
only; no third-party numerical dependency is introduced.

- **Warmup.** Each suite first performs `--warmup` unrecorded runs
  (`perform_benchmark_suite`) to prime OS file caches, CPU caches and, for the
  container backends, any first-run setup, so the measured iterations reflect
  steady state.
- **Confidence intervals.** Every reported mean carries a two-sided 95% Student-t
  confidence interval (`calculate_stats`, `t_critical_95`). Critical values are
  exact for `df ≤ 30`, table-interpolated conservatively above that, and fall
  back to the asymptotic `z = 1.960` for large `df`.
- **CV-adaptive repetition.** A minimum of `--iterations` measured runs is taken;
  if `--target-cv` is set, sampling continues until the coefficient of variation
  falls to or below the target or `--max-iterations` is reached
  (`perform_benchmark_suite`). `run_evaluation.py` defaults to a 15% CV target
  with a floor of 30 and a cap of 60 iterations, so noisy workloads are sampled
  more heavily and quiet ones are not over-sampled.
- **Welch's unpaired t-test.** Native versus each container backend is compared
  with Welch's t-test for unequal variances (`welch_t_test`), reporting `t`,
  Welch–Satterthwaite `df`, a two-sided p-value computed from the regularised
  incomplete beta function (`student_t_two_sided_p`), the mean difference and its
  95% CI, and significance at α = 0.05.
- **Paired t-test (interleaved mode).** With `--interleave`, native and container
  runs alternate per iteration (`perform_interleaved_suite`) so the samples are
  aligned and a paired t-test is valid (`paired_t_test`); the per-iteration
  pairing cancels slow drift in machine load.
- **Effect size.** Cohen's *d* with pooled standard deviation (`cohens_d`)
  accompanies each comparison, separating statistical significance from practical
  magnitude.
- **Outlier flagging.** Iterations more than `--outlier-sigma` (default 2σ) from
  the mean are flagged (`detect_outliers`); they are reported, not silently
  discarded.

## Reproducibility

Each result file is self-describing so that any run can be reproduced and audited
without external notes. `export_json` (`SCHEMA_VERSION = 2`) records:

- **Environment.** OS, release, architecture, CPU model, logical core count,
  total RAM, Python version, and Docker server version, OS, storage driver and
  architecture (`get_cpu_model`, `get_total_ram_gb`, `get_docker_info`).
- **Image identity.** The image reference and its resolved digest
  (`get_image_digest`), pinning the exact toolchain binaries that ran.
- **Input and harness identity.** SHA-256 of every workload input file
  (`hash_input_files`), the SHA-256 of `benchmark.py` itself, and the short git
  commit of the harness (`get_git_commit`), so the inputs, the measurement code
  and the repository state are all fixed.
- **Output artifacts.** SHA-256 of each produced artifact (`output_artifacts_sha256`),
  the basis of the cross-machine determinism matrix.
- **Separated image-pull cost.** With `--measure-pull`, the image is removed and
  cold-pulled under timing (`measure_pull_cost`), recording pull seconds and image
  size in a distinct `pull_cost` block. The one-time acquisition cost is thus
  never folded into the steady-state execution figures.

All output is constrained to the repository root; `export_json` refuses to write
outside it.

## How to Reproduce

### One command per machine

On each host (macOS/arm64, Linux/amd64, Windows/amd64), with a container runtime
available, build the .NET strategy harness once and run the evaluation:

```bash
# Build the real-strategy backend (once per machine).
dotnet publish src/ContainerBenchmarkHarness/ContainerBenchmarkHarness.csproj \
  -c Release -o tests/benchmarking_suite/harness_bin

# Run the full cross-platform workload matrix (CLI + real strategy).
python3 tests/benchmarking_suite/run_evaluation.py \
  --image fentwums/oss-cad-suite:latest
```

The runner writes one JSON file per workload into
`tests/benchmarking_suite/results/<platform-tag>/`. Add `--with-native` on hosts
where the toolchain is installed natively to obtain the overhead baseline; use
`--only <name> ...` to run a subset, and `--image` to pin a specific toolchain
image.

### Aggregate across machines

Collect every machine's `results/<platform-tag>/` tree into a single
`results/` directory, then aggregate:

```bash
python3 tests/benchmarking_suite/aggregate.py
```

This emits the reporting artifacts described below. Figures require `matplotlib`;
the tables do not.

### Reading `results/`

| Artifact | Content |
| --- | --- |
| `results/summary.csv` | One row per platform × workload × backend: `n`, mean, 95% CI bounds, CV%, and the native-versus-container overhead factor and Welch p-value. The machine-readable source of every reported number. |
| `results/determinism.md` | The cross-machine artifact-hash matrix; the `Identical?` column is the portability result per artifact. |
| `results/summary.md` | Human-readable per-platform table: mean (95% CI), CV and overhead factor per workload and backend, headed by the machine's CPU, RAM, core count and Docker version. |
| `results/figures/*.png` | Per-platform grouped bar charts of mean execution time with 95% CI error bars (strategy backend), generated only when `matplotlib` is installed. |

## Interpreting the Results

The three axes are read against three distinct expectations.

- **Transparency and coverage** hold when every phase in
  `tests/integration/test_report.md` and every workload in `results/summary.md`
  completes successfully across the toolchain categories. A failure localises to a
  specific tool and platform rather than a global pass/fail.
- **Determinism** holds where the `Identical?` column in `results/determinism.md`
  reads `YES` for an artifact across all evaluated machines: byte-identical
  netlists and bitstreams from heterogeneous hosts are the portability claim,
  realised. Concrete hashes and the per-artifact verdict are in that file (see
  `results/determinism.md`).
- **Overhead** is read from `results/summary.csv` and `results/summary.md`: the
  `overhead_x_vs_native` factor with its Welch p-value quantifies the steady-state
  cost of containerisation, and the gap between the `cli` and `strategy` backends
  isolates any cost the extension adds beyond raw containerisation. The one-time
  image-pull cost is reported separately and must not be conflated with per-run
  overhead. The measured magnitudes are in `results/summary.csv` (per-platform
  means, intervals and overhead factors) rather than reproduced here, so that this
  chapter remains valid as the data is regenerated.

The overhead figures are supporting evidence for the viability of the
architecture, not its contribution. The contribution is that the same tool
invocation, unchanged from the user's perspective, runs in an isolated and
portable environment and yields a machine-independent result — which the
transparency and determinism axes establish, and which the overhead axis bounds.
