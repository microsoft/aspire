# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

<#
.SYNOPSIS
Validates the npm ESRP owner and approver aliases used by the release pipeline.

.DESCRIPTION
The Aspire release pipeline (eng/pipelines/release-publish-nuget.yml) submits the
@microsoft/aspire-cli npm packages through MicroBuild's ESRP publish template. ESRP
requires a set of owner aliases and a single approver alias. This script normalizes
those values, enforces the release rules (owners must be a single alias that is one of
the required owner aliases, approvers must be a single alias, and the two must not be
the same alias), and emits the normalized ("effective") sets so the pipeline can forward
them to the publish template.

The release job runs with `checkout: none`, so the pipeline cannot dot-source this file
at runtime. The helper functions are mirrored inline in the pipeline YAML and the
ReleasePublishNugetPipelineTests.NpmAliasValidationHelpersMatchScript test keeps the two
copies in sync. This script exists so the same logic can be executed directly in unit
tests (ValidateNpmReleaseAliasesTests).

Dot-source the script to import only the helper functions without running validation:

    . ./validate-npm-release-aliases.ps1

.PARAMETER Owners
A single owner alias or @microsoft.com email address. Defaults to the
NPM_PUBLISH_OWNERS environment variable.

.PARAMETER Approvers
A single approver alias or @microsoft.com email address. Defaults to the
NPM_PUBLISH_APPROVERS environment variable.

.PARAMETER RequiredOwners
Comma-separated list of owner aliases; the single owner must be one of these.
Defaults to the NPM_PUBLISH_REQUIRED_OWNERS environment variable.
#>
[CmdletBinding()]
param(
    [string] $Owners = $env:NPM_PUBLISH_OWNERS,
    [string] $Approvers = $env:NPM_PUBLISH_APPROVERS,
    [string] $RequiredOwners = $env:NPM_PUBLISH_REQUIRED_OWNERS
)

# >>> BEGIN npm release alias helpers (keep in sync with eng/pipelines/release-publish-nuget.yml) >>>
function Format-NpmReleaseAliasForError([string] $value) {
  $escaped = $value.Replace("`r", '\r').Replace("`n", '\n')
  return [regex]::Replace($escaped, '##vso\[', '## vso[', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

function ConvertTo-NpmReleaseAliasSet([string] $value, [string] $parameterName) {
  $aliases = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

  # Parse values supplied as:
  #   joperezr, ankj@microsoft.com
  # The normalized aliases are later emitted in Azure Pipelines logging commands, so
  # accept only a small single-line alias alphabet before writing them back to the log.
  foreach ($entry in $value -split ',') {
    $alias = $entry.Trim()
    if ([string]::IsNullOrWhiteSpace($alias)) {
      continue
    }

    $originalAlias = $alias
    if ($alias.EndsWith('@microsoft.com', [StringComparison]::OrdinalIgnoreCase)) {
      $alias = $alias.Substring(0, $alias.Length - '@microsoft.com'.Length)
    } elseif ($alias.Contains('@')) {
      Write-Error "$parameterName entry '$(Format-NpmReleaseAliasForError $originalAlias)' must be a Microsoft alias or @microsoft.com email address."
      exit 1
    }

    if ([string]::IsNullOrWhiteSpace($alias) -or $alias -notmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*\z') {
      Write-Error "$parameterName entry '$(Format-NpmReleaseAliasForError $originalAlias)' must be a non-empty Microsoft alias or @microsoft.com email address containing only letters, digits, '.', '_' or '-'."
      exit 1
    }

    [void]$aliases.Add($alias.ToLowerInvariant())
  }

  return ,$aliases
}

function Assert-SingleNpmReleaseAlias(
  [System.Collections.Generic.HashSet[string]] $actualAliases,
  [string] $parameterName) {
  if ($actualAliases.Count -ne 1) {
    Write-Error "$parameterName must contain exactly one Microsoft alias or @microsoft.com email address."
    exit 1
  }
}

function Assert-ContainsAnyRequiredNpmOwnerAlias(
  [System.Collections.Generic.HashSet[string]] $actualAliases,
  [System.Collections.Generic.HashSet[string]] $requiredAliases,
  [string] $parameterName) {
  $matches = @($requiredAliases | Where-Object { $actualAliases.Contains($_) })
  if ($matches.Count -eq 0) {
    $requiredAliasList = ($requiredAliases | Sort-Object) -join ', '
    Write-Error "$parameterName must include at least one required ESRP owner alias: $requiredAliasList."
    exit 1
  }
}

function Invoke-NpmReleaseAliasValidation(
  [string] $owners,
  [string] $approvers,
  [string] $requiredNpmOwnersValue) {
  $requiredNpmOwners = ConvertTo-NpmReleaseAliasSet $requiredNpmOwnersValue 'NPM_PUBLISH_REQUIRED_OWNERS'
  $normalizedOwners = ConvertTo-NpmReleaseAliasSet $owners 'NpmPublishOwners'
  $normalizedApprovers = ConvertTo-NpmReleaseAliasSet $approvers 'NpmPublishApprovers'

  if ($normalizedOwners.Count -eq 0) {
    Write-Error "NpmPublishOwners must contain at least one alias before publishing npm packages."
    exit 1
  }

  if ($normalizedApprovers.Count -eq 0) {
    Write-Error "NpmPublishApprovers must contain at least one alias before publishing npm packages."
    exit 1
  }

  # ESRP accepts multiple owners, but the release process requires exactly one so that
  # ownership of the @microsoft/aspire-cli package maps to a single accountable alias.
  Assert-SingleNpmReleaseAlias $normalizedOwners 'NpmPublishOwners'
  Assert-ContainsAnyRequiredNpmOwnerAlias $normalizedOwners $requiredNpmOwners 'NpmPublishOwners'
  Assert-SingleNpmReleaseAlias $normalizedApprovers 'NpmPublishApprovers'

  $overlappingAliases = @($normalizedOwners | Where-Object { $normalizedApprovers.Contains($_) })
  if ($overlappingAliases.Count -gt 0) {
    Write-Error "NpmPublishOwners and NpmPublishApprovers must not contain the same alias(es): $($overlappingAliases -join ', ')."
    exit 1
  }

  $effectiveOwners = ($normalizedOwners | Sort-Object) -join ','
  $effectiveApprovers = ($normalizedApprovers | Sort-Object) -join ','

  Write-Host "##vso[task.setvariable variable=NpmPublishOwnersEffective]$effectiveOwners"
  Write-Host "##vso[task.setvariable variable=NpmPublishApproversEffective]$effectiveApprovers"
  Write-Host "npm ESRP owners and approvers were resolved and include the required release contacts."
}
# <<< END npm release alias helpers <<<

# Importing the helpers (dot-sourcing) should not trigger validation. When the script is
# dot-sourced, $MyInvocation.InvocationName is '.'; when it is run via -File it is the
# script path. See https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_scripts#script-scope-and-dot-sourcing
if ($MyInvocation.InvocationName -eq '.') {
  return
}

Invoke-NpmReleaseAliasValidation $Owners $Approvers $RequiredOwners
