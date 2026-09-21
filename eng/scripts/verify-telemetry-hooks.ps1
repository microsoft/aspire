#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'telemetry-hooks.common.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$hooksDir = Join-Path $repoRoot 'src\Aspire.Cli\Agents\Hooks'
$metadata = Read-TelemetryHooksMetadata (Join-Path $hooksDir $script:TelemetryHooksMetadataFileName)
Assert-TelemetryHooksLocalFiles $hooksDir $metadata
$source = Get-TelemetryHooksSource $metadata.commitSha $metadata.version
Assert-TelemetryHooksSourceMetadata $metadata $source

Write-Host "Telemetry hooks verified against '$($metadata.repository)' version '$($metadata.version)' at '$($metadata.commitSha)'."
