param(
  [Parameter(Mandatory = $true)]
  [string]$Rid,

  [Parameter(Mandatory = $true)]
  [string]$Version,

  [Parameter(Mandatory = $true)]
  [string]$NativeBinaryPath,

  [Parameter(Mandatory = $true)]
  [string]$OutputPath,

  [Parameter(Mandatory = $true)]
  [string]$StagingRoot,

  [string]$PackageName = '@microsoft/aspire-cli'
)

$ErrorActionPreference = 'Stop'

function New-StringList([string[]]$Values) {
  # PowerShell enumerates single-item arrays when returning from functions.
  # Returning a List keeps os/cpu/libc/files as JSON arrays for npm metadata.
  $list = [System.Collections.Generic.List[string]]::new()
  foreach ($value in $Values) {
    $list.Add($value)
  }
  return ,$list
}

function Get-RidPackageInfo([string]$PackageRid) {
  # Keep npm platform metadata aligned with the RIDs produced by the native
  # archive build. npm uses os/cpu/libc to install only the matching package.
  switch ($PackageRid) {
    'win-x64' {
      return [ordered]@{
        BinaryName = 'aspire.exe'
        Os = New-StringList 'win32'
        Cpu = New-StringList 'x64'
      }
    }
    'win-arm64' {
      return [ordered]@{
        BinaryName = 'aspire.exe'
        Os = New-StringList 'win32'
        Cpu = New-StringList 'arm64'
      }
    }
    'linux-x64' {
      return [ordered]@{
        BinaryName = 'aspire'
        Os = New-StringList 'linux'
        Cpu = New-StringList 'x64'
        Libc = New-StringList 'glibc'
      }
    }
    'linux-arm64' {
      return [ordered]@{
        BinaryName = 'aspire'
        Os = New-StringList 'linux'
        Cpu = New-StringList 'arm64'
        Libc = New-StringList 'glibc'
      }
    }
    'linux-musl-x64' {
      return [ordered]@{
        BinaryName = 'aspire'
        Os = New-StringList 'linux'
        Cpu = New-StringList 'x64'
        Libc = New-StringList 'musl'
      }
    }
    'osx-x64' {
      return [ordered]@{
        BinaryName = 'aspire'
        Os = New-StringList 'darwin'
        Cpu = New-StringList 'x64'
      }
    }
    'osx-arm64' {
      return [ordered]@{
        BinaryName = 'aspire'
        Os = New-StringList 'darwin'
        Cpu = New-StringList 'arm64'
      }
    }
    default {
      throw "Unsupported Aspire CLI RID for npm packaging: $PackageRid"
    }
  }
}

function Write-JsonFile([string]$Path, [object]$Value) {
  $json = $Value | ConvertTo-Json -Depth 20
  $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
  [System.IO.File]::WriteAllText($Path, "$json`n", $utf8NoBom)
}

function Write-TextFile([string]$Path, [string]$Value) {
  $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
  [System.IO.File]::WriteAllText($Path, $Value, $utf8NoBom)
}

function Invoke-NpmPack([string]$PackageDirectory, [string]$DestinationDirectory) {
  Write-Host "Packing npm package from $PackageDirectory"
  & npm pack $PackageDirectory --pack-destination $DestinationDirectory
  if ($LASTEXITCODE -ne 0) {
    throw "npm pack failed for $PackageDirectory with exit code $LASTEXITCODE."
  }
}

# MSBuild extracts this from the signed native CLI archive. This script only
# repackages that payload; it must not rebuild or substitute another binary.
if (-not (Test-Path -LiteralPath $NativeBinaryPath)) {
  throw "Native binary path does not exist: $NativeBinaryPath"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$launcherPath = Join-Path $repoRoot 'eng\clipack\npm\aspire.js'
if (-not (Test-Path -LiteralPath $launcherPath)) {
  throw "Npm launcher path does not exist: $launcherPath"
}

$ridInfo = Get-RidPackageInfo $Rid
$ridPackageName = "$PackageName-$Rid"
$supportedRids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'osx-x64', 'osx-arm64')

Remove-Item -LiteralPath $StagingRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $StagingRoot -Force | Out-Null
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

$ridPackageRoot = Join-Path $StagingRoot 'rid'
$ridPackageBin = Join-Path $ridPackageRoot 'bin'
New-Item -ItemType Directory -Path $ridPackageBin -Force | Out-Null
Copy-Item -LiteralPath $NativeBinaryPath -Destination (Join-Path $ridPackageBin $ridInfo.BinaryName) -Force

$ridPackageJson = [ordered]@{
  name = $ridPackageName
  version = $Version
  description = "Native Aspire CLI binary for $Rid."
  license = 'MIT'
  repository = [ordered]@{
    type = 'git'
    url = 'git+https://github.com/microsoft/aspire.git'
  }
  bugs = [ordered]@{
    url = 'https://github.com/microsoft/aspire/issues'
  }
  os = $ridInfo.Os
  cpu = $ridInfo.Cpu
  files = New-StringList @('bin', 'README.md')
}

if ($ridInfo.Contains('Libc')) {
  $ridPackageJson.libc = $ridInfo.Libc
}

Write-JsonFile (Join-Path $ridPackageRoot 'package.json') $ridPackageJson
# Use a non-expanding here-string so the markdown backticks (`) survive verbatim.
# In a normal (double-quoted) here-string ` is the PowerShell escape character, which
# both swallows the backticks and suppresses $-interpolation; using @'...'@ and a
# manual -replace lets us emit literal `<value>` code spans for $Rid / $PackageName.
$ridReadmeTemplate = @'
# __RID_PACKAGE_NAME__

Native Aspire CLI binary for `__RID__`.

This package is installed as an optional dependency of `__PACKAGE_NAME__`.
'@

$ridReadme = $ridReadmeTemplate `
  -replace '__RID_PACKAGE_NAME__', $ridPackageName `
  -replace '__RID__', $Rid `
  -replace '__PACKAGE_NAME__', $PackageName

Write-TextFile (Join-Path $ridPackageRoot 'README.md') $ridReadme

$pointerPackageRoot = Join-Path $StagingRoot 'pointer'
$pointerPackageBin = Join-Path $pointerPackageRoot 'bin'
New-Item -ItemType Directory -Path $pointerPackageBin -Force | Out-Null
Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $pointerPackageBin 'aspire.js') -Force

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
  chmod +x (Join-Path $pointerPackageBin 'aspire.js')
}

$optionalDependencies = [ordered]@{}
foreach ($supportedRid in $supportedRids) {
  $optionalDependencies["$PackageName-$supportedRid"] = $Version
}

# The launcher reads this map instead of hardcoding package names so package
# scope/name changes remain an MSBuild property.
$ridPackageMap = [ordered]@{}
foreach ($supportedRid in $supportedRids) {
  $ridPackageMap[$supportedRid] = "$PackageName-$supportedRid"
}

$pointerPackageJson = [ordered]@{
  name = $PackageName
  version = $Version
  description = 'Command line tool for Aspire developers.'
  license = 'MIT'
  repository = [ordered]@{
    type = 'git'
    url = 'git+https://github.com/microsoft/aspire.git'
  }
  bugs = [ordered]@{
    url = 'https://github.com/microsoft/aspire/issues'
  }
  bin = [ordered]@{
    aspire = 'bin/aspire.js'
  }
  # Minimum Node 20: the launcher (`bin/aspire.js`) uses Error options-bag
  # `new Error(msg, { cause: err })` which was added in Node 16.9.0. The
  # `libc` selector in the per-RID optionalDependencies relies on
  # npm >= 10.7 (ships with Node 20.10+). Node 18 reaches end-of-life
  # 2025-04-30, so Node 20 is the lowest LTS we should support at GA.
  # See: https://nodejs.org/en/about/previous-releases
  engines = [ordered]@{
    node = '>=20'
  }
  optionalDependencies = $optionalDependencies
  files = New-StringList @('bin', 'README.md')
}

Write-JsonFile (Join-Path $pointerPackageRoot 'package.json') $pointerPackageJson
Write-JsonFile (Join-Path $pointerPackageBin 'aspire-package-map.json') $ridPackageMap
Write-TextFile (Join-Path $pointerPackageRoot 'README.md') @"
# $PackageName

Npm package for the Aspire CLI.

This package installs a small JavaScript launcher and resolves the matching native Aspire CLI package for the current platform.
"@

Invoke-NpmPack $ridPackageRoot $OutputPath
Invoke-NpmPack $pointerPackageRoot $OutputPath
