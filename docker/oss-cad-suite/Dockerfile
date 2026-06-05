# ──────────────────────────────────────────────────────────────────────────────
# OSS CAD Suite — Hardened Docker Image (Tarball-based)
#
# Contains 40+ open-source FPGA/EDA tools. Hardened with tini (PID 1), 
# dropped root privileges, and strict immutable toolchain boundaries.
# ──────────────────────────────────────────────────────────────────────────────

# --- Stage 1: Downloader ---
FROM ubuntu:24.04 AS downloader

ENV DEBIAN_FRONTEND=noninteractive

ARG RELEASE_TAG=2026-05-16
ARG RELEASE_DATE=20260516
ARG ARCH=linux-x64

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    wget \
    tar \
    && rm -rf /var/lib/apt/lists/*

# Hardened wget call with strict timeouts
RUN wget --retry-connrefused --read-timeout=60 --timeout=30 -qO /tmp/oss-cad-suite.tgz \
    "https://github.com/YosysHQ/oss-cad-suite-build/releases/download/${RELEASE_TAG}/oss-cad-suite-${ARCH}-${RELEASE_DATE}.tgz" \
    && mkdir -p /opt \
    && tar xzf /tmp/oss-cad-suite.tgz -C /opt \
    && rm -f /tmp/oss-cad-suite.tgz \
    && rm -rf /opt/oss-cad-suite/lib/python3*/test

# --- Stage 2: Final Image ---
FROM ubuntu:24.04

LABEL org.opencontainers.image.authors="YosysHQ, Mert Torun"
LABEL org.opencontainers.image.title="OSS CAD Suite"
LABEL org.opencontainers.image.description="All-in-one container with 40+ open-source FPGA/EDA tools. Hardened for secure execution."
LABEL org.opencontainers.image.source="https://github.com/YosysHQ/oss-cad-suite-build"

ENV DEBIAN_FRONTEND=noninteractive
ENV LC_ALL=C.UTF-8
ENV LANG=C.UTF-8

# Install runtime dependencies and Tini for proper PID 1 Signal Management
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    libffi8 \
    tini \
    gcc \
    libc6-dev \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean

# Copy extracted tools (immutable, owned by root)
COPY --from=downloader /opt/oss-cad-suite /opt/oss-cad-suite

# Security Hardening: Strip suid/sgid bits from EDA binaries to prevent privilege escalation
RUN find /opt/oss-cad-suite -xdev \( -perm -4000 -o -perm -2000 \) -type f -exec chmod a-s {} + || true

ENV PATH="/opt/oss-cad-suite/bin:${PATH}"
WORKDIR /workspace

RUN (userdel -r ubuntu || true) \
    && (groupdel ubuntu || true) \
    && groupadd -g 1000 oneware \
    && useradd -u 1000 -g oneware -m -s /bin/bash oneware \
    && chown -R oneware:oneware /workspace

# Health Check to ensure container runtime integrity
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD yosys -V || exit 1

# Process Management: Use tini to reap zombie processes properly when forwarding CLI arguments
ENTRYPOINT ["/usr/bin/tini", "--"]

# Execute securely as non-root user
USER oneware

# Smoke Test Validation
RUN yosys --version && \
    nextpnr-ice40 --version && \
    nextpnr-ecp5 --version && \
    ghdl --version
