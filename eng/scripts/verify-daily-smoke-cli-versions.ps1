# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$VersionsDir,

    [string]$StepSummaryPath = $env:GITHUB_STEP_SUMMARY
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $VersionsDir -PathType Container)) {
    Write-Host "::error::Aspire CLI version records directory was not created: $VersionsDir"
    exit 1
}

$versionFiles = @(Get-ChildItem -LiteralPath $VersionsDir -Filter '*.env' -File)

if ($versionFiles.Count -eq 0) {
    Write-Host '::error::No Aspire CLI version records were produced.'
    exit 1
}

function Read-CliVersionRecord {
    param([System.IO.FileInfo]$File)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $File.FullName) {
        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -lt 0) {
            continue
        }

        $key = $line.Substring(0, $separatorIndex)
        if ([string]::IsNullOrWhiteSpace($key)) {
            continue
        }

        $values[$key] = $line.Substring($separatorIndex + 1)
    }

    $testName = $values['test']
    $mode = $values['mode']
    $strategy = $values['strategy']
    $version = $values['version']

    $route = if (-not [string]::IsNullOrWhiteSpace($strategy)) {
        $strategy
    }
    elseif (-not [string]::IsNullOrWhiteSpace($mode)) {
        $mode
    }
    else {
        'unknown install route'
    }

    if ([string]::IsNullOrWhiteSpace($testName)) {
        $testName = $File.Name
    }

    $versionLabel = if ([string]::IsNullOrWhiteSpace($version)) {
        '(missing version)'
    }
    else {
        $version
    }

    [pscustomobject]@{
        Test = $testName
        Mode = $mode
        Strategy = $strategy
        Version = $version
        Route = $route
        Description = "$route | $testName | $versionLabel"
    }
}

function Write-FailureSummary {
    param([object[]]$Records)

    if ([string]::IsNullOrWhiteSpace($StepSummaryPath)) {
        return
    }

    $summaryLines = @(
        '## Aspire CLI version consistency check failed'
        ''
        'All daily smoke install routes should test the same Aspire CLI version.'
        ''
        '### Version records'
        ''
    )

    foreach ($record in $Records) {
        $summaryLines += "- ``$($record.Description)``"
    }

    Add-Content -LiteralPath $StepSummaryPath -Value ($summaryLines -join [Environment]::NewLine)
}

$records = @($versionFiles | ForEach-Object { Read-CliVersionRecord $_ })
$missingVersionRecords = @($records | Where-Object { [string]::IsNullOrWhiteSpace($_.Version) })

if ($missingVersionRecords.Count -gt 0) {
    Write-Host '::error::Some Aspire CLI version records did not include a version.'
    $missingVersionRecords | ForEach-Object { Write-Host "Missing version: $($_.Description)" }
    Write-FailureSummary $records
    exit 1
}

$uniqueVersions = @($records | ForEach-Object { $_.Version } | Sort-Object -Unique)
if ($uniqueVersions.Count -ne 1) {
    Write-Host "::error::Daily smoke tests installed $($uniqueVersions.Count) different Aspire CLI versions."
    Write-Host 'Installed versions:'
    $uniqueVersions | ForEach-Object { Write-Host "  $_" }
    Write-Host 'Version records:'
    $records | ForEach-Object { Write-Host "  $($_.Description)" }
    Write-FailureSummary $records
    exit 1
}

Write-Host "All daily smoke tests installed Aspire CLI version: $($uniqueVersions[0])"
