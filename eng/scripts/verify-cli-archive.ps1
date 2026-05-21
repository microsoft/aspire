<#
.SYNOPSIS
    Verify that a signed Aspire CLI archive produces a working binary.

.DESCRIPTION
    This script:
    1. Cleans ~/.aspire to ensure no stale state
    2. Extracts the CLI archive to a temp location
    3. Verifies the archive shape contains the native CLI payload and no install-route sidecar
    4. Runs 'aspire --version' to validate the binary executes
    5. Runs 'aspire new aspire-starter' to test C# starter creation
    6. Cleans up temp directories

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

function Write-Step  { param([string]$msg) Write-Host "▶ $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "✅ $msg" -ForegroundColor Green }
function Write-Err   { param([string]$msg) Write-Host "❌ $msg" -ForegroundColor Red }

function Get-ExecutableFileName([string]$BaseName) {
    if (Test-IsWindows) {
        return "$BaseName.exe"
    }

    return $BaseName
}

function Get-UserHome {
    if ($env:USERPROFILE) {
        return $env:USERPROFILE
    }

    if ($env:HOME) {
        return $env:HOME
    }

    throw "Unable to determine the user home directory."
}

function Test-IsWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Set-ExecutablePermission([string]$Path) {
    if (Test-IsWindows) {
        return
    }

    & chmod +x $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to mark '$Path' as executable."
    }
}

function Get-ArchiveRidFamily([string]$ArchiveFileName) {
    switch -Wildcard ($ArchiveFileName) {
        '*win-*'   { 'win';   break }
        '*osx-*'   { 'osx';   break }
        '*linux-*' { 'linux'; break }
        default    { $null }
    }
}

function Get-ArchiveRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ExtractDir
    )

    $cliName = Get-ExecutableFileName "aspire"
    $rootCli = Join-Path $ExtractDir $cliName
    if (Test-Path $rootCli) {
        return $ExtractDir
    }

    $candidateDirectories = @(Get-ChildItem -Path $ExtractDir -Directory -Force -ErrorAction SilentlyContinue)
    if ($candidateDirectories.Count -eq 1) {
        $candidateRoot = $candidateDirectories[0].FullName
        if (Test-Path (Join-Path $candidateRoot $cliName)) {
            return $candidateRoot
        }
    }

    throw "Could not find '$cliName' in extracted archive '$ExtractDir'."
}

function Test-ArchiveSidecar {
    # Per-RID CLI archives must ship sidecar-free; each install route writes
    # its own .aspire-install.json. See docs/specs/install-routes.md.
    param(
        [Parameter(Mandatory = $true)][string]$ExtractDir,
        [Parameter(Mandatory = $true)][string]$ArchiveFileName
    )

    $ridFamily = Get-ArchiveRidFamily $ArchiveFileName

    if ($null -eq $ridFamily) {
        throw "Archive RID family not recognized in filename '$ArchiveFileName'. Expected the filename to contain 'win-', 'osx-', or 'linux-'."
    }

    $strays = Get-ChildItem -Path $ExtractDir -Recurse -File -Filter '.aspire-install.json' -Force -ErrorAction SilentlyContinue
    if ($strays) {
        $strayPaths = $strays | ForEach-Object { $_.FullName.Substring($ExtractDir.Length + 1).Replace('\', '/') }
        throw "$ridFamily-* archive '$ArchiveFileName' must not contain '.aspire-install.json' (per-RID archives are shared across install routes; each route authors its own sidecar after extraction). Found: $($strayPaths -join ', ')"
    }
    Write-Step "$ridFamily-* archive correctly omits the install-route sidecar."
}

function Test-CSharpStarterProject {
    param(
        [Parameter(Mandatory = $true)][string]$AspireBin,
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    Write-Step "Running 'aspire new aspire-starter --name VerifyApp --output $ProjectRoot --non-interactive --nologo'..."
    & $AspireBin new aspire-starter --name VerifyApp --output $ProjectRoot --non-interactive --nologo 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'aspire new aspire-starter' failed with exit code $LASTEXITCODE"
        exit 1
    }

    $appHostDir = Join-Path $ProjectRoot "VerifyApp.AppHost"
    if (-not (Test-Path $appHostDir)) {
        Write-Err "Expected project directory 'VerifyApp.AppHost' not found after 'aspire new aspire-starter'"
        Get-ChildItem $ProjectRoot | Format-Table
        exit 1
    }

    Write-Ok "'aspire new aspire-starter' created project successfully"
}

$userHome = Get-UserHome
$verifyTmpDir = $null
$aspireBackup = $null

function Invoke-Cleanup {
    if ($verifyTmpDir -and (Test-Path $verifyTmpDir)) {
        Write-Step "Cleaning up temp directory: $verifyTmpDir"
        Remove-Item -Recurse -Force $verifyTmpDir -ErrorAction SilentlyContinue
    }
    # Restore ~/.aspire if we backed it up
    $aspireDir = Join-Path $userHome ".aspire"
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

    # Step 1: Back up and clean ~/.aspire
    Write-Step "Cleaning ~/.aspire state..."
    $aspireDir = Join-Path $userHome ".aspire"
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

    $archiveRoot = Get-ArchiveRoot -ExtractDir $extractDir
    $aspireBin = Join-Path $archiveRoot (Get-ExecutableFileName "aspire")
    Write-Ok "Extracted CLI binary: $aspireBin"

    # Assert the source sidecar matches the archive's RID family before mutating the
    # extracted shape. After Copy-Item moves the binary out, the archive layout is
    # no longer observable. Scan the full extraction directory, not $archiveRoot:
    # the sidecar contract is about the archive itself, and Get-ArchiveRoot may
    # return a single sub-directory (when the archive nests its binary), which
    # would let a stray sidecar at the true archive root slip past.
    Test-ArchiveSidecar -ExtractDir $extractDir -ArchiveFileName ([System.IO.Path]::GetFileName($ArchivePath))

    # Install to ~/.aspire/bin so self-extraction works correctly
    Write-Step "Installing CLI to ~/.aspire/bin..."
    $aspireDir = Join-Path $userHome ".aspire"
    $aspireBinDir = Join-Path $aspireDir "bin"
    New-Item -ItemType Directory -Path $aspireBinDir -Force | Out-Null
    Copy-Item $aspireBin (Join-Path $aspireBinDir (Split-Path $aspireBin -Leaf))
    $aspireBin = Join-Path $aspireBinDir (Split-Path $aspireBin -Leaf)
    Set-ExecutablePermission $aspireBin
    $env:PATH = "$aspireBinDir$([System.IO.Path]::PathSeparator)$env:PATH"
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

    # Step 4: Create starter project with aspire new. The C# starter exercises
    # template engine + bundle self-extraction without requiring a NuGet restore.
    # The TypeScript starter check (#17274) is intentionally not invoked here:
    # its packager-managed AppHost bootstrap triggers a NuGet restore whose
    # transitive deps resolve to api.nuget.org, which the 1ES signed-build agent
    # has no egress to. Re-adding it requires either an internal NuGet mirror
    # config or skipping the auto-restore. Tracked in #17345.
    $csharpProjectDir = Join-Path $verifyTmpDir "VerifyApp"
    New-Item -ItemType Directory -Path $csharpProjectDir -Force | Out-Null
    Test-CSharpStarterProject -AspireBin $aspireBin -ProjectRoot $csharpProjectDir

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
