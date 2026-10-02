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

Container E2E tests (`tests/ContainerExtension.UnitTests`, marked `[FactIfNoCI]`) are skipped under CI;
[Running the container tests](#running-the-container-tests) lists what they need locally. Mutation testing
is available via `dotnet stryker` (configured through `dotnet-tools.json`).

## Running the container tests

The container tests start real containers and are skipped under CI. Locally they need four things:

1. A running container engine (Docker, Podman, OrbStack, or Colima).
2. The project's toolchain image `fentwums/oss-cad-suite:latest`. It is on no registry: build it with
   Build Local Image in the Container Dashboard or with `docker/build_oss_cad_suite.sh`. The `hdlc/*`
   images some tests use are pulled on their first run.
3. The HDL fixtures, which live in `evaluation/integration/` of the
   [thesis repository](https://github.com/mtorun0x7cd/thesis-fentwums-container-extension). Copy that folder
   to a place outside this repository and point `CONTAINER_EXTENSION_LOCAL_TESTS` at the copy.
4. The intermediate files of the place-and-route tests (`*.json`, `*.asc`, `*.config`). Run `run_all.sh`
   once in the copy; it builds them with the toolchain image. Use a copy, since `run_all.sh` writes into the
   fixture folders.

```bash
export CONTAINER_EXTENSION_LOCAL_TESTS=/path/to/the/copy
dotnet test OneWare.ContainerExtension.slnx -c Release
```

Without the fixtures, the tests that need them fail instead of being skipped.

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
