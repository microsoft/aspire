#!/usr/bin/env bash
# Stabilization smoke test: aspire init + aspire restore against the locally-built stable feed.
#
# Purpose
# -------
# This is a moderate-coverage smoke that lives outside the regular CI test path so it can
# always exercise the "everything is stable" build shape, regardless of whether the rest of
# the PR's CI is running on prerelease versions (which it is — see ci.yml). It catches:
#
#   * Restore-time failures for stable AppHosts referencing packages whose csproj sets
#     SuppressFinalPackageVersion=true (the bucket of issues that historically forced 13.1
#     / 13.2 / 13.3 stabilization PRs to hand-skip whole test classes — see #15335).
#   * Template version-pinning regressions where `aspire init` emits an SDK or package
#     reference that doesn't resolve against the stable feed.
#
# Prerequisites
# -------------
# This script assumes a stabilized pack has already populated $REPO_ROOT/artifacts/packages
# (the ci.yml stabilization_check job does this in the preceding step via
# `./build.sh -pack /p:StabilizePackageVersion=true ...`). For local repro, run that build
# first.
#
# Local repro
# -----------
#   ./build.sh -pack -p:StabilizePackageVersion=true -p:SkipTestProjects=true \
#              -p:SkipPlaygroundProjects=true -p:SkipNativeBuild=true
#   ./eng/scripts/stabilization-smoke-init-restore.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
LOCAL_FEED="$REPO_ROOT/artifacts/packages/Debug/Shipping"
ASPIRE_CLI_PROJECT="$REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj"
# Use the repo-local SDK that restore.sh / build.sh install under .dotnet/. The wrapper script
# also sets DOTNET_SKIP_FIRST_TIME_EXPERIENCE and resolves the SDK version from global.json,
# which is what we need on CI runners that don't have a system-wide .NET 10 SDK.
DOTNET="$REPO_ROOT/dotnet.sh"

echo "=== Stabilization smoke: aspire init + restore ==="
echo "Repo root:   $REPO_ROOT"
echo "Local feed:  $LOCAL_FEED"

if [ ! -d "$LOCAL_FEED" ]; then
    echo "❌ Local feed not found at: $LOCAL_FEED"
    echo "   Run a stabilized pack first (see prerequisites at the top of this script)."
    exit 1
fi

# Confirm the feed actually contains stabilized Aspire packages (i.e. e.g. Aspire.Hosting.13.X.0.nupkg
# without a -dev/-preview suffix). Otherwise this smoke would be checking the wrong shape.
if ! ls "$LOCAL_FEED"/Aspire.Hosting.[0-9]*.[0-9]*.[0-9]*.nupkg > /dev/null 2>&1; then
    echo "❌ No stable Aspire.Hosting.*.nupkg in $LOCAL_FEED. Did pack run with StabilizePackageVersion=true?"
    echo "   Found nupkgs:"
    ls "$LOCAL_FEED"/Aspire.Hosting.*.nupkg 2>/dev/null | head -5 || true
    exit 1
fi

WORK_DIR="$(mktemp -d -t aspire-stab-smoke-XXXXXXXX)"
# Clean up on any exit; -f tolerates the dir being already gone if cleanup ran inside the script.
trap 'rm -rf "$WORK_DIR"' EXIT
echo "Work dir:    $WORK_DIR"

# Build the CLI project up-front (under stabilization) so subsequent `dotnet run` invocations
# from inside the temp dir don't trigger a build in the test working directory (which would
# pull in the temp dir's NuGet.config and try to restore the CLI's own dependencies against
# our locally-only feed).
echo ""
echo "→ Building Aspire.Cli (stabilized) for use as the smoke driver"
(
    cd "$REPO_ROOT"
    "$DOTNET" build "$ASPIRE_CLI_PROJECT" \
        -c Debug \
        -p:StabilizePackageVersion=true \
        --nologo -v quiet
)

# Helper: invoke the CLI from anywhere. We use --no-build because we just built above; this
# also prevents `dotnet run` from re-resolving NuGet against the temp-dir NuGet.config.
# --no-launch-profile keeps src/Aspire.Cli/Properties/launchSettings.json out of the picture
# so the CLI receives exactly the args we pass after `--`.
run_aspire() {
    (
        cd "$REPO_ROOT"
        "$DOTNET" run --project "$ASPIRE_CLI_PROJECT" \
            -c Debug \
            --no-build \
            --no-launch-profile \
            -p:StabilizePackageVersion=true \
            -- "$@"
    )
}

echo ""
echo "→ aspire init --language csharp --suppress-agent-init"
# --language csharp suppresses the language prompt.
# --suppress-agent-init suppresses the agent-skill install prompt.
# Together these make init non-interactive (the two prompts above are the only ones init
# raises by default — see InitCommand.cs:97-105 and InitCommand.cs:162-165).
# < /dev/null is belt-and-braces: if a new prompt is ever added without a corresponding
# suppression flag we want this script to fail fast rather than hang the CI job.
(cd "$WORK_DIR" && run_aspire init --language csharp --suppress-agent-init < /dev/null)

# Verify init produced what we expect.
for required in apphost.cs aspire.config.json; do
    if [ ! -f "$WORK_DIR/$required" ]; then
        echo "❌ aspire init did not create $required"
        ls -la "$WORK_DIR"
        exit 1
    fi
done
echo "  ✓ apphost.cs and aspire.config.json present"

# Replace whatever NuGet.config init wrote with one that points only at the local stable
# feed. This makes the subsequent restore deterministic and offline-friendly:
#   * If a stable Aspire package transitively requires a preview-only package, restore must
#     fail here (not silently fall back to nuget.org).
#   * Avoids depending on network availability inside the CI job.
cat > "$WORK_DIR/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-stable" value="$LOCAL_FEED" />
  </packageSources>
</configuration>
EOF
echo "  ✓ NuGet.config overridden to local-stable feed only"

echo ""
echo "→ aspire restore (against local stable feed)"
(cd "$WORK_DIR" && run_aspire restore < /dev/null)

echo ""
echo "✅ Stabilization smoke passed: aspire init + restore both succeeded against stable packages."
