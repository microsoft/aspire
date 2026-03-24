#!/bin/bash
# Polyglot SDK Validation - Java Playground Apps
# Iterates all Java playground apps under playground/polyglot/Java/,
# runs 'aspire restore' to regenerate the .modules/ SDK, and compiles the
# compact AppHost plus generated Java SDK sources to verify there are no
# regressions in the codegen API surface.
set -euo pipefail

echo "=== Java Playground Codegen Validation ==="

if ! command -v aspire &> /dev/null; then
    echo "❌ Aspire CLI not found in PATH"
    exit 1
fi

if ! command -v javac &> /dev/null; then
    echo "❌ javac not found in PATH (JDK required)"
    exit 1
fi

echo "Aspire CLI version:"
aspire --version

echo "javac version:"
javac -version

SCRIPT_SOURCE="${BASH_SOURCE[0]:-$0}"
SCRIPT_DIR="$(cd "$(dirname "$SCRIPT_SOURCE")" && pwd)"
if [ -d "/workspace/playground/polyglot/Java" ]; then
    PLAYGROUND_ROOT="/workspace/playground/polyglot/Java"
elif [ -d "$PWD/playground/polyglot/Java" ]; then
    PLAYGROUND_ROOT="$(cd "$PWD/playground/polyglot/Java" && pwd)"
elif [ -d "$SCRIPT_DIR/../../../playground/polyglot/Java" ]; then
    PLAYGROUND_ROOT="$(cd "$SCRIPT_DIR/../../../playground/polyglot/Java" && pwd)"
else
    echo "❌ Cannot find playground/polyglot/Java directory"
    exit 1
fi

echo "Playground root: $PLAYGROUND_ROOT"

APP_DIRS=()
for integration_dir in "$PLAYGROUND_ROOT"/*/; do
    if [ -f "$integration_dir/ValidationAppHost/AppHost.java" ]; then
        APP_DIRS+=("$integration_dir/ValidationAppHost")
    fi
done

if [ ${#APP_DIRS[@]} -eq 0 ]; then
    echo "❌ No Java playground apps found"
    exit 1
fi

echo "Found ${#APP_DIRS[@]} Java playground apps:"
for dir in "${APP_DIRS[@]}"; do
    echo "  - $(basename "$(dirname "$dir")")/$(basename "$dir")"
done
echo ""

FAILED=()
PASSED=()

for app_dir in "${APP_DIRS[@]}"; do
    app_name="$(basename "$(dirname "$app_dir")")/$(basename "$app_dir")"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "Testing: $app_name"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    cd "$app_dir"

    echo "  → aspire restore..."
    if ! aspire restore --non-interactive 2>&1; then
        echo "  ❌ aspire restore failed for $app_name"
        FAILED+=("$app_name (aspire restore)")
        continue
    fi

    echo "  → javac..."
    build_dir="$app_dir/.java-build"
    rm -rf "$build_dir"
    mkdir -p "$build_dir"

    if [ ! -f ".modules/sources.txt" ]; then
        echo "  ❌ No generated Java source list found for $app_name"
        FAILED+=("$app_name (generated sources missing)")
        rm -rf "$build_dir"
        continue
    fi

    if ! javac --enable-preview --source 25 -d "$build_dir" @.modules/sources.txt AppHost.java 2>&1; then
        echo "  ❌ javac compilation failed for $app_name"
        FAILED+=("$app_name (javac)")
        rm -rf "$build_dir"
        continue
    fi

    rm -rf "$build_dir"
    echo "  ✅ $app_name passed"
    PASSED+=("$app_name")
    echo ""
done

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed out of ${#APP_DIRS[@]} apps"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if [ ${#FAILED[@]} -gt 0 ]; then
    echo ""
    echo "❌ Failed apps:"
    for f in "${FAILED[@]}"; do
        echo "  - $f"
    done
    exit 1
fi

echo "✅ All Java playground apps validated successfully!"
exit 0
