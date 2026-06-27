# Evaluation summary

Mean execution time (95% CI) per workload and backend, by platform. Overhead is the
container-vs-native ratio where a native baseline was measured.

## macos-arm64 — Apple M4 Max (128.0 GB, 16 cores, Docker 29.4.0)

| Workload | Backend | n | Mean (s) | 95% CI (s) | CV% | Overhead x |
|---|---|---|---|---|---|---|
| pack_ecp5_ecppack | cli | 10 | 0.9766 | [0.9565, 0.9967] | 2.9 |  |
| pack_ecp5_ecppack | strategy | 10 | 1.1487 | [1.1262, 1.1712] | 2.7 |  |
| pack_ice40_icepack | cli | 10 | 0.3446 | [0.3300, 0.3592] | 5.9 |  |
| pack_ice40_icepack | strategy | 10 | 0.5413 | [0.5286, 0.5539] | 3.3 |  |
| pnr_ecp5_nextpnr | cli | 10 | 0.9679 | [0.9426, 0.9931] | 3.6 |  |
| pnr_ecp5_nextpnr | strategy | 10 | 1.1451 | [1.1320, 1.1582] | 1.6 |  |
| pnr_ice40_nextpnr | cli | 10 | 0.5922 | [0.5760, 0.6083] | 3.8 |  |
| pnr_ice40_nextpnr | strategy | 10 | 0.7567 | [0.7435, 0.7699] | 2.4 |  |
| sim_iverilog | cli | 10 | 0.5333 | [0.5124, 0.5542] | 5.5 |  |
| sim_iverilog | strategy | 10 | 0.6935 | [0.6752, 0.7119] | 3.7 |  |
| synth_ecp5_yosys | cli | 10 | 1.0886 | [1.0821, 1.0950] | 0.8 |  |
| synth_ecp5_yosys | strategy | 10 | 1.2787 | [1.2592, 1.2983] | 2.1 |  |
| synth_ice40_yosys | cli | 10 | 1.2428 | [1.2208, 1.2648] | 2.5 |  |
| synth_ice40_yosys | strategy | 10 | 1.4164 | [1.4048, 1.4280] | 1.1 |  |

