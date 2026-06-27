# mux4

> Target: GateMate Eval Board - full flow: synth_gatemate -> nextpnr-himbaechel -> gmpack; simulate on Icarus Verilog.

Parameterized 4-to-1 multiplexer (combinational). Selects one of four `WIDTH`-bit
input words onto the output under control of a 2-bit selector. The selector case
is exhaustive over its two bits, so the design is purely combinational and infers
no storage.

## Interface

| Port  | Dir | Width       | Description                  |
|-------|-----|-------------|------------------------------|
| `d0`  | in  | `WIDTH`     | Data input, selected by `sel=0` |
| `d1`  | in  | `WIDTH`     | Data input, selected by `sel=1` |
| `d2`  | in  | `WIDTH`     | Data input, selected by `sel=2` |
| `d3`  | in  | `WIDTH`     | Data input, selected by `sel=3` |
| `sel` | in  | 2           | Selector                     |
| `y`   | out | `WIDTH`     | Selected data word           |

Parameter: `WIDTH` (default 8).

## Toolchain path

Exercises the **Yosys synthesis + Icarus simulation** container path. The
testbench instantiates the DUT at the default width and at a narrower width to
confirm `WIDTH` propagation, then Yosys elaborates and checks the RTL.

## Running in OneWare

Open the project and run the testbench `mux4_tb.v` via the FEntwumS Container
Extension. Simulation prints `mux4: PASS - 8 cases` and terminates with `$finish`.
