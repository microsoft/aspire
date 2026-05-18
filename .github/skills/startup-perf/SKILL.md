---
name: startup-perf
description: Measures Aspire startup profiling with CLI self-profile capture and dashboard export traces.
---

# Aspire Startup Profiling with OTEL

Use this skill when measuring, validating, or investigating Aspire startup performance with the CLI self-profile capture flow.

The workflow is the hidden CLI flag `--capture-profile`. It starts a private standalone dashboard collector, enables profiling-only OTEL instrumentation for the command and child AppHost processes, exports a trace archive, and then exits with the wrapped command's exit code.

## Current Profiling Model

Profiling is opt-in and separate from reported telemetry:

- Enable profiling with `ASPIRE_PROFILING_ENABLED=true` or `1`.
- CLI profiling spans use the `Aspire.Cli.Profiling` ActivitySource.
- Hosting profiling spans use the `Aspire.Hosting.Profiling` ActivitySource.
- DCP startup spans use the `dcp.startup` instrumentation scope.
- Reported telemetry must not carry profiling session IDs, high-cardinality profiling tags, or profiling spans.

The older EventSource/dotnet-trace startup measurement scripts still exist for explicit EventSource timing requests. Prefer the self-profile capture flow for current profiling work.

## Prerequisites

Use an Aspire CLI build that contains `--capture-profile`. From a repo checkout:

```bash
./restore.sh
./dotnet.sh build src/Aspire.Cli/Aspire.Cli.csproj /p:SkipNativeBuild=true
```

Repo-local development builds discover the built managed dashboard from `artifacts/bin/Aspire.Managed` when `ASPIRE_REPO_ROOT` points at the checkout. Installed or bundled CLIs discover the dashboard from the bundle. Use `ASPIRE_DASHBOARD_PATH` / `ASPIRE_MANAGED_PATH` when profiling with a custom dashboard build.

## Quick Start

Capture startup for an AppHost and exit automatically after startup:

```bash
./dotnet.sh exec artifacts/bin/Aspire.Cli/Debug/net10.0/aspire.dll run \
  --project tests/TestingAppHost1/TestingAppHost1.AppHost/TestingAppHost1.AppHost.csproj \
  --capture-profile \
  --capture-profile-output artifacts/tmp/startup-profile/profile.zip \
  --non-interactive
```

Capture any other Aspire command:

```bash
aspire ls \
  --capture-profile \
  --capture-profile-output artifacts/tmp/startup-profile/ls-profile.zip \
  --non-interactive
```

If `--capture-profile-output` is omitted, the CLI writes `aspire-profile-<timestamp>-<session>.zip` under the current working directory. For long-lived `run` and `start`, the CLI exits automatically after startup and waits for profiling data to settle before writing the export.

## Self-Profile Options

| Option | Description |
| --- | --- |
| `--capture-profile` | Hidden recursive root option that enables self-profile capture for any Aspire command. |
| `--capture-profile-output PATH` | Output zip path. Relative paths are rooted at the current working directory. |
| `--capture-profile-delay SECONDS` | Optional warmup delay before stopping long-lived `run`/`start` commands. Defaults to 0 seconds. Use only when you intentionally want additional post-start resource activity in the capture. |

## Output Artifacts

The capture writes a dashboard export zip containing:

| Path | Description |
| --- | --- |
| `traces/profile.json` | OTLP JSON trace export from the private dashboard collector. |

Inspect the export:

```bash
unzip -l artifacts/tmp/startup-profile/profile.zip
tmpdir="$(mktemp -d)"
unzip -q artifacts/tmp/startup-profile/profile.zip -d "$tmpdir"
jq -r '.resourceSpans[]?.scopeSpans[]?.scope.name' "$tmpdir/traces/profile.json" | sort | uniq -c
jq -r '.resourceSpans[]?.scopeSpans[]?.spans[]?.name' "$tmpdir/traces/profile.json" | sort | uniq -c
```

Expected startup captures include:

- `Aspire.Cli.Profiling` spans such as `aspire/cli/command`, `aspire/cli/run`, dotnet process spans, backchannel connect spans, and dashboard URL retrieval.
- `Aspire.Hosting.Profiling` spans such as DCP model work, resource creation, resource wait, and DCP resource observation.
- `dcp.startup` spans when the DCP process emits startup telemetry and the scenario is configured to require them.

## Comparing Before/After Changes

Prefer separate worktrees for baseline and feature measurements so branch switching does not disturb a dirty worktree.

```bash
# Baseline worktree
aspire run --project path/to/AppHost.csproj \
  --capture-profile \
  --capture-profile-output artifacts/tmp/startup-profile-baseline/profile.zip \
  --non-interactive

# Feature worktree
aspire run --project path/to/AppHost.csproj \
  --capture-profile \
  --capture-profile-output artifacts/tmp/startup-profile-feature/profile.zip \
  --non-interactive
```

Compare `traces/profile.json` span names, durations, operation IDs, process IDs, events, and trace correlation. For statistically meaningful wall-clock comparisons, run multiple iterations manually and keep the environment stable. The self-profile capture flow produces artifacts; it is not a statistical benchmark runner by itself.

Parallel captures are supported because each `--capture-profile` process allocates its own collector ports and profiling session ID. Always use distinct `--capture-profile-output` paths. If the profiled AppHost launch profile pins dashboard, resource-service, or application ports, those AppHost ports can still conflict across parallel worktrees; use an isolated/randomized profile or adjust the AppHost ports for parallel runs.

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
| `The CLI bundle layout was found, but the dashboard binary (aspire-managed) is missing.` | The CLI could not find a bundled, repo-local, or override dashboard binary. | Build the repo-local CLI, use an installed/bundled CLI, set `ASPIRE_REPO_ROOT` to the checkout, or set `ASPIRE_DASHBOARD_PATH` / `ASPIRE_MANAGED_PATH` to a custom managed dashboard build. |
| Self-profile export contains CLI spans but not Hosting spans | The AppHost did not run through a profiled startup path, or Hosting telemetry did not reach the collector. | Confirm `aspire run` or `aspire start` launched the expected AppHost and inspect `traces/profile.json` for `Aspire.Hosting.Profiling`. |
| `No exported spans contained aspire.profiling.session_id` | Profiling was not enabled or telemetry was not exported. | Confirm `--capture-profile` was parsed before `--` and inspect `traces/profile.json`. |
| `No profiling session contained correlated... spans` | CLI/Hosting/DCP spans did not land in one correlated trace. | Inspect `traces/profile.json` for missing scopes or broken parent/trace IDs. |

## Legacy EventSource Tooling

Use the legacy perf scripts only when the task explicitly asks for `Microsoft-Aspire-Hosting` EventSource timing or the `DcpModelCreationStart`/`DcpModelCreationStop` duration:

```bash
./tools/perf/measure-startup-performance.sh
```

```powershell
.\tools\perf\Measure-StartupPerformance.ps1
```

These scripts collect `dotnet-trace` EventSource data and analyze it with `tools/perf/TraceAnalyzer`. They do not validate OTEL span correlation.
