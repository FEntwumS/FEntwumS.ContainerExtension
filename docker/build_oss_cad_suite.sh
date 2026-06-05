#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# build_oss_cad_suite.sh — Automated builder for the OSS CAD Suite Image
# ──────────────────────────────────────────────────────────────────────────────
set -Eeuo pipefail
trap 'echo -e "\n❌ Critical failure at line $LINENO"; exit 1' ERR

export DOCKER_BUILDKIT=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}/oss-cad-suite"

RELEASE_TAG="2026-05-16"
RELEASE_DATE="20260516"
IMAGE_NAME="fentwums/oss-cad-suite"

# Force linux/amd64 platform to ensure GHDL (which is only package-supported on linux-x64)
# works consistently with an x86_64 toolchain and linker under emulation on Apple Silicon.
ARCH="linux-x64"

echo "╔═══════════════════════════════════════════════════════════════╗"
echo "║  Building ${IMAGE_NAME}:${RELEASE_TAG} (linux/amd64) (Hardened)   ║"
echo "╚═══════════════════════════════════════════════════════════════╝"

# Execute multi-stage docker build with security hardening
docker build \
    --platform linux/amd64 \
    --no-cache \
    --security-opt=no-new-privileges:true \
    -t "${IMAGE_NAME}:latest" \
    -t "${IMAGE_NAME}:${RELEASE_TAG}" \
    --build-arg RELEASE_TAG="${RELEASE_TAG}" \
    --build-arg RELEASE_DATE="${RELEASE_DATE}" \
    --build-arg ARCH="${ARCH}" \
    .

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "🔍 Running Security Vulnerability Scan (Trivy)..."
if command -v trivy >/dev/null 2>&1; then
    trivy image --severity HIGH,CRITICAL --no-progress "${IMAGE_NAME}:${RELEASE_TAG}"
else
    echo "⚠️ Trivy not installed. Skipping local vulnerability scan."
fi

echo ""
echo "✅ Build complete. You can test the immutable sandbox using:"
echo "   docker run --rm --security-opt=no-new-privileges:true ${IMAGE_NAME} yosys --version"
