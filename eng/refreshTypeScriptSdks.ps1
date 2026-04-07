#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [string]$AppPattern = '*'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$playgroundRoot = Join-Path -Path $repoRoot -ChildPath 'playground/polyglot/TypeScript'
$cliProject = Join-Path -Path $repoRoot -ChildPath 'src/Aspire.Cli/Aspire.Cli.csproj'
$requiredGeneratedFiles = @('aspire.ts', 'base.ts', 'transport.ts')

function Invoke-RepoRestore {
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        & (Join-Path $repoRoot 'restore.cmd')
    }
    else {
        & (Join-Path $repoRoot 'restore.sh')
    }
}

function Get-ValidationAppHosts {
    if (-not (Test-Path $playgroundRoot)) {
        throw "TypeScript playground directory not found at '$playgroundRoot'."
    }

    $appHosts = foreach ($integrationDir in Get-ChildItem -Path $playgroundRoot -Directory | Sort-Object Name) {
        if ($integrationDir.Name -notlike $AppPattern) {
            continue
        }

        $appHostDir = Join-Path $integrationDir.FullName 'ValidationAppHost'
        $appHostEntryPoint = Join-Path $appHostDir 'apphost.ts'
        if (Test-Path $appHostEntryPoint) {
            Get-Item $appHostDir
        }
    }

    if (@($appHosts).Count -eq 0) {
        throw "No TypeScript playground ValidationAppHost directories matched '$AppPattern'."
    }

    return @($appHosts)
}

function Install-NodeDependencies([string]$appDir) {
    Push-Location $appDir
    try {
        $packageLockPath = Join-Path $appDir 'package-lock.json'
        if (Test-Path $packageLockPath) {
            & npm ci --ignore-scripts --no-audit --no-fund
        }
        else {
            & npm install --ignore-scripts --no-audit --no-fund
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-GeneratedSdkFiles([string]$appDir) {
    $generatedDir = Join-Path $appDir '.modules'
    foreach ($file in $requiredGeneratedFiles) {
        $path = Join-Path $generatedDir $file
        if (-not (Test-Path $path)) {
            throw "Expected generated SDK file '$path' was not created."
        }
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found in PATH."
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw "npm was not found in PATH."
}

Write-Host '=== Refreshing TypeScript playground SDKs ==='
Write-Host "Playground root: $playgroundRoot"
Write-Host "CLI project: $cliProject"

Invoke-RepoRestore

& dotnet build $cliProject /p:SkipNativeBuild=true

$appHosts = Get-ValidationAppHosts
Write-Host "Found $($appHosts.Count) TypeScript playground apps matching '$AppPattern'."

$failures = [System.Collections.Generic.List[string]]::new()
$updated = [System.Collections.Generic.List[string]]::new()

foreach ($appHost in $appHosts) {
    $appName = '{0}/{1}' -f (Split-Path -Path $appHost.FullName -Parent | Split-Path -Leaf), $appHost.Name

    Write-Host ''
    Write-Host '----------------------------------------'
    Write-Host "Refreshing: $appName"
    Write-Host '----------------------------------------'

    Push-Location $appHost.FullName
    try {
        Write-Host '  -> Installing npm dependencies...'
        Install-NodeDependencies -appDir $appHost.FullName

        $generatedDir = Join-Path $appHost.FullName '.modules'
        if (Test-Path $generatedDir) {
            Write-Host '  -> Clearing existing generated SDK...'
            Remove-Item -Path $generatedDir -Recurse -Force
        }

        Write-Host '  -> Running aspire restore...'
        & dotnet run --no-build --project $cliProject -- restore

        Write-Host '  -> Verifying generated SDK...'
        Assert-GeneratedSdkFiles -appDir $appHost.FullName

        Write-Host "  OK $appName refreshed"
        $updated.Add($appName)
    }
    catch {
        Write-Host "  ERROR failed to refresh $appName"
        Write-Host ($_ | Out-String)
        $failures.Add($appName)
    }
    finally {
        Pop-Location
    }
}

Write-Host ''
Write-Host '----------------------------------------'
Write-Host "Results: $($updated.Count) refreshed, $($failures.Count) failed out of $($appHosts.Count) apps"
Write-Host '----------------------------------------'

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'Failed apps:'
    foreach ($failure in $failures) {
        Write-Host "  - $failure"
    }

    exit 1
}

Write-Host 'All TypeScript playground SDKs refreshed successfully.'
