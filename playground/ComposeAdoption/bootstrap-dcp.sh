#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || ! "$1" =~ ^[0-9a-f]{40}$ ]]; then
    echo "Usage: bash bootstrap-dcp.sh <reviewed-full-public-microsoft-dcp-commit>" >&2
    exit 2
fi
revision="$1"
for tool in curl tar make go unzip; do
    if ! command -v "$tool" > /dev/null; then
        echo "Building DCP from source requires $tool on PATH." >&2
        exit 1
    fi
done

sample="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$sample/../.." && pwd)"
mkdir -p "$repo/artifacts/compose-dcp"
workspace="$(mktemp -d "$repo/artifacts/compose-dcp/$revision.XXXXXX")"
source="$workspace/source"
mkdir "$source"
echo "Building public microsoft/dcp revision $revision in $workspace"

# Use the public archive without Git credentials or another worker's checkout.
curl --fail --location --silent --show-error --connect-timeout 30 \
    "https://codeload.github.com/microsoft/dcp/tar.gz/$revision" \
    --output "$workspace/source.tar.gz"
tar -xzf "$workspace/source.tar.gz" --strip-components=1 -C "$source"

# The upstream build generates the API code and builds both dcp and dcptun_c.
# Retain the complete tools layout rather than copying just the main executable.
make -C "$source" build COMMIT="$revision" 2>&1 | tee "$workspace/build.log"
tools="$source/bin"
if [[ ! -x "$tools/dcp" || ! -x "$tools/dcptun_c" ]]; then
    echo "DCP build did not produce executable bin/dcp and bin/dcptun_c; inspect $workspace/build.log." >&2
    exit 1
fi
"$tools/dcp" version | tee "$workspace/version.json"
if ! grep -Eq "\"commitHash\"[[:space:]]*:[[:space:]]*\"$revision\"" "$workspace/version.json"; then
    echo "Built DCP does not report requested commit $revision; inspect $workspace/version.json." >&2
    exit 1
fi
printf '%s\n' "$revision" > "$workspace/source-commit"
printf '\nUse these exact tools for the runtime verification:\nexport ASPIRE_DCP_PATH=%q\n' "$tools"
