#!/bin/bash
# Polyglot SDK Validation - Python validation AppHosts
# Iterates all Python validation AppHosts under tests/PolyglotAppHosts/*/Python,
# runs 'aspire restore --apphost' to regenerate the per-integration .aspire/modules/ SDK, and
# compiles and type-checks each AppHost against the generated Python modules. The JavaScript
# fixture also executes its Deno calls without starting an AppHost or external services.
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

if ! command -v pyright &> /dev/null; then
    echo "ERROR: pyright not found in PATH"
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
CHANNEL_SETTINGS_PATH=""
CHANNEL_SETTINGS_BACKUP=""

restore_channel_settings() {
    if [ -z "$CHANNEL_SETTINGS_PATH" ]; then
        return
    fi

    if [ -n "$CHANNEL_SETTINGS_BACKUP" ]; then
        cp "$CHANNEL_SETTINGS_BACKUP" "$CHANNEL_SETTINGS_PATH"
        rm -f "$CHANNEL_SETTINGS_BACKUP"
    else
        rm -f "$CHANNEL_SETTINGS_PATH"
    fi

    CHANNEL_SETTINGS_PATH=""
    CHANNEL_SETTINGS_BACKUP=""
}

pin_validation_channel() {
    if [ -z "${ASPIRE_CLI_CHANNEL:-}" ]; then
        return
    fi

    local settings_path="$PWD/.aspire/settings.json"
    local settings_backup=""
    mkdir -p "$(dirname "$settings_path")"

    if [ -f "$settings_path" ]; then
        settings_backup="$(mktemp)"
        cp "$settings_path" "$settings_backup"
    fi

    CHANNEL_SETTINGS_PATH="$settings_path"
    CHANNEL_SETTINGS_BACKUP="$settings_backup"

    # Validation jobs install the current build into a local hive. Pin the requested
    # project channel so package restore uses that exact hive instead of accepting a
    # newer package version from an ambient daily feed.
    python3 - "$settings_path" "$ASPIRE_CLI_CHANNEL" <<'INNERPY'
import json
import sys
from pathlib import Path

settings_path = Path(sys.argv[1])
settings = json.loads(settings_path.read_text(encoding='utf-8')) if settings_path.exists() else {}
settings['channel'] = sys.argv[2]
settings_path.write_text(json.dumps(settings, indent=2) + '\n', encoding='utf-8')
INNERPY
}

trap restore_channel_settings EXIT

for app_dir in "${APP_DIRS[@]}"; do
    integration_name="$(basename "$(dirname "$app_dir")")"

    echo "----------------------------------------"
    echo "Testing: $integration_name"
    echo "----------------------------------------"

    cd "$app_dir"
    pin_validation_channel

    echo "  -> aspire restore --apphost apphost.py..."
    if ! aspire restore --non-interactive --apphost apphost.py 2>&1; then
        echo "  ERROR: aspire restore failed for $integration_name"
        FAILED+=("$integration_name (aspire restore)")
        echo ""
        restore_channel_settings
        continue
    fi

    if [ ! -f ".aspire/modules/aspire_app.py" ]; then
        echo "  ERROR: generated .aspire/modules/aspire_app.py missing for $integration_name"
        FAILED+=("$integration_name (missing .aspire/modules/aspire_app.py)")
        echo ""
        restore_channel_settings
        continue
    fi

    echo "  -> python syntax validation..."
    if ! python3 - <<'INNERPY'
from pathlib import Path

files = [Path('apphost.py')]
files.extend(sorted(Path('.aspire/modules').rglob('*.py')))
for file in files:
    compile(file.read_text(encoding='utf-8'), str(file), 'exec')
INNERPY
    then
        echo "  ERROR: python compilation failed for $integration_name"
        FAILED+=("$integration_name (python compile)")
        echo ""
        restore_channel_settings
        continue
    fi

    echo "  -> generated SDK type validation..."
    if ! PYTHONPATH="$PWD/.aspire/modules${PYTHONPATH:+:$PYTHONPATH}" \
        pyright \
            --project "$SCRIPT_DIR/pyrightconfig.json" \
            --pythonpath "$(command -v python3)" \
            apphost.py; then
        echo "  ERROR: generated SDK type validation failed for $integration_name"
        FAILED+=("$integration_name (generated SDK type validation)")
        echo ""
        restore_channel_settings
        continue
    fi

    if [ "$integration_name" = "Aspire.Hosting.JavaScript" ]; then
        echo "  -> generated Deno SDK execution..."
        if ! PYTHONPATH="$PWD/.aspire/modules${PYTHONPATH:+:$PYTHONPATH}" python3 - <<'INNERPY'
import apphost
import aspire_app


# Syntax compilation does not resolve Python attributes. This client lets the fixture call the
# generated builder and Deno resource classes while keeping every capability invocation in-process.
class ValidationClient:
    def __init__(self):
        self.next_handle = 0

    def create_handle(self, type_id):
        self.next_handle += 1
        return aspire_app.Handle({
            "$handle": str(self.next_handle),
            "$type": type_id,
        })

    def invoke_capability(self, capability_id, args, kwargs=None):
        if capability_id == "Aspire.Hosting/createBuilder":
            return self.create_handle("Aspire.Hosting/IDistributedApplicationBuilder")
        if capability_id == "Aspire.Hosting.JavaScript/addDenoApp":
            handle = self.create_handle("Aspire.Hosting.JavaScript/DenoAppResource")
            return aspire_app.DenoAppResource(handle, self)

        return next(
            value for value in args.values()
            if isinstance(value, aspire_app.Handle)
        )

    def disconnect(self):
        pass


client = ValidationClient()
options = aspire_app.CreateBuilderOptions()
with aspire_app.DistributedApplicationBuilder(client, options) as builder:
    apphost.add_deno_app(builder)
INNERPY
        then
            echo "  ERROR: generated Deno SDK execution failed for $integration_name"
            FAILED+=("$integration_name (generated Deno SDK execution)")
            echo ""
            restore_channel_settings
            continue
        fi
    fi

    restore_channel_settings
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
