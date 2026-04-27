<#
.SYNOPSIS
    Verify that CLI dotnet-tool nupkgs are valid.

.DESCRIPTION
    This script:
    1. Finds the RID-specific tool nupkg (Aspire.Cli.<rid>.*.nupkg)
    2. Validates the nupkg size is above a minimum threshold
    3. Extracts the package and verifies the aspire binary is present
    3a. Validates the bundled binary is large enough to contain the embedded bundle
    4. Validates the nupkg contains only the expected NativeAOT artifacts
       (binary + DotnetToolSettings.xml, no managed DLLs/deps.json)
    5. Verifies the binary OS format and CPU architecture match the target RID
       (PE Machine type on Windows, ELF e_machine on Linux, Mach-O cputype on macOS)
    6. Checks the primary pointer package (Aspire.Cli.*.nupkg) exists and validates
       its contents (no binary, small size, DotnetToolSettings.xml references all RIDs)

.PARAMETER PackagesDir
    Path to the directory containing nupkg files

.PARAMETER Rid
    Runtime identifier (e.g., win-x64, win-arm64)

.PARAMETER VerifySignature
    When set, verifies that both the RID-specific nupkg and the pointer package
    contain a .signature.p7s entry (NuGet package signature smoke test).

.EXAMPLE
    .\verify-cli-tool-nupkg.ps1 -PackagesDir "artifacts\packages\Release" -Rid "win-x64"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$PackagesDir,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [switch]$VerifySignature
)

$ErrorActionPreference = 'Stop'

function Write-Step  { param([string]$msg) Write-Host "`u{25B6} $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "`u{2705} $msg" -ForegroundColor Green }
function Write-Err   { param([string]$msg) Write-Host "`u{274C} $msg" -ForegroundColor Red }

function Test-NupkgSignature {
    param([string]$NupkgPath)
    # A NuGet-signed nupkg contains a .signature.p7s entry inside the zip archive.
    try {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
        try {
            $hasSig = $zip.Entries | Where-Object { $_.FullName -eq '.signature.p7s' }
            return [bool]$hasSig
        } finally {
            $zip.Dispose()
        }
    } catch {
        Write-Err "Failed to open nupkg as zip: $_"
        return $false
    }
}

# Minimum expected nupkg size in bytes (5 MB — NativeAOT binary should be large)
$MinNupkgSize = 5MB

$extractDir = $null

Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  Aspire CLI Tool Nupkg Verification"
    Write-Host "=========================================="
    Write-Host "  Packages dir:     $PackagesDir"
    Write-Host "  RID:              $Rid"
    Write-Host "  Verify signature: $VerifySignature"
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

    # Step 3a.1: Verify the binary is large enough to contain the embedded bundle.
    # The flat binary is ~35 MB; the bundled binary embeds a ~40 MB tar.gz, so it should be ~120 MB.
    # A 60 MB floor catches the catastrophic failure mode where BundlePayloadPath silently fails
    # and the "bundled" binary ships without the embedded bundle.
    $MinBundledBinarySizeMB = 60
    $binarySizeMB = [math]::Round($toolBinary.Length / 1MB, 1)
    Write-Step "Checking bundled binary size: $binarySizeMB MB (minimum $MinBundledBinarySizeMB MB)"
    if ($binarySizeMB -lt $MinBundledBinarySizeMB) {
        Write-Err "Binary is $binarySizeMB MB — expected >$MinBundledBinarySizeMB MB for bundled binary. Bundle may not be embedded."
        exit 1
    }
    Write-Ok "Bundled binary size check passed ($binarySizeMB MB)"

    # Step 3b: Verify the nupkg contains only expected NativeAOT artifacts.
    # A correctly-built NativeAOT tool nupkg has exactly 2 files under tools/:
    # the native binary and DotnetToolSettings.xml. A managed fallback may produce
    # additional DLLs, deps.json, or runtimeconfig.json files.
    $toolsDir = Join-Path $extractDir "tools"
    if (Test-Path $toolsDir) {
        $toolFiles = Get-ChildItem -Path $toolsDir -Recurse -File
        $toolFileNames = $toolFiles | ForEach-Object { $_.Name }
        Write-Step "Files under tools/: $($toolFileNames -join ', ')"

        # Check for managed-publish artifacts that should never appear in a NativeAOT package
        $managedArtifacts = $toolFiles | Where-Object {
            $_.Name -like '*.deps.json' -or
            $_.Name -like '*.runtimeconfig.json' -or
            ($_.Name -like '*.dll' -and $_.Name -ne 'DotnetToolSettings.xml')
        }
        if ($managedArtifacts) {
            Write-Err "Nupkg contains managed-publish artifacts (expected NativeAOT single binary):"
            foreach ($f in $managedArtifacts) { Write-Host "  $($f.FullName.Substring($extractDir.Length))" }
            exit 1
        }
        Write-Ok "No managed-publish artifacts found (deps.json, runtimeconfig.json, DLLs)"

        # Expect exactly 2 files: binary + DotnetToolSettings.xml
        if ($toolFiles.Count -ne 2) {
            Write-Err "Expected exactly 2 files under tools/ (binary + DotnetToolSettings.xml), found $($toolFiles.Count):"
            foreach ($f in $toolFiles) { Write-Host "  $($f.FullName.Substring($extractDir.Length))" }
            exit 1
        }
        Write-Ok "Tool directory contains exactly 2 files (binary + DotnetToolSettings.xml)"
    }

    # Step 4: Verify binary OS and architecture match the target RID
    $expectedArch = $Rid.Split('-')[-1]  # x64 or arm64 (works for linux-musl-x64 too)

    if ($Rid -like 'win-*') {
        # Read PE Machine type from header via BitConverter
        Write-Step "Checking PE binary architecture (expected: $expectedArch)..."
        $bytes = [System.IO.File]::ReadAllBytes($toolBinary.FullName)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)

        $expectedMachine = switch ($expectedArch) {
            'x64'   { 0x8664 }  # IMAGE_FILE_MACHINE_AMD64
            'arm64' { 0xAA64 }  # IMAGE_FILE_MACHINE_ARM64
            default { Write-Err "Unknown Windows architecture: $expectedArch"; exit 1 }
        }

        if ($machine -ne $expectedMachine) {
            Write-Err "PE Machine mismatch. Expected: 0x$($expectedMachine.ToString('X4')) ($expectedArch), Got: 0x$($machine.ToString('X4'))"
            exit 1
        }
        Write-Ok "PE binary architecture: 0x$($machine.ToString('X4')) ($expectedArch)"
    }
    else {
        # On Linux and macOS, use `file` to verify binary format and architecture
        Write-Step "Checking binary format with 'file' (expected arch: $expectedArch)..."
        if (-not (Get-Command file -ErrorAction SilentlyContinue)) {
            Write-Err "'file' command not found. Cannot verify binary architecture for $Rid."
            exit 1
        }

        $fileOutput = & file -b $toolBinary.FullName 2>&1
        Write-Step "file output: $fileOutput"

        $expectedPattern = switch ($Rid) {
            'linux-x64'       { 'ELF 64-bit.*x86-64' }
            'linux-arm64'     { 'ELF 64-bit.*ARM aarch64' }
            'linux-musl-x64'  { 'ELF 64-bit.*x86-64' }
            'osx-x64'         { 'Mach-O 64-bit.*x86_64' }
            'osx-arm64'       { 'Mach-O 64-bit.*arm64' }
            default           { $null }
        }

        if (-not $expectedPattern) {
            Write-Step "No known file signature pattern for RID '$Rid', skipping architecture check"
        }
        elseif ($fileOutput -notmatch $expectedPattern) {
            Write-Err "Binary format mismatch for $Rid. Expected pattern: '$expectedPattern', Got: '$fileOutput'"
            exit 1
        }
        else {
            Write-Ok "Binary format and architecture verified for $Rid"
        }
    }

    # Step 4b: Verify RID-specific nupkg is signed (smoke test)
    if ($VerifySignature) {
        Write-Step "Checking RID-specific nupkg signature..."
        if (-not (Test-NupkgSignature $ridNupkg.FullName)) {
            Write-Err "RID-specific nupkg $($ridNupkg.Name) is NOT signed (no .signature.p7s)"
            exit 1
        }
        Write-Ok "RID-specific nupkg is signed"
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

    # Step 5b: Deep-validate the pointer package contents.
    # The pointer package should be small (metadata-only), contain no binary,
    # and its DotnetToolSettings.xml should reference all 7 supported RIDs.
    Write-Step "Validating pointer package contents..."
    $maxPointerSize = 1MB
    if ($primaryNupkg.Length -gt $maxPointerSize) {
        Write-Err "Pointer package is unexpectedly large ($([math]::Round($primaryNupkg.Length / 1MB, 1)) MB). It should be metadata-only (< 1 MB)."
        exit 1
    }
    Write-Ok "Pointer package size OK ($([math]::Round($primaryNupkg.Length / 1KB, 0)) KB)"

    $pointerExtractDir = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-pointer-verify-$([System.IO.Path]::GetRandomFileName())"
    New-Item -ItemType Directory -Path $pointerExtractDir -Force | Out-Null
    try {
        $pointerZip = Join-Path $pointerExtractDir "$($primaryNupkg.BaseName).zip"
        Copy-Item $primaryNupkg.FullName $pointerZip
        Expand-Archive -Path $pointerZip -DestinationPath $pointerExtractDir -Force

        # Verify no binary is inside the pointer package
        $pointerBinaries = Get-ChildItem -Path $pointerExtractDir -Recurse -File |
            Where-Object { $_.Name -eq 'aspire' -or $_.Name -eq 'aspire.exe' }
        if ($pointerBinaries) {
            Write-Err "Pointer package contains a binary (it should be metadata-only):"
            foreach ($f in $pointerBinaries) { Write-Host "  $($f.FullName.Substring($pointerExtractDir.Length))" }
            exit 1
        }
        Write-Ok "Pointer package contains no binary"

        # Verify DotnetToolSettings.xml lists all expected RID packages.
        # The pointer package maps RIDs to RID-specific packages via
        # <RuntimeIdentifierPackages> in DotnetToolSettings.xml (not the nuspec).
        $toolSettingsFile = Get-ChildItem -Path $pointerExtractDir -Filter "DotnetToolSettings.xml" -Recurse | Select-Object -First 1
        if ($toolSettingsFile) {
            $toolSettingsContent = Get-Content $toolSettingsFile.FullName -Raw
            Write-Step "Checking DotnetToolSettings.xml for RID package references..."

            # The current RID must always be referenced
            if ($toolSettingsContent -notmatch [regex]::Escape("Aspire.Cli.$Rid")) {
                Write-Err "DotnetToolSettings.xml is missing reference to current RID package: Aspire.Cli.$Rid"
                exit 1
            }
            Write-Ok "DotnetToolSettings.xml references current RID: $Rid"

            # Count total RuntimeIdentifierPackage entries
            $ridEntries = [regex]::Matches($toolSettingsContent, 'RuntimeIdentifier="([^"]+)"')
            $referencedRids = $ridEntries | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
            Write-Step "DotnetToolSettings.xml lists $($referencedRids.Count) RIDs: $($referencedRids -join ', ')"

            # Expect the well-known set of 7 RIDs
            $expectedRidSet = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'osx-x64', 'osx-arm64') | Sort-Object
            $missingRids = $expectedRidSet | Where-Object { $referencedRids -notcontains $_ }
            if ($missingRids.Count -gt 0) {
                Write-Err "DotnetToolSettings.xml is missing these expected RIDs: $($missingRids -join ', ')"
                exit 1
            }
            Write-Ok "DotnetToolSettings.xml references all $($expectedRidSet.Count) expected RIDs"
        } else {
            Write-Err "No DotnetToolSettings.xml found inside pointer package"
            exit 1
        }

        # Verify the nuspec version matches the RID-specific nupkg version
        $nuspecFile = Get-ChildItem -Path $pointerExtractDir -Filter "*.nuspec" -Recurse | Select-Object -First 1
        if ($nuspecFile) {
            $nuspecContent = Get-Content $nuspecFile.FullName -Raw
            $expectedVersion = $ridNupkg.Name -replace "^Aspire\.Cli\.$([regex]::Escape($Rid))\.", '' -replace '\.nupkg$', ''
            if ($nuspecContent -match 'version>([^<]+)<') {
                $pointerVersion = $Matches[1]
                if ($pointerVersion -ne $expectedVersion) {
                    Write-Err "Pointer package version ($pointerVersion) does not match RID package version ($expectedVersion)"
                    exit 1
                }
                Write-Ok "Pointer package version matches RID package: $expectedVersion"
            }
        }
    } finally {
        if (Test-Path $pointerExtractDir) {
            Remove-Item -Recurse -Force $pointerExtractDir -ErrorAction SilentlyContinue
        }
    }

    # Step 5b: Verify pointer package is signed (smoke test)
    if ($VerifySignature) {
        Write-Step "Checking pointer package signature..."
        if (-not (Test-NupkgSignature $primaryNupkg.FullName)) {
            Write-Err "Pointer package $($primaryNupkg.Name) is NOT signed (no .signature.p7s)"
            exit 1
        }
        Write-Ok "Pointer package is signed"
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
}

# Explicitly exit 0 so $LASTEXITCODE is set for callers.
# The catch block already calls exit 1 on any failure path.
exit 0
