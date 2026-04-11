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
#   docker build -t fentwums/oss-cad-suite:20260410 \
#     --build-arg RELEASE_TAG=2026-04-10 --build-arg RELEASE_DATE=20260410 .
#
# Run:
#   docker run --rm -v $(pwd):/workspace fentwums/oss-cad-suite yosys --version
# ──────────────────────────────────────────────────────────────────────────────

FROM ubuntu:22.04

LABEL maintainer="Mert Torun <mert.torun@smail.th-koeln.de>, <mtorun0x7cd@icloud.com>"
LABEL org.opencontainers.image.title="OSS CAD Suite"
LABEL org.opencontainers.image.description="All-in-one container with 40+ open-source FPGA/EDA tools from YosysHQ"
LABEL org.opencontainers.image.source="https://github.com/YosysHQ/oss-cad-suite-build"

# Release tag format: YYYY-MM-DD (GitHub tag), YYYYMMDD (filename)
ARG RELEASE_TAG=2026-04-10
ARG RELEASE_DATE=20260410
ARG ARCH=linux-x64

ENV DEBIAN_FRONTEND=noninteractive

# Install minimal runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    wget \
    libgomp1 \
    libffi8 \
    && rm -rf /var/lib/apt/lists/*

# Download to file first (avoids pipe failures with large files + GitHub redirects)
RUN wget --retry-connrefused --read-timeout=60 --timeout=30 -qO /tmp/oss-cad-suite.tgz \
    "https://github.com/YosysHQ/oss-cad-suite-build/releases/download/${RELEASE_TAG}/oss-cad-suite-${ARCH}-${RELEASE_DATE}.tgz" \
    && tar xzf /tmp/oss-cad-suite.tgz -C /opt \
    && rm -f /tmp/oss-cad-suite.tgz \
    && rm -rf /opt/oss-cad-suite/lib/python3*/test

# Add tools to PATH
ENV PATH="/opt/oss-cad-suite/bin:${PATH}"

WORKDIR /workspace

# Smoke test — verify key tools are functional
RUN yosys --version && \
    nextpnr-ice40 --version && \
    nextpnr-ecp5 --version && \
    ghdl --version
