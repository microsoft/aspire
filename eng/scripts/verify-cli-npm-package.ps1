param(
  [Parameter(Mandatory = $true)]
  [string]$PackagesDir,

  [Parameter(Mandatory = $true)]
  [string]$Rid,

  [string]$ArchivePath,

  [string]$PackageName = '@microsoft/aspire-cli'
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
  Write-Host "[verify-cli-npm-package] $Message"
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

function Expand-Tgz([string]$PackagePath, [string]$Destination) {
  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  tar -xzf $PackagePath -C $Destination
  if ($LASTEXITCODE -ne 0) {
    throw "Failed to extract $PackagePath."
  }
}

function Expand-CliArchive([string]$Archive, [string]$Destination) {
  New-Item -ItemType Directory -Path $Destination -Force | Out-Null

  if ($Archive.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
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

function Get-NpmPackageFilePrefix([string]$NpmPackageName) {
  return $NpmPackageName.TrimStart('@').Replace('/', '-')
}

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

# npm pack flattens scoped package names into tarball file names, e.g.
# @microsoft/aspire-cli -> microsoft-aspire-cli-1.0.0.tgz.
$packageFilePrefix = Get-NpmPackageFilePrefix $PackageName
$ridPackage = Assert-SinglePackage `
  (Get-ChildItem -Path $effectiveDir -Filter "$packageFilePrefix-$Rid-*.tgz" -ErrorAction SilentlyContinue) `
  "RID-specific npm package for $Rid"
$pointerPackage = Assert-SinglePackage `
  (Get-ChildItem -Path $effectiveDir -Filter "$packageFilePrefix-*.tgz" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch "^$([System.Text.RegularExpressions.Regex]::Escape($packageFilePrefix))-(win|linux|linux-musl|osx)-(x64|arm64)-" }) `
  'Aspire CLI npm pointer package'

Write-Step "RID package: $($ridPackage.Name)"
Write-Step "Pointer package: $($pointerPackage.Name)"

$root = [System.IO.Directory]::CreateTempSubdirectory('aspire-cli-npm-package-').FullName
$ridExtract = Join-Path $root 'rid'
$pointerExtract = Join-Path $root 'pointer'
$archiveExtract = Join-Path $root 'archive'

try {
  Expand-Tgz $ridPackage.FullName $ridExtract
  Expand-Tgz $pointerPackage.FullName $pointerExtract

  $binaryName = if ($Rid -like 'win-*') { 'aspire.exe' } else { 'aspire' }
  $binaryPath = Join-Path $ridExtract "package/bin/$binaryName"
  if (-not (Test-Path -LiteralPath $binaryPath)) {
    throw "Could not find expected native binary at package/bin/$binaryName in $($ridPackage.Name)."
  }

  if ($ArchivePath) {
    Expand-CliArchive $ArchivePath $archiveExtract
    $archiveBinaryPath = Join-Path $archiveExtract $binaryName
    if (-not (Test-Path -LiteralPath $archiveBinaryPath)) {
      throw "Could not find $binaryName in CLI archive $ArchivePath."
    }

    # The signed native archive remains the canonical payload. The npm RID
    # tarball must contain the exact same executable bytes.
    if (-not (Test-FileContentEqual $archiveBinaryPath $binaryPath)) {
      $archiveBinary = Get-Item -LiteralPath $archiveBinaryPath
      $npmBinary = Get-Item -LiteralPath $binaryPath
      throw "RID npm package binary does not match archive binary '$($archiveBinary.Name)'. Archive size: $($archiveBinary.Length) bytes; npm package size: $($npmBinary.Length) bytes."
    }

    Write-Step "RID package binary matches CLI archive binary."
  }

  $pointerPackageJsonPath = Join-Path $pointerExtract 'package/package.json'
  if (-not (Test-Path -LiteralPath $pointerPackageJsonPath)) {
    throw "Pointer package $($pointerPackage.Name) is missing package.json."
  }

  $pointerPackageJson = Get-Content -Path $pointerPackageJsonPath -Raw | ConvertFrom-Json
  $expectedRidPackageName = "$PackageName-$Rid"
  if ($pointerPackageJson.name -ne $PackageName) {
    throw "Pointer package name mismatch. Expected '$PackageName', got '$($pointerPackageJson.name)'."
  }

  if (-not $pointerPackageJson.bin -or $pointerPackageJson.bin.aspire -ne 'bin/aspire.js') {
    throw "Pointer package must expose the aspire bin at bin/aspire.js."
  }

  if (-not (Test-Path -LiteralPath (Join-Path $pointerExtract 'package/bin/aspire.js'))) {
    throw "Pointer package is missing bin/aspire.js."
  }

  $packageMapPath = Join-Path $pointerExtract 'package/bin/aspire-package-map.json'
  if (-not (Test-Path -LiteralPath $packageMapPath)) {
    throw "Pointer package is missing bin/aspire-package-map.json."
  }

  # The launcher depends on this generated map to resolve the selected RID
  # package without hardcoding the package scope/name.
  $packageMap = Get-Content -Path $packageMapPath -Raw | ConvertFrom-Json
  if ($packageMap.$Rid -ne $expectedRidPackageName) {
    throw "Pointer package map does not reference $expectedRidPackageName for $Rid."
  }

  $optionalDependencyVersion = $pointerPackageJson.optionalDependencies.$expectedRidPackageName
  if ($optionalDependencyVersion -ne $pointerPackageJson.version) {
    throw "Pointer package optionalDependencies does not reference $expectedRidPackageName at version $($pointerPackageJson.version)."
  }

  $ridPackageJsonPath = Join-Path $ridExtract 'package/package.json'
  if (-not (Test-Path -LiteralPath $ridPackageJsonPath)) {
    throw "RID package $($ridPackage.Name) is missing package.json."
  }

  $ridPackageJson = Get-Content -Path $ridPackageJsonPath -Raw | ConvertFrom-Json
  if ($ridPackageJson.name -ne $expectedRidPackageName) {
    throw "RID package name mismatch. Expected '$expectedRidPackageName', got '$($ridPackageJson.name)'."
  }

  if ($ridPackageJson.version -ne $pointerPackageJson.version) {
    throw "RID package version '$($ridPackageJson.version)' does not match pointer package version '$($pointerPackageJson.version)'."
  }

  Write-Step "CLI npm package verification passed."
}
finally {
  if (Test-Path $root) {
    Remove-Item -Path $root -Recurse -Force
  }
}
