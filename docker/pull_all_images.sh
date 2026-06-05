#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# pull_all_images.sh — Resilient Pre-cache script for FPGA/EDA Docker images
# ──────────────────────────────────────────────────────────────────────────────
set -Eeuo pipefail
trap 'echo -e "\n❌ Process terminated abnormally." ; exit 1' ERR INT TERM

PLATFORM="linux/amd64"
DRY_RUN="${1:-}"
TOTAL=0
FAILED=0
SUCCEEDED=0

# Production-ready note: Enable Docker Content Trust for cryptographic verification
# export DOCKER_CONTENT_TRUST=1

pull() {
  local image="$1"
  local desc="$2"
  TOTAL=$((TOTAL + 1))
  
  printf "\n\033[1;34m[%02d]\033[0m %-40s  %s\n" "$TOTAL" "$image" "$desc"
  
  if [[ "$DRY_RUN" == "--dry" ]]; then
    return
  fi
  
  # Production-ready retry loop for network reliability
  local retries=3
  local count=0
  local success=false
  
  while [ $count -lt $retries ]; do
    if docker pull --quiet --platform "$PLATFORM" "$image" > /dev/null 2>&1; then
      success=true
      break
    fi
    count=$((count + 1))
    echo "⚠️ Pull failed, retrying ($count/$retries)..."
    sleep 2
  done

  if $success; then
    local digest
    digest=$(docker inspect --format='{{index .RepoDigests 0}}' "$image" 2>/dev/null || echo "unknown")
    printf "\033[1;32m     ✅ OK\033[0m (Digest: %s)\n" "${digest#*@}"
    SUCCEEDED=$((SUCCEEDED + 1))
  else
    printf "\033[1;31m     ❌ FAILED (After $retries retries)\033[0m\n"
    FAILED=$((FAILED + 1))
  fi
}

echo "╔═══════════════════════════════════════════════════════════════╗"
echo "║  FPGA/EDA Target Cache Sequence (Platform: $PLATFORM) ║"
echo "╚═══════════════════════════════════════════════════════════════╝"

echo -e "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " CORE — ContainerExtension default tool images"
pull "hdlc/ghdl:yosys"           "GHDL + Yosys plugin [FALLBACK DEFAULT]"
pull "hdlc/nvc"                  "NVC (VHDL simulator)"
pull "hdlc/iverilog"             "Icarus Verilog (Verilog simulation)"
pull "hdlc/verilator"            "Verilator (Verilog simulation/lint)"
pull "hdlc/apicula"              "Apicula (Gowin bitstream tools)"
pull "hdlc/impl"                 "ALL impl tools: nextpnr-* + icestorm + trellis + oxide"
pull "hdlc/prog"                 "openFPGALoader (FPGA programming)"
pull "hdlc/gtkwave"              "GTKWave (waveform viewer)"

echo -e "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " INDIVIDUAL — Standalone tool images"
pull "hdlc/ghdl"                 "GHDL standalone (VHDL only)"
pull "hdlc/yosys"                "Yosys standalone (Verilog synthesis)"
pull "hdlc/nextpnr"              "nextpnr (all targets, no databases)"
pull "hdlc/icestorm"             "iCE40 tools (iceprog, icepack)"
pull "hdlc/prjtrellis"           "Project Trellis (ECP5 database)"
pull "hdlc/prjoxide"             "Project Oxide (Nexus database)"
pull "hdlc/openfpgaloader"       "openFPGALoader standalone"

echo -e "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " ALL-IN-ONE — Multi-tool combo images"
pull "hdlc/sim"                  "GHDL + NVC + Verilator + Icarus Verilog"
pull "hdlc/formal"               "All formal solvers (Z3, Yices2, Boolector, etc.)"

echo -e "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " ASIC — Layout, DRC, simulation tools"
pull "hdlc/klayout"              "KLayout (IC layout viewer/editor)"
pull "hdlc/magic"                "Magic VLSI layout tool"
pull "hdlc/netgen"               "Netgen (LVS/DRC tool)"
pull "hdlc/openroad"             "OpenROAD (RTL-to-GDS flow)"
pull "hdlc/xschem"               "Xschem (schematic editor)"
pull "hdlc/xyce"                 "Xyce (circuit simulator)"

echo -e "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " MISC — Academic and utility tools"
pull "hdlc/vtr"                  "Verilog-to-Routing (academic FPGA CAD)"
pull "hdlc/verible"              "Verible (SystemVerilog linter/formatter)"

echo -e "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo " BASE — Build prerequisites"
pull "ubuntu:24.04"              "Ubuntu 24.04 LTS (Dockerfile base target)"

echo -e "\n╔═══════════════════════════════════════════════════════════════╗"
printf "║  Total: %-3d  |  OK: %-3d  |  Failed: %-3d                   ║\n" "$TOTAL" "$SUCCEEDED" "$FAILED"
echo "╚═══════════════════════════════════════════════════════════════╝"

if [[ "$FAILED" -gt 0 ]]; then
  exit 1
fi
