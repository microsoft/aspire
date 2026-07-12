import { useMemo, useState } from "react";
import type { SpanSummary } from "../api/types";
import { useTelemetry } from "../lib/useDeckEvent";
import { formatDurationNanos } from "../lib/format";
import { buildResourceColorMap, colorFor } from "../lib/colors";
import { SpanDetailDrawer } from "../components/SpanDetailDrawer";
import { formatSpanJson } from "../components/SpanActions";
import {
  ChevronIcon,
  Page,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
  SearchBox,
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
  onSelectSpan,
  onNavigateToSpan,
  onNavigateToLogs,
  onCloseDetails,
}: {
  routeTraceId: string | null;
  routeSpanId: string | null;
  onSelectSpan: (span: SpanSummary) => void;
  onNavigateToSpan: (traceId: string, spanId: string | null) => void;
  onNavigateToLogs: (spanId: string) => void;
  onCloseDetails: () => void;
}) {
  const telemetry = useTelemetry();
  const [query, setQuery] = useState("");
  const [minMs, setMinMs] = useState(0);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [textViewer, setTextViewer] = useState<TextViewerRequest | null>(null);

  const spans = telemetry?.recentSpans ?? [];
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
    const minNano = BigInt(minMs) * NANOS_PER_MS;
    const significant = minNano > 0n ? spans.filter((s) => toBig(s.durationNanos) >= minNano) : spans;

    const groups = buildTraceGroups(significant);

    const trimmed = (routeTraceId ?? query).trim().toLowerCase();
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
  }, [spans, query, minMs, routeTraceId]);

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

  return (
    <Page aria-labelledby="deck-page-traces-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-traces-title">Traces</PageTitle>
          <PageSubtitle>
            {telemetry ? `${traceCount} traces \u00b7 ${telemetry.spanCount.toLocaleString()} spans` : "Loading\u2026"}
          </PageSubtitle>
        </PageHeading>
      </PageHeader>

      <PageToolbar ariaLabel="Trace tools">
        <SearchBox
          value={routeTraceId ?? query}
          onChange={(value) => {
            if (routeTraceId !== null) {
              onCloseDetails();
            }
            setQuery(value);
          }}
          placeholder="Filter traces…"
        />
        <label className="min-duration">
          <span className="min-duration__label">Min duration</span>
          <select className="select" value={minMs} onChange={(e) => setMinMs(Number(e.target.value))}>
            {MIN_DURATION_OPTIONS.map((opt) => (
              <option key={opt.ms} value={opt.ms}>
                {opt.label}
              </option>
            ))}
          </select>
        </label>
      </PageToolbar>

      <PageBody className="wf">
        {traces.length === 0 ? (
          <div className="wf__empty">
            {telemetry ? "No traces match your filter." : "Waiting for telemetry\u2026"}
          </div>
        ) : (
          traces.map((trace) => (
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
