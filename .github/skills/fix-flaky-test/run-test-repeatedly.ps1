#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Repeatedly runs a dotnet test command to validate flaky test fixes.

.DESCRIPTION
    Runs <TestCommand> repeatedly to validate flaky test fixes.
    Cleanup logic modeled on tests/helix/send-to-helix-inner.proj pre-commands.
    Everything after '--' (or after the last recognized option) is executed
    verbatim each iteration.

    NOTE: PowerShell strips '--' when calling scripts directly. The script
    handles both cases: with '--' (e.g. via pwsh -File) and without it
    (direct invocation from a PowerShell prompt).

.EXAMPLE
    .github/skills/fix-flaky-test/run-test-repeatedly.ps1 -- dotnet test tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj --no-build -- --filter-method "*.MyTest"

.EXAMPLE
    .github/skills/fix-flaky-test/run-test-repeatedly.ps1 -n 50 --run-all -- dotnet test tests/Foo/Foo.csproj -- --filter-method "*.Bar"
#>

# ---------- defaults ----------
$Iterations = 100
$StopOnFailure = $true

# ---------- parse arguments ----------
# PowerShell's parameter binder consumes '--' when scripts are called directly
# (e.g. ./script.ps1 ... -- cmd), so the separator may not appear in $args.
# We handle both cases: if '--' is found we use everything after it; otherwise
# the first unrecognized argument starts the test command.
$TestCmd = @()
$i = 0
# Label the loop so we can break out of it from inside the switch statement.
# In PowerShell, 'break' inside a switch only exits the switch, not an enclosing loop.
:argloop while ($i -lt $args.Count) {
    switch ($args[$i]) {
        '-n' {
            $Iterations = [int]$args[$i + 1]
            $i += 2
        }
        '--run-all' {
            $StopOnFailure = $false
            $i++
        }
        '--help' {
            Write-Host @"
Usage: .github/skills/fix-flaky-test/run-test-repeatedly.ps1 [OPTIONS] -- <test command...>

Runs <test command> repeatedly to validate flaky test fixes.
Everything after '--' is executed verbatim each iteration.

Options:
  -n <count>    Number of iterations (default: 100)
  --run-all     Don't stop on first failure, run all iterations
  --help        Show this help message

Examples:
  .github/skills/fix-flaky-test/run-test-repeatedly.ps1 -n 20 -- dotnet test tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj -- --filter-method "*.MyTest"
  .github/skills/fix-flaky-test/run-test-repeatedly.ps1 -n 50 --run-all -- dotnet test tests/Foo/Foo.csproj -- --filter-method "*.Bar"
"@
            exit 0
        }
        '--' {
            # Explicit separator (works with pwsh -File)
            $TestCmd = @($args[($i + 1)..($args.Count - 1)])
            break argloop
        }
        default {
            # First unrecognized argument starts the test command.
            # This handles direct invocation where PowerShell consumed '--'.
            $TestCmd = @($args[$i..($args.Count - 1)])
            break argloop
        }
    }
}

if ($TestCmd.Count -eq 0) {
    Write-Error "Error: no test command provided. Use -- to separate options from the test command."
    Write-Error "Run with --help for usage."
    exit 1
}

# ---------- detect CI vs local ----------
$DockerCleanup = $true
if (-not $env:CI -and -not $env:GITHUB_ACTIONS) {
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host "Warning: This script removes ALL Docker containers and volumes between iterations." -ForegroundColor Yellow
        $response = Read-Host "Allow Docker cleanup? (y/N)"
        if ($response -notmatch '^[Yy]$') {
            $DockerCleanup = $false
            Write-Host "Docker cleanup disabled. Note: leftover containers may affect test results."
        }
    }
}

# ---------- infer test assembly name from .csproj in args ----------
$TestAssemblyName = ""
$TestProjectDir = ""
foreach ($arg in $TestCmd) {
    if ($arg -like '*.csproj') {
        $TestAssemblyName = [System.IO.Path]::GetFileNameWithoutExtension($arg)
        $TestProjectDir = Split-Path -Parent $arg
        break
    }
}

# ---------- setup ----------
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$ResultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "test-results-$timestamp"
New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null
$LogFile = Join-Path $ResultsDir "test-run.log"

$PassCount = 0
$FailCount = 0
$TimeoutCount = 0

# ---------- cleanup ----------
function Invoke-TestCleanup {
    # Kill dcp / dcpctrl processes
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match '^dcp(ctl)?$'
    } | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }

    # Kill dotnet-tests processes (actual test runner)
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -eq 'dotnet-tests'
    } | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }

    # Kill test service processes (TestProject.Service*, TestProject.Worker*)
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match '^TestProject\.(Service|Worker)'
    } | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }

    # Brief wait for processes to die
    Start-Sleep -Seconds 1

    # Docker cleanup — only if docker is available and cleanup is approved
    if ($DockerCleanup -and (Get-Command docker -ErrorAction SilentlyContinue)) {
        $containers = docker ps -aq 2>$null
        if ($containers) {
            $containers | ForEach-Object { docker stop $_ 2>$null } | Out-Null
            $containers | ForEach-Object { docker rm $_ 2>$null } | Out-Null
        }

        $volumes = docker volume ls -q 2>$null
        if ($volumes) {
            $volumes | ForEach-Object { docker volume rm $_ 2>$null } | Out-Null
        }

        docker network prune -f 2>$null | Out-Null
    }

    # Clean test result directories under the project path
    if ($TestProjectDir -and (Test-Path (Join-Path $TestProjectDir "TestResults"))) {
        Remove-Item -Recurse -Force (Join-Path $TestProjectDir "TestResults") -ErrorAction SilentlyContinue
    }
}

# ---------- state logging ----------
function Write-EnvironmentState {
    param(
        [int]$Iteration,
        [string]$LogTarget
    )

    $logContent = @()
    $logContent += "--- Environment state before cleanup (iteration $Iteration) ---"

    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $logContent += ">> docker container ls --all"
        $logContent += (docker container ls --all 2>&1 | Out-String)
        $logContent += ">> docker volume ls"
        $logContent += (docker volume ls 2>&1 | Out-String)
        $logContent += ">> docker network ls"
        $logContent += (docker network ls 2>&1 | Out-String)
    }

    $logContent += ">> processes (dcp|dotnet)"
    $procs = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -match 'dcp|dotnet'
    } | Select-Object Id, ProcessName | Out-String
    if ($procs.Trim()) {
        $logContent += $procs
    } else {
        $logContent += "(none)"
    }
    $logContent += "--- end environment state ---"

    $logContent | Out-File -Append -FilePath $LogTarget -Encoding utf8
}

# ---------- header ----------
$header = @"
========================================
Test Verification Run — $Iterations iterations
Mode: $(if ($StopOnFailure) { 'STOP ON FIRST FAILURE' } else { 'RUN ALL' })
========================================
Command: $($TestCmd -join ' ')
Test assembly: $(if ($TestAssemblyName) { $TestAssemblyName } else { '(unknown)' })
Results: $ResultsDir
Started: $(Get-Date)
Git commit: $(try { git rev-parse HEAD 2>$null } catch { 'N/A' })
========================================

"@
Write-Host $header
$header | Out-File -Append -FilePath $LogFile -Encoding utf8

# ---------- main loop ----------
$FirstFailureIteration = 0
$TimeoutSeconds = 180

for ($iter = 1; $iter -le $Iterations; $iter++) {
    $iterLog = Join-Path $ResultsDir "iteration-$iter.log"

    # Log environment state, then clean
    Write-EnvironmentState -Iteration $iter -LogTarget $iterLog
    Invoke-TestCleanup

    # Print iteration header
    $iterHeader = "Iteration $iter/$Iterations [$(Get-Date -Format 'HH:mm:ss')]: "
    Write-Host -NoNewline $iterHeader
    $iterHeader | Out-File -Append -FilePath $LogFile -Encoding utf8 -NoNewline

    # Log exact command
    "Running: $($TestCmd -join ' ')" | Out-File -Append -FilePath $iterLog -Encoding utf8

    # Build properly escaped argument string for Start-Process.
    # Start-Process -ArgumentList with a string[] joins elements with spaces but
    # does not quote elements containing spaces, so we pass a single pre-escaped string.
    $argString = ""
    if ($TestCmd.Count -gt 1) {
        $argString = ($TestCmd[1..($TestCmd.Count - 1)] | ForEach-Object {
            if ($_ -match '\s') { "`"$_`"" } else { $_ }
        }) -join ' '
    }

    # Run test with timeout
    $stdoutFile = Join-Path $ResultsDir "iter-stdout-$iter.tmp"
    $stderrFile = Join-Path $ResultsDir "iter-stderr-$iter.tmp"
    $process = Start-Process -FilePath $TestCmd[0] -ArgumentList $argString `
        -NoNewWindow -PassThru -RedirectStandardOutput $stdoutFile `
        -RedirectStandardError $stderrFile

    $completed = $process.WaitForExit($TimeoutSeconds * 1000)

    if (-not $completed) {
        # Process timed out
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $exitCode = 124
    } else {
        $exitCode = $process.ExitCode
    }

    # Append stdout/stderr to iteration log
    if (Test-Path $stdoutFile) {
        Get-Content $stdoutFile | Out-File -Append -FilePath $iterLog -Encoding utf8
        Remove-Item $stdoutFile -ErrorAction SilentlyContinue
    }
    if (Test-Path $stderrFile) {
        Get-Content $stderrFile | Out-File -Append -FilePath $iterLog -Encoding utf8
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }

    # Detect zero-test runs (exit code 8 is masked by --ignore-exit-code 8 in Testing.props)
    if ($exitCode -eq 0) {
        $iterContent = Get-Content $iterLog -Raw -ErrorAction SilentlyContinue
        if (-not ($iterContent -match 'Total:\s*[1-9]')) {
            $exitCode = 8
        }
    }

    # Classify result
    if ($exitCode -eq 0) {
        Write-Host "PASS" -ForegroundColor Green
        "PASS" | Out-File -Append -FilePath $LogFile -Encoding utf8
        $PassCount++
    } elseif ($exitCode -eq 8) {
        Write-Host "ZERO TESTS (no tests matched filter — check quarantine/filter settings)" -ForegroundColor Yellow
        "ZERO TESTS (no tests matched filter)" | Out-File -Append -FilePath $LogFile -Encoding utf8
        $FailCount++
        Copy-Item $iterLog (Join-Path $ResultsDir "failure-$iter.log")
    } elseif ($exitCode -eq 124) {
        Write-Host "TIMEOUT" -ForegroundColor Yellow
        "TIMEOUT" | Out-File -Append -FilePath $LogFile -Encoding utf8
        $TimeoutCount++
        Copy-Item $iterLog (Join-Path $ResultsDir "timeout-$iter.log")
    } else {
        Write-Host "FAIL (exit $exitCode)" -ForegroundColor Red
        "FAIL (exit $exitCode)" | Out-File -Append -FilePath $LogFile -Encoding utf8
        $FailCount++
        Copy-Item $iterLog (Join-Path $ResultsDir "failure-$iter.log")
    }

    # Handle failure
    if ($exitCode -ne 0) {
        if ($FirstFailureIteration -eq 0) {
            $FirstFailureIteration = $iter
        }
        if ($StopOnFailure) {
            Write-Host ""
            Write-Host "Stopping at iteration $iter due to failure." -ForegroundColor Red
            "" | Out-File -Append -FilePath $LogFile -Encoding utf8
            "Stopping at iteration $iter due to failure." | Out-File -Append -FilePath $LogFile -Encoding utf8
            break
        }
    }

    # Progress every 10 iterations
    if ($iter % 10 -eq 0) {
        $progressMsg = "  Progress: $iter/$Iterations - Pass: $PassCount, Fail: $FailCount, Timeout: $TimeoutCount"
        Write-Host $progressMsg -ForegroundColor Blue
        $progressMsg | Out-File -Append -FilePath $LogFile -Encoding utf8
    }
}

# ---------- final cleanup ----------
Invoke-TestCleanup

# ---------- summary ----------
$Total = $PassCount + $FailCount + $TimeoutCount
function Get-Pct { param([int]$num, [int]$den); if ($den -eq 0) { return "0.0" }; return "{0:F1}" -f (($num / $den) * 100) }

Write-Host ""
Write-Host "========================================"
Write-Host "Summary"
Write-Host "========================================"

$summaryLines = @("")
$summaryLines += "========================================"
$summaryLines += "Summary"
$summaryLines += "========================================"

if ($FirstFailureIteration -gt 0 -and $StopOnFailure) {
    Write-Host "Stopped at iteration $FirstFailureIteration" -ForegroundColor Red
    $summaryLines += "Stopped at iteration $FirstFailureIteration"
}

$completedMsg = "Completed: $Total / $Iterations"
Write-Host $completedMsg
$summaryLines += $completedMsg

$passMsg = "  Pass:    $PassCount ($(Get-Pct $PassCount $Total)%)"
Write-Host $passMsg -ForegroundColor Green
$summaryLines += $passMsg

$failMsg = "  Fail:    $FailCount ($(Get-Pct $FailCount $Total)%)"
Write-Host $failMsg -ForegroundColor Red
$summaryLines += $failMsg

$timeoutMsg = "  Timeout: $TimeoutCount ($(Get-Pct $TimeoutCount $Total)%)"
Write-Host $timeoutMsg -ForegroundColor Yellow
$summaryLines += $timeoutMsg

Write-Host "========================================"
Write-Host "Finished: $(Get-Date)"
Write-Host "Results:  $ResultsDir"

$summaryLines += "========================================"
$summaryLines += "Finished: $(Get-Date)"
$summaryLines += "Results:  $ResultsDir"

if ($FailCount -eq 0 -and $TimeoutCount -eq 0 -and $Total -eq $Iterations) {
    Write-Host ""
    Write-Host "All $Iterations iterations passed." -ForegroundColor Green
    $summaryLines += ""
    $summaryLines += "All $Iterations iterations passed."
    $summaryLines | Out-File -Append -FilePath $LogFile -Encoding utf8
    exit 0
} else {
    Write-Host ""
    Write-Host "Failure logs: $ResultsDir\failure-*.log"
    Write-Host "Timeout logs: $ResultsDir\timeout-*.log"
    $summaryLines += ""
    $summaryLines += "Failure logs: $ResultsDir\failure-*.log"
    $summaryLines += "Timeout logs: $ResultsDir\timeout-*.log"
    $summaryLines | Out-File -Append -FilePath $LogFile -Encoding utf8
    exit 1
}
