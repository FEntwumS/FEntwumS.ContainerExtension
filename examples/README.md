# FEntwumS ContainerExtension example suite

Eight self-contained FPGA designs that exercise the open-source toolchain through the FEntwumS
ContainerExtension. Every tool runs inside the `fentwums/oss-cad-suite` container; no host toolchain is
required. The designs span both source languages and the full synthesis, place-and-route, and bitstream
pipeline so that, collectively, they drive the back-end tools the extension containerizes.

## Coverage matrix

All eight target the Cologne Chip GateMate evaluation board and run the same back end
(`synth_gatemate` → `nextpnr-himbaechel` → `gmpack`); each compiles end to end to a bitstream.

| Folder | Lang | Synthesis front end | Compile | Fit (P&R) | Assemble | Simulate |
| --- | --- | --- | --- | --- | --- | --- |
| `01_full_adder` | VHDL | `ghdl` plugin | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `ghdl` |
| `02_mux4` | Verilog | Yosys | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `iverilog` |
| `03_counter` | VHDL | `ghdl` plugin | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `ghdl` |
| `04_shift_register` | Verilog | Yosys | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `iverilog` |
| `05_seven_segment` | VHDL | `ghdl` plugin | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `ghdl` |
| `06_debouncer` | VHDL | `ghdl` plugin | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `ghdl` |
| `07_uart_tx` | Verilog | Yosys | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `iverilog` |
| `08_sync_fifo` | VHDL | `ghdl` plugin | `synth_gatemate` | `nextpnr-himbaechel` | `gmpack` | `ghdl` |

VHDL designs are read directly inside the containerized Yosys via the `ghdl` plugin
(`plugin -i ghdl; ghdl --std=08 <source> -e <top>; synth_gatemate -top <top> ...`), keeping the entire
flow in the container. Verilog designs are synthesized by Yosys directly. Place-and-route uses
`--vopt allow-unconstrained`, so a design builds whether or not it carries pin constraints.

## Running in OneWare

1. Open a project's `.fpgaproj` (all are pre-loaded in the project explorer).
2. *Compile* runs the full flow to a bitstream; the Compile drop-down also exposes the individual
   *Run Fit* and *Run Assemble* stages.
3. *Simulate* a testbench to drive the GHDL (VHDL) or Icarus Verilog (Verilog/SystemVerilog) path.

Pin constraints are illustrative, not a physical layout: `01_full_adder`, `02_mux4`, and `08_sync_fifo`
ship a GateMate `project.pcf` (converted to `.ccf`); the others build unconstrained. The designs are
interface examples, not pin-locked reference designs.

## Purpose

This suite is the integration-test corpus for the FEntwumS ContainerExtension. Each design is
self-checking and self-terminating, so a passing simulation plus a produced bitstream confirm the
container, the tool invocation, and the manifest wiring end to end.
