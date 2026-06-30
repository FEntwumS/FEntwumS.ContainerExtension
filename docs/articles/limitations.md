# Limitations and Threats to Validity

The central contribution of this work is the transparent-integration architecture: the
`IToolExecutionStrategy` hybrid (`DockerExecutionStrategy`) that OneWare Studio dispatches its
FPGA-tool execution to, running the unmodified invocation in an isolated container without altering the
tool's command line, working directory, or output contract. Overhead and reproducibility measurements are
supporting evidence for that architecture, not the object of study. This document records the known
boundaries of the implementation and the corresponding threats to the validity of the evaluation, so
that measured numbers are read against the conditions that produced them.

The limitations below are properties of the upstream toolchain images, the public board packages, or
deliberate security boundaries — not defects of the integration mechanism. The architecture is
agnostic to which binary it dispatches; the constraints describe which binaries are *available* and
under *what conditions* they run.

## Image architecture and GHDL on arm64

The reference image `fentwums/oss-cad-suite` is published `linux/amd64` only. Upstream
`oss-cad-suite` arm64 releases omit GHDL, and GHDL is a required front end for the VHDL path
(`ghdl --synth` precedes Yosys, and `ghdl` drives VHDL simulation). To keep a single image that
satisfies the full VHDL and Verilog matrix across all device families, the image is built for amd64
only. On an Apple Silicon (arm64) host the container therefore runs under the daemon's binary
emulation layer (qemu via Rosetta/`binfmt`).

The integration layer accommodates this through the **Image Platform** setting
(`ContainerExtensionModule.PlatformSetting`, default `auto`), which is forwarded to both the pull and
the run path. When set, it is attached to `ImagesCreateParameters.Platform` and to `--platform`
on the reconstructed run command (`DockerExecutionStrategy.cs`); the pull path additionally retries
without an explicit platform if the platform-pinned pull fails, falling back to the host
architecture. The configuration guide documents setting **Image Platform** to `linux/amd64` on Apple
Silicon for exactly this reason.

Impact on the evaluation: any overhead measured on an arm64 host that runs the amd64 image is
dominated by instruction-level emulation, which is an artifact of the image-distribution situation,
not of the architecture's integration or container-lifecycle code. Emulated arm64 results must not
be compared directly against native amd64 results, and must not be attributed to the integration
layer. They are reported separately and read as an upper bound under emulation.

## Registry credential forwarding is scoped to the registry host

When the dashboard's image browser queries a private registry over the Distribution v2 API, it may
forward the local Docker credential read from `~/.docker/config.json`. This forwarding is restricted
to the exact registry host. In `RegistryClient.GetDockerAuthHeader`, the stored `auths` entry is
matched against the registry host after stripping the scheme and trailing slash, with Docker Hub
aliases (`docker.io`, `hub.docker.com`) treated as equivalent. When a registry answers a tags request
with a `401` Bearer challenge, the credential is attached to the token endpoint only if the
challenge's `realm` host equals the registry host (`realmHostIsRegistry` in
`FetchGenericV2TagsAsync`); the realm must additionally be an absolute HTTPS URL. The rationale is in
the source: a malicious or compromised registry could otherwise name an arbitrary `realm` host in its
`WWW-Authenticate` challenge and exfiltrate the stored credential.

A consequence by design is that split-auth private registries — where the token service lives on a
different host than the registry endpoint — are not supported for credential forwarding. The image
browser will fall back to unauthenticated access for such registries. This is a security boundary,
not a deficiency: pulling and running a fully qualified private image through the execution strategy
itself is unaffected, since that path delegates to the configured engine and its native credential
store.

## The container E2E suite is gated out of CI

The end-to-end suite in `tests/ContainerExtension.UnitTests/DockerExecutionE2ETests.cs` exercises the
real container path: it pulls images, starts containers, and asserts on streamed output. These tests
are timing-sensitive — they depend on registry latency, image-pull throughput, and daemon
responsiveness under load — and are therefore unreliable on a shared, resource-constrained CI runner.
The `FactIfNoCI` attribute skips them when `GITHUB_ACTIONS == "true"`, with the recorded reason
"Skipped in GitHub Actions to prevent Docker Hub rate limits and image pulling flakiness." The
remainder of the suite (parameter assembly, path mapping, shell escaping, bind validation, telemetry
scrubbing) runs in CI unconditionally.

Impact on the evaluation: container-level behaviour is validated locally against a built
`fentwums/oss-cad-suite` image rather than on every push. CI guarantees the deterministic,
host-only portions of the strategy; it does not certify end-to-end container execution. Reported
end-to-end results come from controlled local runs, not from the CI matrix.

## Scope and non-goals

This artifact is an *execution-integration layer*. Its responsibility is to run tool execution
transparently in a container, mediating connection, image resolution, volume mounting,
resource limits, I/O streaming, and telemetry. The following are explicit non-goals:

- **Tool distribution.** The extension does not build, version, or vendor the FPGA toolchain. It
  consumes whatever image is resolved through the hierarchy (`ONEWARE_DOCKER_IMAGE`, per-tool
  override, default image, hardcoded fallback `hdlc/ghdl:yosys`). The capabilities of a given run are
  the capabilities of the resolved image.
- **A formal security proof of the host daemon.** The implementation hardens the *client* boundary —
  named-pipe server-process trust verification on Windows, socket-owner checks on Unix, critical-path
  bind blocking, workspace path containment that remaps out-of-tree paths to an in-workspace sentinel,
  a non-privileged default (`--cap-drop=ALL`, `--security-opt no-new-privileges`, a PID-count cap),
  SSRF gating on registry hosts, and telemetry credential scrubbing — but it does not, and
  cannot, prove the host container daemon trustworthy. A compromised or misconfigured daemon is
  outside the trust model. The native-fallback path is opt-in and explicitly documented as bypassing
  container isolation.

These boundaries delimit what the evaluation claims: it measures the cost and reproducibility of
*transparent integration and containerized dispatch*, not the security of the underlying engine or
the provenance of the toolchain image.

## Threats to validity

**Emulation overhead (construct validity).** As above, overhead on an arm64 host running the amd64
image conflates the cost of the integration layer with the cost of CPU emulation. To avoid mistaking
an emulation artifact for an architectural cost, emulated and native results are kept distinct, the
host architecture and image digest are captured per run, and cross-machine claims are restricted to
matching-architecture comparisons.

**Per-machine variance (internal validity).** Single-shot timings on a loaded developer machine are
noisy. The benchmark driver (`tests/benchmarking_suite/benchmark.py`) controls this with warmup runs,
CV-adaptive repetition (sampling continues until the relative standard deviation falls below a target
or a cap is reached), 95 % Student-t confidence intervals on every mean, and 2-sigma outlier flagging.
Native-versus-container comparisons use Welch's unpaired t-test, with an interleaved paired mode that
alternates execution backends per iteration so that drift in machine state affects both arms equally.
Residual variance from background load on the host is not eliminated, only bounded and reported. Where
a concrete measured value belongs, it is drawn from `results/summary.csv` rather than stated here.

**Workload selection (external validity).** The example suite is a coverage matrix, not a
representative sample of production designs. It is constructed to drive every back-end tool the
extension containerizes — three synthesis back ends (`synth_gatemate`, `synth_ice40`, `synth_ecp5`),
three routers (`nextpnr-himbaechel`, `nextpnr-ice40`, `nextpnr-ecp5`), three packers (`gmpack`,
`icepack`, `ecppack`), and both simulation front ends (`ghdl`, `iverilog`) — across the GateMate, iCE40,
and ECP5 families and both source languages.
The designs are small interface examples with synthetic pin assignments, not pin-locked reference
designs, so absolute runtimes are short and the relative weight of fixed per-invocation container
overhead is correspondingly larger than it would be for long-running industrial syntheses. The
measured overhead is therefore a conservative (upper-bound) estimate of the steady-state cost on
larger designs, where the fixed container setup amortizes over a longer tool execution.

## Future work

Two internal refactors are recognised but deliberately deferred on this artifact. `DockerExecutionStrategy`
concentrates connection probing, image resolution, command assembly, and stream handling in a single class;
extracting these into dedicated collaborators would improve cohesion. The path-canonicalisation helper is
likewise duplicated across the telemetry, strategy, and command-builder layers. Both are behaviour-preserving
moves with no correctness payoff, and the path-canonicalisation and bind-validation code lies on the
per-invocation execution path the overhead evaluation measures; consolidating it would perturb the measured
subject and require a re-baseline. They are left for a post-evaluation maintenance pass rather than undertaken
on the frozen, benchmarked codebase.
