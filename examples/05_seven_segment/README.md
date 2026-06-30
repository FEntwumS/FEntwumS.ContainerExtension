# seven_seg_decoder

> Target: iCEBreaker (iCE40 UP5K) - full flow: synth_ice40 -> nextpnr-ice40 -> icepack; simulate on GHDL.

Combinational hexadecimal-to-seven-segment decoder for a common-anode display.
A 4-bit input code `0x0..0xF` is mapped to the active-LOW segment drive for the
canonical hex font (`0`-`9`, `A`, `b`, `C`, `d`, `E`, `F`). The lower-case `b`
and `d` glyphs are used so that they remain distinguishable from `8` and `0`.

## Interface

| Port    | Direction | Width | Description                                                  |
|---------|-----------|-------|--------------------------------------------------------------|
| `digit` | in        | 4     | Hex code to display, `0x0`-`0xF`.                            |
| `seg`   | out       | 7     | Active-LOW segments, packed `g f e d c b a` (`seg(0)` = a). |

Active-LOW: a driven `0` lights the segment (common anode tied high, cathode
sinks current when pulled low).

## Toolchain path

VHDL-2008 source synthesisable through the Yosys flow (GHDL front end). The
project is purely combinational and therefore also serves as a minimal,
self-contained GHDL simulation example. OneWare passes `--std=08` via the
`vhdlStandard` manifest key.

## Running in OneWare

Open the folder as a project. The manifest selects `seven_seg_decoder.vhd` as
the top entity and `seven_seg_decoder_tb.vhd` as the testbench. Simulate to run
the self-checking bench; it asserts every code against an independent reference
array and reports `seven_seg_decoder: PASS - 16 cases` on success.

### Standalone container check

```sh
docker run --rm -v "$PWD":/work -w /work fentwums/oss-cad-suite:2026-06-30 \
  bash -lc 'ghdl -a --std=08 --workdir=/tmp seven_seg_decoder.vhd seven_seg_decoder_tb.vhd \
            && ghdl -e --std=08 --workdir=/tmp seven_seg_decoder_tb \
            && ghdl -r --std=08 --workdir=/tmp seven_seg_decoder_tb --assert-level=error'
```
