# uart_tx

> Target: iCEBreaker (iCE40 UP5K) - full flow: synth_ice40 -> nextpnr-ice40 -> icepack; simulate on Icarus Verilog.

Parameterized 8N1 UART transmitter. A four-state FSM (IDLE, START, DATA, STOP)
serializes a byte onto a single line: the line idles high, a frame begins with
one start bit (0), followed by eight data bits transmitted LSB-first, and closes
with one stop bit (1). The bit period is derived at elaboration from
`CLKS_PER_BIT = CLK_FREQ / BAUD`, so the same RTL retargets to any clock and
baud-rate pair.

## Interface

| Port   | Dir | Width | Description                                              |
|--------|-----|-------|----------------------------------------------------------|
| `clk`  | in  | 1     | System clock; all state advances on its rising edge.     |
| `rst`  | in  | 1     | Synchronous, active-high reset to the idle state.        |
| `start`| in  | 1     | One-cycle pulse latches `data` and starts a frame.       |
| `data` | in  | 8     | Payload byte, sampled when `start` is asserted.           |
| `tx`   | out | 1     | Serial output; idles high, registered.                   |
| `busy` | out | 1     | High for the duration of a frame; low when idle.         |

### Parameters

| Parameter  | Default   | Description                          |
|------------|-----------|--------------------------------------|
| `CLK_FREQ` | 1000000   | System clock frequency in Hz.        |
| `BAUD`     | 115200    | Target baud rate.                    |

## Toolchain path

Verilog RTL exercising the Yosys synthesis path of the FEntwumS
ContainerExtension. Functional verification uses Icarus Verilog (`iverilog`
`-g2012` / `vvp`); a Yosys `hierarchy`/`proc`/`opt`/`check` pass confirms the
design elaborates and is synthesizable.

## Running in OneWare

Open the folder as a project in OneWare Studio. The `.fpgaproj` manifest selects
`uart_tx.v` as the top entity and `uart_tx_tb.sv` as the testbench; the
ContainerExtension runs the flow inside the `fentwums/oss-cad-suite` image.

The testbench overrides the parameters to `CLK_FREQ=1000`, `BAUD=100`
(`CLKS_PER_BIT = 10`) to bound the simulation, transmits `0xB3`, samples `tx` at
each bit-period centre, reconstructs the frame, and asserts the start bit, all
eight data bits, and the stop bit. On success it prints
`uart_tx: PASS - ...` and calls `$finish`; any mismatch raises `$error`/`$fatal`.

### Standalone verification

```
docker run --rm -v "$PWD":/work -w /work fentwums/oss-cad-suite:2026-06-30 \
  bash -lc 'iverilog -g2012 -o /tmp/tb.out uart_tx.v uart_tx_tb.sv && vvp /tmp/tb.out \
            && yosys -q -p "read_verilog -sv uart_tx.v; hierarchy -top uart_tx; proc; opt; check"'
```
