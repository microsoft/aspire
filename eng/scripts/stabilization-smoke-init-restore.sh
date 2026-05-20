#!/usr/bin/env bash
# Stabilization smoke test: aspire new aspire-empty + aspire restore against the locally-built
# stable Aspire feed.
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
# This script (the smoke) handles the end-to-end consumer flow specifically. It exercises:
#   * Template integrity — that `aspire new aspire-empty` produces a buildable project shape
#     using the stably-built Aspire.ProjectTemplates package.
#   * Template version-pinning — that the SDK reference (Aspire.AppHost.Sdk) and PackageReferences
#     emitted by the template resolve against the stable feed for Aspire.* packages.
#   * Restore wiring — that `aspire restore` succeeds end-to-end for the generated project tree.
#
# Aspire.* packages MUST come from the local stable feed (so missing/preview-only Aspire packages
# fail restore here, exactly as they would on a real stabilization branch). Non-Aspire deps
# pulled in by the template (Microsoft.Extensions.*, OpenTelemetry.*, etc.) are routed to the
# normal public dotnet feeds via packageSourceMapping — they're not the surface we're validating.
#
# The pack-time NU5104 check (the primary detector for the Foo.Lib scenario above) is the
# previous step in the stabilization_check job in ci.yml — this script complements it by
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
#
# Override defaults via env vars when needed:
#   STABILIZATION_SMOKE_CONFIGURATION  Build configuration the pack step used (default: Debug)
#   STABILIZATION_SMOKE_FEED           Absolute path to the local NuGet feed
#                                      (default: $REPO_ROOT/artifacts/packages/$CONFIG/Shipping)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CONFIGURATION="${STABILIZATION_SMOKE_CONFIGURATION:-Debug}"
LOCAL_FEED="${STABILIZATION_SMOKE_FEED:-$REPO_ROOT/artifacts/packages/$CONFIGURATION/Shipping}"
ASPIRE_CLI_PROJECT="$REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj"
# Use the repo-local SDK that restore.sh / build.sh install under .dotnet/. The wrapper script
# also sets DOTNET_SKIP_FIRST_TIME_EXPERIENCE and resolves the SDK version from global.json,
# which is what we need on CI runners that don't have a system-wide .NET 10 SDK.
DOTNET="$REPO_ROOT/dotnet.sh"

echo "=== Stabilization smoke: aspire new aspire-empty + restore ==="
echo "Repo root:     $REPO_ROOT"
echo "Configuration: $CONFIGURATION"
echo "Local feed:    $LOCAL_FEED"

if [ ! -d "$LOCAL_FEED" ]; then
    echo "❌ Local feed not found at: $LOCAL_FEED"
    echo "   Run a stabilized pack first (see prerequisites at the top of this script)."
    exit 1
fi

# Confirm the feed actually contains stabilized Aspire packages (i.e. Aspire.Hosting.13.X.0.nupkg
# without a -dev/-preview suffix). Otherwise this smoke would be checking the wrong shape.
if ! ls "$LOCAL_FEED"/Aspire.Hosting.[0-9]*.[0-9]*.[0-9]*.nupkg > /dev/null 2>&1; then
    echo "❌ No stable Aspire.Hosting.*.nupkg in $LOCAL_FEED. Did pack run with StabilizePackageVersion=true?"
    echo "   Found nupkgs:"
    ls "$LOCAL_FEED"/Aspire.Hosting.*.nupkg 2>/dev/null | head -5 || true
    exit 1
fi

# Same expectation for the templates package — `aspire new` needs it installable from the feed.
if ! ls "$LOCAL_FEED"/Aspire.ProjectTemplates.[0-9]*.[0-9]*.[0-9]*.nupkg > /dev/null 2>&1; then
    echo "❌ No stable Aspire.ProjectTemplates.*.nupkg in $LOCAL_FEED."
    echo "   Did pack run with StabilizePackageVersion=true (and without -p:SkipProjectTemplates)?"
    exit 1
fi

WORK_DIR="$(mktemp -d -t aspire-stab-smoke-XXXXXXXX)"
# Clean up on any exit; -f tolerates the dir being already gone if cleanup ran inside the script.
trap 'rm -rf "$WORK_DIR"' EXIT
echo "Work dir:      $WORK_DIR"

# Build the CLI project up-front (under stabilization) so subsequent `dotnet run` invocations
# from inside the temp dir don't trigger a build in the test working directory (which would
# pull in the temp dir's NuGet.config and try to restore the CLI's own dependencies against
# our local-only feed).
echo ""
echo "→ Building Aspire.Cli (stabilized) for use as the smoke driver"
(
    cd "$REPO_ROOT"
    "$DOTNET" build "$ASPIRE_CLI_PROJECT" \
        -c "$CONFIGURATION" \
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
            -c "$CONFIGURATION" \
            --no-build \
            --no-launch-profile \
            -p:StabilizePackageVersion=true \
            -- "$@"
    )
}

# Stage a NuGet.config in the work dir BEFORE running `aspire new`. The CLI itself fetches
# the templates package via NuGet, and the subsequent restore needs both Aspire.* (from local
# stable) and non-Aspire transitives (from the public dotnet feeds). packageSourceMapping pins
# Aspire.* to our feed so a missing/preview Aspire dep fails fast rather than silently falling
# back to nuget.org. Mirrors the source-mapping pattern in tests/Shared/nuget-with-package-
# source-mapping.config used by Helix.
cat > "$WORK_DIR/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <packageSources>
    <clear />
    <add key="local-stable" value="$LOCAL_FEED" />
    <add key="dotnet-public" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json" />
    <add key="dotnet-eng" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json" />
    <add key="dotnet9" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
    <add key="dotnet10" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-stable">
      <package pattern="Aspire*" />
    </packageSource>
    <packageSource key="dotnet-public">
      <package pattern="*" />
    </packageSource>
    <packageSource key="dotnet-eng">
      <package pattern="*" />
    </packageSource>
    <packageSource key="dotnet9">
      <package pattern="*" />
    </packageSource>
    <packageSource key="dotnet10">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <disabledPackageSources>
    <clear />
  </disabledPackageSources>
</configuration>
EOF
echo "  ✓ Source-mapped NuGet.config written (Aspire* -> local-stable; everything else -> public dotnet feeds)"

PROJECT_NAME="MyAspireApp"
echo ""
echo "→ aspire new aspire-empty --name $PROJECT_NAME --output $PROJECT_NAME --language csharp --suppress-agent-init --localhost-tld false"
# Flags chosen to make the command fully non-interactive:
#   --language csharp        suppresses the language prompt
#   --suppress-agent-init    suppresses the "configure AI agent environments" prompt
#   --localhost-tld false    suppresses the localhost-tld prompt
# --source pins the templates-package fetch to our local-stable feed, so we test the templates
# we just packed (not whatever's on nuget.org).
# < /dev/null is belt-and-braces: if a new prompt is ever added without a corresponding
# suppression flag we want this script to fail fast rather than hang the CI job.
(
    cd "$WORK_DIR"
    run_aspire new aspire-empty \
        --name "$PROJECT_NAME" \
        --output "$PROJECT_NAME" \
        --source "$LOCAL_FEED" \
        --language csharp \
        --suppress-agent-init \
        --localhost-tld false \
        < /dev/null
)

# Verify the template produced something. `aspire-empty` may resolve to either the
# single-file AppHost shape (apphost.cs + aspire.config.json) or a project-based shape
# (<Name>.AppHost.csproj + <Name>.ServiceDefaults.csproj), depending on the CLI's template
# resolution. Accept either — both are valid AppHost layouts that aspire restore can drive.
PROJECT_DIR="$WORK_DIR/$PROJECT_NAME"
if [ ! -d "$PROJECT_DIR" ]; then
    echo "❌ aspire new did not create the project directory $PROJECT_DIR"
    exit 1
fi
if [ -f "$PROJECT_DIR/apphost.cs" ] && [ -f "$PROJECT_DIR/aspire.config.json" ]; then
    echo "  ✓ Single-file AppHost detected (apphost.cs + aspire.config.json)"
elif [ -f "$PROJECT_DIR/$PROJECT_NAME.AppHost/$PROJECT_NAME.AppHost.csproj" ]; then
    echo "  ✓ Project-based AppHost detected ($PROJECT_NAME.AppHost.csproj)"
else
    echo "❌ aspire new produced an unrecognized layout in $PROJECT_DIR"
    echo "   Contents (depth 3):"
    find "$PROJECT_DIR" -maxdepth 3 -type f 2>/dev/null || true
    exit 1
fi

echo ""
echo "→ aspire restore (against local stable feed + public dotnet feeds for transitives)"
(cd "$PROJECT_DIR" && run_aspire restore < /dev/null)

echo ""
echo "✅ Stabilization smoke passed: aspire new aspire-empty + restore both succeeded against stable packages."
