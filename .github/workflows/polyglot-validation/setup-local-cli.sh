#!/bin/bash
# setup-local-cli.sh - Set up Aspire CLI and NuGet packages from local artifacts
# Used by polyglot validation Dockerfiles to use pre-built artifacts from the workflow
#
# The artifact is a self-extracting binary that embeds the runtime, dashboard, dcp, etc.
# Bundle extraction happens lazily on first command that needs the layout.

set -e

ARTIFACTS_DIR="/workspace/artifacts"
BUNDLE_DIR="$ARTIFACTS_DIR/bundle"
NUGETS_DIR="$ARTIFACTS_DIR/nugets"
NUGETS_RID_DIR="$ARTIFACTS_DIR/nugets-rid"
ASPIRE_HOME="$HOME/.aspire"

# Install the self-extracting binary
echo "=== Installing Aspire CLI ==="
if [ ! -f "$BUNDLE_DIR/aspire" ]; then
    echo "ERROR: aspire binary not found at $BUNDLE_DIR/aspire"
    ls -la "$BUNDLE_DIR" 2>/dev/null || echo "Bundle directory does not exist"
    exit 1
fi

mkdir -p "$ASPIRE_HOME/bin"
cp "$BUNDLE_DIR/aspire" "$ASPIRE_HOME/bin/"
chmod +x "$ASPIRE_HOME/bin/aspire"
echo "  ✓ Installed to $ASPIRE_HOME/bin/aspire"

# Extract the embedded bundle so runtime/dotnet and other components are available
# Commands like 'aspire init' and 'aspire add' need the bundled dotnet for NuGet operations
echo "=== Extracting bundle ==="
"$ASPIRE_HOME/bin/aspire" setup || {
    echo "ERROR: aspire setup failed"
    exit 1
}

# Set up NuGet hive
echo "=== Setting up NuGet package hive ==="

SHIPPING_DIR="$NUGETS_DIR/Release/Shipping"
if [ ! -d "$SHIPPING_DIR" ]; then
    SHIPPING_DIR="$NUGETS_DIR"
fi

# Auto-detect PR identity from .nupkg filenames (e.g. "Aspire.Hosting.AppHost.13.4.0-pr.16820.g3703c5c4.nupkg")
# so PR-built packages land in the same hive the CLI's CliExecutionContext.Channel
# resolves to ("pr-<N>"). Main branch validation passes ASPIRE_CLI_CHANNEL=local
# because main-built packages do not carry a PR suffix and local is already a
# local-build channel.
#
# Anchor on Aspire.Hosting.AppHost because:
#   - It is the core MSBuild SDK package every AppHost references; removing/renaming it
#     would require updating every consumer, so it is effectively guaranteed to exist.
#   - It is packable with no <SuppressFinalPackageVersion>, so its version stamp follows
#     the standard "{semver}[-pr.<N>.g<sha>]" shape that the regex below expects.
#   - It only ever lives in the general built-nugets artifact ($SHIPPING_DIR), not the
#     per-RID built-nugets-for-<rid> artifact ($NUGETS_RID_DIR), so we avoid the
#     Aspire.Cli pointer-vs-RID ambiguity (Aspire.Cli.<rid>.<ver>.nupkg also matches
#     "Aspire.Cli.*.nupkg" and could be picked first depending on filesystem order).
SAMPLE_NUPKG=$(find "$SHIPPING_DIR" -maxdepth 4 -name "Aspire.Hosting.AppHost.*.nupkg" 2>/dev/null | head -1)
if [ -z "$SAMPLE_NUPKG" ]; then
    echo "ERROR: Could not find Aspire.Hosting.AppHost.*.nupkg under $SHIPPING_DIR." >&2
    echo "       PR-suffix detection depends on this package being present in the built-nugets artifact." >&2
    echo "       Available .nupkg files under $SHIPPING_DIR (first 20):" >&2
    find "$SHIPPING_DIR" -maxdepth 4 -name "*.nupkg" 2>/dev/null | head -20 >&2
    exit 1
fi

SUFFIX=$(basename "$SAMPLE_NUPKG" | sed -nE 's/.*-(pr\.[0-9]+\.[0-9a-g]+).*\.nupkg$/\1/p')
if [[ "$SUFFIX" =~ ^pr\.([0-9]+)\.[0-9a-g]+$ ]]; then
    HIVE_LABEL="pr-${BASH_REMATCH[1]}"
elif [[ "${ASPIRE_CLI_CHANNEL:-}" =~ ^(daily|staging|local)$ ]]; then
    HIVE_LABEL="$ASPIRE_CLI_CHANNEL"
else
    echo "ERROR: Could not derive PR identity from $(basename "$SAMPLE_NUPKG")." >&2
    echo "       PR validation expects a '-pr.<N>.g<sha>' suffix on the built nupkgs." >&2
    echo "       Non-PR validation must set ASPIRE_CLI_CHANNEL to daily, staging, or local." >&2
    exit 1
fi
HIVE_DIR="$ASPIRE_HOME/hives/$HIVE_LABEL/packages"
echo "  Using hive label: $HIVE_LABEL"
mkdir -p "$HIVE_DIR"

if [ -d "$SHIPPING_DIR" ]; then
    find "$SHIPPING_DIR" -name "*.nupkg" -exec cp {} "$HIVE_DIR/" \;
    echo "  ✓ Copied $(find "$HIVE_DIR" -name "*.nupkg" | wc -l) packages"
fi

if [ -d "$NUGETS_RID_DIR" ]; then
    find "$NUGETS_RID_DIR" -name "*.nupkg" -exec cp {} "$HIVE_DIR/" \;
    echo "  ✓ Copied RID-specific packages"
fi

echo "  Total packages in hive: $(find "$HIVE_DIR" -name "*.nupkg" | wc -l)"

echo ""
echo "=== Aspire CLI setup complete ==="
