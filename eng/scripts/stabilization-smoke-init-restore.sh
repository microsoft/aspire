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
#   * Template version-pinning — that the SDK reference (Aspire.AppHost.Sdk) and any
#     PackageReferences emitted by the template resolve against the stable feed for
#     Aspire.* packages.
#   * Restore wiring — that `aspire restore` succeeds end-to-end for the generated project tree.
#
# Coverage NOTE: today `aspire new aspire-empty` resolves to a single-file `apphost.cs` shape
# whose only NuGet dependency is `Aspire.AppHost.Sdk` (which transitively pulls
# Aspire.Hosting.AppHost / Aspire.Hosting). So this smoke validates that the SDK reference path
# is wired correctly through to the local stable feed — NOT that every Aspire integration
# package restores. Per-integration NU5104 (stable depends on preview) detection comes from the
# previous step in the stabilization_check job (the stabilized pack). The two are complementary.
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

# Confirm the feed actually contains a stabilized Aspire.Hosting nupkg — i.e. the version is
# exactly MAJOR.MINOR.PATCH with no prerelease suffix at all. Without this guard a local-repro
# run that forgot to pass StabilizePackageVersion=true to the pack would sail past, and the
# smoke would silently be validating a prerelease build instead of a stable one. (CI is safe
# regardless because the prior pack step always passes the flag, but local-repro paths through
# this script need the check.)
# The grep enforces "no `-` between the version and `.nupkg`" — that catches every SemVer
# prerelease shape (-dev.<n>, -preview.X.Y, -rc1, -rc.1, -alpha, etc.) without having to
# enumerate suffix labels.
if ! ls "$LOCAL_FEED"/Aspire.Hosting.*.nupkg 2>/dev/null \
        | grep -E '/Aspire\.Hosting\.[0-9]+\.[0-9]+\.[0-9]+\.nupkg$' \
        | grep -q .; then
    echo "❌ No stable Aspire.Hosting.MAJOR.MINOR.PATCH.nupkg in $LOCAL_FEED."
    echo "   Did pack run with StabilizePackageVersion=true?"
    echo "   Aspire.Hosting nupkgs found:"
    ls "$LOCAL_FEED"/Aspire.Hosting.*.nupkg 2>/dev/null | head -5 || true
    exit 1
fi

# Same expectation for the templates package — `aspire new` needs a stable templates nupkg
# installable from the feed. Use the same prerelease-exclusion logic.
if ! ls "$LOCAL_FEED"/Aspire.ProjectTemplates.*.nupkg 2>/dev/null \
        | grep -E '/Aspire\.ProjectTemplates\.[0-9]+\.[0-9]+\.[0-9]+\.nupkg$' \
        | grep -q .; then
    echo "❌ No stable Aspire.ProjectTemplates.MAJOR.MINOR.PATCH.nupkg in $LOCAL_FEED."
    echo "   Did pack run with StabilizePackageVersion=true (and without -p:SkipProjectTemplates)?"
    echo "   Aspire.ProjectTemplates nupkgs found:"
    ls "$LOCAL_FEED"/Aspire.ProjectTemplates.*.nupkg 2>/dev/null | head -5 || true
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

# Helper: invoke the CLI via the repo-local SDK. Intentionally does NOT change directory —
# the caller's CWD is what propagates to the spawned aspire process. That matters because:
#   * aspire's --output (and other path-based) flags resolve relative paths against
#     Environment.CurrentDirectory.
#   * NuGet config-walk starts from CWD, so we want the temp-dir NuGet.config (with the
#     Aspire* -> local-stable source mapping) to be the one found, not the repo's own
#     NuGet.config which points at dnceng-internal feeds.
# (`./dotnet.sh` resolves the local SDK via $scriptroot — independent of CWD — so we don't
# need to be in $REPO_ROOT for the SDK lookup.)
# --no-build because we just built above; --no-launch-profile keeps src/Aspire.Cli/Properties/
# launchSettings.json from interfering with our explicit args. --non-interactive (a recursive
# root-level option, see Aspire.Cli/Commands/RootCommand.cs: NonInteractiveOption with
# Recursive = true) is the canonical "I'm in CI, never prompt" flag. The explicit suppression
# flags below (--language, --suppress-agent-init, --localhost-tld) supply the actual *values*
# we want for the known prompts; --non-interactive guards against any future prompt being
# added without a matching suppression flag, and against ambient prompts like the certificate
# trust check.
run_aspire() {
    "$DOTNET" run --project "$ASPIRE_CLI_PROJECT" \
        -c "$CONFIGURATION" \
        --no-build \
        --no-launch-profile \
        -p:StabilizePackageVersion=true \
        -- --non-interactive "$@"
}

# Stage a NuGet.config in the work dir BEFORE running `aspire new`. The CLI itself fetches
# the templates package via NuGet, and the subsequent restore needs both Aspire.* (from local
# stable) and any non-Aspire transitives. packageSourceMapping pins Aspire.* to our feed so
# a missing/preview Aspire dep fails fast rather than silently falling back to nuget.org for
# an older stable release. Everything else flows from nuget.org — matches what an end user
# running `aspire new` would have configured by default.
cat > "$WORK_DIR/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <packageSources>
    <clear />
    <add key="local-stable" value="$LOCAL_FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-stable">
      <package pattern="Aspire*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <disabledPackageSources>
    <clear />
  </disabledPackageSources>
</configuration>
EOF
echo "  ✓ Source-mapped NuGet.config written (Aspire* -> local-stable; everything else -> nuget.org)"

PROJECT_NAME="MyAspireApp"
# Absolute path so `--output` is unambiguous regardless of CWD.
PROJECT_DIR="$WORK_DIR/$PROJECT_NAME"

# Derive the exact Aspire.AppHost.Sdk version from the local feed. We pass this via
# `aspire new --version` AND assert later that the generated apphost.cs references this
# exact version (rather than whatever the CLI's own template-version resolution would
# pick). This makes the smoke test the precise contract — "the AppHost SDK this stable
# build packed is what the generated app restores from $LOCAL_FEED" — instead of
# leaving room for the CLI to silently pick a different version that then fails restore
# with a confusing "not found in local feed" message.
APPHOST_SDK_NUPKG=$(ls "$LOCAL_FEED"/Aspire.AppHost.Sdk.[0-9]*.[0-9]*.[0-9]*.nupkg 2>/dev/null | head -1)
if [ -z "$APPHOST_SDK_NUPKG" ]; then
    echo "❌ No Aspire.AppHost.Sdk.*.nupkg in $LOCAL_FEED — can't pin the template SDK version."
    exit 1
fi
# Strip the leading 'Aspire.AppHost.Sdk.' prefix and trailing '.nupkg' to get the version.
APPHOST_SDK_VERSION=$(basename "$APPHOST_SDK_NUPKG" .nupkg)
APPHOST_SDK_VERSION="${APPHOST_SDK_VERSION#Aspire.AppHost.Sdk.}"
echo "  ✓ Pinned Aspire.AppHost.Sdk version: $APPHOST_SDK_VERSION (from $(basename "$APPHOST_SDK_NUPKG"))"

echo ""
echo "→ aspire new aspire-empty --name $PROJECT_NAME --output $PROJECT_DIR --version $APPHOST_SDK_VERSION --language csharp --suppress-agent-init --localhost-tld false"
# Flags chosen to make the command fully non-interactive (in addition to --non-interactive
# baked into run_aspire):
#   --language csharp        suppresses the language prompt
#   --suppress-agent-init    suppresses the "configure AI agent environments" prompt
#   --localhost-tld false    suppresses the localhost-tld prompt
# --source pins the templates-package fetch to our local-stable feed, so we test the templates
# we just packed (not whatever's on nuget.org).
# --version pins the Aspire.AppHost.Sdk version emitted into the generated AppHost; without
# it, the CLI's own template-version resolution decides. We want this smoke to test the exact
# version we just packed.
# CWD is $WORK_DIR so the temp NuGet.config (with Aspire* source-mapped to local-stable) is
# the one NuGet finds via its config walk.
(
    cd "$WORK_DIR"
    run_aspire new aspire-empty \
        --name "$PROJECT_NAME" \
        --output "$PROJECT_DIR" \
        --source "$LOCAL_FEED" \
        --version "$APPHOST_SDK_VERSION" \
        --language csharp \
        --suppress-agent-init \
        --localhost-tld false \
        < /dev/null
)

# Verify the template produced something. `aspire-empty` may resolve to either the
# single-file AppHost shape (apphost.cs + aspire.config.json) or a project-based shape
# (<Name>.AppHost.csproj + <Name>.ServiceDefaults.csproj), depending on the CLI's template
# resolution. Accept either — both are valid AppHost layouts that aspire restore can drive.
if [ ! -d "$PROJECT_DIR" ]; then
    echo "❌ aspire new did not create the project directory $PROJECT_DIR"
    exit 1
fi
APPHOST_SDK_REF_FILE=""
if [ -f "$PROJECT_DIR/apphost.cs" ] && [ -f "$PROJECT_DIR/aspire.config.json" ]; then
    echo "  ✓ Single-file AppHost detected (apphost.cs + aspire.config.json)"
    # Single-file AppHosts reference the SDK as `#:sdk Aspire.AppHost.Sdk@<version>`.
    APPHOST_SDK_REF_FILE="$PROJECT_DIR/apphost.cs"
    EXPECTED_SDK_REF="#:sdk Aspire.AppHost.Sdk@$APPHOST_SDK_VERSION"
elif [ -f "$PROJECT_DIR/$PROJECT_NAME.AppHost/$PROJECT_NAME.AppHost.csproj" ]; then
    echo "  ✓ Project-based AppHost detected ($PROJECT_NAME.AppHost.csproj)"
    # Project-based AppHosts reference the SDK as `Sdk="Aspire.AppHost.Sdk/<version>"`.
    APPHOST_SDK_REF_FILE="$PROJECT_DIR/$PROJECT_NAME.AppHost/$PROJECT_NAME.AppHost.csproj"
    EXPECTED_SDK_REF="Aspire.AppHost.Sdk/$APPHOST_SDK_VERSION"
else
    echo "❌ aspire new produced an unrecognized layout in $PROJECT_DIR"
    echo "   Contents (depth 3):"
    find "$PROJECT_DIR" -maxdepth 3 -type f 2>/dev/null || true
    exit 1
fi

# Assert the AppHost references the exact SDK version we pinned. Catches the failure mode
# where `--version` is ignored / overridden by CLI template-version resolution. Without
# this assertion, a mismatch would later surface as a confusing "package not found in
# local-stable feed" restore error instead of a clear "wrong SDK version emitted."
if ! grep -qF "$EXPECTED_SDK_REF" "$APPHOST_SDK_REF_FILE"; then
    echo "❌ Expected AppHost SDK reference not found in $APPHOST_SDK_REF_FILE:"
    echo "   Expected: $EXPECTED_SDK_REF"
    echo "   Actual SDK references in file:"
    grep -E '(#:sdk|Sdk=)' "$APPHOST_SDK_REF_FILE" || true
    exit 1
fi
echo "  ✓ AppHost references the pinned SDK ($EXPECTED_SDK_REF)"

echo ""
echo "→ aspire restore (against local stable feed for Aspire packages, nuget.org for everything else)"
# Run from $PROJECT_DIR so aspire restore discovers aspire.config.json there AND so the
# nearest NuGet.config (the one we wrote at $WORK_DIR) is the one used.
(cd "$PROJECT_DIR" && run_aspire restore < /dev/null)

echo ""
echo "✅ Stabilization smoke passed: aspire new aspire-empty + restore both succeeded against stable packages, with Aspire.AppHost.Sdk pinned to $APPHOST_SDK_VERSION."
