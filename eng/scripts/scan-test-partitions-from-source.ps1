<#
.SYNOPSIS
  Discovers test partitions for CI splitting by scanning SOURCE (.cs) files,
  without building the test project or its dependency closure.

.DESCRIPTION
  The compiled enumeration path (split-test-projects-for-ci.ps1 +
  ExtractTestPartitions) reads [Trait("Partition", "<name>")] attributes from
  the built test assembly. Building a partition-mode project such as
  Aspire.Hosting.Tests forces a compile of its entire ProjectReference closure
  (the hosting "god-edge": ~40 integrations), which dominates the
  enumerate-tests step on CI.

  Partition trait values are plain string literals on the test classes, e.g.:

      [Trait("Partition", "5")]
      public class WithUrlsTests { ... }

  so they can be read directly from source with no compile. This script scans
  the project directory for those literals and emits the same
  .tests-partitions.json shape the compiled path produces:

      { "testPartitions": ["collection:1", ..., "collection:6", "uncollected:*"] }

  build-test-matrix.ps1 turns each "collection:N" into
  --filter-trait "Partition=N" and "uncollected:*" into
  --filter-not-trait "Partition=*", so the "uncollected" entry runs every test
  that lacks a Partition trait. That backstop means a class missed by the scan
  (or one with no trait) is never dropped — it still runs under "uncollected".

  Class-mode projects (no Partition traits in source) are NOT handled here: the
  caller falls back to the build + --list-tests path, because class lists have
  no equivalent always-runs backstop and must match the runtime exactly.

.PARAMETER ProjectDirectory
  Directory of the test project to scan (recursively) for partition traits.

.PARAMETER TestPartitionsJsonFile
  Path to write the .tests-partitions.json output file. Only written when at
  least one partition trait is found (so the caller can detect class mode by
  the file's absence).

.NOTES
  PowerShell 7+. Exit code is always 0; "no partitions found" is signalled by
  not writing the output file, not by failure.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$ProjectDirectory,

  [Parameter(Mandatory = $true)]
  [string]$TestPartitionsJsonFile
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

if (-not (Test-Path $ProjectDirectory)) {
  Write-FatalError "ProjectDirectory not found: $ProjectDirectory"
}

# Matches a Partition trait attribute, allowing arbitrary inner whitespace, an optional namespace
# qualifier, the optional 'Attribute' suffix, and a case-insensitive key -- the same shapes the
# compiled extractor (Infrastructure.Tests/ExtractTestPartitions) accepts. The compiled assembly is
# the source of truth; this source scan is only valid as a build-free shortcut if it recognises every
# form the extractor would, otherwise a class on a missed partition silently runs in NO shard.
# Examples matched:
#   [Trait("Partition", "5")]
#   [Trait( "Partition" , "BasicTests" )]
#   [Xunit.Trait("Partition", "5")]
#   [TraitAttribute("partition", "5")]
# The value is a string literal (xunit trait values are compile-time constants), so the captured set
# matches what the compiled assembly exposes. Note: this does not catch the rarer combined-attribute
# form [Trait(...), Trait(...)]; the repo uses only the standalone form today (enforced by the
# compiled extractor being the authority for split projects).
$partitionRegex = [regex]'(?i)\[\s*(?:[\w.]+\.)?Trait(?:Attribute)?\s*\(\s*"Partition"\s*,\s*"([^"]+)"\s*\)\s*\]'

$partitions = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# Skip obj/bin so generated/copied sources don't introduce phantom partitions.
$sourceFiles = Get-ChildItem -Path $ProjectDirectory -Recurse -File -Filter '*.cs' |
  Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

foreach ($file in $sourceFiles) {
  $content = Get-Content -Raw -LiteralPath $file.FullName
  foreach ($m in $partitionRegex.Matches($content)) {
    $value = $m.Groups[1].Value.Trim()
    if (-not [string]::IsNullOrWhiteSpace($value)) {
      [void]$partitions.Add($value)
    }
  }
}

if ($partitions.Count -eq 0) {
  Write-Host "No [Trait(`"Partition`", ...)] attributes found in source under '$ProjectDirectory'. Falling back to class-mode (build + --list-tests)."
  # Remove any stale output so the caller's !Exists() class-mode fallback is not
  # fooled by a partitions file left over from a previous build.
  if (Test-Path $TestPartitionsJsonFile) {
    Remove-Item -LiteralPath $TestPartitionsJsonFile -Force -ErrorAction SilentlyContinue
  }
  exit 0
}

$lines = [System.Collections.Generic.List[string]]::new()
foreach ($p in ($partitions | Sort-Object)) {
  $lines.Add("collection:$p")
}
# Always include the uncollected backstop so tests without a Partition trait still run.
$lines.Add("uncollected:*")

$outputDir = Split-Path -Parent $TestPartitionsJsonFile
if (-not [string]::IsNullOrEmpty($outputDir) -and -not (Test-Path $outputDir)) {
  New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$testPartitionsJson = @{}
$testPartitionsJson | Add-Member -Force -MemberType NoteProperty -Name 'testPartitions' -Value @($lines)
$testPartitionsJson | ConvertTo-Json -Depth 20 | Set-Content -Path $TestPartitionsJsonFile -Encoding UTF8

Write-Host "Source partition scan found $($partitions.Count) partition(s): $(( $partitions | Sort-Object ) -join ', ')"
Write-Host "Wrote: $TestPartitionsJsonFile"
exit 0
