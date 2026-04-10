#!/usr/bin/env python3
"""
OneWare Hybrid Strategy Benchmarking Suite
==========================================

High-precision benchmark driver for measuring the execution overhead of the
Hybrid Strategy Pattern (Native vs. Containerized execution). Supports three
backends: native host execution, Docker CLI (docker run), and the .NET
Docker.DotNet SDK harness.

Usage:
    python3 benchmarking_suite/benchmark.py --cmd <tool> [args...]
    python3 benchmarking_suite/benchmark.py --compare file_a.json file_b.json

Author: Mert Torun (TH Köln)
"""
import argparse
import logging
import math
import os
import shlex
import statistics
import subprocess
import sys
import time
import platform
import datetime
import json
from typing import Dict, List, Optional, Tuple

# ═════════════════════════════════════════════════════════════════════════════
#  Constants
# ═════════════════════════════════════════════════════════════════════════════

DOCKER_EXECUTABLE = "docker"
CONTAINER_WORKSPACE = "/workspace"
DEFAULT_IMAGE = "hdlc/ghdl:yosys"
HARNESS_DIR = "harness_bin"
HARNESS_BINARY = "ContainerBenchmarkHarness"
ENV_IMAGE_OVERRIDE = "ONEWARE_DOCKER_IMAGE"
OUTLIER_SIGMA = 2.0  # Flag iterations >2σ from the mean

# ═════════════════════════════════════════════════════════════════════════════
#  Logging
# ═════════════════════════════════════════════════════════════════════════════

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] %(levelname)s: %(message)s",
    datefmt="%H:%M:%S"
)

# ═════════════════════════════════════════════════════════════════════════════
#  System Information
# ═════════════════════════════════════════════════════════════════════════════

def get_docker_info() -> str:
    """Extracts Docker engine and host OS information for scientific comparison."""
    try:
        result = subprocess.run(
            [DOCKER_EXECUTABLE, "info", "--format", "{{.ServerVersion}} ({{.OperatingSystem}})"],
            capture_output=True, text=True, check=True, timeout=10
        )
        return result.stdout.strip()
    except Exception:
        return "Unknown"


def get_tool_version(cmd: List[str]) -> str:
    """Attempts to extract the version of the natively executed tool."""
    if not cmd:
        return "Unknown"
    tool = cmd[0]
    if tool in ("sh", "bash"):
        return "Shell Script"
    for flag in ("--version", "-v"):
        try:
            result = subprocess.run([tool, flag], capture_output=True, text=True, timeout=2)
            if result.returncode == 0 and result.stdout:
                return result.stdout.split('\n')[0].strip()
        except Exception:
            pass
    return "Unknown or Exe missing"

# ═════════════════════════════════════════════════════════════════════════════
#  Statistics
# ═════════════════════════════════════════════════════════════════════════════

def calculate_stats(times: List[float]) -> dict:
    """
    Calculates comprehensive statistical metrics for a list of execution times.
    Includes median, 95th percentile, and coefficient of variation for
    thesis-grade analysis.
    """
    if not times:
        return {"mean": 0.0, "stdev": 0.0, "min": 0.0, "max": 0.0,
                "median": 0.0, "p95": 0.0, "cv_percent": 0.0}

    n = len(times)
    mean = statistics.mean(times)
    stdev = statistics.stdev(times) if n > 1 else 0.0
    sorted_times = sorted(times)

    # 95th percentile via nearest-rank method
    p95_idx = min(math.ceil(0.95 * n) - 1, n - 1)
    p95 = sorted_times[p95_idx]

    # Coefficient of variation (relative standard deviation)
    cv = (stdev / mean * 100) if mean > 0 else 0.0

    return {
        "mean": mean,
        "stdev": stdev,
        "min": min(times),
        "max": max(times),
        "median": statistics.median(times),
        "p95": p95,
        "cv_percent": round(cv, 2)
    }


def detect_outliers(times: List[float]) -> List[int]:
    """
    Returns indices of iterations that are more than OUTLIER_SIGMA standard
    deviations from the mean. Useful for identifying cold-start effects.
    """
    if len(times) < 3:
        return []
    mean = statistics.mean(times)
    stdev = statistics.stdev(times)
    if stdev == 0:
        return []
    return [i for i, t in enumerate(times) if abs(t - mean) > OUTLIER_SIGMA * stdev]

# ═════════════════════════════════════════════════════════════════════════════
#  Command Execution
# ═════════════════════════════════════════════════════════════════════════════

def run_command(cmd: List[str], cwd: Optional[str] = None,
                timeout: Optional[int] = None) -> Tuple[float, Optional[int]]:
    """
    Executes a shell command and measures its execution time precisely.
    Returns (execution_time_seconds, return_code).
    If the executable is missing, returns (-1.0, None).
    """
    start_time = time.perf_counter()
    try:
        process = subprocess.run(
            cmd,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            cwd=cwd,
            check=False,
            timeout=timeout
        )
        end_time = time.perf_counter()
        return (end_time - start_time), process.returncode
    except subprocess.TimeoutExpired:
        end_time = time.perf_counter()
        logging.error(f"Iteration timed out after {timeout}s: {shlex.join(cmd)}")
        return (end_time - start_time), -1
    except FileNotFoundError:
        logging.error(f"Executable not found on the host system: {cmd[0]}")
        return -1.0, None
    except Exception as e:
        logging.error(f"Critical execution failure for command '{shlex.join(cmd)}': {e}")
        return -1.0, None

# ═════════════════════════════════════════════════════════════════════════════
#  Docker Pre-Pull
# ═════════════════════════════════════════════════════════════════════════════

def ensure_image_pulled(image: str, platform_flag: Optional[str] = None) -> None:
    """
    Ensures the Docker image is pulled before benchmarking begins.
    This prevents the first iteration from including pull time in its measurement.
    """
    cmd = [DOCKER_EXECUTABLE, "pull"]
    if platform_flag:
        cmd += ["--platform", platform_flag]
    cmd.append(image)

    logging.info(f"Pre-pulling image: {image}")
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
        if result.returncode == 0:
            logging.info(f"Image ready: {image}")
        else:
            logging.warning(f"Image pull returned non-zero ({result.returncode}). "
                            f"First iteration may include pull time.")
    except Exception as e:
        logging.warning(f"Image pre-pull failed: {e}. Proceeding anyway.")

# ═════════════════════════════════════════════════════════════════════════════
#  Benchmark Suite
# ═════════════════════════════════════════════════════════════════════════════

def perform_benchmark_suite(name: str, cmd: List[str], cwd: str,
                            iterations: int, warmup: int,
                            timeout: Optional[int] = None) -> Tuple[Optional[List[float]], Optional[List[int]]]:
    """
    Executes a benchmark configuration: warmup phase + measured iterations.
    Returns (execution_times, return_codes) for statistical analysis.
    """
    logging.info(f"--- Starting Suite: {name} ---")
    logging.info(f"Command: {shlex.join(cmd)}")

    # 1. Warmup Phase
    if warmup > 0:
        logging.info(f"Performing {warmup} warmup run(s)...")
        for i in range(warmup):
            exec_time, rc = run_command(cmd, cwd=cwd, timeout=timeout)
            if rc is None:
                logging.error(f"Suite '{name}' aborted during warmup. Executable missing.")
                return None, None
            if rc != 0:
                logging.warning(f"Warmup run {i+1} completed with non-zero exit code ({rc}).")

    # 2. Measured Iterations
    times: List[float] = []
    rcs: List[int] = []
    logging.info(f"Performing {iterations} measured iteration(s)...")
    for i in range(iterations):
        exec_time, rc = run_command(cmd, cwd=cwd, timeout=timeout)
        if rc is None:
            return None, None

        times.append(exec_time)
        rcs.append(rc)
        logging.info(f"  [{i+1}/{iterations}] {exec_time:.4f}s (RC: {rc})")

    if times:
        stats = calculate_stats(times)
        logging.info(f"Suite '{name}' completed. "
                     f"Mean: {stats['mean']:.4f}s ± {stats['stdev']:.4f}s | "
                     f"Median: {stats['median']:.4f}s | CV: {stats['cv_percent']:.1f}%")

        outliers = detect_outliers(times)
        if outliers:
            logging.warning(f"  ⚠️  Outlier iterations (>{OUTLIER_SIGMA}σ): "
                            f"{[i+1 for i in outliers]} — consider cold-start effects")

    return times, rcs

# ═════════════════════════════════════════════════════════════════════════════
#  Results Display
# ═════════════════════════════════════════════════════════════════════════════

def print_results(native_times: Optional[List[float]],
                  docker_cli_times: Optional[List[float]],
                  docker_dotnet_times: Optional[List[float]],
                  elapsed: float) -> None:
    """Prints final benchmark statistics in a professional, thesis-grade table."""
    print("\n\n======================================================================")
    print(" 📊 ONEWARE STUDIO - HYBRID STRATEGY BENCHMARK RESULTS")
    print("======================================================================")
    print(f"{'Metric':<25} | {'Native Execution':<20} | {'CLI Container':<20} | {'DotNet API':<20}")
    print("-" * 90)

    n_stats = calculate_stats(native_times) if native_times else None
    cli_stats = calculate_stats(docker_cli_times) if docker_cli_times else None
    dot_stats = calculate_stats(docker_dotnet_times) if docker_dotnet_times else None

    def fmt(stats, key):
        if not stats:
            return "N/A".center(20)
        val = stats[key]
        if key == "cv_percent":
            return f"{val:>8.1f}%".center(20)
        prefix = "±" if key == "stdev" else ""
        return f"{prefix}{val:>9.4f}s".center(20)

    for label, key in [("Mean Time (Avg)", "mean"),
                       ("Median", "median"),
                       ("Standard Deviation", "stdev"),
                       ("95th Percentile", "p95"),
                       ("CV (Rel. StdDev)", "cv_percent"),
                       ("Min Time (Fastest)", "min"),
                       ("Max Time (Slowest)", "max")]:
        print(f"{label:<25} | {fmt(n_stats, key)} | {fmt(cli_stats, key)} | {fmt(dot_stats, key)}")

    print("-" * 90)
    print(f"  Total wall-clock time: {elapsed:.1f}s")
    print("======================================================================\n")

# ═════════════════════════════════════════════════════════════════════════════
#  Comparison Mode
# ═════════════════════════════════════════════════════════════════════════════

def compare_results(file_a: str, file_b: str) -> None:
    """
    Loads two JSON benchmark result files and prints a side-by-side
    regression/improvement table with delta percentages.
    """
    def load_json(path: str) -> dict:
        with open(path) as f:
            return json.load(f)

    a = load_json(file_a)
    b = load_json(file_b)

    print("\n======================================================================")
    print(" 🔬 BENCHMARK COMPARISON REPORT")
    print("======================================================================")
    print(f"  File A: {os.path.basename(file_a)}")
    print(f"  File B: {os.path.basename(file_b)}")
    print("----------------------------------------------------------------------")

    categories = set(a.get("results", {}).keys()) | set(b.get("results", {}).keys())
    categories.discard("comparison")

    for cat in sorted(categories):
        stats_a = a.get("results", {}).get(cat, {}).get("statistics")
        stats_b = b.get("results", {}).get(cat, {}).get("statistics")
        if not stats_a and not stats_b:
            continue

        label = cat.replace("_", " ").title()
        print(f"\n  📊 {label}:")
        print(f"  {'Metric':<20} | {'File A':>12} | {'File B':>12} | {'Delta':>10} | {'Change':>8}")
        print(f"  {'-'*20}-+-{'-'*12}-+-{'-'*12}-+-{'-'*10}-+-{'-'*8}")

        for metric in ["mean", "median", "stdev", "p95", "min", "max"]:
            val_a = stats_a.get(metric) if stats_a else None
            val_b = stats_b.get(metric) if stats_b else None

            # Skip metrics not present in older result files
            if val_a is None and val_b is None:
                continue

            str_a = f"{val_a:>10.4f}s" if val_a is not None else "N/A".center(12)
            str_b = f"{val_b:>10.4f}s" if val_b is not None else "N/A".center(12)

            if val_a is not None and val_b is not None and val_a > 0:
                delta = val_b - val_a
                pct = ((val_b / val_a) - 1.0) * 100
                sign = "+" if delta >= 0 else ""
                symbol = "⬆️ slower" if delta > 0.0001 else ("⬇️ faster" if delta < -0.0001 else "  ≈ same")
                str_delta = f"{sign}{delta:>8.4f}s"
                str_pct = f"{sign}{pct:.1f}%"
            else:
                str_delta = "N/A".center(10)
                str_pct = "N/A".center(8)
                symbol = ""

            print(f"  {metric:<20} | {str_a} | {str_b} | {str_delta} | {str_pct} {symbol}")

    print("\n======================================================================\n")

# ═════════════════════════════════════════════════════════════════════════════
#  JSON Export
# ═════════════════════════════════════════════════════════════════════════════

def export_json(args, native_times: Optional[List[float]],
                native_rcs: Optional[List[int]],
                docker_results: Dict[str, Dict],
                native_cmd: List[str]) -> None:
    """Exports benchmark results to a JSON file with full system metadata."""
    if not args.output:
        return

    data = {
        "timestamp": datetime.datetime.now().isoformat(),
        "system": {
            "os": platform.system(),
            "release": platform.release(),
            "machine": platform.machine(),
            "processor": platform.processor(),
            "docker_engine": get_docker_info()
        },
        "experiment": {
            "workload": shlex.join(native_cmd),
            "native_tool_version": get_tool_version(native_cmd),
            "image": args.image,
            "backend": args.backend,
            "iterations": args.iterations,
            "warmup": args.warmup
        },
        "results": {
            "native": {
                "times": native_times or [],
                "return_codes": native_rcs or [],
                "statistics": calculate_stats(native_times) if native_times else None
            }
        }
    }

    # Inject Docker results — use "docker" key for CLI backend to maintain
    # backward compatibility with stored thesis results (picorv32.json, neorv32.json)
    for backend, result in docker_results.items():
        key = "docker" if backend == "cli" else f"docker_{backend}"
        times = result.get("times") or []
        data["results"][key] = {
            "times": times,
            "return_codes": result.get("rcs") or [],
            "statistics": calculate_stats(times) if times else None
        }

    # Comparison block: native vs. primary Docker backend
    primary_backend = "cli" if "cli" in docker_results else next(iter(docker_results), None)
    if primary_backend and native_times:
        primary_times = (docker_results.get(primary_backend, {}).get("times") or [])
        if primary_times:
            n_mean = calculate_stats(native_times)["mean"]
            d_mean = calculate_stats(primary_times)["mean"]
            if n_mean > 0:
                data["results"]["comparison"] = {
                    "absolute_overhead_sec": round(d_mean - n_mean, 6),
                    "relative_overhead_x": round(d_mean / n_mean, 4)
                }

    try:
        out_path = os.path.abspath(args.output)
        os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
        with open(out_path, 'w') as f:
            json.dump(data, f, indent=4)
        logging.info(f"Detailed thesis metrics exported to: {out_path}")
    except OSError as e:
        logging.error(f"Failed to export results: {e}")

# ═════════════════════════════════════════════════════════════════════════════
#  CLI Arguments
# ═════════════════════════════════════════════════════════════════════════════

def parse_arguments():
    parser = argparse.ArgumentParser(
        description="High-Precision Benchmark Utility for OneWare Hybrid Strategy (Native vs. Containerized).",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter
    )
    parser.add_argument("--image", default=DEFAULT_IMAGE,
                        help="Docker base image for containerized execution.")
    parser.add_argument("--backend", choices=["cli", "dotnet", "all"], default="cli",
                        help="Container backend: 'cli' (docker run), 'dotnet' (Docker.DotNet API), 'all' (both).")
    parser.add_argument("--cmd", nargs=argparse.REMAINDER,
                        help="Command to benchmark. Must be the last argument.")
    parser.add_argument("--iterations", type=int, default=10,
                        help="Number of measured benchmark iterations.")
    parser.add_argument("--warmup", type=int, default=2,
                        help="Number of unrecorded warmup runs to populate caches.")
    parser.add_argument("--timeout", type=int, default=None,
                        help="Per-iteration timeout in seconds. Prevents hanging benchmarks.")
    parser.add_argument("--verbose", action="store_true",
                        help="Enable verbose debug logging.")
    parser.add_argument("--output", type=str,
                        help="Export benchmark results to a JSON file.")
    parser.add_argument("--workspace", type=str, default=".",
                        help="Working directory for command execution.")
    parser.add_argument("--platform", type=str, default=None,
                        help="Force Docker platform (e.g., linux/amd64).")
    parser.add_argument("--skip-native", action="store_true",
                        help="Skip native execution benchmark.")
    parser.add_argument("--skip-pull", action="store_true",
                        help="Skip Docker image pre-pull before benchmarking.")
    parser.add_argument("--dry-run", action="store_true",
                        help="Print the commands that would be executed without running them.")
    parser.add_argument("--compare", nargs=2, metavar=("FILE_A", "FILE_B"),
                        help="Compare two JSON result files (does not run benchmarks).")
    return parser.parse_args()

# ═════════════════════════════════════════════════════════════════════════════
#  Docker Command Builder
# ═════════════════════════════════════════════════════════════════════════════

def build_docker_cli_cmd(args, workspace_dir: str, native_cmd: List[str]) -> List[str]:
    """Constructs the `docker run` command for the CLI backend."""
    cmd = [DOCKER_EXECUTABLE, "run", "--rm"]
    if args.platform:
        cmd += ["--platform", args.platform]
    cmd += ["-v", f"{workspace_dir}:{CONTAINER_WORKSPACE}",
            "-w", CONTAINER_WORKSPACE,
            args.image]
    cmd += native_cmd
    return cmd


def build_dotnet_cmd(native_cmd: List[str]) -> List[str]:
    """Constructs the harness command for the DotNet backend."""
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    harness_exe = os.path.join(repo_root, "benchmarking_suite", HARNESS_DIR, HARNESS_BINARY)
    if platform.system() == "Windows":
        harness_exe += ".exe"
    if not os.path.exists(harness_exe):
        logging.error(f"⚡ DotNet Backend Error: Executable not found at {harness_exe}")
        logging.error("Run: dotnet publish src/ContainerBenchmarkHarness -c Release "
                       f"-o benchmarking_suite/{HARNESS_DIR}")
        sys.exit(1)
    return [harness_exe] + native_cmd

# ═════════════════════════════════════════════════════════════════════════════
#  Main
# ═════════════════════════════════════════════════════════════════════════════

def main():
    args = parse_arguments()
    if args.verbose:
        logging.getLogger().setLevel(logging.DEBUG)

    # Compare mode — load two JSON files and exit
    if args.compare:
        compare_results(args.compare[0], args.compare[1])
        return

    if not args.cmd:
        print("Error: --cmd is required when not using --compare.", file=sys.stderr)
        sys.exit(1)

    workspace_dir = os.path.abspath(args.workspace)
    native_cmd = args.cmd

    # Determine backends
    backends = ["cli", "dotnet"] if args.backend == "all" else [args.backend]

    # Build all commands first (for dry-run and logging)
    backend_cmds: Dict[str, List[str]] = {}
    for backend in backends:
        if backend == "cli":
            backend_cmds[backend] = build_docker_cli_cmd(args, workspace_dir, native_cmd)
        elif backend == "dotnet":
            os.environ[ENV_IMAGE_OVERRIDE] = args.image
            backend_cmds[backend] = build_dotnet_cmd(native_cmd)

    # Dry-run mode — print commands and exit
    if args.dry_run:
        print("\n🔍 Dry-run mode — commands that would be executed:\n")
        if not args.skip_native:
            print(f"  Native:  {shlex.join(native_cmd)}")
        for backend, cmd in backend_cmds.items():
            print(f"  {backend.upper():7s}: {shlex.join(cmd)}")
        print(f"\n  Working directory: {workspace_dir}")
        print(f"  Iterations: {args.iterations} (warmup: {args.warmup})")
        if args.timeout:
            print(f"  Timeout: {args.timeout}s per iteration")
        return

    logging.info("Initializing Benchmark Protocol...")
    logging.info(f"Target Workload: {shlex.join(native_cmd)}")
    logging.info(f"Target Image:    {args.image}")
    logging.info(f"Working Dir:     {workspace_dir}")
    if args.timeout:
        logging.info(f"Timeout:         {args.timeout}s per iteration")

    wall_start = time.perf_counter()

    # Docker image pre-pull (prevents first iteration from including pull time)
    if not args.skip_pull and "cli" in backends:
        ensure_image_pulled(args.image, args.platform)

    # 1. Native Execution
    native_times: Optional[List[float]] = None
    native_rcs: Optional[List[int]] = None
    if not args.skip_native:
        native_times, native_rcs = perform_benchmark_suite(
            "Native Execution", native_cmd, cwd=workspace_dir,
            warmup=args.warmup, iterations=args.iterations, timeout=args.timeout
        )
        if not native_times:
            logging.warning("Native execution failed or not found. Proceeding with containerized benchmarks only.")

    # 2. Containerized Execution (all requested backends)
    docker_results: Dict[str, Dict] = {}
    for backend in backends:
        times, rcs = perform_benchmark_suite(
            f"Containerized Execution ({backend.upper()})",
            backend_cmds[backend], cwd=workspace_dir,
            warmup=args.warmup, iterations=args.iterations, timeout=args.timeout
        )
        docker_results[backend] = {"times": times, "rcs": rcs}

    wall_elapsed = time.perf_counter() - wall_start

    # Print results and export
    print_results(
        native_times,
        docker_results.get("cli", {}).get("times"),
        docker_results.get("dotnet", {}).get("times"),
        wall_elapsed
    )
    export_json(args, native_times, native_rcs, docker_results, native_cmd)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nBenchmark aborted by user.")
        sys.exit(130)
