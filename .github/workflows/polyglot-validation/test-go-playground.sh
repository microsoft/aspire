#!/bin/bash
# Polyglot SDK Validation - Go validation AppHosts
# Iterates all Go validation AppHosts under tests/PolyglotAppHosts/*/Go,
# runs 'aspire restore --apphost' to regenerate the per-integration .modules/ SDK, and
# compile-checks each AppHost with 'go build ./...' to verify the generated API surface.
set -euo pipefail

echo "=== Go Validation AppHost Codegen Validation ==="

if ! command -v aspire &> /dev/null; then
    echo "❌ Aspire CLI not found in PATH"
    exit 1
fi

if ! command -v go &> /dev/null; then
    echo "❌ go not found in PATH (Go toolchain required)"
    exit 1
fi

echo "Aspire CLI version:"
aspire --version
echo "Go version:"
go version

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -d "/workspace/tests/PolyglotAppHosts" ]; then
    VALIDATION_ROOT="/workspace/tests/PolyglotAppHosts"
elif [ -d "$SCRIPT_DIR/../../../tests/PolyglotAppHosts" ]; then
    VALIDATION_ROOT="$(cd "$SCRIPT_DIR/../../../tests/PolyglotAppHosts" && pwd)"
else
    echo "❌ Cannot find tests/PolyglotAppHosts directory"
    exit 1
fi

echo "Validation root: $VALIDATION_ROOT"

APP_DIRS=()
while IFS= read -r app_dir; do
    APP_DIRS+=("$app_dir")
done < <(find "$VALIDATION_ROOT" -mindepth 2 -maxdepth 2 -type d -name 'Go' | sort)

if [ ${#APP_DIRS[@]} -eq 0 ]; then
    echo "❌ No Go validation AppHosts found"
    exit 1
fi

echo "Found ${#APP_DIRS[@]} Go validation AppHosts:"
for app_dir in "${APP_DIRS[@]}"; do
    echo "  - $(basename "$(dirname "$app_dir")")"
done
echo ""

FAILED=()
PASSED=()

for app_dir in "${APP_DIRS[@]}"; do
    integration_name="$(basename "$(dirname "$app_dir")")"

    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "Testing: $integration_name"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    cd "$app_dir"

    echo "  → aspire restore --apphost apphost.go..."
    if ! aspire restore --non-interactive --apphost apphost.go 2>&1; then
        echo "  ❌ aspire restore failed for $integration_name"
        FAILED+=("$integration_name (aspire restore)")
        echo ""
        continue
    fi

    if [ ! -f ".modules/aspire.go" ]; then
        echo "  ❌ generated .modules/aspire.go missing for $integration_name"
        FAILED+=("$integration_name (missing .modules/aspire.go)")
        echo ""
        continue
    fi

    echo "  → go build ./..."
    if ! go build -buildvcs=false ./... 2>&1; then
        echo "  ❌ go build failed for $integration_name"
        FAILED+=("$integration_name (go build)")
        echo ""
        continue
    fi

    echo "  ✅ $integration_name passed"
    PASSED+=("$integration_name")
    echo ""
done

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed out of ${#APP_DIRS[@]} AppHosts"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if [ ${#FAILED[@]} -gt 0 ]; then
    echo ""
    echo "❌ Failed apps:"
    for f in "${FAILED[@]}"; do
        echo "  - $f"
    done
    exit 1
fi

echo "✅ All Go validation AppHosts validated successfully!"
exit 0
