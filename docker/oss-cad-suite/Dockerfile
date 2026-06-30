# OSS CAD Suite Build

# Stage 1
FROM ubuntu@sha256:53958ec7b67c2c9355df922dd08dbf0360611f8c3cdb656875e81873db9ffdba AS downloader

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    wget \
    tar \
    && rm -rf /var/lib/apt/lists/*

ARG RELEASE_TAG=2026-06-30
ARG RELEASE_DATE=20260630
ARG ARCH=linux-x64
# SHA-256 of the release asset for the pinned tag/date/arch (oss-cad-suite-build does not publish
# per-asset checksums; computed from the tarball). The build fails closed on a mismatch.
ARG OSS_CAD_SUITE_SHA256=28f1018973618732dd35c0f95550ff85ee7687fc3b87860608eeffb86ad7909e

# Download and verify integrity before extraction
RUN wget --progress=dot:giga --retry-connrefused --read-timeout=60 --timeout=30 -O /tmp/oss-cad-suite.tgz \
    "https://github.com/YosysHQ/oss-cad-suite-build/releases/download/${RELEASE_TAG}/oss-cad-suite-${ARCH}-${RELEASE_DATE}.tgz" \
    && echo "${OSS_CAD_SUITE_SHA256}  /tmp/oss-cad-suite.tgz" | sha256sum -c - \
    && mkdir -p /opt \
    && tar xzf /tmp/oss-cad-suite.tgz -C /opt \
    && rm -f /tmp/oss-cad-suite.tgz \
    && rm -rf /opt/oss-cad-suite/lib/python3*/test

# Stage 2
FROM ubuntu@sha256:53958ec7b67c2c9355df922dd08dbf0360611f8c3cdb656875e81873db9ffdba

LABEL org.opencontainers.image.authors="YosysHQ, Mert Torun"
LABEL org.opencontainers.image.title="OSS CAD Suite"
LABEL org.opencontainers.image.description="All-in-one container with 40+ open-source FPGA/EDA tools. Hardened for secure execution."
LABEL org.opencontainers.image.source="https://github.com/YosysHQ/oss-cad-suite-build"

ENV DEBIAN_FRONTEND=noninteractive
ENV LC_ALL=C.UTF-8
ENV LANG=C.UTF-8
ENV PYTHONDONTWRITEBYTECODE=1

# Runtime dependencies. The native toolchain (gcc/g++/make/perl/libc6-dev/zlib1g-dev) is REQUIRED at
# runtime, not build-time scaffolding: the Verilator flow emits C++ from the HDL and compiles it into a
# simulation executable inside the container, so it cannot be dropped without losing Verilator support.
# The in-container-compiler escape exposure is mitigated by the non-privileged, cap-dropped,
# no-new-privileges, non-root, read-only-tooling execution posture. python3 backs SymbiYosys and flow
# scripts. The base image is digest-pinned (above); the apt index it points at still serves rolling
# security updates, so a from-source rebuild is not bit-identical — reproducibility is anchored on the
# built image's recorded digest (the evaluation captures it per run), not on a Dockerfile rebuild.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    libffi8 \
    tini \
    gcc \
    g++ \
    make \
    perl \
    python3 \
    libc6-dev \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean

# Copy tools
COPY --from=downloader /opt/oss-cad-suite /opt/oss-cad-suite

# Strip any suid/sgid bits from the prebuilt tools, then make the toolchain tree read-only. Fail closed:
# a chmod failure aborts the build rather than shipping an unstripped binary (the previous `|| true`
# masked exactly that). `find -exec ... +` still exits 0 when no suid/sgid files are present.
RUN find /opt/oss-cad-suite -xdev \( -perm -4000 -o -perm -2000 \) -type f -exec chmod a-s {} + \
    && chmod -R a-w /opt/oss-cad-suite

ENV PATH="/opt/oss-cad-suite/bin:${PATH}"
WORKDIR /workspace

RUN (userdel -r ubuntu || true) \
    && (groupdel ubuntu || true) \
    && groupadd -g 1000 oneware \
    && useradd -u 1000 -g oneware -m -s /bin/bash oneware \
    && chown -R oneware:oneware /workspace

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD yosys -V || exit 1

ENV TINI_SUBREAPER=1

# Entrypoint
ENTRYPOINT ["/usr/bin/tini", "-s", "--"]

# Non-root user
USER oneware

# Smoke test
RUN yosys --version && \
    nextpnr-ice40 --version && \
    nextpnr-ecp5 --version && \
    ghdl --version
