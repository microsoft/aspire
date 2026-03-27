#!/bin/bash
# Polyglot SDK Validation - TypeScript validation AppHosts
# Iterates all TypeScript validation AppHosts under tests/Polyglot/TypeScript/,
# runs 'aspire restore --apphost' to regenerate the shared .modules/ SDK, and
# type-checks each AppHost against the generated API surface.
set -euo pipefail

echo "=== TypeScript Validation AppHost Codegen Validation ==="

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

# Locate validation root (works from repo root or /workspace mount)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -d "/workspace/tests/Polyglot/TypeScript" ]; then
    PLAYGROUND_ROOT="/workspace/tests/Polyglot/TypeScript"
elif [ -d "$SCRIPT_DIR/../../../tests/Polyglot/TypeScript" ]; then
    PLAYGROUND_ROOT="$(cd "$SCRIPT_DIR/../../../tests/Polyglot/TypeScript" && pwd)"
else
    echo "❌ Cannot find tests/Polyglot/TypeScript directory"
    exit 1
fi

echo "Validation root: $PLAYGROUND_ROOT"

cd "$PLAYGROUND_ROOT"

echo "→ npm install..."
npm_output=$(npm install --ignore-scripts --no-audit --no-fund 2>&1) || {
    echo "$npm_output" | tail -5
    echo "❌ npm install failed for $PLAYGROUND_ROOT"
    exit 1
}
echo "$npm_output" | tail -3

mapfile -t APP_HOSTS < <(find "$PLAYGROUND_ROOT" -maxdepth 1 -type f -name '*.apphost.ts' | sort)

if [ ${#APP_HOSTS[@]} -eq 0 ]; then
    echo "❌ No TypeScript validation AppHosts found"
    exit 1
fi

echo "Found ${#APP_HOSTS[@]} TypeScript validation AppHosts:"
for app_host in "${APP_HOSTS[@]}"; do
    echo "  - $(basename "$app_host")"
done
echo ""

FAILED=()
PASSED=()
TEMP_TSCONFIG="$PLAYGROUND_ROOT/.validation.tsconfig.json"
trap 'rm -f "$TEMP_TSCONFIG"' EXIT

for app_host in "${APP_HOSTS[@]}"; do
    app_name="$(basename "$app_host")"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "Testing: $app_name"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    # Step 1: Regenerate SDK code for the selected AppHost
    echo "  → aspire restore --apphost $app_name..."
    if ! aspire restore --non-interactive --apphost "$app_host" 2>&1; then
        echo "  ❌ aspire restore failed for $app_name"
        FAILED+=("$app_name (aspire restore)")
        continue
    fi

    cat > "$TEMP_TSCONFIG" <<EOF
{
  "extends": "./tsconfig.json",
  "include": [
    "$app_name",
    ".modules/**/*.ts"
  ]
}
EOF

    # Step 2: Type-check the selected AppHost with the generated modules
    echo "  → tsc --noEmit --project $(basename "$TEMP_TSCONFIG")..."
    if ! npx tsc --noEmit --project "$TEMP_TSCONFIG" 2>&1; then
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

echo "✅ All TypeScript validation AppHosts validated successfully!"
exit 0
