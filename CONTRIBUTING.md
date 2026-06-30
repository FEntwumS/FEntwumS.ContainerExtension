# Contributing

Contributions are welcome. This document describes how to build, test, and submit changes.

## Branch model

- `main` is the released branch; `dev` is the integration branch. Open pull requests against `dev`.
- Keep changes focused; unrelated cleanups belong in separate pull requests.

## Prerequisites

- .NET SDK 10 (see `global.json` for the pinned version).
- A container engine (Docker, Podman, OrbStack, or Colima) for the gated E2E tests.

## Build and test

```bash
dotnet format OneWare.ContainerExtension.slnx --verify-no-changes
dotnet build  OneWare.ContainerExtension.slnx -warnaserror -c Release
dotnet test   OneWare.ContainerExtension.slnx -c Release
```

The build treats warnings as errors and runs the full analyzer set (`AnalysisMode=All`). A change must
build clean; do not widen the `NoWarn` set in `Directory.Build.props` without a written justification.

Container E2E tests (`tests/ContainerExtension.UnitTests`, marked `[FactIfNoCI]`) are skipped under CI and
run locally when a daemon and the `fentwums/oss-cad-suite` image are available. Mutation testing is
available via `dotnet stryker` (configured through `dotnet-tools.json`).

## Code style

- Follow the existing idiom; `dotnet format` and the in-build analyzers are authoritative.
- Comments explain *why*, not *what*. No banner/divider comments, no narration of the adjacent code.
- Prefer source-generated JSON and regex; keep the plugin assembly reflection-free and AOT-compatible.

## Commit messages

Use conventional-commit style in the imperative mood (`fix:`, `feat:`, `docs:`, `chore:`, `test:`,
`security:`). Keep the subject terse and factual.

## Security

Do not file vulnerabilities as public issues. Follow the private process in
[.github/SECURITY.md](.github/SECURITY.md).
