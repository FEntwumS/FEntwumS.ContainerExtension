# Output-determinism across platforms

Each toolchain artifact (bitstream / netlist) is SHA-256 hashed after generation on every
machine. Identical hashes across platforms are direct evidence that the containerized
architecture makes tool outputs machine-independent.

Platforms compared: macos-arm64

> **Cross-platform determinism requires at least two platforms.** With a single platform
> the matrix below is informational only — an artifact trivially equals itself. Collect a
> second platform before claiming machine-independent outputs.

## pack_ecp5_ecppack

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ecp5_blink.bit` | 37e5e6ab1004 | n/a (1 platform) |

## pack_ice40_icepack

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ice40_blink.bin` | a8b0b3aa554c | n/a (1 platform) |

## pnr_ecp5_nextpnr

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ecp5_blink.config` | ab2628df3a28 | n/a (1 platform) |

## pnr_ice40_nextpnr

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ice40_blink.asc` | 31350507657f | n/a (1 platform) |

## sim_iverilog

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `Blink.vvp` | f2cf898fe9db | n/a (1 platform) |

## synth_ecp5_yosys

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ecp5_blink.json` | 65bb1549bab3 | n/a (1 platform) |

## synth_ice40_yosys

| Artifact | macos-arm64 | Identical? |
|---|---|---|
| `ice40_blink.json` | c9d719c17f01 | n/a (1 platform) |

