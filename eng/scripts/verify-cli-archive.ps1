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
    6. Runs 'aspire new aspire-ts-starter' to test TypeScript starter restore/codegen against the shipped layout
    7. Verifies the TypeScript starter path extracted the embedded bundle layout
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

function Test-ExtractedBundleLayout {
    param(
        [Parameter(Mandatory = $true)][string]$LayoutRoot
    )

    $bundleRoot = Join-Path $LayoutRoot "bundle"
    $managedExecutablePath = Join-Path $bundleRoot (Join-Path "managed" (Get-ExecutableFileName "aspire-managed"))
    if (-not (Test-Path $managedExecutablePath)) {
        throw "Expected extracted bundle-managed server binary at '$managedExecutablePath', but it was not found."
    }

    $wwwRootPath = Join-Path $bundleRoot (Join-Path "managed" "wwwroot")
    if (-not (Test-Path $wwwRootPath)) {
        throw "Expected dashboard web assets at '$wwwRootPath', but they were not found."
    }

    $wwwRootFileCount = @(Get-ChildItem -Path $wwwRootPath -Recurse -File -ErrorAction SilentlyContinue).Count
    if ($wwwRootFileCount -eq 0) {
        throw "Dashboard asset directory '$wwwRootPath' is empty."
    }

    $dcpExecutablePath = Join-Path $bundleRoot (Join-Path "dcp" (Get-ExecutableFileName "dcp"))
    if (-not (Test-Path $dcpExecutablePath)) {
        throw "Expected DCP binary at '$dcpExecutablePath', but it was not found."
    }

    Write-Step "Extracted bundle layout contains AppHost server assets."

    # aspire-managed's top-level entry point is a hard dispatch on
    # dashboard|server|nuget and returns 1 for anything else (including --help).
    # Use a real subcommand whose System.CommandLine help path returns 0; this
    # also exercises the NativeAOT-compiled CommandLine code path as part of
    # the smoke check. See src/Aspire.Managed/Program.cs.
    Write-Step "Running '$managedExecutablePath nuget --help'..."
    $managedOutput = & $managedExecutablePath nuget --help 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "aspire-managed failed with exit code $LASTEXITCODE. Output: $managedOutput"
    }

    Write-Ok "Extracted bundled AppHost server is executable"
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

function Test-TypeScriptStarterProject {
    param(
        [Parameter(Mandatory = $true)][string]$AspireBin,
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    Write-Step "Running 'aspire new aspire-ts-starter --name VerifyTsApp --output $ProjectRoot --non-interactive --nologo'..."
    & $AspireBin new aspire-ts-starter --name VerifyTsApp --output $ProjectRoot --non-interactive --nologo 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Err "'aspire new aspire-ts-starter' failed with exit code $LASTEXITCODE"
        exit 1
    }

    $expectedPaths = @(
        "apphost.ts",
        "aspire.config.json",
        ".modules/aspire.ts",
        "package.json",
        "frontend/package.json",
        "frontend/src/main.tsx",
        "api/package.json",
        "api/src/index.ts"
    )

    foreach ($relativePath in $expectedPaths) {
        $fullPath = Join-Path $ProjectRoot $relativePath
        if (-not (Test-Path $fullPath)) {
            throw "Expected TypeScript starter asset '$relativePath' not found under '$ProjectRoot'."
        }
    }

    $configContent = Get-Content -Path (Join-Path $ProjectRoot "aspire.config.json") -Raw
    if ($configContent -notmatch '"sdk"') {
        throw "TypeScript starter config '$ProjectRoot/aspire.config.json' did not contain the expected 'sdk' section."
    }

    Write-Ok "'aspire new aspire-ts-starter' produced AppHost restore/codegen artifacts successfully"
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
    # no longer observable.
    Test-ArchiveSidecar -ExtractDir $archiveRoot -ArchiveFileName ([System.IO.Path]::GetFileName($ArchivePath))

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

    # Step 4: Create starter projects with aspire new. The C# starter covers the
    # existing happy path; the TypeScript starter proves the shipped archive can
    # supply the bundled AppHost server and generate the .modules SDK layout.
    $csharpProjectDir = Join-Path $verifyTmpDir "VerifyApp"
    New-Item -ItemType Directory -Path $csharpProjectDir -Force | Out-Null
    Test-CSharpStarterProject -AspireBin $aspireBin -ProjectRoot $csharpProjectDir

    $typeScriptProjectDir = Join-Path $verifyTmpDir "VerifyTsApp"
    New-Item -ItemType Directory -Path $typeScriptProjectDir -Force | Out-Null
    Test-TypeScriptStarterProject -AspireBin $aspireBin -ProjectRoot $typeScriptProjectDir
    Test-ExtractedBundleLayout -LayoutRoot $aspireDir

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
