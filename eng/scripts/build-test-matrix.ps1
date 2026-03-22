<#
.SYNOPSIS
  Builds the canonical test matrix from test enumeration files.

.DESCRIPTION
  This script processes test metadata files and generates a canonical test matrix
  that can be consumed by any CI platform (GitHub Actions, Azure DevOps, etc.).

  The script:
  1. Collects all .tests-metadata.json files from the artifacts directory
  2. Processes regular and split test projects
  3. Applies default values for missing properties
  4. Normalizes boolean values
  5. Outputs a canonical JSON format with supportedOSes arrays (not expanded)

  The output format is platform-agnostic. Each CI platform should have a thin
  script to expand supportedOSes into platform-specific runner configurations.

.PARAMETER ArtifactsDir
  Path to the artifacts directory containing .tests-metadata.json files.

.PARAMETER OutputMatrixFile
  Path to write the canonical test matrix JSON file.

.NOTES
  PowerShell 7+

  Output format:
  {
    "tests": [ { entry with supportedOSes array and requiresNugets boolean }, ... ]
  }
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)]
  [string]$ArtifactsDir,

  [Parameter(Mandatory=$true)]
  [string]$OutputMatrixFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Default values applied to all entries
$script:defaults = @{
  extraTestArgs = ''
  requiresNugets = $false
  requiresTestSdk = $false
  testSessionTimeout = '20m'
  testHangTimeout = '10m'
  supportedOSes = @('windows', 'linux', 'macos')
}

Write-Host "Building canonical test matrix"
Write-Host "Artifacts directory: $ArtifactsDir"

# Helper function to normalize boolean values
function ConvertTo-Boolean {
  param($Value)
  return ($Value -eq 'true' -or $Value -eq $true)
}

# Helper function to apply defaults and normalize an entry
function Complete-EntryWithDefaults {
  param([Parameter(Mandatory=$true)]$Entry)

  # Apply defaults for missing properties
  if (-not $Entry['testSessionTimeout']) { $Entry['testSessionTimeout'] = $script:defaults.testSessionTimeout }
  if (-not $Entry['testHangTimeout']) { $Entry['testHangTimeout'] = $script:defaults.testHangTimeout }
  if (-not $Entry.Contains('extraTestArgs')) { $Entry['extraTestArgs'] = $script:defaults.extraTestArgs }
  if (-not $Entry['supportedOSes'] -or $Entry['supportedOSes'].Count -eq 0) {
    $Entry['supportedOSes'] = $script:defaults.supportedOSes
  }

  # Normalize boolean values
  $Entry['requiresNugets'] = if ($Entry.Contains('requiresNugets')) { ConvertTo-Boolean $Entry['requiresNugets'] } else { $false }
  $Entry['requiresTestSdk'] = if ($Entry.Contains('requiresTestSdk')) { ConvertTo-Boolean $Entry['requiresTestSdk'] } else { $false }
  $Entry['requiresCliArchive'] = if ($Entry.Contains('requiresCliArchive')) { ConvertTo-Boolean $Entry['requiresCliArchive'] } else { $false }

  return $Entry
}

# Helper function to create matrix entry for regular (non-split) tests
function New-RegularTestEntry {
  param(
    [Parameter(Mandatory=$false)]
    $Metadata = $null
  )

  $entry = [ordered]@{
    type = 'regular'
    projectName = $Metadata.projectName
    name = $Metadata.shortName
    shortname = $Metadata.shortName
    testProjectPath = $Metadata.testProjectPath
    workitemprefix = $Metadata.projectName
    splitTests = $false
  }

  # Add metadata if available
  if ($Metadata) {
    if ($Metadata.PSObject.Properties['testSessionTimeout']) { $entry['testSessionTimeout'] = $Metadata.testSessionTimeout }
    if ($Metadata.PSObject.Properties['testHangTimeout']) { $entry['testHangTimeout'] = $Metadata.testHangTimeout }
    if ($Metadata.PSObject.Properties['requiresNugets']) { $entry['requiresNugets'] = $Metadata.requiresNugets }
    if ($Metadata.PSObject.Properties['requiresTestSdk']) { $entry['requiresTestSdk'] = $Metadata.requiresTestSdk }
    if ($Metadata.PSObject.Properties['requiresCliArchive']) { $entry['requiresCliArchive'] = $Metadata.requiresCliArchive }
    if ($Metadata.PSObject.Properties['extraTestArgs'] -and $Metadata.extraTestArgs) { $entry['extraTestArgs'] = $Metadata.extraTestArgs }
  }

  # Add supported OSes
  $entry['supportedOSes'] = @($Metadata.supportedOSes)

  # Pass through custom runners if specified
  if ($Metadata.PSObject.Properties['runners'] -and $Metadata.runners) {
    $entry['runners'] = $Metadata.runners
  }

  return Complete-EntryWithDefaults $entry
}

# Helper function to create matrix entry for collection-based split tests
function New-CollectionTestEntry {
  param(
    [Parameter(Mandatory=$true)]
    [string]$CollectionName,
    [Parameter(Mandatory=$true)]
    $Metadata,
    [Parameter(Mandatory=$true)]
    [bool]$IsUncollected
  )

  $suffix = if ($IsUncollected) { 'uncollected' } else { $CollectionName }
  $baseShortName = if ($Metadata.shortName) { $Metadata.shortName } else { $Metadata.projectName }

  $entry = [ordered]@{
    type = 'collection'
    projectName = $Metadata.projectName
    name = if ($IsUncollected) { $baseShortName } else { "$baseShortName-$suffix" }
    shortname = if ($IsUncollected) { $baseShortName } else { "$baseShortName-$suffix" }
    testProjectPath = $Metadata.testProjectPath
    workitemprefix = "$($Metadata.projectName)_$suffix"
    collection = $CollectionName
    splitTests = $true
  }

  # Use uncollected timeouts if available, otherwise use regular
  if ($IsUncollected) {
    if ($Metadata.PSObject.Properties['uncollectedTestsSessionTimeout']) {
      $entry['testSessionTimeout'] = $Metadata.uncollectedTestsSessionTimeout
    } elseif ($Metadata.PSObject.Properties['testSessionTimeout']) {
      $entry['testSessionTimeout'] = $Metadata.testSessionTimeout
    }

    if ($Metadata.PSObject.Properties['uncollectedTestsHangTimeout']) {
      $entry['testHangTimeout'] = $Metadata.uncollectedTestsHangTimeout
    } elseif ($Metadata.PSObject.Properties['testHangTimeout']) {
      $entry['testHangTimeout'] = $Metadata.testHangTimeout
    }
  } else {
    if ($Metadata.PSObject.Properties['testSessionTimeout']) { $entry['testSessionTimeout'] = $Metadata.testSessionTimeout }
    if ($Metadata.PSObject.Properties['testHangTimeout']) { $entry['testHangTimeout'] = $Metadata.testHangTimeout }
  }

  if ($Metadata.PSObject.Properties['requiresNugets']) { $entry['requiresNugets'] = $Metadata.requiresNugets }
  if ($Metadata.PSObject.Properties['requiresTestSdk']) { $entry['requiresTestSdk'] = $Metadata.requiresTestSdk }
  if ($Metadata.PSObject.Properties['requiresCliArchive']) { $entry['requiresCliArchive'] = $Metadata.requiresCliArchive }

  # Add test filter for collection-based splitting
  if ($IsUncollected) {
    $entry['extraTestArgs'] = '--filter-not-trait "Partition=*"'
  } else {
    $entry['extraTestArgs'] = "--filter-trait `"Partition=$CollectionName`""
  }

  # Add supported OSes from metadata
  if ($Metadata.PSObject.Properties['supportedOSes']) {
    $entry['supportedOSes'] = @($Metadata.supportedOSes)
  }

  # Pass through custom runners if specified
  if ($Metadata.PSObject.Properties['runners'] -and $Metadata.runners) {
    $entry['runners'] = $Metadata.runners
  }

  return Complete-EntryWithDefaults $entry
}

# Helper function to create matrix entry for class-based split tests
function New-ClassTestEntry {
  param(
    [Parameter(Mandatory=$true)]
    [string]$ClassName,
    [Parameter(Mandatory=$true)]
    $Metadata
  )

  # Extract short class name (last segment after last dot)
  $shortClassName = $ClassName.Split('.')[-1]
  $baseShortName = if ($Metadata.shortName) { $Metadata.shortName } else { $Metadata.projectName }

  $entry = [ordered]@{
    type = 'class'
    projectName = $Metadata.projectName
    name = "$baseShortName-$shortClassName"
    shortname = "$baseShortName-$shortClassName"
    testProjectPath = $Metadata.testProjectPath
    workitemprefix = "$($Metadata.projectName)_$shortClassName"
    classname = $ClassName
    splitTests = $true
  }

  if ($Metadata.PSObject.Properties['testSessionTimeout']) { $entry['testSessionTimeout'] = $Metadata.testSessionTimeout }
  if ($Metadata.PSObject.Properties['testHangTimeout']) { $entry['testHangTimeout'] = $Metadata.testHangTimeout }
  if ($Metadata.PSObject.Properties['requiresNugets']) { $entry['requiresNugets'] = $Metadata.requiresNugets }
  if ($Metadata.PSObject.Properties['requiresTestSdk']) { $entry['requiresTestSdk'] = $Metadata.requiresTestSdk }
  if ($Metadata.PSObject.Properties['requiresCliArchive']) { $entry['requiresCliArchive'] = $Metadata.requiresCliArchive }

  # Add test filter for class-based splitting
  $entry['extraTestArgs'] = "--filter-class `"$ClassName`""

  # Add supported OSes from metadata
  if ($Metadata.PSObject.Properties['supportedOSes']) {
    $entry['supportedOSes'] = @($Metadata.supportedOSes)
  }

  # Pass through custom runners if specified
  if ($Metadata.PSObject.Properties['runners'] -and $Metadata.runners) {
    $entry['runners'] = $Metadata.runners
  }

  return Complete-EntryWithDefaults $entry
}

# 1. Collect all metadata files
$metadataFiles = @(Get-ChildItem -Path $ArtifactsDir -Filter "*.tests-metadata.json" -Recurse -ErrorAction SilentlyContinue)

if ($metadataFiles.Count -eq 0) {
  Write-Warning "No test metadata files found in $ArtifactsDir"
  # Create empty canonical matrix
  $canonicalMatrix = @{
    tests = @()
  }
  $canonicalMatrix | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputMatrixFile -Encoding UTF8
  Write-Host "Created empty test matrix: $OutputMatrixFile"
  exit 0
}

Write-Host "Found $($metadataFiles.Count) test metadata file(s)"

# 2. Build matrix entries
$matrixEntries = [System.Collections.Generic.List[object]]::new()

foreach ($metadataFile in $metadataFiles) {
  $metadataObject = Get-Content -Raw -Path $metadataFile.FullName | ConvertFrom-Json

  Write-Host "Processing: $($metadataObject.projectName)"

  # Check if this is a split test with metadata
  if ($metadataObject.splitTests -eq 'true') {
    Write-Host "  → Split test (processing partitions/classes)"

    $metaFile = $metadataFile.FullName
    $partitionsFile = $metaFile -replace '\.tests-metadata\.json$', '.tests-partitions.json'

    if (-not (Test-Path $partitionsFile)) {
      throw "Test partitions file not found: $partitionsFile"
    }

    $metadata = Get-Content -Raw -Path $metaFile | ConvertFrom-Json

    # Add supported OSes to metadata from enumeration
    $metadata | Add-Member -Force -MemberType NoteProperty -Name 'supportedOSes' -Value $metadataObject.supportedOSes

    # Extract the array testPartitions
    $partitionsJson = Get-Content -Raw -Path $partitionsFile | ConvertFrom-Json
    $testPartitions = $partitionsJson.testPartitions

    $partitionCount = 0
    $classCount = 0

    foreach ($testPartition in $testPartitions) {
      $testPartition = $testPartition.Trim()
      if ([string]::IsNullOrWhiteSpace($testPartition)) { continue }

      if ($testPartition -match '^collection:(.+)$') {
        # Collection/partition entry
        $collectionName = $Matches[1]
        $entry = New-CollectionTestEntry -CollectionName $collectionName -Metadata $metadata -IsUncollected $false
        $matrixEntries.Add($entry)
        $partitionCount++
      }
      elseif ($testPartition -match '^uncollected:\*$') {
        # Uncollected tests entry
        $entry = New-CollectionTestEntry -CollectionName '*' -Metadata $metadata -IsUncollected $true
        $matrixEntries.Add($entry)
        $partitionCount++
      }
      elseif ($testPartition -match '^class:(.+)$') {
        # Class-based entry
        $className = $Matches[1]
        $entry = New-ClassTestEntry -ClassName $className -Metadata $metadata
        $matrixEntries.Add($entry)
        $classCount++
      }
    }

    Write-Host "  ✓ Added $partitionCount partition(s) and $classCount class(es)"
  }
  else {
    # Regular (non-split) test
    $entry = New-RegularTestEntry -Metadata $metadataObject
    $matrixEntries.Add($entry)
  }
}

# 3. Sort entries and output
Write-Host ""
Write-Host "Generated $($matrixEntries.Count) total matrix entries"

$sortedEntries = @($matrixEntries | Sort-Object -Property projectName, name)

$requiresNugetsCount = @($sortedEntries | Where-Object { $_.requiresNugets -eq $true }).Count
$noNugetsCount = @($sortedEntries | Where-Object { $_.requiresNugets -ne $true }).Count

Write-Host "  - Requiring NuGets: $requiresNugetsCount"
Write-Host "  - Not requiring NuGets: $noNugetsCount"

# 4. Write canonical matrix
$canonicalMatrix = [ordered]@{
  tests = $sortedEntries
}

$outputDir = [System.IO.Path]::GetDirectoryName($OutputMatrixFile)
if ($outputDir -and -not (Test-Path $outputDir)) {
  New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$canonicalMatrix | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputMatrixFile -Encoding UTF8

Write-Host ""
Write-Host "✓ Canonical matrix written to: $OutputMatrixFile"
Write-Host ""
Write-Host "Matrix build complete!"
