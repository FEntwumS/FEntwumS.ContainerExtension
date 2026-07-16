# Security Policy

## Supported Versions

| Version | Supported | Verified |
| ------- | --------- | ---------------------- |
| 1.0.x   | Yes       | Yes                    |

## Secure Infrastructure

This project executes user-specified FPGA toolchains inside Docker/Podman containers and adheres to zero-trust principles:
- **SLSA build provenance (L2)** for the published release archive (the release zip is the attestation subject).
- **Deterministic Builds** ensuring verifiable and reproducible binary outputs.
- **Non-root Container Execution** preventing namespace privilege escalation.
- **Static Analysis** enforced via GitHub CodeQL and Trivy.

## Reporting a Vulnerability

If you discover a security vulnerability, please report it responsibly by emailing **<info@mtorun0x7cd.com>** instead of opening a public issue.

You should receive an acknowledgement within **48 hours**. Critical issues affecting container isolation or credential exposure will be prioritized for immediate out-of-band patches.

## Scope & Threat Model

Security-relevant areas in-scope include:
- **Container escape** - Any bypass of the Docker sandbox boundary via extension mounts or config.
- **Credential leakage** - Exposure of host environment variables, SSH keys, or socket paths via telemetry or logs.
- **Path traversal** - Workspace mount injection that accesses files outside the active project directory.
- **Zombie Process Exhaustion** - Unhandled PIDs overloading host PID limits.

Two settings deliberately relax the isolation boundary. Both default to disabled, and a bypass reached through either is not a vulnerability:
- **Allow Native Fallback** - Runs the tool directly on the host when no container engine is reachable, bypassing container isolation by design.
- **Allow Privileged Containers** - Permits `--privileged`, forfeiting the `--cap-drop=ALL` and `no-new-privileges` posture of the default path.

Issues related to host-side Docker daemon misconfiguration (e.g., exposing unauthenticated TCP sockets) or inherent zero-days in the EDA tools themselves (e.g., buffer overflows in `yosys`) are outside the scope of this project, provided they do not facilitate a container escape.
