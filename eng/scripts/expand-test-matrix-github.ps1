<#
.SYNOPSIS
  Expands the canonical test matrix for GitHub Actions.

.DESCRIPTION
  This script takes the canonical test matrix (output by build-test-matrix.ps1)
  and transforms it for GitHub Actions consumption by:
  1. Expanding each entry for every OS in its supportedOSes array
  2. Mapping OS names to GitHub runner names (linux -> ubuntu-latest, etc.)
  3. Outputting a single all_tests matrix with all entries

  This is the platform-specific layer for GitHub Actions. Azure DevOps would
  have a similar script with different runner mappings and output format.

  Downstream consumers (e.g., tests.yml) are responsible for splitting the
  matrix by dependency type and handling overflow.

.PARAMETER CanonicalMatrixFile
  Path to the canonical test matrix JSON file (output of build-test-matrix.ps1).

.PARAMETER OutputMatrixFile
  Output file path for the matrix JSON. When set, writes the all_tests matrix.

.PARAMETER OutputToGitHubEnv
  If set, outputs to GITHUB_OUTPUT environment file instead of files.

.NOTES
  PowerShell 7+
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)]
  [string]$CanonicalMatrixFile,

  [Parameter(Mandatory=$false)]
  [string]$OutputMatrixFile = "",

  [Parameter(Mandatory=$false)]
  [switch]$OutputToGitHubEnv
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# GitHub runner mappings
$runnerMap = @{
  'windows' = 'windows-latest'
  'linux' = 'ubuntu-latest'
  'macos' = 'macos-latest'
}

# Valid OS values
$validOSes = @('windows', 'linux', 'macos')

function Expand-MatrixEntriesByOS {
  param(
    [Parameter(Mandatory=$true)]
    [array]$Entries
  )

  $expandedEntries = @()

  foreach ($entry in $Entries) {
    # Get supported OSes (default to all if not specified)
    $hasSupportedOSes = [bool]($entry.PSObject.Properties.Name -contains 'supportedOSes')
    $supportedOSes = if ($hasSupportedOSes -and $entry.supportedOSes -and $entry.supportedOSes.Count -gt 0) {
      $entry.supportedOSes
    } else {
      $validOSes
    }

    # Validate and expand for each OS
    foreach ($os in $supportedOSes) {
      $osLower = $os.ToLowerInvariant()

      if ($osLower -notin $validOSes) {
        Write-Warning "Invalid OS '$os' in supportedOSes for test '$($entry.name)'. Skipping."
        continue
      }

      # Create a copy of the entry for this OS
      $expandedEntry = [ordered]@{}
      foreach ($prop in $entry.PSObject.Properties) {
        if ($prop.Name -ne 'supportedOSes') {
          $expandedEntry[$prop.Name] = $prop.Value
        }
      }

      # Add GitHub-specific runner (use custom runner if specified and non-empty, otherwise default)
      $hasRunners = $entry.PSObject.Properties.Name -contains 'runners'
      $customRunner = $null
      if ($hasRunners -and $entry.runners) {
        $runnerProp = $entry.runners.PSObject.Properties[$osLower]
        if ($runnerProp -and -not [string]::IsNullOrWhiteSpace([string]$runnerProp.Value)) {
          $customRunner = [string]$runnerProp.Value
        }
      }
      $expandedEntry['runs-on'] = if ($customRunner) { $customRunner } else { $runnerMap[$osLower] }

      $expandedEntries += [PSCustomObject]$expandedEntry
    }
  }

  return $expandedEntries
}

# Read canonical matrix
if (-not (Test-Path $CanonicalMatrixFile)) {
  Write-Error "Canonical matrix file not found: $CanonicalMatrixFile"
  exit 1
}

Write-Host "Reading canonical matrix from: $CanonicalMatrixFile"
$canonicalMatrix = Get-Content -Raw $CanonicalMatrixFile | ConvertFrom-Json

# Expand matrix entries by OS
$allEntries = @()

if ($canonicalMatrix.PSObject.Properties.Name -contains 'tests' -and $canonicalMatrix.tests) {
  $allEntries = Expand-MatrixEntriesByOS -Entries $canonicalMatrix.tests
}

Write-Host "Expanded matrix: $($allEntries.Count) total entries"

$allTestsMatrix = ConvertTo-Json @{ include = @($allEntries) } -Compress -Depth 10

# Output results
if ($OutputToGitHubEnv) {
  # Output to GitHub Actions environment
  if ($env:GITHUB_OUTPUT) {
    "all_tests=$allTestsMatrix" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    Write-Host "✓ Matrix written to GITHUB_OUTPUT ($($allEntries.Count) entries)"
  } else {
    Write-Error "GITHUB_OUTPUT environment variable not set"
    exit 1
  }
} elseif ($OutputMatrixFile) {
  $allTestsMatrix | Set-Content -Path $OutputMatrixFile -Encoding UTF8
  Write-Host "✓ Matrix written to: $OutputMatrixFile"
} else {
  # Output to console for debugging
  Write-Host ""
  Write-Host "All tests: $allTestsMatrix"
}

Write-Host ""
Write-Host "GitHub Actions matrix expansion complete!"
