# Dashboard Telemetry HTTP API

This document describes the HTTP API for exposing telemetry data (spans, logs, and traces) from the Aspire Dashboard, enabling CLI commands and other programmatic access.

## Overview

The Dashboard exposes telemetry data via a REST HTTP API that provides data in **standard OTLP JSON format**. This enables CLI tools, automation scripts, and other consumers to access the same telemetry data visible in the Dashboard UI.

**Related:**

- Issue: <https://github.com/microsoft/aspire/issues/14138>
- PR: <https://github.com/microsoft/aspire/pull/14168>
- MCP implementation: `src/Aspire.Dashboard/Mcp/AspireTelemetryMcpTools.cs`

## Design Philosophy

1. **OTLP JSON Format**: Uses standard OpenTelemetry Protocol JSON format for responses, enabling interoperability with OTLP-compatible tools.
2. **RESTful Design**: Standard HTTP verbs and resource-oriented URLs.
3. **Configurable Auth**: Supports API key authentication or unsecured mode (shared with MCP).
4. **Push-Based Streaming**: Real-time streaming via NDJSON with O(1) memory per watcher.

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Response format | OTLP JSON | Standard format, reuses existing `TelemetryExportService` |
| Streaming format | NDJSON (Newline Delimited JSON) | Simple, widely supported, easy to parse |
| Streaming implementation | Push-based with bounded channels | O(1) memory, no re-querying on updates |
| Endpoint naming | `/spans` not `/traces` | OTLP format returns spans; traces are mutable groupings |
| No `/{spanId}` endpoint | Query by `?traceId=` instead | SpanId is not globally unique across traces |

---

## Configuration

The API can be enabled/disabled and configured via `Dashboard:Api` settings:

```json
{
  "Dashboard": {
    "Api": {
      "Enabled": true,
      "AuthMode": "ApiKey",
      "PrimaryApiKey": "your-api-key"
    }
  }
}
```

| Setting | Values | Default | Description |
|---------|--------|---------|-------------|
| `Enabled` | `true`, `false` | `true` | Whether the Telemetry HTTP API is enabled |
| `AuthMode` | `ApiKey`, `Unsecured` | `Unsecured` | Authentication mode for the API |
| `PrimaryApiKey` | string | - | API key for authentication (required when `AuthMode=ApiKey`) |
| `SecondaryApiKey` | string | - | Optional secondary API key for key rotation |

**Notes:**

- The API shares the same port as the Dashboard frontend (default: 18888).
- Hosters may set `Enabled: false` to disable the API for security.
- API keys are shared with MCP configuration—setting either configures both.
- When running in unsecured mode, a warning is logged on first API request.

---

## Part 1: HTTP API

### Endpoints

#### Spans

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/telemetry/spans` | List spans in OTLP JSON format |

#### Structured Logs

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/telemetry/logs` | List structured logs |

#### Traces

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/telemetry/traces` | List traces in OTLP JSON format |
| GET | `/api/telemetry/traces/{traceId}` | Get a specific trace with all spans |

### Authentication

All endpoints require authentication based on the configured `AuthMode`:

**API Key mode (`X-API-Key` header):**

```http
GET /api/telemetry/spans HTTP/1.1
Host: localhost:18888
X-API-Key: <api-key>
```

**Unsecured mode:** No authentication required (warning logged on first request).

### Error Responses

| Status | Description |
|--------|-------------|
| 401 Unauthorized | Missing or invalid API key (when `AuthMode=ApiKey`) |
| 404 Not Found | Unknown resource specified in `?resource=` filter, or trace not found |

---

### `GET /api/telemetry/spans`

List spans with optional filtering.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `resource` | string | No | - | Filter to spans from this resource (returns 404 if not found) |
| `traceId` | string | No | - | Filter to spans with this trace ID |
| `hasError` | bool | No | - | Filter by error status (`true` = only errors, `false` = exclude errors) |
| `limit` | int | No | 200 | Maximum spans to return |
| `follow` | bool | No | false | Enable streaming mode |

**Response:** `200 OK`

```json
{
  "data": {
    "resourceSpans": [
      {
        "resource": {
          "attributes": [
            { "key": "service.name", "value": { "stringValue": "apiservice" } }
          ]
        },
        "scopeSpans": [
          {
            "scope": { "name": "Aspire.Hosting" },
            "spans": [
              {
                "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
                "spanId": "00f067aa0ba902b7",
                "name": "GET /api/products",
                "kind": 2,
                "startTimeUnixNano": "1706425800000000000",
                "endTimeUnixNano": "1706425800142000000",
                "attributes": [
                  { "key": "http.method", "value": { "stringValue": "GET" } },
                  { "key": "http.status_code", "value": { "stringValue": "200" } }
                ],
                "status": { "code": 1 }
              }
            ]
          }
        ]
      }
    ]
  },
  "totalCount": 1500,
  "returnedCount": 200
}
```

**Response:** `404 Not Found` — Unknown resource specified

**Streaming Mode (`?follow=true`):**

When `follow=true` is specified:

- Response uses `Content-Type: application/x-ndjson`
- Each line is a complete OTLP JSON object (one span per line)
- Connection stays open, new spans are pushed in real-time
- Uses push-based delivery with O(1) memory overhead
- Note: `limit` parameter is not supported for streaming

```text
{"resourceSpans":[...]}
{"resourceSpans":[...]}
{"resourceSpans":[...]}
```

---

### `GET /api/telemetry/logs`

List structured logs with optional filtering.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `resource` | string | No | - | Filter to logs from this resource (returns 404 if not found) |
| `traceId` | string | No | - | Filter to logs from this trace |
| `severity` | string | No | - | Minimum severity level (includes this level and higher) |
| `limit` | int | No | 200 | Maximum logs to return |
| `follow` | bool | No | false | Enable streaming mode |

**Severity Values:** `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`

The severity filter uses "greater than or equal" logic. For example, `severity=Error` returns both Error and Critical logs.

**Response:** `200 OK`

**Response:** `404 Not Found` — Unknown resource specified

```json
{
  "data": {
    "resourceLogs": [
      {
        "resource": {
          "attributes": [
            { "key": "service.name", "value": { "stringValue": "apiservice" } }
          ]
        },
        "scopeLogs": [
          {
            "scope": { "name": "Microsoft.AspNetCore" },
            "logRecords": [
              {
                "timeUnixNano": "1706425800123000000",
                "severityNumber": 9,
                "severityText": "Information",
                "body": { "stringValue": "Request starting HTTP/1.1 GET /api/products" },
                "attributes": [
                  { "key": "RequestPath", "value": { "stringValue": "/api/products" } }
                ],
                "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
                "spanId": "00f067aa0ba902b7"
              }
            ]
          }
        ]
      }
    ]
  },
  "totalCount": 5000,
  "returnedCount": 200
}
```

**Streaming Mode (`?follow=true`):**

Same as spans — uses NDJSON format with one log entry per line. Note: `limit` parameter is not supported for streaming.

---

### `GET /api/telemetry/traces`

List traces in OTLP JSON format (snapshot only, no streaming).

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `resource` | string | No | - | Filter to traces involving this resource |
| `hasError` | bool | No | - | Filter by error status (`true` = only errors, `false` = exclude errors) |
| `limit` | int | No | 100 | Maximum traces to return |

**Response:** `200 OK`

Returns traces as OTLP JSON (same format as spans endpoint, but grouped by trace):

```json
{
  "data": {
    "resourceSpans": [
      {
        "resource": {
          "attributes": [
            { "key": "service.name", "value": { "stringValue": "frontend" } }
          ]
        },
        "scopeSpans": [
          {
            "scope": { "name": "Microsoft.AspNetCore" },
            "spans": [
              {
                "traceId": "4bf92f3577b34da6a3ce929d0e0e4736",
                "spanId": "00f067aa0ba902b7",
                "name": "GET /api/products",
                ...
              }
            ]
          }
        ]
      }
    ]
  },
  "totalCount": 50,
  "returnedCount": 50
}
```

---

### `GET /api/telemetry/traces/{traceId}`

Get a specific trace with all spans in OTLP format.

**Response:** `200 OK`

```json
{
  "data": {
    "resourceSpans": [
      {
        "resource": { ... },
        "scopeSpans": [
          {
            "scope": { ... },
            "spans": [ ... ]
          }
        ]
      }
    ]
  },
  "totalCount": 5,
  "returnedCount": 5
}
```

**Response:** `404 Not Found` — Trace not found

---

## Part 2: Response Format

### OTLP JSON

All responses use the standard [OpenTelemetry Protocol JSON format](https://opentelemetry.io/docs/specs/otlp/). This is the same format used by OTLP exporters, ensuring compatibility with other OpenTelemetry tools.

The Dashboard uses existing conversion methods in `TelemetryExportService`:

- `ConvertTracesToOtlpJson()` — Converts traces to OTLP JSON
- `ConvertLogsToOtlpJson()` — Converts logs to OTLP JSON
- `ConvertTraceToJson()` — Converts a single trace to JSON string

### Response Wrapper

List endpoints wrap OTLP data with counts:

```json
{
  "data": { ... },        // OTLP JSON (resourceSpans or resourceLogs)
  "totalCount": 1500,     // Total matching items in repository
  "returnedCount": 200    // Items returned (limited)
}
```

### Streaming Format

Streaming endpoints use NDJSON (Newline Delimited JSON):

- `Content-Type: application/x-ndjson`
- Each line is a complete, valid JSON object
- No wrapper — just raw OTLP JSON per line
- Suitable for `curl`, `jq`, and streaming parsers

---

## Part 3: Implementation Details

### Push-Based Streaming

The streaming implementation uses a push-based architecture for efficiency:

1. **Watcher Registration**: When a client starts streaming, a watcher is registered with a bounded channel (1000 items, drop oldest).

2. **Push on Add**: When new spans/logs are added to the repository, they are pushed directly to all registered watchers.

3. **Deduplication**: Uses `HashSet<string>` of span/log IDs to avoid duplicates when initial snapshot overlaps with pushed items.

4. **Lazy Initialization**: Watcher lists are lazily allocated to avoid memory overhead when no watchers are registered.

5. **Cleanup**: Watchers are removed in `finally` blocks and channels are completed on `Dispose()`.

### Files

| File | Purpose |
|------|---------|
| `src/Aspire.Dashboard/Api/TelemetryApiService.cs` | API service with endpoint handlers |
| `src/Aspire.Dashboard/Api/ApiAuthenticationHandler.cs` | Shared authentication handler (API + MCP) |
| `src/Aspire.Dashboard/DashboardEndpointsBuilder.cs` | Endpoint registration |
| `src/Aspire.Dashboard/Otlp/Storage/TelemetryRepository.cs` | Main repository |
| `src/Aspire.Dashboard/Otlp/Storage/TelemetryRepository.Watchers.cs` | Push-based streaming (`WatchSpansAsync`, `WatchLogsAsync`) |
| `src/Aspire.Dashboard/Model/TelemetryExportService.cs` | OTLP JSON conversion |

---

## Part 4: CLI Commands

### `aspire otel`

Subcommand group for telemetry operations.

```text
aspire otel logs [resource] [options]
aspire otel spans [resource] [options]
aspire otel trace [traceId] [options]
```

### Commands

#### `aspire otel logs`

List or stream structured logs.

```bash
aspire otel logs                        # Recent logs
aspire otel logs frontend               # Logs from frontend service
aspire otel logs --severity error       # Errors and above
aspire otel logs --trace-id 4bf92f...   # Logs correlated to a trace
aspire otel logs --follow               # Stream in real-time
aspire otel logs --limit 50             # Cap results
aspire otel logs --json                 # Raw OTLP JSON output
```

| Flag | Description |
|------|-------------|
| `--severity` | Minimum severity (Trace, Debug, Information, Warning, Error, Critical) |
| `--trace-id` | Filter to logs from a specific trace |
| `--follow` | Stream new logs as they arrive |
| `--limit` | Maximum number of logs to return |
| `--has-error` | Filter to error logs |
| `--json` | Output raw OTLP JSON |

#### `aspire otel spans`

List or stream raw spans (power user / scripting).

```bash
aspire otel spans                       # Recent spans
aspire otel spans catalogdb             # Spans from catalogdb service
aspire otel spans --trace-id 4bf92f...  # Spans in a trace
aspire otel spans --has-error           # Failed spans only
aspire otel spans --follow              # Stream in real-time
aspire otel spans --json | jq ...       # Pipe to tools
```

| Flag | Description |
|------|-------------|
| `--trace-id` | Filter to spans from a specific trace |
| `--follow` | Stream new spans as they arrive |
| `--limit` | Maximum number of spans to return |
| `--has-error` | Filter to spans with error status |
| `--json` | Output raw OTLP JSON |

#### `aspire otel trace`

List traces or show a trace waterfall (snapshot only, no streaming).

```bash
aspire otel trace                       # List recent traces
aspire otel trace 4bf92f3577b34d        # Show waterfall view
aspire otel trace --has-error           # Failed traces only
aspire otel trace 4bf92f... --logs      # Include correlated logs
```

**Trace list output:**

```text
TRACE ID         DURATION  SERVICES              ROOT SPAN                STATUS
4bf92f3577b34d   142ms     frontend→api→db       GET /api/products        ✓
c21a35b944...    3.7s      catalogdb→postgres    Initializing catalog     ✓
28336000bd...    87ms      orderprocessor→rabbit rabbitmq connect         ✓
```

**Trace waterfall output:**

```text
┌─ frontend (52ms)
│  GET /api/products
│
├──┬─ apiservice (48ms)
│  │  GET /api/products
│  │
│  └──┬─ catalogdb (12ms)
│     │  SELECT * FROM products
│     │
│     └─ catalogdb (3ms)
│        SELECT * FROM inventory
```

| Flag | Description |
|------|-------------|
| `--limit` | Maximum number of traces to list |
| `--has-error` | Filter to traces with errors |
| `--logs` | Include correlated logs (when showing a specific trace) |
| `--json` | Output raw OTLP JSON |

### Streaming Support

| Command | `--follow` | Notes |
|---------|------------|-------|
| `otel logs` | ✅ | Natural - like `tail -f` |
| `otel spans` | ✅ | Stream raw spans as they arrive |
| `otel trace` | ❌ | Traces are groupings with no "complete" signal |

### Implementation Notes

- **`logs`**: Direct API call to `/api/telemetry/logs`
- **`spans`**: Direct API call to `/api/telemetry/spans`
- **`trace` list**: API call to `/api/telemetry/traces` (returns summaries)
- **`trace` show**: API call to `/api/telemetry/traces/{traceId}` (returns full trace with spans)

### CLI → Dashboard Communication

```text
┌─────────┐                      ┌─────────┐                    ┌───────────┐
│   CLI   │─── Backchannel ─────▶│ AppHost │                    │ Dashboard │
│         │  GetDashboardInfo()  │         │                    │           │
│         │◀─── URL + Token ─────│         │                    │           │
│         │                      └─────────┘                    │           │
│         │                                                     │           │
│         │─────────────── HTTP GET /api/telemetry/spans ──────▶│           │
│         │                      X-API-Key: <token>             │           │
│         │◀──────────────────── JSON Response ─────────────────│           │
└─────────┘                                                     └───────────┘
```

---

## Part 5: Testing

### Unit/Integration Tests

Location: `tests/Aspire.Dashboard.Tests/Integration/TelemetryApiTests.cs`

25 integration tests covering:

**Spans:**
- `GetSpans_UnsecuredMode_Returns200`
- `GetSpans_WithQueryParameters_Returns200`
- `GetSpans_WithUnknownResource_Returns404`
- `GetSpans_WithApiKey_Returns200`
- `GetSpans_WithWrongApiKey_ReturnsUnauthorized`
- `GetSpans_WithSecondaryApiKey_Returns200`
- `GetSpans_McpKeyFallback_Returns200`
- `GetSpans_StreamingMode_ReturnsNdjsonContentType`

**Logs:**
- `GetLogs_UnsecuredMode_Returns200`
- `GetLogs_WithTraceIdFilter_Returns200`
- `GetLogs_WithUnknownResource_Returns404`
- `GetLogs_WithApiKey_Returns200`
- `GetLogs_WithWrongApiKey_ReturnsUnauthorized`
- `GetLogs_StreamingMode_ReturnsNdjsonContentType`

**Traces:**
- `GetTraces_UnsecuredMode_Returns200`
- `GetTraces_WithHasErrorFilter_Returns200`
- `GetTraces_WithUnknownResource_Returns404`
- `GetTraceById_NotFound_Returns404`
- `GetTraces_WithApiKey_Returns200`

**Configuration:**
- `Configuration_ApiAuthModeDefaults_WhenNotConfigured`
- `Configuration_ApiKeyFromMcp_CopiedToApi`
- `Configuration_ApiKeyExplicit_OverridesMcp`
- `Configuration_ApiEnabled_DefaultsToTrue`
- `Configuration_ApiDisabled_Returns404`

Location: `tests/Aspire.Dashboard.Tests/TelemetryApiServiceTests.cs`

6 unit tests covering API service logic:

- `FollowSpansAsync_StreamsAllSpans`
- `FollowLogsAsync_StreamsAllLogs`
- `GetSpans_HasErrorFalse_ExcludesErrorSpans`
- `GetSpans_HasErrorTrue_OnlyReturnsErrorSpans`
- `GetTraces_HasErrorFalse_ExcludesTracesWithErrors`
- `GetTraces_HasErrorTrue_OnlyReturnsTracesWithErrors`

Location: `tests/Aspire.Dashboard.Tests/TelemetryRepositoryTests/TelemetryRepositoryTests.cs`

7 watcher tests covering:

- `WatchSpansAsync_ReturnsExistingSpans_ThenNewSpans`
- `WatchSpansAsync_CanBeCancelled`
- `WatchSpansAsync_FiltersById_WhenResourceKeyProvided`
- `WatchLogsAsync_ReturnsExistingLogs_ThenNewLogs`
- `WatchLogsAsync_CanBeCancelled`
- `WatchLogsAsync_FiltersAppliedWhenPushing`
- `WatchLogsAsync_SeverityFilterApplied`

---

## Comparison with MCP

| Aspect | MCP | HTTP API |
|--------|-----|----------|
| Protocol | JSON-RPC over SSE | REST over HTTP |
| Auth | X-API-Key header | Same |
| Response format | Text with embedded JSON | OTLP JSON |
| Streaming | Yes (SSE) | Yes (NDJSON) |
| Client | MCP SDK required | Any HTTP client |
| Use case | AI assistants | CLI, automation, scripts |

---

## Future Considerations

### Metrics

When metrics support is added:

```text
GET /api/telemetry/metrics
GET /api/telemetry/metrics/{metricName}
```

### Pagination

For large datasets, consider cursor-based pagination:

```text
GET /api/telemetry/logs?limit=100&after=12345
```
