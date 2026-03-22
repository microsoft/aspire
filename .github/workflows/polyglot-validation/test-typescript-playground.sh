#!/bin/bash
# Polyglot SDK Validation - TypeScript Playground Apps
# Iterates all TypeScript playground apps under playground/polyglot/TypeScript/,
# runs 'aspire restore' to regenerate the .modules/ SDK, and compiles with 'tsc'
# to verify there are no regressions in the codegen API surface.
set -euo pipefail

echo "=== TypeScript Playground Codegen Validation ==="

# Verify prerequisites
if ! command -v aspire &> /dev/null; then
    echo "❌ Aspire CLI not found in PATH"
    exit 1
fi

if ! command -v npx &> /dev/null; then
    echo "❌ npx not found in PATH (Node.js required)"
    exit 1
fi

echo "Aspire CLI version:"
aspire --version

# Locate playground root (works from repo root or /workspace mount)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -d "/workspace/playground/polyglot/TypeScript" ]; then
    PLAYGROUND_ROOT="/workspace/playground/polyglot/TypeScript"
elif [ -d "$SCRIPT_DIR/../../../playground/polyglot/TypeScript" ]; then
    PLAYGROUND_ROOT="$(cd "$SCRIPT_DIR/../../../playground/polyglot/TypeScript" && pwd)"
else
    echo "❌ Cannot find playground/polyglot/TypeScript directory"
    exit 1
fi

echo "Playground root: $PLAYGROUND_ROOT"

# Discover all TypeScript ValidationAppHost apps
APP_DIRS=()
for integration_dir in "$PLAYGROUND_ROOT"/*/; do
    if [ -f "$integration_dir/ValidationAppHost/apphost.ts" ]; then
        APP_DIRS+=("$integration_dir/ValidationAppHost")
    fi
done

if [ ${#APP_DIRS[@]} -eq 0 ]; then
    echo "❌ No TypeScript playground apps found"
    exit 1
fi

echo "Found ${#APP_DIRS[@]} TypeScript playground apps:"
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

    # Step 1: Install npm dependencies
    echo "  → npm install..."
    npm_output=$(npm install --ignore-scripts --no-audit --no-fund 2>&1) || {
        echo "$npm_output" | tail -5
        echo "  ❌ npm install failed for $app_name"
        FAILED+=("$app_name (npm install)")
        continue
    }
    echo "$npm_output" | tail -3

    # Step 2: Regenerate SDK code
    echo "  → aspire restore..."
    if ! aspire restore 2>&1; then
        echo "  ❌ aspire restore failed for $app_name"
        FAILED+=("$app_name (aspire restore)")
        continue
    fi

    # Step 3: Type-check with TypeScript compiler
    echo "  → tsc --noEmit..."
    if ! npx tsc --noEmit 2>&1; then
        echo "  ❌ tsc compilation failed for $app_name"
        FAILED+=("$app_name (tsc)")
        continue
    fi

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

echo "✅ All TypeScript playground apps validated successfully!"
exit 0
