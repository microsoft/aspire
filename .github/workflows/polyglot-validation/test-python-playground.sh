#!/bin/bash
# Polyglot SDK Validation - Python validation AppHosts
# Iterates all Python validation AppHosts under tests/Polyglot/Python/,
# runs 'aspire restore --apphost' to regenerate the shared .modules/ SDK, and
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
if [ -d "/workspace/tests/Polyglot/Python" ]; then
    PLAYGROUND_ROOT="/workspace/tests/Polyglot/Python"
elif [ -d "$SCRIPT_DIR/../../../tests/Polyglot/Python" ]; then
    PLAYGROUND_ROOT="$(cd "$SCRIPT_DIR/../../../tests/Polyglot/Python" && pwd)"
else
    echo "ERROR: Cannot find tests/Polyglot/Python directory"
    exit 1
fi

echo "Validation root: $PLAYGROUND_ROOT"

mapfile -t APP_HOSTS < <(find "$PLAYGROUND_ROOT" -maxdepth 1 -type f -name '*.apphost.py' | sort)

if [ ${#APP_HOSTS[@]} -eq 0 ]; then
    echo "ERROR: No Python validation AppHosts found"
    exit 1
fi

echo "Found ${#APP_HOSTS[@]} Python validation AppHosts:"
for app_host in "${APP_HOSTS[@]}"; do
    echo "  - $(basename "$app_host")"
done
echo ""

FAILED=()
PASSED=()

for app_host in "${APP_HOSTS[@]}"; do
    app_name="$(basename "$app_host")"
    echo "----------------------------------------"
    echo "Testing: $app_name"
    echo "----------------------------------------"

    cd "$PLAYGROUND_ROOT"

    echo "  -> aspire restore --apphost $app_name..."
    if ! aspire restore --non-interactive --apphost "$app_host" 2>&1; then
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
    if ! python3 - "$app_name" <<'INNERPY'
from pathlib import Path
import sys

files = [Path(sys.argv[1])]
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
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed out of ${#APP_HOSTS[@]} AppHosts"
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
