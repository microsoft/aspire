#!/bin/bash
# Restore and typecheck the external-host and reusable-package TypeScript playgrounds.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="${1:-$(cd "$SCRIPT_DIR/../../.." && pwd)}"
TEMP_ROOT="$(mktemp -d)"
trap 'rm -rf -- "$TEMP_ROOT"' EXIT

# Restore can update package manifests and generates SDKs. Keep checked-in inputs
# untouched, and retain the library's relative file dependency in the copied tree.
cp -R "$REPOSITORY_ROOT/playground/TsIntegrationSpike" "$TEMP_ROOT/TsIntegrationSpike"
cp -R "$REPOSITORY_ROOT/playground/TsKafkaLib" "$TEMP_ROOT/TsKafkaLib"

if command -v tsgo > /dev/null; then
    TYPECHECK=(tsgo)
else
    TYPECHECK=(npx --yes @typescript/native-preview)
fi

echo "=== External TypeScript integration hosts and generated AppHost SDK ==="
(
    cd "$TEMP_ROOT/TsIntegrationSpike"
    aspire restore --non-interactive --apphost apphost.mts
    "${TYPECHECK[@]}" --noEmit --project tsconfig.apphost.json
    "${TYPECHECK[@]}" --noEmit --project kafka-integration/tsconfig.json
    "${TYPECHECK[@]}" --noEmit --project deno-integration/tsconfig.json
)

echo "=== Reusable TypeScript integration library ==="
(
    cd "$TEMP_ROOT/TsKafkaLib/packages/aspire-kafka"
    aspire restore --non-interactive
    "${TYPECHECK[@]}" --noEmit --project tsconfig.json
)

echo "=== Reusable TypeScript integration consumer ==="
(
    cd "$TEMP_ROOT/TsKafkaLib/app"
    aspire restore --non-interactive --apphost apphost.mts
    "${TYPECHECK[@]}" --noEmit --project tsconfig.json
)

echo "=== TypeScript integration playgrounds validated ==="
