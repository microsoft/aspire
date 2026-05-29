#!/usr/bin/env pwsh

$ErrorActionPreference = "Stop"

Write-Host "Checking prerequisites..."

# Check for Node.js
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error "Error: Node.js is not installed. Please install Node.js first."
    exit 1
}

# Check for Corepack so the build uses the Yarn Classic version that matches extension/yarn.lock.
if (-not (Get-Command corepack -ErrorAction SilentlyContinue)) {
    Write-Error "Error: Corepack is not installed. Please install a Node.js version that includes Corepack."
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

# Ensure we run from the extension directory
Set-Location $PSScriptRoot

Write-Host ""
Write-Host "Running yarn install..."
corepack yarn@1.22.22 install --frozen-lockfile --non-interactive

if ($LASTEXITCODE -ne 0) {
    Write-Error "yarn install failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Running yarn compile..."
corepack yarn@1.22.22 compile

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
