<#
.SYNOPSIS
  Compares predicted would-run projects against actual failed test results.

.DESCRIPTION
  Reads the structured selector and matrix audit artifacts, parses downloaded .trx files,
  and reports any failing test project that was not present in the predicted would-run set.
  This script is intended for audit mode and does not fail the workflow.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory=$false)]
  [string]$SelectorActivePath,

  [Parameter(Mandatory=$false)]
  [string]$SelectorAuditPath,

  [Parameter(Mandatory=$false)]
  [string]$MatrixAuditPath,

  [Parameter(Mandatory=$true)]
  [string]$TestResultsRoot,

  [Parameter(Mandatory=$false)]
  [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-JsonFile {
  param([string]$Path)

  if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
    return $null
  }

  return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-FailedTrxFiles {
  param([string]$Root)

  if (-not (Test-Path -LiteralPath $Root)) {
    return @()
  }

  $failed = [System.Collections.Generic.List[object]]::new()
  $trxFiles = Get-ChildItem -LiteralPath $Root -Filter *.trx -File -Recurse -ErrorAction SilentlyContinue
  foreach ($trxFile in $trxFiles) {
    try {
      [xml]$trx = Get-Content -LiteralPath $trxFile.FullName
      $counters = $trx.TestRun.ResultSummary.Counters
      $failedCount = 0
      if ($null -ne $counters -and $null -ne $counters.failed) {
        $failedCount = [int]$counters.failed
      }

      if ($failedCount -gt 0) {
        $failed.Add([pscustomobject]@{
          path = $trxFile.FullName
          fileName = $trxFile.Name
          baseName = $trxFile.BaseName
          failedCount = $failedCount
        })
      }
    } catch {
      Write-Warning "Failed to parse TRX file '$($trxFile.FullName)': $_"
    }
  }

  return @($failed)
}

function Resolve-ShortNameMatch {
  param(
    [string]$BaseName,
    [System.Collections.Generic.List[object]]$Entries
  )

  foreach ($entry in $Entries) {
    if ([string]::IsNullOrWhiteSpace($entry.shortname)) {
      continue
    }

    if ($BaseName.StartsWith($entry.shortname, [System.StringComparison]::OrdinalIgnoreCase)) {
      return $entry
    }
  }

  return $null
}

$selectorActive = Get-JsonFile -Path $SelectorActivePath
$selectorAudit = Get-JsonFile -Path $SelectorAuditPath
$matrixAudit = Get-JsonFile -Path $MatrixAuditPath
$missingArtifacts = [System.Collections.Generic.List[string]]::new()
$requiredMissingArtifacts = [System.Collections.Generic.List[string]]::new()

if ($null -eq $selectorActive) {
  $missingArtifacts.Add('selector-active')
}

if ($null -eq $selectorAudit) {
  $missingArtifacts.Add('selector-audit')
  $requiredMissingArtifacts.Add('selector-audit')
}

if ($null -eq $matrixAudit) {
  $missingArtifacts.Add('matrix-audit')
  $requiredMissingArtifacts.Add('matrix-audit')
}

$activeSelectorRunAll = $null
$activeSelectorReason = $null
if ($null -ne $selectorActive) {
  $activeSelectorRunAll = [bool]$selectorActive.runAllTests
  $activeSelectorReason = $selectorActive.reason
}

$auditSelectorRunAll = $false
$auditSelectorReason = $null
if ($null -ne $selectorAudit) {
  $auditSelectorRunAll = [bool]$selectorAudit.runAllTests
  $auditSelectorReason = $selectorAudit.reason
}

$templateGate = $null
if ($null -ne $matrixAudit -and $null -ne $matrixAudit.PSObject.Properties['templateGate']) {
  $templateGate = $matrixAudit.templateGate
}

$predictedWouldRunProjects = @()
$candidateEntries = [System.Collections.Generic.List[object]]::new()

if ($null -ne $matrixAudit) {
  $predictedWouldRunProjects = @($matrixAudit.wouldRunProjects)
  foreach ($entry in @($matrixAudit.wouldRunEntries)) {
    $candidateEntries.Add($entry)
  }
}

$sortedCandidateEntries = [System.Collections.Generic.List[object]]::new()
foreach ($entry in @($candidateEntries | Sort-Object @{ Expression = { $_.shortname.Length }; Descending = $true }, shortname)) {
  $sortedCandidateEntries.Add($entry)
}

$failedTrxFiles = @(Get-FailedTrxFiles -Root $TestResultsRoot)
$failedProjects = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$auditMisses = [System.Collections.Generic.List[object]]::new()
$unmappedFailedResults = [System.Collections.Generic.List[object]]::new()

foreach ($trxFile in $failedTrxFiles) {
  $matchedEntry = Resolve-ShortNameMatch -BaseName $trxFile.baseName -Entries $sortedCandidateEntries
  if ($null -eq $matchedEntry -or [string]::IsNullOrWhiteSpace($matchedEntry.testProjectPath)) {
    $unmappedFailedResults.Add([pscustomobject]@{
      trxFile = $trxFile.fileName
      failedCount = $trxFile.failedCount
    })
    continue
  }

  [void]$failedProjects.Add($matchedEntry.testProjectPath)

  if (-not $auditSelectorRunAll -and $predictedWouldRunProjects -notcontains $matchedEntry.testProjectPath) {
    $auditMisses.Add([pscustomobject]@{
      trxFile = $trxFile.fileName
      shortname = $matchedEntry.shortname
      testProjectPath = $matchedEntry.testProjectPath
      failedCount = $trxFile.failedCount
    })
  }
}

$status = 'ok'
if ($requiredMissingArtifacts.Count -gt 0) {
  $status = 'missing_audit_data'
} elseif ($failedTrxFiles.Count -eq 0) {
  $status = 'no_failed_tests'
} elseif ($unmappedFailedResults.Count -gt 0) {
  $status = 'unmapped_failed_results'
}

$result = [pscustomobject]@{
  status = $status
  hasAuditMiss = ($auditMisses.Count -gt 0)
  hasDataIssues = ($missingArtifacts.Count -gt 0 -or $unmappedFailedResults.Count -gt 0)
  selectorRunAll = $auditSelectorRunAll
  selectorReason = $auditSelectorReason
  activeSelectorRunAll = $activeSelectorRunAll
  activeSelectorReason = $activeSelectorReason
  auditSelectorRunAll = $auditSelectorRunAll
  auditSelectorReason = $auditSelectorReason
  templateGate = $templateGate
  predictedWouldRunProjects = @($predictedWouldRunProjects | Sort-Object)
  failedProjects = @(@($failedProjects) | Sort-Object)
  auditMisses = @($auditMisses)
  unmappedFailedResults = @($unmappedFailedResults)
  missingArtifacts = @($missingArtifacts)
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
  $outputDirectory = Split-Path -Path $OutputPath -Parent
  if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
  }

  $result | ConvertTo-Json -Depth 10 | Out-File -FilePath $OutputPath -Encoding utf8
  Write-Host "✓ Audit miss report written to $OutputPath"
}

$result | ConvertTo-Json -Depth 10
