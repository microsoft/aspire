<#
.SYNOPSIS
    Verify that a signed Aspire CLI archive produces a working binary.

.DESCRIPTION
    This script:
    1. Cleans ~/.aspire to ensure no stale state
    2. Extracts the CLI archive to a temp location
    3. Runs 'aspire --version' to validate the binary executes
    4. Runs 'aspire new aspire-starter --name VerifyApp' to test bundle self-extraction + project creation
    5. Runs 'aspire restore' on the created project to test NuGet restore via aspire-managed
    6. Runs 'dotnet build' on the created project to validate the template compiles
    7. Cleans up temp directories

.PARAMETER ArchivePath
    Path to the CLI archive (.zip or .tar.gz)

.PARAMETER DotNetRoot
    Optional path to the .NET SDK root

.PARAMETER SkipBuild
    Skip the project build step (only verify extraction + version)

.EXAMPLE
    .\verify-cli-archive.ps1 -ArchivePath "artifacts\packages\Release\Shipping\aspire-cli-win-x64-10.0.0.zip"
#>

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ArchivePath,

    [string]$DotNetRoot = "",

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

function Write-Step  { param([string]$msg) Write-Host "▶ $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "✅ $msg" -ForegroundColor Green }
function Write-Warn  { param([string]$msg) Write-Host "⚠️  $msg" -ForegroundColor Yellow }
function Write-Err   { param([string]$msg) Write-Host "❌ $msg" -ForegroundColor Red }

$verifyTmpDir = $null
$aspireBackup = $null

function Invoke-Cleanup {
    if ($verifyTmpDir -and (Test-Path $verifyTmpDir)) {
        Write-Step "Cleaning up temp directory: $verifyTmpDir"
        Remove-Item -Recurse -Force $verifyTmpDir -ErrorAction SilentlyContinue
    }
    # Restore ~/.aspire if we backed it up
    $aspireDir = Join-Path $env:USERPROFILE ".aspire"
    if ($aspireBackup -and (Test-Path $aspireBackup)) {
        if (Test-Path $aspireDir) {
            Remove-Item -Recurse -Force $aspireDir -ErrorAction SilentlyContinue
        }
        Move-Item $aspireBackup $aspireDir
        Write-Step "Restored original ~/.aspire"
    }
}

try {
    # Validate archive exists
    if (-not (Test-Path $ArchivePath)) {
        Write-Err "Archive not found: $ArchivePath"
        exit 1
    }

    $ArchivePath = (Resolve-Path $ArchivePath).Path

    # Set up dotnet if specified
    if ($DotNetRoot) {
        $env:DOTNET_ROOT = $DotNetRoot
        $env:PATH = "$DotNetRoot;$env:PATH"
    }

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  Aspire CLI Archive Verification"
    Write-Host "=========================================="
    Write-Host "  Archive: $ArchivePath"
    Write-Host "  dotnet:  $(Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)"
    Write-Host "=========================================="
    Write-Host ""

    # Step 0: Verify dotnet is available
    Write-Step "Checking dotnet SDK availability..."
    $dotnetVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0) {
        Write-Err "dotnet command not found or failed."
        exit 1
    }
    Write-Host "  dotnet version: $dotnetVersion"
    Write-Ok "dotnet SDK available"

    # Step 1: Back up and clean ~/.aspire
    Write-Step "Cleaning ~/.aspire state..."
    $aspireDir = Join-Path $env:USERPROFILE ".aspire"
    if (Test-Path $aspireDir) {
        $aspireBackup = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-backup-$([System.IO.Path]::GetRandomFileName())"
        Move-Item $aspireDir $aspireBackup
        Write-Step "Backed up existing ~/.aspire to $aspireBackup"
    }
    Write-Ok "Clean ~/.aspire state"

    # Step 2: Extract the archive
    $verifyTmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-verify-$([System.IO.Path]::GetRandomFileName())"
    $extractDir = Join-Path $verifyTmpDir "cli"
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    Write-Step "Extracting archive to $extractDir..."
    if ($ArchivePath.EndsWith(".zip")) {
        Expand-Archive -Path $ArchivePath -DestinationPath $extractDir
    }
    elseif ($ArchivePath.EndsWith(".tar.gz")) {
        tar -xzf $ArchivePath -C $extractDir
        if ($LASTEXITCODE -ne 0) {
            Write-Err "Failed to extract tar.gz archive"
            exit 1
        }
    }
    else {
        Write-Err "Unsupported archive format: $ArchivePath (expected .zip or .tar.gz)"
        exit 1
    }

    # Find the aspire binary
    $aspireBin = Join-Path $extractDir "aspire.exe"
    if (-not (Test-Path $aspireBin)) {
        $aspireBin = Join-Path $extractDir "aspire"
        if (-not (Test-Path $aspireBin)) {
            Write-Err "Could not find 'aspire' binary in extracted archive."
            Get-ChildItem $extractDir | Format-Table
            exit 1
        }
    }
    Write-Ok "Extracted CLI binary: $aspireBin"

    # Install to ~/.aspire/bin so self-extraction works correctly
    Write-Step "Installing CLI to ~/.aspire/bin..."
    $aspireDir = Join-Path $env:USERPROFILE ".aspire"
    $aspireBinDir = Join-Path $aspireDir "bin"
    New-Item -ItemType Directory -Path $aspireBinDir -Force | Out-Null
    Copy-Item $aspireBin (Join-Path $aspireBinDir (Split-Path $aspireBin -Leaf))
    $aspireBin = Join-Path $aspireBinDir (Split-Path $aspireBin -Leaf)
    $env:PATH = "$aspireBinDir;$env:PATH"
    Write-Ok "CLI installed to ~/.aspire/bin"

    # Step 3: Verify aspire --version
    Write-Step "Running 'aspire --version'..."
    $versionOutput = & $aspireBin --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'aspire --version' failed with exit code $LASTEXITCODE"
        Write-Host "Output: $versionOutput"
        exit 1
    }
    Write-Host "  Version: $versionOutput"
    Write-Ok "'aspire --version' succeeded"

    # Step 4: Create a new project with aspire new
    $projectDir = Join-Path $verifyTmpDir "VerifyApp"
    New-Item -ItemType Directory -Path $projectDir -Force | Out-Null

    Write-Step "Running 'aspire new aspire-starter --name VerifyApp'..."
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "true"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "true"
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"

    Push-Location $projectDir
    try {
        & $aspireBin new aspire-starter --name VerifyApp 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire new' failed with exit code $LASTEXITCODE"
            exit 1
        }
    }
    finally {
        Pop-Location
    }

    # Verify the project was created
    $appHostDir = Join-Path $projectDir "VerifyApp.AppHost"
    if (-not (Test-Path $appHostDir)) {
        Write-Err "Expected project directory 'VerifyApp.AppHost' not found after 'aspire new'"
        Get-ChildItem $projectDir | Format-Table
        exit 1
    }
    Write-Ok "'aspire new' created project successfully"

    if ($SkipBuild) {
        Write-Warn "Skipping build step (-SkipBuild)"
    }
    else {
        # Step 5: Restore the project
        $appHostCsproj = Join-Path $appHostDir "VerifyApp.AppHost.csproj"
        Write-Step "Running 'aspire restore' on the created project..."
        Push-Location $projectDir
        try {
            & $aspireBin restore --apphost $appHostCsproj 2>&1 | Write-Host
            if ($LASTEXITCODE -ne 0) {
                Write-Err "'aspire restore' failed with exit code $LASTEXITCODE"
                exit 1
            }
        }
        finally {
            Pop-Location
        }
        Write-Ok "'aspire restore' succeeded"

        # Step 6: Build the project
        Write-Step "Running 'dotnet build' on the created project..."
        Push-Location $projectDir
        try {
            & dotnet build $appHostCsproj 2>&1 | Write-Host
            if ($LASTEXITCODE -ne 0) {
                Write-Err "'dotnet build' failed with exit code $LASTEXITCODE"
                exit 1
            }
        }
        finally {
            Pop-Location
        }
        Write-Ok "'dotnet build' succeeded"
    }

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  All verification checks passed!" -ForegroundColor Green
    Write-Host "=========================================="
    Write-Host ""
}
catch {
    Write-Err "Verification failed: $_"
    Write-Host $_.ScriptStackTrace
    exit 1
}
finally {
    Invoke-Cleanup
}
