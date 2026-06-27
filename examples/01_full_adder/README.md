# full_adder

> Target: GateMate Eval Board - full flow: synth_gatemate -> nextpnr-himbaechel -> gmpack; simulate on GHDL.

Single-bit full adder, the canonical tier-1 combinational example. It serves as
the minimal smoke test for the GHDL analyze/elaborate/run path of the FEntwumS
ContainerExtension, which drives the toolchain inside the oss-cad-suite Docker
container.

## Interface

| Port   | Direction | Type        | Description                          |
| ------ | --------- | ----------- | ------------------------------------ |
| `a`    | in        | `std_logic` | First addend bit                     |
| `b`    | in        | `std_logic` | Second addend bit                    |
| `cin`  | in        | `std_logic` | Carry in                             |
| `sum`  | out       | `std_logic` | `a xor b xor cin`                    |
| `cout` | out       | `std_logic` | Majority of `a`, `b`, `cin`          |

## Toolchain path

VHDL-2008, simulated with GHDL. OneWare invokes the container with `--std=08`
(set via `vhdlStandard` in the manifest). The testbench `tb_full_adder`
exhaustively sweeps all eight input combinations and checks `sum` and `cout`
against an integer reference (the ones-count of the inputs), exercising the
GHDL `-a` / `-e` / `-r` stages. No synthesis backend is required.

## Running in OneWare

Open the project in OneWare Studio and run the simulation on `tb_full_adder`.
A passing run prints:

```
tb_full_adder: PASS - 8 cases
```

The simulation is self-terminating via `std.env.finish`.
