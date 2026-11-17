# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
#
# Validates the evidence from a Microsoft.Testing.Platform run before
# run-tests.yml treats recognized test outcomes as non-blocking.
#
# Inputs:
# - TestResultsPath: directory containing TRX files.
# - ExitCodePath: file containing the test command's numeric exit code.
# - AllowZeroTests: explicitly permits successful runs with valid TRX evidence
#   and zero executed tests for specialized trait-filter lanes.
# Output: writes validation details and GitHub Actions annotations to stdout. It
# does not create or modify result files.
# Exit: 0 only when the exit code and available evidence satisfy the policy;
# otherwise 1 so setup, runner, malformed-result, and unexpected empty-run
# failures remain blocking.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TestResultsPath,

    [Parameter(Mandatory = $true)]
    [string] $ExitCodePath,

    [switch] $AllowZeroTests
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) {
    Write-Host "::error::$Message"
    exit 1
}

# https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-troubleshooting#exit-codes
# Only known test outcomes are non-blocking: 0 (success), 2 (test failure),
# 3 (session aborted), 7 (test host crash), and 13 (--maximum-failed-tests reached).
# Every recognized outcome still requires valid TRX evidence of executed tests.
# Fail closed for every other code so project-level opt-in cannot hide runner,
# configuration, or future unclassified failures.
if (-not (Test-Path -LiteralPath $ExitCodePath)) {
    Fail "No test exit code file found at $ExitCodePath. The runner outcome cannot be classified safely."
}

$rawExitCode = (Get-Content -LiteralPath $ExitCodePath -Raw).Trim()
$mtpExitCode = 0
if (-not [int]::TryParse($rawExitCode, [ref]$mtpExitCode)) {
    Fail "Test exit code '$rawExitCode' is not a valid integer."
}

$allowedTestOutcomeCodes = @(0, 2, 3, 7, 13)
if ($allowedTestOutcomeCodes -notcontains $mtpExitCode) {
    Fail "Test runner exited with unclassified code $mtpExitCode. Failing the job even though test failures are non-blocking."
}
Write-Host "Test runner exit code: $mtpExitCode (classified as a known test outcome)"

$trxFiles = Get-ChildItem -LiteralPath $TestResultsPath -Filter *.trx -Recurse -ErrorAction SilentlyContinue
if ($trxFiles.Count -eq 0) {
    Fail "No .trx files found. Tests may not have run due to infrastructure issues."
}

Write-Host "Found $($trxFiles.Count) .trx file(s)"

$validFileCount = 0
$totalTestCount = 0
$totalExecutedTestCount = 0
foreach ($trxFile in $trxFiles) {
    Write-Host "Checking $($trxFile.Name)..."

    try {
        [xml]$trxContent = Get-Content -LiteralPath $trxFile.FullName -Raw
        if ($trxContent -and $trxContent.TestRun) {
            $countersNode = $trxContent.TestRun.ResultSummary.Counters
            if (-not $countersNode -or $null -eq $countersNode.total) {
                Fail "TRX file $($trxFile.Name) does not contain a Counters total value."
            }

            $testCount = 0
            if (-not [int]::TryParse([string]$countersNode.total, [ref]$testCount) -or $testCount -lt 0) {
                Fail "TRX file $($trxFile.Name) has invalid test count '$($countersNode.total)'. Counters total must be a nonnegative integer."
            }

            if ($null -eq $countersNode.executed) {
                Fail "TRX file $($trxFile.Name) does not contain a Counters executed value."
            }

            $executedTestCount = 0
            if (-not [int]::TryParse([string]$countersNode.executed, [ref]$executedTestCount) -or
                $executedTestCount -lt 0 -or
                $executedTestCount -gt $testCount) {
                Fail "TRX file $($trxFile.Name) has invalid executed test count '$($countersNode.executed)'. Counters executed must be an integer between zero and total."
            }

            $validFileCount++
            $totalTestCount += $testCount
            $totalExecutedTestCount += $executedTestCount
            Write-Host "  Tests in file: $executedTestCount executed out of $testCount total"
        }
        else {
            Fail "TRX file $($trxFile.Name) is empty or does not contain a TestRun element."
        }
    }
    catch {
        Fail "Failed to parse TRX file $($trxFile.Name): $_"
    }
}

if ($validFileCount -eq 0) {
    Fail "No valid .trx files found. All .trx files are empty or invalid XML."
}

if ($totalExecutedTestCount -eq 0) {
    if ($AllowZeroTests -and $mtpExitCode -eq 0) {
        Write-Warning "Valid .trx files were produced with zero executed tests, which this specialized workflow explicitly allows."
        exit 0
    }

    Fail "Valid .trx files were produced, but they contain zero executed tests."
}

Write-Host "Test execution completed with $totalExecutedTestCount executed test(s) out of $totalTestCount total in $validFileCount valid .trx file(s)"
