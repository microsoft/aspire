#!/usr/bin/env bash
# Stabilization smoke test: aspire init + aspire restore against the locally-built stable feed.
#
# Purpose
# -------
# Most regular PR CI runs unstable (DotNetFinalVersionKind=prerelease), which means a class of
# bugs that only show up under "everything is stable" is invisible until the actual
# stabilization PR. The canonical case:
#
#   Suppose Aspire.Hosting.Foo (which is NOT marked <SuppressFinalPackageVersion>true</...>,
#   so it ships as stable when we stabilize) adds a PackageReference to Foo.Lib, but Foo.Lib
#   is still publishing preview versions only. Under a prerelease PR build, both Aspire.Hosting.Foo
#   and Foo.Lib are prerelease and restore happily. The moment we flip StabilizePackageVersion
#   on, Aspire.Hosting.Foo becomes stable while its transitive dep is still prerelease — and
#   the build breaks (NU5104 at pack time, or restore failures downstream).
#
# Without a check like this one we only catch that class of regression at stabilization time,
# usually within days of a release. With this check it surfaces on the PR that introduces the
# preview dependency, so the integration author can decide up front whether to:
#   - flip <SuppressFinalPackageVersion>true</...> on Aspire.Hosting.Foo (ship it as preview), or
#   - request Foo.Lib to stabilize before our next stable release.
#
# This script (the smoke) handles the end-to-end consumer flow specifically:
#   * That `aspire init` emits an SDK / package reference shape that actually resolves against
#     the stable feed (catches template version-pinning regressions).
#   * That `aspire restore` succeeds end-to-end against a feed that contains only the stable
#     packages we'd ship — i.e. no silent fallback to nuget.org papering over a missing dep.
#
# The pack-time NU5104 check (which is the primary detector for the Foo.Lib scenario above) is
# the previous step in the stabilization_check job in ci.yml — this script complements it by
# exercising the user-visible CLI workflow against the same stably-built feed.
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
