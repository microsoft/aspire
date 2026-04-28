param(
    [Parameter(Mandatory = $true)]
    [string]$PackagesDir,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

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
