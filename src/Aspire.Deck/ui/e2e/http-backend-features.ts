export const httpBackendFeatures = {
  "HTTP-SHELL-UNSECURED-001": "Backend security configuration renders a persistent, dismissible unsecured-endpoint warning.",
  "HTTP-MANAGE-DATA-001": "Manage Data inventories, selects, exports, imports, and removes dashboard data through the HTTP backend.",
  "HTTP-CONFIG-001": "HTTP mode loads the application identity and version from the dashboard backend.",
  "HTTP-RESOURCES-001": "HTTP mode renders the resource snapshot returned by the dashboard backend.",
  "HTTP-RESOURCE-VIRTUALIZATION-001": "Large HTTP resource inventories keep a bounded semantic table while preserving sorting and row actions.",
  "HTTP-MOCK-ISOLATION-001": "Explicit HTTP mode never falls back to the standalone mock backend.",
  "HTTP-FAILURE-001": "HTTP mode reports an unavailable dashboard backend without unhandled browser errors.",
  "HTTP-RECOVERY-001": "HTTP mode recovers application identity and resources when the dashboard backend returns.",
  "HTTP-RECONNECT-001": "A backend outage exposes an explicit retry action with and without a retained resource snapshot.",
  "HTTP-COMMAND-001": "HTTP mode executes a live resource command through the dashboard backend.",
  "HTTP-COMMAND-OUTCOMES-001": "HTTP commands distinguish successful, cancelled, failed, and transport-error outcomes.",
  "HTTP-INTERACTION-001": "HTTP mode round-trips resource command input interactions through the dashboard backend.",
  "HTTP-CONSOLE-001": "HTTP mode replays and streams resource console output from the dashboard backend.",
  "HTTP-CONSOLE-CONTROLS-001": "HTTP console output supports parsed timestamps, UTC display, wrapping, and exact text download.",
  "HTTP-CONSOLE-VIRTUALIZATION-001": "HTTP console keeps a bounded DOM while preserving full-stream geometry and stable line numbers.",
  "HTTP-STRUCTURED-LOGS-001": "HTTP mode replays and streams OTLP structured logs from the dashboard backend.",
  "HTTP-STRUCTURED-LOG-DETAILS-001": "HTTP mode preserves event, scope, exception, resource, and correlation details for structured logs.",
  "HTTP-STRUCTURED-LOG-VIRTUALIZATION-001": "Large HTTP structured-log inventories keep a bounded semantic table with accessible total row count.",
  "HTTP-STRUCTURED-LOG-CLEAR-001": "HTTP mode clears one structured-log resource or all resources and refreshes its snapshot.",
  "HTTP-TRACES-001": "HTTP mode replays and streams OTLP spans into trace waterfalls and exact span details.",
  "HTTP-TRACE-VIRTUALIZATION-001": "Large HTTP trace inventories use variable-height virtualization while preserving waterfall actions.",
  "HTTP-TRACE-CLEAR-001": "HTTP mode clears one trace resource or all trace resources and refreshes its snapshot.",
  "HTTP-METRICS-001": "HTTP mode loads real metric summaries and requests an exact resource, meter, and instrument series.",
  "HTTP-METRIC-CLEAR-001": "HTTP mode clears one metric resource or all metric resources and refreshes its snapshot.",
  "HTTP-EMPTY-TELEMETRY-001": "HTTP mode distinguishes loading, no-resource, no-meter, missing-meter, missing-instrument, and no-sample metric states.",
} as const;

export type HttpBackendFeatureId = keyof typeof httpBackendFeatures;

export function getMissingHttpBackendFeatures(
  covered: ReadonlySet<HttpBackendFeatureId>,
): HttpBackendFeatureId[] {
  return (Object.keys(httpBackendFeatures) as HttpBackendFeatureId[]).filter(
    (feature) => !covered.has(feature),
  );
}
