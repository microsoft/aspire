#!/usr/bin/env bash
#
# build-cli.sh — single-shot Aspire CLI build for the homebrew-core
# formula's install block.
#
# By default produces a NativeAOT `aspire` binary with the Aspire bundle
# (managed runtime, DCP, templates) embedded as a self-extracting resource.
# With --no-embed produces an unbundled binary plus a sibling layout
# (`managed/`, `dcp/`) so the caller can lay them out in the canonical
# sidecar-route shape (`versions/<v>/{managed,dcp}` + `bundle` symlink)
# without invoking `aspire setup` at install time.
#
# Usage:
#   eng/homebrew-core/build-cli.sh \
#       --rid <runtime-id> \
#       --output <dest-dir> \
#       [--config <Release|Debug>] \
#       [--channel <stable|dev|pr|...>] \
#       [--no-embed]
#
# Required:
#   --rid       Runtime identifier (osx-arm64 | osx-x64 | linux-x64 | linux-arm64)
#   --output    Directory the binary + sibling runtime files are copied into.
#               With --no-embed, the layout directories (managed/, dcp/) are
#               also placed under <output>. Created if missing.
#
# Optional:
#   --config    Build configuration. Defaults to Release.
#   --channel   AspireCliChannel value baked into the binary's assembly
#               metadata. The CLI's install-route gating reads this to know
#               which release channel produced the binary. Defaults to
#               "stable" — set explicitly for non-stable / dogfood builds.
#   --no-embed  Skip the self-extracting bundle embed. The binary is built
#               without an embedded `bundle.tar.gz` resource; the bundle
#               layout dirs (`managed/`, `dcp/`) are copied into <output>/
#               alongside the binary. Intended for routes with a native
#               install-time hook (homebrew-core's `def install`) that lay
#               out the components themselves and therefore do not need
#               the self-extracting transport. Saves ~100 MB on disk per
#               install and skips the tar.gz creation + runtime extraction
#               steps.

set -euo pipefail

CONFIG=Release
CHANNEL=stable
RID=
OUTPUT=
NO_EMBED=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)       RID="$2";     shift 2 ;;
        --output)    OUTPUT="$2";  shift 2 ;;
        --config)    CONFIG="$2";  shift 2 ;;
        --channel)   CHANNEL="$2"; shift 2 ;;
        --no-embed)  NO_EMBED=true; shift 1 ;;
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

echo "=== aspire CLI build ==="
echo "  rid:      $RID"
echo "  config:   $CONFIG"
echo "  channel:  $CHANNEL"
echo "  output:   $OUTPUT"
echo "  source:   $REPO_ROOT"
echo "  embed:    $([[ "$NO_EMBED" == true ]] && echo "no (sibling layout)" || echo "yes (self-extracting)")"
echo

# Bundle.proj's Build target chains:
#   1. _PublishManagedProjects (Aspire.Managed self-contained)
#   2. _RestoreDcpPackage (per-RID DCP)
#   3. _RunCreateLayout (produces artifacts/bundle/<rid>/{managed,dcp}/ and,
#      unless SkipBundleEmbed=true, the aspire-<ver>-<rid>.tar.gz archive)
#   4. _PublishNativeCli (NativeAOT publish of Aspire.Cli.csproj; embeds
#      BundlePayloadPath unless SkipBundleEmbed=true)
_SKIP_EMBED_ARG=
if [[ "$NO_EMBED" == true ]]; then
    _SKIP_EMBED_ARG="/p:SkipBundleEmbed=true"
fi

dotnet msbuild eng/Bundle.proj \
    /p:TargetRid="$RID" \
    /p:Configuration="$CONFIG" \
    /p:AspireCliChannel="$CHANNEL" \
    $_SKIP_EMBED_ARG

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

if [[ "$NO_EMBED" == true ]]; then
    # Also copy the unwrapped bundle layout (managed/ + dcp/) into the
    # output dir so the caller can lay it out in the canonical sidecar-
    # route shape (versions/<v>/{managed,dcp} + bundle symlink). Without
    # this, an unbundled CLI run from <output>/aspire would find no
    # layout via LayoutDiscovery and the first polyglot command would
    # fail.
    BUNDLE_LAYOUT_DIR="$REPO_ROOT/artifacts/bundle/$RID"
    if [[ ! -d "$BUNDLE_LAYOUT_DIR/managed" ]] || [[ ! -d "$BUNDLE_LAYOUT_DIR/dcp" ]]; then
        echo "ERROR: expected layout dirs at $BUNDLE_LAYOUT_DIR/{managed,dcp}" >&2
        [[ -d "$BUNDLE_LAYOUT_DIR" ]] && ls -la "$BUNDLE_LAYOUT_DIR" >&2 || \
            echo "       (bundle layout dir does not exist)" >&2
        exit 1
    fi
    cp -R "$BUNDLE_LAYOUT_DIR/managed" "$OUTPUT/"
    cp -R "$BUNDLE_LAYOUT_DIR/dcp"     "$OUTPUT/"
    echo "Copied unwrapped layout (managed/, dcp/) into $OUTPUT/"
fi

echo
echo "=== Build complete ==="
ls -lh "$OUTPUT/aspire"
command -v file >/dev/null 2>&1 && file "$OUTPUT/aspire" || true
