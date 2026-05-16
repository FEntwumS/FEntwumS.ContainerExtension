# Security Policy

## Supported Versions

| Version | Supported |
| ------- | --------- |
| 1.0.x   | ✅ Yes    |

## Reporting a Vulnerability

If you discover a security vulnerability in this project, please report it
responsibly by emailing **<mtorun0x7cd@icloud.com>** instead of opening
a public issue.

You should receive an acknowledgement within **48 hours**. Critical issues
affecting container isolation or credential exposure will be prioritised.

## Scope

This project executes user-specified FPGA toolchains inside Docker containers.
Security-relevant areas include:

- **Container escape** — Any bypass of the Docker sandbox boundary
- **Credential leakage** — Exposure of host environment variables, SSH keys, or socket paths
- **Path traversal** — Workspace mount injection that reads files outside the project directory
- **Telemetry exfiltration** — Unintended transmission of execution logs to external endpoints

Issues related to Docker daemon misconfiguration on the host are outside the
scope of this project.
