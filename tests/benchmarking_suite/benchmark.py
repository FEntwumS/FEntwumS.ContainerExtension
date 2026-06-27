#!/usr/bin/env python3
"""
OneWare Hybrid Strategy Benchmarking Suite

Benchmark driver for measuring the execution overhead of the Hybrid Strategy
Pattern (native vs. containerized execution) and the reproducibility of tool
outputs across machines. Supports three execution backends: native host
execution, Docker CLI (``docker run``), and the .NET ``DockerExecutionStrategy``
harness (the real OneWare code path).

Statistical treatment:
  * 95 % confidence intervals (Student-t) on every mean.
  * Welch's unpaired t-test and an optional paired t-test (interleaved mode),
    with two-sided p-values and Cohen's d effect size — all pure-stdlib.
  * CV-adaptive repetition (keep sampling until the relative standard deviation
    is below a target or a cap is reached).
  * Full, reproducible environment capture (CPU model, cores, RAM, OS/arch,
    engine version, image digest, input-file SHA-256, harness git commit).
  * Separated image-pull cost measurement.
  * Native peak resident memory (POSIX).
  * Output-determinism mode: SHA-256 of produced artifacts, for cross-machine
    reproducibility comparison.

Usage:
    python3 benchmarking_suite/benchmark.py --cmd <tool> [args...]
    python3 benchmarking_suite/benchmark.py --compare file_a.json file_b.json

Author: Mert Torun (TH Koeln)
"""
import argparse
import datetime
import hashlib
import json
import logging
import math
import os
import platform
import re
import shlex
import statistics
import subprocess
import sys
import time
from typing import Dict, List, Optional, Sequence, Tuple

try:
    import resource  # POSIX only; used for native peak-RSS capture.
except ImportError:  # pragma: no cover - Windows
    resource = None

#  Constants

DOCKER_EXECUTABLE = "docker"
CONTAINER_WORKSPACE = "/workspace"
DEFAULT_IMAGE = "hdlc/ghdl:yosys"
HARNESS_DIR = "harness_bin"
HARNESS_BINARY = "ContainerBenchmarkHarness"
ENV_IMAGE_OVERRIDE = "ONEWARE_DOCKER_IMAGE"
OUTLIER_SIGMA = 2.0  # Flag iterations >2 sigma from the mean.
SCHEMA_VERSION = 2

#  Logging

logging.basicConfig(
    level=logging.INFO,
    format="[%(asctime)s] %(levelname)s: %(message)s",
    datefmt="%H:%M:%S",
)

#  Statistics — confidence intervals, hypothesis tests, effect size

# Two-sided 95 % Student-t critical values, t_{0.975, df}. Exact for df<=30,
# then a sparse table; for a df between table points we step to the nearest
# lower entry (slightly conservative — wider interval).
_T95 = {
    1: 12.706, 2: 4.303, 3: 3.182, 4: 2.776, 5: 2.571, 6: 2.447, 7: 2.365,
    8: 2.306, 9: 2.262, 10: 2.228, 11: 2.201, 12: 2.179, 13: 2.160, 14: 2.145,
    15: 2.131, 16: 2.120, 17: 2.110, 18: 2.101, 19: 2.093, 20: 2.086, 21: 2.080,
    22: 2.074, 23: 2.069, 24: 2.064, 25: 2.060, 26: 2.056, 27: 2.052, 28: 2.048,
    29: 2.045, 30: 2.042, 40: 2.021, 50: 2.009, 60: 2.000, 80: 1.990, 100: 1.984,
    120: 1.980,
}
_T95_BRACKETS = sorted(_T95)


def t_critical_95(df: float) -> float:
    """Two-sided 95 % Student-t critical value for ``df`` degrees of freedom."""
    if df <= 0:
        return float("nan")
    d = int(math.floor(df))
    if d in _T95:
        return _T95[d]
    if d >= 120:
        return 1.960  # asymptotic z_{0.975}
    lower = max(b for b in _T95_BRACKETS if b <= d)
    return _T95[lower]


def _betacf(a: float, b: float, x: float) -> float:
    """Continued-fraction expansion for the incomplete beta (Numerical Recipes)."""
    MAXIT, EPS, FPMIN = 200, 3.0e-12, 1.0e-300
    qab, qap, qam = a + b, a + 1.0, a - 1.0
    c = 1.0
    d = 1.0 - qab * x / qap
    if abs(d) < FPMIN:
        d = FPMIN
    d = 1.0 / d
    h = d
    for m in range(1, MAXIT + 1):
        m2 = 2 * m
        aa = m * (b - m) * x / ((qam + m2) * (a + m2))
        d = 1.0 + aa * d
        if abs(d) < FPMIN:
            d = FPMIN
        c = 1.0 + aa / c
        if abs(c) < FPMIN:
            c = FPMIN
        d = 1.0 / d
        h *= d * c
        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2))
        d = 1.0 + aa * d
        if abs(d) < FPMIN:
            d = FPMIN
        c = 1.0 + aa / c
        if abs(c) < FPMIN:
            c = FPMIN
        d = 1.0 / d
        delta = d * c
        h *= delta
        if abs(delta - 1.0) < EPS:
            break
    return h


def _betai(a: float, b: float, x: float) -> float:
    """Regularised incomplete beta function I_x(a, b)."""
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    lbeta = math.lgamma(a + b) - math.lgamma(a) - math.lgamma(b)
    bt = math.exp(lbeta + a * math.log(x) + b * math.log(1.0 - x))
    if x < (a + 1.0) / (a + b + 2.0):
        return bt * _betacf(a, b, x) / a
    return 1.0 - bt * _betacf(b, a, 1.0 - x) / b


def student_t_two_sided_p(t: float, df: float) -> float:
    """Two-sided p-value for a Student-t statistic with ``df`` degrees of freedom."""
    if df <= 0 or math.isnan(t):
        return float("nan")
    x = df / (df + t * t)
    return _betai(df / 2.0, 0.5, x)


def calculate_stats(times: Sequence[float]) -> Optional[dict]:
    """Descriptive statistics plus a 95 % confidence interval on the mean."""
    if not times:
        return None
    n = len(times)
    mean = statistics.mean(times)
    stdev = statistics.stdev(times) if n > 1 else 0.0
    sem = stdev / math.sqrt(n) if n > 1 else 0.0
    half = t_critical_95(n - 1) * sem if n > 1 else 0.0
    srt = sorted(times)
    p95 = srt[min(math.ceil(0.95 * n) - 1, n - 1)]
    cv = (stdev / mean * 100.0) if mean > 0 else 0.0
    return {
        "n": n,
        "mean": mean,
        "stdev": stdev,
        "sem": sem,
        "ci95_halfwidth": half,
        "ci95_low": mean - half,
        "ci95_high": mean + half,
        "min": min(times),
        "max": max(times),
        "median": statistics.median(times),
        "p95": p95,
        "cv_percent": round(cv, 2),
    }


def welch_t_test(a: Sequence[float], b: Sequence[float]) -> Optional[dict]:
    """Welch's unpaired t-test (unequal variances) comparing two samples a, b."""
    if len(a) < 2 or len(b) < 2:
        return None
    na, nb = len(a), len(b)
    ma, mb = statistics.mean(a), statistics.mean(b)
    va, vb = statistics.variance(a), statistics.variance(b)
    if va == 0 and vb == 0:
        return None
    se = math.sqrt(va / na + vb / nb)
    t = (mb - ma) / se if se > 0 else float("nan")
    df = (va / na + vb / nb) ** 2 / (
        (va / na) ** 2 / (na - 1) + (vb / nb) ** 2 / (nb - 1)
    )
    diff = mb - ma
    half = t_critical_95(df) * se
    return {
        "test": "welch_unpaired",
        "t": t,
        "df": df,
        "p_value": student_t_two_sided_p(t, df),
        "mean_diff": diff,
        "mean_diff_ci95_low": diff - half,
        "mean_diff_ci95_high": diff + half,
        "significant_0_05": bool(student_t_two_sided_p(t, df) < 0.05),
    }


def paired_t_test(a: Sequence[float], b: Sequence[float]) -> Optional[dict]:
    """Paired t-test on per-iteration differences (requires equal, aligned n)."""
    if len(a) != len(b) or len(a) < 2:
        return None
    diffs = [bi - ai for ai, bi in zip(a, b)]
    n = len(diffs)
    md = statistics.mean(diffs)
    sd = statistics.stdev(diffs)
    if sd == 0:
        return None
    se = sd / math.sqrt(n)
    t = md / se
    df = n - 1
    half = t_critical_95(df) * se
    p = student_t_two_sided_p(t, df)
    return {
        "test": "paired",
        "t": t,
        "df": df,
        "p_value": p,
        "mean_diff": md,
        "mean_diff_ci95_low": md - half,
        "mean_diff_ci95_high": md + half,
        "significant_0_05": bool(p < 0.05),
    }


def cohens_d(a: Sequence[float], b: Sequence[float]) -> Optional[float]:
    """Cohen's d effect size using the pooled standard deviation."""
    if len(a) < 2 or len(b) < 2:
        return None
    na, nb = len(a), len(b)
    va, vb = statistics.variance(a), statistics.variance(b)
    pooled = math.sqrt(((na - 1) * va + (nb - 1) * vb) / (na + nb - 2))
    if pooled == 0:
        return None
    return (statistics.mean(b) - statistics.mean(a)) / pooled


def detect_outliers(times: Sequence[float], sigma: float = OUTLIER_SIGMA) -> List[int]:
    """Indices of iterations more than ``sigma`` standard deviations from the mean."""
    if len(times) < 3:
        return []
    mean = statistics.mean(times)
    stdev = statistics.stdev(times)
    if stdev < 1e-9:
        return []
    return [i for i, t in enumerate(times) if abs(t - mean) > sigma * stdev]


#  Environment capture (cross-platform, reproducibility metadata)

def _run(cmd: List[str], timeout: int = 10) -> str:
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, check=False)
        return r.stdout.strip() if r.returncode == 0 else ""
    except Exception:
        return ""


def get_cpu_model() -> str:
    sysname = platform.system()
    if sysname == "Darwin":
        v = _run(["sysctl", "-n", "machdep.cpu.brand_string"])
        if v:
            return v
    elif sysname == "Linux":
        try:
            with open("/proc/cpuinfo", encoding="utf-8") as f:
                for line in f:
                    if line.startswith("model name"):
                        return line.split(":", 1)[1].strip()
        except OSError:
            pass
    elif sysname == "Windows":
        v = os.environ.get("PROCESSOR_IDENTIFIER", "")
        if v:
            return v
    return platform.processor() or "Unknown"


def get_total_ram_gb() -> Optional[float]:
    sysname = platform.system()
    try:
        if sysname == "Darwin":
            v = _run(["sysctl", "-n", "hw.memsize"])
            return round(int(v) / (1024 ** 3), 2) if v else None
        if sysname == "Linux":
            with open("/proc/meminfo", encoding="utf-8") as f:
                for line in f:
                    if line.startswith("MemTotal"):
                        kb = int(re.findall(r"\d+", line)[0])
                        return round(kb / (1024 ** 2), 2)
        if sysname == "Windows":
            import ctypes

            class MEMORYSTATUSEX(ctypes.Structure):
                _fields_ = [("dwLength", ctypes.c_ulong),
                            ("dwMemoryLoad", ctypes.c_ulong),
                            ("ullTotalPhys", ctypes.c_ulonglong),
                            ("ullAvailPhys", ctypes.c_ulonglong),
                            ("ullTotalPageFile", ctypes.c_ulonglong),
                            ("ullAvailPageFile", ctypes.c_ulonglong),
                            ("ullTotalVirtual", ctypes.c_ulonglong),
                            ("ullAvailVirtual", ctypes.c_ulonglong),
                            ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
            stat = MEMORYSTATUSEX()
            stat.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
            ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat))
            return round(stat.ullTotalPhys / (1024 ** 3), 2)
    except Exception:
        return None
    return None


def get_docker_info() -> Dict[str, str]:
    out = _run([DOCKER_EXECUTABLE, "info", "--format",
                "{{.ServerVersion}}|{{.OperatingSystem}}|{{.Driver}}|{{.Architecture}}"])
    parts = out.split("|") if out else []
    return {
        "server_version": parts[0] if len(parts) > 0 else "Unknown",
        "operating_system": parts[1] if len(parts) > 1 else "Unknown",
        "storage_driver": parts[2] if len(parts) > 2 else "Unknown",
        "architecture": parts[3] if len(parts) > 3 else "Unknown",
    }


def get_image_digest(image: str) -> Optional[str]:
    digest = _run([DOCKER_EXECUTABLE, "image", "inspect", "--format",
                   "{{index .RepoDigests 0}}", image])
    if digest:
        return digest
    return _run([DOCKER_EXECUTABLE, "image", "inspect", "--format", "{{.Id}}", image]) or None


def get_git_commit() -> Optional[str]:
    here = os.path.dirname(os.path.abspath(__file__))
    return _run(["git", "-C", here, "rev-parse", "--short=12", "HEAD"]) or None


def get_tool_version(cmd: Sequence[str]) -> str:
    if not cmd:
        return "Unknown"
    tool = cmd[0]
    if tool in ("sh", "bash"):
        return "Shell Script"
    for flag in ("--version", "-V", "-v"):
        try:
            r = subprocess.run([tool, flag], capture_output=True, text=True, timeout=3, check=False)
            if r.returncode == 0 and r.stdout:
                return r.stdout.split("\n")[0].strip()
        except Exception:
            pass
    return "Unknown or executable missing"


def sha256_file(path: str) -> Optional[str]:
    try:
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 16), b""):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


def hash_input_files(cmd: Sequence[str], cwd: str) -> Dict[str, str]:
    """SHA-256 every argument that resolves to an existing file (workload inputs)."""
    hashes: Dict[str, str] = {}
    for arg in cmd:
        candidate = arg if os.path.isabs(arg) else os.path.join(cwd, arg)
        if os.path.isfile(candidate):
            digest = sha256_file(candidate)
            if digest:
                hashes[arg] = digest
    return hashes


def hash_artifacts(paths: Sequence[str], cwd: str) -> Dict[str, Optional[str]]:
    """SHA-256 the named output artifacts after a run (output-determinism)."""
    out: Dict[str, Optional[str]] = {}
    for p in paths:
        candidate = p if os.path.isabs(p) else os.path.join(cwd, p)
        out[p] = sha256_file(candidate)
    return out


#  Command execution

def _max_rss_children_bytes() -> Optional[int]:
    if resource is None:
        return None
    rss = resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss
    # Linux reports kibibytes; macOS/BSD report bytes.
    return rss * 1024 if platform.system() == "Linux" else rss


def run_command(cmd: Sequence[str], cwd: Optional[str] = None,
                timeout: Optional[int] = None) -> Tuple[float, Optional[int]]:
    """Execute a command, returning (wall_seconds, return_code). (-1.0, None) if missing."""
    start = time.perf_counter()
    try:
        proc = subprocess.run(list(cmd), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              cwd=cwd, check=False, timeout=timeout)
        return time.perf_counter() - start, proc.returncode
    except subprocess.TimeoutExpired:
        logging.error("Iteration timed out after %ss: %s", timeout, shlex.join(cmd))
        return time.perf_counter() - start, -1
    except FileNotFoundError:
        logging.error("Executable not found on the host: %s", cmd[0])
        return -1.0, None
    except Exception as exc:  # noqa: BLE001
        logging.error("Execution failure for '%s': %s", shlex.join(cmd), exc)
        return -1.0, None


def measure_pull_cost(image: str, platform_flag: Optional[str], timeout: int) -> Optional[dict]:
    """Remove the image then time a cold pull, to separate pull cost from run cost."""
    logging.info("Measuring cold image-pull cost for %s ...", image)
    subprocess.run([DOCKER_EXECUTABLE, "image", "rm", "-f", image],
                   capture_output=True, text=True, check=False)
    cmd = [DOCKER_EXECUTABLE, "pull"]
    if platform_flag:
        cmd += ["--platform", platform_flag]
    cmd.append(image)
    start = time.perf_counter()
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, check=False)
    elapsed = time.perf_counter() - start
    if r.returncode != 0:
        logging.warning("Cold pull failed (%s); pull-cost not recorded.", r.returncode)
        return None
    size = _run([DOCKER_EXECUTABLE, "image", "inspect", "--format", "{{.Size}}", image])
    return {
        "pull_seconds": round(elapsed, 4),
        "image_size_bytes": int(size) if size.isdigit() else None,
        "platform": platform_flag,
    }


def ensure_image_pulled(image: str, platform_flag: Optional[str] = None, timeout: int = 300) -> None:
    cmd = [DOCKER_EXECUTABLE, "pull"]
    if platform_flag:
        cmd += ["--platform", platform_flag]
    cmd.append(image)
    logging.info("Pre-pulling image: %s", image)
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout, check=False)
        if r.returncode == 0:
            logging.info("Image ready: %s", image)
        else:
            logging.warning("Image pull returned %s: %s. First iteration may include pull time.",
                            r.returncode, (r.stderr or "").strip())
    except Exception as exc:  # noqa: BLE001
        logging.warning("Image pre-pull failed: %s. Proceeding anyway.", exc)


#  Benchmark suite (warmup, measured iterations, CV-adaptive escalation)

def perform_benchmark_suite(name: str, cmd: Sequence[str], cwd: str, *,
                            iterations: int, warmup: int,
                            timeout: Optional[int] = None,
                            outlier_sigma: float = OUTLIER_SIGMA,
                            target_cv: Optional[float] = None,
                            max_iterations: Optional[int] = None
                            ) -> Tuple[Optional[List[float]], Optional[List[int]], Optional[int]]:
    """Warmup + measured iterations. Returns (times, return_codes, native_peak_rss_bytes)."""
    logging.info("--- Suite: %s ---", name)
    logging.info("Command: %s", shlex.join(cmd))

    if warmup > 0:
        logging.info("Warmup: %s run(s)...", warmup)
        for i in range(warmup):
            _, rc = run_command(cmd, cwd=cwd, timeout=timeout)
            if rc is None:
                logging.error("Suite '%s' aborted during warmup (executable missing).", name)
                return None, None, None
            if rc != 0:
                logging.warning("Warmup run %s exited non-zero (%s).", i + 1, rc)

    rss_before = _max_rss_children_bytes()
    times: List[float] = []
    rcs: List[int] = []
    cap = max_iterations if (max_iterations and target_cv) else iterations

    logging.info("Measuring %s iteration(s)%s...", iterations,
                 f" (CV-adaptive up to {cap}, target {target_cv}%)" if (target_cv and cap > iterations) else "")
    i = 0
    while i < cap:
        exec_time, rc = run_command(cmd, cwd=cwd, timeout=timeout)
        if rc is None:
            return None, None, None
        times.append(exec_time)
        rcs.append(rc)
        i += 1
        logging.info("  [%s/%s] %.4fs (rc=%s)", i, cap if (target_cv and cap > iterations) else iterations, exec_time, rc)
        if i >= iterations and target_cv is not None:
            s = calculate_stats(times)
            if s and s["cv_percent"] <= target_cv:
                break

    rss_after = _max_rss_children_bytes()
    peak_rss = rss_after if (rss_after is not None and (rss_before is None or rss_after >= rss_before)) else rss_before

    stats = calculate_stats(times)
    if stats:
        logging.info("Suite '%s' done. mean=%.4fs +/- %.4f (95%% CI [%.4f, %.4f]) | median=%.4f | CV=%.1f%% | n=%d",
                     name, stats["mean"], stats["ci95_halfwidth"], stats["ci95_low"], stats["ci95_high"],
                     stats["median"], stats["cv_percent"], stats["n"])
        outliers = detect_outliers(times, outlier_sigma)
        if outliers:
            logging.warning("  Outliers (>%s sigma): %s", outlier_sigma, [o + 1 for o in outliers])
    return times, rcs, peak_rss


def perform_interleaved_suite(modes: Dict[str, List[str]], cwd: str, *,
                              iterations: int, warmup: int, timeout: Optional[int]
                              ) -> Dict[str, Tuple[List[float], List[int]]]:
    """Run modes alternately per iteration so a paired comparison is valid."""
    order = list(modes)
    if warmup > 0:
        logging.info("Interleaved warmup: %s round(s) across %s...", warmup, ", ".join(order))
        for _ in range(warmup):
            for key in order:
                run_command(modes[key], cwd=cwd, timeout=timeout)
    out: Dict[str, Tuple[List[float], List[int]]] = {k: ([], []) for k in order}
    logging.info("Interleaved measurement: %s iteration(s) across %s...", iterations, ", ".join(order))
    for i in range(iterations):
        for key in order:
            t, rc = run_command(modes[key], cwd=cwd, timeout=timeout)
            if rc is None:
                logging.error("Interleaved suite aborted: '%s' executable missing.", key)
                return out
            out[key][0].append(t)
            out[key][1].append(rc)
        logging.info("  round [%s/%s] done", i + 1, iterations)
    return out


#  Results display

def _fmt(stats: Optional[dict], key: str) -> str:
    if not stats or stats.get(key) is None:
        return "N/A".center(20)
    val = stats[key]
    if key == "cv_percent":
        return f"{val:>8.1f}%".center(20)
    if key == "n":
        return f"{val}".center(20)
    return f"{val:>9.4f}s".center(20)


def print_results(native: Optional[List[float]], cli: Optional[List[float]],
                  dotnet: Optional[List[float]], elapsed: float) -> None:
    print("\n\n======================================================================")
    print(" OneWare Studio - Hybrid Strategy Benchmark Results")
    print("======================================================================")
    print(f"{'Metric':<25} | {'Native':<20} | {'CLI Container':<20} | {'DotNet (strategy)':<20}")
    print("-" * 92)
    ns, cs, ds = calculate_stats(native), calculate_stats(cli), calculate_stats(dotnet)
    for label, key in [("Samples (n)", "n"), ("Mean", "mean"), ("95% CI half-width", "ci95_halfwidth"),
                       ("Median", "median"), ("Std. deviation", "stdev"), ("95th percentile", "p95"),
                       ("CV (rel. std.dev)", "cv_percent"), ("Min", "min"), ("Max", "max")]:
        print(f"{label:<25} | {_fmt(ns, key)} | {_fmt(cs, key)} | {_fmt(ds, key)}")
    print("-" * 92)
    for label, sample in [("Native vs CLI", cli), ("Native vs DotNet", dotnet)]:
        if native and sample:
            w = welch_t_test(native, sample)
            d = cohens_d(native, sample)
            if w:
                print(f"  {label}: overhead {w['mean_diff']:+.4f}s "
                      f"(95% CI [{w['mean_diff_ci95_low']:+.4f}, {w['mean_diff_ci95_high']:+.4f}]), "
                      f"Welch p={w['p_value']:.4g}, Cohen's d={d:.2f}" if d is not None else "")
    print(f"  Total wall-clock time: {elapsed:.1f}s")
    print("======================================================================\n")


#  Comparison mode

def compare_results(file_a: str, file_b: str) -> None:
    def load(path: str) -> dict:
        try:
            with open(path, encoding="utf-8") as f:
                return json.load(f)
        except FileNotFoundError:
            print(f"Error: File not found: '{path}'", file=sys.stderr)
            sys.exit(1)

    a, b = load(file_a), load(file_b)
    print("\n======================================================================")
    print(" Benchmark Comparison Report")
    print("======================================================================")
    print(f"  File A: {os.path.basename(file_a)}")
    print(f"  File B: {os.path.basename(file_b)}")
    cats = (set(a.get("results", {})) | set(b.get("results", {}))) - {"comparison"}
    for cat in sorted(cats):
        sa = a.get("results", {}).get(cat, {}).get("statistics")
        sb = b.get("results", {}).get(cat, {}).get("statistics")
        if not sa and not sb:
            continue
        print(f"\n  {cat.replace('_', ' ').title()}:")
        print(f"  {'Metric':<18} | {'File A':>12} | {'File B':>12} | {'Delta':>10} | {'Change':>8}")
        print(f"  {'-'*18}-+-{'-'*12}-+-{'-'*12}-+-{'-'*10}-+-{'-'*8}")
        for metric in ["mean", "median", "stdev", "p95", "min", "max"]:
            va = sa.get(metric) if sa else None
            vb = sb.get(metric) if sb else None
            if va is None and vb is None:
                continue
            sa_s = f"{va:>10.4f}s" if va is not None else "N/A".center(12)
            sb_s = f"{vb:>10.4f}s" if vb is not None else "N/A".center(12)
            if va is not None and vb is not None and va > 0:
                delta = vb - va
                pct = (vb / va - 1.0) * 100
                tag = "slower" if delta > 1e-4 else ("faster" if delta < -1e-4 else "same")
                print(f"  {metric:<18} | {sa_s} | {sb_s} | {delta:>+9.4f}s | {pct:>+6.1f}% {tag}")
            else:
                print(f"  {metric:<18} | {sa_s} | {sb_s} | {'N/A':>10} | {'N/A':>8}")
    print("\n======================================================================\n")


#  JSON export

def export_json(args, native_times, native_rcs, docker_results: Dict[str, Dict],
                native_cmd: Sequence[str], cwd: str, extra: dict) -> None:
    if not args.output:
        return

    def block(times, rcs, peak=None):
        b = {"times": times or [], "return_codes": rcs or [],
             "statistics": calculate_stats(times) if times else None}
        if peak is not None:
            b["peak_rss_bytes"] = peak
        return b

    data = {
        "schema_version": SCHEMA_VERSION,
        "timestamp": datetime.datetime.now().astimezone().isoformat(),
        "system": {
            "os": platform.system(),
            "release": platform.release(),
            "machine": platform.machine(),
            "cpu_model": get_cpu_model(),
            "logical_cores": os.cpu_count(),
            "total_ram_gb": get_total_ram_gb(),
            "python_version": platform.python_version(),
            "docker": get_docker_info(),
        },
        "experiment": {
            "workload": shlex.join(native_cmd),
            "native_tool_version": get_tool_version(native_cmd),
            "image": args.image,
            "image_digest": get_image_digest(args.image),
            "platform_override": args.platform,
            "backend": args.backend,
            "interleaved": bool(args.interleave),
            "iterations": args.iterations,
            "warmup": args.warmup,
            "target_cv_percent": args.target_cv,
            "max_iterations": args.max_iterations,
            "input_file_sha256": hash_input_files(native_cmd, cwd),
            "harness_git_commit": get_git_commit(),
            "benchmark_py_sha256": sha256_file(os.path.abspath(__file__)),
        },
        "results": {"native": block(native_times, native_rcs, extra.get("native_peak_rss"))},
    }
    for backend, res in docker_results.items():
        key = "docker" if backend == "cli" else f"docker_{backend}"
        data["results"][key] = block(res.get("times"), res.get("rcs"), res.get("peak_rss"))

    # Comparison block: native vs each container backend (Welch + paired if interleaved).
    if native_times:
        comp = {}
        for backend, res in docker_results.items():
            ct = res.get("times") or []
            if not ct:
                continue
            key = "docker" if backend == "cli" else f"docker_{backend}"
            entry = {"welch": welch_t_test(native_times, ct), "cohens_d": cohens_d(native_times, ct)}
            if args.interleave and len(native_times) == len(ct):
                entry["paired"] = paired_t_test(native_times, ct)
            ns = calculate_stats(native_times)
            cs = calculate_stats(ct)
            if ns and cs and ns["mean"] > 0:
                entry["relative_overhead_x"] = round(cs["mean"] / ns["mean"], 4)
                entry["absolute_overhead_sec"] = round(cs["mean"] - ns["mean"], 6)
            comp[f"native_vs_{key}"] = entry
        if comp:
            data["results"]["comparison"] = comp

    if extra.get("pull_cost"):
        data["pull_cost"] = extra["pull_cost"]
    if extra.get("artifacts") is not None:
        data["output_artifacts_sha256"] = extra["artifacts"]

    try:
        out_path = os.path.abspath(args.output)
        repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        if not out_path.startswith(repo_root):
            logging.error("Refusing to write outside the repository root: %s", out_path)
            return
        os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        logging.info("Results exported to: %s", out_path)
    except OSError as exc:
        logging.error("Failed to export results: %s", exc)


#  Command builders

def build_docker_cli_cmd(args, workspace: str, native_cmd: Sequence[str]) -> List[str]:
    cmd = [DOCKER_EXECUTABLE, "run", "--rm"]
    if args.platform:
        cmd += ["--platform", args.platform]
    cmd += ["-v", f"{workspace}:{CONTAINER_WORKSPACE}", "-w", CONTAINER_WORKSPACE, args.image]
    cmd += list(native_cmd)
    return cmd


def build_dotnet_cmd(native_cmd: Sequence[str]) -> List[str]:
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    exe = os.path.join(repo_root, "tests", "benchmarking_suite", HARNESS_DIR, HARNESS_BINARY)
    if platform.system() == "Windows":
        exe += ".exe"
    if not os.path.exists(exe):
        logging.error("DotNet backend executable not found at %s", exe)
        logging.error("Build it: dotnet publish src/ContainerBenchmarkHarness/ContainerBenchmarkHarness.csproj "
                      "-c Release -o tests/benchmarking_suite/%s", HARNESS_DIR)
        sys.exit(1)
    return [exe] + list(native_cmd)


#  CLI

def parse_arguments():
    p = argparse.ArgumentParser(
        description="Benchmark utility for the OneWare hybrid execution strategy.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    p.add_argument("--image", default=DEFAULT_IMAGE, help="Container image for the containerized backends.")
    p.add_argument("--backend", choices=["cli", "dotnet", "all"], default="cli",
                   help="Container backend(s): cli (docker run), dotnet (real strategy), all.")
    p.add_argument("--cmd", nargs=argparse.REMAINDER, help="Command to benchmark (must be last).")
    p.add_argument("--iterations", type=int, default=30, help="Measured iterations (minimum, before CV escalation).")
    p.add_argument("--warmup", type=int, default=2, help="Unrecorded warmup runs.")
    p.add_argument("--target-cv", type=float, default=None,
                   help="If set, keep sampling until the CV (%%) is at/below this or --max-iterations is hit.")
    p.add_argument("--max-iterations", type=int, default=None, help="Hard cap when --target-cv is set.")
    p.add_argument("--interleave", action="store_true",
                   help="Alternate native/container per iteration (enables a paired t-test).")
    p.add_argument("--timeout", type=int, default=None, help="Per-iteration timeout (seconds).")
    p.add_argument("--pull-timeout", type=int, default=600, help="Timeout for image pre-pull/cold pull.")
    p.add_argument("--measure-pull", action="store_true",
                   help="Also measure the cold image-pull cost separately (removes then re-pulls the image).")
    p.add_argument("--artifacts", nargs="+", default=None,
                   help="Output files to SHA-256 after the run (output-determinism / reproducibility).")
    p.add_argument("--outlier-sigma", type=float, default=OUTLIER_SIGMA, help="Outlier detection threshold.")
    p.add_argument("--verbose", action="store_true", help="Verbose logging.")
    p.add_argument("--output", type=str, help="Export results JSON to this path (inside the repo).")
    p.add_argument("--workspace", type=str, default=".", help="Working directory for the workload.")
    p.add_argument("--platform", type=str, default=None, help="Force the container platform (e.g. linux/amd64).")
    p.add_argument("--skip-native", action="store_true", help="Skip the native baseline.")
    p.add_argument("--skip-pull", action="store_true", help="Skip the image pre-pull.")
    p.add_argument("--dry-run", action="store_true", help="Print the commands without running them.")
    p.add_argument("--compare", nargs=2, metavar=("FILE_A", "FILE_B"), help="Compare two result files and exit.")
    return p.parse_args()


#  Main

def main():
    args = parse_arguments()
    if args.verbose:
        logging.getLogger().setLevel(logging.DEBUG)
    if args.compare:
        compare_results(args.compare[0], args.compare[1])
        return
    if not args.cmd:
        print("Error: --cmd is required unless --compare is used.", file=sys.stderr)
        sys.exit(1)

    workspace = os.path.abspath(args.workspace)
    native_cmd = args.cmd
    backends = ["cli", "dotnet"] if args.backend == "all" else [args.backend]

    backend_cmds: Dict[str, List[str]] = {}
    for backend in backends:
        if backend == "cli":
            backend_cmds[backend] = build_docker_cli_cmd(args, workspace, native_cmd)
        else:
            os.environ[ENV_IMAGE_OVERRIDE] = args.image
            backend_cmds[backend] = build_dotnet_cmd(native_cmd)

    if args.dry_run:
        print("\nDry-run — commands that would run:\n")
        if not args.skip_native:
            print(f"  Native : {shlex.join(native_cmd)}")
        for backend, cmd in backend_cmds.items():
            print(f"  {backend.upper():7s}: {shlex.join(cmd)}")
        print(f"\n  Workspace: {workspace}\n  Iterations: {args.iterations} (warmup {args.warmup})"
              f"{', interleaved' if args.interleave else ''}")
        return

    logging.info("Workload: %s | image: %s | workspace: %s", shlex.join(native_cmd), args.image, workspace)
    wall_start = time.perf_counter()
    extra: dict = {}

    if args.measure_pull and "cli" in backends:
        extra["pull_cost"] = measure_pull_cost(args.image, args.platform, args.pull_timeout)
    if not args.skip_pull and "cli" in backends:
        ensure_image_pulled(args.image, args.platform, args.pull_timeout)

    native_times = native_rcs = None
    docker_results: Dict[str, Dict] = {}

    if args.interleave and not args.skip_native:
        modes = {"native": list(native_cmd)}
        for backend in backends:
            modes[backend] = backend_cmds[backend]
        res = perform_interleaved_suite(modes, workspace, iterations=args.iterations,
                                        warmup=args.warmup, timeout=args.timeout)
        native_times, native_rcs = res.get("native", ([], []))
        for backend in backends:
            t, rc = res.get(backend, ([], []))
            docker_results[backend] = {"times": t, "rcs": rc, "peak_rss": None}
    else:
        if not args.skip_native:
            native_times, native_rcs, peak = perform_benchmark_suite(
                "Native", native_cmd, workspace, iterations=args.iterations, warmup=args.warmup,
                timeout=args.timeout, outlier_sigma=args.outlier_sigma,
                target_cv=args.target_cv, max_iterations=args.max_iterations)
            extra["native_peak_rss"] = peak
            if not native_times:
                logging.warning("Native execution unavailable; continuing with containerized backends only.")
        for backend in backends:
            t, rc, peak = perform_benchmark_suite(
                f"Containerized ({backend.upper()})", backend_cmds[backend], workspace,
                iterations=args.iterations, warmup=args.warmup, timeout=args.timeout,
                outlier_sigma=args.outlier_sigma, target_cv=args.target_cv, max_iterations=args.max_iterations)
            docker_results[backend] = {"times": t, "rcs": rc, "peak_rss": peak}

    if args.artifacts:
        extra["artifacts"] = hash_artifacts(args.artifacts, workspace)
        logging.info("Hashed %s output artifact(s) for determinism comparison.", len(args.artifacts))

    wall_elapsed = time.perf_counter() - wall_start
    print_results(native_times, docker_results.get("cli", {}).get("times"),
                  docker_results.get("dotnet", {}).get("times"), wall_elapsed)
    export_json(args, native_times, native_rcs, docker_results, native_cmd, workspace, extra)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nBenchmark aborted by user.")
        sys.exit(130)
