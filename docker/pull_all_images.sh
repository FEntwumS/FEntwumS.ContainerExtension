#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# pull_all_images.sh — Pre-cache all FPGA/EDA Docker images for offline use
#
# All images are from Docker Hub (hub.docker.com/u/hdlc).
# Multi-level image paths (e.g. impl/icestorm) are NOT available as separate
# pullable images — the all-in-one images (impl, formal, sim) already contain
# every tool from their sub-images.
#
# All images are x86-only (no ARM/Apple Silicon native builds).
# On macOS with OrbStack/Docker Desktop, Rosetta handles emulation.
#
# Usage:
#   chmod +x pull_all_images.sh
#   ./pull_all_images.sh          # Pull everything
#   ./pull_all_images.sh --dry    # Just list, don't pull
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

PLATFORM="linux/amd64"
DRY_RUN="${1:-}"
TOTAL=0
FAILED=0
SUCCEEDED=0

pull() {
  local image="$1"
  local desc="$2"
  TOTAL=$((TOTAL + 1))
  printf "\n\033[1;34m[%02d]\033[0m %-40s  %s\n" "$TOTAL" "$image" "$desc"
  if [[ "$DRY_RUN" == "--dry" ]]; then
    return
  fi
  if docker pull --platform "$PLATFORM" "$image"; then
    printf "\033[1;32m     ✅ OK\033[0m\n"
    SUCCEEDED=$((SUCCEEDED + 1))
  else
    printf "\033[1;31m     ❌ FAILED\033[0m\n"
    FAILED=$((FAILED + 1))
  fi
}

echo "╔═══════════════════════════════════════════════════════════════╗"
echo "║  FPGA/EDA Docker Image Pre-Cache (platform: $PLATFORM)  ║"
echo "╚═══════════════════════════════════════════════════════════════╝"

# ══════════════════════════════════════════════════════════════════════════════
#  1. CORE — Default images used by ContainerExtension
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " CORE — ContainerExtension default tool images"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

pull "hdlc/ghdl:yosys"           "GHDL + Yosys plugin [FALLBACK DEFAULT]"
pull "hdlc/nvc"                  "NVC (VHDL simulator)"
pull "hdlc/iverilog"             "Icarus Verilog (Verilog simulation)"
pull "hdlc/verilator"            "Verilator (Verilog simulation/lint)"
pull "hdlc/apicula"              "Apicula (Gowin bitstream tools)"
pull "hdlc/impl"                 "ALL impl tools: nextpnr-* + icestorm + trellis + oxide"
pull "hdlc/prog"                 "openFPGALoader (FPGA programming)"
pull "hdlc/gtkwave"              "GTKWave (waveform viewer)"

# ══════════════════════════════════════════════════════════════════════════════
#  2. INDIVIDUAL TOOL IMAGES
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " INDIVIDUAL — Standalone tool images"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

pull "hdlc/ghdl"                 "GHDL standalone (VHDL only)"
pull "hdlc/yosys"                "Yosys standalone (Verilog synthesis)"
pull "hdlc/nextpnr"              "nextpnr (all targets, no databases)"
pull "hdlc/icestorm"             "iCE40 tools (iceprog, icepack)"
pull "hdlc/prjtrellis"           "Project Trellis (ECP5 database)"
pull "hdlc/prjoxide"             "Project Oxide (Nexus database)"
pull "hdlc/openfpgaloader"       "openFPGALoader standalone"

# ══════════════════════════════════════════════════════════════════════════════
#  3. ALL-IN-ONE COMBO IMAGES
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " ALL-IN-ONE — Multi-tool combo images"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

pull "hdlc/sim"                  "GHDL + NVC + Verilator + Icarus Verilog"
pull "hdlc/formal"               "All formal solvers (Z3, Yices2, Boolector, etc.)"

# ══════════════════════════════════════════════════════════════════════════════
#  4. ASIC / LAYOUT TOOLS
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " ASIC — Layout, DRC, simulation tools"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

pull "hdlc/klayout"              "KLayout (IC layout viewer/editor)"
pull "hdlc/magic"                "Magic VLSI layout tool"
pull "hdlc/netgen"               "Netgen (LVS/DRC tool)"
pull "hdlc/openroad"             "OpenROAD (RTL-to-GDS flow)"
pull "hdlc/xschem"               "Xschem (schematic editor)"
pull "hdlc/xyce"                 "Xyce (circuit simulator)"

# ══════════════════════════════════════════════════════════════════════════════
#  5. ACADEMIC / MISC
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " MISC — Academic and utility tools"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

pull "hdlc/vtr"                  "Verilog-to-Routing (academic FPGA CAD)"
pull "hdlc/verible"              "Verible (SystemVerilog linter/formatter)"

# ══════════════════════════════════════════════════════════════════════════════
#  6. BASE IMAGE
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " BASE — Build prerequisites"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

pull "ubuntu:22.04"              "Ubuntu 22.04 (oss-cad-suite Dockerfile base)"

# ══════════════════════════════════════════════════════════════════════════════
#  SUMMARY
# ══════════════════════════════════════════════════════════════════════════════
echo ""
echo "╔═══════════════════════════════════════════════════════════════╗"
printf "║  Total: %-3d  |  OK: %-3d  |  Failed: %-3d                   ║\n" "$TOTAL" "$SUCCEEDED" "$FAILED"
echo "╠═══════════════════════════════════════════════════════════════╣"
echo "║  NOTE: hdlc/impl already contains ALL nextpnr variants,    ║"
echo "║  icestorm, prjtrellis, and prjoxide tools. No need for     ║"
echo "║  separate sub-images.                                       ║"
echo "╚═══════════════════════════════════════════════════════════════╝"

if [[ "$FAILED" -gt 0 ]]; then
  exit 1
fi
