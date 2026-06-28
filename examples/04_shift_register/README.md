# shift_register

> Target: Cologne Chip GateMate evaluation board - full flow `synth_gatemate` → `nextpnr-himbaechel` → `gmpack` via Compile (end to end to a bitstream); simulate on Icarus Verilog.

Parameterized parallel-load / left-shifting serial shift register (tier 2:
sequential, parameterized). Demonstrates the Verilog synthesis path of the
FEntwumS ContainerExtension (Icarus for simulation, Yosys for the synthesis
front end) inside the `oss-cad-suite` container.

## Behaviour

Synchronous, active-high reset. On each rising clock edge the priority is:

1. `rst` - clear `q` to zero;
2. `load` - capture the parallel word `d`;
3. `shift_en` - shift left, dropping the MSB and admitting `sin` at the LSB.

`load` takes precedence over `shift_en` so a concurrent assertion is
deterministic. `sout` continuously exposes the MSB (`q[WIDTH-1]`), the bit
leaving the register on the next shift.

## Interface

| Port       | Dir | Width     | Description                                   |
|------------|-----|-----------|-----------------------------------------------|
| `clk`      | in  | 1         | Clock; all state updates on the rising edge   |
| `rst`      | in  | 1         | Synchronous reset, active high                |
| `load`     | in  | 1         | Parallel load enable (`q <= d`)               |
| `d`        | in  | `WIDTH`   | Parallel input word                           |
| `sin`      | in  | 1         | Serial input, shifted into the LSB            |
| `shift_en` | in  | 1         | Serial shift enable                           |
| `q`        | out | `WIDTH`   | Register state                                |
| `sout`     | out | 1         | Serial output, `q[WIDTH-1]` (MSB)             |

Parameter `WIDTH` (default 8) sets the register width.

## Toolchain path

`toolchain: Yosys` - the Verilog front end. Simulation uses Icarus Verilog
(`iverilog -g2012` / `vvp`); synthesis elaboration is checked with Yosys
(`read_verilog -sv`, `hierarchy`, `proc`, `opt`, `check`).

## Verification

The testbench `shift_register_tb.v` is self-checking: an independent
behavioural reference is driven with identical stimulus and compared against the
DUT on every clock edge, with `$error`/`$fatal` on any divergence. It exercises
reset, parallel load, hold, a full WIDTH-deep serial shift-in/out sequence,
load-over-shift precedence, and a mid-stream reset, then prints
`shift_register: PASS - N cases`.

Standalone (matching the container flow):

```
iverilog -g2012 -o /tmp/tb.out shift_register.v shift_register_tb.v
vvp /tmp/tb.out
yosys -q -p "read_verilog -sv shift_register.v; hierarchy -top shift_register; proc; opt; check"
```

## Running in OneWare

Open the folder as a project (`shift_register.fpgaproj`). The simulation entry
is `shift_register_tb.v`; the synthesis top is `shift_register.v`. Run the
testbench through the simulator, or invoke synthesis to drive the Yosys path via
the ContainerExtension.
