#!/usr/bin/env pwsh

$ErrorActionPreference = "Stop"

# Ensure we run from the extension directory so corepack/yarn pick up
# extension/.npmrc and extension/package.json (which holds the packageManager pin).
Set-Location $PSScriptRoot

# Pinned Corepack shim version. Node.js >= 16.10 bundles a Corepack, but the
# bundled version drifts with each Node release and Corepack is on track to be
# unbundled from Node entirely (see https://github.com/nodejs/node/issues/54647).
# Installing a pinned Corepack from npm makes the build reproducible regardless
# of which Node version a developer or CI runner happens to have.
#
# The version is stored in scripts/corepack-version.txt so that this script, the
# Bash build script, the GitHub Actions workflow, and the AzDO pipelines all
# read from a single source of truth.
$CorepackVersion = (Get-Content -Raw -Path (Join-Path $PSScriptRoot 'scripts/corepack-version.txt')).Trim()
if ([string]::IsNullOrWhiteSpace($CorepackVersion)) {
    Write-Error "scripts/corepack-version.txt is empty or unreadable."
    exit 1
}

# Yarn version is pinned in extension/package.json via the "packageManager"
# field, which scripts/prepareCorepackYarn.mjs uses to seed Corepack's cache.

# Point npm at the dnceng internal npm mirror when installing the pinned Corepack
# shim and seeding Corepack's Yarn cache. npm global installs do not use the
# project .npmrc, so pass the registry explicitly. Override locally with
# `$env:NPM_REGISTRY = '<url>'; ./build.ps1`.
if (-not $env:NPM_REGISTRY) {
    $env:NPM_REGISTRY = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/"
}
if (-not $env:COREPACK_ENABLE_DOWNLOAD_PROMPT) {
    $env:COREPACK_ENABLE_DOWNLOAD_PROMPT = "0"
}

# Pin Corepack's cache directory to a build-scoped location. Without this, every
# build shares the user's default cache (%LOCALAPPDATA%\node\corepack on
# Windows, ~/.cache/node/corepack on Linux, ~/Library/Caches/node/corepack on
# macOS). prepareCorepackYarn.mjs rewrites that cache in place
# (rmSync(installDirectory) followed by renameSync(staging, installDirectory)),
# so concurrent builds racing on the same Corepack home can corrupt each other's
# cache. The CI pipelines already scope this per-job (e.g. AzDO uses
# Agent.TempDirectory); do the same here so multi-worktree setups stay
# isolated. Concurrent builds in the *same* worktree still race on this shared
# directory — prepareCorepackYarn.mjs handles the EEXIST/ENOTEMPTY rename
# collision but is not a substitute for a lock. The directory is gitignored.
if (-not $env:COREPACK_HOME) {
    $env:COREPACK_HOME = Join-Path $PSScriptRoot '.corepack-cache'
}

Write-Host "Checking prerequisites..."

# Check for Node.js
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error "Error: Node.js is not installed. Please install Node.js first."
    exit 1
}

# npm is required to install our pinned Corepack. It ships with every official
# Node.js distribution, so this should only fail on broken installs.
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Error "Error: npm is not available. Reinstall Node.js so npm is on PATH."
    exit 1
}

# Check for VS Code or VS Code Insiders
$hasVSCode = Get-Command code -ErrorAction SilentlyContinue
$hasVSCodeInsiders = Get-Command code-insiders -ErrorAction SilentlyContinue

if (-not $hasVSCode -and -not $hasVSCodeInsiders) {
    Write-Error "Error: VS Code or VS Code Insiders is not installed or not in PATH."
    Write-Host "Please install VS Code or VS Code Insiders and ensure it's added to your PATH."
    exit 1
}

# Check for dotnet
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "Error: .NET SDK is not installed. Please install .NET SDK first."
    Write-Host "Use the restore script at the repo root."
    exit 1
}

Write-Host "All prerequisites satisfied."

Write-Host ""
Write-Host "Installing pinned Corepack $CorepackVersion..."
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
npm install --global --force --registry "$env:NPM_REGISTRY" "corepack@$CorepackVersion"

if ($LASTEXITCODE -ne 0) {
    Write-Error "npm install -g corepack@$CorepackVersion failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# Verify the version actually on PATH matches our pin. On Windows the Node.js
# installer registers a `corepack.cmd` under %ProgramFiles%\nodejs which may
# shadow the npm-global shim under %APPDATA%\npm, so a successful
# `npm install -g corepack@<version>` does NOT guarantee that subsequent
# `corepack` calls resolve to it. Fail loudly here so we don't silently run
# with the wrong tool.
$installedCorepack = $null
try { $installedCorepack = (corepack --version).Trim() } catch { }
if ($installedCorepack -ne $CorepackVersion) {
    Write-Error @"
corepack version mismatch: expected $CorepackVersion, got '$installedCorepack'.
The bundled Corepack on PATH may be taking precedence over the npm-global install.
Ensure your npm global bin directory (typically %APPDATA%\npm on Windows) comes
before %ProgramFiles%\nodejs on PATH, or run `corepack disable` to remove the
bundled shim before re-running this script.
"@
    exit 1
}

Write-Host ""
Write-Host "Enabling Corepack package manager shims..."
corepack enable

if ($LASTEXITCODE -ne 0) {
    Write-Error "corepack enable failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Preparing Yarn from packageManager pin in package.json..."
node ./scripts/prepareCorepackYarn.mjs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Preparing Yarn for Corepack failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Running yarn install..."
corepack yarn install --frozen-lockfile --non-interactive

if ($LASTEXITCODE -ne 0) {
    Write-Error "yarn install failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Running yarn compile..."
corepack yarn compile

if ($LASTEXITCODE -ne 0) {
    Write-Error "yarn compile failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Building Aspire CLI..."
dotnet build ../src/Aspire.Cli/Aspire.Cli.csproj

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Build completed successfully!"
