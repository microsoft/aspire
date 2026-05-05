<#
.SYNOPSIS
  Filters test matrices to only include entries whose test project is in the affected list.

.DESCRIPTION
  Takes one or more test matrix JSON strings and an affected test projects list.
  Returns filtered matrices containing only entries whose testProjectPath appears
  in the affected list. Supports audit mode (log-only, no filtering).

  When -RunAll is true or the affected projects list is empty, all entries pass through.

.PARAMETER Matrices
  Hashtable mapping matrix names to their JSON strings.
  Example: @{ 'tests_matrix_no_nugets' = '{"include":[...]}' }

.PARAMETER AffectedProjects
  JSON array string of affected test project .csproj paths.
  Example: '["tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj"]'

.PARAMETER RunAll
  If true, skip filtering and pass through all entries unchanged.

.PARAMETER DefaultCoverageProjects
  JSON array string of test project .csproj paths that should stay in the matrix even
  when they are not directly affected. These entries are reduced to the preferred
  runner unless they are also directly affected.

.PARAMETER DefaultCoverageRunsOn
  Runner name used for default coverage entries that are not directly affected.

.PARAMETER AuditOnly
  If set, log what would be filtered but return unfiltered matrices.

.PARAMETER OutputToGitHubEnv
  If set, outputs filtered matrices to GITHUB_OUTPUT environment file.

.NOTES
  PowerShell 7+
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)]
  [hashtable]$Matrices,

  [Parameter(Mandatory=$false)]
  [string]$AffectedProjects = "[]",

  [Parameter(Mandatory=$false)]
  [switch]$RunAll,

  [Parameter(Mandatory=$false)]
  [string]$DefaultCoverageProjects = "[]",

  [Parameter(Mandatory=$false)]
  [string]$DefaultCoverageRunsOn = "ubuntu-latest",

  [Parameter(Mandatory=$false)]
  [switch]$AuditOnly,

  [Parameter(Mandatory=$false)]
  [switch]$OutputToGitHubEnv,

  [Parameter(Mandatory=$false)]
  [string]$AuditFilePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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

function Parse-ProjectListJson {
  param(
    [Parameter(Mandatory=$true)]
    [string]$Value,

    [Parameter(Mandatory=$true)]
    [string]$ParameterName
  )

  if (-not $Value -or $Value -eq "[]") {
    return @()
  }

  try {
    $parsed = $Value | ConvertFrom-Json -NoEnumerate
  } catch {
    throw "$ParameterName must be a JSON array string. Received: $Value"
  }

  if ($null -eq $parsed) {
    return @()
  }

  if ($parsed -isnot [System.Collections.IEnumerable] -or $parsed -is [string] -or $parsed -is [System.ValueType]) {
    throw "$ParameterName must be a JSON array string. Received a value of type $(Get-FriendlyTypeName $parsed)."
  }

  return @($parsed)
}

# Parse affected projects
$affected = @()
if ($AffectedProjects -and $AffectedProjects -ne "[]") {
  $affected = Parse-ProjectListJson -Value $AffectedProjects -ParameterName 'AffectedProjects'
}

$defaultCoverage = @()
if ($DefaultCoverageProjects -and $DefaultCoverageProjects -ne "[]") {
  $defaultCoverage = Parse-ProjectListJson -Value $DefaultCoverageProjects -ParameterName 'DefaultCoverageProjects'
}

# Normalize paths for comparison (forward slashes, case-insensitive)
$affectedSet = [System.Collections.Generic.HashSet[string]]::new(
  [StringComparer]::OrdinalIgnoreCase
)
foreach ($path in $affected) {
  if ([string]::IsNullOrWhiteSpace($path)) {
    continue
  }

  $normalizedPath = $path -replace '\\', '/'
  [void]$affectedSet.Add($normalizedPath)
}

$defaultCoverageSet = [System.Collections.Generic.HashSet[string]]::new(
  [StringComparer]::OrdinalIgnoreCase
)
foreach ($path in $defaultCoverage) {
  if ([string]::IsNullOrWhiteSpace($path)) {
    continue
  }

  $normalizedPath = $path -replace '\\', '/'
  [void]$defaultCoverageSet.Add($normalizedPath)
}

Write-Host "Affected test projects: $($affectedSet.Count)"
if ($affectedSet.Count -gt 0 -and $affectedSet.Count -le 20) {
  foreach ($p in $affectedSet) {
    Write-Host "  - $p"
  }
}

Write-Host "Default coverage test projects: $($defaultCoverageSet.Count)"
if ($defaultCoverageSet.Count -gt 0 -and $defaultCoverageSet.Count -le 20) {
  foreach ($p in $defaultCoverageSet) {
    Write-Host "  - $p"
  }
}

$skipFiltering = $false
if ($RunAll) {
  Write-Host "RunAll=true — skipping matrix filtering"
  $skipFiltering = $true
} elseif ($affectedSet.Count -eq 0 -and $defaultCoverageSet.Count -eq 0) {
  Write-Host "No affected projects — skipping matrix filtering (pass-through)"
  $skipFiltering = $true
}

$results = @{}
$auditLog = @()
$auditWouldRunProjects = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$auditWouldRunEntries = [System.Collections.Generic.List[object]]::new()
$auditFilteredEntries = [System.Collections.Generic.List[object]]::new()
$matrixAudit = [ordered]@{}
$templateGateProjectPath = 'tests/Aspire.Templates.Tests/Aspire.Templates.Tests.csproj'

function New-AuditEntry {
  param(
    [string]$MatrixName,
    [object]$Entry
  )

  $testProjectPath = ''
  if (-not [string]::IsNullOrWhiteSpace($Entry.testProjectPath)) {
    $testProjectPath = $Entry.testProjectPath -replace '\\', '/'
  }

  [pscustomobject]@{
    matrixName = $MatrixName
    shortname = $Entry.shortname
    testProjectPath = $testProjectPath
  }
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

foreach ($matrixName in $Matrices.Keys) {
  $matrixJson = $Matrices[$matrixName]
  $matrix = $matrixJson | ConvertFrom-Json
  $entries = @(Get-MatrixEntries -Matrix $matrix -MatrixName $matrixName)

  if ($skipFiltering) {
    $results[$matrixName] = ConvertTo-Json @{ include = @($entries) } -Compress -Depth 10
    foreach ($entry in $entries) {
      if ([string]::IsNullOrWhiteSpace($entry.testProjectPath)) {
        continue
      }

      $testPath = ($entry.testProjectPath -replace '\\', '/')
      [void]$auditWouldRunProjects.Add($testPath)
      $auditWouldRunEntries.Add((New-AuditEntry -MatrixName $matrixName -Entry $entry))
    }
    $matrixAudit[$matrixName] = [pscustomobject]@{
      inputCount = $entries.Count
      outputCount = $entries.Count
      keptCount = $entries.Count
      removedCount = 0
      mode = 'pass-through'
    }
    Write-Host "${matrixName}: $($entries.Count) entries (pass-through)"
    continue
  }

  $kept = @()
  $removed = @()

  foreach ($entry in $entries) {
    if (-not $entry.testProjectPath) {
      $removed += $entry
      continue
    }
    $testPath = ($entry.testProjectPath -replace '\\', '/')
    $isDirectlyAffected = $affectedSet.Contains($testPath)
    $isDefaultCoverage = $defaultCoverageSet.Contains($testPath)

    if ($isDirectlyAffected -or $isDefaultCoverage) {
      $runnerName = if ($entry.PSObject.Properties.Name -contains 'runs-on') { [string]$entry.'runs-on' } else { '' }
      if ($isDefaultCoverage -and -not $isDirectlyAffected -and -not [string]::IsNullOrWhiteSpace($runnerName) -and $runnerName -ne $DefaultCoverageRunsOn) {
        $removed += $entry
        $auditFilteredEntries.Add((New-AuditEntry -MatrixName $matrixName -Entry $entry))
        continue
      }

      $kept += $entry
      [void]$auditWouldRunProjects.Add($testPath)
      $auditWouldRunEntries.Add((New-AuditEntry -MatrixName $matrixName -Entry $entry))
    } else {
      $removed += $entry
      $auditFilteredEntries.Add((New-AuditEntry -MatrixName $matrixName -Entry $entry))
    }
  }

  if ($removed.Count -gt 0) {
    Write-Host "${matrixName}: $($entries.Count) → $($kept.Count) entries ($($removed.Count) filtered)"
    foreach ($r in $removed) {
      $auditLog += "  [${matrixName}] REMOVE: $($r.testProjectPath) ($($r.shortname))"
      Write-Host "  - filtered: $($r.shortname)"
    }
  } else {
    Write-Host "${matrixName}: $($entries.Count) entries (all match)"
  }

  if ($AuditOnly) {
    # In audit mode, return unfiltered matrix
    $results[$matrixName] = ConvertTo-Json @{ include = @($entries) } -Compress -Depth 10
  } else {
    $results[$matrixName] = ConvertTo-Json @{ include = @($kept) } -Compress -Depth 10
  }

  $matrixAudit[$matrixName] = [pscustomobject]@{
    inputCount = $entries.Count
    outputCount = $(if ($AuditOnly) { $entries.Count } else { $kept.Count })
    keptCount = $kept.Count
    removedCount = $removed.Count
    mode = $(if ($AuditOnly) { 'audit' } else { 'filtered' })
  }
}

# Write audit summary if applicable
if ($AuditOnly -and $auditWouldRunProjects.Count -gt 0) {
  Write-Host ""
  Write-Host "=== AUDIT MODE: Projects that would run after filtering ==="
  foreach ($project in @($auditWouldRunProjects) | Sort-Object) {
    Write-Host "  [WOULD RUN] $project"
  }
}

if ($auditLog.Count -gt 0) {
  if ($AuditOnly) {
    Write-Host ""
    Write-Host "=== AUDIT MODE: The following would be filtered (not applied) ==="
  } else {
    Write-Host ""
    Write-Host "=== Filtered entries ==="
  }
  foreach ($line in $auditLog) {
    Write-Host $line
  }
}

# Write structured audit output if requested
$sortedAffectedProjects = @(@($affectedSet) | Sort-Object)
$sortedWouldRunProjects = @(@($auditWouldRunProjects) | Sort-Object)
$sortedWouldRunEntries = @($auditWouldRunEntries | Sort-Object testProjectPath, shortname, matrixName)
$sortedFilteredEntries = @($auditFilteredEntries | Sort-Object testProjectPath, shortname, matrixName)

$auditPayload = [pscustomobject]@{
  runAll = [bool]$RunAll
  auditOnly = [bool]$AuditOnly
  skipFiltering = $skipFiltering
  affectedProjects = $sortedAffectedProjects
  defaultCoverageProjects = @(@($defaultCoverageSet) | Sort-Object)
  wouldRunProjects = $sortedWouldRunProjects
  wouldRunEntries = $sortedWouldRunEntries
  filteredEntries = $sortedFilteredEntries
  templateGate = [pscustomobject]@{
    projectPath = $templateGateProjectPath
    wouldRun = $auditWouldRunProjects.Contains($templateGateProjectPath)
  }
  matrices = [pscustomobject]$matrixAudit
}

if (-not [string]::IsNullOrWhiteSpace($AuditFilePath)) {
  $auditDirectory = Split-Path -Path $AuditFilePath -Parent
  if (-not [string]::IsNullOrWhiteSpace($auditDirectory)) {
    New-Item -ItemType Directory -Path $auditDirectory -Force | Out-Null
  }

  $auditPayload | ConvertTo-Json -Depth 10 | Out-File -FilePath $AuditFilePath -Encoding utf8
  Write-Host "✓ Matrix audit written to $AuditFilePath"
}

# Output
if ($OutputToGitHubEnv) {
  if ($env:GITHUB_OUTPUT) {
    foreach ($name in $results.Keys) {
      "$name=$($results[$name])" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
    $auditWouldRunProjectsJson = ConvertTo-Json $sortedWouldRunProjects -Compress
    "audit_would_run_projects=$auditWouldRunProjectsJson" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    Write-Host "✓ Filtered matrices written to GITHUB_OUTPUT"
  } else {
    Write-Error "GITHUB_OUTPUT environment variable not set"
    exit 1
  }
} else {
  # Output to console for debugging/testing
  foreach ($name in $results.Keys) {
    Write-Host "${name}: $($results[$name])"
  }
}
