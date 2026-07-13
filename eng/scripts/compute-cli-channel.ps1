# Computes the Aspire CLI channel that gets baked into the native binary as
# the AspireCliChannel MSBuild property. The resolved value drives the staging
# vs stable feed-resolution behavior in PackagingService at CLI startup, so the
# same accepted set (stable | staging | daily | pr-<N>) that
# IdentityChannelReader.IsValidChannel enforces at runtime is enforced here.
#
# Consumed by eng/pipelines/templates/build_sign_native.yml: the YAML step
# resolves DotNetFinalVersionKind from eng/Versions.props, then invokes this
# script. AzDO callers pick up the resolved channel via the
# `##vso[task.setvariable variable=aspireCliChannel]` logging command; other
# callers (unit tests, ad-hoc dev runs) consume the final `Write-Output` line.
#
# See https://github.com/microsoft/aspire/issues/17527 for the bug whose fix
# required this cascade reorder, and the accompanying ComputeCliChannelTests
# for the cases this script must continue to satisfy.

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Reason,

  [Parameter(Mandatory = $true)]
  [string]$SourceBranch,

  [string]$PrNumber = '',

  [string]$Override = 'auto',

  [Parameter(Mandatory = $true)]
  [string]$VersionKind
)

$ErrorActionPreference = 'Stop'

Write-Host "Build.Reason: '$Reason'"
Write-Host "Build.SourceBranch: '$SourceBranch'"
Write-Host "System.PullRequest.PullRequestNumber: '$PrNumber'"
Write-Host "aspireCliChannelOverride: '$Override'"
Write-Host "DotNetFinalVersionKind: '$VersionKind'"

if ($Override -and $Override -ne 'auto') {
  # Operator override path. Validate against the same accepted set that
  # IdentityChannelReader.IsValidChannel enforces at CLI startup so a typo
  # here fails the pipeline step rather than producing a binary that refuses
  # to boot. pr-<N> is intentionally excluded from the override set —
  # PR builds always come from the PullRequest reason arm below.
  if ($Override -notin @('stable', 'staging', 'daily')) {
    throw "aspireCliChannelOverride='$Override' is not one of: auto, stable, staging, daily."
  }
  # Normalize after validation: PowerShell's `-notin` above is case-insensitive
  # by default, but the runtime `IdentityChannelReader.IsValidChannel` is
  # case-sensitive — without this, a capitalized override would build cleanly
  # but produce a binary that throws at startup with
  # "Assembly metadata 'AspireCliChannel' has invalid value 'Stable'".
  $channel = $Override.ToLowerInvariant()
}
elseif ($Reason -eq 'PullRequest') {
  # Defense in depth: validate digit-only PR number rather than just
  # non-emptiness. If the agent ever returns the literal macro string
  # (e.g. '$(System.PullRequest.PullRequestNumber)' unresolved) this catches
  # it at compute time rather than letting an invalid AspireCliChannel value
  # reach the build and be rejected later by IdentityChannelReader.IsValidChannel
  # — clearer failure attribution.
  if ($PrNumber -notmatch '^\d+$') {
    throw "Build.Reason is 'PullRequest' but System.PullRequest.PullRequestNumber was not a numeric PR number: '$PrNumber'."
  }
  # Bake the resolved hive label directly into AspireCliChannel. The CLI
  # consumes this verbatim and avoids the legacy "pr" + parsed-PrNumber join.
  $channel = "pr-$PrNumber"
} elseif ($SourceBranch -match '^refs/heads/(release|internal/release)/') {
  # Release/internal-release branches always produce staging artifacts —
  # they are published to the staging feed for dogfooding and only later
  # promoted to nuget.org. This must be checked BEFORE the
  # `versionKind == release` arm, because a release-branch build also sets
  # StabilizePackageVersion=true (→ DotNetFinalVersionKind=release) once we
  # are stabilizing for ship. Without this ordering, the stabilized staging
  # build would bake AspireCliChannel=stable and `aspire init` would drop a
  # nuget.config with no staging feed mapping, causing `aspire add` to
  # resolve Aspire.* packages from nuget.org (older versions) or fail to
  # resolve the +sha-pinned Aspire.AppHost.Sdk.
  # See https://github.com/microsoft/aspire/issues/17527.
  $channel = 'staging'
} elseif ($VersionKind -eq 'release') {
  $channel = 'stable'
} else {
  # main and any other branch fall through to daily
  $channel = 'daily'
}

Write-Host "Aspire CLI channel: $channel"
# AzDO logging command for build_sign_native.yml: subsequent steps in the
# same job read the resolved value via $(aspireCliChannel). Non-AzDO callers
# (tests, ad-hoc dev runs) ignore the prefix.
Write-Host "##vso[task.setvariable variable=aspireCliChannel]$channel"
# Tag the source build with the resolved channel so release-publish-nuget
# can verify the channel of the build it's about to ship without inspecting
# binary metadata. The tag shape mirrors the existing `release-version - X.Y.Z`
# tag emitted by azure-pipelines.yml so the consumer can use the same
# tag-fetch REST API call. `build.addbuildtag` is idempotent across jobs
# in the same build, so the per-RID `build_sign_native` invocations all
# setting the same tag is safe — AzDO dedupes them on the build.
Write-Host "##vso[build.addbuildtag]aspire-cli-channel - $channel"
# Emit the channel on stdout so callers can capture it via $(pwsh -File ...)
# without parsing Write-Host diagnostics or AzDO logging-command prefixes.
Write-Output $channel
