param(
    [Parameter(Mandatory = $true)]
    [string]$PackagesDir,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [string]$ArchivePath,

    [switch]$VerifySignature
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host "[verify-cli-tool-nupkg] $Message"
}

function Test-NupkgSignature([string]$NupkgPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
    try {
        return [bool]($zip.Entries | Where-Object { $_.FullName -eq '.signature.p7s' } | Select-Object -First 1)
    }
    finally {
        $zip.Dispose()
    }
}

function Expand-Nupkg([System.IO.FileInfo]$Package, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($Package.FullName, $Destination)
}

function Expand-CliArchive([string]$Archive, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    if ($Archive.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination)
        return
    }

    if ($Archive.EndsWith('.tar.gz', [System.StringComparison]::OrdinalIgnoreCase)) {
        tar -xzf $Archive -C $Destination
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to extract CLI archive $Archive."
        }
        return
    }

    throw "Unsupported archive format: $Archive (expected .zip or .tar.gz)."
}

function Test-FileContentEqual([string]$ExpectedPath, [string]$ActualPath) {
    $expected = Get-Item -LiteralPath $ExpectedPath
    $actual = Get-Item -LiteralPath $ActualPath
    if ($expected.Length -ne $actual.Length) {
        return $false
    }

    $expectedStream = [System.IO.File]::OpenRead($expected.FullName)
    $actualStream = [System.IO.File]::OpenRead($actual.FullName)
    try {
        $expectedBuffer = [byte[]]::new(1024 * 1024)
        $actualBuffer = [byte[]]::new(1024 * 1024)

        while ($true) {
            $expectedRead = $expectedStream.Read($expectedBuffer, 0, $expectedBuffer.Length)
            $actualRead = $actualStream.Read($actualBuffer, 0, $actualBuffer.Length)

            if ($expectedRead -ne $actualRead) {
                return $false
            }

            if ($expectedRead -eq 0) {
                return $true
            }

            for ($i = 0; $i -lt $expectedRead; $i++) {
                if ($expectedBuffer[$i] -ne $actualBuffer[$i]) {
                    return $false
                }
            }
        }
    }
    finally {
        $expectedStream.Dispose()
        $actualStream.Dispose()
    }
}

function Assert-SinglePackage([object[]]$Packages, [string]$Description) {
    if (-not $Packages -or $Packages.Count -eq 0) {
        throw "Could not find $Description."
    }

    if ($Packages.Count -gt 1) {
        throw "Found multiple packages for ${Description}: $($Packages.Name -join ', ')"
    }

    return $Packages[0]
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$effectiveDir = if (Test-Path (Join-Path $PackagesDir 'Shipping')) {
    Join-Path $PackagesDir 'Shipping'
} else {
    $PackagesDir
}

Write-Step "PackagesDir: $effectiveDir"
Write-Step "RID: $Rid"

if ($ArchivePath) {
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        throw "ArchivePath does not exist: $ArchivePath"
    }

    $ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
    Write-Step "Archive: $ArchivePath"
}

$ridPackage = Assert-SinglePackage `
    (Get-ChildItem -Path $effectiveDir -Filter "Aspire.Cli.$Rid.*.nupkg" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '\.symbols\.' }) `
    "RID-specific Aspire.Cli package for $Rid"

$pointerPackages = Get-ChildItem -Path $effectiveDir -Filter 'Aspire.Cli.*.nupkg' -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -notmatch '\.symbols\.' -and
        $_.Name -notmatch '^Aspire\.Cli\.(win|linux|linux-musl|osx)-(x64|arm64)\.'
    }
$pointerPackage = Assert-SinglePackage $pointerPackages 'Aspire.Cli pointer package'

Write-Step "RID package: $($ridPackage.Name)"
Write-Step "Pointer package: $($pointerPackage.Name)"

if ($ridPackage.Length -lt 5MB) {
    throw "RID package $($ridPackage.Name) is too small ($($ridPackage.Length) bytes). The NativeAOT binary may be missing."
}

if ($pointerPackage.Length -gt 1MB) {
    throw "Pointer package $($pointerPackage.Name) is unexpectedly large ($($pointerPackage.Length) bytes). It should not contain a native binary."
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) "aspire-cli-tool-nupkg-$([System.IO.Path]::GetRandomFileName())"
$ridExtract = Join-Path $root 'rid'
$pointerExtract = Join-Path $root 'pointer'
$archiveExtract = Join-Path $root 'archive'

try {
    Expand-Nupkg $ridPackage $ridExtract
    Expand-Nupkg $pointerPackage $pointerExtract

    $binaryName = if ($Rid -like 'win-*') { 'aspire.exe' } else { 'aspire' }
    $toolBinary = Get-ChildItem -Path $ridExtract -Recurse -File -Filter $binaryName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $toolBinary) {
        throw "Could not find $binaryName in RID package $($ridPackage.Name)."
    }

    $expectedBinaryPath = "tools/net10.0/$Rid/$binaryName"
    $relativeBinaryPath = $toolBinary.FullName.Substring($ridExtract.Length + 1).Replace('\', '/')
    if ($relativeBinaryPath -ne $expectedBinaryPath) {
        throw "Expected binary at '$expectedBinaryPath', but found '$relativeBinaryPath'."
    }

    $expectedSidecarPath = "tools/net10.0/$Rid/.aspire-install.json"
    $sidecarFile = Join-Path $ridExtract ($expectedSidecarPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $sidecarFile)) {
        throw "RID package $($ridPackage.Name) is missing sidecar at '$expectedSidecarPath'."
    }
    Write-Step "Sidecar present in RID package at: $expectedSidecarPath"

    if ($ArchivePath) {
        Expand-CliArchive $ArchivePath $archiveExtract
        $archiveBinaryPath = Join-Path $archiveExtract $binaryName
        if (-not (Test-Path -LiteralPath $archiveBinaryPath)) {
            throw "Could not find $binaryName in CLI archive $ArchivePath."
        }

        if (-not (Test-FileContentEqual $archiveBinaryPath $toolBinary.FullName)) {
            $archiveBinary = Get-Item -LiteralPath $archiveBinaryPath
            throw "RID package binary '$relativeBinaryPath' does not match archive binary '$($archiveBinary.Name)'. Archive size: $($archiveBinary.Length) bytes; package size: $($toolBinary.Length) bytes."
        }

        Write-Step "RID package binary matches CLI archive binary."
    }

    # Assert .aspire-install.json is present in the RID-specific nupkg at the expected tool path
    $expectedSidecarPath = "tools/net10.0/$Rid/.aspire-install.json"
    $sidecarFile = Get-ChildItem -Path $ridExtract -Recurse -File -Filter '.aspire-install.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $sidecarFile) {
        throw "RID package $($ridPackage.Name) is missing .aspire-install.json at '$expectedSidecarPath'."
    }
    $relativeSidecarPath = $sidecarFile.FullName.Substring($ridExtract.Length + 1).Replace('\', '/')
    if ($relativeSidecarPath -ne $expectedSidecarPath) {
        throw "Expected .aspire-install.json at '$expectedSidecarPath', but found '$relativeSidecarPath'."
    }
    Write-Step "RID package contains .aspire-install.json at '$expectedSidecarPath'."

    # Assert .aspire-install.json is NOT present in the pointer (architecture-neutral) nupkg
    $pointerSidecar = Get-ChildItem -Path $pointerExtract -Recurse -File -Filter '.aspire-install.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pointerSidecar) {
        throw "Pointer package should not contain .aspire-install.json, but found: $($pointerSidecar.FullName)"
    }
    Write-Step "Pointer package correctly omits .aspire-install.json."

    $toolSettings = Get-ChildItem -Path $ridExtract -Recurse -File -Filter 'DotnetToolSettings.xml' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $toolSettings) {
        throw "RID package $($ridPackage.Name) is missing DotnetToolSettings.xml."
    }

    $managedArtifacts = Get-ChildItem -Path (Join-Path $ridExtract 'tools') -Recurse -File |
        Where-Object { $_.Name -like '*.deps.json' -or $_.Name -like '*.runtimeconfig.json' -or $_.Name -like '*.dll' }
    if ($managedArtifacts) {
        throw "RID package contains managed publish artifacts: $($managedArtifacts.Name -join ', ')"
    }

    $pointerBinary = Get-ChildItem -Path $pointerExtract -Recurse -File -Filter 'aspire*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'aspire' -or $_.Name -eq 'aspire.exe' }
    if ($pointerBinary) {
        throw "Pointer package should not contain native binaries: $($pointerBinary.Name -join ', ')"
    }

    $pointerSidecar = Get-ChildItem -Path $pointerExtract -Recurse -File -Filter '.aspire-install.json' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pointerSidecar) {
        throw "Pointer package must not contain '.aspire-install.json': found at '$($pointerSidecar.FullName.Substring($pointerExtract.Length + 1))'."
    }
    Write-Step "Sidecar absent from pointer package (correct)."

    $pointerNuspec = Get-ChildItem -Path $pointerExtract -Filter '*.nuspec' -File | Select-Object -First 1
    if (-not $pointerNuspec) {
        throw "Pointer package $($pointerPackage.Name) is missing a nuspec."
    }

    $pointerToolSettings = Get-ChildItem -Path $pointerExtract -Recurse -File -Filter 'DotnetToolSettings.xml' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pointerToolSettings) {
        throw "Pointer package $($pointerPackage.Name) is missing DotnetToolSettings.xml."
    }

    $pointerToolSettingsXml = [xml](Get-Content -Path $pointerToolSettings.FullName -Raw)
    $runtimeIdentifierPackage = @($pointerToolSettingsXml.DotNetCliTool.RuntimeIdentifierPackages.RuntimeIdentifierPackage) |
        Where-Object { $_.RuntimeIdentifier -eq $Rid -and $_.Id -eq "Aspire.Cli.$Rid" } |
        Select-Object -First 1
    if (-not $runtimeIdentifierPackage) {
        throw "Pointer package DotnetToolSettings.xml does not reference Aspire.Cli.$Rid for $Rid."
    }

    if ($VerifySignature) {
        if (-not (Test-NupkgSignature $ridPackage.FullName)) {
            throw "RID package $($ridPackage.Name) is not signed."
        }

        if (-not (Test-NupkgSignature $pointerPackage.FullName)) {
            throw "Pointer package $($pointerPackage.Name) is not signed."
        }
    }

    Write-Step "CLI tool nupkg verification passed."
}
finally {
    if (Test-Path $root) {
        Remove-Item -Path $root -Recurse -Force
    }
}
