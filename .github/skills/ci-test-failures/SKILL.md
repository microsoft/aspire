---
name: ci-test-failures
description: Guide for diagnosing and fixing CI test failures using the DownloadFailingJobLogs tool. Use this when asked to investigate GitHub Actions test failures, download failure logs, or debug CI issues.
---

# CI Test Failure Diagnosis

This skill provides patterns and practices for diagnosing GitHub Actions test failures in the Aspire repository using the `DownloadFailingJobLogs` tool.

## Overview

When CI tests fail, use the `DownloadFailingJobLogs.cs` tool to automatically download logs and artifacts for failed jobs. This tool eliminates manual log hunting and provides structured access to test failures.

**Location**: `tools/scripts/DownloadFailingJobLogs.cs`

## Quick Start

### Step 1: Find the Run ID

Get the run ID from the GitHub Actions URL or use the `gh` CLI:

```bash
# From URL: https://github.com/microsoft/aspire/actions/runs/19846215629
#                                                        ^^^^^^^^^^
#                                                        run ID

# Or find the latest run on a branch
gh run list --repo microsoft/aspire --branch <branch-name> --limit 1 --json databaseId --jq '.[0].databaseId'

# Or for a PR
gh pr checks <pr-number> --repo microsoft/aspire
```

### Step 2: Run the Tool

```bash
cd tools/scripts
dotnet run DownloadFailingJobLogs.cs -- <run-id>
```

**Example:**
```bash
dotnet run DownloadFailingJobLogs.cs -- 19846215629
```

### Step 3: Analyze Output

The tool creates files in your current directory:

| File Pattern | Contents |
|--------------|----------|
| `failed_job_<n>_<job-name>.log` | Raw job logs from GitHub Actions |
| `artifact_<n>_<testname>_<os>.zip` | Downloaded artifact zip files |
| `artifact_<n>_<testname>_<os>/` | Extracted directory with .trx files, logs, binlogs |

## What the Tool Does

1. **Finds all failed jobs** in a GitHub Actions workflow run
2. **Downloads job logs** for each failed job
3. **Extracts test failures and errors** from logs using regex patterns
4. **Determines artifact names** from job names (pattern: `logs-{testShortName}-{os}`)
5. **Downloads test artifacts** containing .trx files and test logs
6. **Extracts artifacts** to local directories for inspection

## Example Workflow

```bash
# 1. Check failed jobs on a PR
gh pr checks 14105 --repo microsoft/aspire 2>&1 | Where-Object { $_ -match "fail" }

# 2. Get the run ID
$runId = gh run list --repo microsoft/aspire --branch davidfowl/my-branch --limit 1 --json databaseId --jq '.[0].databaseId'

# 3. Download failure logs
cd tools/scripts
dotnet run DownloadFailingJobLogs.cs -- $runId

# 4. Search for errors in downloaded logs
Get-Content "failed_job_0_*.log" | Select-String -Pattern "error|Error:" -Context 2,3 | Select-Object -First 20

# 5. Check .trx files for test failures
Get-ChildItem -Recurse -Filter "*.trx" | ForEach-Object {
    [xml]$xml = Get-Content $_.FullName
    $xml.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" }
}
```

## Understanding Job Log Output

The tool prints a summary for each failed job:

```
=== Failed Job 1/1 ===
Name: Tests / Integrations macos (Hosting.Azure) / Hosting.Azure (macos-latest)
ID: 56864254427
URL: https://github.com/microsoft/aspire/actions/runs/19846215629/job/56864254427
Downloading job logs...
Saved job logs to: failed_job_0_Tests___Integrations_macos__Hosting_Azure____Hosting_Azure__macos-latest_.log

Errors found (2):
  - System.InvalidOperationException: Step 'provision-api-service' failed...
```

## Searching Downloaded Logs

### Find Errors in Job Logs

```powershell
# PowerShell
Get-Content "failed_job_*.log" | Select-String -Pattern "error|Error:" -Context 2,3

# Bash
grep -i "error" failed_job_*.log | head -50
```

### Find Build Failures

```powershell
Get-Content "failed_job_*.log" | Select-String -Pattern "Build FAILED|error MSB|error CS"
```

### Find Test Failures

```powershell
Get-Content "failed_job_*.log" | Select-String -Pattern "Failed!" -Context 5,0
```

### Check for Disk Space Issues

```powershell
Get-Content "failed_job_*.log" | Select-String -Pattern "No space left|disk space"
```

### Check for Timeout Issues

```powershell
Get-Content "failed_job_*.log" | Select-String -Pattern "timeout|timed out|Timeout"
```

## Using GitHub API for Annotations

Sometimes job logs aren't available (404). Use annotations instead:

```bash
gh api repos/microsoft/aspire/check-runs/<job-id>/annotations
```

This returns structured error information even when full logs aren't downloadable.

## Common Failure Patterns

### Disk Space Exhaustion

**Symptom:** `No space left on device` in annotations or logs

**Diagnosis:**
```powershell
gh api repos/microsoft/aspire/check-runs/<job-id>/annotations 2>&1
```

**Common fixes:**
- Add disk cleanup step before build
- Use larger runner (e.g., `8-core-ubuntu-latest`)
- Skip unnecessary build steps (e.g., `/p:BuildTests=false`)

### Command Not Found

**Symptom:** `exit code 127` or `command not found`

**Diagnosis:**
```powershell
Get-Content "failed_job_*.log" | Select-String -Pattern "command not found|exit code 127" -Context 3,1
```

**Common fixes:**
- Ensure PATH includes required tools
- Use full path to executables
- Install missing dependencies

### Test Timeout

**Symptom:** Test hangs, then fails with timeout

**Diagnosis:**
```powershell
Get-Content "failed_job_*.log" | Select-String -Pattern "Test host process exited|Timeout|timed out"
```

**Common fixes:**
- Increase test timeout
- Check for deadlocks in test code
- Review Heartbeat.cs output for resource exhaustion

### Build Failure

**Symptom:** `Build FAILED` or MSBuild errors

**Diagnosis:**
```powershell
Get-Content "failed_job_*.log" | Select-String -Pattern "error CS|error MSB|Build FAILED" -Context 0,3
```

**Common fixes:**
- Check for missing project references
- Verify package versions
- Download and analyze .binlog from artifacts

## Artifact Contents

Downloaded artifacts typically contain:

```
artifact_0_TestName_os/
├── testresults/
│   ├── TestName_net10.0_timestamp.trx    # Test results XML
│   ├── Aspire.*.Tests_*.log              # Console output
│   ├── recordings/                        # Asciinema recordings (CLI E2E tests)
│   └── workspaces/                        # Captured project workspaces (CLI E2E tests)
│       └── TestClassName.MethodName/      # Full generated project for failed tests
│           ├── apphost.ts
│           ├── aspire.config.json
│           ├── .modules/                  # Generated SDK (aspire.js) - key for debugging
│           └── ...
├── *.crash.dmp                            # Crash dump (if test crashed)
└── test.binlog                            # MSBuild binary log
```

### CLI E2E Workspace Capture

CLI E2E tests annotated with `[CaptureWorkspaceOnFailure]` automatically capture the full generated project workspace when a test fails. This includes the generated SDK (`.modules/aspire.js`), template output, and config files — critical for debugging template generation or `aspire run` failures.

Look in `testresults/workspaces/{TestClassName.MethodName}/` inside the downloaded artifact.

## Parsing .trx Files

```powershell
# Find all failed tests in .trx files
Get-ChildItem -Path "artifact_*" -Recurse -Filter "*.trx" | ForEach-Object {
    Write-Host "=== $($_.Name) ==="
    [xml]$xml = Get-Content $_.FullName
    $xml.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq "Failed" } | ForEach-Object {
        Write-Host "FAILED: $($_.testName)"
        Write-Host $_.Output.ErrorInfo.Message
        Write-Host "---"
    }
}
```

## Tips

### Clean Up Before Running

```powershell
Remove-Item *.log -Force -ErrorAction SilentlyContinue
Remove-Item *.zip -Force -ErrorAction SilentlyContinue
Remove-Item -Recurse artifact_* -Force -ErrorAction SilentlyContinue
```

### Run From tools/scripts Directory

The tool creates files in the current directory, so run it from `tools/scripts` to keep things organized:

```bash
cd tools/scripts
dotnet run DownloadFailingJobLogs.cs -- <run-id>
```

### Don't Commit Log Files

The downloaded log files can be large. Don't commit them to the repository:

```bash
# Before committing
rm tools/scripts/*.log
rm tools/scripts/*.zip
rm -rf tools/scripts/artifact_*
```

## Prerequisites

- .NET 10 SDK or later
- GitHub CLI (`gh`) installed and authenticated
- Access to the microsoft/aspire repository

## See Also

- `tools/scripts/README.md` - Full documentation
- `tools/scripts/Heartbeat.cs` - System monitoring tool for diagnosing hangs
- `.github/skills/cli-e2e-testing/SKILL.md` - CLI E2E test troubleshooting
