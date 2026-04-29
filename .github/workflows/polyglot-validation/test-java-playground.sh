#!/bin/bash
# Polyglot SDK Validation - Java validation AppHosts
# Iterates all Java validation AppHosts under tests/PolyglotAppHosts/*/Java,
# runs 'aspire restore --apphost' to regenerate the per-integration .modules/ SDK, and
# compiles each AppHost plus the generated Java SDK sources to verify there are
# no regressions in the codegen API surface.
set -euo pipefail

echo "=== Java Validation AppHost Codegen Validation ==="

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
if [ -d "/workspace/tests/PolyglotAppHosts" ]; then
    VALIDATION_ROOT="/workspace/tests/PolyglotAppHosts"
elif [ -d "$PWD/tests/PolyglotAppHosts" ]; then
    VALIDATION_ROOT="$(cd "$PWD/tests/PolyglotAppHosts" && pwd)"
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
done < <(find "$VALIDATION_ROOT" -mindepth 2 -maxdepth 2 -type d -name 'Java' | sort)

if [ ${#APP_DIRS[@]} -eq 0 ]; then
    echo "❌ No Java validation AppHosts found"
    exit 1
fi

echo "Found ${#APP_DIRS[@]} Java validation AppHosts:"
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
# The "Aspire.Hosting" entry is included because its aspire.config.json transitively depends on
# Aspire.Hosting.Azure.Kubernetes, which is suppressed.
SKIP_PREVIEW_ONLY=(
    "Aspire.Hosting"
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

    echo "  → aspire restore --apphost AppHost.java..."
    if ! aspire restore --non-interactive --apphost AppHost.java 2>&1; then
        echo "  ❌ aspire restore failed for $integration_name"
        FAILED+=("$integration_name (aspire restore)")
        echo ""
        continue
    fi

    echo "  → javac..."
    build_dir="$app_dir/.java-build"
    rm -rf "$build_dir"
    mkdir -p "$build_dir"

    if [ ! -f ".modules/sources.txt" ]; then
        echo "  ❌ No generated Java source list found for $integration_name"
        FAILED+=("$integration_name (generated sources missing)")
        rm -rf "$build_dir"
        echo ""
        continue
    fi

    if ! javac --enable-preview --source 25 -d "$build_dir" @.modules/sources.txt AppHost.java 2>&1; then
        echo "  ❌ javac compilation failed for $integration_name"
        FAILED+=("$integration_name (javac)")
        rm -rf "$build_dir"
        echo ""
        continue
    fi

    rm -rf "$build_dir"
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

echo "✅ All Java validation AppHosts validated successfully!"
exit 0
