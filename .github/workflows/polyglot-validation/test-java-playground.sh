#!/bin/bash
# Polyglot SDK Validation - Java validation AppHosts
# Iterates all Java validation AppHosts under tests/Polyglot/Java/,
# runs 'aspire restore --apphost' to regenerate the shared .modules/ SDK, and
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
if [ -d "/workspace/tests/Polyglot/Java" ]; then
    PLAYGROUND_ROOT="/workspace/tests/Polyglot/Java"
elif [ -d "$PWD/tests/Polyglot/Java" ]; then
    PLAYGROUND_ROOT="$(cd "$PWD/tests/Polyglot/Java" && pwd)"
elif [ -d "$SCRIPT_DIR/../../../tests/Polyglot/Java" ]; then
    PLAYGROUND_ROOT="$(cd "$SCRIPT_DIR/../../../tests/Polyglot/Java" && pwd)"
else
    echo "❌ Cannot find tests/Polyglot/Java directory"
    exit 1
fi

echo "Validation root: $PLAYGROUND_ROOT"

mapfile -t APP_HOSTS < <(find "$PLAYGROUND_ROOT" -maxdepth 1 -type f -name '*.apphost.java' | sort)

if [ ${#APP_HOSTS[@]} -eq 0 ]; then
    echo "❌ No Java validation AppHosts found"
    exit 1
fi

echo "Found ${#APP_HOSTS[@]} Java validation AppHosts:"
for app_host in "${APP_HOSTS[@]}"; do
    echo "  - $(basename "$app_host")"
done
echo ""

FAILED=()
PASSED=()

for app_host in "${APP_HOSTS[@]}"; do
    app_name="$(basename "$app_host")"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "Testing: $app_name"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    cd "$PLAYGROUND_ROOT"

    echo "  → aspire restore --apphost $app_name..."
    if ! aspire restore --non-interactive --apphost "$app_host" 2>&1; then
        echo "  ❌ aspire restore failed for $app_name"
        FAILED+=("$app_name (aspire restore)")
        continue
    fi

    echo "  → javac..."
    build_dir="$PLAYGROUND_ROOT/.java-build"
    rm -rf "$build_dir"
    mkdir -p "$build_dir"

    if [ ! -f ".modules/sources.txt" ]; then
        echo "  ❌ No generated Java source list found for $app_name"
        FAILED+=("$app_name (generated sources missing)")
        rm -rf "$build_dir"
        continue
    fi

    temp_apphost="$build_dir/AppHost.java"
    if ! cp "$app_host" "$temp_apphost"; then
        echo "  ❌ Failed to prepare AppHost.java for $app_name"
        FAILED+=("$app_name (prepare apphost)")
        rm -rf "$build_dir"
        continue
    fi

    if ! javac --enable-preview --source 25 -d "$build_dir" @.modules/sources.txt "$temp_apphost" 2>&1; then
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
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed out of ${#APP_HOSTS[@]} AppHosts"
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
