# debouncer

> Target: GateMate Eval Board - full flow: synth_gatemate -> nextpnr-himbaechel -> gmpack; simulate on GHDL.

Synchronous push-button debouncer with rising-edge detection. The raw button
input is resynchronised to the clock domain, filtered against contact bounce,
and exposed as a stable level together with a one-cycle press strobe.

## Purpose

A mechanical button connected to an FPGA pin produces tens of milliseconds of
contact bounce on each press and is asynchronous to the system clock. This
block removes both hazards: a two-stage synchroniser resolves metastability,
and a stability counter rejects any transition that does not persist for the
configured guard interval. The clean output is suitable for driving edge-
triggered logic (counters, state machines) that would otherwise miscount a
single press.

## Parameters

| Generic       | Default   | Meaning                                                     |
| ------------- | --------- | ----------------------------------------------------------- |
| `CLK_FREQ_HZ` | 1 000 000 | System clock frequency in hertz                             |
| `STABLE_USEC` | 100       | Required input stability, in microseconds, before latching  |

The stability window in clock cycles is
`COUNT_MAX = CLK_FREQ_HZ / 1_000_000 * STABLE_USEC`, clamped to a minimum of 1.

## Interface

| Port           | Dir | Width | Description                                                   |
| -------------- | --- | ----- | ------------------------------------------------------------- |
| `clk`          | in  | 1     | System clock; all logic is synchronous to its rising edge     |
| `rst`          | in  | 1     | Synchronous active-high reset                                 |
| `btn_in`       | in  | 1     | Raw, asynchronous button input                                |
| `btn_state`    | out | 1     | Debounced button level                                        |
| `rising_pulse` | out | 1     | One-cycle strobe on a clean 0->1 transition of `btn_state`    |

## Toolchain path

VHDL-2008, analysed and elaborated with GHDL and synthesised through the Yosys
GHDL frontend. The manifest sets `vhdlStandard` to `08`, so OneWare invokes the
tools with `--std=08`. This project exercises the GHDL simulation path and the
Yosys VHDL synthesis path inside the `fentwums/oss-cad-suite` container.

## Running in OneWare

1. Open the project folder in OneWare Studio; the `.fpgaproj` manifest is
   detected automatically.
2. Run the testbench `debouncer_tb` through the simulation action. It drives a
   glitch, a bouncy press, and a stable press, and prints
   `debouncer_tb: PASS - 4 cases` on success.
3. Run synthesis to elaborate `debouncer` through Yosys.

### Standalone verification

```sh
docker run --rm -v "$PWD":/work -w /work fentwums/oss-cad-suite:2026-06-30 \
  bash -lc 'ghdl -a --std=08 --workdir=/tmp debouncer.vhd debouncer_tb.vhd \
            && ghdl -e --std=08 --workdir=/tmp debouncer_tb \
            && ghdl -r --std=08 --workdir=/tmp debouncer_tb \
                 --stop-time=200ms --assert-level=error'
```
