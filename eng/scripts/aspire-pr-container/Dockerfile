FROM ubuntu:24.04

ARG DEBIAN_FRONTEND=noninteractive

ENV PATH=/workspace/.aspire/bin:$PATH

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        docker.io \
        gh \
        git \
        libicu74 \
        unzip \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace
CMD ["bash"]
