#!/usr/bin/env bash
# Stabilization smoke test: C#, Go, and Java starters + aspire restore against the locally-built
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
#   * Template version-pinning — that the hosting package references emitted by the
#     templates resolve against the stable feed for Aspire.* packages.
#   * Restore wiring — that `aspire restore` succeeds end-to-end for the generated project tree.
#
# Coverage NOTE: `aspire new aspire-empty` resolves to a single-file `apphost.cs` with
# Aspire.Hosting.AppHost and Aspire.Hosting.Dotnet package directives. Go and Java starters
# additionally validate their hosting package versions, not every integration.
# Per-integration NU5104 (stable depends on preview) detection comes from the
# previous step in the stabilization_check job (the stabilized pack). The two are complementary.
#
# Aspire.* packages MUST come from the local stable feed, selected by ASPIRE_CLI_PACKAGES
# (so missing/preview-only Aspire packages fail restore here). Non-Aspire dependencies
# (Microsoft.Extensions.*, OpenTelemetry.*, etc.) use nuget.org.
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
#
# The driver is always a Release bundled CLI published outside the checkout, regardless of
# the pack configuration. Java is enabled only for its subprocess. Its preview-only hosting
# package is deliberately not substituted for the stable version emitted by the starter.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CONFIGURATION="${STABILIZATION_SMOKE_CONFIGURATION:-Debug}"
LOCAL_FEED="${STABILIZATION_SMOKE_FEED:-$REPO_ROOT/artifacts/packages/$CONFIGURATION/Shipping}"
ASPIRE_CLI_PROJECT="$REPO_ROOT/src/Aspire.Cli/Aspire.Cli.csproj"
# Use the repo-local SDK that restore.sh / build.sh install under .dotnet/. The wrapper script
# also sets DOTNET_SKIP_FIRST_TIME_EXPERIENCE and resolves the SDK version from global.json,
# which is what we need on CI runners without the repository's pinned SDK.
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

# Pin the driver and generated projects to the SDK that the stabilized pack produced.
APPHOST_SDK_NUPKG=$(ls "$LOCAL_FEED"/Aspire.AppHost.Sdk.*.nupkg 2>/dev/null \
    | grep -E '/Aspire\.AppHost\.Sdk\.[0-9]+\.[0-9]+\.[0-9]+\.nupkg$' \
    | head -1 || true)
if [ -z "$APPHOST_SDK_NUPKG" ]; then
    echo "❌ No stable Aspire.AppHost.Sdk.MAJOR.MINOR.PATCH.nupkg in $LOCAL_FEED — can't pin the template SDK version."
    exit 1
fi
# For example, Aspire.AppHost.Sdk.17.0.0.nupkg becomes 17.0.0.
APPHOST_SDK_VERSION=$(basename "$APPHOST_SDK_NUPKG" .nupkg)
APPHOST_SDK_VERSION="${APPHOST_SDK_VERSION#Aspire.AppHost.Sdk.}"
echo "  ✓ Pinned Aspire.AppHost.Sdk version: $APPHOST_SDK_VERSION (from $(basename "$APPHOST_SDK_NUPKG"))"

# dotnet --info prints a line such as " RID: linux-x64". Use the SDK's host RID
# rather than assuming x64 when the smoke is run locally on another architecture.
RUNTIME_IDENTIFIER=$(DOTNET_CLI_UI_LANGUAGE=en-US "$DOTNET" --info | awk '/^[[:space:]]*RID:/ { print $2; exit }')
if [ -z "$RUNTIME_IDENTIFIER" ]; then
    echo "❌ Could not determine the .NET runtime identifier."
    exit 1
fi

# Debug checkout drivers select the development server and replace package references
# with source projects. A Release driver with its real bundled server must instead
# resolve the guest template's pins from LOCAL_FEED.
echo ""
echo "→ Building a stabilized Release CLI bundle for use as the smoke driver"
(
    cd "$REPO_ROOT"
    # Bundle.proj invokes child dotnet processes, so the stabilization setting must
    # also be inherited by those processes, not only passed to the outer MSBuild.
    export StabilizePackageVersion=true
    "$DOTNET" build "$REPO_ROOT/eng/Bundle.proj" \
        -c Release \
        -p:StabilizePackageVersion=true \
        "-p:BundleVersion=$APPHOST_SDK_VERSION" \
        "-p:TargetRid=$RUNTIME_IDENTIFIER" \
        -p:SkipNativeBuild=true \
        --nologo -v quiet
    "$DOTNET" publish "$ASPIRE_CLI_PROJECT" \
        -c Release -r "$RUNTIME_IDENTIFIER" --self-contained \
        -p:PublishAot=false -p:PublishSingleFile=true \
        -p:StabilizePackageVersion=true \
        "-p:VersionPrefix=$APPHOST_SDK_VERSION" \
        "-p:BundlePayloadPath=$REPO_ROOT/artifacts/bundle/aspire-$APPHOST_SDK_VERSION-$RUNTIME_IDENTIFIER.tar.gz" \
        --output "$WORK_DIR/cli" \
        --nologo -v quiet
)

# Keep consumer restores cold, independently of the packages needed to build the driver.
# The package override also reaches the server's temporary integration project, which
# cannot discover the consumer workspace's NuGet.config.
export NUGET_PACKAGES="$WORK_DIR/.nuget/packages"
export ASPIRE_CLI_PACKAGES="$LOCAL_FEED"
# The standalone driver no longer inherits dotnet.sh's SDK environment.
export DOTNET_ROOT="$REPO_ROOT/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

# Helper: invoke the bundled CLI outside the checkout. Intentionally does NOT change directory —
# the caller's CWD is what propagates to the spawned aspire process. That matters because:
#   * aspire's --output (and other path-based) flags resolve relative paths against
#     Environment.CurrentDirectory.
#   * NuGet config-walk starts from CWD, so we want the temp-dir NuGet.config
#     to be the one found, not the repo's own
#     NuGet.config which points at dnceng-internal feeds.
# --non-interactive (a recursive
# root-level option, see Aspire.Cli/Commands/RootCommand.cs: NonInteractiveOption with
# Recursive = true) is the canonical "I'm in CI, never prompt" flag. The explicit suppression
# flags below (--language, --suppress-agent-init, --localhost-tld) supply the actual *values*
# we want for the known prompts; --non-interactive guards against any future prompt being
# added without a matching suppression flag, and against ambient prompts like the certificate
# trust check.
run_aspire() (
    # Release normally avoids repository discovery, but an inherited explicit
    # developer override would still select source references.
    unset ASPIRE_REPO_ROOT
    "$WORK_DIR/cli/aspire" --non-interactive "$@"
)

# Isolate ambient feeds without pre-owning the CLI's channel mapping. ASPIRE_CLI_PACKAGES
# supplies the authoritative Aspire* policy; Go and C# scaffold their project-local mappings.
# An inherited user-owned mapping for that same feed cannot safely be rewritten by scaffolding.
cat > "$WORK_DIR/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
  <disabledPackageSources>
    <clear />
  </disabledPackageSources>
</configuration>
EOF
echo "  ✓ Isolated NuGet.config written (Aspire* uses the CLI's local stable-feed override)"

PROJECT_NAME="MyAspireApp"
# Absolute path so `--output` is unambiguous regardless of CWD.
PROJECT_DIR="$WORK_DIR/$PROJECT_NAME"

# Exercise both guest starters even if one fails, so a preview-only hosting package does not
# hide the other language's result. Keep the template's actual pins: rewriting them to whatever
# exists in the feed would mask the stabilization regression this smoke is intended to catch.
GUEST_FAILURES=()
for language in go java; do
    guest_dir="$WORK_DIR/${language}-starter"
    echo ""
    echo "→ Scaffolding and restoring $language starter against the stable feed"
    if ! (
        if [ "$language" = java ]; then
            # Java is opt-in. Enable it only in this smoke subprocess so template
            # filtering cannot hide the stable-package restore being tested.
            export features__experimentalPolyglotJava=true
        fi
        cd "$WORK_DIR" || exit $?
        run_aspire new "aspire-${language}-starter" \
            --name "Stable${language}Starter" \
            --output "$guest_dir" \
            --source "$LOCAL_FEED" \
            --version "$APPHOST_SDK_VERSION" \
            --suppress-agent-init \
            --localhost-tld false < /dev/null || exit $?
        cd "$guest_dir" || exit $?
        run_aspire restore < /dev/null || exit $?
        # A development server can succeed with source references instead. Require cold
        # NuGet extraction of the pinned hosting packages and retain their provenance in CI.
        packages=("aspire.hosting.$language")
        if [ "$language" = go ]; then
            packages+=(aspire.hosting.redis)
        fi
        for package in "${packages[@]}"; do
            metadata="$NUGET_PACKAGES/$package/$APPHOST_SDK_VERSION/.nupkg.metadata"
            if [ ! -f "$metadata" ]; then
                echo "❌ Restore did not extract $package/$APPHOST_SDK_VERSION from NuGet."
                exit 1
            fi
            echo "  ✓ Restored $package/$APPHOST_SDK_VERSION; NuGet provenance:"
            cat "$metadata" || exit $?
            echo ""
        done
    ); then
        GUEST_FAILURES+=("$language")
        echo "❌ $language starter could not restore its emitted package pins from the stable feed."
    fi
done

echo ""
echo "→ aspire new aspire-empty --name $PROJECT_NAME --output $PROJECT_DIR --version $APPHOST_SDK_VERSION --language csharp --suppress-agent-init --localhost-tld false"
# Flags chosen to make the command fully non-interactive (in addition to --non-interactive
# baked into run_aspire):
#   --language csharp        suppresses the language prompt
#   --suppress-agent-init    suppresses the "configure AI agent environments" prompt
#   --localhost-tld false    suppresses the localhost-tld prompt
# --source pins the templates-package fetch to our local-stable feed, so we test the templates
# we just packed (not whatever's on nuget.org).
# --version pins the hosting package versions emitted into the generated AppHost; without
# it, the CLI's own template-version resolution decides. We want this smoke to test the exact
# product version we just packed.
# CWD is $WORK_DIR so NuGet discovers the isolated consumer configuration.
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
APPHOST_REF_FILE=""
if [ -f "$PROJECT_DIR/apphost.cs" ] && [ -f "$PROJECT_DIR/aspire.config.json" ]; then
    echo "  ✓ Single-file AppHost detected (apphost.cs + aspire.config.json)"
    # File-based AppHosts use "#:package Aspire.Hosting.AppHost@17.0.0", not #:sdk.
    APPHOST_REF_FILE="$PROJECT_DIR/apphost.cs"
    EXPECTED_APPHOST_REF="#:package Aspire.Hosting.AppHost@$APPHOST_SDK_VERSION"
    REFERENCE_MATCH=(-qxF)
elif [ -f "$PROJECT_DIR/$PROJECT_NAME.AppHost/$PROJECT_NAME.AppHost.csproj" ]; then
    echo "  ✓ Project-based AppHost detected ($PROJECT_NAME.AppHost.csproj)"
    APPHOST_REF_FILE="$PROJECT_DIR/$PROJECT_NAME.AppHost/$PROJECT_NAME.AppHost.csproj"
    EXPECTED_APPHOST_REF="Include=\"Aspire.Hosting.AppHost\" Version=\"$APPHOST_SDK_VERSION\""
    REFERENCE_MATCH=(-qF)
else
    echo "❌ aspire new produced an unrecognized layout in $PROJECT_DIR"
    echo "   Contents (depth 3):"
    find "$PROJECT_DIR" -maxdepth 3 -type f 2>/dev/null || true
    exit 1
fi

# Assert the AppHost references the exact hosting version we pinned. Catches the failure mode
# where `--version` is ignored / overridden by CLI template-version resolution. Without
# this assertion, a mismatch would later surface as a confusing "package not found in
# local stable feed" restore error instead of a clear "wrong hosting version emitted."
if ! grep "${REFERENCE_MATCH[@]}" "$EXPECTED_APPHOST_REF" "$APPHOST_REF_FILE"; then
    echo "❌ Expected AppHost package reference not found in $APPHOST_REF_FILE:"
    echo "   Expected: $EXPECTED_APPHOST_REF"
    echo "   Actual package references in file:"
    grep -E '(#:package|PackageReference)' "$APPHOST_REF_FILE" || true
    exit 1
fi
echo "  ✓ AppHost references the pinned hosting package ($EXPECTED_APPHOST_REF)"

echo ""
echo "→ aspire restore (against local stable feed for Aspire packages, nuget.org for everything else)"
# Run from $PROJECT_DIR so aspire restore discovers aspire.config.json there AND so the
# NuGet discovers both the CLI's project-local source policy and our isolated parent defaults.
(cd "$PROJECT_DIR" && run_aspire restore < /dev/null)

if [ ${#GUEST_FAILURES[@]} -gt 0 ]; then
    echo "❌ Guest starter stabilization failures: ${GUEST_FAILURES[*]}"
    exit 1
fi

echo ""
echo "✅ Stabilization smoke passed: C#, Go, and Java scaffolding and restore succeeded against stable packages pinned to $APPHOST_SDK_VERSION."
