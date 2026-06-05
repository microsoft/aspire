#!/bin/bash
set -eo pipefail
# Installs .NET SDK with retry logic for polyglot validation Dockerfiles

MAX_ATTEMPTS=3
RETRY_DELAY=5
DOTNET_CHANNEL="10.0"

for attempt in $(seq 1 $MAX_ATTEMPTS); do
    echo "=== .NET SDK installation attempt $attempt of $MAX_ATTEMPTS ==="

    if curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel $DOTNET_CHANNEL; then
        # Verify installation
        export PATH="/root/.dotnet:${PATH}"
        export DOTNET_ROOT="/root/.dotnet"

        if dotnet --version > /dev/null 2>&1; then
            echo "=== .NET SDK installation successful ==="
            exit 0
        else
            echo "WARNING: .NET install script succeeded but 'dotnet --version' failed"
        fi
    else
        echo "WARNING: .NET install script failed on attempt $attempt"
    fi

    if [ $attempt -lt $MAX_ATTEMPTS ]; then
        echo "Retrying in $RETRY_DELAY seconds..."
        sleep $RETRY_DELAY
    fi
done

echo "ERROR: .NET SDK installation failed after $MAX_ATTEMPTS attempts"
echo "Please check network connectivity and try again"
exit 1
