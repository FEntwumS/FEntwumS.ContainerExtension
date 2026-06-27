# counter

> Target: TangNano9K (Gowin) - synthesis tier: synth_gowin via Compile; simulate on GHDL. Fit/Assemble out of scope (see suite README).

Parametric binary up-counter (Tier 2, sequential). The count is held in an
`unsigned` register from `numeric_std`, so wrap-around at `2**WIDTH` follows
defined modular arithmetic. Reset is synchronous and active-high; the counter
advances by one on each rising clock edge while `en` is asserted.

## Interface

| Port | Direction | Width                  | Description                                   |
|------|-----------|------------------------|-----------------------------------------------|
| clk  | in        | 1                      | Clock; all state updates on the rising edge   |
| rst  | in        | 1                      | Synchronous, active-high reset to zero        |
| en   | in        | 1                      | Count enable; holds the value when deasserted |
| q    | out       | `WIDTH`                | Current count, unsigned binary                |

Generic `WIDTH : natural := 8` sets the counter width. The testbench
instantiates `WIDTH = 4` to exercise the full range and the 15 -> 0 wrap
within a bounded simulation.

## Toolchain path

VHDL-2008 (`vhdlStandard: "08"`), simulated and elaborated with GHDL and
synthesised through the Yosys flow inside the `oss-cad-suite` container. This
project exercises the GHDL analyse/elaborate/run path and the GHDL-Yosys
front end for synthesis.

## Running in OneWare

Open the folder as a project; OneWare reads `counter.fpgaproj`. Run the
testbench `counter_tb` to simulate. The self-checking testbench compares the
DUT against an independent reference model on every cycle and reports
`counter: PASS - N cases` on success, terminating via `std.env.finish`.

Standalone reproduction of the simulation:

```
ghdl -a --std=08 counter.vhd counter_tb.vhd
ghdl -e --std=08 counter_tb
ghdl -r --std=08 counter_tb --assert-level=error
```
