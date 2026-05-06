---
name: startup-perf
description: Measures Aspire profiling with the OTEL startup harness, dashboard export, optional dotnet-trace traces, and optional MSBuild binlogs.
---

# Aspire Startup Profiling with OTEL

Use this skill when measuring, validating, or investigating Aspire startup performance with the OTEL profiling harness.

The primary workflow is the PowerShell-free `eng/scripts/verify-startup-otel.sh` harness. A Windows PowerShell equivalent exists at `eng/scripts/verify-startup-otel.ps1` for parity checks. Both scripts run a throwaway AppHost through the Aspire CLI, enable profiling-only OTEL instrumentation, export dashboard telemetry, and validate that CLI, Hosting, and DCP startup spans are correlated in one trace.

## Current Profiling Model

Profiling is opt-in and separate from reported telemetry:

- Enable profiling with `ASPIRE_PROFILING_ENABLED=true` or `1`.
- CLI profiling spans use the `Aspire.Cli.Profiling` ActivitySource.
- Hosting profiling spans use the `Aspire.Hosting.Profiling` ActivitySource.
- DCP startup spans use the `dcp.startup` instrumentation scope.
- Reported telemetry must not carry profiling session IDs, high-cardinality profiling tags, or profiling spans.

The older EventSource/dotnet-trace startup measurement scripts still exist, but they are a legacy fallback for explicit EventSource timing requests. Prefer the OTEL harness for current profiling work.

## Prerequisites

The Bash harness runs on macOS/Linux with:

- Bash
- `dotnet`
- `node`
- `curl`
- `unzip`
- `pgrep` and `ps`
- `dotnet-trace` only when using `--collect-dotnet-traces`

From a clean checkout, the harness can restore and build the local CLI and bundle layout itself. Use `--skip-build` only when the CLI and bundle layout were already built in the same worktree.

The PowerShell harness runs on Windows with PowerShell 7+, `dotnet`, `node`/`npm`, and the usual Aspire CLI prerequisites. The Bash harness has the richer diagnostics path today (`--collect-dotnet-traces` and `--collect-dotnet-binlogs`); keep the shared validator in parity when updating either shell.

## Quick Start

```bash
# From repository root. Builds local CLI/layout if needed.
./eng/scripts/verify-startup-otel.sh
```

After a successful run, inspect the generated run root:

```bash
cat artifacts/tmp/startup-otel-harness/*/summary.json
```

For faster iteration after a successful local build:

```bash
./eng/scripts/verify-startup-otel.sh --skip-build
```

Windows parity check:

```powershell
.\eng\scripts\verify-startup-otel.ps1 -SkipBuild
```

Collect sampled CPU traces and MSBuild binlogs:

```bash
./eng/scripts/verify-startup-otel.sh --collect-dotnet-traces
```

Collect only MSBuild binlogs:

```bash
./eng/scripts/verify-startup-otel.sh --collect-dotnet-binlogs
```

Use a specific Aspire CLI, bundle layout, or DCP build:

```bash
./eng/scripts/verify-startup-otel.sh \
  --target-aspire-path artifacts/bin/Aspire.Cli/Debug/net10.0/aspire \
  --layout-path artifacts/bundle/osx-arm64 \
  --dcp-path path/to/dcp
```

## Bash Harness Options

| Option | Description |
| --- | --- |
| `--target-aspire-path PATH` | Aspire CLI under test. Alias: `--aspire-path`. |
| `--profiler-aspire-path PATH` | Aspire CLI used to host/export dashboard telemetry. Alias: `--dashboard-aspire-path`. |
| `--layout-path PATH` | Aspire bundle layout path. |
| `--dcp-path PATH` | DCP directory or binary path override. |
| `--output-root PATH` | Output root for harness artifacts. Defaults to `artifacts/tmp/startup-otel-harness`. |
| `--post-start-delay SECONDS` | Delay after AppHost start before stopping to allow extra telemetry to flush. |
| `--require-dcp-spans` | Require exported DCP process/resource spans in addition to CLI/Hosting spans. |
| `--collect-dotnet-traces` | Collect `.nettrace` files for the CLI and child .NET processes. Also enables MSBuild binlog collection. |
| `--collect-dotnet-binlogs` | Collect `.binlog` files for dotnet MSBuild commands. |
| `--skip-build` | Do not restore/build the local Aspire CLI or bundle layout. |

## PowerShell Harness Options

The PowerShell script intentionally mirrors the core validation knobs but does not duplicate the Bash-only process sampling path:

| Parameter | Description |
| --- | --- |
| `-TargetAspirePath PATH` | Aspire CLI under test. Alias: `-AspirePath`. |
| `-ProfilerAspirePath PATH` | Aspire CLI used to host/export dashboard telemetry. Alias: `-DashboardAspirePath`. |
| `-LayoutPath PATH` | Aspire bundle layout path. |
| `-DcpPath PATH` | DCP directory or binary path override. |
| `-OutputRoot PATH` | Output root for harness artifacts. Defaults to `artifacts\tmp\startup-otel-harness`. |
| `-PostStartDelaySeconds SECONDS` | Delay after AppHost start before stopping to allow extra telemetry to flush. |
| `-RequireDcpSpans` | Require exported DCP process/resource spans in addition to CLI/Hosting spans. |
| `-SkipBuild` | Do not restore/build the local Aspire CLI. |

## Output Artifacts

Each run writes to:

```text
artifacts/tmp/startup-otel-harness/<timestamp>/
```

Important files:

| Path | Description |
| --- | --- |
| `summary.json` | Run summary with `ProfilingSessionId`, `TraceId`, `CorrelatedSpanCount`, paths, and optional trace/binlog file lists. |
| `span-summary.json` | Flattened exported span summary for quick inspection. |
| `startup-otel-export.zip` | Dashboard export containing trace JSON. |
| `logs/` | stdout/stderr for harness commands and child processes. |
| `workspace/` | Generated throwaway AppHost fixture. |
| `dotnet-traces/` | Optional `.nettrace` files from `--collect-dotnet-traces`. |
| `binlogs/` | Optional MSBuild `.binlog` files from trace/binlog collection. |

## What the Harness Validates

The shared C# file-based validator (`tools/StartupOtelValidator/ValidateStartupOtelExport.cs`) reads the dashboard export and requires a profiling session with correlated spans from:

- CLI startup/launch spans, including `aspire/cli/start_apphost.spawn_child`.
- Child CLI spans such as `aspire/cli/run`, dotnet build/run spans, backchannel connect spans, and dashboard URL retrieval.
- Hosting spans such as DCP model work, resource creation, resource wait, and DCP resource observation.
- Hosting-to-DCP trace links for created DCP objects.
- Resource wait events, including observed and completed events.
- DCP process/resource spans when `--require-dcp-spans` is specified.

If validation fails, inspect `span-summary.json` first. Then use `startup-otel-export.zip` for the full dashboard export and `logs/` for process output.

## Comparing Before/After Changes

Prefer separate worktrees for baseline and feature measurements so branch switching does not disturb a dirty worktree.

```bash
# Baseline worktree
./eng/scripts/verify-startup-otel.sh --output-root artifacts/tmp/startup-otel-baseline --collect-dotnet-binlogs

# Feature worktree
./eng/scripts/verify-startup-otel.sh --output-root artifacts/tmp/startup-otel-feature --collect-dotnet-binlogs
```

Compare:

- `summary.json` for correlated span count and artifact paths.
- `span-summary.json` for span names, durations, operation IDs, process IDs, and events.
- `binlogs/` for MSBuild cost.
- `.nettrace` files when CPU sampling was collected.

For statistically meaningful wall-clock comparisons, run multiple iterations manually and keep the environment stable. The OTEL harness validates correlation and produces artifacts; it is not a statistical benchmark runner by itself.

## Instrumentation Guidance

Keep profiling APIs coarse-grained and profiling-specific:

- Centralize raw `Activity`, activity names, tag names, and event names in the profiling telemetry type for the area (`Aspire.Cli.Profiling` or `Aspire.Hosting.Profiling`).
- Do not expose one public/internal method per tag. Prefer operation/result-level methods that accept the data for a phase and set multiple tags/events internally.
- Good API shape examples: start a dotnet process span with command, project, working directory, and options; record a process start result with started/process ID; record process completion with exit code and output counts; start a Kubernetes API span with operation/resource type; record retry details as one event method.
- Call sites should describe the operation being profiled, not know tag/event names.
- Do not add profiling tags/events to `Activity.Current` unless the current activity is known to be a profiling activity or profiling has explicitly wrapped it.
- Keep high-cardinality data out of reported telemetry.

## Common Issues

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Required command 'dotnet-trace' was not found` | `--collect-dotnet-traces` was used without the global tool. | Run `dotnet tool install -g dotnet-trace`. |
| `Target Aspire CLI not found` | CLI was not built or `--target-aspire-path` is wrong. | Omit `--skip-build` or pass the correct CLI path. |
| `No exported spans contained aspire.profiling.session_id` | Profiling was not enabled or telemetry was not exported. | Confirm `ASPIRE_PROFILING_ENABLED=true` and inspect `logs/`. |
| `No profiling session contained correlated... spans` | CLI/Hosting/DCP spans did not land in one correlated trace. | Inspect `span-summary.json` for missing scopes or broken parent/trace IDs. |
| `No dotnet-trace files were collected` | Trace collection was requested but no traceable child process was found or attach failed. | Inspect `logs/dotnet-trace-*.stderr.txt`; rerun with a longer `--post-start-delay`. |
| `No dotnet MSBuild binlogs were collected` | Binlog collection was requested but no MSBuild-backed dotnet command ran. | Inspect `logs/start.*`; rerun without `--skip-build` if necessary. |

## Legacy EventSource Tooling

Use the legacy perf scripts only when the task explicitly asks for `Microsoft-Aspire-Hosting` EventSource timing or the `DcpModelCreationStart`/`DcpModelCreationStop` duration:

```bash
./tools/perf/measure-startup-performance.sh
```

```powershell
.\tools\perf\Measure-StartupPerformance.ps1
```

These scripts collect `dotnet-trace` EventSource data and analyze it with `tools/perf/TraceAnalyzer`. They do not validate OTEL span correlation.
