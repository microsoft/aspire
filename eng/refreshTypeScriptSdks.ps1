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
# The TypeScript playground AppHosts live under these roots. Each AppHost is a directory
# containing an entry point named 'apphost.mts' (or the legacy 'apphost.ts'). This replaced
# the old 'playground/polyglot/TypeScript/<app>/ValidationAppHost/apphost.ts' layout.
$playgroundRoots = @(
    Join-Path -Path $repoRoot -ChildPath 'playground/TypeScriptAppHost'
    Join-Path -Path $repoRoot -ChildPath 'playground/TypeScriptApps'
)
$cliProject = Join-Path -Path $repoRoot -ChildPath 'src/Aspire.Cli/Aspire.Cli.csproj'

function Invoke-RepoRestore {
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        & (Join-Path $repoRoot 'restore.cmd')
    }
    else {
        & (Join-Path $repoRoot 'restore.sh')
    }
}

function Get-ValidationAppHosts {
    # Discover TypeScript AppHosts by their entry point file. The CLI recognizes both the
    # modern 'apphost.mts' and the legacy 'apphost.ts' (see TypeScriptLanguageSupport
    # detection patterns). The directory containing the entry point is the working directory
    # for npm install, 'aspire restore', and the generated SDK output (under '.aspire/modules'
    # for 'apphost.mts', or the legacy '.modules' folder for 'apphost.ts').
    $appHosts = foreach ($root in $playgroundRoots) {
        if (-not (Test-Path $root)) {
            continue
        }

        foreach ($entryPoint in Get-ChildItem -Path $root -Recurse -File -Include 'apphost.mts', 'apphost.ts' | Sort-Object FullName) {
            $appHostDir = $entryPoint.Directory
            $relativeName = [System.IO.Path]::GetRelativePath($repoRoot, $appHostDir.FullName).Replace('\', '/')

            if ($relativeName -notlike $AppPattern -and $appHostDir.Name -notlike $AppPattern) {
                continue
            }

            [pscustomobject]@{
                Directory   = $appHostDir.FullName
                EntryPoint  = $entryPoint.Name
                DisplayName = $relativeName
            }
        }
    }

    if (@($appHosts).Count -eq 0) {
        throw "No TypeScript playground AppHost directories matched '$AppPattern'."
    }

    return @($appHosts)
}

function Get-RequiredGeneratedFiles([string]$entryPoint) {
    # The CLI emits generated SDK module files whose extension mirrors the AppHost entry point:
    # 'apphost.mts' produces '.mts' modules, while the legacy 'apphost.ts' produces '.ts'
    # modules (see GuestAppHostProject.ConvertGeneratedFilesForLegacyTypeScriptAppHost).
    $extension = if ($entryPoint -like '*.mts') { 'mts' } else { 'ts' }
    return @("aspire.$extension", "base.$extension", "transport.$extension")
}

function Get-GeneratedModulesDir([string]$appDir, [string]$entryPoint) {
    # The CLI writes the generated SDK to a directory that depends on the entry point: modern
    # 'apphost.mts' apps use '.aspire/modules' (ILanguageDiscovery.GeneratedFolderName), while
    # legacy 'apphost.ts' apps still import from './.modules/' so the CLI writes there instead
    # (ILanguageDiscovery.LegacyGeneratedFolderName; see the code-gen output path in
    # GuestAppHostProject). Mirror that here so cleanup and verification target the directory
    # the CLI actually writes to.
    $relativeDir = if ($entryPoint -like '*.mts') { '.aspire/modules' } else { '.modules' }
    return Join-Path $appDir $relativeDir
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

function Assert-GeneratedSdkFiles([string]$generatedDir, [string[]]$requiredGeneratedFiles) {
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
Write-Host "Playground roots: $($playgroundRoots -join ', ')"
Write-Host "CLI project: $cliProject"

Invoke-RepoRestore

& dotnet build $cliProject /p:SkipNativeBuild=true

$appHosts = Get-ValidationAppHosts
Write-Host "Found $($appHosts.Count) TypeScript playground apps matching '$AppPattern'."

$failures = [System.Collections.Generic.List[string]]::new()
$updated = [System.Collections.Generic.List[string]]::new()

foreach ($appHost in $appHosts) {
    $appName = $appHost.DisplayName

    Write-Host ''
    Write-Host '----------------------------------------'
    Write-Host "Refreshing: $appName"
    Write-Host '----------------------------------------'

    Push-Location $appHost.Directory
    try {
        Write-Host '  -> Installing npm dependencies...'
        Install-NodeDependencies -appDir $appHost.Directory

        $generatedDir = Get-GeneratedModulesDir -appDir $appHost.Directory -entryPoint $appHost.EntryPoint
        if (Test-Path $generatedDir) {
            Write-Host '  -> Clearing existing generated SDK...'
            Remove-Item -Path $generatedDir -Recurse -Force
        }

        Write-Host '  -> Running aspire restore...'
        & dotnet run --no-build --project $cliProject -- restore

        Write-Host '  -> Verifying generated SDK...'
        Assert-GeneratedSdkFiles -generatedDir $generatedDir -requiredGeneratedFiles (Get-RequiredGeneratedFiles $appHost.EntryPoint)

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
