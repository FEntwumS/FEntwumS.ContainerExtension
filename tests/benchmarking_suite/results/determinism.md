# Output-determinism across platforms

Each toolchain artifact (bitstream / netlist) is SHA-256 hashed after generation on every
machine. Identical hashes across platforms are direct evidence that the containerized
architecture makes tool outputs machine-independent.

Platforms compared: macos-arm64

## pack_ecp5_ecppack

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ecp5_blink.bit` | 37e5e6ab1004 | YES |

## pack_ice40_icepack

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ice40_blink.bin` | a8b0b3aa554c | YES |

## pnr_ecp5_nextpnr

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ecp5_blink.config` | ab2628df3a28 | YES |

## pnr_ice40_nextpnr

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ice40_blink.asc` | 31350507657f | YES |

## sim_iverilog

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `Blink.vvp` | a74a467a1a72 | YES |

## synth_ecp5_yosys

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ecp5_blink.json` | dab976a2dc92 | YES |

## synth_ice40_yosys

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ice40_blink.json` | e4749c3dcfe7 | YES |

