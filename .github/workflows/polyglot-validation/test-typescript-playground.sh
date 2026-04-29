#!/bin/bash
# Polyglot SDK Validation - TypeScript validation AppHosts
# Iterates all TypeScript validation AppHosts under tests/PolyglotAppHosts/*/TypeScript,
# runs 'aspire restore --apphost' to regenerate the per-integration .modules/ SDK, and
# type-checks each AppHost against the generated API surface.
set -euo pipefail

echo "=== TypeScript Validation AppHost Codegen Validation ==="

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
done < <(find "$VALIDATION_ROOT" -mindepth 2 -maxdepth 2 -type d -name 'TypeScript' | sort)

if [ ${#APP_DIRS[@]} -eq 0 ]; then
    echo "❌ No TypeScript validation AppHosts found"
    exit 1
fi

echo "Found ${#APP_DIRS[@]} TypeScript validation AppHosts:"
for app_dir in "${APP_DIRS[@]}"; do
    echo "  - $(basename "$(dirname "$app_dir")")"
done
echo ""

FAILED=()
PASSED=()
SKIPPED=()

# Packages that only produce prerelease versions (SuppressFinalPackageVersion=true) cannot be
# restored when the build is stabilized, because aspire restore requests stable versions.
# Skip these until the build infrastructure dynamically computes versions. See #15335.
SKIP_PREVIEW_ONLY=(
    "Aspire.Hosting.Azure.Kusto"
    "Aspire.Hosting.Foundry"
    "Aspire.Hosting.Keycloak"
    "Aspire.Hosting.Kubernetes"
    "Aspire.Hosting.Maui"
)

for app_dir in "${APP_DIRS[@]}"; do
    integration_name="$(basename "$(dirname "$app_dir")")"

    # Check if this integration is in the skip list
    skip=false
    for skip_pkg in "${SKIP_PREVIEW_ONLY[@]}"; do
        if [ "$integration_name" = "$skip_pkg" ]; then
            skip=true
            break
        fi
    done

    if [ "$skip" = true ]; then
        echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
        echo "Testing: $integration_name"
        echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
        echo "  ⏭ Skipping (preview-only package, see #15335)"
        SKIPPED+=("$integration_name")
        echo ""
        continue
    fi

    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "Testing: $integration_name"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    cd "$app_dir"

    echo "  → npm install..."
    npm_output=$(npm install --ignore-scripts --no-audit --no-fund 2>&1) || {
        echo "$npm_output" | tail -5
        echo "  ❌ npm install failed for $integration_name"
        FAILED+=("$integration_name (npm install)")
        echo ""
        continue
    }
    echo "$npm_output" | tail -3

    echo "  → aspire restore --apphost apphost.ts..."
    if ! aspire restore --non-interactive --apphost apphost.ts 2>&1; then
        echo "  ❌ aspire restore failed for $integration_name"
        FAILED+=("$integration_name (aspire restore)")
        echo ""
        continue
    fi

    echo "  → tsc --noEmit --project tsconfig.json..."
    if ! npx tsc --noEmit --project tsconfig.json 2>&1; then
        echo "  ❌ tsc compilation failed for $integration_name"
        FAILED+=("$integration_name (tsc)")
        echo ""
        continue
    fi

    echo "  ✅ $integration_name passed"
    PASSED+=("$integration_name")
    echo ""
done

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed, ${#SKIPPED[@]} skipped out of ${#APP_DIRS[@]} AppHosts"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

if [ ${#FAILED[@]} -gt 0 ]; then
    echo ""
    echo "❌ Failed apps:"
    for f in "${FAILED[@]}"; do
        echo "  - $f"
    done
    exit 1
fi

echo "✅ All TypeScript validation AppHosts validated successfully!"
exit 0
