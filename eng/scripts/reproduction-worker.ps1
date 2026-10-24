# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Configure', 'Start', 'Iteration')]
    [string] $Operation,
    [string] $ResultsDirectory = 'all-results',
    [int] $Iteration,
    [int] $ProcessExitCode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-JsonFile($Path, $Value) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

if ($Operation -eq 'Configure') {
    $inputs = if ($env:DISPATCH_INPUTS) { ConvertFrom-Json -AsHashtable $env:DISPATCH_INPUTS } else { @{} }
    $allowed = @('test_project', 'test_method', 'mode', 'os', 'repetitions', 'runner_count', 'candidate_commit', 'correlation_id')
    foreach ($key in $inputs.Keys) {
        if ($key -notin $allowed -or $inputs[$key] -isnot [string]) {
            throw "Unknown or non-string dispatch input: $key"
        }
    }
    function Get-Setting($Name, $Fallback) {
        if ($inputs.ContainsKey($Name) -and $inputs[$Name] -ne '') { return $inputs[$Name] }
        return $Fallback
    }
    function Get-Count($Name, $Fallback, $Maximum) {
        $value = Get-Setting $Name $Fallback
        # Bound dispatch overrides without narrowing existing author-controlled manual settings.
        $limit = if ($inputs.ContainsKey($Name) -and $inputs[$Name] -ne '') { $Maximum } else { [int]::MaxValue }
        if ($value -cnotmatch '\A[1-9][0-9]*\z' -or [double]$value -gt $limit) {
            throw "$Name must be an integer between 1 and $limit."
        }
        return [int]$value
    }

    $project = Get-Setting 'test_project' $env:TEST_PROJECT
    if ($project -cnotmatch '\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z') {
        throw 'test_project must be a project shortname, not a path.'
    }
    $method = Get-Setting 'test_method' ''
    $mode = Get-Setting 'mode' $(if ($method) { 'single-test' } else { 'manual' })
    $filter = $env:TEST_FILTER
    switch -CaseSensitive ($mode) {
        'single-test' {
            if ($method -cnotmatch '\A[A-Za-z_][A-Za-z0-9_]*([.+][A-Za-z_][A-Za-z0-9_]*)+\z') {
                throw 'single-test requires an exact fully qualified test_method without wildcards or arguments.'
            }
            $filter = "--filter-method `"$method`""
        }
        'quarantine-project' {
            if ($method) { throw 'quarantine-project does not accept test_method.' }
            $filter = '--filter-trait "quarantined=true"'
        }
        'manual' {
            if ($method) { throw 'manual mode does not accept test_method.' }
            if (-not $filter -or $filter.Contains('YourTestMethodName')) {
                throw 'Configure TEST_FILTER or provide test_method/quarantine-project mode.'
            }
        }
        default { throw 'mode must be single-test or quarantine-project (or omitted for manual configuration).' }
    }
    # Manual filters remain author-controlled workflow configuration, never a dispatch input.
    if ($filter.Contains("`n") -or $filter.Contains("`r")) { throw 'TEST_FILTER must be a single line.' }
    $oses = @((Get-Setting 'os' $env:TARGET_OSES).Split(',') | ForEach-Object { $_.Trim() })
    if ($oses.Count -eq 0 -or @($oses | Select-Object -Unique).Count -ne $oses.Count) {
        throw 'os must contain distinct runner labels.'
    }
    foreach ($os in $oses) {
        if ($os -cnotin @('ubuntu-latest', 'windows-latest', 'macos-latest')) {
            throw "Unsupported os: $os"
        }
    }
    $runners = Get-Count 'runner_count' $env:RUNNERS_PER_OS 10
    $repetitions = Get-Count 'repetitions' $env:ITERATIONS_PER_RUNNER 50
    if ([long]$runners * $oses.Count -gt 256) { throw 'Runner matrix must not exceed 256 jobs.' }
    $candidate = Get-Setting 'candidate_commit' ''
    if ($candidate -and $candidate -cnotmatch '\A[0-9a-fA-F]{40}\z') {
        throw 'candidate_commit must be a full 40-character commit SHA.'
    }
    $correlation = Get-Setting 'correlation_id' ''
    if ($correlation -and $correlation -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z') {
        throw 'correlation_id must be 1-100 letters, digits, dots, underscores or hyphens, starting with a letter or digit.'
    }
    $config = [ordered]@{
        test_project = $project
        test_filter = $filter
        mode = $mode
        os = $oses
        runner_count = $runners
        repetitions = $repetitions
        candidate_commit = $candidate.ToLowerInvariant()
        correlation_id = $correlation
    }
    $matrix = @{ include = @(
        foreach ($os in $oses) {
            foreach ($index in 1..$runners) { @{ os = $os; index = $index } }
        }
    ) }
    $outputs = [ordered]@{
        config = ($config | ConvertTo-Json -Compress -Depth 10)
        matrix = ($matrix | ConvertTo-Json -Compress -Depth 10)
        is_cli_e2e = ($project -eq 'Cli.EndToEnd' -or $project -eq 'Aspire.Cli.EndToEnd').ToString().ToLowerInvariant()
        candidate_commit = $config.candidate_commit
        test_project = $project
        test_filter = $filter
        repetitions = $repetitions
    }
    foreach ($entry in $outputs.GetEnumerator()) {
        "$($entry.Key)=$($entry.Value)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
    }
    Write-Host "Generated $($oses.Count * $runners) runners; $repetitions iterations per runner."
    exit 0
}

$receiptPath = Join-Path $ResultsDirectory 'receipt.json'
if ($Operation -eq 'Start') {
    $config = ConvertFrom-Json -AsHashtable $env:WORKER_CONFIG
    $sourceSha = & git --no-pager rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine checked-out source SHA.' }
    if ($config.candidate_commit -and $sourceSha -cne $config.candidate_commit) {
        throw "Candidate checkout mismatch: requested $($config.candidate_commit), got $sourceSha."
    }
    New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
    Write-JsonFile $receiptPath ([ordered]@{
        schema_version = 1
        repository = $env:GITHUB_REPOSITORY
        run_id = $env:GITHUB_RUN_ID
        run_attempt = $env:GITHUB_RUN_ATTEMPT
        workflow_ref = $env:WORKER_WORKFLOW_REF
        workflow_sha = $env:WORKER_WORKFLOW_SHA
        source_sha = $sourceSha
        runner_os = $env:RUNNER_OS
        runner_index = $env:WORKER_RUNNER_INDEX
        config = $config
        iterations = @()
    })
    exit 0
}

$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
if ($Iteration -lt 1 -or $Iteration -gt $receipt.config.repetitions -or
    $Iteration -ne $receipt.iterations.Count + 1) {
    throw 'Iteration must be the next requested iteration; duplicate or out-of-range receipts are not allowed.'
}
$directory = Join-Path $ResultsDirectory "iteration-$Iteration"
$outputPath = Join-Path $directory 'test-output.log'
if (-not (Test-Path -LiteralPath $outputPath)) { throw "Missing iteration output: $outputPath" }
# MTP 2.4 can also emit merged copies of the original reports. Retain those files
# as evidence, but count only original reports to avoid doubling executions.
$trxFiles = @(Get-ChildItem -LiteralPath $directory -Filter '*.trx' -Recurse |
    Where-Object { [System.IO.Path]::GetRelativePath($directory, $_.FullName) -notmatch '(^|[/\\])merged[/\\]' })
$executed = 0L
$passed = 0L
$failed = 0L
foreach ($file in $trxFiles) {
    # MTP --report-trx emits <ResultSummary><Counters total="2" executed="1"
    # passed="1" failed="0" ... /></ResultSummary>. Skipped tests are not executions.
    # Use an XML reader with DTDs disabled: these files come from candidate code.
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $reader = [System.Xml.XmlReader]::Create($file.FullName, $settings)
    try {
        $xml = [System.Xml.XmlDocument]::new()
        $xml.Load($reader)
    } finally {
        $reader.Dispose()
    }
    $counters = $xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters) { throw "Missing TRX counters in $($file.FullName)" }
    foreach ($name in @('executed', 'passed', 'failed')) {
        if ($counters.GetAttribute($name) -cnotmatch '\A[0-9]+\z') {
            throw "Invalid TRX $name counter in $($file.FullName)"
        }
    }
    $executed += [long]$counters.GetAttribute('executed')
    $passed += [long]$counters.GetAttribute('passed')
    $failed += [long]$counters.GetAttribute('failed')
}
$result = $ProcessExitCode
$status = 'failed'
if ($trxFiles.Count -eq 0 -or $executed -eq 0) {
    if ($result -eq 0) { $result = 8 }
    $status = 'no-execution-evidence'
} elseif ($result -eq 0 -and $failed -eq 0 -and $passed -eq $executed) {
    $status = 'passed'
} elseif ($result -eq 0) {
    $result = 1
}
$receipt.iterations += [ordered]@{
    iteration = $Iteration
    process_exit_code = $ProcessExitCode
    exit_code = $result
    status = $status
    executed = $executed
    passed = $passed
    failed = $failed
    output = "iteration-$Iteration/test-output.log"
    trx = @($trxFiles | ForEach-Object { [System.IO.Path]::GetRelativePath($ResultsDirectory, $_.FullName).Replace('\', '/') })
}
Write-JsonFile $receiptPath $receipt
Write-Host "Iteration ${Iteration}: $status (executed=$executed, passed=$passed, failed=$failed, process exit=$ProcessExitCode)"
if ($result -ne 0) { exit 1 }
