---
name: startup-perf
description: Measures Aspire application startup performance using dotnet-trace and the TraceAnalyzer tool. Use this when asked to measure impact of a code change on Aspire application startup performance.
---

# Aspire Startup Performance Measurement

This skill provides patterns and practices for measuring .NET Aspire application startup performance using the `Measure-StartupPerformance.ps1` (Windows/PowerShell) or `measure-startup-performance.sh` (macOS/Linux) script and the companion `TraceAnalyzer` tool.

## Overview

The startup performance tooling collects `dotnet-trace` traces from an Aspire AppHost application and computes the startup duration from `AspireEventSource` events. Specifically, it measures the time between the `DcpModelCreationStart` (event ID 17) and `DcpModelCreationStop` (event ID 18) events emitted by the `Microsoft-Aspire-Hosting` EventSource provider.

**PowerShell Script (Windows)**: `tools/perf/Measure-StartupPerformance.ps1`
**Bash Script (macOS/Linux)**: `tools/perf/measure-startup-performance.sh`
**TraceAnalyzer Location**: `tools/perf/TraceAnalyzer/`
**Documentation**: `docs/getting-perf-traces.md`

## Prerequisites

### Windows

- PowerShell 7+
- `dotnet-trace` global tool (`dotnet tool install -g dotnet-trace`)
- .NET SDK (restored via `./restore.cmd` or `./restore.sh`)

### macOS / Linux

- Bash 4+
- `dotnet-trace` global tool (`dotnet tool install -g dotnet-trace`)
- `python3` (for parsing `launchSettings.json`)
- .NET SDK (restored via `./restore.sh`)

## Quick Start

### Single Measurement

```powershell
# From repository root — measures the default TestShop.AppHost (Windows)
.\tools\perf\Measure-StartupPerformance.ps1
```

```bash
# From repository root — measures the default TestShop.AppHost (macOS/Linux)
./tools/perf/measure-startup-performance.sh
```

### Multiple Iterations with Statistics

```powershell
.\tools\perf\Measure-StartupPerformance.ps1 -Iterations 5
```

```bash
./tools/perf/measure-startup-performance.sh --iterations 5
```

### Custom Project

```powershell
.\tools\perf\Measure-StartupPerformance.ps1 -ProjectPath "path\to\MyApp.AppHost.csproj" -Iterations 3
```

```bash
./tools/perf/measure-startup-performance.sh --project-path path/to/MyApp.AppHost.csproj --iterations 3
```

### Preserve Traces for Manual Analysis

```powershell
.\tools\perf\Measure-StartupPerformance.ps1 -Iterations 3 -PreserveTraces -TraceOutputDirectory "C:\traces"
```

```bash
./tools/perf/measure-startup-performance.sh --iterations 3 --preserve-traces --trace-output-directory /tmp/traces
```

### Verbose Output

```powershell
.\tools\perf\Measure-StartupPerformance.ps1 -Verbose
```

```bash
./tools/perf/measure-startup-performance.sh --verbose
```

## Parameters

| PowerShell Parameter | Bash Parameter | Default | Description |
|---------------------|----------------|---------|-------------|
| `-ProjectPath` | `--project-path` | TestShop.AppHost | Path to the AppHost `.csproj` to measure |
| `-Iterations` | `--iterations` | 1 | Number of measurement runs (1–100) |
| `-PreserveTraces` | `--preserve-traces` | false | Keep `.nettrace` files after analysis |
| `-TraceOutputDirectory` | `--trace-output-directory` | temp folder | Directory for preserved trace files |
| `-SkipBuild` | `--skip-build` | false | Skip `dotnet build` before running |
| `-TraceDurationSeconds` | `--trace-duration-seconds` | 60 | Maximum trace collection time (1–86400) |
| `-PauseBetweenIterationsSeconds` | `--pause-between-iterations-seconds` | 45 | Pause between iterations (0–3600) |
| `-Verbose` | `--verbose` | false | Show detailed output |

## How It Works

The script follows this sequence:

1. **Prerequisites check** — Verifies `dotnet-trace` is installed and the project exists.
2. **Build** — Builds the AppHost project in Release configuration (unless `-SkipBuild`).
3. **Build TraceAnalyzer** — Builds the companion `tools/perf/TraceAnalyzer` project.
4. **For each iteration:**
   a. Locates the compiled executable (Arcade-style or traditional output paths).
   b. Reads `launchSettings.json` for environment variables.
   c. Launches the AppHost as a separate process.
   d. Attaches `dotnet-trace` to the running process with the `Microsoft-Aspire-Hosting` provider.
   e. Waits for the trace to complete (duration timeout or process exit).
   f. Runs the TraceAnalyzer to extract the startup duration from the `.nettrace` file.
   g. Cleans up processes.
5. **Reports results** — Prints per-iteration times and statistics (min, max, average, std dev).

## TraceAnalyzer Tool

The `tools/perf/TraceAnalyzer` is a small .NET console app that parses `.nettrace` files using the `Microsoft.Diagnostics.Tracing.TraceEvent` library.

### What It Does

- Opens the `.nettrace` file with `EventPipeEventSource`
- Listens for events from the `Microsoft-Aspire-Hosting` provider
- Extracts timestamps for `DcpModelCreationStart` (ID 17) and `DcpModelCreationStop` (ID 18)
- Outputs the duration in milliseconds (or `"null"` if events are not found)

### Standalone Usage

```bash
dotnet run --project tools/perf/TraceAnalyzer -c Release -- <path-to-nettrace-file>
```

## Understanding Output

### Successful Run

```
==================================================
 Aspire Startup Performance Measurement
==================================================

Project: TestShop.AppHost
Iterations: 3
...

Iteration 1
----------------------------------------
Starting TestShop.AppHost...
Attaching trace collection to PID 12345...
Collecting performance trace...
Trace collection completed.
Analyzing trace: ...
Startup time: 1234.56 ms

...

==================================================
 Results Summary
==================================================

Iteration StartupTimeMs
--------- -------------
        1       1234.56
        2       1189.23
        3       1201.45

Statistics:
  Successful iterations: 3 / 3
  Minimum: 1189.23 ms
  Maximum: 1234.56 ms
  Average: 1208.41 ms
  Std Dev: 18.92 ms
```

### Common Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `dotnet-trace is not installed` | Missing global tool | Run `dotnet tool install -g dotnet-trace` |
| `Could not find compiled executable` | Project not built | Remove `-SkipBuild` or build manually |
| `Could not find DcpModelCreation events` | Trace too short or events not emitted | Increase `-TraceDurationSeconds` |
| `Application exited immediately` | App crash on startup | Check app logs, ensure dependencies are available |
| `dotnet-trace exited with code != 0` | Trace collection error | Check verbose output; trace file may still be valid |

## Comparing Before/After Performance

To measure the impact of a code change:

```powershell
# Windows: Measure baseline (on main branch)
git checkout main
.\tools\perf\Measure-StartupPerformance.ps1 -Iterations 5 -PreserveTraces -TraceOutputDirectory "C:\traces\baseline"

# Windows: Measure with changes
git checkout my-feature-branch
.\tools\perf\Measure-StartupPerformance.ps1 -Iterations 5 -PreserveTraces -TraceOutputDirectory "C:\traces\feature"

# Compare the reported averages and std devs
```

```bash
# macOS/Linux: Measure baseline (on main branch)
git checkout main
./tools/perf/measure-startup-performance.sh --iterations 5 --preserve-traces --trace-output-directory /tmp/traces/baseline

# macOS/Linux: Measure with changes
git checkout my-feature-branch
./tools/perf/measure-startup-performance.sh --iterations 5 --preserve-traces --trace-output-directory /tmp/traces/feature

# Compare the reported averages and std devs
```

Use enough iterations (5+) and a consistent pause between iterations for reliable comparisons.

## Collecting Traces for Manual Analysis

If you need to inspect trace files manually (e.g., in PerfView or Visual Studio):

```powershell
.\tools\perf\Measure-StartupPerformance.ps1 -PreserveTraces -TraceOutputDirectory "C:\my-traces"
```

```bash
./tools/perf/measure-startup-performance.sh --preserve-traces --trace-output-directory /tmp/my-traces
```

See `docs/getting-perf-traces.md` for guidance on analyzing traces with PerfView or `dotnet trace report`.

## EventSource Provider Details

The `Microsoft-Aspire-Hosting` EventSource emits events for key Aspire lifecycle milestones. The startup performance script focuses on:

| Event ID | Event Name | Description |
|----------|------------|-------------|
| 17 | `DcpModelCreationStart` | Marks the beginning of DCP model creation |
| 18 | `DcpModelCreationStop` | Marks the completion of DCP model creation |

The measured startup time is the wall-clock difference between these two events, representing the time to create all application services and supporting dependencies.
