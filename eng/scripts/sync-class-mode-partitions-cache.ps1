<#
.SYNOPSIS
  Restores/saves class-mode test-partition JSON files to/from a cache directory.

.DESCRIPTION
  Class-mode split test projects (e.g. Aspire.Cli.EndToEnd.Tests,
  Aspire.Templates.Tests) discover their per-class matrix entries by building the
  test assembly + its closure and running `--list-tests`. That build+list is the
  bulk of the enumerate-tests step.

  The set of class entries changes only when the test project's source changes, so
  the discovered .tests-partitions.json can be cached (keyed on that source) and
  reused. This script moves those files between the enumerate output location and a
  cache directory:

    -Mode restore : for every cached *.tests-partitions.json, copy it next to the
                    matching <Project>.tests-metadata.json (if a partitions file is
                    not already present). write-class-mode-test-props.ps1 then sees
                    the file and does NOT schedule that project for build + --list-tests.

    -Mode save    : copy every CLASS-MODE partitions file (one whose entries are
                    "class:..." rather than "collection:...") into the cache dir,
                    so the next run with an unchanged source hash can restore it.

  Identifying class mode by the "class:" entry content (rather than a hard-coded
  project list) keeps this independent of which projects use class mode. Partition-
  mode files ("collection:...") are produced cheaply from a source scan and are not
  cached here.

.PARAMETER Mode
  'restore' or 'save'.

.PARAMETER ArtifactsDir
  Artifacts directory holding the *.tests-metadata.json / *.tests-partitions.json files.

.PARAMETER CacheDir
  Directory backed by actions/cache that holds the cached class-mode partition files.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('restore', 'save')]
  [string]$Mode,

  [Parameter(Mandatory = $true)]
  [string]$ArtifactsDir,

  [Parameter(Mandatory = $true)]
  [string]$CacheDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-IsClassModePartitionsFile([string]$path) {
  # Class-mode files list "class:<FQN>" entries; partition-mode files list
  # "collection:<n>"/"uncollected:*". Only the former is expensive to regenerate.
  $content = Get-Content -Raw -LiteralPath $path
  return $content -match '"class:'
}

if ($Mode -eq 'restore') {
  if (-not (Test-Path $CacheDir)) {
    Write-Host "Cache dir '$CacheDir' does not exist; nothing to restore."
    return
  }
  if (-not (Test-Path $ArtifactsDir)) {
    Write-Error "ArtifactsDir not found: $ArtifactsDir"
  }

  $metadataFiles = @(Get-ChildItem -Path $ArtifactsDir -Filter '*.tests-metadata.json' -Recurse -File -ErrorAction SilentlyContinue)
  $restored = 0
  foreach ($metadataFile in $metadataFiles) {
    $metadata = Get-Content -Raw -LiteralPath $metadataFile.FullName | ConvertFrom-Json
    if ($metadata.splitTests -ne 'true' -and $metadata.splitTests -ne $true) {
      continue
    }

    $projectName = $metadataFile.Name -replace '\.tests-metadata\.json$', ''
    $partitionsFile = $metadataFile.FullName -replace '\.tests-metadata\.json$', '.tests-partitions.json'
    $cachedFile = Join-Path $CacheDir "$projectName.tests-partitions.json"

    # Only restore when the project has no partitions file yet (a source-scan partition-mode
    # project already wrote its own; class-mode projects have none after the metadata pass).
    if ((Test-Path $cachedFile) -and -not (Test-Path $partitionsFile)) {
      Copy-Item -LiteralPath $cachedFile -Destination $partitionsFile -Force
      Write-Host "Restored cached class list: $partitionsFile"
      $restored++
    }
  }
  Write-Host "Restored $restored class-mode partition file(s) from cache."
}
else {
  # save
  if (-not (Test-Path $ArtifactsDir)) {
    Write-Host "ArtifactsDir '$ArtifactsDir' does not exist; nothing to save."
    return
  }
  New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null

  $partitionFiles = @(Get-ChildItem -Path $ArtifactsDir -Filter '*.tests-partitions.json' -Recurse -File -ErrorAction SilentlyContinue)
  $saved = 0
  foreach ($partitionFile in $partitionFiles) {
    if (-not (Test-IsClassModePartitionsFile $partitionFile.FullName)) {
      continue
    }
    $projectName = $partitionFile.Name -replace '\.tests-partitions\.json$', ''
    $cachedFile = Join-Path $CacheDir "$projectName.tests-partitions.json"
    Copy-Item -LiteralPath $partitionFile.FullName -Destination $cachedFile -Force
    Write-Host "Saved class list to cache: $cachedFile"
    $saved++
  }
  Write-Host "Saved $saved class-mode partition file(s) to cache."
}
