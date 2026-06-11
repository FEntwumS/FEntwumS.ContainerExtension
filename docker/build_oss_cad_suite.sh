#!/usr/bin/env bash
# ==============================================================================
# build_oss_cad_suite.sh
# ==============================================================================
set -Eeuo pipefail
trap 'echo -e "\nCritical failure at line $LINENO"; exit 1' ERR

export DOCKER_BUILDKIT=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}/oss-cad-suite"

RELEASE_TAG="2026-06-11"
RELEASE_DATE="20260611"
IMAGE_NAME="fentwums/oss-cad-suite"

# Force linux-x64 for GHDL compatibility
ARCH="linux-x64"

echo "==============================================================="
echo "Building ${IMAGE_NAME}:${RELEASE_TAG} (linux/amd64) (Hardened)"
echo "==============================================================="

# Build
docker build \
    --platform linux/amd64 \
    --no-cache \
    -t "${IMAGE_NAME}:latest" \
    -t "${IMAGE_NAME}:${RELEASE_TAG}" \
    --build-arg RELEASE_TAG="${RELEASE_TAG}" \
    --build-arg RELEASE_DATE="${RELEASE_DATE}" \
    --build-arg ARCH="${ARCH}" \
    --build-arg BUILDKIT_INLINE_CACHE=1 \
    .

echo ""
echo "--------------------------------------------------------------"
echo "Running Security Vulnerability Scan (Trivy)..."
if command -v trivy >/dev/null 2>&1; then
    trivy image --severity HIGH,CRITICAL --no-progress "${IMAGE_NAME}:${RELEASE_TAG}"
else
    echo "Trivy not installed. Skipping local vulnerability scan."
fi

echo ""
echo "Build complete. You can test the immutable sandbox using:"
echo "   docker run --rm --security-opt=no-new-privileges:true ${IMAGE_NAME} yosys --version"
