<#
.SYNOPSIS
    Prepares the WinGet manifest artifact for Aspire CLI builds.

.DESCRIPTION
    Generates WinGet manifests, optionally validates/tests them, and adds the
    dogfood helper script. This script intentionally does not publish artifacts;
    each CI system owns its upload task.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidateSet("stable", "prerelease")]
    [string]$Channel,

    [Parameter(Mandatory = $false)]
    [string]$ArtifactVersion,

    [Parameter(Mandatory = $false)]
    [string]$ArchiveRoot,

    [Parameter(Mandatory = $false)]
    [string]$TemplateDir,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Full", "Offline", "GenerateOnly")]
    [string]$ValidationMode = "Full"
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($TemplateDir)) {
    $TemplateDir = Join-Path $ScriptDir "microsoft.aspire"
}

function Get-ArchiveVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArchiveRoot,

        [Parameter(Mandatory = $true)]
        [string]$Rid
    )

    if (-not (Test-Path $ArchiveRoot)) {
        Write-Error "Archive root directory not found: $ArchiveRoot"
        exit 1
    }

    $prefix = "aspire-cli-$Rid-"
    $suffix = ".zip"
    $matchedItems = @(Get-ChildItem -Path $ArchiveRoot -File -Recurse -Filter "$prefix*$suffix" | Sort-Object FullName)

    if ($matchedItems.Count -eq 0) {
        Write-Error "Could not find archive '$prefix*$suffix' under '$ArchiveRoot' to infer the Aspire CLI version."
        exit 1
    }

    if ($matchedItems.Count -gt 1) {
        $matchList = $matchedItems | ForEach-Object { "  $($_.FullName)" }
        Write-Error "Found multiple archives matching '$prefix*$suffix' under '$ArchiveRoot':`n$($matchList -join "`n")"
        exit 1
    }

    $fileName = $matchedItems[0].Name
    return $fileName.Substring($prefix.Length, $fileName.Length - $prefix.Length - $suffix.Length)
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    if ([string]::IsNullOrWhiteSpace($ArchiveRoot)) {
        Write-Error "Version is required when ArchiveRoot is not specified."
        exit 1
    }

    $Version = Get-ArchiveVersion -ArchiveRoot $ArchiveRoot -Rid "win-x64"
}

if ([string]::IsNullOrWhiteSpace($ArtifactVersion)) {
    $ArtifactVersion = $Version
}

$isPrereleaseInStablePackage = $Channel -ne "stable"

Write-Host "Preparing WinGet manifests"
Write-Host "  Version: $Version"
Write-Host "  Channel: $Channel"
Write-Host "  Artifact version: $ArtifactVersion"
Write-Host "  Template dir: $TemplateDir"
Write-Host "  Output path: $OutputPath"
Write-Host "  Validation mode: $ValidationMode"

$generateArgs = @{
    Version = $Version
    ArtifactVersion = $ArtifactVersion
    TemplateDir = $TemplateDir
    OutputPath = $OutputPath
}

if (-not [string]::IsNullOrWhiteSpace($ArchiveRoot)) {
    $generateArgs.ArchiveRoot = $ArchiveRoot
}

if ($ValidationMode -ne "Full") {
    $generateArgs.SkipUrlValidation = $true
}

if ($isPrereleaseInStablePackage) {
    $generateArgs.IsPrereleaseInStablePackage = $true
}

& (Join-Path $ScriptDir "generate-manifests.ps1") @generateArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "generate-manifests.ps1 failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "Manifest files generated:"
Get-ChildItem -Path $OutputPath -Recurse | Format-Table FullName

$versionedManifestPath = Get-ChildItem -Path $OutputPath -Directory -Recurse |
    Where-Object { $_.Name -eq $Version } |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $versionedManifestPath) {
    $versionedManifestPath = $OutputPath
}

Write-Host "Versioned manifest path: $versionedManifestPath"

if ($ValidationMode -ne "GenerateOnly") {
    $winget = Get-Command winget -ErrorAction SilentlyContinue

    if ($winget) {
        Write-Host "Enabling local manifest files in winget settings..."
        winget settings --enable LocalManifestFiles
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to enable local manifests. This may require admin privileges."
        }

        Write-Host "Running winget validate..."
        winget validate --manifest $versionedManifestPath
        if ($LASTEXITCODE -ne 0) {
            Write-Error "winget validate failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        Write-Host "winget validate passed"
    } elseif ($ValidationMode -eq "Full") {
        Write-Error "winget is required for Full validation mode, but it was not found in PATH."
        exit 1
    } else {
        Write-Warning "winget was not found in PATH; skipping offline manifest validation."
    }
}

if ($ValidationMode -eq "Full") {
    Write-Host "Testing WinGet manifest install/uninstall at: $versionedManifestPath"

    Write-Host "Verifying aspire is not already installed..."
    if (Get-Command aspire -ErrorAction SilentlyContinue) {
        Write-Error "aspire command is already available before install - test environment is not clean"
        exit 1
    }
    Write-Host "  Confirmed: aspire is not in PATH"

    Write-Host "Installing Aspire.Cli from local manifest..."
    winget install --manifest $versionedManifestPath --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Error "winget install failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "Install succeeded"

    Write-Host "Refreshing PATH environment variable..."
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")

    $failed = $false
    Write-Host "Verifying aspire CLI is in PATH (new process)..."
    try {
        $aspireInfo = pwsh -NoProfile -Command '
          $cmd = Get-Command aspire -ErrorAction SilentlyContinue
          if (-not $cmd) { Write-Error "aspire not found in PATH"; exit 1 }
          Write-Host "  Path: $($cmd.Source)"
          $v = & aspire --version 2>&1
          if ($LASTEXITCODE -ne 0) { Write-Error "aspire --version failed: $v"; exit $LASTEXITCODE }
          Write-Host "  Version: $v"
        '
        if ($LASTEXITCODE -ne 0) {
            throw "Child process exited with code $LASTEXITCODE"
        }
        Write-Host "aspire CLI verified"
    } catch {
        Write-Host "##[error]Failed to verify aspire CLI: $_"
        $failed = $true
    }

    Write-Host "Uninstalling Aspire.Cli..."
    winget uninstall --manifest $versionedManifestPath --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        if ($failed) {
            Write-Warning "winget uninstall also failed with exit code $LASTEXITCODE (ignoring since verification already failed)"
        } else {
            Write-Error "winget uninstall failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
        }
    } else {
        Write-Host "Uninstall succeeded"
    }

    if ($failed) {
        exit 1
    }
}

Copy-Item (Join-Path $ScriptDir "dogfood.ps1") (Join-Path $OutputPath "dogfood.ps1")

Write-Host "WinGet manifest artifact prepared at: $OutputPath"
