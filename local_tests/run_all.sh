#!/usr/bin/env bash
set -Eeuo pipefail
trap 'echo -e "\nCritical failure at line $LINENO"; exit 1' ERR

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)
      echo "Usage: $0"
      echo "Options:"
      echo "  -h, --help   Show this help message"
      exit 0
      ;;
    *)
      echo "Unknown option: $1"
      echo "Usage: $0"
      exit 1
      ;;
  esac
done

IMAGE="fentwums/oss-cad-suite:latest"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPORT="${DIR}/test_report.md"

echo "# Headless Container Workflow Test Report" > "$REPORT"
echo "Generated on: $(date)" >> "$REPORT"
echo "" >> "$REPORT"
echo "| Phase | Test Name | Status | Duration |" >> "$REPORT"
echo "|---|---|---|---|" >> "$REPORT"

function run_test() {
    local phase="$1"
    local name="$2"
    local workdir="$3"
    shift 3
    local cmd=("$@")

    echo -n "Running [${phase}] ${name}... "
    local start_time
    start_time=$(date +%s)
    
    local status="SUCCESS"
    if ! docker run --rm -v "${DIR}:/workspace" -w "/workspace/${workdir}" "$IMAGE" "${cmd[@]}" > /dev/null 2>&1; then
        status="FAILED"
    fi

    local end_time
    end_time=$(date +%s)
    local duration=$((end_time - start_time))
    
    if [ "$status" == "SUCCESS" ]; then
        echo -e "\033[1;32mOK\033[0m (${duration}s)"
    else
        echo -e "\033[1;31mFAILED\033[0m (${duration}s)"
    fi

    echo "| ${phase} | ${name} | ${status} | ${duration}s |" >> "$REPORT"
}

# 1. VHDL Flow
run_test "Phase 1a" "GHDL Analysis" "VHDL_Blink" ghdl -a "VHDL_Blink.vhd" "VHDL_Blink_tb.vhd"
run_test "Phase 1b" "GHDL Elaboration" "VHDL_Blink" ghdl -e "VHDL_Blink_tb"
run_test "Phase 1c" "GHDL Simulation" "VHDL_Blink" ghdl -r "VHDL_Blink_tb"

# 2. Verilog Simulation & Synthesis Flow
run_test "Phase 2a" "Icarus Verilog Compile" "Verilog_Blink" iverilog -o Blink.vvp Verilog_Blink.v Verilog_Blink_tb.v
run_test "Phase 2b" "Icarus Verilog Exec" "Verilog_Blink" vvp Blink.vvp
run_test "Phase 2c" "Verilator Build" "Verilog_Blink" verilator -Wall -Wno-PROCASSINIT --cc Verilog_Blink.v --exe sim_main.cpp --build
run_test "Phase 2d" "Verilator Exec" "Verilog_Blink" ./obj_dir/VVerilog_Blink
run_test "Phase 2e" "Yosys Synthesis (Verilog)" "Verilog_Blink" yosys -p "synth_ice40" "Verilog_Blink.v"

# 3. Formal Verification
run_test "Phase 3a" "SymbiYosys Formal Verification" "Formal_Verification" sby -f "Blink.sby"

# 4. iCE40 Bitstream Flow
run_test "Phase 4a" "iCE40 Yosys Synth" "iCE40_Flow" yosys -p "synth_ice40 -top ice40_blink -json ice40_blink.json" "ice40_blink.v"
run_test "Phase 4b" "NextPNR Place & Route" "iCE40_Flow" nextpnr-ice40 --hx1k --json "ice40_blink.json" --pcf "ice40_blink.pcf" --asc "ice40_blink.asc"
run_test "Phase 4c" "IcePack Bitstream Gen" "iCE40_Flow" icepack "ice40_blink.asc" "ice40_blink.bin"

# 5. Lattice ECP5 Physical Flow
run_test "Phase 5a" "ECP5 Yosys Synth" "ECP5_Flow" yosys -p "synth_ecp5 -json ecp5_blink.json" "ecp5_blink.v"
run_test "Phase 5b" "NextPNR Place & Route (ECP5)" "ECP5_Flow" nextpnr-ecp5 --85k --package CABGA381 --json ecp5_blink.json --lpf ecp5_blink.lpf --textcfg ecp5_blink.config
run_test "Phase 5c" "EcpPack Bitstream Gen" "ECP5_Flow" ecppack ecp5_blink.config ecp5_blink.bit

echo ""
echo "Tests completed. Report saved to: $REPORT"
