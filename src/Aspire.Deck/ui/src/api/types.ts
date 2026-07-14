import type { OtlpTelemetryData } from "./otlp";

// Mirrors src/Aspire.Deck/CONTRACT.md exactly. This is the authoritative shared
// boundary between the Rust (Tauri) backend and this UI. Field names are camelCase.

export interface DeckConfig {
  applicationName: string | null;
  resourceServiceUrl: string | null;
  otlpGrpcUrl: string | null;
  otlpHttpUrl: string | null;
  version: string;
  runtimeVersion?: string;
  isTelemetryEndpointUnsecured?: boolean;
  isApiEndpointUnsecured?: boolean;
  frontendAuthMode?: string;
  user?: DeckUser | null;
  culture?: string;
  cultures?: DeckCulture[];
  isAgentHelpEnabled?: boolean;
  agentHelpMarkdown?: string | null;
  isAssistantEnabled?: boolean;
}

// Versioned ASP.NET backend discovery types. Keep these aligned with the
// versioned contract in ../../CONTRACT.md rather than the legacy /api/deck shapes.
export interface DashboardApiDiscovery {
  product: string;
  versions: DashboardApiVersion[];
}

export interface DashboardApiVersion {
  version: number;
  basePath: string;
  capabilities: string[];
}

export interface DashboardConfiguration {
  applicationName: string;
  dashboardVersion: string;
  runtimeVersion: string;
}

export interface DashboardStructuredLogsSnapshot {
  totalCount: number;
  data: OtlpTelemetryData;
}

export interface DashboardStructuredLogsEvent {
  data: OtlpTelemetryData;
}

export interface DeckUser {
  name: string;
  username: string | null;
}

export interface DeckCulture {
  name: string;
  displayName: string;
}

export interface AssistantModel {
  family: string;
  displayName: string;
}

export interface AssistantInfo {
  models: AssistantModel[];
}

export interface AssistantMessage {
  role: "user" | "assistant" | "system";
  content: string;
}

export interface AssistantChatRequest {
  messages: AssistantMessage[];
  model: string | null;
}

export interface AssistantEvent {
  type: "start" | "content" | "complete" | "error";
  content: string | null;
  message: string | null;
}

export type ManageDataType = "ResourceDetails" | "ConsoleLogs" | "StructuredLogs" | "Traces" | "Metrics" | "Resource";

export interface ManageDataResource {
  name: string;
  displayName: string;
  dataTypes: ManageDataType[];
}

export interface ManageDataResponse {
  resources: ManageDataResource[];
  isImportEnabled: boolean;
}

export interface ManageDataSelection {
  resourceName: string;
  dataTypes: ManageDataType[];
}

export interface ManageDataRequest {
  resources: ManageDataSelection[];
}

export interface ManageDataExport {
  fileName: string;
  blob: Blob;
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
export type IconVariant = "regular" | "filled";

export interface ResourceCommand {
  name: string;
  displayName: string;
  displayDescription: string | null;
  confirmationMessage: string | null;
  iconName: string | null;
  iconVariant: IconVariant;
  isHighlighted: boolean;
  state: ResourceCommandState;
}

export interface ResourceRelationship {
  resourceName: string;
  type: string;
}

// Well-known resource type for AppHost parameters. Parameters get their own page
// in the UI, separate from the Resources list.
export const PARAMETER_RESOURCE_TYPE = "Parameter";

// Well-known property name carrying a parameter's resolved value (sensitive when
// the parameter was declared as a secret).
export const PARAMETER_VALUE_PROPERTY = "Value";

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
  iconVariant: IconVariant | null;
  hasTerminal?: boolean;
  terminalReplicaIndex?: number | null;
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
  result: CommandResult | null;
}

export type CommandResultFormat = "text" | "json" | "markdown";

export interface CommandResult {
  value: string;
  format: CommandResultFormat;
  displayImmediately: boolean;
}

export interface TelemetryAttribute {
  key: string;
  value: string;
}

export interface LogRecordSummary {
  timeUnixNano: string;
  observedTimeUnixNano: string;
  severity: string | null;
  severityNumber: number;
  body: string;
  resourceName: string | null;
  traceId: string | null;
  spanId: string | null;
  parentId: string | null;
  eventName: string | null;
  originalFormat: string | null;
  scopeName: string;
  scopeVersion: string | null;
  attributes: TelemetryAttribute[];
  scopeAttributes: TelemetryAttribute[];
  resourceAttributes: TelemetryAttribute[];
  flags: number;
  droppedAttributesCount: number;
  scopeDroppedAttributesCount: number;
  resourceDroppedAttributesCount: number;
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
  statusCode: string | null;
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

export type MetricKind = "gauge" | "counter" | "upDownCounter" | "histogram";
export type HistogramMode = "percentiles" | "count" | "sum" | "buckets";

export interface MetricSummary {
  name: string;
  description?: string | null;
  meterName?: string | null;
  unit: string | null;
  resourceName: string | null;
  kind: MetricKind;
  lastValue: number | null;
  pointCount: number;
}

// Downsampled time series for a metric. Non-histogram metrics fill `values`;
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
  sum?: number[];
  bucketBounds?: number[];
  buckets?: MetricBucketSeries[];
  dimensionFilters?: MetricDimensionFilter[];
  dimensions?: MetricDimensionSeries[];
  exemplars?: MetricExemplar[];
  hasOverflow?: boolean;
  showCount?: boolean;
  histogramMode?: HistogramMode | null;
}

export interface MetricBucketSeries {
  upperBound: number | null;
  values: number[];
}

export interface MetricAttribute {
  key: string;
  value: string;
}

export interface MetricDimensionFilter {
  name: string;
  values: Array<string | null>;
}

export interface MetricDimensionSeries {
  attributes: MetricAttribute[];
  timestampsMs: number[];
  values?: number[];
  p50?: number[];
  p90?: number[];
  p99?: number[];
  sum?: number[];
  buckets?: MetricBucketSeries[];
}

export interface MetricExemplar {
  timestampMs: number;
  value: number;
  traceId: string;
  spanId: string;
  attributes: MetricAttribute[];
}

// Options for a metric series query.
export interface MetricSeriesQuery {
  name: string;
  meterName?: string | null;
  resourceName?: string | null;
  windowSeconds?: number;
  maxPoints?: number;
  dimensions?: Record<string, Array<string | null>>;
  showCount?: boolean;
  histogramMode?: HistogramMode;
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

// An attached AppHost shown in the switcher. Deck can attach to multiple AppHosts
// at once (each `aspire run --deck`); the UI shows one — the active one — at a time.
export interface AppHostInfo {
  id: string;
  name: string;
  resourceServiceUrl: string;
  state: ConnectionState;
  active: boolean;
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
  options: [string, string][]; // [value, display]
  value: string;
  validationErrors: string[];
  description: string;
  enableDescriptionMarkdown: boolean;
  maxLength: number;
  allowCustomChoice: boolean;
  disabled: boolean;
  updateStateOnChange: boolean;
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
  linkText: string;
  linkUrl: string;
}
