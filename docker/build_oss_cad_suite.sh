#!/usr/bin/env bash
# Builds the hardened oss-cad-suite image for the host platform and runs a Trivy scan.
set -Eeuo pipefail
trap 'echo -e "\nCritical failure at line $LINENO"; exit 1' ERR


SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

NO_CACHE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --no-cache)
            NO_CACHE="--no-cache"
            shift
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: $0 [--no-cache]"
            exit 1
            ;;
    esac
done

RELEASE_TAG="2026-06-30"
RELEASE_DATE="20260630"
IMAGE_NAME="fentwums/oss-cad-suite"

# Force linux-x64 (linux/amd64) for GHDL compatibility.
# Upstream oss-cad-suite-build does not compile GHDL in its ARM64 releases.
ARCH="linux-x64"
DOCKER_PLATFORM="linux/amd64"

echo "==============================================================="
echo "Building ${IMAGE_NAME}:${RELEASE_TAG} (${DOCKER_PLATFORM}) (Hardened)"
echo "==============================================================="

# Build
(
    cd "${SCRIPT_DIR}/oss-cad-suite"
    docker build \
        --platform "${DOCKER_PLATFORM}" \
        ${NO_CACHE} \
        -t "${IMAGE_NAME}:latest" \
        -t "${IMAGE_NAME}:${RELEASE_TAG}" \
        --build-arg RELEASE_TAG="${RELEASE_TAG}" \
        --build-arg RELEASE_DATE="${RELEASE_DATE}" \
        --build-arg ARCH="${ARCH}" \
        .
)

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
