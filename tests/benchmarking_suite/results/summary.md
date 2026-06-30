# Evaluation summary

Mean execution time (95% CI) per workload and backend, by platform. Overhead x is the
container-vs-native ratio (where a native baseline was measured); Ext. overhead x is the
strategy-vs-CLI ratio — the cost the extension adds beyond raw containerization.

## macos-arm64 — Apple M4 Max (128.0 GB, 16 cores, Docker 29.4.0)

| Workload | Backend | n | Mean (s) | 95% CI (s) | CV% | Overhead x | Ext. overhead x |
|---|---|---|---|---|---|---|---|
| lifecycle_floor | cli | 30 | 0.2504 | [0.2391, 0.2617] | 12.1 |  |  |
| lifecycle_floor | strategy | 48 | 0.4520 | [0.4323, 0.4718] | 15.0 |  | 1.81x |
| pack_ecp5_ecppack | cli | 30 | 0.9976 | [0.9895, 1.0058] | 2.2 |  |  |
| pack_ecp5_ecppack | strategy | 30 | 1.1785 | [1.1685, 1.1885] | 2.3 |  | 1.18x |
| pack_ice40_icepack | cli | 30 | 0.3490 | [0.3406, 0.3575] | 6.5 |  |  |
| pack_ice40_icepack | strategy | 30 | 0.5634 | [0.5553, 0.5715] | 3.9 |  | 1.61x |
| pnr_ecp5_nextpnr | cli | 30 | 1.0036 | [0.9925, 1.0146] | 3.0 |  |  |
| pnr_ecp5_nextpnr | strategy | 30 | 1.1866 | [1.1779, 1.1953] | 2.0 |  | 1.18x |
| pnr_ice40_nextpnr | cli | 30 | 0.6309 | [0.6208, 0.6410] | 4.3 |  |  |
| pnr_ice40_nextpnr | strategy | 30 | 0.8207 | [0.8129, 0.8286] | 2.6 |  | 1.30x |
| sim_iverilog | cli | 30 | 0.5283 | [0.5189, 0.5378] | 4.8 |  |  |
| sim_iverilog | strategy | 30 | 0.7130 | [0.7087, 0.7172] | 1.6 |  | 1.35x |
| synth_ecp5_yosys | cli | 30 | 1.1647 | [1.1537, 1.1757] | 2.5 |  |  |
| synth_ecp5_yosys | strategy | 30 | 1.3111 | [1.2955, 1.3268] | 3.2 |  | 1.13x |
| synth_ice40_yosys | cli | 30 | 1.3456 | [1.3327, 1.3584] | 2.5 |  |  |
| synth_ice40_yosys | strategy | 30 | 1.5042 | [1.4951, 1.5134] | 1.6 |  | 1.12x |

