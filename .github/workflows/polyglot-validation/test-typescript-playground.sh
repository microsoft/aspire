#!/bin/bash
# Polyglot SDK Validation - TypeScript validation AppHosts
# Iterates all TypeScript validation AppHosts under tests/PolyglotAppHosts/*/TypeScript,
# runs 'aspire restore --apphost' to regenerate the per-integration .aspire/modules/ SDK, and
# type-checks each AppHost with tsgo against the generated API surface.
set -euo pipefail

echo "=== TypeScript Validation AppHost Codegen Validation ==="

if ! command -v aspire &> /dev/null; then
    echo "❌ Aspire CLI not found in PATH"
    exit 1
fi

if ! command -v node &> /dev/null; then
    echo "❌ node not found in PATH (Node.js required)"
    exit 1
fi

if ! command -v npm &> /dev/null; then
    echo "❌ npm not found in PATH (Node.js required)"
    exit 1
fi

if ! command -v npx &> /dev/null; then
    echo "❌ npx not found in PATH (Node.js required to run @typescript/native-preview when tsgo is not installed)"
    exit 1
fi

if command -v tsgo &> /dev/null; then
    TSGO_COMMAND=(tsgo)
elif command -v npx &> /dev/null; then
    TSGO_COMMAND=(npx --yes @typescript/native-preview)
else
    echo "❌ tsgo not found in PATH and npx is unavailable to run @typescript/native-preview"
    exit 1
fi

detect_parallelism() {
    if [ -n "${MAX_PARALLEL_TYPESCRIPT_VALIDATIONS:-}" ]; then
        echo "$MAX_PARALLEL_TYPESCRIPT_VALIDATIONS"
    elif command -v nproc &> /dev/null; then
        nproc
    elif command -v getconf &> /dev/null; then
        getconf _NPROCESSORS_ONLN
    elif command -v sysctl &> /dev/null; then
        sysctl -n hw.ncpu
    else
        echo 4
    fi
}

echo "Aspire CLI version:"
aspire --version
echo "TypeScript checker:"
"${TSGO_COMMAND[@]}" --version

export COREPACK_ENABLE_DOWNLOAD_PROMPT=0

SELECTED_PACKAGE_MANAGER=""
SELECTED_PACKAGE_MANAGER_REASON=""

resolve_package_manager() {
    local app_dir="$1"
    local package_json="$app_dir/package.json"
    local package_manager=""
    local package_manager_name=""

    if [ -f "$package_json" ]; then
        # Parse packageManager values such as "pnpm@10.0.0" or "yarn@4.14.1".
        package_manager=$(node -e '
const fs = require("fs");
const packageJsonPath = process.argv[1];
try {
  const value = JSON.parse(fs.readFileSync(packageJsonPath, "utf8")).packageManager;
  if (typeof value === "string" && value.trim().length > 0) {
    process.stdout.write(value.trim());
  }
} catch {
}
' "$package_json")

        if [ -n "$package_manager" ]; then
            package_manager_name=$(printf '%s' "${package_manager%%@*}" | tr '[:upper:]' '[:lower:]')
            case "$package_manager_name" in
                npm|pnpm|bun)
                    SELECTED_PACKAGE_MANAGER="$package_manager_name"
                    SELECTED_PACKAGE_MANAGER_REASON="packageManager '$package_manager' found in $package_json"
                    return 0
                    ;;
                yarn)
                    if [[ "$package_manager" =~ ^[Yy][Aa][Rr][Nn]@1([^0-9]|$) ]]; then
                        echo "  ❌ Yarn Classic is not supported for TypeScript AppHosts. Upgrade '$package_manager' in $package_json to Yarn 4 or later, or use npm, pnpm, or Bun."
                        return 1
                    fi

                    SELECTED_PACKAGE_MANAGER="yarn"
                    SELECTED_PACKAGE_MANAGER_REASON="packageManager '$package_manager' found in $package_json"
                    return 0
                    ;;
            esac
        fi
    fi

    if [ -f "$app_dir/bun.lock" ]; then
        SELECTED_PACKAGE_MANAGER="bun"
        SELECTED_PACKAGE_MANAGER_REASON="bun.lock found in $app_dir"
        return 0
    fi

    if [ -f "$app_dir/bun.lockb" ]; then
        SELECTED_PACKAGE_MANAGER="bun"
        SELECTED_PACKAGE_MANAGER_REASON="bun.lockb found in $app_dir"
        return 0
    fi

    if [ -f "$app_dir/pnpm-lock.yaml" ]; then
        SELECTED_PACKAGE_MANAGER="pnpm"
        SELECTED_PACKAGE_MANAGER_REASON="pnpm-lock.yaml found in $app_dir"
        return 0
    fi

    if [ -f "$app_dir/yarn.lock" ]; then
        if head -n 5 "$app_dir/yarn.lock" | grep -qi '^# yarn lockfile v1$'; then
            echo "  ❌ Yarn Classic is not supported for TypeScript AppHosts. Upgrade the Yarn lockfile at $app_dir/yarn.lock to Yarn 4 or later, or use npm, pnpm, or Bun."
            return 1
        fi

        SELECTED_PACKAGE_MANAGER="yarn"
        SELECTED_PACKAGE_MANAGER_REASON="yarn.lock found in $app_dir"
        return 0
    fi

    if [ -f "$app_dir/.yarnrc.yml" ]; then
        SELECTED_PACKAGE_MANAGER="yarn"
        SELECTED_PACKAGE_MANAGER_REASON=".yarnrc.yml found in $app_dir"
        return 0
    fi

    if [ -f "$app_dir/package-lock.json" ]; then
        SELECTED_PACKAGE_MANAGER="npm"
        SELECTED_PACKAGE_MANAGER_REASON="package-lock.json found in $app_dir"
        return 0
    fi

    SELECTED_PACKAGE_MANAGER="npm"
    SELECTED_PACKAGE_MANAGER_REASON="no package manager marker found in $app_dir"
    return 0
}

install_command_text() {
    case "$1" in
        npm) echo "npm install --ignore-scripts --no-audit --no-fund" ;;
        pnpm) echo "pnpm install --ignore-workspace" ;;
        yarn) echo "yarn install" ;;
        bun) echo "bun install" ;;
    esac
}

typecheck_command_text() {
    echo "tsgo --noEmit --project tsconfig.json"
}

run_install() {
    case "$1" in
        npm) npm install --ignore-scripts --no-audit --no-fund ;;
        pnpm) pnpm install --ignore-workspace ;;
        yarn) yarn install ;;
        bun) bun install ;;
    esac
}

run_typecheck() {
    "${TSGO_COMMAND[@]}" --noEmit --project tsconfig.json
}

ensure_package_manager_available() {
    local package_manager="$1"
    local required_command="$package_manager"

    if ! command -v "$required_command" &> /dev/null; then
        echo "  ❌ $required_command not found in PATH (required for $(install_command_text "$package_manager"))"
        return 1
    fi
}

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

MAX_PARALLEL="$(detect_parallelism)"
if ! [[ "$MAX_PARALLEL" =~ ^[0-9]+$ ]] || [ "$MAX_PARALLEL" -lt 1 ]; then
    echo "Invalid MAX_PARALLEL_TYPESCRIPT_VALIDATIONS value '$MAX_PARALLEL'; using 1."
    MAX_PARALLEL=1
fi
if [ "$MAX_PARALLEL" -gt "${#APP_DIRS[@]}" ]; then
    MAX_PARALLEL="${#APP_DIRS[@]}"
fi

echo "Running up to $MAX_PARALLEL TypeScript validations in parallel."
echo ""

FAILED=()
PASSED=()
RUN_ROOT="$(mktemp -d)"
trap 'rm -rf "$RUN_ROOT"' EXIT

validate_apphost() {
    local app_dir="$1"
    local integration_name="$2"
    local result_file="$3"

    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    echo "Testing: $integration_name"
    echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    cd "$app_dir"

    if ! resolve_package_manager "$app_dir"; then
        printf 'FAIL|%s|package manager resolution\n' "$integration_name" > "$result_file"
        return 1
    fi

    echo "  → package manager: $SELECTED_PACKAGE_MANAGER ($SELECTED_PACKAGE_MANAGER_REASON)"

    if ! ensure_package_manager_available "$SELECTED_PACKAGE_MANAGER"; then
        printf 'FAIL|%s|%s unavailable\n' "$integration_name" "$SELECTED_PACKAGE_MANAGER" > "$result_file"
        return 1
    fi

    install_command=$(install_command_text "$SELECTED_PACKAGE_MANAGER")
    echo "  → $install_command..."
    install_output=$(run_install "$SELECTED_PACKAGE_MANAGER" 2>&1) || {
        echo "$install_output" | tail -5
        echo "  ❌ $install_command failed for $integration_name"
        printf 'FAIL|%s|%s\n' "$integration_name" "$install_command" > "$result_file"
        return 1
    }
    echo "$install_output" | tail -3

    echo "  → aspire restore --apphost apphost.mts..."
    if ! aspire restore --non-interactive --apphost apphost.mts 2>&1; then
        echo "  ❌ aspire restore failed for $integration_name"
        printf 'FAIL|%s|aspire restore\n' "$integration_name" > "$result_file"
        return 1
    fi

    typecheck_command=$(typecheck_command_text "$SELECTED_PACKAGE_MANAGER")
    echo "  → $typecheck_command..."
    if ! run_typecheck "$SELECTED_PACKAGE_MANAGER" 2>&1; then
        echo "  ❌ tsgo compilation failed for $integration_name"
        printf 'FAIL|%s|tsgo\n' "$integration_name" > "$result_file"
        return 1
    fi

    echo "  ✅ $integration_name passed"
    printf 'PASS|%s|\n' "$integration_name" > "$result_file"
    echo ""
}

BATCH_PIDS=()
BATCH_INDEXES=()
LOG_FILES=()
RESULT_FILES=()

wait_for_batch() {
    local pid
    local index
    local result_file

    for pid in "${BATCH_PIDS[@]}"; do
        wait "$pid" || true
    done

    for index in "${BATCH_INDEXES[@]}"; do
        cat "${LOG_FILES[$index]}"
        result_file="${RESULT_FILES[$index]}"
        if [ ! -f "$result_file" ]; then
            printf 'FAIL|%s|unexpected failure\n' "$(basename "$(dirname "${APP_DIRS[$index]}")")" > "$result_file"
        fi
    done

    BATCH_PIDS=()
    BATCH_INDEXES=()
}

for index in "${!APP_DIRS[@]}"; do
    app_dir="${APP_DIRS[$index]}"
    integration_name="$(basename "$(dirname "$app_dir")")"
    LOG_FILES[$index]="$RUN_ROOT/$index.log"
    RESULT_FILES[$index]="$RUN_ROOT/$index.result"

    validate_apphost "$app_dir" "$integration_name" "${RESULT_FILES[$index]}" > "${LOG_FILES[$index]}" 2>&1 &
    BATCH_PIDS+=("$!")
    BATCH_INDEXES+=("$index")

    if [ "${#BATCH_PIDS[@]}" -ge "$MAX_PARALLEL" ]; then
        wait_for_batch
    fi
done

if [ "${#BATCH_PIDS[@]}" -gt 0 ]; then
    wait_for_batch
fi

for result_file in "${RESULT_FILES[@]}"; do
    IFS='|' read -r status integration_name failure_step < "$result_file"
    if [ "$status" = "PASS" ]; then
        PASSED+=("$integration_name")
    else
        FAILED+=("$integration_name ($failure_step)")
    fi
done

echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "Results: ${#PASSED[@]} passed, ${#FAILED[@]} failed out of ${#APP_DIRS[@]} AppHosts"
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
