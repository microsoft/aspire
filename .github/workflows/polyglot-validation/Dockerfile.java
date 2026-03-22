# Polyglot SDK Validation - Java
# This Dockerfile sets up an environment for validating the Java AppHost SDK
#
# Usage:
#   docker build -f Dockerfile.java -t polyglot-java .
#   docker run --rm \
#     -v "$(pwd):/workspace" \
#     -v /var/run/docker.sock:/var/run/docker.sock \
#     polyglot-java
#
# Note: Expects self-extracting binary and NuGet artifacts to be pre-downloaded to /workspace/artifacts/
#
FROM mcr.microsoft.com/devcontainers/java:17

# Ensure Yarn APT repository signing key is available (base image includes Yarn repo)
RUN curl -sS https://dl.yarnpkg.com/debian/pubkey.gpg | gpg --dearmor | sudo tee /etc/apt/keyrings/yarn-archive-keyring.gpg > /dev/null

# Install system dependencies (wget, docker CLI, jq for JSON manipulation)
RUN apt-get update && apt-get install -y \
    wget \
    docker.io \
    jq \
    && rm -rf /var/lib/apt/lists/*

# Pre-configure Aspire CLI path
ENV PATH="/root/.aspire/bin:${PATH}"
ENV ASPIRE_CLI_TELEMETRY_OPTOUT=1

WORKDIR /workspace

COPY setup-local-cli.sh /scripts/setup-local-cli.sh
COPY test-java.sh /scripts/test-java.sh
RUN chmod +x /scripts/setup-local-cli.sh /scripts/test-java.sh

# Entrypoint: Set up Aspire CLI and run validation
# Bundle extraction happens lazily on first command that needs the layout
ENTRYPOINT ["/bin/bash", "-c", "\
    set -e && \
    /scripts/setup-local-cli.sh && \
    aspire --nologo config set features:experimentalPolyglot:java true --global && \
    echo '' && \
    echo '=== Running validation ===' && \
    /scripts/test-java.sh \
"]
