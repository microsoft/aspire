#!/bin/bash
set -e

# Ensure we run from the extension directory so corepack/yarn pick up
# extension/.npmrc and extension/package.json (which holds the packageManager pin).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Pinned Corepack shim version. Node.js >= 16.10 bundles a Corepack, but the
# bundled version drifts with each Node release and Corepack is on track to be
# unbundled from Node entirely (see https://github.com/nodejs/node/issues/54647).
# Installing a pinned Corepack from npm makes the build reproducible regardless
# of which Node version a developer or CI runner happens to have.
#
# The version is stored in scripts/corepack-version.txt so that this script, the
# PowerShell build script, the GitHub Actions workflow, and the AzDO pipelines
# all read from a single source of truth.
COREPACK_VERSION="$(tr -d '[:space:]' < "$SCRIPT_DIR/scripts/corepack-version.txt")"
if [ -z "$COREPACK_VERSION" ]; then
    echo "Error: scripts/corepack-version.txt is empty or unreadable."
    exit 1
fi

# Yarn version is pinned in extension/package.json via the "packageManager"
# field, which scripts/prepareCorepackYarn.mjs uses to seed Corepack's cache.

# Point npm at the dnceng internal npm mirror when installing the pinned Corepack
# shim and seeding Corepack's Yarn cache. npm global installs do not use the
# project .npmrc, so pass the registry explicitly. Override locally with
# `NPM_REGISTRY=<url> ./build.sh`.
# Export NPM_REGISTRY so the child `node ./scripts/prepareCorepackYarn.mjs`
# process inherits it; without `export`, the script silently falls back to its
# own DefaultNpmRegistry constant and any user override of NPM_REGISTRY would
# be ignored when seeding Corepack's Yarn cache.
: "${NPM_REGISTRY:=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/}"
export NPM_REGISTRY
: "${COREPACK_ENABLE_DOWNLOAD_PROMPT:=0}"
export COREPACK_ENABLE_DOWNLOAD_PROMPT

# Pin Corepack's cache directory to a build-scoped location. Without this, every
# build shares the user's default cache (~/.cache/node/corepack on Linux,
# ~/Library/Caches/node/corepack on macOS, %LOCALAPPDATA%\node\corepack on
# Windows). prepareCorepackYarn.mjs rewrites that cache in place
# (rmSync(installDirectory) followed by renameSync(staging, installDirectory)),
# so concurrent builds racing on the same Corepack home can corrupt each other's
# cache. The CI pipelines already scope this per-job (e.g. AzDO uses
# Agent.TempDirectory); do the same here so multi-worktree setups stay
# isolated. Concurrent builds in the *same* worktree still race on this shared
# directory — prepareCorepackYarn.mjs handles the EEXIST/ENOTEMPTY rename
# collision but is not a substitute for a lock. The directory is gitignored.
: "${COREPACK_HOME:=$SCRIPT_DIR/.corepack-cache}"
export COREPACK_HOME

echo "Checking prerequisites..."

# Check for Node.js
if ! command -v node &> /dev/null; then
    echo "Error: Node.js is not installed. Please install Node.js first."
    exit 1
fi

# npm is required to install our pinned Corepack. It ships with every official
# Node.js distribution, so this should only fail on broken installs.
if ! command -v npm &> /dev/null; then
    echo "Error: npm is not available. Reinstall Node.js so npm is on PATH."
    exit 1
fi

# Check for VS Code or VS Code Insiders
if ! command -v code &> /dev/null && ! command -v code-insiders &> /dev/null; then
    echo "Error: VS Code or VS Code Insiders is not installed or not in PATH."
    echo "Please install VS Code or VS Code Insiders and ensure it's added to your PATH."
    exit 1
fi

# Check for dotnet
if ! command -v dotnet &> /dev/null; then
    echo "Error: .NET SDK is not installed. Please install .NET SDK first."
    echo "Use the restore script at the repo root."
    exit 1
fi

echo "All prerequisites satisfied."

cd "$SCRIPT_DIR"

echo ""
echo "Installing pinned Corepack ${COREPACK_VERSION}..."
# Reinstall every time so we overwrite any older Corepack shim that Node.js
# may have placed on PATH ahead of npm's global prefix. npm global installs do
# not use the project .npmrc, so pass the registry explicitly.
#
# --force is required because Corepack's npm package declares `yarn`, `yarnpkg`,
# `pnpm`, `pnpx`, and `corepack` as bin entries. Without --force, npm refuses to
# overwrite bins owned by a pre-existing global yarn or pnpm (a very common
# setup, and the state this repo itself was in before this build script existed)
# and aborts with EEXIST. The CI pipelines already pass --force for the same
# reason.
npm install --global --force --registry "$NPM_REGISTRY" "corepack@${COREPACK_VERSION}"

# Verify the version actually on PATH matches our pin. If a system-bundled
# Corepack shim shadows the npm-global install (common on Windows; possible on
# macOS/Linux when /usr/local/bin precedes the npm prefix), `npm install -g`
# can "succeed" while subsequent `corepack` calls still resolve to the bundled
# version. Fail loudly here so we don't silently run with the wrong tool.
installed_corepack=$(corepack --version 2>/dev/null || echo "")
if [ "$installed_corepack" != "$COREPACK_VERSION" ]; then
    echo "Error: corepack version mismatch: expected $COREPACK_VERSION, got '$installed_corepack'."
    echo "The bundled Corepack on PATH may be taking precedence over the npm-global install."
    echo "Ensure your npm global bin directory comes before any other Node.js install on PATH,"
    echo "or use a Node version manager (nvm, asdf, fnm) that places the npm prefix appropriately."
    exit 1
fi

echo ""
echo "Enabling Corepack package manager shims..."
corepack enable

echo ""
echo "Preparing Yarn from packageManager pin in package.json..."
node ./scripts/prepareCorepackYarn.mjs

echo ""
echo "Running yarn install..."
corepack yarn install --frozen-lockfile --non-interactive

echo ""
echo "Running yarn compile..."
corepack yarn compile

echo ""
echo "Building Aspire CLI..."
dotnet build ../src/Aspire.Cli/Aspire.Cli.csproj

echo ""
echo "Build completed successfully!"
