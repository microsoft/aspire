---
name: ci-test-failures
description: Guide for diagnosing GitHub Actions test failures, extracting failed tests from runs, and creating or updating failing-test issues. Use this when asked to investigate GitHub Actions test failures, download failure logs, create failing-test issues, or debug CI issues.
---

# CI Test Failure Diagnosis and Issue Filing

## Recipe: Create an Issue for a Test Failure

When the user asks to create an issue for a failing test, follow these steps. Always redirect full output to a log file (not `tail`) so you can inspect it if the command fails.

### Step 1: List failed tests (if user didn't specify one)

Omit `--test` to discover all failures. Redirect output to a log file:

```bash
dotnet run --project tools/CreateFailingTestIssue -- \
  --url "<the-url-the-user-gave>" \
  --output /tmp/cfti-result.json \
  > /tmp/cfti-list.log 2>&1
```

```powershell
dotnet run --project tools/CreateFailingTestIssue -- `
  --url "<the-url-the-user-gave>" `
  --output $env:TEMP/cfti-result.json `
  > $env:TEMP/cfti-list.log 2>&1
```

Then read the result with `jq`:

```bash
jq '{ success, availableFailedTests: .diagnostics.availableFailedTests, errorMessage: .errorMessage }' /tmp/cfti-result.json
```

```powershell
Get-Content $env:TEMP/cfti-result.json | ConvertFrom-Json | Select-Object success, errorMessage, @{N='availableFailedTests';E={$_.diagnostics.availableFailedTests}}
```

If `success` is `false`, inspect the full log: `cat /tmp/cfti-list.log` (bash) or `Get-Content $env:TEMP/cfti-list.log` (PowerShell).

Ask the user which test to file for, then proceed to Step 2.

### Step 2: Create the issue

```bash
dotnet run --project tools/CreateFailingTestIssue -- \
  --url "<the-url-the-user-gave>" \
  --test "<test-name>" \
  --create \
  --output /tmp/cfti-result.json \
  > /tmp/cfti-create.log 2>&1
```

```powershell
dotnet run --project tools/CreateFailingTestIssue -- `
  --url "<the-url-the-user-gave>" `
  --test "<test-name>" `
  --create `
  --output $env:TEMP/cfti-result.json `
  > $env:TEMP/cfti-create.log 2>&1
```

Then read the result:

```bash
jq '{ success, issue: .issue.createdIssue, errorMessage: .errorMessage }' /tmp/cfti-result.json
```

```powershell
Get-Content $env:TEMP/cfti-result.json | ConvertFrom-Json | Select-Object success, errorMessage, @{N='issue';E={$_.issue.createdIssue}}
```

If `success` is `false`, inspect the full log: `cat /tmp/cfti-create.log` (bash) or `Get-Content $env:TEMP/cfti-create.log` (PowerShell).

That's it — do not add analysis comments, do not use `--dry-run` unless the user explicitly asks for a preview.

**Rules:**
- Always use `--output <file>` to keep JSON clean. Do NOT try to parse JSON from stdout — it is interleaved with dotnet build progress output.
- Use `jq` to extract fields from the output file. Key paths:
  - `.success` — whether the operation succeeded
  - `.issue.createdIssue.number` and `.issue.createdIssue.url` — the created/updated issue
  - `.diagnostics.availableFailedTests[]` — test names when `--test` is omitted
  - `.errorMessage` — error details when `.success` is false
- Do NOT create issues manually with `gh issue create`. The tool handles everything: resolving the run, finding the test, generating a template-compliant body, and creating the issue.
- Do NOT invent your own issue markdown. The tool generates content that matches `.github/ISSUE_TEMPLATE/50_failing_test.yml`.
- If the tool fails, report the error from `diagnostics.log` and the JSON output. Do not fall back to manual issue creation.

## Recipe: Investigate a Failing Run (No Issue Creation)

To download and inspect failure artifacts without creating an issue:

```bash
cd tools/scripts
dotnet run DownloadFailingJobLogs.cs -- <run-id>
```

```powershell
Set-Location tools/scripts
dotnet run DownloadFailingJobLogs.cs -- <run-id>
```

Then search the downloaded logs and `.trx` files for errors.

---

## Reference Documentation

Everything below is reference material for edge cases and deeper investigation.

### Overview

Use this skill in two phases:

1. **Investigate the run** with `DownloadFailingJobLogs.cs` to fetch failed job logs and artifacts.
2. **Create or update a failing-test issue** with `tools/CreateFailingTestIssue --create`.

### Tools covered

| Tool | Purpose | Location |
|------|---------|----------|
| `DownloadFailingJobLogs.cs` | Download failed job logs and test artifacts from a GitHub Actions run | `tools/scripts/DownloadFailingJobLogs.cs` |
| `CreateFailingTestIssue` | Resolve a failing test from PR/run/job URLs and create/update issues | `tools/CreateFailingTestIssue` |
| `/create-issue` workflow | Create, reopen, or comment on failing-test issues from issue/PR comments | `.github/workflows/create-failing-test-issue.yml` |

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

```powershell
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

```powershell
Set-Location tools/scripts
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

## Creating or Updating Failing-Test Issues

After you know which test failed, use the branch automation to create a failing-test issue in the known-issues format.

### Preferred path: `/create-issue` from a PR or issue comment

Comment on the PR or issue with:

```text
/create-issue --test "<test-name>" [--url <pr|run|job-url>] [--workflow <selector>] [--force-new]
```

Examples:

```text
/create-issue --test "Tests.Namespace.Type.Method(input: 1)"
/create-issue --test "Tests.Namespace.Type.Method(input: 1)" --url https://github.com/microsoft/aspire/actions/runs/123
/create-issue "Tests.Namespace.Type.Method(input: 1)" https://github.com/microsoft/aspire/actions/runs/123/job/456
/create-issue --test "Tests.Namespace.Type.Method(input: 1)" --url https://github.com/microsoft/aspire/actions/runs/123/attempts/2/job/456?pr=321 --force-new
```

Notes:

- When the command is posted on a PR and no `--url` is supplied, the workflow defaults to that PR URL.
- `--workflow` defaults to `ci`.
- `--force-new` bypasses issue reuse and always requests a fresh issue.
- The workflow requires write or admin access to the repository before it will create or update issues.

### Supported source URLs

The resolver accepts:

- Pull request URLs: `https://github.com/<owner>/<repo>/pull/<number>`
- Workflow run URLs: `https://github.com/<owner>/<repo>/actions/runs/<run-id>`
- Attempt URLs: `https://github.com/<owner>/<repo>/actions/runs/<run-id>/attempts/<attempt>`
- Job URLs: `https://github.com/<owner>/<repo>/actions/runs/<run-id>/job/<job-id>`
- Attempt job URLs with query strings

### Local path: run the resolver directly

Always use `--output` to write results to a file so JSON is not interleaved with build output:

To generate the JSON result locally without creating an issue (dry run):

```bash
dotnet run --project tools/CreateFailingTestIssue -- \
  --url "https://github.com/microsoft/aspire/actions/runs/123" \
  --test "<test-name>" \
  --repo "microsoft/aspire" \
  --output /tmp/cfti-result.json
```

```powershell
dotnet run --project tools/CreateFailingTestIssue -- `
  --url "https://github.com/microsoft/aspire/actions/runs/123" `
  --test "<test-name>" `
  --repo "microsoft/aspire" `
  --output $env:TEMP/cfti-result.json
```

To resolve the failure **and create the issue on GitHub** in one step:

```bash
dotnet run --project tools/CreateFailingTestIssue -- \
  --url "https://github.com/microsoft/aspire/actions/runs/123" \
  --test "<test-name>" \
  --repo "microsoft/aspire" \
  --create \
  --output /tmp/cfti-result.json
```

```powershell
dotnet run --project tools/CreateFailingTestIssue -- `
  --url "https://github.com/microsoft/aspire/actions/runs/123" `
  --test "<test-name>" `
  --repo "microsoft/aspire" `
  --create `
  --output $env:TEMP/cfti-result.json
```

Read the result with `jq`:

```bash
jq '{ success, issue: .issue, availableFailedTests: .diagnostics.availableFailedTests }' /tmp/cfti-result.json
```

```powershell
Get-Content $env:TEMP/cfti-result.json | ConvertFrom-Json | Select-Object success, @{N='issue';E={$_.issue}}, @{N='availableFailedTests';E={$_.diagnostics.availableFailedTests}}
```

If `--test` is omitted, the tool emits structured JSON for all failing tests it found in the run (useful for picking which test to file).

The command writes a `diagnostics.log` file in the current directory. The JSON output (written to the `--output` file or stdout) contains:

- the resolved run and job URLs
- either the matched canonical and display test names plus generated issue content, or a per-test list of all failures in the run
- primary failure details (error, stack trace, stdout)
- when `--create` is set, the created issue number and URL
- warnings and alternate failed-test names if the match fails

### What the resolver does

`CreateFailingTestIssue`:

1. Resolves the workflow selector and source URL.
2. Finds the workflow run and failed jobs, including attempt URLs.
3. Downloads failed test occurrences from `.trx` artifacts.
4. Falls back to failed job logs when artifacts are missing or the run is still active.
5. Matches the requested test using canonical or display names.
6. Generates issue content that matches `.github/ISSUE_TEMPLATE/50_failing_test.yml`. The error details code block is wrapped in a collapsible `<details>` element when it exceeds 30 lines.
7. Reuses an open issue with the same stable signature, reopens a closed one, or creates a new issue.

### Finding the failed tests to file

For a run with multiple failures, first extract the candidate test names, then issue one `/create-issue` command per test:

```powershell
Get-ChildItem -Path "artifact_*" -Recurse -Filter "*.trx" | ForEach-Object {
    [xml]$xml = Get-Content $_.FullName
    $xml.TestRun.Results.UnitTestResult |
        Where-Object { $_.outcome -eq "Failed" } |
        Select-Object -ExpandProperty testName
}
```

If the resolver cannot match the requested test exactly, it returns `availableFailedTests` so you can retry with one of the discovered names.

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

# 6. Create or update the failing-test issue from the PR or issue thread
/create-issue --test "Tests.Namespace.Type.Method(input: 1)" --url https://github.com/microsoft/aspire/actions/runs/$runId
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

```powershell
Set-Location tools/scripts
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

```powershell
# Before committing
Remove-Item tools/scripts/*.log -Force -ErrorAction SilentlyContinue
Remove-Item tools/scripts/*.zip -Force -ErrorAction SilentlyContinue
Remove-Item tools/scripts/artifact_* -Recurse -Force -ErrorAction SilentlyContinue
```

## Prerequisites

- .NET 10 SDK or later
- GitHub CLI (`gh`) installed and authenticated
- Access to the microsoft/aspire repository

## See Also

- `tools/scripts/README.md` - Full documentation
- `tools/scripts/Heartbeat.cs` - System monitoring tool for diagnosing hangs
- `.github/skills/cli-e2e-testing/SKILL.md` - CLI E2E test troubleshooting
