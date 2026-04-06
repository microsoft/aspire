#!/bin/bash
# Polyglot SDK Validation - Python validation AppHosts
# Iterates all Python validation AppHosts under tests/PolyglotAppHosts/*/Python,
# runs 'aspire restore --apphost' to regenerate the per-integration .modules/ SDK, and
# compiles each AppHost with the generated Python modules to verify syntax.
set -euo pipefail

echo "=== Python Validation AppHost Codegen Validation ==="

if ! command -v aspire &> /dev/null; then
    echo "ERROR: Aspire CLI not found in PATH"
    exit 1
fi

if ! command -v python3 &> /dev/null; then
    echo "ERROR: python3 not found in PATH"
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
    echo "ERROR: Cannot find tests/PolyglotAppHosts directory"
    exit 1
fi

echo "Validation root: $VALIDATION_ROOT"

APP_DIRS=()
while IFS= read -r app_dir; do
    APP_DIRS+=("$app_dir")
done < <(find "$VALIDATION_ROOT" -mindepth 2 -maxdepth 2 -type d -name 'Python' | sort)

if [ ${#APP_DIRS[@]} -eq 0 ]; then
    echo "ERROR: No Python validation AppHosts found"
    exit 1
fi

echo "Found ${#APP_DIRS[@]} Python validation AppHosts:"
for app_dir in "${APP_DIRS[@]}"; do
    echo "  - $(basename "$(dirname "$app_dir")")"
done
echo ""

FAILED=()
PASSED=()

for app_dir in "${APP_DIRS[@]}"; do
    integration_name="$(basename "$(dirname "$app_dir")")"

    echo "----------------------------------------"
    echo "Testing: $integration_name"
    echo "----------------------------------------"

    cd "$app_dir"

    echo "  -> aspire restore --apphost apphost.py..."
    if ! aspire restore --non-interactive --apphost apphost.py 2>&1; then
        echo "  ERROR: aspire restore failed for $integration_name"
        FAILED+=("$integration_name (aspire restore)")
        echo ""
        continue
    fi

    if [ ! -f ".modules/aspire_app.py" ]; then
        echo "  ERROR: generated .modules/aspire_app.py missing for $integration_name"
        FAILED+=("$integration_name (missing .modules/aspire_app.py)")
        echo ""
        continue
    fi

    echo "  -> python syntax validation..."
    if ! python3 - <<'INNERPY'
from pathlib import Path

files = [Path('apphost.py')]
files.extend(sorted(Path('.modules').rglob('*.py')))
for file in files:
    compile(file.read_text(encoding='utf-8'), str(file), 'exec')
INNERPY
    then
        echo "  ERROR: python compilation failed for $integration_name"
        FAILED+=("$integration_name (python compile)")
        echo ""
        continue
    fi

    echo "  OK: $integration_name passed"
    PASSED+=("$integration_name")
    echo ""
done

echo ""
echo "----------------------------------------"
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed out of ${#APP_DIRS[@]} AppHosts"
echo "----------------------------------------"

if [ ${#FAILED[@]} -gt 0 ]; then
    echo ""
    echo "Failed apps:"
    for f in "${FAILED[@]}"; do
        echo "  - $f"
    done
    exit 1
fi

echo "All Python validation AppHosts validated successfully!"
exit 0
