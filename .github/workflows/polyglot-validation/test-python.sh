#!/bin/bash
# Polyglot SDK Validation - Python
# This script validates the Python AppHost SDK with Redis integration
set -e

echo "=== Python AppHost SDK Validation ==="

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

# Initialize Python AppHost
echo "Creating Python apphost project..."
aspire init --language python --non-interactive -d

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

# Insert Redis line into apphost.py
echo "Configuring apphost.py with Redis..."
if grep -q "builder.build().run()" apphost.py; then
    sed -i '/builder.build().run()/i\# Add Redis cache resource\nredis = builder.add_redis("cache").with_image_registry("netaspireci.azurecr.io")' apphost.py
    echo "✅ Redis configuration added to apphost.py"
fi

echo "=== apphost.py ==="
cat apphost.py

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
