#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Simulate an official STABLE-shaped staging build (e.g. 13.4.0) and validate
    that the CLI resolves Aspire.* from its SHA-specific darc feed.

.DESCRIPTION
    This is the scenario from https://github.com/microsoft/aspire/issues/17527:
    a stable-shaped release-branch build still resolves from its own darc feed
    (quality=Stable), not nuget.org.

    See docs/cli-staging-validation.md for the full validation matrix.

.PARAMETER Sha
    Commit hash of the darc feed to target (>= 8 hex chars). Use a real
    release-branch build commit to actually restore packages; any 8+ hex value
    is fine for inspecting/asserting the resolved feed URL.

.PARAMETER Pr
    Install that PR's build first (via get-aspire-cli-pr.ps1) and target it.
    Omit to use an already-installed CLI.

.PARAMETER Cli
    Path to the aspire CLI to drive. Default: 'aspire' on PATH, else
    ~/.aspire/bin/aspire.

.PARAMETER Version
    Override the informational version (without +<sha>). Default: 13.4.0.

.PARAMETER Identity
    Override the identity used for staging-feed routing. Default: staging.

.PARAMETER Package
    Package to use for the validation 'aspire add'. Default: foundry.

.PARAMETER PassThrough
    Extra arguments passed through to the aspire invocation.

.EXAMPLE
    ./debug-stable.ps1 -Sha 1a2b3c4d5e6f7a8b

.EXAMPLE
    ./debug-stable.ps1 -Pr 17743 -Sha 1a2b3c4d5e6f7a8b
#>

[CmdletBinding()]
param(
    [string]$Sha,
    [string]$Pr,
    [string]$Cli,
    [string]$Version,
    [string]$Identity = 'staging',
    [string]$Package = 'foundry',
    [switch]$Shell,
    [switch]$PrintEnv,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$PassThrough = @()
)

. (Join-Path (Split-Path -Parent $PSCommandPath) 'debug-aspire-channel.ps1')

Invoke-DebugChannel -Kind 'stable' -Sha $Sha -Pr $Pr -Cli $Cli -Version $Version `
    -Identity $Identity -Package $Package -Shell:$Shell -PrintEnv:$PrintEnv -PassThrough $PassThrough
