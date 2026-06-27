# sync_fifo

> Target: GateMate Eval Board - full flow: synth_gatemate -> nextpnr-himbaechel -> gmpack; simulate on GHDL.

Synchronous (single-clock) first-in/first-out buffer. Tier 4 example combining
a datapath (the circular memory and read/write pointers) with control logic
(the occupancy counter and full/empty interlock). It exercises the
**GHDL -> Yosys** VHDL-2008 path of the FEntwumS ContainerExtension.

## Architecture

A circular buffer of `DEPTH` words is addressed by independent write and read
pointers that wrap modulo `DEPTH`. Because `DEPTH` is a power of two, the
pointers wrap naturally on `ADDR_W = log2(DEPTH)` bits. An explicit occupancy
register `occ` (width `ADDR_W + 1`) tracks the number of stored words and drives
the `full`/`empty` flags and the `count` output. Writes are ignored while
`full`; reads are ignored while `empty`, so the pointers can never cross.
Output data is the registered read location, valid the cycle after `rd_en`.

## Interface

| Signal | Dir | Width                | Description                                    |
|--------|-----|----------------------|------------------------------------------------|
| clk    | in  | 1                    | Clock; all state updates on rising edge        |
| rst    | in  | 1                    | Synchronous reset, active high                 |
| wr_en  | in  | 1                    | Write request; honoured only when not `full`   |
| rd_en  | in  | 1                    | Read request; honoured only when not `empty`   |
| din    | in  | `DATA_WIDTH`         | Write data                                     |
| dout   | out | `DATA_WIDTH`         | Read data, valid one cycle after `rd_en`       |
| full   | out | 1                    | Asserted when occupancy equals `DEPTH`         |
| empty  | out | 1                    | Asserted when occupancy is zero                |
| count  | out | `log2(DEPTH) + 1`    | Current occupancy, range `[0, DEPTH]`          |

Generics: `DATA_WIDTH` (default 8), `DEPTH` (default 16, must be a power of two).

## Verification

`sync_fifo_tb` is self-checking against an independent software queue. It fills
to `full` and confirms an overflowing write is dropped, drains in FIFO order
checking every word and the `empty` flag, confirms a read on `empty` is ignored,
and streams `3*DEPTH+5` elements to force several pointer wraparounds. On success
it reports `sync_fifo: PASS - N cases` and terminates via `std.env.finish`.

## Running in OneWare

Open the folder as a project. OneWare reads `sync_fifo.fpgaproj`, selects the
Yosys toolchain, and passes `--std=08` to GHDL (`vhdlStandard: "08"`). Run the
testbench from the simulation panel, or reproduce the container flow directly:

```sh
docker run --rm -v "$PWD":/work -w /work fentwums/oss-cad-suite:2026-06-26 \
  bash -lc 'ghdl -a --std=08 --workdir=/tmp sync_fifo.vhd sync_fifo_tb.vhd && \
            ghdl -e --std=08 --workdir=/tmp sync_fifo_tb && \
            ghdl -r --std=08 --workdir=/tmp sync_fifo_tb --assert-level=error'
```
