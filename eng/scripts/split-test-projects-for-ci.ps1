<#
.SYNOPSIS
  Discovers test partitions or classes for CI test splitting.

.DESCRIPTION
  Determines how to split a test project for parallel CI execution:

  1. Extracts partitions using ExtractTestPartitions tool:
     - Scans assembly for [Trait("Partition", "name")] attributes
     - If partitions found → partition mode
     - Fails if the tool cannot be built or run

  2. Uses class-based splitting if no partitions are found:
     - Runs --list-tests to enumerate test classes
     - Creates one entry per test class

  Outputs a .tests-partitions.json file with entries like:
    Partition mode:  ["collection:Name", ..., "uncollected:*"]
    Class mode:      ["class:Full.Namespace.ClassName", ...]

  The uncollected:* entry ensures tests without partition traits still run.

.PARAMETER TestAssemblyPath
  Path to the test assembly DLL for extracting partition attributes.

.PARAMETER RunCommand
  The command to run the test assembly (e.g., "dotnet exec <assembly>").
  Only invoked if partition extraction fails and class-based splitting is needed.

.PARAMETER TestClassNamePrefixForCI
  Namespace prefix used to recognize test classes (e.g., Aspire.Hosting.Tests).

.PARAMETER TestPartitionsJsonFile
  Path to write the .tests-partitions.json output file.

.PARAMETER RepoRoot
  Path to the repository root (for locating the ExtractTestPartitions tool).

.PARAMETER IncludeTraitFilter
  Optional. When set, uses positive trait filtering for class discovery instead
  of the default exclusion-based approach. For example, "quarantined=true" will
  run --list-tests with --filter-trait quarantined=true to discover only classes
  that contain quarantined tests. Used by specialized test runners (quarantine,
  outerloop) to build a lean matrix of only relevant classes.

.NOTES
  PowerShell 7+
  Fails fast if ExtractTestPartitions cannot be built or run.
  Fails fast if zero test classes discovered when in class mode.
  Only runs --list-tests when no partitions are found in the assembly.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)]
  [string]$TestAssemblyPath,

  [Parameter(Mandatory=$true)]
  [string]$RunCommand,

  [Parameter(Mandatory=$true)]
  [string]$TestClassNamePrefixForCI,

  [Parameter(Mandatory=$true)]
  [string]$TestPartitionsJsonFile,

  [Parameter(Mandatory=$true)]
  [string]$RepoRoot,

  [Parameter(Mandatory=$false)]
  [string]$IncludeTraitFilter = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $TestAssemblyPath)) {
  Write-Error "TestAssemblyPath not found: $TestAssemblyPath"
}

$collections = [System.Collections.Generic.HashSet[string]]::new()
$classes     = [System.Collections.Generic.HashSet[string]]::new()

# Extract partitions using the ExtractTestPartitions tool
$partitionsFile = Join-Path ([System.IO.Path]::GetTempPath()) "partitions-$([System.Guid]::NewGuid()).txt"
try {
  $toolPath = Join-Path $RepoRoot "artifacts/bin/ExtractTestPartitions/Release/net8.0/ExtractTestPartitions.dll"

  # Build the tool if it doesn't exist
  if (-not (Test-Path $toolPath)) {
    Write-Host "Building ExtractTestPartitions tool..."
    $toolProjectPath = Join-Path $RepoRoot "tools/ExtractTestPartitions/ExtractTestPartitions.csproj"
    & dotnet build $toolProjectPath -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
      Write-Error "Failed to build ExtractTestPartitions tool."
    }
  }

  if (-not (Test-Path $toolPath)) {
    Write-Error "ExtractTestPartitions tool not found at $toolPath after build."
  }

  Write-Host "Extracting partitions from assembly: $TestAssemblyPath"
  $toolOutput = & dotnet $toolPath --assembly-path $TestAssemblyPath --output-file $partitionsFile 2>&1
  $toolExitCode = $LASTEXITCODE

  # Display tool output (informational)
  if ($toolOutput) {
    $toolOutput | Write-Host
  }

  if ($toolExitCode -ne 0) {
    Write-Error "ExtractTestPartitions failed with exit code $toolExitCode."
  }

  # Read partitions if the file was created
  if (Test-Path $partitionsFile) {
    $partitionLines = Get-Content $partitionsFile -ErrorAction SilentlyContinue
    if ($partitionLines) {
      foreach ($partition in $partitionLines) {
        if (-not [string]::IsNullOrWhiteSpace($partition)) {
          $collections.Add($partition.Trim()) | Out-Null
        }
      }
      Write-Host "Found $($collections.Count) partition(s) via attribute extraction"
    }
  }
}
finally {
  # Clean up temp file
  if (Test-Path $partitionsFile) {
    Remove-Item $partitionsFile -ErrorAction SilentlyContinue
  }
}

# Determine mode: if we have partitions, use collection mode; otherwise fall back to class mode
$mode = if ($collections.Count -gt 0) { 'collection' } else { 'class' }

# Only run --list-tests if we need class-based splitting (no partitions found)
if ($mode -eq 'class') {
  Write-Host "No partitions found. Running --list-tests to extract class names..."

  # Run the test assembly with --list-tests to get all test names.
  # When IncludeTraitFilter is set, use positive filtering to discover only classes
  # with the specified trait (e.g., quarantined=true). Otherwise, use the default
  # exclusion-based approach that skips quarantined/outerloop tests.
  if ($IncludeTraitFilter) {
    Write-Host "Using positive trait filter for class discovery: $IncludeTraitFilter"
    $testOutput = & $RunCommand --filter-trait $IncludeTraitFilter --filter-not-trait category=failing --list-tests 2>&1
  } else {
    $testOutput = & $RunCommand --filter-not-trait category=failing --list-tests --filter-not-trait outerloop=true --filter-not-trait quarantined=true 2>&1
  }

  if ($LASTEXITCODE -ne 0) {
    Write-Error "Test listing command failed with exit code $LASTEXITCODE"
  }

  # Extract class names from test listing
  # Match everything up to the last segment (method name), capturing the full class name
  $classNamePattern = '^\s*(' + [Regex]::Escape($TestClassNamePrefixForCI) + '\..+)\.[^\.]+$'

  foreach ($line in $testOutput) {
    $lineStr = $line.ToString().Trim()
    # Extract class name from test name
    # Format: "Namespace.SubNs.ClassName.MethodName(...)" or "Namespace.ClassName.MethodName"
    # Strip any trailing parenthesized arguments: "Method(arg1, arg2)" → "Method"
    $cleanLine = $lineStr -replace '\(.*\)$', ''
    if ($cleanLine -match $classNamePattern) {
      $className = $Matches[1]
      $classes.Add($className) | Out-Null
    }
  }

  if ($classes.Count -eq 0) {
    if ($IncludeTraitFilter) {
      # When using positive trait filtering (specialized mode), zero classes is
      # expected — this project may not have any tests with the specified trait.
      # Write an empty partitions file so the matrix builder skips it gracefully.
      Write-Host "No test classes found matching prefix '$TestClassNamePrefixForCI' with trait filter '$IncludeTraitFilter'. This project will be skipped."
    } else {
      Write-Error "No test classes discovered matching prefix '$TestClassNamePrefixForCI'."
    }
  }
}

$lines = [System.Collections.Generic.List[string]]::new()

if ($mode -eq 'collection') {
  foreach ($c in ($collections | Sort-Object)) {
    $lines.Add("collection:$c")
  }
  $lines.Add("uncollected:*")
} else {
  foreach ($cls in ($classes | Sort-Object)) {
    $lines.Add("class:$cls")
  }
}

# Create tests partitions json file
try {
  $testPartitionsJson = @{}
  $testPartitionsJson | Add-Member -Force -MemberType NoteProperty -Name 'testPartitions' -Value @($lines)
  $testPartitionsJson | ConvertTo-Json -Depth 20 | Set-Content -Path $TestPartitionsJsonFile -Encoding UTF8
} catch {
  Write-Warning "Failed updating metadata JSON: $_"
}

Write-Host "Mode: $mode"
Write-Host "Collections discovered: $($collections.Count)"
Write-Host "Classes discovered: $($classes.Count)"
Write-Host "Test partitions JSON: $TestPartitionsJsonFile"
