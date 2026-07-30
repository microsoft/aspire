# Agent diagnostics with Dashboard run IDs

## Summary

Aspire Dashboard run IDs let an agent preserve and query telemetry from separate AppHost executions. An agent can use one run as evidence of a failure, make or evaluate a change in another run, and compare equivalent queries without losing the original data when the AppHost restarts.

This document describes the end-to-end diagnostic workflow. The underlying storage and API contracts are documented in [Dashboard persistence](dashboard-persistence.md), [Dashboard Telemetry HTTP API](dashboard-http-api.md), and [Aspire CLI output formats](cli-output-formats.md).

## Mental model

A run ID identifies one Dashboard database created with `Run` persistence. The database contains the resources, console logs, structured logs, traces, spans, and metrics captured during that AppHost execution.

Run IDs have the following properties:

- Omitting `--run-id` when starting an AppHost creates a UTC timestamp ID.
- Supplying `--run-id` creates a memorable name such as `incident-42` or `after-timeout-fix`.
- A named ID must be unique within the resolved Dashboard application and data directory. Invalid or duplicate IDs fail the launch instead of reusing or overwriting data.
- Run IDs are application-scoped, not globally unique. Record the AppHost or application together with the ID.
- Historical runs are immutable. A query with a run ID is a snapshot query and cannot use `--follow`.
- An unknown or pruned ID is an error. Historical queries never silently fall back to the current run.

Persistence allows data to outlive the AppHost and Dashboard processes, but a query still needs a reachable Dashboard API associated with the same application and data directory. In the common workflow, the Dashboard for the current AppHost run serves both current and historical data.

## End-to-end investigation

### 1. Assign or capture the failing run ID

Prefer a meaningful ID when a reproduction is planned:

```bash
aspire start --run-id incident-42-before --format json
```

`aspire run` supports the same option. For a detached run, it emits the same launch result shape as `aspire start`:

```bash
aspire run --detach --run-id incident-42-before --format json
```

The JSON result includes the effective `runId`. If an automatic ID is sufficient, capture it rather than trying to reconstruct it later:

```bash
RUN_ID=$(aspire start --format json | jq -r '.runId')
```

Keep the run ID in the agent's working notes with:

- the AppHost path;
- the code revision and relevant configuration;
- the workload or reproduction steps;
- the expected and actual behavior; and
- the approximate reproduction time.

This context prevents a technically valid comparison between runs that exercised different code paths or environments.

### 2. Reproduce before changing code

Run the smallest workload that demonstrates the issue. While the failing run is active, inspect live state with commands such as `aspire describe`, and omit `--run-id` when streaming live logs.

Capture a narrow initial evidence set rather than dumping all telemetry:

```bash
aspire otel traces apiservice --has-error --limit 50 --format json
aspire otel logs apiservice --severity Error --limit 100 --format json
aspire logs apiservice --search "failed timeout" --tail 100 --format json
```

The run ID is the durable handle for later queries; the initial evidence helps identify useful resource names, search terms, trace IDs, and time windows.

### 3. Create a comparison run

Stop the failing run, make or select the candidate change, and use a different run ID:

```bash
aspire start --run-id incident-42-after --format json
```

Repeat the same workload. Keep inputs, resource configuration, request count, and timing as similar as practical. A useful comparison changes one relevant variable at a time.

After the workload completes, both runs can be queried through the active Dashboard. Use the same resource, search, severity, error, and limit filters for each query:

```bash
aspire otel traces apiservice --run-id incident-42-before --has-error --limit 50 --format json
aspire otel traces apiservice --run-id incident-42-after --has-error --limit 50 --format json

aspire otel logs apiservice --run-id incident-42-before --severity Error --limit 100 --format json
aspire otel logs apiservice --run-id incident-42-after --severity Error --limit 100 --format json
```

Do not infer a fix only from a lower error count. Compare the operation names, status, duration, relevant attributes, and whether the expected workload actually ran in both runs.

### 4. Move from symptoms to a correlated trace

Use each telemetry type for the question it answers:

| Question | CLI command | MCP tool |
|----------|-------------|----------|
| Did the process start, crash, or write to stderr? | `aspire logs <resource> --run-id <id>` | `list_console_logs` |
| What application or framework errors were recorded? | `aspire otel logs <resource> --run-id <id>` | `list_structured_logs` |
| Which distributed operation failed or became slow? | `aspire otel traces <resource> --run-id <id>` | `list_traces` |
| Which span failed and what attributes did it carry? | `aspire otel spans <resource> --run-id <id>` | Use the spans returned for a trace |
| What logs occurred inside one trace? | `aspire otel logs --trace-id <trace-id> --run-id <id>` | `list_trace_structured_logs` |

A productive sequence is:

1. Find an errored or unexpectedly slow trace.
2. Record its trace ID and the resources participating in it.
3. Inspect spans for the trace to locate the first failing dependency or operation.
4. Query structured logs with the same trace ID.
5. Use console logs only when startup, process output, or missing instrumentation requires them.
6. Run the equivalent queries against the comparison run.

For example:

```bash
aspire otel traces apiservice --run-id incident-42-before --has-error --format json
aspire otel spans --run-id incident-42-before --trace-id 4bf92f3577b34da6a3ce929d0e0e4736 --format json
aspire otel logs --run-id incident-42-before --trace-id 4bf92f3577b34da6a3ce929d0e0e4736 --format json
```

Trace IDs are meaningful only within the run in which they were produced. Do not expect the comparison workload to generate the same trace ID; correlate equivalent operations using names, resources, attributes, and workload inputs.

### 5. Report evidence with its identity

An agent's conclusion should cite enough information for another person or agent to repeat the query:

- application or AppHost;
- failing and comparison run IDs;
- resource names;
- command or MCP tool arguments;
- trace IDs and relevant timestamps;
- the observed difference; and
- any confounding differences between the runs.

Summarize the smallest evidence that supports the conclusion. Avoid pasting complete log or trace collections when a filtered excerpt, count, or correlated trace explains the result.

## MCP workflow

The CLI MCP server exposes historical selection through an optional `runId` argument on four tools:

| Tool | Required arguments | Useful optional arguments |
|------|--------------------|---------------------------|
| `list_console_logs` | `resourceName` | `search`, `runId` |
| `list_structured_logs` | None | `resourceName`, `search`, `runId` |
| `list_traces` | None | `resourceName`, `search`, `runId` |
| `list_trace_structured_logs` | `traceId` | `search`, `runId` |

An agent investigating `incident-42-before` could make these conceptual tool calls:

```json
{"resourceName":"apiservice","search":"timeout","runId":"incident-42-before"}
```

Call `list_traces` with the same `resourceName` and `runId`, select a relevant trace ID, and then call:

```json
{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","runId":"incident-42-before"}
```

against `list_trace_structured_logs`.

When `runId` is omitted, the tools query the current run. In particular, `list_console_logs` can use the live AppHost backchannel without a run ID, but switches to the Dashboard HTTP snapshot when a run ID is supplied. Always pass the run ID through every call in one historical investigation so resources from the current run are not accidentally mixed with telemetry from the historical run.

MCP responses are intentionally bounded. Use `resourceName`, `search`, and trace correlation to narrow results before drawing a conclusion.

## Direct HTTP workflow

Agents that integrate directly with the Dashboard can pass `runId` to every telemetry read endpoint:

```http
GET /api/telemetry/resources?runId=incident-42-before
GET /api/telemetry/console-logs?resource=apiservice&runId=incident-42-before
GET /api/telemetry/logs?resource=apiservice&severity=Error&runId=incident-42-before
GET /api/telemetry/traces?resource=apiservice&hasError=true&runId=incident-42-before
GET /api/telemetry/spans?traceId=4bf92f3577b34da6a3ce929d0e0e4736&runId=incident-42-before
```

Use the Dashboard's configured authentication mode and send `X-API-Key` when API-key authentication is enabled. Treat Dashboard URLs, login tokens, API keys, telemetry, environment values, and persisted databases as sensitive diagnostic data.

The HTTP contract is strict:

- no `runId` selects the current run;
- an unknown or expired `runId` returns RFC 7807 `404 Not Found`;
- `follow=true` with `runId` returns `400 Bad Request`; and
- a historical response is read-only and request-scoped, so concurrent queries can select different runs safely.

## Operational constraints

### Retention

`Run` mode retains the current run and the five newest unpinned historical runs per application. Old unpinned runs can be pruned as new runs start. Pin important evidence from the Dashboard run selector before starting many comparison runs. Pinned runs are retained in addition to the unpinned historical limit, but an external issue, artifact, or telemetry export is still more appropriate when evidence must survive local storage loss or move between machines.

### Availability

Historical telemetry depends on a valid retained run database and compatible schema. A missing result can mean:

- the run ID belongs to another application or data directory;
- retention pruned the run;
- no reachable Dashboard is serving the application's persisted data;
- the run used `None` or `Resume` rather than `Run` persistence;
- console output was not persisted because its stream was never viewed or exported;
- telemetry was never emitted or arrived after the workload ended; or
- the stored schema is incompatible with the current Dashboard.

Do not respond to a run-not-found error by dropping `--run-id`; that changes the question to the current run and can produce a plausible but incorrect diagnosis.

### Scope

Run IDs select persisted Dashboard data. They do not make live lifecycle commands historical. Commands such as `aspire describe`, resource start or stop, and resource commands still operate on the current AppHost.

Dashboard persistence is intended for local development and diagnostics. It is not a durable production telemetry backend, does not replicate data, and delegates protection at rest to directory access controls.

## Agent checklist

Before concluding an investigation, verify that:

- the run ID was captured from launch output or explicitly assigned;
- failing and comparison IDs are different and belong to the same application;
- the same workload and filters were used for both runs;
- every historical CLI or MCP query carried the intended run ID;
- traces were correlated to spans and structured logs where possible;
- missing telemetry was not mistaken for successful behavior;
- retention and Dashboard availability were considered; and
- the final report includes the run IDs and reproducible query details.