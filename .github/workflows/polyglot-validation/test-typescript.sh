#!/bin/bash
# Polyglot SDK Validation - TypeScript
# This script validates the TypeScript AppHost SDK with Redis integration
set -e

echo "=== TypeScript AppHost SDK Validation ==="

# `aspire init --language typescript` installs the scaffolded AppHost's dependencies through the
# guest runtime's package manager, which is invoked without a --registry argument. Without this the
# install resolves through whatever registry the image happens to default to, so the packages this
# job validates would come from outside the approved dotnet-public-npm feed.
#
# Fail closed when the helper is absent: an image that did not ship it must not fall through to
# installing from an unapproved feed.
NPM_REGISTRY_ENV="$(dirname "${BASH_SOURCE[0]}")/npm-registry-env.sh"
if [ ! -f "$NPM_REGISTRY_ENV" ]; then
    echo "❌ $NPM_REGISTRY_ENV is missing, so the approved-feed configuration cannot be applied."
    echo "   Refusing to install packages that would come from an unapproved registry."
    exit 1
fi
# shellcheck source=npm-registry-env.sh
source "$NPM_REGISTRY_ENV"

# Verify aspire CLI is available
if ! command -v aspire &> /dev/null; then
    echo "❌ Aspire CLI not found in PATH"
    exit 1
fi

echo "Aspire CLI version:"
aspire --version

# Create project directory
WORK_DIR=$(mktemp -d)
echo "Working directory: $WORK_DIR"
cd "$WORK_DIR"

# Initialize TypeScript AppHost
echo "Creating TypeScript apphost project..."
aspire init --language typescript --non-interactive -d

# Add Redis integration
echo "Adding Redis integration..."
aspire add Aspire.Hosting.Redis --non-interactive -d 2>&1 || {
    echo "aspire add failed, manually updating settings.json..."
    PKG_VERSION=$(aspire --version | grep -oP '\d+\.\d+\.\d+-.*' | head -1)
    if [ -f ".aspire/settings.json" ]; then
        if command -v jq &> /dev/null; then
            jq '.packages["Aspire.Hosting.Redis"] = "'"$PKG_VERSION"'"' .aspire/settings.json > .aspire/settings.json.tmp && mv .aspire/settings.json.tmp .aspire/settings.json
        fi
        echo "Settings.json updated"
        cat .aspire/settings.json
    fi
}

# Insert Redis line into apphost.mts
echo "Configuring apphost.mts with Redis..."
if grep -q "builder.build().run()" apphost.mts; then
    sed -i '/builder.build().run()/i\// Add Redis cache resource\nconst redis = await builder.addRedis("cache").withImageRegistry("netaspireci.azurecr.io");' apphost.mts
    echo "✅ Redis configuration added to apphost.mts"
fi

echo "=== apphost.mts ==="
cat apphost.mts

# Run the apphost in background
echo "Starting apphost in background..."
aspire run -d > aspire.log 2>&1 &
ASPIRE_PID=$!
echo "Aspire PID: $ASPIRE_PID"

# Poll for Redis container with retries
echo "Polling for Redis container..."
RESULT=1
for i in {1..12}; do
    echo "Attempt $i/12: Checking for Redis container..."
    if docker ps | grep -q -i redis; then
        echo "✅ SUCCESS: Redis container is running!"
        docker ps | grep -i redis
        RESULT=0
        break
    fi
    echo "Redis not found yet, waiting 10 seconds..."
    sleep 10
done

if [ $RESULT -ne 0 ]; then
    echo "❌ FAILURE: Redis container not found after 2 minutes"
    echo "=== Docker containers ==="
    docker ps
    echo "=== Aspire log ==="
    cat aspire.log || true
fi

# Cleanup
echo "Stopping apphost..."
kill -9 $ASPIRE_PID 2>/dev/null || true
rm -rf "$WORK_DIR"

exit $RESULT
