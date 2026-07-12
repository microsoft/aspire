# Aspire Deck backend ⇄ UI contract

This is the single source of truth for the boundary between the Rust (Tauri) or dashboard HTTP
backend and the web UI. Both sides MUST agree on these names and shapes.

The backend exposes **commands** (request/response via `@tauri-apps/api/core` `invoke`) and
**events** (push via `@tauri-apps/api/event` `listen`). All payloads are JSON; field names are
`camelCase`.

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
