#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# build_oss_cad_suite.sh — Automated builder for the OSS CAD Suite Image
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# Navigate to the oss-cad-suite directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}/oss-cad-suite"

# Define the release versions to build (must match the updated submodule tags)
RELEASE_TAG="2026-05-16"
RELEASE_DATE="20260516"

echo "╔═══════════════════════════════════════════════════════════════╗"
echo "║  Building fentwums/oss-cad-suite:${RELEASE_TAG}     ║"
echo "╚═══════════════════════════════════════════════════════════════╝"

# Execute multi-stage defense-ready docker build
docker build \
    -t "fentwums/oss-cad-suite:latest" \
    -t "fentwums/oss-cad-suite:${RELEASE_TAG}" \
    --build-arg RELEASE_TAG="${RELEASE_TAG}" \
    --build-arg RELEASE_DATE="${RELEASE_DATE}" \
    .

echo ""
echo "✅ Build complete. You can test it using:"
echo "   docker run --rm fentwums/oss-cad-suite yosys --version"
