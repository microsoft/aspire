#!/usr/bin/env pwsh

$ErrorActionPreference = "Stop"

Write-Host "Checking prerequisites..."

# Check for Node.js
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error "Error: Node.js is not installed. Please install Node.js first."
    exit 1
}

# Check for npm
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Error "Error: npm is not installed. Please install npm first."
    exit 1
}

# Check for yarn
if (-not (Get-Command yarn -ErrorAction SilentlyContinue) -and (Get-Command corepack -ErrorAction SilentlyContinue)) {
    Write-Host "Enabling yarn with corepack..."
    corepack enable yarn
}

if (-not (Get-Command yarn -ErrorAction SilentlyContinue)) {
    Write-Error "Error: yarn is not installed. Please install yarn first."
    Write-Host "You can install yarn by running: npm install -g corepack@latest; corepack enable yarn"
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
yarn install

if ($LASTEXITCODE -ne 0) {
    Write-Error "yarn install failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Running yarn compile..."
yarn compile

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
