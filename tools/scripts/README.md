# GitHub Actions Failure Analysis Tools

This directory contains tools for analyzing and diagnosing GitHub Actions test failures.

## DownloadFailingJobLogs.cs

A .NET file-based program that automatically downloads logs and artifacts for failed GitHub Actions jobs.

### What it does

This tool helps you quickly investigate GitHub Actions test failures by:

1. **Finding all failed jobs** in a GitHub Actions workflow run
2. **Downloading job logs** for each failed job
3. **Extracting test failures and errors** from the logs using regex patterns
4. **Determining artifact names** from job names based on the workflow's naming convention
5. **Downloading test artifacts** containing .trx files and test logs
6. **Extracting artifacts** to local directories for inspection

### Prerequisites

- .NET 10 SDK or later
- GitHub CLI (`gh`) installed and authenticated
- Access to the dotnet/aspire repository (or appropriate permissions for your target repo)

### Usage

Run the tool by passing a GitHub Actions run ID:

```bash
dotnet tools/scripts/DownloadFailingJobLogs.cs <run-id>
```

**Example:**
```bash
dotnet tools/scripts/DownloadFailingJobLogs.cs 19846215629
```

You can find the run ID from the GitHub Actions URL:
```
https://github.com/dotnet/aspire/actions/runs/19846215629
                                                  ^^^^^^^^^^
                                                  run ID
```

### Output

The tool creates the following files in your current directory:

- **`failed_job_<n>_<job-name>.log`** - Raw job logs from GitHub Actions
- **`artifact_<n>_<testname>_<os>.zip`** - Downloaded artifact zip files
- **`artifact_<n>_<testname>_<os>/`** - Extracted artifact directories containing:
  - `.trx` test result files
  - Test logs
  - Build binary logs (`.binlog`)
  - DCP (Distributed Application) diagnostic logs

### Example Output

```
Finding failed jobs for run 19846215629...
Found 100 total jobs
Found 1 failed jobs

=== Failed Job 1/1 ===
Name: Tests / Integrations macos (Hosting.Azure) / Hosting.Azure (macos-latest)
ID: 56864254427
URL: https://github.com/dotnet/aspire/actions/runs/19846215629/job/56864254427
Downloading job logs...
Saved job logs to: failed_job_0_Tests___Integrations_macos__Hosting_Azure____Hosting_Azure__macos-latest_.log (354209 characters)

Searching for test failures in job logs...

Errors found (2):
  - System.InvalidOperationException: Step 'provision-api-service-website' failed: No output for AZURE_APP_SERVICE_DASHBOARD_URI
  ...

Attempting to download artifact: logs-Hosting.Azure-macos-latest
Found 282 total artifacts
Found artifact ID: 4732859962
Downloaded artifact to: artifact_0_Hosting.Azure_macos-latest.zip
Extracted artifact to: artifact_0_Hosting.Azure_macos-latest

Found 1 .trx file(s):
  - artifact_0_Hosting.Azure_macos-latest/testresults/Hosting.Azure_net8.0_20251202034715.trx

=== Summary ===
Total jobs: 100
Failed jobs: 1
Logs downloaded: 1

All logs saved in current directory with pattern: failed_job_*.log
```

### How it works

1. **Job Discovery**: Uses `gh api` to query all jobs in the workflow run with manual pagination to handle 200+ artifacts
2. **Failure Detection**: Filters jobs by `conclusion == "failure"`
3. **Log Download**: Downloads raw logs for each failed job via the GitHub API
4. **Error Extraction**: Uses regex patterns to find:
   - Failed test names
   - Error messages
   - Exception stack traces
5. **Artifact Matching**: Parses job names to determine the artifact name using the pattern `logs-{testShortName}-{os}`
6. **Artifact Download**: Downloads the matching artifact containing test results
7. **Extraction**: Uses `System.IO.Compression.ZipFile` to extract artifacts

### Technical Details

- **Language**: C# 10+ (file-based program)
- **Runtime**: .NET 10
- **Key Dependencies**:
  - `System.IO.Compression` - For extracting zip artifacts
  - `System.Text.Json` - For parsing GitHub API responses
  - `System.Text.RegularExpressions` - For extracting test failures
- **GitHub API**: Uses `gh api` command-line tool for authenticated API access

### Troubleshooting

**No failed jobs found:**
- Verify the run ID is correct
- Check that the workflow run actually has failed jobs
- Ensure you have access to the repository

**Artifact not found:**
- The artifact may have expired (GitHub Actions artifacts are typically retained for 90 days)
- The job may not have uploaded an artifact (e.g., if it failed before tests ran)

**Permission denied:**
- Ensure you're authenticated with `gh auth login`
- Verify you have read access to the repository

### See Also

- [GitHub Actions Documentation](https://docs.github.com/en/actions)
- [GitHub CLI Documentation](https://cli.github.com/manual/)
- [.NET File-Based Programs](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/tutorials/file-based-programs)

---

## Heartbeat.cs

A cross-platform system monitoring tool that outputs CPU, memory, network, and Docker stats at regular intervals. Designed to help diagnose GitHub Actions runner hangs during tests.

### What it does

This tool helps diagnose runner communication issues by:

1. **Monitoring CPU usage** - System-wide CPU utilization percentage
2. **Tracking memory** - Used and total memory with percentage
3. **Counting network connections** - Established, listening, and TIME_WAIT connections
4. **Docker stats** - Container count and aggregate CPU/memory usage
5. **DCP process tracking** - Finds all processes starting with "dcp" and reports their memory usage

### Prerequisites

- .NET 10 SDK or later
- Docker (optional, for container stats on Linux)

### Usage

Run the tool with an optional interval (default: 5 seconds):

```bash
# Default 5-second interval
dotnet tools/scripts/Heartbeat.cs

# Custom 10-second interval
dotnet tools/scripts/Heartbeat.cs 10
```

### Output Format

```
[2025-12-12T10:30:00Z] HEARTBEAT | Starting system monitor (interval: 5s)
[2025-12-12T10:30:00Z] HEARTBEAT | Platform: Linux 5.15.0-1053-azure
[2025-12-12T10:30:00Z] HEARTBEAT | CPU: 45.2% | Mem: 4.2/8.0 GB (52%) | Net: 24 est, 12 listen, 5 tw | Docker: 3 containers (CPU: 120.5%, Mem: 45.2%) | DCP: none
[2025-12-12T10:30:05Z] HEARTBEAT | CPU: 67.8% | Mem: 5.1/8.0 GB (64%) | Net: 28 est, 12 listen, 8 tw | Docker: 3 containers (CPU: 156.2%, Mem: 52.1%) | DCP: 2 procs (245MB) [dcp(1234):120MB, dcp-api(5678):125MB]
```

### GitHub Actions Integration

Start the heartbeat at the beginning of a workflow and stop it at the end:

**Linux/macOS:**
```yaml
- name: Start heartbeat monitor
  run: |
    nohup dotnet tools/scripts/Heartbeat.cs > heartbeat.log 2>&1 &
    echo $! > heartbeat.pid

# ... run tests ...

- name: Stop heartbeat and show logs
  if: always()
  run: |
    if [ -f heartbeat.pid ]; then
      kill $(cat heartbeat.pid) 2>/dev/null || true
    fi
    echo "=== Heartbeat Log ==="
    cat heartbeat.log 2>/dev/null || echo "No heartbeat log found"
```

**Windows:**
```yaml
- name: Start heartbeat monitor
  shell: pwsh
  run: |
    Start-Process -FilePath "dotnet" -ArgumentList "tools/scripts/Heartbeat.cs" -RedirectStandardOutput "heartbeat.log" -NoNewWindow

# ... run tests ...

- name: Stop heartbeat and show logs
  if: always()
  shell: pwsh
  run: |
    Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*Heartbeat*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path "heartbeat.log") { Get-Content "heartbeat.log" }
```

### Platform Support

| Platform | CPU | Memory | Network | Docker | DCP |
|----------|-----|--------|---------|--------|-----|
| Linux | `/proc/stat` | `/proc/meminfo` | `netstat` | `docker stats` | `ps aux` |
| macOS | `top -l 1` | `vm_stat` + `sysctl` | `netstat` | `docker stats` | `ps aux` |
| Windows | `wmic cpu` | `wmic OS` | `netstat` | `docker stats` | `wmic process` |

### Graceful Shutdown

The heartbeat responds to:
- `Ctrl+C` (SIGINT)
- Process termination (SIGTERM)
- Parent process exit

### Troubleshooting

**CPU shows "calculating..." on first iteration:**
- This is expected. CPU usage requires two samples to calculate the delta.

**Docker shows "unavailable":**
- Docker daemon may not be running
- The user may not have permission to access the Docker socket
- Docker CLI may not be installed

**Stats show "N/A":**
- The underlying command may have failed or timed out
- Check if required tools (netstat, wmic, etc.) are available

**DCP shows "none":**
- No processes starting with "dcp" are currently running
- This is expected when Aspire tests are not actively running
