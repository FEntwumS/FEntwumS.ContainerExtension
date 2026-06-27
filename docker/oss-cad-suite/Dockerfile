# OSS CAD Suite Build

# Stage 1
FROM ubuntu@sha256:f3d28607ddd78734bb7f71f117f3c6706c666b8b76cbff7c9ff6e5718d46ff64 AS downloader

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    wget \
    tar \
    && rm -rf /var/lib/apt/lists/*

ARG RELEASE_TAG=2026-06-26
ARG RELEASE_DATE=20260626
ARG ARCH=linux-x64
# SHA-256 of the release asset (GitHub-published digest for the pinned tag/date/arch).
# The build fails closed if the downloaded tarball does not match.
ARG OSS_CAD_SUITE_SHA256=091122431af82e0229ee693e1a126096cb88ba8979896a586518af957ae68cf0

# Download and verify integrity before extraction
RUN wget --progress=dot:giga --retry-connrefused --read-timeout=60 --timeout=30 -O /tmp/oss-cad-suite.tgz \
    "https://github.com/YosysHQ/oss-cad-suite-build/releases/download/${RELEASE_TAG}/oss-cad-suite-${ARCH}-${RELEASE_DATE}.tgz" \
    && echo "${OSS_CAD_SUITE_SHA256}  /tmp/oss-cad-suite.tgz" | sha256sum -c - \
    && mkdir -p /opt \
    && tar xzf /tmp/oss-cad-suite.tgz -C /opt \
    && rm -f /tmp/oss-cad-suite.tgz \
    && rm -rf /opt/oss-cad-suite/lib/python3*/test

# Stage 2
FROM ubuntu@sha256:f3d28607ddd78734bb7f71f117f3c6706c666b8b76cbff7c9ff6e5718d46ff64

LABEL org.opencontainers.image.authors="YosysHQ, Mert Torun"
LABEL org.opencontainers.image.title="OSS CAD Suite"
LABEL org.opencontainers.image.description="All-in-one container with 40+ open-source FPGA/EDA tools. Hardened for secure execution."
LABEL org.opencontainers.image.source="https://github.com/YosysHQ/oss-cad-suite-build"

ENV DEBIAN_FRONTEND=noninteractive
ENV LC_ALL=C.UTF-8
ENV LANG=C.UTF-8
ENV PYTHONDONTWRITEBYTECODE=1

# Dependencies
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

# Strip suid/sgid and enforce read-only
RUN find /opt/oss-cad-suite -xdev \( -perm -4000 -o -perm -2000 \) -type f -exec chmod a-s {} + || true \
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
