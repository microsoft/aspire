#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, HelpMessage = "Pull request number used to select the PR dogfood channel")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$PRNumber,

    [Parameter(HelpMessage = "Maximum number of seconds allowed for aspire start to complete")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$MaxStartupSeconds = 120,

    [Parameter(HelpMessage = "Maximum number of seconds to wait for each expected resource to reach the requested status")]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$ResourceReadyTimeoutSeconds = 120,

    [Parameter(HelpMessage = "Directory used to store starter validation projects and diagnostics")]
    [string]$ValidationRoot = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$scriptDirectory = Split-Path -Parent $PSCommandPath
$nodeHarnessPath = Join-Path $scriptDirectory 'cli-platform-smoke/cli-platform-smoke.js'

if (-not (Test-Path $nodeHarnessPath))
{
    throw "Could not find the node-pty smoke harness at '$nodeHarnessPath'."
}

$arguments = @(
    $nodeHarnessPath,
    '--pr-number', $PRNumber,
    '--max-startup-seconds', $MaxStartupSeconds,
    '--resource-ready-timeout-seconds', $ResourceReadyTimeoutSeconds
)

if (-not [string]::IsNullOrWhiteSpace($ValidationRoot))
{
    $arguments += @('--validation-root', $ValidationRoot)
}

& node @arguments
