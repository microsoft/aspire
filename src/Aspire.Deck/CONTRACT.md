# Aspire Deck backend ⇄ UI contract

This is the single source of truth for the boundary between the Rust (Tauri) or dashboard HTTP
backend and the web UI. Both sides MUST agree on these names and shapes.

The backend exposes **commands** (request/response via `@tauri-apps/api/core` `invoke`) and
**events** (push via `@tauri-apps/api/event` `listen`). All payloads are JSON; field names are
`camelCase`.

## Versioned ASP.NET Core backend

The React migration uses a separately runnable Native AOT backend at `/api/dashboard`. This
contract grows capability-by-capability while the existing Blazor dashboard and its unversioned
`/api/deck` transport remain available.

The published Native AOT backend also serves the bundled React production application. Its hosted
`index.html` marks the transport as `aot`, so the UI negotiates `/api/dashboard` on the same origin
without a query parameter. Vite and Tauri retain the standalone build marker and their existing
development/bridge defaults. SPA fallback handles extensionless UI routes only and never converts
an unknown `/api/*` or missing asset request into HTML.

React starts with `GET /api/dashboard` and intersects the returned versions with the versions it
understands. It selects the highest compatible version and uses that entry's `basePath` for every
capability advertised by the entry. A client must fail the new capability explicitly when there is
no compatible version; it must not guess a route or silently reinterpret an incompatible payload.

The first discovery response is:

```json
{
  "product": "Aspire.Dashboard",
  "versions": [
    {
      "version": 1,
      "basePath": "/api/dashboard/v1",
      "capabilities": ["configuration", "shell", "culture", "authentication", "manage-data", "resources", "resources-live", "commands", "structured-logs", "structured-logs-live", "structured-logs-clear", "traces", "traces-live", "traces-clear", "metrics", "metrics-series", "metrics-clear", "console-logs", "console-logs-live", "terminal", "interactions"]
    }
  ]
}
```

Version 1 currently defines twenty-one capabilities when the side-by-side legacy authority is
configured. Servers without that authority omit `shell`, `culture`, `authentication`, and
`manage-data`:

| Capability | Route | Response |
| --- | --- | --- |
| `configuration` | `GET {basePath}/config` | `DashboardConfiguration` |
| `shell` | `GET {basePath}/shell` | `DeckConfig` |
| `culture` | `GET {basePath}/culture?language={name}&redirectUrl={localUrl}` | Redirect and culture cookie |
| `authentication` | `POST {basePath}/authentication/logout` | Dashboard sign-out response |
| `manage-data` | `GET/POST {basePath}/manage-data[/export\|/import\|/remove]` | Inventory JSON, ZIP export, telemetry import, or removal response |
| `resources` | `GET {basePath}/resources` | `Resource[]` |
| `resources-live` | SignalR hub at `{basePath}/resources/live` | `ResourcesEvent` server stream |
| `commands` | `POST {basePath}/commands/execute` | `CommandResponse` |
| `structured-logs` | `GET {basePath}/structured-logs` | `StructuredLogsSnapshot` |
| `structured-logs-live` | SignalR hub at `{basePath}/structured-logs/live` | `StructuredLogsEvent` server stream |
| `structured-logs-clear` | `DELETE {basePath}/structured-logs` | No content |
| `traces` | `GET {basePath}/traces` | `TraceSnapshot` |
| `traces-live` | SignalR hub at `{basePath}/traces/live` | `TraceEvent` server stream |
| `traces-clear` | `DELETE {basePath}/traces` | No content |
| `metrics` | `GET {basePath}/metrics` | `MetricSummary[]` |
| `metrics-series` | `GET {basePath}/metrics/series` | `MetricSeriesResponse` |
| `metrics-clear` | `DELETE {basePath}/metrics` | No content |
| `console-logs` | Capability marker for resource console output | `ConsoleLogEvent` |
| `console-logs-live` | SignalR hub at `{basePath}/console-logs/live` | `ConsoleLogEvent` server stream |
| `terminal` | WebSocket at `{basePath}/terminal?resource={displayName}&replica={index}` | Binary HMP1 frames |
| `interactions` | `GET {basePath}/interactions`; `POST {basePath}/interactions/respond` | `InteractionInfo[]`; no content |

```ts
export interface DashboardConfiguration {
  applicationName: string;
  dashboardVersion: string;
  runtimeVersion: string;
}
```

`configuration` remains the small, independently runnable identity contract understood by older
AOT clients. `shell` is advertised only when the host can return the complete authenticated
`DeckConfig`: application identity and versions, endpoint-security warnings, authentication mode
and user profile, current and available cultures, and AI-agent guidance.
React never combines partial shell state from two sessions.

During side-by-side convergence, BrowserToken and OpenID Connect remain one authoritative identity
session in the existing dashboard. The AOT host preserves the browser-facing Host while delegating
login, token validation, OIDC callback, logout, and culture operations, so cookies remain on the
same hostname and redirects remain on the AOT origin. Before serving the React root or a direct
versioned resource, telemetry, SignalR, interaction, command, or terminal route, the AOT host asks
that authority to authenticate the original local return URL. The authority returns `204` for an
authorized request or the exact cookie/OIDC challenge. OIDC callback-shaped root requests are
streamed back to the authority for code/state processing. Assets and version discovery contain no
application data and remain anonymous. No authentication result is cached or duplicated.

`culture` forwards the requested supported language, browser `Accept-Language`, local redirect,
and resulting culture cookie without exposing an unversioned browser route. `authentication`
submits sign-out with `POST`; React retains the legacy paths only for compatibility with older AOT
servers that do not advertise these capabilities.

`manage-data` keeps the inventory, selected signal mapping, export filename/content-disposition,
ZIP bytes, import filename/media type/body, 100 MB ceiling, import availability, and destructive
remove operation within one authenticated versioned surface. The AOT host streams these operations
to the existing telemetry repository while the processes coexist. It does not deserialize or
buffer the server-side payload, and it advertises the capability only when that repository is
configured and can authorize the same browser session.

The `resources` response uses the transport-neutral `Resource` shape documented below. It is a
complete point-in-time snapshot, returned with `Cache-Control: no-store`. It remains the fallback
when a compatible server does not advertise `resources-live`; commands and other resource
operations remain on `/api/deck` when the `commands` capability is not advertised.

The `commands` request is `{ resourceName, commandName }`. The backend resolves the resource type
and command from its authoritative resource snapshot before sending the command to the AppHost;
unknown resources or commands return `404`, and malformed requests return `400`. Command input
interactions are read and answered through the `interactions` capability. Resource watching,
command execution, and the bidirectional interaction stream share one long-lived AppHost
resource-service channel. Responses are queued in order with a bounded buffer; terminal actions
remove the prompt optimistically and restore it at its original position if delivery fails.
Reconnects receive the AppHost's current pending interactions before live updates. The unversioned
command and interaction aliases used by older React bundles enter this same direct session.

The read-only `structured-logs` response contains `{ totalCount, data }`, where `data` is the OTLP
JSON resource-log tree used by the existing dashboard. The AOT host obtains the backlog from the
loopback legacy dashboard and forwards the browser's dashboard credentials. `structured-logs-live`
exposes `WatchStructuredLogs`; each event contains one `data` OTLP tree. React performs text,
resource, severity, and structured-attribute filtering locally and freezes its displayed snapshot
while paused. `structured-logs-clear` accepts the optional `resource` query parameter. React bounds
its identity window to 10,000 entries and retains at most 5,000 log details. After clearing, it
ignores the old stream generation, refreshes the authoritative snapshot, and reconnects so buffered
pre-clear logs cannot reappear. A server must advertise all three structured-log capabilities
before React stops using the existing structured-log transport.

The `traces` response contains `{ totalCount, returnedCount, data }`, where `data` is the complete
OTLP resource-span tree needed for trace correlation, waterfall layout, span details, events, and
links. Repeated `resource` parameters plus `traceId`, `hasError`, `search`, and non-negative `limit`
preserve the existing server-side filtering contract. `traces-live` exposes
`WatchTraces(TraceStreamRequest)` and preserves the telemetry repository's chronological bounded
backlog, race-free backlog/live handoff, and live arrival order. React deduplicates through a
bounded 10,000-entry trace/span identity window, retains at most 5,000 span details, refreshes the
authoritative count on reconnect, and freezes only the displayed snapshot while paused.
`traces-clear` accepts the optional `resource` query parameter. After clearing, React ignores the
old stream generation, refreshes the snapshot, and opens a new watcher so buffered pre-clear spans
cannot reappear. A server must advertise all three trace capabilities before React stops using the
existing trace transport.

The `metrics` response is the stable resource/meter/instrument summary inventory, including exact
point counts and the latest display value. React polls it every 1.5 seconds, coalesces overlapping
polls, stops polling when the final subscriber leaves, and resumes after transient failures.
`metrics-series` accepts required `resource`, `meter`, and `instrument` values plus bounded
`windowSeconds` and `maxPoints`, histogram modes (`percentiles`, `count`, `sum`, or `buckets`), and
repeated `dimension.{name}` selections. Dimension values use `s:{value}`, `n:` for an unset
attribute, and a sole `x:` for an explicitly empty selection. The source-generated response keeps
aggregate and per-dimension series, bucket bounds, exemplars and trace correlation, and overflow
state aligned with the existing metric repository. `metrics-clear` accepts an optional `resource`
group and refreshes the authoritative summary after clearing. A server must advertise all three
metric capabilities before React stops using the existing metric transport.

`console-logs-live` exposes `WatchConsoleLogs(resourceName)`. Each subscription is resource scoped;
the existing dashboard emits its bounded backlog first and then live batches without a handoff gap.
Each line preserves its monotonic resource line number and stdout/stderr identity. On reconnect,
React drops replayed line numbers, and a server that does not advertise both console capabilities
continues to use the existing `/api/deck` NDJSON stream.

The `terminal` WebSocket is a byte-level bridge to the HMP1 producer identified by the requested
resource display name and non-negative replica index. The browser cannot provide a filesystem
path: the backend resolves the AppHost-provided `terminal.consumerUdsPath` from the same bounded,
authoritative resource snapshot used by the UI, keeps it server-side, and omits it from resource
JSON. The endpoint requires a same-origin WebSocket `Origin`, preserves HMP1 input, resize,
take-control, role-change, state-replay, and reconnect frames verbatim, and closes a slow or ended
peer without unbounded buffering. The AOT frontend archive includes the terminal module, HMP1
client, xterm runtime, and font. React uses the legacy `/api/terminal` route only when capability
discovery explicitly reports that an older AOT backend does not own `terminal`.

For `resources-live`, React connects with the SignalR JSON hub protocol and invokes the streaming
hub method `WatchResources`. The first stream item is always an authoritative `snapshot`; later
items are `change` events containing resource upserts and deleted resource names. Snapshot capture
and subscriber registration are atomic, so an upstream gRPC change cannot overtake the first item.
Every new or reconnected SignalR connection starts a new stream and receives a fresh snapshot before
its changes. A slow subscriber is disconnected instead of receiving an unbounded backlog.

The AOT backend must advertise only capabilities it implements end-to-end. During the side-by-side
migration, React delegates every capability not advertised here to the existing `/api/deck`
transport. All .NET request and response types in the versioned contract must be registered in a
source-generated `JsonSerializerContext`, including SignalR hub payloads; reflection serialization
is not part of the contract.

## Existing Deck transports

The ASP.NET Core dashboard backend exposes the same config, resource, command, and interaction
shapes through `GET /api/deck/config`, `GET /api/deck/resources`,
`POST /api/deck/commands/execute`, `GET /api/deck/interactions`, and
`POST /api/deck/interactions/respond`. Structured logs are streamed through
`GET /api/deck/telemetry/logs?follow=true` and cleared through
`DELETE /api/deck/telemetry/logs` with an optional `resource` query parameter. Traces use
the equivalent `GET`/`DELETE /api/deck/telemetry/spans` routes.

Metric summaries are read from `GET /api/deck/telemetry/metrics`. A selected time series is
read from `GET /api/deck/telemetry/metrics/series` with `resource`, `meter`, `instrument`,
`windowSeconds`, and `maxPoints` query parameters. Selected dimensions are repeated as
`dimension.{name}=s:{value}`; `n:` selects an unset value and `x:` selects no values. Responses
include the aggregate chart, known dimension values, individual dimension series, exemplars,
and the OpenTelemetry dimension-overflow flag. Metrics are cleared through
`DELETE /api/deck/telemetry/metrics` with an optional `resource` query parameter.
Command execution accepts
`{ resourceName, commandName }` and returns `CommandResponse`. The interactions GET returns the
current `InteractionInfo[]`; the response POST accepts `{ interactionId, action, values }`.

## Commands (invoke)

| Command | Args | Returns |
| --- | --- | --- |
| `deck_get_config` | – | `DeckConfig` |
| `deck_list_resources` | – | `Resource[]` (current snapshot) |
| `deck_subscribe_console_logs` | `{ resourceName: string }` | `void` (begins emitting `deck://console-log`) |
| `deck_unsubscribe_console_logs` | `{ resourceName: string }` | `void` |
| `deck_execute_command` | `{ resourceName, resourceType, commandName }` | `CommandResponse` |
| `deck_list_canvases` | – | `CanvasManifest[]` |
| `deck_get_telemetry_summary` | – | `TelemetrySummary` |
| `deck_clear_structured_logs` | `{ resourceName?: string \| null }` | `void` |
| `deck_clear_traces` | `{ resourceName?: string \| null }` | `void` |
| `deck_clear_metrics` | `{ resourceName?: string \| null }` | `void` |
| `deck_get_metric_series` | `{ name, resourceName?, windowSeconds?, maxPoints? }` | `MetricSeriesResponse \| null` (downsampled time series) |
| `deck_list_apphosts` | – | `AppHostInfo[]` (attached AppHosts) |
| `deck_select_apphost` | `{ id: string }` | `void` (switches the active AppHost) |
| `deck_respond_interaction` | `{ interactionId: number, action: string, values: Record<string,string> }` | `void` (replies to one open interaction) |

Resource/console/command operations target the **active** AppHost. Deck can attach to
multiple AppHosts at once (one per `aspire run --deck`); `deck_select_apphost` changes which
one is shown.

`deck_respond_interaction` replies to one of the active AppHost's open interactions
(identified by `interactionId`), raised by a resource command that needs inputs, a
message box, or a notification. `action` is one of `submit`/`update` (inputs dialog —
`update` re-validates without completing), `cancel`/`primary`/`secondary` (message box /
notification buttons); `values` maps input `name` → string value.

The active AppHost can have several interactions open at once. The UI splits them by
surface: **inputs dialogs** and **message boxes** are blocking and shown one-at-a-time in
the side drawer, while **notifications** (errors, the "parameters required" prompt, status
messages) are non-blocking and stack as toasts — matching the dashboard, which routes
notifications to message bars.

## Events (listen)

| Event | Payload |
| --- | --- |
| `deck://connection` | `ConnectionStatus` |
| `deck://resources` | `ResourcesEvent` |
| `deck://console-log` | `ConsoleLogEvent` |
| `deck://telemetry` | `TelemetrySummary` (debounced push when new OTLP data arrives) |
| `deck://apphosts` | `AppHostInfo[]` (attached AppHosts changed, or the active one switched) |
| `deck://interactions` | `InteractionInfo[]` (the active AppHost's open interactions; full list sent on every change, empty array when none) |

## Types

```ts
export interface DeckConfig {
  applicationName: string | null;
  resourceServiceUrl: string | null;
  otlpGrpcUrl: string | null;
  otlpHttpUrl: string | null;
  version: string;
  runtimeVersion?: string; // ASP.NET runtime description when hosted by the dashboard
}

export type ConnectionTarget = "resourceService" | "otlpGrpc" | "otlpHttp";
export type ConnectionState = "connecting" | "connected" | "disconnected" | "error";
export interface ConnectionStatus {
  target: ConnectionTarget;
  state: ConnectionState;
  message?: string | null;
}

export interface ResourceUrl {
  name: string | null;
  url: string;
  isInternal: boolean;
  isInactive: boolean;
  displayName: string | null;
  sortOrder: number;
}
export interface ResourceProperty {
  name: string;
  displayName: string | null;
  value: string;            // already rendered to a display string
  isSensitive: boolean;
  isHighlighted: boolean;
  sortOrder: number | null;
}
export interface EnvVar { name: string; value: string | null; isFromSpec: boolean; }
export interface HealthReport { status: string | null; key: string; description: string; }
export interface ResourceCommand {
  name: string;
  displayName: string;
  displayDescription: string | null;
  confirmationMessage: string | null;
  iconName: string | null;
  iconVariant: "regular" | "filled";
  isHighlighted: boolean;
  state: "enabled" | "disabled" | "hidden";
}
export interface ResourceRelationship { resourceName: string; type: string; }

export interface Resource {
  name: string;
  resourceType: string;
  displayName: string;
  uid: string;
  state: string | null;        // e.g. "Running", "Exited", "Starting"
  stateStyle: string | null;   // "success" | "info" | "warning" | "error" | null
  health: string | null;       // aggregate: "Healthy" | "Unhealthy" | "Degraded" | null
  createdAt: string | null;    // ISO-8601
  startedAt: string | null;
  stoppedAt: string | null;
  urls: ResourceUrl[];
  properties: ResourceProperty[];
  environment: EnvVar[];
  healthReports: HealthReport[];
  commands: ResourceCommand[];
  relationships: ResourceRelationship[];
  isHidden: boolean;
  supportsDetailedTelemetry: boolean;
  iconName: string | null;
  iconVariant: "regular" | "filled" | null;
}

export interface ResourcesEvent {
  type: "snapshot" | "change";
  resources?: Resource[];      // present when type === "snapshot"
  upserts?: Resource[];        // present when type === "change"
  deletes?: string[];          // resource names, present when type === "change"
}

export interface ConsoleLogLine { lineNumber: number; text: string; isStdErr: boolean; }
export interface ConsoleLogEvent { resourceName: string; lines: ConsoleLogLine[]; }

export interface CommandResponse {
  kind: "succeeded" | "failed" | "cancelled" | "invalidArguments" | "undefined";
  message: string | null;
}

// --- Telemetry (OTLP) ---
export interface TelemetryAttribute {
  key: string;
  value: string;
}
export interface LogRecordSummary {
  timeUnixNano: string;        // event time, or observed time when event time is 0
  observedTimeUnixNano: string; // string to avoid JS bigint loss
  severity: string | null;     // e.g. "Information", "Error"
  severityNumber: number;
  body: string;
  resourceName: string | null; // from service.name attribute
  traceId: string | null;
  spanId: string | null;
  parentId: string | null;
  eventName: string | null;
  originalFormat: string | null;
  scopeName: string;           // "unknown" when the scope name is empty
  scopeVersion: string | null;
  attributes: TelemetryAttribute[];
  scopeAttributes: TelemetryAttribute[];
  resourceAttributes: TelemetryAttribute[];
  flags: number;
  droppedAttributesCount: number;
  scopeDroppedAttributesCount: number;
  resourceDroppedAttributesCount: number;
}
export interface SpanSummary {
  traceId: string;
  spanId: string;
  traceState: string | null;
  parentSpanId: string | null;
  flags: number;
  name: string;
  kind: string;
  resourceName: string | null;
  startUnixNano: string;
  durationNanos: string;
  statusCode: string | null;   // "Unset" | "Ok" | "Error"
  statusMessage: string | null;
  scopeName: string;
  scopeVersion: string | null;
  attributes: TelemetryAttribute[];
  scopeAttributes: TelemetryAttribute[];
  resourceAttributes: TelemetryAttribute[];
  droppedAttributesCount: number;
  scopeDroppedAttributesCount: number;
  resourceDroppedAttributesCount: number;
  events: SpanEventSummary[];
  droppedEventsCount: number;
  links: SpanLinkSummary[];
  droppedLinksCount: number;
}
export interface SpanEventSummary {
  timeUnixNano: string;
  name: string;
  attributes: TelemetryAttribute[];
  droppedAttributesCount: number;
}
export interface SpanLinkSummary {
  traceId: string;
  spanId: string;
  traceState: string | null;
  attributes: TelemetryAttribute[];
  droppedAttributesCount: number;
  flags: number;
}
export type MetricKind = "gauge" | "counter" | "upDownCounter" | "histogram";

export interface MetricSummary {
  name: string;
  description?: string | null;
  meterName?: string | null;
  unit: string | null;
  resourceName: string | null;
  kind: MetricKind;             // how the series should be charted
  lastValue: number | null;     // latest raw value (cumulative for counters)
  pointCount: number;
}

// Downsampled time series for one (name, resource) metric within a window.
// Non-histogram metrics fill `values` (rate/s for counters, raw otherwise);
// histograms fill `p50`/`p90`/`p99`. All y-arrays align with `timestampsMs`.
export interface MetricSeriesResponse {
  name: string;
  meterName?: string | null;
  resourceName: string | null;
  unit: string | null;
  kind: MetricKind;
  timestampsMs: number[];
  values?: number[];
  p50?: number[];
  p90?: number[];
  p99?: number[];
  dimensionFilters?: Array<{ name: string; values: Array<string | null> }>;
  dimensions?: Array<{
    attributes: Array<{ key: string; value: string }>;
    timestampsMs: number[];
    values?: number[];
    p50?: number[];
    p90?: number[];
    p99?: number[];
  }>;
  exemplars?: Array<{
    timestampMs: number;
    value: number;
    traceId: string;
    spanId: string;
    attributes: Array<{ key: string; value: string }>;
  }>;
  hasOverflow?: boolean;
  showCount?: boolean;
}
export interface TelemetrySummary {
  logCount: number;
  spanCount: number;
  metricCount: number;
  recentLogs: LogRecordSummary[];   // newest first, capped (e.g. 200)
  recentSpans: SpanSummary[];       // newest first, capped
  metrics: MetricSummary[];         // one row per (name, resource) series
}

// --- Canvas ---
export interface CanvasManifest {
  id: string;
  title: string;
  description: string | null;
  icon: string | null;        // optional emoji or icon name
  entry: string;              // relative html entry, e.g. "index.html"
  url: string;                // resolved asset url the UI can load in an <iframe>
}

// --- AppHost switcher ---
export interface AppHostInfo {
  id: string;                 // stable id assigned at registration
  name: string;               // application name, or the id until connected
  resourceServiceUrl: string; // the AppHost's resource-service endpoint
  state: ConnectionState;     // resource-service connection state
  active: boolean;            // whether this AppHost is the one being shown
}

// --- Interactions (command inputs / prompts) ---
export type InteractionKind = "inputsDialog" | "messageBox" | "notification" | "complete";
export type InteractionInputType = "text" | "secretText" | "choice" | "boolean" | "number";

export interface InteractionInputInfo {
  name: string;
  label: string;
  placeholder: string;
  inputType: InteractionInputType;
  required: boolean;
  options: [string, string][];   // [value, display] for choice inputs
  value: string;                 // server-provided current value
  validationErrors: string[];    // shown inline under the field
  description: string;
  maxLength: number;             // 0 = unlimited
  allowCustomChoice: boolean;    // choice inputs may accept a free value
  disabled: boolean;
  updateStateOnChange: boolean;  // re-validate via deck_respond_interaction("update") on change
}

export interface InteractionInfo {
  interactionId: number;
  kind: InteractionKind;
  title: string;
  message: string;
  primaryButtonText: string;
  secondaryButtonText: string;
  showSecondaryButton: boolean;
  showDismiss: boolean;
  enableMessageMarkdown: boolean;
  intent: "none" | "success" | "warning" | "error" | "information" | "confirmation";
  inputs: InteractionInputInfo[];
  linkText: string;              // notification link
  linkUrl: string;
}
```

## Canvas runtime

Canvases are sandboxed HTML panels loaded in an `<iframe>`. The host page exposes a small
`window.parent`-based bridge so canvases can call back into Deck. See
`.agents/skills/deck-canvas/SKILL.md` for the authoring contract and `canvases/` for samples.
