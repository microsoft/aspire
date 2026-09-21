#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateNotNullOrEmpty()][string]$BaselineMetadataPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'telemetry-hooks.common.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$hooksDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\Hooks'
Update-TelemetryHooks -Directory $hooksDir @PSBoundParameters
