#!/usr/bin/env python3
"""
Cross-platform evaluation orchestrator for the OneWare ContainerExtension thesis.

Runs a fixed workload matrix across the FPGA toolchain (synthesis, place-and-route,
bitstream, simulation, formal) through the containerized backends, capturing:
  * execution time with 95% confidence intervals (delegated to benchmark.py),
  * output-determinism hashes of the produced artifacts (for cross-machine
    reproducibility comparison), and
  * full environment metadata.

One command per machine; run it on macOS/arm64, Linux/amd64 and Windows/amd64 and
aggregate the per-platform results with ``aggregate.py``. Pure stdlib + Docker.

Usage:
    python3 run_evaluation.py                  # cli + strategy backends, no native
    python3 run_evaluation.py --with-native    # also benchmark host-native tools
    python3 run_evaluation.py --iterations 30 --image fentwums/oss-cad-suite:latest

Author: Mert Torun (TH Koeln)
"""
import argparse
import os
import platform
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(HERE))
# HDL workload fixtures live alongside this suite under tests/integration.
FIXTURES = os.path.join(os.path.dirname(HERE), "integration")
BENCHMARK = os.path.join(HERE, "benchmark.py")
DEFAULT_IMAGE = "fentwums/oss-cad-suite:latest"

# Workload matrix. Each workload invokes a SINGLE FPGA tool, mirroring how OneWare
# drives the toolchain (one tool per interception); the DockerExecutionStrategy is
# not a shell and does not run `sh -c` pipelines. Workloads are listed in dependency
# order per board (synth -> place-and-route -> pack) so each phase's artifact persists
# for the next. `artifacts` are SHA-256 hashed after the run for cross-platform
# output-determinism.
WORKLOADS = [
    # --- iCE40 (Lattice) flow ---
    {
        "name": "synth_ice40_yosys", "category": "synthesis", "workdir": "iCE40_Flow",
        "cmd": ["yosys", "-p", "synth_ice40 -top ice40_blink -json ice40_blink.json", "ice40_blink.v"],
        "artifacts": ["ice40_blink.json"],
    },
    {
        "name": "pnr_ice40_nextpnr", "category": "place_and_route", "workdir": "iCE40_Flow",
        "cmd": ["nextpnr-ice40", "--hx1k", "--json", "ice40_blink.json",
                "--pcf", "ice40_blink.pcf", "--asc", "ice40_blink.asc"],
        "artifacts": ["ice40_blink.asc"],
    },
    {
        "name": "pack_ice40_icepack", "category": "bitstream", "workdir": "iCE40_Flow",
        "cmd": ["icepack", "ice40_blink.asc", "ice40_blink.bin"],
        "artifacts": ["ice40_blink.bin"],
    },
    # --- ECP5 (Lattice) flow ---
    {
        "name": "synth_ecp5_yosys", "category": "synthesis", "workdir": "ECP5_Flow",
        "cmd": ["yosys", "-p", "synth_ecp5 -json ecp5_blink.json", "ecp5_blink.v"],
        "artifacts": ["ecp5_blink.json"],
    },
    {
        "name": "pnr_ecp5_nextpnr", "category": "place_and_route", "workdir": "ECP5_Flow",
        "cmd": ["nextpnr-ecp5", "--85k", "--package", "CABGA381", "--json", "ecp5_blink.json",
                "--lpf", "ecp5_blink.lpf", "--textcfg", "ecp5_blink.config"],
        "artifacts": ["ecp5_blink.config"],
    },
    {
        "name": "pack_ecp5_ecppack", "category": "bitstream", "workdir": "ECP5_Flow",
        "cmd": ["ecppack", "ecp5_blink.config", "ecp5_blink.bit"],
        "artifacts": ["ecp5_blink.bit"],
    },
    # --- Verilog simulation ---
    {
        "name": "sim_iverilog", "category": "simulation", "workdir": "Verilog_Blink",
        "cmd": ["iverilog", "-o", "Blink.vvp", "Verilog_Blink.v", "Verilog_Blink_tb.v"],
        "artifacts": ["Blink.vvp"],
    },
]


def platform_tag() -> str:
    sysname = {"darwin": "macos", "linux": "linux", "windows": "windows"}.get(
        platform.system().lower(), platform.system().lower())
    arch = {"x86_64": "amd64", "amd64": "amd64", "arm64": "arm64", "aarch64": "arm64"}.get(
        platform.machine().lower(), platform.machine().lower())
    return f"{sysname}-{arch}"


def main() -> int:
    ap = argparse.ArgumentParser(description="Cross-platform ContainerExtension evaluation runner.")
    ap.add_argument("--image", default=DEFAULT_IMAGE, help="Container image for the toolchain.")
    ap.add_argument("--backend", choices=["cli", "dotnet", "all"], default="all",
                    help="cli (docker run), dotnet (real strategy), all.")
    ap.add_argument("--iterations", type=int, default=30, help="Measured iterations per workload.")
    ap.add_argument("--warmup", type=int, default=2)
    ap.add_argument("--target-cv", type=float, default=15.0, help="CV-adaptive target (%); 0 disables.")
    ap.add_argument("--max-iterations", type=int, default=60)
    ap.add_argument("--with-native", action="store_true", help="Also benchmark host-native tools.")
    ap.add_argument("--timeout", type=int, default=600, help="Per-iteration timeout (seconds).")
    ap.add_argument("--only", nargs="+", default=None, help="Run only these workload names.")
    ap.add_argument("--results-dir", default=None, help="Override results directory.")
    args = ap.parse_args()

    tag = platform_tag()
    results_dir = args.results_dir or os.path.join(HERE, "results", tag)
    os.makedirs(results_dir, exist_ok=True)
    print(f"Platform: {tag} | image: {args.image} | results -> {results_dir}\n")

    selected = [w for w in WORKLOADS if not args.only or w["name"] in args.only]
    ran, failed = [], []
    for w in selected:
        workdir = os.path.join(FIXTURES, w["workdir"])
        if not os.path.isdir(workdir):
            print(f"  SKIP {w['name']}: workdir not found ({workdir})")
            continue
        out_json = os.path.join(results_dir, f"{w['name']}.json")
        cmd = [sys.executable, BENCHMARK,
               "--backend", args.backend, "--image", args.image,
               "--workspace", workdir, "--output", out_json,
               "--iterations", str(args.iterations), "--warmup", str(args.warmup),
               "--timeout", str(args.timeout)]
        if args.target_cv and args.target_cv > 0:
            cmd += ["--target-cv", str(args.target_cv), "--max-iterations", str(args.max_iterations)]
        if not args.with_native:
            cmd += ["--skip-native"]
        if w["artifacts"]:
            cmd += ["--artifacts"] + w["artifacts"]
        cmd += ["--cmd"] + w["cmd"]

        print(f"== {w['name']} ({w['category']}) ==")
        rc = subprocess.run(cmd, check=False).returncode
        (ran if rc == 0 else failed).append(w["name"])
        print()

    print("=" * 60)
    print(f"Evaluation complete on {tag}: {len(ran)} ok, {len(failed)} failed.")
    if failed:
        print(f"  Failed: {', '.join(failed)}")
    print(f"Per-workload results in: {results_dir}")
    print("Aggregate across platforms with: python3 aggregate.py")
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\nEvaluation aborted.")
        sys.exit(130)
