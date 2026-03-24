#!/bin/bash
# Polyglot SDK Validation - Python Playground Apps
# Iterates all Python playground apps under playground/polyglot/Python/,
# runs 'aspire restore' to regenerate the .modules/ SDK, and compiles the
# apphost and generated Python modules to verify there are no syntax regressions.
set -euo pipefail

echo "=== Python Playground Codegen Validation ==="

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
if [ -d "/workspace/playground/polyglot/Python" ]; then
    PLAYGROUND_ROOT="/workspace/playground/polyglot/Python"
elif [ -d "$SCRIPT_DIR/../../../playground/polyglot/Python" ]; then
    PLAYGROUND_ROOT="$(cd "$SCRIPT_DIR/../../../playground/polyglot/Python" && pwd)"
else
    echo "ERROR: Cannot find playground/polyglot/Python directory"
    exit 1
fi

echo "Playground root: $PLAYGROUND_ROOT"

APP_DIRS=()
for integration_dir in "$PLAYGROUND_ROOT"/*/; do
    if [ -f "$integration_dir/ValidationAppHost/apphost.py" ]; then
        APP_DIRS+=("$integration_dir/ValidationAppHost")
    fi
done

if [ ${#APP_DIRS[@]} -eq 0 ]; then
    echo "ERROR: No Python playground apps found"
    exit 1
fi

echo "Found ${#APP_DIRS[@]} Python playground apps:"
for dir in "${APP_DIRS[@]}"; do
    echo "  - $(basename "$(dirname "$dir")")/$(basename "$dir")"
done
echo ""

FAILED=()
PASSED=()

for app_dir in "${APP_DIRS[@]}"; do
    app_name="$(basename "$(dirname "$app_dir")")/$(basename "$app_dir")"
    echo "----------------------------------------"
    echo "Testing: $app_name"
    echo "----------------------------------------"

    cd "$app_dir"

    echo "  -> aspire restore..."
    if ! aspire restore 2>&1; then
        echo "  ERROR: aspire restore failed for $app_name"
        FAILED+=("$app_name (aspire restore)")
        continue
    fi

    if [ ! -f ".modules/aspire_app.py" ]; then
        echo "  ERROR: generated .modules/aspire_app.py missing for $app_name"
        FAILED+=("$app_name (missing .modules/aspire_app.py)")
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
        echo "  ERROR: python compilation failed for $app_name"
        FAILED+=("$app_name (python compile)")
        continue
    fi

    echo "  OK: $app_name passed"
    PASSED+=("$app_name")
    echo ""
done

echo ""
echo "----------------------------------------"
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed out of ${#APP_DIRS[@]} apps"
echo "----------------------------------------"

if [ ${#FAILED[@]} -gt 0 ]; then
    echo ""
    echo "Failed apps:"
    for f in "${FAILED[@]}"; do
        echo "  - $f"
    done
    exit 1
fi

echo "All Python playground apps validated successfully!"
exit 0
