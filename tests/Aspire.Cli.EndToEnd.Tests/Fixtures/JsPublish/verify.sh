#!/bin/bash
# Verify all deployed JS publish services respond correctly.
# Phase 1: capture all responses unconditionally to diagnostics/
# Phase 2: assert expected content — failures still have full diagnostics

DIAG=diagnostics
mkdir -p "$DIAG"
COMPOSE_PROJECT=${1:?A Compose project name is required}

capture_container_logs() {
    for c in $(docker ps --filter "label=com.docker.compose.project=$COMPOSE_PROJECT" --format '{{.Names}}'); do
        echo "=== $c ===" >> "$DIAG/container-logs.txt"
        docker logs "$c" >> "$DIAG/container-logs.txt" 2>&1
    done
}
trap capture_container_logs EXIT

# Capture docker state
docker ps -a --filter "label=com.docker.compose.project=$COMPOSE_PROJECT" > "$DIAG/docker-ps.txt" 2>&1

get_port() {
    local service=$1 port
    port=$(docker ps --filter "label=com.docker.compose.project=$COMPOSE_PROJECT" \
        --filter "label=com.docker.compose.service=$service" --format '{{.Ports}}' |
        grep -oP '0.0.0.0:\K[0-9]+' | head -1)
    if [ -z "$port" ]; then
        echo "${service}_port_FAIL (no published port found for $COMPOSE_PROJECT)" >&2
        return 1
    fi
    echo "$port"
}

if ! API_PORT=$(get_port api); then
    exit 1
fi

resolve_published_host() {
    local hosts=(localhost host.docker.internal)
    local gateway_hex gateway host
    gateway_hex=$(awk '$2 == "00000000" { print $3; exit }' /proc/net/route 2>/dev/null)
    if [ -n "$gateway_hex" ]; then
        gateway=$(printf '%d.%d.%d.%d' \
            "0x${gateway_hex:6:2}" \
            "0x${gateway_hex:4:2}" \
            "0x${gateway_hex:2:2}" \
            "0x${gateway_hex:0:2}")
        hosts+=("$gateway")
    fi

    for i in $(seq 1 15); do
        for host in "${hosts[@]}"; do
            if curl -sf --max-time 2 "http://$host:$API_PORT/api/weather" > /dev/null 2>&1; then
                echo "$host"
                return 0
            fi
        done
        sleep 1
    done

    echo "api_host_FAIL (could not reach the published API for $COMPOSE_PROJECT)" >&2
    return 1
}

if ! PUBLISHED_HOST=$(resolve_published_host); then
    exit 1
fi
echo "published_host=$PUBLISHED_HOST" >> "$DIAG/ports.txt"
echo "api=$API_PORT" >> "$DIAG/ports.txt"

wait_for_url() {
    URL=$1
    for i in $(seq 1 30); do
        curl -sf --max-time 5 "$URL" > /dev/null 2>&1 && return 0
        sleep 1
    done
    return 1
}

capture_response() {
    local name=$1 url=$2
    shift 2
    curl -sS --max-time 10 "$@" -o "$DIAG/$name-response.txt" \
        -w '%{http_code}' "$url" > "$DIAG/$name-status.txt" 2> "$DIAG/$name-error.txt"
}

# Phase 1: Capture all responses (no -f, so non-200 still writes output)
curl -s "http://$PUBLISHED_HOST:$API_PORT/api/weather" > "$DIAG/api-response.txt" 2>&1

if ! SP=$(get_port staticsite); then
    exit 1
fi
echo "staticsite=$SP" >> "$DIAG/ports.txt"
wait_for_url "http://$PUBLISHED_HOST:$SP/index.html" || true
capture_response staticsite "http://$PUBLISHED_HOST:$SP/index.html"
capture_response staticsite-deep "http://$PUBLISHED_HOST:$SP/test"
capture_response staticsite-nested "http://$PUBLISHED_HOST:$SP/test/nested?query=value"
capture_response staticsite-head "http://$PUBLISHED_HOST:$SP/test" -I
capture_response staticsite-post "http://$PUBLISHED_HOST:$SP/test" -X POST
capture_response staticsite-missing-asset "http://$PUBLISHED_HOST:$SP/missing.js"
capture_response staticsite-api "http://$PUBLISHED_HOST:$SP/api/weather"
capture_response staticsite-missing-api "http://$PUBLISHED_HOST:$SP/api/missing"

if ! DP=$(get_port static-no-fallback); then
    exit 1
fi
echo "static-no-fallback=$DP" >> "$DIAG/ports.txt"
wait_for_url "http://$PUBLISHED_HOST:$DP/index.html" || true
capture_response static-no-fallback "http://$PUBLISHED_HOST:$DP/index.html"
capture_response static-no-fallback-deep "http://$PUBLISHED_HOST:$DP/test"

if ! NP=$(get_port nodeserver); then
    exit 1
fi
echo "nodeserver=$NP" >> "$DIAG/ports.txt"
wait_for_url "http://$PUBLISHED_HOST:$NP/" || true
curl -s "http://$PUBLISHED_HOST:$NP/" > "$DIAG/nodeserver-response.txt" 2>&1

if ! MP=$(get_port npmscript); then
    exit 1
fi
echo "npmscript=$MP" >> "$DIAG/ports.txt"
wait_for_url "http://$PUBLISHED_HOST:$MP/" || true
curl -s "http://$PUBLISHED_HOST:$MP/" > "$DIAG/npmscript-response.txt" 2>&1

if ! XP=$(get_port nextjs); then
    exit 1
fi
echo "nextjs=$XP" >> "$DIAG/ports.txt"
wait_for_url "http://$PUBLISHED_HOST:$XP/" || true
curl -s "http://$PUBLISHED_HOST:$XP/" > "$DIAG/nextjs-response.txt" 2>&1

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

check_same() {
    local name=$1 actual=$2 expected=$3
    if cmp -s "$actual" "$expected"; then
        echo "${name}_OK"
    else
        echo "${name}_FAIL (response differs from index.html)"
        FAIL=1
    fi
}

check api "$DIAG/api-response.txt" temperatureC
check staticsite "$DIAG/staticsite-response.txt" Weather
check staticsite_status "$DIAG/staticsite-status.txt" '^200$'
check staticsite_deep_status "$DIAG/staticsite-deep-status.txt" '^200$'
check_same staticsite_deep "$DIAG/staticsite-deep-response.txt" "$DIAG/staticsite-response.txt"
check staticsite_nested_status "$DIAG/staticsite-nested-status.txt" '^200$'
check_same staticsite_nested "$DIAG/staticsite-nested-response.txt" "$DIAG/staticsite-response.txt"
check staticsite_head "$DIAG/staticsite-head-status.txt" '^200$'
check staticsite_post "$DIAG/staticsite-post-status.txt" '^405$'
check staticsite_missing_asset "$DIAG/staticsite-missing-asset-status.txt" '^404$'
check staticsite_api_status "$DIAG/staticsite-api-status.txt" '^200$'
check staticsite_api "$DIAG/staticsite-api-response.txt" temperatureC
check staticsite_missing_api_status "$DIAG/staticsite-missing-api-status.txt" '^404$'
check staticsite_missing_api "$DIAG/staticsite-missing-api-response.txt" 'API route not found'
check static_no_fallback "$DIAG/static-no-fallback-response.txt" Weather
check static_no_fallback_deep "$DIAG/static-no-fallback-deep-status.txt" '^404$'
check nodeserver "$DIAG/nodeserver-response.txt" PublishAsNodeServer
check npmscript "$DIAG/npmscript-response.txt" PublishAsPackageScript
check nextjs "$DIAG/nextjs-response.txt" "Next.js"

if [ $FAIL -eq 0 ]; then
    echo "ALL_OK"
else
    echo "SOME_FAILED"
    exit 1
fi
