<#
.SYNOPSIS
  Writes an OverrideProjectToBuild props file for split test projects that still
  need compiled class-mode discovery.

.DESCRIPTION
  Test enumeration now has a metadata/source-partition pass that does not build
  test projects. After that pass, split projects that already have a
  .tests-partitions.json file are complete:

    - partition-mode projects were discovered from [Trait("Partition", ...)]
      literals in source;
    - class-mode projects still have split metadata but no partitions file.

  This script finds those remaining class-mode projects and writes a props file
  consumed by eng/Build.props. Passing that props file via BeforeBuildPropsPath
  restricts restore/build to just the class-mode projects whose test assemblies
  must run --list-tests.

.PARAMETER ArtifactsDir
  Artifacts directory containing .tests-metadata.json and .tests-partitions.json
  files from the metadata/source-partition pass.

.PARAMETER OutputPropsPath
  Path for the generated MSBuild props file.

.PARAMETER GitHubOutputName
  Optional GitHub Actions output name. When set and GITHUB_OUTPUT exists, the
  script writes '<GitHubOutputName>=<count>'.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$ArtifactsDir,

  [Parameter(Mandatory = $true)]
  [string]$OutputPropsPath,

  [Parameter(Mandatory = $false)]
  [string]$GitHubOutputName = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Emit a fatal diagnostic as a single plain-text stderr line and exit non-zero.
#
# We deliberately bypass Write-Error here. Under PowerShell's default ConciseView, a Write-Error
# raised from a script is rendered with ANSI color codes and the message is wrapped across several
# lines at the host's buffer width (e.g. ~120 cols on a CI runner vs. the local terminal width).
# That wrapping injects newlines mid-sentence, which makes the message hard to read in CI logs and
# non-deterministic to match (the same message wraps at different points depending on the width).
# Writing the raw string to stderr keeps the diagnostic on one contiguous line in every environment.
function Write-FatalError([string]$Message) {
  [Console]::Error.WriteLine($Message)
  exit 1
}

if (-not (Test-Path $ArtifactsDir)) {
  Write-FatalError "ArtifactsDir not found: $ArtifactsDir"
}

$metadataFiles = @(Get-ChildItem -Path $ArtifactsDir -Filter '*.tests-metadata.json' -Recurse -File -ErrorAction SilentlyContinue)
$classModeProjectPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($metadataFile in $metadataFiles) {
  $metadata = Get-Content -Raw -LiteralPath $metadataFile.FullName | ConvertFrom-Json
  if ($metadata.splitTests -ne 'true' -and $metadata.splitTests -ne $true) {
    continue
  }

  $partitionsFile = $metadataFile.FullName -replace '\.tests-metadata\.json$', '.tests-partitions.json'
  if (Test-Path $partitionsFile) {
    continue
  }

  if (-not $metadata.PSObject.Properties['testProjectPath'] -or [string]::IsNullOrWhiteSpace([string]$metadata.testProjectPath)) {
    Write-FatalError "Split test metadata '$($metadataFile.FullName)' does not contain testProjectPath; cannot safely restrict class-mode restore/build."
  }

  $projectPath = ([string]$metadata.testProjectPath).Replace('\', '/').TrimStart('/')
  [void]$classModeProjectPaths.Add($projectPath)
}

$outputDir = Split-Path -Parent $OutputPropsPath
if (-not [string]::IsNullOrEmpty($outputDir) -and -not (Test-Path $outputDir)) {
  New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('<Project>')

if ($classModeProjectPaths.Count -gt 0) {
  $lines.Add('  <ItemGroup>')
  foreach ($projectPath in ($classModeProjectPaths | Sort-Object)) {
    $includePath = if ([System.IO.Path]::IsPathRooted($projectPath)) {
      $projectPath
    } else {
      "`$(RepoRoot)$projectPath"
    }

    $escapedIncludePath = [System.Security.SecurityElement]::Escape($includePath)
    $lines.Add("    <OverrideProjectToBuild Include=`"$escapedIncludePath`" />")
  }
  $lines.Add('  </ItemGroup>')
}

$lines.Add('</Project>')
$lines | Set-Content -LiteralPath $OutputPropsPath -Encoding UTF8

Write-Host "Class-mode split projects: $($classModeProjectPaths.Count)"
Write-Host "Wrote: $OutputPropsPath"

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputName) -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
  "$GitHubOutputName=$($classModeProjectPaths.Count)" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}
