#!/usr/bin/env pwsh

<#!
.SYNOPSIS
  Build local NuGet packages, Aspire CLI, and bundle, then create/update a hive and install everything (Windows/PowerShell).

.DESCRIPTION
  Mirrors localhive.sh behavior on Windows. Packs the repo, creates a symlink from
  $HOME/.aspire/hives/<HiveName> to artifacts/packages/<Config>/Shipping (or copies .nupkg files),
  installs the locally-built Aspire CLI to $HOME/.aspire/bin, and builds/installs the bundle
  (aspire-managed + DCP) to $HOME/.aspire so the CLI can auto-discover it.

.PARAMETER Configuration
  Build configuration: Release or Debug (positional parameter 0). If omitted, the script tries Release then falls back to Debug.

.PARAMETER Name
  Hive name (positional parameter 1). Default: local.

.PARAMETER VersionSuffix
  Prerelease version suffix. If omitted, auto-generates: local.YYYYMMDD.tHHmmss (UTC)

.PARAMETER Copy
  Copy .nupkg files instead of linking the hive directory.

.PARAMETER SkipCli
  Skip installing the locally-built CLI to $HOME/.aspire/bin.

.PARAMETER SkipBundle
  Skip building and installing the bundle (aspire-managed + DCP) to $HOME/.aspire.

.PARAMETER NativeAot
  Build and install the native AOT CLI (self-extracting binary with embedded bundle) instead of the dotnet tool version.

.PARAMETER Help
  Show help and exit.

.EXAMPLE
  .\localhive.ps1 -Configuration Release -Name local

.EXAMPLE
  .\localhive.ps1 Debug my-feature

.EXAMPLE
  .\localhive.ps1 -SkipCli

.NOTES
  The hive is created at $HOME/.aspire/hives/<HiveName> so the Aspire CLI can discover a channel.
  The CLI is installed to $HOME/.aspire/bin so it can be used directly.
#>

[CmdletBinding(PositionalBinding=$true)]
param(
  [Alias('c')]
  [Parameter(Position=0)]
  [string] $Configuration,

  [Alias('n','hive','hiveName')]
  [Parameter(Position=1)]
  [string] $Name = 'local',

  [Alias('v')]
  [string] $VersionSuffix,

  [switch] $Copy,

  [switch] $SkipCli,

  [switch] $SkipBundle,

  [switch] $NativeAot,

  [Alias('h')]
  [switch] $Help
)

$ErrorActionPreference = 'Stop'

function Show-Usage {
  @'
Usage:
  .\localhive.ps1 [options]
  .\localhive.ps1 [Release|Debug] [HiveName]

Positional parameters:
  [Release|Debug]      Optional build configuration (Position 0). If omitted, attempts Release then Debug.
  [HiveName]           Optional hive name (Position 1). Defaults to 'local'.

Options:
  -Configuration (-c)   Build configuration: Release or Debug
  -Name (-n)            Hive name (default: local)
  -VersionSuffix (-v)   Prerelease version suffix (default: auto-generates local.YYYYMMDD.tHHmmss)
  -Copy                 Copy .nupkg files instead of creating a symlink
  -SkipCli              Skip installing the locally-built CLI to $HOME\.aspire\bin
  -SkipBundle           Skip building and installing the bundle (aspire-managed + DCP)
  -NativeAot            Build native AOT CLI (self-extracting with embedded bundle)
  -Help (-h)            Show this help and exit

Examples:
  .\localhive.ps1 -c Release -n local
  .\localhive.ps1 Debug my-feature
  .\localhive.ps1 -c Release -n demo -v local.20250811.t033324
  .\localhive.ps1            # Packs (tries Release then Debug) -> hive 'local'
  .\localhive.ps1 Debug      # Packs Debug -> hive 'local'
  .\localhive.ps1 Release demo

This will pack NuGet packages into artifacts\packages\<Config>\Shipping and create/update
a hive at $HOME\.aspire\hives\<HiveName> so the Aspire CLI can use it as a channel.
It also installs the locally-built CLI to $HOME\.aspire\bin (unless -SkipCli is specified).
'@ | Write-Host
}

function Write-Log   { param([string]$m) Write-Host "[localhive] $m" }
function Write-Warn  { param([string]$m) Write-Warning "[localhive] $m" }
function Write-Err   { param([string]$m) Write-Error "[localhive] $m" }

if ($Help) { Show-Usage; exit 0 }

# Normalize configuration casing if provided (case-insensitive) and allow common abbreviations.
if ($Configuration) {
  switch ($Configuration.ToLowerInvariant()) {
    'release' { $Configuration = 'Release' }
    'debug'   { $Configuration = 'Debug' }
    default   { Write-Err "Unsupported configuration '$Configuration'. Use Release or Debug."; exit 1 }
  }
}

# Compute repo root based on script location
$RepoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path

function Test-VersionSuffix {
  param([Parameter(Mandatory)][string]$Suffix)
  # Must be dot-separated identifiers containing only 0-9A-Za-z- per SemVer2.
  if ($Suffix -notmatch '^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*$') { return $false }
  $parts = $Suffix -split '\.'
  foreach ($p in $parts) {
    if ($p -match '^[0-9]+$' -and $p.Length -gt 1 -and $p.StartsWith('0')) { return $false }
  }
  return $true
}

# Auto-generate version suffix if not specified
if (-not $VersionSuffix) {
  $utc = [DateTime]::UtcNow
  $VersionSuffix = 'local.{0}.t{1}' -f $utc.ToString('yyyyMMdd'), $utc.ToString('HHmmss')
}

if (-not (Test-VersionSuffix -Suffix $VersionSuffix)) {
  Write-Err "Invalid versionsuffix '$VersionSuffix'. It must be dot-separated identifiers using [0-9A-Za-z-] only; numeric identifiers cannot have leading zeros."
  Write-Warn "Examples: preview.1, rc.2, local.20250811.t033324"
  exit 1
}
Write-Log "Using prerelease version suffix: $VersionSuffix"

# Build and pack
$pkgDir = $null
# Use build.cmd on Windows, build.sh otherwise (PowerShell is cross-platform)
if ($IsWindows) {
  $buildScript = Join-Path $RepoRoot 'build.cmd'
}
else {
  $buildScript = Join-Path $RepoRoot 'build.sh'
}

function Get-PackagesPath {
  param([Parameter(Mandatory)][string]$Config)
  Join-Path (Join-Path (Join-Path (Join-Path $RepoRoot 'artifacts') 'packages') $Config) 'Shipping'
}

$effectiveConfig = if ($Configuration) { $Configuration } else { 'Release' }

# Skip native AOT during pack unless user will build it separately via -NativeAot + Bundle.proj
$aotArg = if (-not $NativeAot) { "/p:PublishAot=false" } else { "" }

if ($Configuration) {
  Write-Log "Building and packing NuGet packages [-c $Configuration] with versionsuffix '$VersionSuffix'"
  & $buildScript -restore -build -pack -c $Configuration "/p:VersionSuffix=$VersionSuffix" "/p:SkipTestProjects=true" "/p:SkipPlaygroundProjects=true" $aotArg
  if ($LASTEXITCODE -ne 0) {
    Write-Err "Build failed for configuration $Configuration."
    exit 1
  }
  $pkgDir = Get-PackagesPath -Config $Configuration
  if (-not (Test-Path -LiteralPath $pkgDir)) {
    Write-Err "Could not find packages path $pkgDir for CONFIG=$Configuration"
    exit 1
  }
}
else {
  Write-Log "Building and packing NuGet packages [-c Release] with versionsuffix '$VersionSuffix'"
  & $buildScript -restore -build -pack -c Release "/p:VersionSuffix=$VersionSuffix" "/p:SkipTestProjects=true" "/p:SkipPlaygroundProjects=true" $aotArg
  if ($LASTEXITCODE -ne 0) {
    Write-Err "Build failed for configuration Release."
    exit 1
  }
  $pkgDir = Get-PackagesPath -Config 'Release'
  if (-not (Test-Path -LiteralPath $pkgDir)) {
    Write-Err "Could not find packages path $pkgDir for CONFIG=Release"
    exit 1
  }
}

# Ensure there are .nupkg files
$packages = Get-ChildItem -LiteralPath $pkgDir -Filter *.nupkg -File -ErrorAction SilentlyContinue
if (-not $packages -or $packages.Count -eq 0) {
  Write-Err "No .nupkg files found in $pkgDir. Did the pack step succeed?"
  exit 1
}
Write-Log ("Found {0} packages in {1}" -f $packages.Count, $pkgDir)

$hivesRoot = Join-Path (Join-Path $HOME '.aspire') 'hives'
$hiveRoot  = Join-Path $hivesRoot $Name
$hivePath  = Join-Path $hiveRoot 'packages'

Write-Log "Preparing hive directory: $hivesRoot"
New-Item -ItemType Directory -Path $hivesRoot -Force | Out-Null

# Remove previous hive content (handles both old layout junctions and stale data)
if (Test-Path -LiteralPath $hiveRoot) {
  Write-Log "Removing previous hive '$Name'"
  Remove-Item -LiteralPath $hiveRoot -Force -Recurse -ErrorAction SilentlyContinue
}

function Copy-PackagesToHive {
  param([string]$Source,[string]$Destination)
  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  Get-ChildItem -LiteralPath $Source -Filter *.nupkg -File | Copy-Item -Destination $Destination -Force
}

if ($Copy) {
  Write-Log "Populating hive '$Name' by copying .nupkg files"
  Copy-PackagesToHive -Source $pkgDir -Destination $hivePath
  Write-Log "Created/updated hive '$Name' at $hivePath (copied packages)."
}
else {
  Write-Log "Linking hive '$Name/packages' to $pkgDir"
  New-Item -ItemType Directory -Path $hiveRoot -Force | Out-Null
  try {
    # Try symlink first (requires Developer Mode or elevated privilege)
    New-Item -Path $hivePath -ItemType SymbolicLink -Target $pkgDir -Force | Out-Null
    Write-Log "Created/updated hive '$Name/packages' -> $pkgDir (symlink)"
  }
  catch {
    Write-Warn "Symlink not supported; attempting junction, else copying .nupkg files"
    try {
      New-Item -Path $hivePath -ItemType Junction -Target $pkgDir -Force | Out-Null
      Write-Log "Created/updated hive '$Name/packages' -> $pkgDir (junction)"
    }
    catch {
      Write-Warn "Link creation failed; copying .nupkg files instead"
      Copy-PackagesToHive -Source $pkgDir -Destination $hivePath
      Write-Log "Created/updated hive '$Name' at $hivePath (copied packages)."
    }
  }
}

# Determine the RID for the current platform
if ($IsWindows) {
  $bundleRid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
} elseif ($IsMacOS) {
  $bundleRid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'osx-arm64' } else { 'osx-x64' }
} else {
  $bundleRid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'linux-arm64' } else { 'linux-x64' }
}

$aspireRoot = Join-Path $HOME '.aspire'
$cliBinDir = Join-Path $aspireRoot 'bin'

# Build the bundle (aspire-managed + DCP, and optionally native AOT CLI)
if (-not $SkipBundle) {
  $bundleProjPath = Join-Path $RepoRoot "eng" "Bundle.proj"
  $skipNativeArg = if ($NativeAot) { '' } else { '/p:SkipNativeBuild=true' }

  Write-Log "Building bundle (aspire-managed + DCP$(if ($NativeAot) { ' + native AOT CLI' }))..."
  $buildArgs = @($bundleProjPath, '-c', $effectiveConfig, "/p:VersionSuffix=$VersionSuffix")
  if (-not $NativeAot) {
    $buildArgs += '/p:SkipNativeBuild=true'
  }
  & dotnet build @buildArgs
  if ($LASTEXITCODE -ne 0) {
    Write-Err "Bundle build failed."
    exit 1
  }

  $bundleLayoutDir = Join-Path $RepoRoot "artifacts" "bundle" $bundleRid

  if (-not (Test-Path -LiteralPath $bundleLayoutDir)) {
    Write-Err "Bundle layout not found at $bundleLayoutDir"
    exit 1
  }

  # Copy managed/ and dcp/ to $HOME/.aspire so the CLI auto-discovers them
  foreach ($component in @('managed', 'dcp')) {
    $sourceDir = Join-Path $bundleLayoutDir $component
    $destDir = Join-Path $aspireRoot $component
    if (Test-Path -LiteralPath $sourceDir) {
      if (Test-Path -LiteralPath $destDir) {
        Remove-Item -LiteralPath $destDir -Force -Recurse
      }
      Write-Log "Copying $component/ to $destDir"
      Copy-Item -LiteralPath $sourceDir -Destination $destDir -Recurse -Force
    } else {
      Write-Warn "$component/ not found in bundle layout at $sourceDir"
    }
  }

  Write-Log "Bundle installed to $aspireRoot (managed/ + dcp/)"
}

# Install the CLI to $HOME/.aspire/bin
if (-not $SkipCli) {
  $cliExeName = if ($IsWindows) { 'aspire.exe' } else { 'aspire' }

  if ($NativeAot) {
    # Native AOT CLI is produced by Bundle.proj's _PublishNativeCli target
    $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli" $effectiveConfig "net10.0" $bundleRid "native"
    if (-not (Test-Path -LiteralPath $cliPublishDir)) {
      $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli" $effectiveConfig "net10.0" $bundleRid "publish"
    }
  } else {
    # Framework-dependent CLI from dotnet tool build
    $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli.Tool" $effectiveConfig "net10.0" "publish"
    if (-not (Test-Path -LiteralPath $cliPublishDir)) {
      $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli.Tool" $effectiveConfig "net10.0"
    }
  }

  $cliSourcePath = Join-Path $cliPublishDir $cliExeName

  if (Test-Path -LiteralPath $cliSourcePath) {
    Write-Log "Installing Aspire CLI$(if ($NativeAot) { ' (native AOT)' }) to $cliBinDir"
    New-Item -ItemType Directory -Path $cliBinDir -Force | Out-Null

    # Backup existing CLI executable if it's locked (same pattern as aspire update --self)
    $targetExePath = Join-Path $cliBinDir $cliExeName
    $backupPath = $null
    if (Test-Path -LiteralPath $targetExePath) {
      $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
      $backupPath = "$targetExePath.old.$timestamp"
      try {
        Move-Item -LiteralPath $targetExePath -Destination $backupPath -Force -ErrorAction Stop
        Write-Log "Backed up existing CLI to $backupPath"
      }
      catch {
        Write-Warn "Could not backup existing CLI (may be in use). Attempting direct overwrite."
        $backupPath = $null
      }
    }

    try {
      # Copy all files from the publish directory (CLI and its dependencies)
      # Use -ErrorAction SilentlyContinue for individual files that may be locked by running processes
      $copyErrors = @()
      Get-ChildItem -LiteralPath $cliPublishDir -File | ForEach-Object {
        try {
          Copy-Item $_.FullName -Destination $cliBinDir -Force -ErrorAction Stop
        }
        catch {
          $copyErrors += $_.Exception.Message
        }
      }
      if ($copyErrors.Count -gt 0) {
        Write-Warn "$($copyErrors.Count) file(s) could not be overwritten (likely locked by a running process). The CLI executable was updated successfully."
      }

      # Clean up old backup files
      Get-ChildItem -LiteralPath $cliBinDir -Filter "$cliExeName.old.*" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    }
    catch {
      # Restore backup if copy failed
      if ($backupPath -and (Test-Path -LiteralPath $backupPath)) {
        Write-Warn "Copy failed, restoring backup"
        Move-Item -LiteralPath $backupPath -Destination $targetExePath -Force
      }
      throw
    }

    $installedCliPath = Join-Path $cliBinDir $cliExeName
    Write-Log "Aspire CLI installed to: $installedCliPath"

    # Set the channel to the local hive so templates and packages resolve from it
    & $installedCliPath config set channel $Name -g 2>$null
    Write-Log "Set global channel to '$Name'"

    # Check if the bin directory is in PATH
    $pathSeparator = [System.IO.Path]::PathSeparator
    $currentPathArray = $env:PATH.Split($pathSeparator, [StringSplitOptions]::RemoveEmptyEntries)
    if ($currentPathArray -notcontains $cliBinDir) {
      Write-Warn "The CLI bin directory is not in your PATH."
      Write-Log "Add it to your PATH with: `$env:PATH = '$cliBinDir' + '$pathSeparator' + `$env:PATH"
    }
  }
  else {
    Write-Warn "Could not find CLI at $cliSourcePath. Skipping CLI installation."
    Write-Warn "You may need to build the CLI separately or use 'dotnet tool install' for the Aspire.Cli package."
  }
}

Write-Host
Write-Log 'Done.'
Write-Host
Write-Log "Aspire CLI will discover a channel named '$Name' from:"
Write-Log "  $hivePath"
Write-Host
Write-Log "Channel behavior: Aspire* comes from the hive; others from nuget.org."
Write-Host
if (-not $SkipCli) {
  Write-Log "The locally-built CLI was installed to: $(Join-Path (Join-Path $HOME '.aspire') 'bin')"
  Write-Host
}
if (-not $SkipBundle) {
  Write-Log "Bundle (aspire-managed + DCP) installed to: $(Join-Path $HOME '.aspire')"
  Write-Log "  The CLI at ~/.aspire/bin/ will auto-discover managed/ and dcp/ in the parent directory."
  Write-Host
}
Write-Log 'The Aspire CLI discovers channels automatically from the hives directory; no extra flags are required.'
