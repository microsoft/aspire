<#
.SYNOPSIS
    Verify that CLI dotnet-tool nupkgs are valid.

.DESCRIPTION
    This script:
    1. Finds the RID-specific tool nupkg (Aspire.Cli.<rid>.*.nupkg)
    2. Validates the nupkg size is above a minimum threshold
    3. Extracts the package and verifies the aspire binary is present
    4. On Windows: checks the binary is a PE executable
    5. Checks the primary pointer package (Aspire.Cli.*.nupkg) exists
    6. (Optional) Installs the tool locally and runs aspire --version + aspire new

.PARAMETER PackagesDir
    Path to the directory containing nupkg files

.PARAMETER Rid
    Runtime identifier (e.g., win-x64, win-arm64)

.PARAMETER RunFunctionalTests
    When set, installs the tool from the local packages and runs functional checks
    (aspire --version, aspire new). Only use for RIDs that can execute on the agent.

.EXAMPLE
    .\verify-cli-tool-nupkg.ps1 -PackagesDir "artifacts\packages\Release" -Rid "win-x64"
.EXAMPLE
    .\verify-cli-tool-nupkg.ps1 -PackagesDir "artifacts\packages\Release" -Rid "linux-x64" -RunFunctionalTests
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$PackagesDir,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [switch]$RunFunctionalTests
)

$ErrorActionPreference = 'Stop'

function Write-Step  { param([string]$msg) Write-Host "`u{25B6} $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "`u{2705} $msg" -ForegroundColor Green }
function Write-Err   { param([string]$msg) Write-Host "`u{274C} $msg" -ForegroundColor Red }

# Minimum expected nupkg size in bytes (5 MB — NativeAOT binary should be large)
$MinNupkgSize = 5MB

$extractDir = $null
$toolInstallDir = $null

try {
    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  Aspire CLI Tool Nupkg Verification"
    Write-Host "=========================================="
    Write-Host "  Packages dir:     $PackagesDir"
    Write-Host "  RID:              $Rid"
    Write-Host "  Functional tests: $RunFunctionalTests"
    Write-Host "=========================================="
    Write-Host ""

    # Resolve the effective packages source directory (prefer Shipping/ when present)
    $effectiveDir = if (Test-Path (Join-Path $PackagesDir "Shipping")) {
        Join-Path $PackagesDir "Shipping"
    } else {
        $PackagesDir
    }
    Write-Step "Effective source dir: $effectiveDir"

    # Step 1: Find the RID-specific nupkg
    Write-Step "Looking for RID-specific nupkg matching Aspire.Cli.$Rid.*.nupkg ..."
    $ridNupkg = Get-ChildItem -Path $effectiveDir -Filter "Aspire.Cli.$Rid.*.nupkg" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '\.symbols\.' } |
        Select-Object -First 1

    if (-not $ridNupkg) {
        Write-Err "No RID-specific nupkg found for $Rid in $effectiveDir"
        Write-Host "Contents of packages directory:"
        Get-ChildItem -Path $effectiveDir -Filter "*.nupkg" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name)" }
        exit 1
    }

    Write-Ok "Found: $($ridNupkg.Name)"

    # Step 2: Check nupkg size
    $nupkgSize = $ridNupkg.Length
    $sizeMB = [math]::Round($nupkgSize / 1MB, 1)
    Write-Step "Checking nupkg size: $sizeMB MB ($nupkgSize bytes)"

    if ($nupkgSize -lt $MinNupkgSize) {
        Write-Err "Nupkg is too small ($nupkgSize bytes, minimum $MinNupkgSize bytes). The NativeAOT binary may be missing."
        exit 1
    }
    Write-Ok "Size check passed ($sizeMB MB)"

    # Step 3: Extract and verify the binary
    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-tool-verify-$([System.IO.Path]::GetRandomFileName())"
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    Write-Step "Extracting nupkg to verify contents..."
    # nupkg is a zip file
    $zipPath = Join-Path $extractDir "$($ridNupkg.BaseName).zip"
    Copy-Item $ridNupkg.FullName $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

    # The tool binary is at tools/net10.0/<rid>/aspire.exe (or aspire on Unix)
    $binaryName = if ($Rid -like 'win-*') { "aspire.exe" } else { "aspire" }
    $toolBinary = Get-ChildItem -Path $extractDir -Recurse -Filter $binaryName -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $toolBinary) {
        Write-Err "Could not find '$binaryName' inside the nupkg"
        Write-Host "Nupkg contents:"
        Get-ChildItem -Path $extractDir -Recurse | Select-Object -First 20 | ForEach-Object { Write-Host "  $($_.FullName.Substring($extractDir.Length))" }
        exit 1
    }
    Write-Ok "Found binary: $($toolBinary.FullName.Substring($extractDir.Length))"

    # Step 4: Verify binary is a PE executable on Windows
    if ($Rid -like 'win-*') {
        Write-Step "Checking binary is a PE executable..."
        $bytes = [System.IO.File]::ReadAllBytes($toolBinary.FullName)
        if ($bytes.Length -lt 2 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
            $firstBytes = if ($bytes.Length -ge 2) { "0x{0:X2} 0x{1:X2}" -f $bytes[0], $bytes[1] } else { "too short" }
            Write-Err "Binary does not have PE (MZ) header. First bytes: $firstBytes"
            exit 1
        }
        Write-Ok "Binary is a valid PE executable"
    }

    # Step 5: Verify the pointer/shim package exists
    Write-Step "Looking for primary pointer package (Aspire.Cli.*.nupkg, not RID-specific)..."
    $primaryNupkg = Get-ChildItem -Path $effectiveDir -Filter "Aspire.Cli.*.nupkg" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '\.symbols\.' -and $_.Name -notmatch 'Aspire\.Cli\.(win|linux|osx|musl)-' } |
        Select-Object -First 1

    if (-not $primaryNupkg) {
        Write-Err "No primary pointer package found (Aspire.Cli.*.nupkg without RID prefix)"
        Write-Host "All Aspire.Cli nupkgs in ${effectiveDir}:"
        Get-ChildItem -Path $effectiveDir -Filter "Aspire.Cli*.nupkg" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name)" }
        exit 1
    }
    Write-Ok "Found primary: $($primaryNupkg.Name)"

    # Step 6: Functional tests — install tool and exercise CLI
    if ($RunFunctionalTests) {
        Write-Host ""
        Write-Host "--- Functional Tests ---" -ForegroundColor Magenta

        # Derive the package version from the RID-specific nupkg filename
        # Filename pattern: Aspire.Cli.<rid>.<version>.nupkg
        $versionMatch = $ridNupkg.Name -replace "^Aspire\.Cli\.$([regex]::Escape($Rid))\.", '' -replace '\.nupkg$', ''
        Write-Step "Detected package version: $versionMatch"

        # Install as a local tool-path tool from the packages directory
        $toolInstallDir = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-tool-install-$([System.IO.Path]::GetRandomFileName())"
        New-Item -ItemType Directory -Path $toolInstallDir -Force | Out-Null

        Write-Step "Installing Aspire.Cli tool (version $versionMatch) from $effectiveDir ..."
        $installArgs = @(
            'tool', 'install', 'Aspire.Cli',
            '--tool-path', $toolInstallDir,
            '--add-source', $effectiveDir,
            '--version', $versionMatch
        )
        $installOutput = & dotnet @installArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'dotnet tool install' failed (exit code $LASTEXITCODE)"
            Write-Host ($installOutput -join "`n")
            exit 1
        }
        Write-Ok "Tool installed to $toolInstallDir"

        # Determine the shim path
        $shimName = if ($Rid -like 'win-*') { "aspire.exe" } else { "aspire" }
        $shimPath = Join-Path $toolInstallDir $shimName
        if (-not (Test-Path $shimPath)) {
            # On Windows, NativeAOT tools get a .cmd shim
            $shimPath = Join-Path $toolInstallDir "aspire.cmd"
        }
        if (-not (Test-Path $shimPath)) {
            Write-Err "Could not find aspire shim in $toolInstallDir"
            Write-Host "Contents:"
            Get-ChildItem $toolInstallDir | ForEach-Object { Write-Host "  $($_.Name)" }
            exit 1
        }

        # Step 7: aspire --version
        Write-Step "Running 'aspire --version' ..."
        $env:ASPIRE_CLI_TELEMETRY_OPTOUT = 'true'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true'
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        $versionOutput = & $shimPath --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire --version' failed (exit code $LASTEXITCODE)"
            Write-Host ($versionOutput -join "`n")
            exit 1
        }
        Write-Ok "'aspire --version' returned: $($versionOutput -join ' ')"

        # Step 8: aspire new — exercises bundle self-extraction + template scaffolding
        $projectDir = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-verify-project-$([System.IO.Path]::GetRandomFileName())"
        New-Item -ItemType Directory -Path $projectDir -Force | Out-Null

        Write-Step "Running 'aspire new aspire-starter' ..."
        $newOutput = & $shimPath new aspire-starter --name VerifyApp --output $projectDir --non-interactive --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Err "'aspire new' failed (exit code $LASTEXITCODE)"
            Write-Host ($newOutput -join "`n")
            exit 1
        }

        $appHostDir = Join-Path $projectDir "VerifyApp.AppHost"
        if (Test-Path $appHostDir) {
            Write-Ok "'aspire new' created project successfully"
        } else {
            Write-Err "'aspire new' did not create expected VerifyApp.AppHost directory"
            Get-ChildItem $projectDir -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name)" }
            exit 1
        }

        # Cleanup project dir
        if (Test-Path $projectDir) {
            Remove-Item -Recurse -Force $projectDir -ErrorAction SilentlyContinue
        }
    }

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  All tool nupkg checks passed!" -ForegroundColor Green
    Write-Host "=========================================="
    Write-Host ""
}
catch {
    Write-Err "Verification failed: $_"
    Write-Host $_.ScriptStackTrace
    exit 1
}
finally {
    if ($extractDir -and (Test-Path $extractDir)) {
        Remove-Item -Recurse -Force $extractDir -ErrorAction SilentlyContinue
    }
    if ($toolInstallDir -and (Test-Path $toolInstallDir)) {
        Remove-Item -Recurse -Force $toolInstallDir -ErrorAction SilentlyContinue
    }
}
