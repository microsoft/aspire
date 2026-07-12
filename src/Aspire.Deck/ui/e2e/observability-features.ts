export const observabilityFeatures = {
  "CONSOLE-RESOURCE-001": "The console lists every non-hidden resource that can be selected for streaming.",
  "CONSOLE-ALL-001": "The console combines every visible resource stream and prefixes each line with its resource.",
  "CONSOLE-STREAM-001": "Selecting a resource replays its console backlog and continues streaming new lines.",
  "CONSOLE-SWITCH-001": "Switching resources replaces the visible console stream with the selected resource.",
  "CONSOLE-FOLLOW-001": "Manual scrolling pauses console following and the user can return to the live tail.",
  "CONSOLE-PAUSE-001": "Incoming console output can be paused without changing the visible stream and catches up when resumed.",
  "CONSOLE-CLEAR-001": "Visible and pending console output can be cleared without restarting its subscription.",
  "CONSOLE-COMMANDS-001": "The selected resource exposes highlighted and overflow commands with confirmation and result status.",
  "CONSOLE-ROUTE-001": "Console resource, pause, timestamp, UTC, and wrapping state restore from a stable URL.",
  "LOG-LIST-001": "Structured logs show resource, level, timestamp, message, trace, and actions for incoming records.",
  "LOG-FILTER-001": "Structured logs filter across resource names and message bodies and expose an empty result.",
  "LOG-SEVERITY-001": "Structured logs expose every minimum severity threshold.",
  "LOG-LIVE-001": "Structured log totals update as live telemetry arrives.",
  "LOG-RESOURCE-001": "Structured logs can be constrained to one emitting resource or all resources.",
  "LOG-PAUSE-001": "Structured logs can freeze incoming data and catch up when resumed.",
  "LOG-DETAILS-001": "Selecting a structured log opens its event, scope, exception, resource, and correlation details.",
  "LOG-ACTIONS-001": "Structured log actions open details plus copyable message and JSON visualizers.",
  "LOG-TRACE-LINK-001": "A structured log trace ID opens the matching trace and span.",
  "TRACE-LIST-001": "Traces group nested spans into chronological waterfall rows.",
  "TRACE-LIVE-001": "Trace totals and waterfalls update as live spans arrive.",
  "TRACE-RESOURCE-001": "Traces can be constrained to a grouped telemetry resource selection.",
  "TRACE-TYPE-001": "Traces can be filtered by HTTP, database, messaging, RPC, GenAI, cloud, or other semantic conventions.",
  "TRACE-PAUSE-001": "Incoming traces can be frozen for inspection and catch up when resumed.",
  "TRACE-CLEAR-001": "Trace telemetry can be cleared for one selected resource or every resource.",
  "TRACE-SESSION-001": "Trace resource, type, text, duration, and pause state restore from the URL.",
  "TRACE-COLLAPSE-001": "A trace waterfall can be collapsed and expanded without losing its spans.",
  "TRACE-DETAILS-001": "Pointer and keyboard activation open complete span, context, scope, and resource details.",
  "TRACE-EVENTS-001": "Span details preserve event timestamps, exception data, attributes, and dropped counts.",
  "TRACE-LINKS-001": "Span links and backlinks preserve attributes and navigate to the exact related span.",
  "TRACE-ACTIONS-001": "Span actions open copyable JSON and filter structured logs by the exact span ID.",
  "TRACE-DETAIL-ROUTE-001": "Trace and span details restore from a stable URL after reload.",
  "TRACE-FILTER-001": "Traces filter by operation, resource, and trace ID prefix.",
  "TRACE-DURATION-001": "The minimum-duration filter removes spans and empty trace groups.",
  "TRACE-ERROR-001": "Failed spans and their trace groups expose error styling and status.",
  "METRIC-LIST-001": "Metrics list every instrument in stable name order with its latest value and resource.",
  "METRIC-SELECT-001": "Selecting an instrument updates the metric value, metadata, and chart series.",
  "METRIC-CHART-001": "Metric samples render a non-empty time-series chart.",
  "METRIC-CURSOR-001": "Hovering the chart exposes live timestamp and series values.",
  "METRIC-PAUSE-001": "Metric polling can be paused and resumed explicitly.",
  "METRIC-RANGE-001": "Metric history supports each available time-range selection.",
  "METRIC-ZOOM-001": "Dragging to zoom the chart pauses live polling for inspection.",
  "OBS-RESPONSIVE-001": "Console, logs, traces, metrics, and canvas catalog remain contained and usable on mobile.",
} as const;

export type ObservabilityFeatureId = keyof typeof observabilityFeatures;

export function getMissingObservabilityFeatures(
  covered: ReadonlySet<ObservabilityFeatureId>,
): ObservabilityFeatureId[] {
  return (Object.keys(observabilityFeatures) as ObservabilityFeatureId[]).filter(
    (feature) => !covered.has(feature),
  );
}
