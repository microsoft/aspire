# Aspire CLI output formats

This document is the source of truth for machine-readable Aspire CLI output formats used by tools that integrate with Aspire through the CLI. It is not a full command reference; use `aspire --help` and command-specific help for complete option lists.

## Conventions

Commands that support `--format json` emit JSON intended for tooling. Snapshot commands emit one JSON document after the command has finished collecting data. Streaming commands emit newline-delimited JSON (NDJSON), where each line is a complete JSON document that can be parsed independently.

Streaming output should be the streamed form of the command's JSON content rather than a separate lifecycle protocol. Unless a command documents a different shape, each NDJSON line is an item or batch of items that would otherwise appear in the non-streaming JSON output. Completion is represented by the process exiting and the stream reaching end-of-file, not by a synthetic `complete` event.

Most JSON output uses camel-case property names. Properties whose values are not available can be omitted or written as `null`, depending on the command-specific serializer.

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
    "sdkVersion": "13.0.0",
    "cliPid": 12340,
    "dashboardUrl": "https://localhost:17010/login?t=token"
  }
]
```

Use `aspire ps --format json --resources` to include each AppHost's current resources:

```json
[
  {
    "appHostPath": "/path/to/MyApp.AppHost/MyApp.AppHost.csproj",
    "appHostPid": 12345,
    "resources": [
      {
        "name": "api",
        "displayName": "api",
        "resourceType": "Project",
        "state": "Running",
        "healthStatus": "Healthy",
        "urls": [
          {
            "name": "https",
            "url": "https://localhost:5001"
          }
        ]
      }
    ]
  }
]
```

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

`aspire describe`, `aspire describe --follow`, and `aspire ps --resources` share the resource object shape:

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
    }
  ],
  "summary": {
    "passed": 1,
    "warnings": 1,
    "failed": 0
  }
}
```

`status` is one of `pass`, `warning`, or `fail`. Individual checks can include `details`, `fix`, `link`, or command-specific `metadata`.

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
| `packages` | Packages or projects scanned for capabilities. |
| `capabilities` | Builder methods and other callable capabilities. |
| `handleTypes` | Resource or builder handle types. |
| `dtoTypes` | DTO types used by capabilities. |
| `enumTypes` | Enum types used by capabilities. |
| `exportedValues` | Exported constants or structured values. |
| `diagnostics` | Errors, warnings, and informational diagnostics from capability discovery. |

`aspire sdk dump --format ci` emits a stable text format intended for diffs rather than JSON parsing.
