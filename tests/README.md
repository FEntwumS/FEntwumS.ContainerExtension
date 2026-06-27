# tests

Three complementary test areas.

| Directory | Kind | Description |
|---|---|---|
| `ContainerExtension.UnitTests` | xUnit (.NET) | Deterministic unit tests for validators, command building, path mapping, telemetry serialization/retention/concurrency, registry parsing and SSRF guards, and settings. Container E2E tests live here too, marked `[FactIfNoCI]` so they are skipped under CI and run locally when a daemon and the toolchain image are present. |
| `integration` | Shell + HDL fixtures | HDL designs (iCE40, ECP5, Verilog, VHDL, formal) and two smoke runners: `run_all.sh` exercises the toolchain image directly with `docker run`; `run_harness_smoke.sh` exercises the real `DockerExecutionStrategy` through `ContainerBenchmarkHarness`. |
| `benchmarking_suite` | Python | Cross-platform evaluation pipeline used by the thesis: `benchmark.py` (single workload, confidence intervals, environment capture, artifact hashing), `run_evaluation.py` (the workload matrix over the `integration` fixtures), and `aggregate.py` (per-platform CSV, determinism table, figures). See its own `README.md`. |

Run the .NET tests with `dotnet test OneWare.ContainerExtension.slnx -c Release`. The integration and
benchmarking suites require a container engine.
