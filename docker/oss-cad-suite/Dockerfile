# ──────────────────────────────────────────────────────────────────────────────
# OSS CAD Suite — All-in-One Docker Image (Tarball-based)
#
# Contains 40+ open-source FPGA/EDA tools:
#   yosys, ghdl, nextpnr (ice40/ecp5/nexus/machxo2/himbaechel/generic),
#   icestorm, prjtrellis, prjoxide, apicula, openfpgaloader, gtkwave,
#   iverilog, verilator, formal verification solvers, and more.
#
# Build:
#   docker build -t fentwums/oss-cad-suite:latest .
#   docker build -t fentwums/oss-cad-suite:20260516 \
#     --build-arg RELEASE_TAG=2026-05-16 --build-arg RELEASE_DATE=20260516 .
#
# Run:
#   docker run --rm -v $(pwd):/workspace fentwums/oss-cad-suite yosys --version
# ──────────────────────────────────────────────────────────────────────────────

# --- Stage 1: Downloader ---
FROM ubuntu:22.04 AS downloader

ENV DEBIAN_FRONTEND=noninteractive

# Release tag format: YYYY-MM-DD (GitHub tag), YYYYMMDD (filename)
ARG RELEASE_TAG=2026-05-16
ARG RELEASE_DATE=20260516
ARG ARCH=linux-x64

# Install minimal download dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    wget \
    tar \
    && rm -rf /var/lib/apt/lists/*

# Download to file first (avoids pipe failures with large files + GitHub redirects)
RUN wget --retry-connrefused --read-timeout=60 --timeout=30 -qO /tmp/oss-cad-suite.tgz \
    "https://github.com/YosysHQ/oss-cad-suite-build/releases/download/${RELEASE_TAG}/oss-cad-suite-${ARCH}-${RELEASE_DATE}.tgz" \
    && mkdir -p /opt \
    && tar xzf /tmp/oss-cad-suite.tgz -C /opt \
    && rm -f /tmp/oss-cad-suite.tgz \
    && rm -rf /opt/oss-cad-suite/lib/python3*/test

# --- Stage 2: Final Image ---
FROM ubuntu:22.04

LABEL org.opencontainers.image.authors="YosysHQ"
LABEL org.opencontainers.image.title="OSS CAD Suite"
LABEL org.opencontainers.image.description="All-in-one container with 40+ open-source FPGA/EDA tools from YosysHQ"
LABEL org.opencontainers.image.source="https://github.com/YosysHQ/oss-cad-suite-build"

ENV DEBIAN_FRONTEND=noninteractive

# Install minimal runtime dependencies required by OSS CAD Suite
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    libffi8 \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean

# Copy extracted tools from downloader stage (immutable, owned by root)
COPY --from=downloader /opt/oss-cad-suite /opt/oss-cad-suite

# Add tools to PATH
ENV PATH="/opt/oss-cad-suite/bin:${PATH}"

WORKDIR /workspace

# Drop root privileges for defense-ready execution
# Toolchain remains owned by root (immutable sandbox), only workspace is writable
RUN groupadd -g 1000 oneware \
    && useradd -u 1000 -g oneware -m -s /bin/bash oneware \
    && chown -R oneware:oneware /workspace

USER oneware

# Smoke test — verify key tools are functional
RUN yosys --version && \
    nextpnr-ice40 --version && \
    nextpnr-ecp5 --version && \
    ghdl --version
