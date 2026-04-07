#!/bin/bash
# Verify all deployed JS publish services respond correctly.
# Phase 1: capture all responses unconditionally to diagnostics/
# Phase 2: assert expected content — failures still have full diagnostics

DIAG=diagnostics
mkdir -p "$DIAG"

# Capture docker state
docker ps -a > "$DIAG/docker-ps.txt" 2>&1
docker images > "$DIAG/docker-images.txt" 2>&1

get_port() {
    docker ps --filter "name=$1" --format '{{.Ports}}' | grep -oP '0.0.0.0:\K[0-9]+' | head -1
}

# Wait for API to be ready
for i in $(seq 1 30); do
    curl -sf http://localhost:3001/ > /dev/null 2>&1 && break
    sleep 1
done

# Phase 1: Capture all responses (no -f, so non-200 still writes output)
curl -s http://localhost:3001/ > "$DIAG/api-response.txt" 2>&1

SP=$(get_port staticsite)
echo "staticsite=$SP" >> "$DIAG/ports.txt"
curl -s "http://localhost:$SP/index.html" > "$DIAG/staticsite-response.txt" 2>&1

NP=$(get_port nodeserver)
echo "nodeserver=$NP" >> "$DIAG/ports.txt"
curl -s "http://localhost:$NP/" > "$DIAG/nodeserver-response.txt" 2>&1

MP=$(get_port npmscript)
echo "npmscript=$MP" >> "$DIAG/ports.txt"
curl -s "http://localhost:$MP/" > "$DIAG/npmscript-response.txt" 2>&1

XP=$(get_port nextjs)
echo "nextjs=$XP" >> "$DIAG/ports.txt"
curl -s "http://localhost:$XP/" > "$DIAG/nextjs-response.txt" 2>&1

# Capture container logs
for c in $(docker ps --format '{{.Names}}'); do
    echo "=== $c ===" >> "$DIAG/container-logs.txt"
    docker logs "$c" >> "$DIAG/container-logs.txt" 2>&1
done

# Phase 2: Assert
FAIL=0

check() {
    local name=$1 file=$2 pattern=$3
    if grep -q "$pattern" "$file" 2>/dev/null; then
        echo "${name}_OK"
    else
        echo "${name}_FAIL (expected '$pattern')"
        echo "--- Response was ---"
        cat "$file" 2>/dev/null || echo "(empty)"
        echo "---"
        FAIL=1
    fi
}

check api "$DIAG/api-response.txt" temperatureC
check staticsite "$DIAG/staticsite-response.txt" Weather
check nodeserver "$DIAG/nodeserver-response.txt" PublishAsNodeServer
check npmscript "$DIAG/npmscript-response.txt" PublishAsNpmScript
check nextjs "$DIAG/nextjs-response.txt" "Next.js"

if [ $FAIL -eq 0 ]; then
    echo "ALL_OK"
else
    echo "SOME_FAILED"
    exit 1
fi
