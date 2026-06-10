#!/usr/bin/env bash
#
# install-formula.sh — build + keg layout for the homebrew-core `aspire`
# formula's install block.
#
# This script owns ALL imperative install logic so that the homebrew-core
# formula's `def install` stays a thin delegation. Keeping the layout here
# (rather than in aspire.rb) means it travels in each release's source
# tarball and is exercised by .github/workflows/homebrew-formula.yml on every
# PR that touches eng/homebrew-core/**. Once submitted, the formula in
# Homebrew/homebrew-core only ever bumps url/sha256/version and the per-RID
# resource SHAs — never this layout — so new releases keep working without a
# hand-edited formula change.
#
# Steps:
#   1. Point dotnet at the staged per-RID SDK (--dotnet-root) so build-cli.sh
#      runs from a known toolset rather than whatever is on PATH.
#   2. Build an unbundled NativeAOT `aspire` plus the unwrapped bundle layout
#      (managed/, dcp/) via build-cli.sh --no-embed.
#   3. Lay the keg out in the canonical sidecar-route shape:
#        <libexec>/aspire                          (+ sibling NativeAOT files)
#        <libexec>/versions/<version>/{managed,dcp}
#        <libexec>/bundle -> versions/<version>    (LayoutDiscovery anchor)
#        <libexec>/.aspire-install.json            ({"source":"brew"})
#        <bin>/aspire -> ../libexec/aspire
#
# Usage:
#   eng/homebrew-core/install-formula.sh \
#       --rid <runtime-id> \
#       --dotnet-root <dir> \
#       --libexec <dir> \
#       --bin <dir> \
#       --version <full-semver>
#
# Required:
#   --rid          Runtime identifier (osx-arm64 | osx-x64 | linux-x64 | linux-arm64).
#   --dotnet-root  Directory holding the staged .NET SDK (the formula stages the
#                  per-RID `dotnet-sdk-<rid>` resource here). Added to PATH and
#                  exported as DOTNET_ROOT for the build.
#   --libexec      The formula's libexec dir. The binary, its sibling runtime
#                  files, and the bundle layout are laid out under here.
#   --bin          The formula's bin dir. A relative symlink to libexec/aspire
#                  is created here.
#   --version      Full version string baked into the binary (so `aspire
#                  --version` reports it) and used as the versions/<version>
#                  layout directory name. Pass the formula's `version` so the
#                  canonical test_do `--version` assertion holds.

set -euo pipefail

RID=
DOTNET_ROOT_DIR=
LIBEXEC=
BIN=
VERSION=

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)         RID="$2";             shift 2 ;;
        --dotnet-root) DOTNET_ROOT_DIR="$2"; shift 2 ;;
        --libexec)     LIBEXEC="$2";         shift 2 ;;
        --bin)         BIN="$2";             shift 2 ;;
        --version)     VERSION="$2";         shift 2 ;;
        -h|--help)
            sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

[[ -n "$RID"             ]] || { echo "Missing --rid"         >&2; exit 2; }
[[ -n "$DOTNET_ROOT_DIR" ]] || { echo "Missing --dotnet-root" >&2; exit 2; }
[[ -n "$LIBEXEC"         ]] || { echo "Missing --libexec"     >&2; exit 2; }
[[ -n "$BIN"             ]] || { echo "Missing --bin"         >&2; exit 2; }
[[ -n "$VERSION"         ]] || { echo "Missing --version"     >&2; exit 2; }

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Run the build from the staged SDK. These only affect this script's process
# tree, not the formula's wider ENV.
export DOTNET_ROOT="$DOTNET_ROOT_DIR"
export PATH="$DOTNET_ROOT_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Build straight into libexec. build-cli.sh --no-embed copies the NativeAOT
# binary and its sibling runtime files plus the unwrapped bundle layout
# (managed/, dcp/) into --output. We reorganize managed/ and dcp/ into the
# versioned layout below. `--channel stable` bakes the release channel into
# the binary's assembly metadata so install-route gating (e.g. `aspire
# doctor`) identifies brew-installed binaries correctly.
"$SCRIPT_DIR/build-cli.sh" \
    --rid "$RID" \
    --output "$LIBEXEC" \
    --channel stable \
    --version "$VERSION" \
    --no-embed

# Move the bundle components into the canonical sidecar-route shape:
# versions/<version>/{managed,dcp}. build-cli.sh placed them at the top of
# libexec alongside the binary; they must live under versions/<version> so the
# `bundle` symlink below resolves to a per-version layout.
VERSION_DIR="$LIBEXEC/versions/$VERSION"
mkdir -p "$VERSION_DIR"
mv "$LIBEXEC/managed" "$VERSION_DIR/managed"
mv "$LIBEXEC/dcp"     "$VERSION_DIR/dcp"

# Canonical sidecar-route layout anchor. LayoutDiscovery's TryInferLayout
# resolves the bundle through {libexec}/bundle/{managed,dcp}. The symlink is
# relative so it survives Homebrew relocating the keg prefix; it also matches
# the shape used by the cask and winget routes.
ln -s "versions/$VERSION" "$LIBEXEC/bundle"

# Install-route sidecar. Consumed by BrewInstallDetection (self-update gate,
# which prints `brew upgrade aspire`) and BundleService.ComputeDefaultExtractDir.
# Same wire value as the cask; the CLI treats them identically via InstallSource.Brew.
printf '{"source":"brew"}\n' > "$LIBEXEC/.aspire-install.json"

# bin/aspire -> ../libexec/aspire. bin and libexec are always sibling dirs
# under the keg prefix in Homebrew, so a relative link is correct and, like
# the bundle symlink, survives keg relocation. This mirrors what
# `bin.install_symlink libexec/"aspire"` produces.
mkdir -p "$BIN"
ln -s "../libexec/aspire" "$BIN/aspire"

echo
echo "=== Keg layout complete ==="
echo "  binary:  $LIBEXEC/aspire"
echo "  bundle:  $LIBEXEC/bundle -> versions/$VERSION"
echo "  symlink: $BIN/aspire -> ../libexec/aspire"
