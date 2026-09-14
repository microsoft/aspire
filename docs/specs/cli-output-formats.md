# Aspire CLI output formats

This document is the source of truth for machine-readable Aspire CLI output formats used by tools that integrate with Aspire through the CLI. It is not a full command reference; use `aspire --help` and command-specific help for complete option lists.

## Conventions

Commands that support `--format json` emit JSON intended for tooling. Snapshot commands emit one JSON document after the command has finished collecting data. Streaming commands emit newline-delimited JSON (NDJSON), where each line is a complete JSON document that can be parsed independently.

Streaming output should be the streamed form of the command's JSON content rather than a separate lifecycle protocol. Unless a command documents a different shape, each NDJSON line is an item or batch of items that would otherwise appear in the non-streaming JSON output. Completion is represented by the process exiting and the stream reaching end-of-file, not by a synthetic `complete` event.

Most JSON output uses camel-case property names. Properties whose values are not available can be omitted or written as `null`, depending on the command-specific serializer.

## Pipeline steps

`aspire do --list-steps --format json` starts the selected AppHost in inspection mode and returns pipeline steps registered while building the application model. Inspection does not run `BeforeStart` callbacks, raise publish events, or execute pipeline steps. The AppHost still starts its registered hosted services so the CLI backchannel can connect. Steps added dynamically by `BeforeStart` are not included. Supplying a step name filters the result to that step and its transitive dependencies.

```json
[
  {
    "name": "deploy-api",
    "description": "Deploy the API",
    "dependsOn": [
      "publish-api"
    ],
    "tags": [
      "deploy-compute"
    ],
    "resourceName": "api"
  }
]
```

| Field | Description |
| ----- | ----------- |
| `name` | Unique pipeline step name. |
| `description` | Optional description of the step. |
| `dependsOn` | Names of direct dependencies. |
| `tags` | Tags associated with the step. |
| `resourceName` | Name of the associated resource, when applicable. |

## AppHost discovery and lifecycle

### `aspire ls`

`aspire ls` lists candidate AppHost project files in the current workspace.

```bash
aspire ls [--all] [--format json] [--stream]
```

By default, the command outputs a human-readable table. Use `--format json` for a stable JSON snapshot after discovery completes:

```json
[
  {
    "path": "/path/to/MyApp.AppHost/MyApp.AppHost.csproj",
    "language": "C#",
    "status": "buildable"
  },
  {
    "path": "/path/to/ts-app/apphost.ts",
    "language": "TypeScript",
    "status": "possibly-unbuildable"
  }
]
```

Use `--format json --stream` to receive discovery results as NDJSON, with one complete AppHost candidate object per line. `--stream` is valid only with `--format json`.

```json
{"path":"/path/to/MyApp.AppHost/MyApp.AppHost.csproj","language":"C#","status":"buildable"}
{"path":"/path/to/ts-app/apphost.ts","language":"TypeScript","status":"possibly-unbuildable"}
```

Stream output is emitted in arrival order from parallel discovery; lines are not sorted. The non-streaming `--format json` snapshot above is sorted by `path`. If you need a deterministic order for streamed output, pipe through your own sort step (for example `jq -s 'sort_by(.path)'`).

If discovery finds no AppHost candidates, the stream emits no lines. The stream does not emit `started`, `complete`, or `canceled` control records; use the command's exit code and end-of-file to detect stream completion.

#### AppHost candidate fields

| Field | Applies to | Description |
| ----- | ---------- | ----------- |
| `path` | All candidates | Full path to the candidate AppHost project file. |
| `language` | All candidates | Detected AppHost language, such as `C#` or `TypeScript`. |
| `status` | All candidates | Candidate validation status, such as `buildable` or `possibly-unbuildable`. |

### `aspire start` and `aspire run --detach`

`aspire start --format json` and `aspire run --detach --format json` emit the same launch result shape:

```json
{
  "appHostPath": "/path/to/MyApp.AppHost/MyApp.AppHost.csproj",
  "appHostPid": 12345,
  "cliPid": 12340,
  "dashboardUrl": "https://localhost:17010/login?t=token",
  "logFile": "/path/to/MyApp.AppHost/.aspire/logs/apphost.log"
}
```

| Field | Description |
| ----- | ----------- |
| `appHostPath` | Full path to the AppHost project file. |
| `appHostPid` | Process ID for the launched AppHost process. |
| `cliPid` | Process ID for the CLI child process that owns the detached AppHost run. |
| `dashboardUrl` | Dashboard URL with login token, when available. |
| `logFile` | Path to the detached AppHost log file. |

## Runtime state

### `aspire ps`

`aspire ps --format json` lists running AppHosts:

```json
[
  {
    "appHostPath": "/path/to/MyApp.AppHost/MyApp.AppHost.csproj",
    "appHostPid": 12345,
    "status": "running",
    "sdkVersion": "13.0.0",
    "cliPid": 12340,
    "dashboardUrl": "https://localhost:17010/login?t=token"
  }
]
```

`aspire ps` returns only AppHost-level information. Use [`aspire describe`](#aspire-describe) to inspect or stream the resources that belong to an AppHost.

`aspire ps --follow --format json` streams newline-delimited AppHost objects. New or changed AppHosts are emitted with `"status": "running"`. When an AppHost stops, it is emitted one last time with `"status": "stopped"` so consumers can remove it from their state:

```json
{"appHostPath":"/path/to/MyApp.AppHost/MyApp.AppHost.csproj","appHostPid":12345,"status":"running"}
{"appHostPath":"/path/to/MyApp.AppHost/MyApp.AppHost.csproj","appHostPid":12345,"status":"stopped"}
```

### Experimental native tray protocol (version 1)

This hidden, opt-in protocol is for the experimental native tray companion, not a replacement for
the existing `ps` snapshot or delta formats. It is experimental and may change.
Its shared DTOs, source-generated JSON context, and limits are defined in
`src/Shared/TrayCliProtocol.cs`.

```bash
aspire ps --protocol-version 1 --follow --format json --non-interactive --nologo
aspire stop --protocol-version 1 --format json --apphost /absolute/path/apphost.cs --pid 12345 --started-at 1789250000000 --non-interactive --nologo
```

Only protocol messages appear on stdout, as compact newline-delimited JSON.
Diagnostics go to stderr; progress, banners, and update notifications do not
appear on stdout. Unsupported versions and invalid arguments exit nonzero.
Help and parser failures, and invalid `ps` invocations, may produce no protocol
message. Consumers must reject missing or malformed responses rather than parse
diagnostic text.

#### Discovery stream

`ps` requires both `--follow` and `--format json`. Discovery is read-only: it
does not collect orphaned AppHosts, prune sockets, or create discovery directories.
The first message is a complete snapshot **after initial discovery finishes**,
including an empty list when there are no AppHosts:

```json
{"version":1,"type":"snapshot","appHosts":[]}
{"version":1,"type":"snapshot","appHosts":[{"appHostPath":"/absolute/path/apphost.cs","appHostPid":12345,"processStartTimeUnixMilliseconds":1789250000000,"dashboardUrl":"http://localhost:18888/login?t=token","health":"healthy"}]}
{"version":1,"type":"heartbeat"}
{"version":1,"type":"snapshot","appHosts":[]}
```

Each snapshot replaces the previous list; removal is represented by absence, not
a `stopped` delta. Lists are ordered by ordinal path, PID, start time, then dashboard
URL. Identical snapshots are suppressed. Discovery polls once per second, and a
capacity-one, latest-wins queue coalesces changes when the consumer is slow.
The initial snapshot is never coalesced away. Heartbeats are emitted every ten
seconds after the first snapshot and do not modify the consumer's state.

`processStartTimeUnixMilliseconds` is the stable process lifetime read from the
operating system using `ProcessStartTimeHelper`. It is omitted when unavailable;
such a row can be displayed and its dashboard opened, but it must not enable Stop.
`dashboardUrl` is optional. Lookups run with at most eight concurrent RPCs, a
two-second deadline per lookup, and a five-second enrichment budget per snapshot.
Timed-out, failed, or not-yet-started lookups leave the URL unavailable without
removing the AppHost or failing discovery. Cancellation stops outstanding waits;
it does not disconnect or stop AppHosts. An RPC that has not acknowledged cancellation
retains its concurrency slot across snapshots, and is not retried while outstanding.
Paths and PIDs identify AppHosts, not launcher CLI
processes. Dashboard URLs may contain login tokens and should not be logged.

`health` is an additive, optional field in protocol version 1. Known values are
`healthy`, `warning`, and `unhealthy`. Missing or unrecognized values mean **unknown**,
not healthy, so older producers remain compatible. Health does not change the
AppHost's process identity.

The CLI maintains one resource snapshot subscription per discovered AppHost
lifetime, using the existing backchannel snapshot watcher and its version
reconciliation. Resource transitions publish complete snapshots without waiting
for discovery changes. They use the same latest-wins output queue, and do not
initiate dashboard URL lookups or poll individual AppHosts for resource health.

The aggregate uses visible resources, excluding both the hidden flag and legacy
`Hidden` state. Successfully completed jobs and resources without a lifetime
(no state or `Active`, with no health reports) are neutral. Failed starts, unhealthy
runtimes, nonzero terminal exit codes, error state styles, and failed health checks
produce `unhealthy`. Waiting, starting, building, stopping, not-started resources,
missing parameter values, degraded health, and pending initial checks produce
`warning`. A failed check takes precedence over another pending check. Running
resources with healthy checks, or no registered checks as in the dashboard, are
healthy; all applicable resources must be healthy for the aggregate to be healthy.
An empty or entirely non-applicable resource set is unknown.

Health is unknown until the initial resource load completes, and becomes unknown
again if the resource stream ends or fails. Resource-stream failures are logged
without removing a still-discovered AppHost or failing discovery. Removed or
replaced AppHosts cancel their subscriptions; a new connection never inherits
the previous lifetime's health.

A discovery failure is not an empty snapshot. It emits a terminal error and exits
nonzero. Exceeding 1,000 AppHosts or 1,048,576 UTF-16 characters per serialized
message also emits a terminal error rather than truncating the list:

```json
{"version":1,"type":"error","errorCode":"discovery_failed"}
{"version":1,"type":"error","errorCode":"limit_exceeded"}
```

The producer cancels and observes its watcher when stdout closes or cancellation
is requested. Heartbeat writes detect a disconnected consumer even if the AppHost
list has not changed. Consumer disconnect is normal completion (exit code 0), not
a discovery error; it does not emit an error message or the failure-log notice.
An already-detected discovery or limit failure still exits nonzero if writing its
terminal error encounters a closed pipe.

#### Exact stop response

The protocol requires `--apphost` with an absolute file path, a positive AppHost
`--pid`, and a positive `--started-at` Unix-millisecond value copied from the
selected snapshot. `--all` and `--force` are forbidden. Directories are not
searched; the full path and PID must match exactly (case-insensitive paths only
on Windows).

Supplying `--started-at` without `--protocol-version 1 --format json` is rejected
before discovery or any stop RPC. A lifetime-guarded request never falls back to
legacy PID-only stopping when the version or format is missing or unsupported.

After selecting one live connection, the CLI re-reads the process's stable
lifetime and requires exact equality with `--started-at` before sending any RPC.
It retains that original connection through shutdown: no re-selection, process
tree escalation, launcher termination, sibling batching, or persistent cleanup.
Success is reported only after the connection-bound stop request succeeds and
exit of that lifetime is verified within the existing ten-second shutdown budget.

Exactly one result is emitted for an executed stop request, with `exitCode` equal
to the process exit code:

```json
{"version":1,"outcome":"stopped","exitCode":0}
```

| Outcome | Meaning |
| ------- | ------- |
| `stopped` | The selected AppHost lifetime exited successfully. |
| `not_found` | No live connection matches the requested full path and PID. |
| `ambiguous` | More than one connection matches; nothing is stopped. |
| `identity_mismatch` | The PID belongs to a different process lifetime; no RPC is sent. |
| `identity_unavailable` | The stable lifetime cannot be read; no RPC is sent. |
| `stop_failed` | Discovery, RPC, or shutdown verification failed, or the request was cancelled. |
| `invalid_request` | Required arguments are missing/invalid, incompatible flags were supplied, or the version is unsupported. |

Every outcome other than `stopped` has a nonzero exit code. Without
`--protocol-version`, existing `ps` and `stop --pid` behavior is unchanged.

### `aspire describe`

`aspire describe --format json` emits a snapshot wrapper with one or more resources:

```json
{
  "resources": [
    {
      "name": "api",
      "displayName": "api",
      "resourceType": "Project",
      "state": "Running",
      "stateStyle": "success",
      "healthStatus": "Healthy",
      "source": "/path/to/Api/Api.csproj",
      "dashboardUrl": "https://localhost:17010/resources/api",
      "urls": [
        {
          "name": "https",
          "displayName": "HTTPS",
          "url": "https://localhost:5001"
        }
      ],
      "environment": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      },
      "properties": {
        "project.path": "/path/to/Api/Api.csproj"
      }
    }
  ]
}
```

`aspire describe --format json --follow` emits NDJSON. Each line is a resource object, not the snapshot wrapper:

```json
{"name":"api","displayName":"api","resourceType":"Project","state":"Starting","stateStyle":"info"}
{"name":"api","displayName":"api","resourceType":"Project","state":"Running","stateStyle":"success","healthStatus":"Healthy"}
```

#### Resource fields

`aspire describe` and `aspire describe --follow` share the resource object shape:

| Field | Description |
| ----- | ----------- |
| `name` | Stable resource name. |
| `displayName` | User-facing resource name. |
| `resourceType` | Resource type, such as `Project`, `Container`, or `Executable`. |
| `uid` | Resource unique ID, when available. |
| `state` | Current resource state. |
| `stateStyle` | UI style hint for the state. |
| `creationTimestamp` | Resource creation time, when available. |
| `startTimestamp` | Resource start time, when available. |
| `stopTimestamp` | Resource stop time, when available. |
| `source` | Source path, image, or executable, depending on resource type. |
| `exitCode` | Process exit code, when the resource has exited. |
| `healthStatus` | Current health status, when available. |
| `dashboardUrl` | Dashboard URL for the resource, when available. |
| `relationships` | Related resources as `{ "type": "...", "resourceName": "..." }`. |
| `urls` | Endpoint objects with `name`, `displayName`, `url`, and `isInternal`. |
| `volumes` | Volume objects with `source`, `target`, `mountType`, and `isReadOnly`. |
| `properties` | Resource properties keyed by property name. |
| `environment` | Environment variables keyed by variable name. |
| `healthReports` | Health report objects keyed by report name. |
| `commands` | Resource command metadata keyed by command name. |

## Logs

### `aspire logs`

`aspire logs --format json` emits a snapshot wrapper:

```json
{
  "logs": [
    {
      "resourceName": "api",
      "timestamp": "2026-05-17T16:00:00.000Z",
      "content": "Now listening on: https://localhost:5001",
      "isError": false
    }
  ]
}
```

`timestamp` is present when `--timestamps` is specified and the log line has a timestamp.

`aspire logs --format json --follow` emits NDJSON. Each line is one log entry:

```json
{"resourceName":"api","content":"Starting","isError":false}
{"resourceName":"api","content":"Unhandled exception","isError":true}
```

| Field | Description |
| ----- | ----------- |
| `resourceName` | Resource that produced the log line. |
| `timestamp` | Parsed timestamp, when requested and available. |
| `content` | Log line content. |
| `isError` | `true` when the line came from stderr. |

## OpenTelemetry

### `aspire otel logs`

`aspire otel logs --format json` emits an array of structured log objects:

```json
[
  {
    "logId": 42,
    "spanId": "6f1d...",
    "traceId": "4bf92f...",
    "message": "Request finished HTTP/1.1 GET /products",
    "severity": "Information",
    "resourceName": "api",
    "attributes": {
      "http.request.method": "GET"
    },
    "source": "Microsoft.AspNetCore.Hosting.Diagnostics",
    "dashboardUrl": "https://localhost:17010/structuredlogs?logEntryId=42"
  }
]
```

`aspire otel logs --format json --follow` emits NDJSON. Each line is a JSON array containing the structured logs from one streamed telemetry batch.

### `aspire otel spans`

`aspire otel spans --format json` emits an array of span objects:

```json
[
  {
    "traceId": "4bf92f...",
    "spanId": "6f1d...",
    "parentSpanId": "3c2a...",
    "kind": "Server",
    "name": "GET /products",
    "status": "Ok",
    "source": "api",
    "destination": "catalogdb",
    "durationMs": 37,
    "timestamp": "2026-05-17T16:00:00Z",
    "attributes": {
      "http.response.status_code": "200 OK"
    },
    "dashboardUrl": "https://localhost:17010/traces/4bf92f...?spanId=6f1d..."
  }
]
```

`aspire otel spans --format json --follow` emits NDJSON. Each line is a JSON array containing the spans from one streamed telemetry batch.

### `aspire otel traces`

`aspire otel traces --format json` emits an array of trace objects:

```json
[
  {
    "traceId": "4bf92f...",
    "durationMs": 142,
    "title": "GET /products",
    "spans": [
      {
        "traceId": "4bf92f...",
        "spanId": "6f1d...",
        "kind": "Server",
        "name": "GET /products",
        "source": "api",
        "durationMs": 37,
        "attributes": {}
      }
    ],
    "hasError": false,
    "timestamp": "2026-05-17T16:00:00Z",
    "dashboardUrl": "https://localhost:17010/traces/4bf92f..."
  }
]
```

`aspire otel traces <trace-id> --format json` emits a single trace object with the same shape.

## Documentation

### `aspire docs`

`aspire docs list --format json` emits an array of documentation pages:

```json
[
  {
    "title": "Service discovery",
    "slug": "service-discovery",
    "summary": "Learn how Aspire apps discover services."
  }
]
```

`aspire docs search <query> --format json` emits an array of search results:

```json
[
  {
    "title": "Service discovery",
    "slug": "service-discovery",
    "content": "Service discovery lets services find each other...",
    "section": "Configuration",
    "score": 12.5
  }
]
```

`aspire docs get <slug> --format json` emits a documentation page:

```json
{
  "title": "Service discovery",
  "slug": "service-discovery",
  "summary": "Learn how Aspire apps discover services.",
  "content": "# Service discovery\n...",
  "sections": [
    "Overview",
    "Configuration"
  ]
}
```

### `aspire docs api`

`aspire docs api list <scope> --format json` emits an array of API items:

```json
[
  {
    "id": "aspire.hosting.applicationmodel",
    "name": "Aspire.Hosting.ApplicationModel",
    "language": "csharp",
    "kind": "namespace",
    "parentId": "aspire.hosting",
    "memberGroup": "Namespaces"
  }
]
```

`aspire docs api search <query> --format json` emits an array of API search results:

```json
[
  {
    "id": "aspire.hosting.applicationmodel.resource",
    "name": "Resource",
    "language": "csharp",
    "kind": "class",
    "parentId": "aspire.hosting.applicationmodel",
    "memberGroup": "Types",
    "summary": "Represents a resource in an Aspire app model.",
    "score": 42.0
  }
]
```

`aspire docs api get <id> --format json` emits one API content item:

```json
{
  "id": "aspire.hosting.applicationmodel.resource",
  "name": "Resource",
  "language": "csharp",
  "kind": "class",
  "url": "https://learn.microsoft.com/dotnet/api/aspire.hosting.applicationmodel.resource",
  "parentId": "aspire.hosting.applicationmodel",
  "memberGroup": "Types",
  "content": "# Resource\n..."
}
```

## Integrations

### `aspire integration list` and `aspire integration search`

`aspire integration list --format json` and `aspire integration search <query> --format json` emit arrays of integration packages:

```json
[
  {
    "name": "Redis",
    "package": "Aspire.Hosting.Redis",
    "version": "13.0.0"
  }
]
```

## Secrets and configuration

### `aspire secret list`

`aspire secret list --format json` emits a JSON object whose property names are secret keys and whose values are secret values:

```json
{
  "ConnectionStrings__redis": "localhost:6379",
  "ApiKey": "secret-value"
}
```

The JSON form includes secret values. Do not redirect it to logs or files unless that destination is allowed to contain secrets.

`aspire secret get <key>` emits the secret value as raw text on stdout. `aspire secret path` emits the user-secrets file path as raw text on stdout.

### `aspire doctor`

`aspire doctor --format json` emits environment checks and a summary:

```json
{
  "checks": [
    {
      "category": "environment",
      "name": "operating-system",
      "status": "pass",
      "message": "Operating system: Linux Ubuntu 24.04",
      "metadata": {
        "osType": "Linux",
        "displayName": "Linux Ubuntu",
        "version": "24.04",
        "description": "Ubuntu 24.04.2 LTS"
      }
    },
    {
      "category": "sdk",
      "name": "dotnet-sdk",
      "status": "pass",
      "message": ".NET SDK is installed."
    },
    {
      "category": "container",
      "name": "daemon-running",
      "status": "warning",
      "message": "Container runtime is not running.",
      "fix": "Start Docker Desktop.",
      "link": "https://learn.microsoft.com/dotnet/aspire/"
    },
    {
      "category": "devtools",
      "name": "vscode-extension",
      "status": "warning",
      "message": "VS Code is installed, but the Aspire extension is not installed",
      "fix": "Install the Aspire extension from the VS Code Marketplace for an integrated Aspire experience.",
      "link": "https://aka.ms/aspire/vscode-extension",
      "metadata": {
        "vsCodeInstalled": true,
        "extensionInstalled": false,
        "extensionId": "microsoft-aspire.aspire-vscode"
      }
    }
  ],
  "summary": {
    "passed": 2,
    "warnings": 2,
    "failed": 0
  }
}
```

`status` is one of `pass`, `warning`, or `fail`. Individual checks can include `details`, `fix`, `link`, or command-specific `metadata`.

The `devtools` category surfaces development-tooling recommendations. The `vscode-extension` check only appears when VS Code is detected: it reports `warning` when the [Aspire VS Code extension](https://aka.ms/aspire/vscode-extension) is missing and `pass` when it is installed. Its `metadata` exposes `vsCodeInstalled` (bool), `extensionInstalled` (bool), and `extensionId` (string).

### `aspire config info`

`aspire config info --json` is a hidden tooling command that emits configuration paths, feature metadata, settings schemas, and advertised CLI capabilities:

```json
{
  "localSettingsPath": "/repo/.aspire/settings.json",
  "globalSettingsPath": "/home/user/.aspire/globalsettings.json",
  "availableFeatures": [
    {
      "name": "feature-name",
      "description": "Feature description.",
      "defaultValue": false
    }
  ],
  "localSettingsSchema": {
    "properties": []
  },
  "globalSettingsSchema": {
    "properties": []
  },
  "configFileSchema": {
    "properties": []
  },
  "capabilities": [
    "capability-name"
  ]
}
```

The `isolated-launch.v1` capability indicates that `aspire run` accepts the `--isolated` option.

## MCP tooling

### `aspire mcp tools`

`aspire mcp tools --format json` emits the MCP tools exposed by running resources:

```json
[
  {
    "resource": "api",
    "tool": "get-products",
    "description": "Gets products.",
    "inputSchema": {
      "type": "object",
      "properties": {
        "category": {
          "type": "string"
        }
      }
    }
  }
]
```

## Hidden tooling outputs

### `aspire extension get-apphosts`

`aspire extension get-apphosts` is a hidden extension-integration command. It emits snake-case JSON:

```json
{
  "selected_project_file": "/repo/MyApp.AppHost/MyApp.AppHost.csproj",
  "all_project_file_candidates": [
    "/repo/MyApp.AppHost/MyApp.AppHost.csproj",
    "/repo/Other.AppHost/Other.AppHost.csproj"
  ]
}
```

### `aspire sdk dump`

`aspire sdk dump --format json` is a hidden SDK-generation command that emits Aspire type-system capabilities:

```json
{
  "packages": [
    {
      "name": "Aspire.Hosting.Redis",
      "version": "13.0.0"
    }
  ],
  "capabilities": [],
  "handleTypes": [],
  "dtoTypes": [],
  "enumTypes": [],
  "exportedValues": [],
  "diagnostics": []
}
```

The top-level arrays are:

| Field | Description |
| ----- | ----------- |
| `packages` | Packages or projects scanned for capabilities. `version` is the version that was **requested**, not the one NuGet resolved: package restore uses a minimum-version reference, so the assembly actually scanned may be newer. Use `aspire sdk export` when the version label has to be exact. Project references are omitted because they have no version. |
| `capabilities` | Builder methods and other callable capabilities. |
| `handleTypes` | Resource or builder handle types. |
| `dtoTypes` | DTO types used by capabilities. |
| `enumTypes` | Enum types used by capabilities. |
| `exportedValues` | Exported constants or structured values. |
| `diagnostics` | Errors, warnings, and informational diagnostics from capability discovery. |

`aspire sdk dump --format ci` emits a stable text format intended for diffs rather than JSON parsing.

### `aspire sdk export`

`aspire sdk export --package Name@Version --language typescript` restores the exact integration package version and writes one canonical JSON document to standard output. `Aspire.Hosting` can only be exported at the CLI's SDK version. The selected language's code-generation package cannot be exported because it supplies the generator instead of an integration API surface. Omit `--package` to export `Aspire.Hosting` at the running CLI's SDK version. Diagnostics are written to standard error.

The top-level fields are `schemaVersion`, `language`, `generator`, `package`, `modules`, and `declarations`. The language exporter owns the schema; the CLI passes it through without reshaping it.
