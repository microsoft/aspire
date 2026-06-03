#!/usr/bin/env bash
#
# build-cli.sh — single-shot bundled Aspire CLI build for the homebrew-core
# formula's install block.
#
# Produces a NativeAOT `aspire` binary with the Aspire bundle (managed runtime,
# DCP, templates) embedded as a self-extracting resource. Mirrors what
# `eng/Bundle.proj` does in CI, but exposes only the knobs a Homebrew install
# block needs and copies the final artifacts to a caller-chosen output dir.
#
# Usage:
#   eng/homebrew-core/build-cli.sh \
#       --rid <runtime-id> \
#       --output <dest-dir> \
#       [--config <Release|Debug>] \
#       [--channel <stable|dev|pr|...>]
#
# Required:
#   --rid       Runtime identifier (osx-arm64 | osx-x64 | linux-x64 | linux-arm64)
#   --output    Directory the bundled binary + sibling runtime files are
#               copied into. Created if missing.
#
# Optional:
#   --config    Build configuration. Defaults to Release.
#   --channel   AspireCliChannel value baked into the binary's assembly
#               metadata. The CLI's install-route gating reads this to know
#               which release channel produced the binary. Defaults to
#               "stable" — set explicitly for non-stable / dogfood builds.
#
# After success, <output>/aspire is a runnable bundled NativeAOT binary.
# `<output>/aspire setup --install-path <dest>` materializes the bundle layout
# next to it without any further network access.

set -euo pipefail

CONFIG=Release
CHANNEL=stable
RID=
OUTPUT=

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)     RID="$2";     shift 2 ;;
        --output)  OUTPUT="$2";  shift 2 ;;
        --config)  CONFIG="$2";  shift 2 ;;
        --channel) CHANNEL="$2"; shift 2 ;;
        -h|--help)
            sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

[[ -n "$RID"    ]] || { echo "Missing --rid"    >&2; exit 2; }
[[ -n "$OUTPUT" ]] || { echo "Missing --output" >&2; exit 2; }

# Resolve repo root — this script lives at $REPO_ROOT/eng/homebrew-core/build-cli.sh
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

echo "=== aspire CLI bundled build ==="
echo "  rid:     $RID"
echo "  config:  $CONFIG"
echo "  channel: $CHANNEL"
echo "  output:  $OUTPUT"
echo "  source:  $REPO_ROOT"
echo

# Bundle.proj's Build target chains:
#   1. _PublishManagedProjects (Aspire.Managed self-contained)
#   2. _RestoreDcpPackage (per-RID DCP)
#   3. _RunCreateLayout (produces artifacts/bundle/aspire-<ver>-<rid>.tar.gz)
#   4. _PublishNativeCli (NativeAOT publish of Aspire.Cli.csproj with
#                        BundlePayloadPath pointing at the tar.gz above)
dotnet msbuild eng/Bundle.proj \
    /p:TargetRid="$RID" \
    /p:Configuration="$CONFIG" \
    /p:AspireCliChannel="$CHANNEL"

PUBLISH_DIR="$REPO_ROOT/artifacts/bin/Aspire.Cli/$CONFIG/net10.0/$RID/publish"
if [[ ! -x "$PUBLISH_DIR/aspire" ]]; then
    echo "ERROR: expected NativeAOT binary at $PUBLISH_DIR/aspire" >&2
    [[ -d "$PUBLISH_DIR" ]] && ls -la "$PUBLISH_DIR" >&2 || \
        echo "       (publish dir does not exist)" >&2
    exit 1
fi

mkdir -p "$OUTPUT"
# Copy the entire publish dir. The NativeAOT binary needs sibling runtime
# files (libsodium.dylib, satellite locale folders); cherry-picking risks
# breaking on a future addition. Homebrew-installed layouts are small enough
# that the extra ~1.5 MB of xmldoc files is not worth the maintenance.
cp -R "$PUBLISH_DIR"/. "$OUTPUT/"

echo
echo "=== Build complete ==="
ls -lh "$OUTPUT/aspire"
command -v file >/dev/null 2>&1 && file "$OUTPUT/aspire" || true
