<#
.SYNOPSIS
    Verify that an Aspire CLI flat-layout archive is valid and functional.

.DESCRIPTION
    This script:
    1. Validates archive compressed size (min 30 MB)
    2. Extracts the CLI archive to a temp location
    3. Validates flat layout structure (aspire + managed/ + dcp/)
    4. Checks the binary file signature (NativeAOT)
    5. Verifies no bundle.tar.gz in archive
    6. Runs 'aspire --version' to validate the binary executes
    7. Runs 'aspire new aspire-starter' to test full layout functionality
    8. Cleans up temp directories

.PARAMETER ArchivePath
    Path to the CLI archive (.zip or .tar.gz)

.EXAMPLE
    .\verify-cli-archive.ps1 -ArchivePath "artifacts\packages\Release\Shipping\aspire-cli-win-x64-10.0.0.zip"
#>

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'

# Minimum compressed archive size in bytes (30 MB)
$MinArchiveSize = 30MB

function Write-Step  { param([string]$msg) Write-Host "▶ $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "✅ $msg" -ForegroundColor Green }
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

    # Suppress interactive prompts and telemetry
    $env:ASPIRE_CLI_TELEMETRY_OPTOUT = "true"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "true"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "true"
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  Aspire CLI Archive Verification"
    Write-Host "=========================================="
    Write-Host "  Archive: $ArchivePath"
    Write-Host "=========================================="
    Write-Host ""

    # Step 1: Check archive compressed size
    Write-Step "Checking archive compressed size..."
    $archiveSize = (Get-Item $ArchivePath).Length
    $archiveSizeMB = [math]::Round($archiveSize / 1MB, 1)
    if ($archiveSize -lt $MinArchiveSize) {
        Write-Err "Archive is too small ($archiveSizeMB MB, minimum $([math]::Round($MinArchiveSize / 1MB)) MB). Layout may be incomplete."
        exit 1
    }
    Write-Ok "Archive size check passed ($archiveSizeMB MB)"

    # Step 2: Back up and clean ~/.aspire
    Write-Step "Cleaning ~/.aspire state..."
    $aspireDir = Join-Path $env:USERPROFILE ".aspire"
    if (Test-Path $aspireDir) {
        $aspireBackup = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-backup-$([System.IO.Path]::GetRandomFileName())"
        Move-Item $aspireDir $aspireBackup
        Write-Step "Backed up existing ~/.aspire to $aspireBackup"
    }
    Write-Ok "Clean ~/.aspire state"

    # Step 3: Extract the archive
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

    # Step 4: Validate flat layout structure
    Write-Step "Validating flat layout structure..."

    # Find the aspire binary
    $aspireBin = Join-Path $extractDir "aspire.exe"
    if (-not (Test-Path $aspireBin)) {
        $aspireBin = Join-Path $extractDir "aspire"
        if (-not (Test-Path $aspireBin)) {
            Write-Err "Could not find 'aspire' binary at archive root."
            Get-ChildItem $extractDir | Format-Table
            exit 1
        }
    }
    Write-Ok "Found aspire binary: $(Split-Path $aspireBin -Leaf)"

    # Check managed/ directory
    $managedDir = Join-Path $extractDir "managed"
    if (-not (Test-Path $managedDir)) {
        Write-Err "managed/ directory not found in archive."
        Get-ChildItem $extractDir | Format-Table
        exit 1
    }
    Write-Ok "Found managed/ directory"

    # Check managed/aspire-managed executable
    $managedBin = Join-Path $managedDir "aspire-managed.exe"
    if (-not (Test-Path $managedBin)) {
        $managedBin = Join-Path $managedDir "aspire-managed"
        if (-not (Test-Path $managedBin)) {
            Write-Err "aspire-managed executable not found in managed/."
            Get-ChildItem $managedDir | Format-Table
            exit 1
        }
    }
    Write-Ok "Found managed/$(Split-Path $managedBin -Leaf)"

    # Check dcp/ directory
    $dcpDir = Join-Path $extractDir "dcp"
    if (-not (Test-Path $dcpDir)) {
        Write-Err "dcp/ directory not found in archive."
        Get-ChildItem $extractDir | Format-Table
        exit 1
    }

    $dcpFileCount = (Get-ChildItem -Path $dcpDir -Recurse -File).Count
    if ($dcpFileCount -eq 0) {
        Write-Err "dcp/ directory is empty"
        exit 1
    }
    Write-Ok "Found dcp/ directory ($dcpFileCount files)"

    # Step 5: Check binary file signature (NativeAOT)
    Write-Step "Checking binary file signature..."
    if ($aspireBin.EndsWith(".exe")) {
        # Windows: check PE (MZ) header
        $bytes = [System.IO.File]::ReadAllBytes($aspireBin)
        if ($bytes.Length -lt 2 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
            $firstBytes = if ($bytes.Length -ge 2) { "0x{0:X2} 0x{1:X2}" -f $bytes[0], $bytes[1] } else { "too short" }
            Write-Err "Binary does not have PE (MZ) header. First bytes: $firstBytes"
            exit 1
        }
        Write-Ok "Binary is a valid PE executable"
    } else {
        # Unix: use file command if available
        if (Get-Command file -ErrorAction SilentlyContinue) {
            $fileOutput = & file -b $aspireBin 2>&1
            if ($fileOutput -match 'ELF 64-bit|Mach-O 64-bit') {
                Write-Ok "Binary is NativeAOT: $fileOutput"
            } else {
                Write-Err "Binary does not appear to be NativeAOT. file output: $fileOutput"
                exit 1
            }
        } else {
            Write-Step "file command not available — skipping binary format check"
        }
    }

    # Step 6: Verify no bundle.tar.gz in archive
    Write-Step "Checking archive does not contain bundle.tar.gz..."
    if ($ArchivePath.EndsWith(".zip")) {
        $zip = $null
        try {
            $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
            $zipEntries = $zip.Entries.Name
            if ($zipEntries -contains "bundle.tar.gz") {
                Write-Err "Archive contains bundle.tar.gz — this should be a flat layout archive without embedded bundle"
                exit 1
            }
        } finally {
            if ($zip) { $zip.Dispose() }
        }
    }
    elseif ($ArchivePath.EndsWith(".tar.gz")) {
        $tarOutput = tar -tzf $ArchivePath 2>&1
        if ($tarOutput -match 'bundle\.tar\.gz') {
            Write-Err "Archive contains bundle.tar.gz — this should be a flat layout archive without embedded bundle"
            exit 1
        }
    }
    Write-Ok "No bundle.tar.gz in archive (correct for flat layout)"

    # Step 7: Verify aspire --version
    # Run directly from extraction dir — LayoutDiscovery finds sibling managed/ and dcp/
    Write-Step "Running 'aspire --version'..."
    $versionOutput = & $aspireBin --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'aspire --version' failed with exit code $LASTEXITCODE"
        Write-Host "Output: $versionOutput"
        exit 1
    }
    Write-Host "  Version: $versionOutput"
    Write-Ok "'aspire --version' succeeded"

    # Step 8: Create a new project with aspire new
    # This exercises aspire-managed (template search + download + scaffolding) via the flat layout
    $projectDir = Join-Path $verifyTmpDir "VerifyApp"
    New-Item -ItemType Directory -Path $projectDir -Force | Out-Null

    Write-Step "Running 'aspire new aspire-starter --name VerifyApp --output $projectDir --non-interactive --nologo'..."
    & $aspireBin new aspire-starter --name VerifyApp --output $projectDir --non-interactive --nologo 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'aspire new' failed with exit code $LASTEXITCODE"
        exit 1
    }

    # Verify the project was created
    $appHostDir = Join-Path $projectDir "VerifyApp.AppHost"
    if (-not (Test-Path $appHostDir)) {
        Write-Err "Expected project directory 'VerifyApp.AppHost' not found after 'aspire new'"
        Get-ChildItem $projectDir | Format-Table
        exit 1
    }
    Write-Ok "'aspire new' created project successfully"

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
