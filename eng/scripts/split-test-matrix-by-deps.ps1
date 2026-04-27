<#
.SYNOPSIS
  Splits an all_tests matrix by dependency type for GitHub Actions.

.DESCRIPTION
  Takes a flat all_tests matrix JSON (already OS-expanded) and splits it into
  dependency-based matrices for GitHub Actions consumption:
  1. tests_matrix_no_nugets (primary) — tests with no package dependencies
  2. tests_matrix_no_nugets_overflow — overflow when primary exceeds threshold
  3. tests_matrix_requires_nugets_{linux,windows,macos} — tests needing built
     NuGet packages, split by OS so each group can depend on the per-OS CLI
     archive build that produces its RID-specific DCP/Dashboard NuGets
  4. tests_matrix_requires_cli_archive — tests needing CLI native archives

  The overflow mechanism keeps each matrix under GitHub Actions' 256-job limit.

.PARAMETER AllTestsMatrix
  JSON string of the all_tests matrix ({"include": [...]}).

.PARAMETER AllTestsMatrixFile
  Path to a JSON file containing the all_tests matrix.
  Mutually exclusive with AllTestsMatrix.

.PARAMETER OutputToGitHubEnv
  If set, outputs to GITHUB_OUTPUT environment file.

.PARAMETER OverflowThreshold
  Maximum entries in the no_nugets primary bucket before overflow kicks in.
  Defaults to 250 (GitHub Actions hard limit is 256).

.NOTES
  PowerShell 7+
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory=$false)]
  [string]$AllTestsMatrix = "",

  [Parameter(Mandatory=$false)]
  [string]$AllTestsMatrixFile = "",

  [Parameter(Mandatory=$false)]
  [switch]$OutputToGitHubEnv,

  [Parameter(Mandatory=$false)]
  [int]$OverflowThreshold = 250
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maxMatrixSize = 256

function Get-FriendlyTypeName {
  param(
    [AllowNull()]
    [object]$Value
  )

  if ($null -eq $Value) {
    return 'null'
  }

  return $Value.GetType().FullName
}

function Get-MatrixEntries {
  param(
    [Parameter(Mandatory=$true)]
    [object]$Matrix,

    [Parameter(Mandatory=$true)]
    [string]$MatrixName
  )

  if ($null -eq $Matrix) {
    throw "Matrix '$MatrixName' could not be parsed from JSON."
  }

  if ($Matrix.PSObject.Properties.Name -notcontains 'include') {
    throw "Matrix '$MatrixName' must contain an 'include' property."
  }

  if ($null -eq $Matrix.include) {
    return @()
  }

  if ($Matrix.include -is [string] -or $Matrix.include -is [System.ValueType]) {
    throw "Matrix '$MatrixName' has an invalid 'include' value of type $(Get-FriendlyTypeName $Matrix.include). Expected an object or array of matrix entries."
  }

  $entries = @($Matrix.include)
  foreach ($entry in $entries) {
    if ($null -eq $entry) {
      throw "Matrix '$MatrixName' contains a null entry in 'include'."
    }

    if ($entry -is [string] -or $entry -is [System.ValueType]) {
      throw "Matrix '$MatrixName' contains an invalid entry of type $(Get-FriendlyTypeName $entry). Expected each matrix entry to be an object."
    }
  }

  return $entries
}

# Read input
if ($AllTestsMatrixFile) {
  if (-not (Test-Path $AllTestsMatrixFile)) {
    Write-Error "Matrix file not found: $AllTestsMatrixFile"
    exit 1
  }
  $AllTestsMatrix = Get-Content -Raw $AllTestsMatrixFile
}

if (-not $AllTestsMatrix) {
  Write-Error "Either -AllTestsMatrix or -AllTestsMatrixFile must be provided."
  exit 1
}

$matrix = $AllTestsMatrix | ConvertFrom-Json
$allEntries = @(Get-MatrixEntries -Matrix $matrix -MatrixName 'all_tests')

Write-Host "Input matrix: $($allEntries.Count) total entries"

# Helper to safely read a boolean flag from the properties sub-object
function Get-PropertyFlag {
  param($Entry, [string]$Name)
  if ($Entry.PSObject.Properties.Name -contains 'properties' -and $Entry.properties -and
      $Entry.properties.PSObject.Properties.Name -contains $Name) {
    return $Entry.properties.$Name -eq $true
  }
  return $false
}

# Split into categories based on dependency requirements
$cliArchiveEntries = @($allEntries | Where-Object { Get-PropertyFlag $_ 'requiresCliArchive' })
$nugetEntries = @($allEntries | Where-Object {
  (Get-PropertyFlag $_ 'requiresNugets') -and
  -not (Get-PropertyFlag $_ 'requiresCliArchive')
})
$noNugetEntries = @($allEntries | Where-Object {
  -not (Get-PropertyFlag $_ 'requiresNugets') -and
  -not (Get-PropertyFlag $_ 'requiresCliArchive')
})

Write-Host "  - No nugets: $($noNugetEntries.Count)"
Write-Host "  - Requires nugets: $($nugetEntries.Count)"
Write-Host "  - Requires CLI archive: $($cliArchiveEntries.Count)"

# Further split nuget entries by OS so test jobs can depend on
# the per-OS CLI archive build that produces their RID-specific NuGets.
function Get-OsCategory([string]$runsOn) {
  if ($runsOn -match 'ubuntu|linux') { return 'linux' }
  if ($runsOn -match 'windows')      { return 'windows' }
  if ($runsOn -match 'macos')        { return 'macos' }
  Write-Warning "Unknown runs-on value '$runsOn', defaulting to 'linux'"
  return 'linux'
}

$nugetEntriesLinux   = @($nugetEntries | Where-Object { (Get-OsCategory $_.'runs-on') -eq 'linux' })
$nugetEntriesWindows = @($nugetEntries | Where-Object { (Get-OsCategory $_.'runs-on') -eq 'windows' })
$nugetEntriesMacos   = @($nugetEntries | Where-Object { (Get-OsCategory $_.'runs-on') -eq 'macos' })

Write-Host "    ↳ nugets linux: $($nugetEntriesLinux.Count), windows: $($nugetEntriesWindows.Count), macos: $($nugetEntriesMacos.Count)"

# Split no_nugets into primary + overflow
$noNugetPrimary = @()
$noNugetOverflow = @()

if ($noNugetEntries.Count -le $OverflowThreshold) {
  $noNugetPrimary = $noNugetEntries
} else {
  $noNugetPrimary = @($noNugetEntries[0..($OverflowThreshold - 1)])
  $noNugetOverflow = @($noNugetEntries[$OverflowThreshold..($noNugetEntries.Count - 1)])
  Write-Host "  ↳ no_nugets overflow: $($noNugetPrimary.Count) primary + $($noNugetOverflow.Count) overflow"
}

# Validate no bucket exceeds the hard limit
$buckets = @{
  'tests_matrix_no_nugets' = $noNugetPrimary
  'tests_matrix_no_nugets_overflow' = $noNugetOverflow
  'tests_matrix_requires_nugets_linux' = $nugetEntriesLinux
  'tests_matrix_requires_nugets_windows' = $nugetEntriesWindows
  'tests_matrix_requires_nugets_macos' = $nugetEntriesMacos
  'tests_matrix_requires_cli_archive' = $cliArchiveEntries
}

foreach ($name in $buckets.Keys) {
  if ($buckets[$name].Count -gt $maxMatrixSize) {
    Write-Error "$name has $($buckets[$name].Count) entries, exceeding the GitHub Actions limit of $maxMatrixSize."
    exit 1
  }
}

# Convert to JSON
$results = @{}
foreach ($name in $buckets.Keys) {
  $results[$name] = ConvertTo-Json @{ include = @($buckets[$name]) } -Compress -Depth 10
}

# Output
if ($OutputToGitHubEnv) {
  if ($env:GITHUB_OUTPUT) {
    foreach ($name in $results.Keys) {
      "$name=$($results[$name])" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
    Write-Host "✓ Split matrices written to GITHUB_OUTPUT"
  } else {
    Write-Error "GITHUB_OUTPUT environment variable not set"
    exit 1
  }
} else {
  # Output to console for debugging
  foreach ($name in $results.Keys) {
    Write-Host "${name}: $($results[$name])"
  }
}

Write-Host ""
Write-Host "Matrix split complete!"
