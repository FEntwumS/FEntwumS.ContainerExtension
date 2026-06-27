# docker

Build inputs for the hardened toolchain image the plugin runs by default
(`fentwums/oss-cad-suite`).

| Path | Description |
|---|---|
| `oss-cad-suite/Dockerfile` | Builds the image from a pinned [oss-cad-suite](https://github.com/YosysHQ/oss-cad-suite-build) release. The release tag, date, and a SHA-256 of the downloaded archive are `ARG`s, so the build is reproducible and the download is integrity-checked. Adds `tini` as PID 1 and a non-root `oneware` user. |
| `oss-cad-suite/oss-cad-suite-build` | Submodule pinning the upstream build definitions. |
| `build_oss_cad_suite.sh` | Builds the image for the host platform and runs a Trivy scan. `--no-cache` forces a clean rebuild. |
| `pull_all_images.sh` | Pulls the images the plugin references, for offline use. |

The release tag is single-sourced in the `Dockerfile` `ARG`s and `build_oss_cad_suite.sh`; the CI
workflow (`.github/workflows/docker-build.yml`) builds and scans the same tag on a schedule.
