#!/usr/bin/env pwsh

<#!
.SYNOPSIS
  Build local NuGet packages, Aspire CLI, and bundle, then create/update a hive and install everything (Windows/PowerShell).

.DESCRIPTION
  Mirrors localhive.sh behavior on Windows. Packs the repo, creates a symlink from
  $HOME/.aspire/hives/<HiveName> to artifacts/packages/<Config>/Shipping (or copies .nupkg files),
  installs the locally-built Aspire CLI to $HOME/.aspire/bin, and embeds the bundle
  payload (aspire-managed + DCP) in the CLI binary so it self-extracts on first run.

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
  Skip building the bundle payload. Without it the CLI binary will not have an embedded
  bundle and will require externally-provided managed/ and dcp/ directories.

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

  [Alias('o')]
  [string] $Output,

  [Alias('r')]
  [string] $Rid,

  [switch] $Archive,

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
  -Output (-o)          Output directory for portable layout (instead of $HOME\.aspire)
  -Rid (-r)             Target RID for cross-platform builds (e.g. linux-x64)
  -VersionSuffix (-v)   Prerelease version suffix (default: auto-generates local.YYYYMMDD.tHHmmss)
  -Archive              Create an archive (.tar.gz or .zip) of the output. Requires -Output.
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
  .\localhive.ps1 -o ./aspire-linux -r linux-x64 -Archive  # Portable archive for a Linux machine

This will pack NuGet packages into artifacts\packages\<Config>\Shipping and create/update
a hive at $HOME\.aspire\hives\<HiveName> so the Aspire CLI can use it as a channel.
It also installs the locally-built CLI to $HOME\.aspire\bin (unless -SkipCli is specified).
'@ | Write-Host
}

function Write-Log   { param([string]$m) Write-Host "[localhive] $m" }
function Write-Warn  { param([string]$m) Write-Warning "[localhive] $m" }
function Write-Err   { param([string]$m) Write-Error "[localhive] $m" }

if ($Help) { Show-Usage; exit 0 }

# Validate flag combinations
if ($Archive -and -not $Output) {
  Write-Err "-Archive requires -Output to be specified."
  exit 1
}

if ($Rid -and $NativeAot) {
  # Detect if this is a cross-OS build
  $hostPrefix = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
  if (-not $Rid.StartsWith($hostPrefix)) {
    Write-Err "Cross-OS native AOT builds are not supported (host=$hostPrefix, target=$Rid). Use -Rid without -NativeAot."
    exit 1
  }
}

# When -Output is specified, always copy (portable layout, no symlinks)
if ($Output) {
  $Copy = $true
}

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

# Restrict hive names to a safe identifier set: this value is joined into
# $hivesRoot\$Name and then passed to Remove-Item -Recurse, so any path
# separator or '..' segment would let the removal escape the hives root.
function Test-HiveName {
  param([Parameter(Mandatory)][string]$HiveName)
  if ([string]::IsNullOrEmpty($HiveName)) { return $false }
  if ($HiveName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { return $false }
  if ($HiveName.Contains('..')) { return $false }
  return $true
}

if (-not (Test-HiveName -HiveName $Name)) {
  Write-Err "Invalid hive name '$Name'. Hive names must match [A-Za-z0-9][A-Za-z0-9._-]* and cannot contain path separators or '..'."
  exit 1
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

# Determine the RID for the target platform (or auto-detect from host)
if ($Rid) {
  $bundleRid = $Rid
  Write-Log "Using target RID: $bundleRid"
} elseif ($IsWindows) {
  $bundleRid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
} elseif ($IsMacOS) {
  $bundleRid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'osx-arm64' } else { 'osx-x64' }
} else {
  $bundleRid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'linux-arm64' } else { 'linux-x64' }
}

if ($Output) {
  $aspireRoot = $Output
} else {
  $aspireRoot = Join-Path $HOME '.aspire'
}
$cliBinDir = Join-Path $aspireRoot 'bin'

$hivesRoot = Join-Path $aspireRoot 'hives'
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
  # When $VersionSuffix is non-empty, only .nupkg filenames containing the suffix are copied,
  # and zero matches is treated as a hard failure. This mirrors localhive.sh's $USE_COPY path
  # (localhive.sh:308-322) and exists because PackagingService.GetLocalHivePinnedVersion picks
  # the *highest* SemVer-precedence package in the hive
  # (src/Aspire.Cli/Packaging/PackagingService.cs:338-350). Without the filter, leftover packages
  # from a prior localhive run with a higher-precedence suffix would silently pin this hive to
  # that stale version.
  # When $VersionSuffix is empty, all .nupkg files are copied — this is used only by the
  # symlink/junction failure fallback below, where the user did not opt into copy mode and
  # parity with the bash fallback (localhive.sh:329-342) takes priority over the staleness
  # check.
  param(
    [string]$Source,
    [string]$Destination,
    [string]$VersionSuffix
  )
  New-Item -ItemType Directory -Path $Destination -Force | Out-Null
  $candidates = Get-ChildItem -LiteralPath $Source -Filter *.nupkg -File
  if ($VersionSuffix) {
    $candidates = $candidates | Where-Object { $_.Name -like "*$VersionSuffix*" }
  }
  $copied = 0
  foreach ($pkg in $candidates) {
    Copy-Item -LiteralPath $pkg.FullName -Destination $Destination -Force
    $copied++
  }
  if ($VersionSuffix -and $copied -eq 0) {
    Write-Err "No .nupkg files matching version suffix '$VersionSuffix' found in $Source."
    exit 1
  }
  return $copied
}

if ($Copy) {
  Write-Log "Populating hive '$Name' by copying .nupkg files (version suffix: $VersionSuffix)"
  $copied = Copy-PackagesToHive -Source $pkgDir -Destination $hivePath -VersionSuffix $VersionSuffix
  Write-Log "Created/updated hive '$Name' at $hivePath (copied $copied packages)."
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
      # Fallback path: user did not request -Copy, so mirror the unfiltered bash fallback
      # (localhive.sh:329-342) and copy everything to maximize the chance the build succeeds.
      $copied = Copy-PackagesToHive -Source $pkgDir -Destination $hivePath -VersionSuffix ''
      Write-Log "Created/updated hive '$Name' at $hivePath (copied $copied packages)."
    }
  }
}

# Build the bundle payload (aspire-managed + DCP tar.gz archive, and optionally native AOT CLI)
if (-not $SkipBundle) {
  $bundleProjPath = Join-Path $RepoRoot "eng" "Bundle.proj"
  $skipNativeArg = if ($NativeAot) { '' } else { '/p:SkipNativeBuild=true' }

  # Clean stale managed publish output so dotnet publish doesn't skip due to incremental builds
  $staleManagedDir = Join-Path $RepoRoot "artifacts" "bundle" $bundleRid "managed"
  if (Test-Path -LiteralPath $staleManagedDir) {
    Write-Log "Cleaning stale managed publish output at $staleManagedDir"
    Remove-Item -LiteralPath $staleManagedDir -Force -Recurse
  }

  Write-Log "Building bundle (aspire-managed + DCP$(if ($NativeAot) { ' + native AOT CLI' }))..."
  $buildArgs = @($bundleProjPath, '-c', $effectiveConfig, "/p:VersionSuffix=$VersionSuffix", "/p:TargetRid=$bundleRid")
  if (-not $NativeAot) {
    $buildArgs += '/p:SkipNativeBuild=true'
  }
  & dotnet build @buildArgs
  if ($LASTEXITCODE -ne 0) {
    Write-Err "Bundle build failed."
    exit 1
  }

  # Locate the bundle payload archive produced by Bundle.proj / CreateLayout.
  # The archive is embedded in the CLI binary so EnsureExtractedAsync handles
  # versioned layout creation, symlink/junction management, and cleanup at runtime.
  $bundlePayloadArchive = Get-ChildItem -Path (Join-Path $RepoRoot "artifacts" "bundle") -Filter "aspire-*-$bundleRid.tar.gz" -File |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
  if (-not $bundlePayloadArchive) {
    Write-Err "Bundle payload archive not found in artifacts/bundle/ for RID $bundleRid"
    exit 1
  }
  Write-Log "Bundle payload archive: $($bundlePayloadArchive.FullName) ($([math]::Round($bundlePayloadArchive.Length / 1MB, 1)) MB)"
}

# Install the CLI to $aspireRoot/bin
if (-not $SkipCli) {
  $cliExeName = if ($bundleRid -like 'win-*') { 'aspire.exe' } else { 'aspire' }

  if ($NativeAot) {
    # Native AOT CLI is produced by Bundle.proj's _PublishNativeCli target
    # (already has embedded bundle payload)
    $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli" $effectiveConfig "net10.0" $bundleRid "native"
    if (-not (Test-Path -LiteralPath $cliPublishDir)) {
      $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli" $effectiveConfig "net10.0" $bundleRid "publish"
    }
  } elseif ($Rid) {
    # Cross-RID: publish CLI for the target platform with embedded bundle payload
    Write-Log "Publishing Aspire CLI for target RID: $Rid"
    $cliProj = Join-Path $RepoRoot "src" "Aspire.Cli" "Aspire.Cli.csproj"
    $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli" $effectiveConfig "net10.0" $Rid "publish"
    $publishArgs = @($cliProj, '-c', $effectiveConfig, '-r', $Rid, '--self-contained', '/p:PublishAot=false', '/p:PublishSingleFile=true', "/p:VersionSuffix=$VersionSuffix")
    if ($bundlePayloadArchive) {
      $publishArgs += "/p:BundlePayloadPath=$($bundlePayloadArchive.FullName)"
    }
    & dotnet publish @publishArgs
    if ($LASTEXITCODE -ne 0) {
      Write-Err "CLI publish for RID $Rid failed."
      exit 1
    }
  } else {
    $cliProj = Join-Path $RepoRoot "src" "Aspire.Cli" "Aspire.Cli.Tool.csproj"
    if ($bundlePayloadArchive) {
      # NativeAOT CLI (Aspire.Cli.csproj sets PublishAot=true) with embedded bundle payload.
      # Publish output is RID-specific when we pass -r, so the path includes $bundleRid.
      $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli.Tool" $effectiveConfig "net10.0" $bundleRid "publish"
      Write-Log "Publishing Aspire CLI (dotnet tool, native AOT) with embedded bundle payload..."
      & dotnet publish $cliProj -c $effectiveConfig -r $bundleRid "/p:VersionSuffix=$VersionSuffix" "/p:BundlePayloadPath=$($bundlePayloadArchive.FullName)"
      if ($LASTEXITCODE -ne 0) {
        Write-Err "CLI publish with embedded bundle failed."
        exit 1
      }
    } else {
      # -SkipBundle builds Aspire.Cli.Tool with PublishAot=false, which keeps the
      # historical framework-dependent, non-RID output layout.
      $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli.Tool" $effectiveConfig "net10.0" "publish"
      if (-not (Test-Path -LiteralPath $cliPublishDir)) {
        $cliPublishDir = Join-Path $RepoRoot "artifacts" "bin" "Aspire.Cli.Tool" $effectiveConfig "net10.0"
      }
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

    $installedCliPath = Join-Path $cliBinDir $cliExeName

    try {
      # Copy all files from the publish directory (CLI and its dependencies)
      # Capture individual copy failures so we can restore the previous CLI and avoid stamping
      # a sidecar onto a stale or partial install.
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
        throw "Failed to copy $($copyErrors.Count) CLI file(s) from $cliPublishDir to $cliBinDir. First error: $($copyErrors[0])"
      }

      if (-not (Test-Path -LiteralPath $installedCliPath)) {
        throw "Installed CLI executable was not found at $installedCliPath"
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

    # Stamp the install-route sidecar so `aspire info` / `aspire uninstall`
    # can identify this binary as a locally-built (`localhive`) install.
    # The format matches docs/specs/install-routes.md exactly; localhive
    # shares the script-route layout (binary under <prefix>/bin/, bundle
    # extracted at parent-of-bin).
    $sidecarPath = Join-Path $cliBinDir ".aspire-install.json"
    Set-Content -LiteralPath $sidecarPath -Value '{"source":"localhive"}' -Encoding UTF8 -NoNewline

    Write-Log "Aspire CLI installed to: $installedCliPath"

    if (-not $Output) {
      $pathSeparator = [System.IO.Path]::PathSeparator
      $currentPathArray = if ($env:PATH) { $env:PATH.Split($pathSeparator, [StringSplitOptions]::RemoveEmptyEntries) } else { @() }
      Write-Log "Run Aspire directly with: $installedCliPath"
      if ($currentPathArray -notcontains $cliBinDir) {
        $env:PATH = (@($cliBinDir) + $currentPathArray) -join $pathSeparator
        Write-Log "Added $cliBinDir to PATH for this PowerShell session."
      }
    }
  }
  else {
    Write-Warn "Could not find CLI at $cliSourcePath. Skipping CLI installation."
    Write-Warn "You may need to build the CLI separately or use 'dotnet tool install' for the Aspire.Cli package."
  }
}

# Create archive if requested
if ($Archive) {
  if ($bundleRid -like 'win-*') {
    $archivePath = "$Output.zip"
    Write-Log "Creating archive: $archivePath"

    # Use System.IO.Compression.ZipFile::CreateFromDirectory rather than
    # Compress-Archive. Compress-Archive enumerates inputs via the PowerShell
    # provider, which on non-Windows hosts treats files whose name starts with
    # '.' as hidden and excludes them from `<dir>/*` wildcard expansion. The
    # portable layout includes bin/.aspire-install.json — the localhive route
    # sidecar that `aspire doctor` and route-aware Aspire-home selection rely
    # on (see docs/specs/install-routes.md) — and silently dropping it from
    # win-* zips built on Linux/macOS would produce sidecar-less installs on
    # the target machine. ZipFile walks the filesystem directly and includes
    # dotfiles unconditionally.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $archivePath) {
      Remove-Item -LiteralPath $archivePath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
      $Output,
      $archivePath,
      [System.IO.Compression.CompressionLevel]::Optimal,
      $false)

    # Belt-and-suspenders: if the portable layout has a sidecar on disk, the
    # archive MUST also have it. Catches future regressions of the dotfile
    # issue (e.g. a switch back to Compress-Archive or a wildcard-based copy).
    $sidecarOnDisk = Join-Path $Output (Join-Path 'bin' '.aspire-install.json')
    if (Test-Path -LiteralPath $sidecarOnDisk) {
      $sidecarEntryName = 'bin/.aspire-install.json'
      $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
      try {
        $hasSidecar = $false
        foreach ($entry in $zip.Entries) {
          if ($entry.FullName -eq $sidecarEntryName) { $hasSidecar = $true; break }
        }
      }
      finally {
        $zip.Dispose()
      }
      if (-not $hasSidecar) {
        throw "Archive '$archivePath' is missing the install-route sidecar entry '$sidecarEntryName'. The zip creation path is dropping hidden files."
      }
    }
  } else {
    $archivePath = "$Output.tar.gz"
    Write-Log "Creating archive: $archivePath"
    tar -czf $archivePath -C $Output .
  }
  Write-Log "Archive created: $archivePath"
}

Write-Host
Write-Log 'Done.'
Write-Host
if ($Output) {
  Write-Log "Portable layout created at: $Output"
  if ($Archive) {
    Write-Log "Archive: $archivePath"
    Write-Log ""
    Write-Log "To install on the target machine:"
    if ($bundleRid -like 'win-*') {
      Write-Log "  Expand-Archive -Path $(Split-Path $archivePath -Leaf) -DestinationPath `$HOME\.aspire"
      Write-Log "  `$HOME\.aspire\bin\aspire.exe"
    } else {
      Write-Log "  mkdir -p ~/.aspire && tar -xzf $(Split-Path $archivePath -Leaf) -C ~/.aspire"
      Write-Log "  ~/.aspire/bin/aspire"
    }
  }
} else {
  Write-Log "Aspire CLI will discover a channel named '$Name' from:"
  Write-Log "  $hivePath"
  Write-Host
  Write-Log "Channel behavior: Aspire* comes from the hive; others from nuget.org."
  Write-Host
  if (-not $SkipCli) {
    Write-Log "The locally-built CLI was installed to: $cliBinDir"
    Write-Host
  }
  if (-not $SkipBundle) {
    Write-Log "Bundle payload embedded in CLI binary. The CLI will extract and"
    Write-Log "  create the versioned layout (bundle/ -> versions/<id>/) on first run."
    Write-Host
  }
  Write-Log 'The Aspire CLI discovers channels automatically from the hives directory; no extra flags are required.'
}
