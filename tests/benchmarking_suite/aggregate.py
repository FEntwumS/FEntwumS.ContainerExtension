#!/usr/bin/env python3
"""
Cross-platform results aggregator for the ContainerExtension evaluation.

Reads per-platform results produced by ``run_evaluation.py`` (results/<platform>/*.json)
and emits, for the thesis:
  * results/summary.csv          — one row per platform x workload x backend, with
                                    mean, 95% CI, CV, and native-vs-container overhead.
  * results/determinism.md       — output-determinism matrix: do the produced
                                    bitstreams/netlists hash-identically across machines?
  * results/summary.md           — human-readable overview table.
  * results/figures/*.png        — box/overhead plots (only if matplotlib is installed).

Pure stdlib for the tables; matplotlib is optional and only needed for figures.

Usage:
    python3 aggregate.py
    python3 aggregate.py --results-dir results
"""
import argparse
import csv
import glob
import json
import os
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))

# Backend key in the JSON -> friendly label.
BACKENDS = {"native": "native", "docker": "cli", "docker_dotnet": "strategy"}


def load_results(results_dir):
    """Returns {platform: {workload: data}} for every results/<platform>/<workload>.json."""
    out = defaultdict(dict)
    for path in glob.glob(os.path.join(results_dir, "*", "*.json")):
        platform = os.path.basename(os.path.dirname(path))
        workload = os.path.splitext(os.path.basename(path))[0]
        try:
            with open(path, encoding="utf-8") as f:
                out[platform][workload] = json.load(f)
        except (OSError, json.JSONDecodeError):
            continue
    return out


def holm_bonferroni(pvals):
    """Holm-Bonferroni step-down adjustment controlling the family-wise error rate across the full set
    of reported overhead tests. Input: list of (key, p). Output: {key: adjusted_p}."""
    valid = [(k, p) for k, p in pvals if isinstance(p, (int, float))]
    m = len(valid)
    out = {}
    running = 0.0
    for rank, (k, p) in enumerate(sorted(valid, key=lambda kp: kp[1])):
        running = max(running, min(1.0, (m - rank) * p))  # step-down; enforce monotonicity
        out[k] = round(running, 6)
    return out


def extract_floors(data):
    """Per (platform, backend-label) container lifecycle floor in seconds, from the lifecycle_floor
    workload; used to estimate each workload's compute time as mean - floor (cold-start decomposition)."""
    floors = {}
    for plat, workloads in data.items():
        d = workloads.get("lifecycle_floor")
        if not d:
            continue
        for jkey, label in BACKENDS.items():
            blk = d.get("results", {}).get(jkey)
            if blk and blk.get("statistics"):
                floors[(plat, label)] = blk["statistics"]["mean"]
    return floors


def write_csv(data, out_csv):
    cols = ["platform", "cpu_model", "workload", "backend", "n", "mean_s", "ci95_low_s",
            "ci95_high_s", "cv_percent", "overhead_x_vs_native", "overhead_p_value",
            "overhead_p_holm", "overhead_x_vs_cli", "overhead_p_vs_cli", "overhead_p_vs_cli_holm",
            "container_peak_mem_mb", "lifecycle_floor_s", "compute_est_s"]
    floors = extract_floors(data)
    raw, family = [], []
    for plat, workloads in sorted(data.items()):
        for wl, d in sorted(workloads.items()):
            cpu = d.get("system", {}).get("cpu_model", "")
            comp = d.get("results", {}).get("comparison", {})
            for jkey, label in BACKENDS.items():
                blk = d.get("results", {}).get(jkey)
                if not blk or not blk.get("statistics"):
                    continue
                s = blk["statistics"]
                overhead_x = overhead_p = ""
                centry = comp.get(f"native_vs_{jkey}")
                if centry:
                    overhead_x = centry.get("relative_overhead_x", "")
                    overhead_p = (centry.get("welch") or {}).get("p_value", "")
                # The extension's overhead beyond raw containerization (strategy vs docker CLI) belongs
                # on the strategy row; it is blank for the native and cli rows.
                ext_x = ext_p = ""
                if jkey == "docker_dotnet":
                    eentry = comp.get("cli_vs_dotnet")
                    if eentry:
                        ext_x = eentry.get("relative_overhead_x", "")
                        ext_p = (eentry.get("welch") or {}).get("p_value", "")
                cmem = blk.get("container_peak_mem_bytes")
                cmem_mb = round(cmem / (1024 * 1024), 1) if isinstance(cmem, (int, float)) else ""
                # Cold-start decomposition: subtract the container lifecycle floor from wall-clock to
                # estimate tool compute. Blank for the floor workload itself and where no floor exists.
                floor = floors.get((plat, label))
                floor_s = round(floor, 6) if floor is not None else ""
                compute_est = (round(s["mean"] - floor, 6)
                               if floor is not None and wl != "lifecycle_floor" else "")
                nat_key, cli_key = f"{plat}|{wl}|{label}|nat", f"{plat}|{wl}|{label}|cli"
                if isinstance(overhead_p, (int, float)):
                    family.append((nat_key, overhead_p))
                if isinstance(ext_p, (int, float)):
                    family.append((cli_key, ext_p))
                raw.append(([plat, cpu, wl, label, s["n"], round(s["mean"], 6),
                             round(s["ci95_low"], 6), round(s["ci95_high"], 6),
                             s["cv_percent"], overhead_x, overhead_p, ext_x, ext_p,
                             cmem_mb, floor_s, compute_est], nat_key, cli_key))
    # Holm-Bonferroni across the whole family of reported overhead tests (FWER control).
    adjusted = holm_bonferroni(family)
    rows = [r[:11] + [adjusted.get(nat_key, "")] + r[11:13] + [adjusted.get(cli_key, "")] + [r[13], r[14], r[15]]
            for r, nat_key, cli_key in raw]
    with open(out_csv, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(cols)
        w.writerows(rows)
    return len(rows)


def write_determinism(data, out_md):
    """Compare each workload's output-artifact SHA-256 across all platforms present."""
    platforms = sorted(data)
    # workload -> artifact -> {platform: hash}
    art = defaultdict(lambda: defaultdict(dict))
    for plat, workloads in data.items():
        for wl, d in workloads.items():
            for name, h in (d.get("output_artifacts_sha256") or {}).items():
                art[wl][name][plat] = h
    lines = ["# Output-determinism across platforms", "",
             "Each toolchain artifact (bitstream / netlist) is SHA-256 hashed after generation on every",
             "machine. Identical hashes across platforms are direct evidence that the containerized",
             "architecture makes tool outputs machine-independent.", "",
             f"Platforms compared: {', '.join(platforms) if platforms else '(none)'}", ""]
    if len(platforms) < 2:
        lines += ["> **Cross-platform determinism requires at least two platforms.** With a single platform",
                  "> the matrix below is informational only — an artifact trivially equals itself. Collect a",
                  "> second platform before claiming machine-independent outputs.", ""]
    if not art:
        lines += ["_No artifact hashes recorded yet — run run_evaluation.py on at least one platform._"]
    for wl in sorted(art):
        lines.append(f"## {wl}")
        lines.append("")
        lines.append("| Artifact | " + " | ".join(platforms) + " | Identical? |")
        lines.append("|---|" + "|".join(["---"] * len(platforms)) + "|---|")
        for name in sorted(art[wl]):
            hashes = art[wl][name]
            present = [hashes.get(p) for p in platforms]
            shorts = [(h[:12] if h else "-") for h in present]
            non_null = [h for h in present if h]
            if len(platforms) < 2:
                # A single platform cannot establish cross-platform determinism; "YES" here would be
                # vacuous (the artifact only equals itself).
                identical = "n/a (1 platform)"
            elif non_null and len(set(non_null)) == 1 and len(non_null) == len(platforms):
                identical = "YES"
            elif non_null and len(set(non_null)) == 1:
                identical = "partial"
            else:
                identical = "NO"
            lines.append(f"| `{name}` | " + " | ".join(shorts) + f" | {identical} |")
        lines.append("")
    with open(out_md, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    return len(art)


def write_summary_md(data, out_md):
    lines = ["# Evaluation summary", "",
             "Mean execution time (95% CI) per workload and backend, by platform. Overhead x is the",
             "container-vs-native ratio (where a native baseline was measured); Ext. overhead x is the",
             "strategy-vs-CLI ratio — the cost the extension adds beyond raw containerization.", ""]
    for plat in sorted(data):
        sysinfo = next(iter(data[plat].values()), {}).get("system", {})
        lines.append(f"## {plat} — {sysinfo.get('cpu_model', '?')} "
                     f"({sysinfo.get('total_ram_gb', '?')} GB, {sysinfo.get('logical_cores', '?')} cores, "
                     f"Docker {sysinfo.get('docker', {}).get('server_version', '?')})")
        lines.append("")
        lines.append("| Workload | Backend | n | Mean (s) | 95% CI (s) | CV% | Overhead x | Ext. overhead x |")
        lines.append("|---|---|---|---|---|---|---|---|")
        for wl in sorted(data[plat]):
            d = data[plat][wl]
            comp = d.get("results", {}).get("comparison", {})
            for jkey, label in BACKENDS.items():
                blk = d.get("results", {}).get(jkey)
                if not blk or not blk.get("statistics"):
                    continue
                s = blk["statistics"]
                ox = ""
                c = comp.get(f"native_vs_{jkey}")
                if c and c.get("relative_overhead_x"):
                    ox = f"{c['relative_overhead_x']:.2f}x"
                ext = ""
                if jkey == "docker_dotnet":
                    e = comp.get("cli_vs_dotnet")
                    if e and e.get("relative_overhead_x"):
                        ext = f"{e['relative_overhead_x']:.2f}x"
                lines.append(f"| {wl} | {label} | {s['n']} | {s['mean']:.4f} | "
                             f"[{s['ci95_low']:.4f}, {s['ci95_high']:.4f}] | {s['cv_percent']:.1f} | {ox} | {ext} |")
        lines.append("")
    with open(out_md, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def make_figures(data, fig_dir):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        print("  matplotlib not installed — skipping figures. Install with: pip install matplotlib")
        return 0
    os.makedirs(fig_dir, exist_ok=True)
    # One grouped bar chart per platform: mean +/- CI per workload (strategy backend).
    made = 0
    for plat in sorted(data):
        labels, means, errs = [], [], []
        for wl in sorted(data[plat]):
            blk = data[plat][wl].get("results", {}).get("docker_dotnet") or \
                  data[plat][wl].get("results", {}).get("docker")
            if not blk or not blk.get("statistics"):
                continue
            s = blk["statistics"]
            labels.append(wl)
            means.append(s["mean"])
            errs.append(s["ci95_halfwidth"])
        if not labels:
            continue
        fig, ax = plt.subplots(figsize=(max(6, len(labels) * 1.4), 4))
        ax.bar(range(len(labels)), means, yerr=errs, capsize=4, color="#2496ED")
        ax.set_xticks(range(len(labels)))
        ax.set_xticklabels(labels, rotation=30, ha="right")
        ax.set_ylabel("Mean execution time (s)")
        ax.set_title(f"Containerized execution time — {plat} (95% CI)")
        fig.tight_layout()
        fig.savefig(os.path.join(fig_dir, f"time_{plat}.png"), dpi=150)
        plt.close(fig)
        made += 1
    return made


def main():
    ap = argparse.ArgumentParser(description="Aggregate cross-platform evaluation results.")
    ap.add_argument("--results-dir", default=os.path.join(HERE, "results"))
    args = ap.parse_args()
    rd = os.path.abspath(args.results_dir)
    data = load_results(rd)
    if not data:
        print(f"No results found under {rd}. Run run_evaluation.py first.")
        return 1
    n = write_csv(data, os.path.join(rd, "summary.csv"))
    w = write_determinism(data, os.path.join(rd, "determinism.md"))
    write_summary_md(data, os.path.join(rd, "summary.md"))
    figs = make_figures(data, os.path.join(rd, "figures"))
    print(f"Platforms: {', '.join(sorted(data))}")
    print(f"  summary.csv     : {n} rows")
    print(f"  determinism.md  : {w} workloads compared")
    print(f"  summary.md      : written")
    print(f"  figures         : {figs} generated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
