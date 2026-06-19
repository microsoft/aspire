# Aspire Deck — Tauri ⇄ UI contract

This is the single source of truth for the boundary between the Rust (Tauri) backend and the
web UI. Both sides MUST agree on these names and shapes.

The backend exposes **commands** (request/response via `@tauri-apps/api/core` `invoke`) and
**events** (push via `@tauri-apps/api/event` `listen`). All payloads are JSON; field names are
`camelCase`.

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
| `deck_list_apphosts` | – | `AppHostInfo[]` (attached AppHosts) |
| `deck_select_apphost` | `{ id: string }` | `void` (switches the active AppHost) |

Resource/console/command operations target the **active** AppHost. Deck can attach to
multiple AppHosts at once (one per `aspire run --deck`); `deck_select_apphost` changes which
one is shown.

## Events (listen)

| Event | Payload |
| --- | --- |
| `deck://connection` | `ConnectionStatus` |
| `deck://resources` | `ResourcesEvent` |
| `deck://console-log` | `ConsoleLogEvent` |
| `deck://telemetry` | `TelemetrySummary` (debounced push when new OTLP data arrives) |
| `deck://apphosts` | `AppHostInfo[]` (attached AppHosts changed, or the active one switched) |

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
export interface LogRecordSummary {
  timeUnixNano: string;        // string to avoid JS bigint loss
  severity: string | null;     // e.g. "Information", "Error"
  severityNumber: number;
  body: string;
  resourceName: string | null; // from service.name attribute
  traceId: string | null;
  spanId: string | null;
}
export interface SpanSummary {
  traceId: string;
  spanId: string;
  parentSpanId: string | null;
  name: string;
  kind: string;
  resourceName: string | null;
  startUnixNano: string;
  durationNanos: string;
  statusCode: string | null;   // "Unset" | "Ok" | "Error"
}
export interface MetricSummary {
  name: string;
  unit: string | null;
  resourceName: string | null;
  lastValue: number | null;
  pointCount: number;
}
export interface TelemetrySummary {
  logCount: number;
  spanCount: number;
  metricCount: number;
  recentLogs: LogRecordSummary[];   // newest first, capped (e.g. 200)
  recentSpans: SpanSummary[];       // newest first, capped
  metrics: MetricSummary[];
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
  state: ConnectionState;     // resource-service connection state
  active: boolean;            // whether this AppHost is the one being shown
}
```

## Canvas runtime

Canvases are sandboxed HTML panels loaded in an `<iframe>`. The host page exposes a small
`window.parent`-based bridge so canvases can call back into Deck. See
`.agents/skills/deck-canvas/SKILL.md` for the authoring contract and `canvases/` for samples.
