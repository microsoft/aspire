import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { clearTraces } from "../api/deck";
import type { SpanSummary, TelemetrySummary } from "../api/types";
import { useResources, useTelemetry } from "../lib/useDeckEvent";
import { formatDurationNanos } from "../lib/format";
import { buildResourceColorMap, colorFor } from "../lib/colors";
import { matchesTelemetryFilters, parseTelemetryFilters, spanFilterFields, telemetryFieldNames, type TelemetryFilter } from "../lib/telemetryFilters";
import { SPAN_TYPE_OPTIONS, spanMatchesType, type SpanTypeId } from "../lib/spans";
import { SpanDetailDrawer } from "../components/SpanDetailDrawer";
import { formatSpanJson } from "../components/SpanActions";
import {
  ChevronIcon,
  CommandMenu,
  NamedIcon,
  Page,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
  SearchBox,
  Select,
  StructuredFilterControl,
  Switch,
  TextViewerDialog,
  type TextViewerRequest,
} from "../toolkit";

// Minimum-duration filter options. Spans shorter than the selected threshold are
// hidden so insignificant work doesn't clutter the waterfall.
const MIN_DURATION_OPTIONS: { label: string; ms: number }[] = [
  { label: "All durations", ms: 0 },
  { label: "\u2265 1 ms", ms: 1 },
  { label: "\u2265 5 ms", ms: 5 },
  { label: "\u2265 10 ms", ms: 10 },
  { label: "\u2265 25 ms", ms: 25 },
  { label: "\u2265 50 ms", ms: 50 },
  { label: "\u2265 100 ms", ms: 100 },
  { label: "\u2265 250 ms", ms: 250 },
  { label: "\u2265 500 ms", ms: 500 },
  { label: "\u2265 1 s", ms: 1000 },
];

const NANOS_PER_MS = 1_000_000n;
const TRACE_VIRTUALIZATION_THRESHOLD = 200;
const TRACE_HEADER_HEIGHT = 42;
const TRACE_AXIS_HEIGHT = 24;
const TRACE_ROW_HEIGHT = 30;
const TRACE_BORDER_HEIGHT = 2;
const TRACE_GAP = 12;
const TRACE_OVERSCAN_PX = 800;

interface WaterfallRow {
  span: SpanSummary;
  depth: number;
  leftPct: number;
  widthPct: number;
  labelRight: boolean;
}

interface TraceGroup {
  traceId: string;
  rootName: string;
  resourceName: string | null;
  startNano: bigint;
  durationNano: bigint;
  rows: WaterfallRow[];
  hasError: boolean;
}

export interface TraceFilterRouteState {
  resourceName: string | null;
  type: SpanTypeId;
  query: string;
  minDurationMs: number;
  paused: boolean;
  filters: TelemetryFilter[];
}

function toBig(value: string): bigint {
  try {
    return BigInt(value);
  } catch {
    return 0n;
  }
}

// Percentage of `part` within `total`, clamped to [0, 100] with two-decimal precision.
function pct(part: bigint, total: bigint): number {
  if (total <= 0n) {
    return 0;
  }
  const scaled = Number((part * 10000n) / total) / 100;
  return Math.max(0, Math.min(100, scaled));
}

// Orders a trace's spans depth-first (parents before their children), with siblings
// sorted by start time. Spans whose parent was filtered out become roots so they
// still appear rather than vanishing with their ancestor.
function orderSpans(spans: SpanSummary[]): { span: SpanSummary; depth: number }[] {
  const byId = new Map(spans.map((s) => [s.spanId, s]));
  const children = new Map<string, SpanSummary[]>();
  const roots: SpanSummary[] = [];

  for (const span of spans) {
    const parent = span.parentSpanId;
    if (parent && byId.has(parent)) {
      const list = children.get(parent) ?? [];
      list.push(span);
      children.set(parent, list);
    } else {
      roots.push(span);
    }
  }

  const byStart = (a: SpanSummary, b: SpanSummary) => {
    const sa = toBig(a.startUnixNano);
    const sb = toBig(b.startUnixNano);
    return sa < sb ? -1 : sa > sb ? 1 : 0;
  };
  roots.sort(byStart);

  const ordered: { span: SpanSummary; depth: number }[] = [];
  const seen = new Set<string>();
  const visit = (span: SpanSummary, depth: number) => {
    if (seen.has(span.spanId)) {
      return;
    }
    seen.add(span.spanId);
    ordered.push({ span, depth });
    const kids = (children.get(span.spanId) ?? []).slice().sort(byStart);
    for (const kid of kids) {
      visit(kid, depth + 1);
    }
  };
  for (const root of roots) {
    visit(root, 0);
  }
  return ordered;
}

function buildTraceGroups(spans: SpanSummary[]): TraceGroup[] {
  const groups = new Map<string, SpanSummary[]>();
  for (const span of spans) {
    const list = groups.get(span.traceId) ?? [];
    list.push(span);
    groups.set(span.traceId, list);
  }

  const result: TraceGroup[] = [];
  for (const [traceId, group] of groups) {
    let traceStart = toBig(group[0]!.startUnixNano);
    let traceEnd = traceStart;
    for (const span of group) {
      const start = toBig(span.startUnixNano);
      const end = start + toBig(span.durationNanos);
      if (start < traceStart) {
        traceStart = start;
      }
      if (end > traceEnd) {
        traceEnd = end;
      }
    }
    const traceDur = traceEnd - traceStart;

    const ordered = orderSpans(group);
    const rows: WaterfallRow[] = ordered.map(({ span, depth }) => {
      const start = toBig(span.startUnixNano);
      const dur = toBig(span.durationNanos);
      const relStart = start - traceStart;
      // Label sits on the right of the bar when the bar's midpoint is in the left
      // half of the trace (so the label has room and doesn't run off the edge).
      const labelRight = relStart * 2n + dur < traceDur;
      return {
        span,
        depth,
        leftPct: pct(relStart, traceDur),
        widthPct: pct(dur, traceDur),
        labelRight,
      };
    });

    const root = ordered[0]?.span;
    result.push({
      traceId,
      rootName: root?.name ?? "trace",
      resourceName: root?.resourceName ?? null,
      startNano: traceStart,
      durationNano: traceDur,
      rows,
      hasError: group.some((s) => s.statusCode === "Error"),
    });
  }

  // Most recent traces first.
  result.sort((a, b) => (a.startNano < b.startNano ? 1 : a.startNano > b.startNano ? -1 : 0));
  return result;
}

export function TracesPage({
  routeTraceId,
  routeSpanId,
  routeResourceName,
  routeType,
  routeQuery,
  routeMinDurationMs,
  routePaused,
  routeFilters,
  onFilterRouteChange,
  onSelectSpan,
  onNavigateToSpan,
  onNavigateToLogs,
  onCloseDetails,
}: {
  routeTraceId: string | null;
  routeSpanId: string | null;
  routeResourceName: string | null;
  routeType: string;
  routeQuery: string;
  routeMinDurationMs: number;
  routePaused: boolean;
  routeFilters: string | null;
  onFilterRouteChange: (state: TraceFilterRouteState) => void;
  onSelectSpan: (span: SpanSummary) => void;
  onNavigateToSpan: (traceId: string, spanId: string | null) => void;
  onNavigateToLogs: (spanId: string) => void;
  onCloseDetails: () => void;
}) {
  const telemetry = useTelemetry();
  const { resources } = useResources();
  const [pausedSnapshot, setPausedSnapshot] = useState<TelemetrySummary | null>(null);
  const [clearing, setClearing] = useState(false);
  const [clearStatus, setClearStatus] = useState<{ message: string; error: boolean } | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [textViewer, setTextViewer] = useState<TextViewerRequest | null>(null);
  const traceScrollRef = useRef<HTMLDivElement | null>(null);
  const [traceScrollTop, setTraceScrollTop] = useState(0);
  const [traceViewportHeight, setTraceViewportHeight] = useState(0);

  const displayedTelemetry = pausedSnapshot ?? telemetry;
  const spans = displayedTelemetry?.recentSpans ?? [];
  const filters = useMemo(() => parseTelemetryFilters(routeFilters), [routeFilters]);
  const filterFields = useMemo(() => telemetryFieldNames(spans.map(spanFilterFields), ["Name", "Kind", "Resource", "TraceId", "SpanId", "Status", "Duration", "ScopeName"]), [spans]);
  const selectedType = SPAN_TYPE_OPTIONS.some((option) => option.value === routeType)
    ? routeType as SpanTypeId
    : "all";
  const selectedMinDurationMs = MIN_DURATION_OPTIONS.some((option) => option.ms === routeMinDurationMs)
    ? routeMinDurationMs
    : 0;
  const resourceOptions = useMemo(() => {
    const resourceTypes = new Map(resources.map((resource) => [resource.name, resource.resourceType]));
    const names = [...new Set(spans.flatMap((span) => span.resourceName === null ? [] : [span.resourceName]))]
      .sort((left, right) => left.localeCompare(right));
    return [
      { value: "all", label: "All resources", group: "All" },
      ...names.map((name) => ({
        value: name,
        label: name,
        group: resourceTypes.get(name) ?? "Telemetry",
      })),
    ];
  }, [resources, spans]);
  const selectedResource = routeResourceName !== null && resourceOptions.some((option) => option.value === routeResourceName)
    ? routeResourceName
    : "all";

  useEffect(() => {
    if (!routePaused) {
      setPausedSnapshot(null);
    } else if (pausedSnapshot === null && telemetry !== null) {
      setPausedSnapshot(telemetry);
    }
  }, [pausedSnapshot, routePaused, telemetry]);
  const selected = useMemo(() => {
    if (routeTraceId === null) {
      return null;
    }

    const traceSpans = spans.filter((span) => span.traceId === routeTraceId);
    return (routeSpanId === null
      ? undefined
      : traceSpans.find((span) => span.spanId === routeSpanId))
      ?? traceSpans[0]
      ?? null;
  }, [routeSpanId, routeTraceId, spans]);

  const colorMap = useMemo(() => buildResourceColorMap(spans.map((s) => s.resourceName)), [spans]);

  const traces = useMemo(() => {
    const minNano = BigInt(selectedMinDurationMs) * NANOS_PER_MS;
    const significant = (minNano > 0n ? spans.filter((s) => toBig(s.durationNanos) >= minNano) : spans)
      .filter((span) => matchesTelemetryFilters(spanFilterFields(span), filters));

    const groups = buildTraceGroups(significant).filter((trace) => {
      if (selectedResource !== "all" && !trace.rows.some((row) => row.span.resourceName === selectedResource)) {
        return false;
      }
      return selectedType === "all" || trace.rows.some((row) => spanMatchesType(row.span, selectedType));
    });

    const trimmed = (routeTraceId ?? routeQuery).trim().toLowerCase();
    if (!trimmed) {
      return groups;
    }
    // Filter at the trace level so matching keeps the full hierarchy intact.
    return groups.filter(
      (t) =>
        t.traceId.toLowerCase().startsWith(trimmed) ||
        t.rows.some(
          (r) =>
            r.span.name.toLowerCase().includes(trimmed) ||
            (r.span.resourceName ?? "").toLowerCase().includes(trimmed),
        ),
    );
  }, [filters, routeQuery, routeTraceId, selectedMinDurationMs, selectedResource, selectedType, spans]);

  const toggle = (traceId: string) => {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(traceId)) {
        next.delete(traceId);
      } else {
        next.add(traceId);
      }
      return next;
    });
  };

  const traceCount = useMemo(() => new Set(spans.map((s) => s.traceId)).size, [spans]);
  const virtualizeTraces = traces.length > TRACE_VIRTUALIZATION_THRESHOLD;
  useLayoutEffect(() => {
    const scroller = traceScrollRef.current;
    if (!virtualizeTraces || scroller === null) return;
    const update = () => setTraceViewportHeight(scroller.clientHeight);
    update();
    const observer = new ResizeObserver(update);
    observer.observe(scroller);
    return () => observer.disconnect();
  }, [virtualizeTraces]);
  const traceLayout = useMemo(() => {
    let top = 0;
    const items = traces.map((trace) => {
      const height = collapsed.has(trace.traceId)
        ? TRACE_HEADER_HEIGHT + TRACE_BORDER_HEIGHT
        : TRACE_HEADER_HEIGHT + TRACE_AXIS_HEIGHT + trace.rows.length * TRACE_ROW_HEIGHT + TRACE_BORDER_HEIGHT;
      const item = { trace, top, height };
      top += height + TRACE_GAP;
      return item;
    });
    return { items, totalHeight: Math.max(0, top - TRACE_GAP) };
  }, [collapsed, traces]);
  const visibleTraceLayout = useMemo(() => {
    if (!virtualizeTraces) return traceLayout.items;
    const start = Math.max(0, traceScrollTop - TRACE_OVERSCAN_PX);
    const end = traceScrollTop + (traceViewportHeight || 600) + TRACE_OVERSCAN_PX;
    return traceLayout.items.filter((item) => item.top + item.height >= start && item.top <= end);
  }, [traceLayout.items, traceScrollTop, traceViewportHeight, virtualizeTraces]);

  const updateRoute = (changes: Partial<TraceFilterRouteState>): void => {
    onFilterRouteChange({
      resourceName: selectedResource === "all" ? null : selectedResource,
      type: selectedType,
      query: routeQuery,
      minDurationMs: selectedMinDurationMs,
      paused: routePaused,
      filters,
      ...changes,
    });
  };

  const clearTraceData = async (resourceName: string | null): Promise<void> => {
    setClearing(true);
    setClearStatus(null);
    try {
      await clearTraces(resourceName);
      setPausedSnapshot(null);
      updateRoute({ resourceName: null, paused: false });
      setClearStatus({
        message: resourceName === null ? "Cleared all traces." : `Cleared traces for ${resourceName}.`,
        error: false,
      });
    } catch (error) {
      setClearStatus({ message: `Could not clear traces: ${String(error)}`, error: true });
    } finally {
      setClearing(false);
    }
  };

  return (
    <Page aria-labelledby="deck-page-traces-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-traces-title">Traces</PageTitle>
          <PageSubtitle>
            {displayedTelemetry
              ? `${traceCount} traces \u00b7 ${displayedTelemetry.spanCount.toLocaleString()} spans${routePaused ? " \u00b7 paused" : ""}`
              : "Loading\u2026"}
          </PageSubtitle>
        </PageHeading>
      </PageHeader>

      <PageToolbar ariaLabel="Trace tools">
        <SearchBox
          value={routeTraceId ?? routeQuery}
          onChange={(value) => {
            if (routeTraceId !== null) {
              onCloseDetails();
            }
            updateRoute({ query: value });
          }}
          placeholder="Filter traces…"
        />
        <Select
          ariaLabel="Resource"
          options={resourceOptions}
          value={selectedResource}
          onValueChange={(value) => updateRoute({ resourceName: value === "all" ? null : value })}
        />
        <StructuredFilterControl filters={filters} fields={filterFields} onChange={(next) => updateRoute({ filters: next })} />
        <Select
          ariaLabel="Span type"
          options={SPAN_TYPE_OPTIONS}
          value={selectedType}
          onValueChange={(value) => updateRoute({ type: value as SpanTypeId })}
        />
        <Select
          ariaLabel="Min duration"
          options={MIN_DURATION_OPTIONS.map((option) => ({ value: option.ms.toString(), label: option.label }))}
          value={selectedMinDurationMs.toString()}
          onValueChange={(value) => updateRoute({ minDurationMs: Number(value) })}
        />
        <div className="page__header-spacer" />
        <Switch
          label="Pause incoming data"
          checked={routePaused}
          disabled={telemetry === null}
          onCheckedChange={(checked) => {
            setPausedSnapshot(checked ? telemetry : null);
            updateRoute({ paused: checked });
          }}
        />
        <CommandMenu
          ariaLabel="Clear traces"
          triggerContent="Clear"
          triggerIcon={<NamedIcon name="Delete" size={16} />}
          placement="below-start"
          entries={[
            {
              id: "clear-all",
              label: "Clear all resources",
              icon: <NamedIcon name="BoxMultiple" size={16} />,
              tone: "danger",
              disabled: clearing || displayedTelemetry === null || displayedTelemetry.spanCount === 0,
              onSelect: () => void clearTraceData(null),
            },
            {
              id: "clear-resource",
              label: selectedResource === "all" ? "Clear selected resource" : `Clear ${selectedResource}`,
              icon: <NamedIcon name="CheckmarkCircle" size={16} />,
              tone: "danger",
              disabled: clearing || selectedResource === "all",
              onSelect: () => void clearTraceData(selectedResource),
            },
          ]}
        />
      </PageToolbar>

      <PageBody
        ref={traceScrollRef}
        className={`wf ${virtualizeTraces ? "wf--virtual" : ""}`}
        data-virtualized={virtualizeTraces ? "true" : undefined}
        aria-setsize={traces.length}
        onScroll={virtualizeTraces ? (event) => setTraceScrollTop(event.currentTarget.scrollTop) : undefined}
      >
        {traces.length === 0 ? (
          <div className="wf__empty">
            {telemetry ? "No traces match your filter." : "Waiting for telemetry\u2026"}
          </div>
        ) : (
          virtualizeTraces ? (
            <div className="wf__virtual-space" style={{ height: traceLayout.totalHeight }}>
              {visibleTraceLayout.map(({ trace, top }) => (
                <div key={trace.traceId} className="wf__virtual-item" style={{ top }}>
                  <TraceBlock
                    trace={trace}
                    colorMap={colorMap}
                    collapsed={collapsed.has(trace.traceId)}
                    onToggle={() => toggle(trace.traceId)}
                    onSelect={onSelectSpan}
                  />
                </div>
              ))}
            </div>
          ) : traces.map((trace) => (
            <TraceBlock
              key={trace.traceId}
              trace={trace}
              colorMap={colorMap}
              collapsed={collapsed.has(trace.traceId)}
              onToggle={() => toggle(trace.traceId)}
              onSelect={onSelectSpan}
            />
          ))
        )}
      </PageBody>

      {clearStatus ? (
        <div className="toast" role="status" aria-live="polite">
          <span className={`state__dot ${clearStatus.error ? "error" : "success"}`} />
          {clearStatus.message}
        </div>
      ) : null}

      {selected ? (
        <SpanDetailDrawer
          span={selected}
          allSpans={spans}
          color={colorFor(colorMap, selected.resourceName)}
          onClose={onCloseDetails}
          onNavigateToSpan={onNavigateToSpan}
          onViewLogs={() => onNavigateToLogs(selected.spanId)}
          onViewJson={() => setTextViewer({
            title: `${selected.name}.json`,
            value: formatSpanJson(selected),
            format: "json",
          })}
        />
      ) : null}

      <TextViewerDialog request={textViewer} onClose={() => setTextViewer(null)} />
    </Page>
  );
}

function TraceBlock({
  trace,
  colorMap,
  collapsed,
  onToggle,
  onSelect,
}: {
  trace: TraceGroup;
  colorMap: Map<string, string>;
  collapsed: boolean;
  onToggle: () => void;
  onSelect: (span: SpanSummary) => void;
}) {
  const headColor = colorFor(colorMap, trace.resourceName);
  // Axis ticks at 0/25/50/75/100% of the trace duration.
  const ticks = [0, 1, 2, 3, 4].map((i) => ({
    pct: i * 25,
    label: formatDurationNanos(String((trace.durationNano * BigInt(i)) / 4n)),
  }));

  return (
    <div className={`wf__trace ${trace.hasError ? "wf__trace--error" : ""}`}>
      <button className="wf__head" onClick={onToggle} aria-expanded={!collapsed}>
        <ChevronIcon size={14} className={`wf__chevron ${collapsed ? "" : "wf__chevron--open"}`} />
        <span className="wf__swatch" style={{ background: headColor }} />
        <span className="wf__head-name">{trace.rootName}</span>
        {trace.resourceName ? <span className="wf__head-res">{trace.resourceName}</span> : null}
        <span className="wf__head-spacer" />
        <span className="wf__head-meta">{trace.rows.length} spans</span>
        <span className="wf__head-dur">{formatDurationNanos(String(trace.durationNano))}</span>
      </button>

      {collapsed ? null : (
        <>
          <div className="wf__row wf__axis">
            <div className="wf__name" />
            <div className="wf__track wf__axis-track">
              {ticks.map((tick) => (
                <span
                  key={tick.pct}
                  className={`wf__tick-label ${tick.pct === 0 ? "wf__tick-label--start" : ""} ${tick.pct === 100 ? "wf__tick-label--end" : ""}`}
                  style={{ left: `${tick.pct}%` }}
                >
                  {tick.label}
                </span>
              ))}
            </div>
          </div>

          <div className="wf__rows">
            {trace.rows.map((row) => {
              const color = colorFor(colorMap, row.span.resourceName);
              const isError = row.span.statusCode === "Error";
              const dur = formatDurationNanos(row.span.durationNanos);
              return (
                <div
                  key={row.span.spanId}
                  className="wf__row wf__span"
                  onClick={() => onSelect(row.span)}
                  role="button"
                  tabIndex={0}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" || e.key === " ") {
                      e.preventDefault();
                      onSelect(row.span);
                    }
                  }}
                >
                  <div
                    className="wf__name"
                    style={{ paddingLeft: row.depth * 16 + 4, borderLeftColor: color }}
                  >
                    {isError ? <span className="wf__error-dot" /> : null}
                    <span className="cell-mono wf__name-text">{row.span.name}</span>
                  </div>
                  <div className="wf__track">
                    <span
                      className={`wf__bar ${isError ? "wf__bar--error" : ""}`}
                      style={{ left: `${row.leftPct}%`, width: `${row.widthPct}%`, background: color }}
                    />
                    {row.widthPct >= 15 ? (
                      <span
                        className="wf__dur wf__dur--inside"
                        style={{ left: `${row.leftPct}%`, width: `${row.widthPct}%` }}
                      >
                        {dur}
                      </span>
                    ) : row.labelRight ? (
                      <span className="wf__dur wf__dur--right" style={{ left: `${row.leftPct + row.widthPct}%` }}>
                        {dur}
                      </span>
                    ) : (
                      <span className="wf__dur wf__dur--left" style={{ width: `${row.leftPct}%` }}>
                        {dur}
                      </span>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}
