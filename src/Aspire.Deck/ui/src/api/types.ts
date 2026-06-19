// Mirrors src/Aspire.Deck/CONTRACT.md exactly. This is the authoritative shared
// boundary between the Rust (Tauri) backend and this UI. Field names are camelCase.

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
  value: string;
  isSensitive: boolean;
  isHighlighted: boolean;
  sortOrder: number | null;
}

export interface EnvVar {
  name: string;
  value: string | null;
  isFromSpec: boolean;
}

export interface HealthReport {
  status: string | null;
  key: string;
  description: string;
}

export type ResourceCommandState = "enabled" | "disabled" | "hidden";

export interface ResourceCommand {
  name: string;
  displayName: string;
  displayDescription: string | null;
  confirmationMessage: string | null;
  iconName: string | null;
  isHighlighted: boolean;
  state: ResourceCommandState;
}

export interface ResourceRelationship {
  resourceName: string;
  type: string;
}

export interface Resource {
  name: string;
  resourceType: string;
  displayName: string;
  uid: string;
  state: string | null;
  stateStyle: string | null;
  health: string | null;
  createdAt: string | null;
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
  resources?: Resource[];
  upserts?: Resource[];
  deletes?: string[];
}

export interface ConsoleLogLine {
  lineNumber: number;
  text: string;
  isStdErr: boolean;
}

export interface ConsoleLogEvent {
  resourceName: string;
  lines: ConsoleLogLine[];
}

export type CommandResponseKind =
  | "succeeded"
  | "failed"
  | "cancelled"
  | "invalidArguments"
  | "undefined";

export interface CommandResponse {
  kind: CommandResponseKind;
  message: string | null;
}

export interface LogRecordSummary {
  timeUnixNano: string;
  severity: string | null;
  severityNumber: number;
  body: string;
  resourceName: string | null;
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
  statusCode: string | null;
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
  recentLogs: LogRecordSummary[];
  recentSpans: SpanSummary[];
  metrics: MetricSummary[];
}

export interface CanvasManifest {
  id: string;
  title: string;
  description: string | null;
  icon: string | null;
  entry: string;
  url: string;
}

export interface ExecuteCommandArgs {
  resourceName: string;
  resourceType: string;
  commandName: string;
}
