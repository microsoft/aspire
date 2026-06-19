import { useMemo, useState } from "react";
import type { SpanSummary } from "../api/types";
import { useTelemetry } from "../lib/useDeckEvent";
import { formatDurationNanos } from "../lib/format";
import { DataTable, type Column } from "../components/DataTable";
import { SearchBox } from "../components/SearchBox";
import { Badge } from "../components/Badge";

interface SpanRow {
  span: SpanSummary;
  depth: number;
  isTraceStart: boolean;
}

function statusTone(code: string | null): "neutral" | "success" | "error" {
  switch (code) {
    case "Ok":
      return "success";
    case "Error":
      return "error";
    default:
      return "neutral";
  }
}

// Flattens spans into a render list grouped by traceId. Within each trace,
// children are indented under their parent (parentSpanId) where resolvable.
function buildRows(spans: SpanSummary[]): SpanRow[] {
  const order: string[] = [];
  const groups = new Map<string, SpanSummary[]>();
  for (const span of spans) {
    let group = groups.get(span.traceId);
    if (!group) {
      group = [];
      groups.set(span.traceId, group);
      order.push(span.traceId);
    }
    group.push(span);
  }

  const rows: SpanRow[] = [];
  for (const traceId of order) {
    const group = groups.get(traceId) ?? [];
    const byId = new Map(group.map((s) => [s.spanId, s]));
    const depthCache = new Map<string, number>();

    const depthOf = (span: SpanSummary): number => {
      const cached = depthCache.get(span.spanId);
      if (cached !== undefined) {
        return cached;
      }
      let depth = 0;
      let current: SpanSummary | undefined = span;
      const seen = new Set<string>();
      while (current?.parentSpanId && byId.has(current.parentSpanId) && !seen.has(current.spanId)) {
        seen.add(current.spanId);
        depth += 1;
        current = byId.get(current.parentSpanId);
      }
      depthCache.set(span.spanId, depth);
      return depth;
    };

    // Roots first (depth 0), then children, keeping a stable, readable order.
    const sorted = [...group].sort((a, b) => depthOf(a) - depthOf(b));
    sorted.forEach((span, index) => {
      rows.push({ span, depth: depthOf(span), isTraceStart: index === 0 });
    });
  }
  return rows;
}

export function TracesPage() {
  const telemetry = useTelemetry();
  const [query, setQuery] = useState("");

  const spans = telemetry?.recentSpans ?? [];

  const rows = useMemo(() => {
    const trimmed = query.trim().toLowerCase();
    const filtered = trimmed
      ? spans.filter(
          (s) =>
            s.name.toLowerCase().includes(trimmed) ||
            (s.resourceName ?? "").toLowerCase().includes(trimmed),
        )
      : spans;
    return buildRows(filtered);
  }, [spans, query]);

  const columns: Column<SpanRow>[] = [
    {
      key: "name",
      header: "Span",
      render: ({ span, depth }) => (
        <span className="trace-name" style={{ paddingLeft: depth * 18 }}>
          {depth > 0 ? <span className="trace-name__connector">└</span> : null}
          <span className="cell-mono">{span.name}</span>
        </span>
      ),
    },
    {
      key: "resource",
      header: "Resource",
      width: "150px",
      render: ({ span }) => <span className="cell-muted">{span.resourceName ?? "—"}</span>,
    },
    {
      key: "kind",
      header: "Kind",
      width: "100px",
      render: ({ span }) => <span className="cell-muted">{span.kind}</span>,
    },
    {
      key: "duration",
      header: "Duration",
      width: "110px",
      render: ({ span }) => <span className="cell-mono">{formatDurationNanos(span.durationNanos)}</span>,
    },
    {
      key: "status",
      header: "Status",
      width: "100px",
      render: ({ span }) => <Badge tone={statusTone(span.statusCode)}>{span.statusCode ?? "Unset"}</Badge>,
    },
  ];

  const traceCount = useMemo(() => new Set(spans.map((s) => s.traceId)).size, [spans]);

  return (
    <div className="page">
      <div className="page__header">
        <div>
          <div className="page__title">Traces</div>
          <div className="page__subtitle">
            {telemetry ? `${traceCount} traces · ${telemetry.spanCount.toLocaleString()} spans` : "Loading…"}
          </div>
        </div>
      </div>

      <div className="page__toolbar">
        <SearchBox value={query} onChange={setQuery} placeholder="Filter spans…" />
      </div>

      <div className="page__body">
        <DataTable
          columns={columns}
          rows={rows}
          rowKey={({ span }) => `${span.traceId}-${span.spanId}`}
          rowClassName={({ isTraceStart }) => (isTraceStart ? "trace-start" : undefined)}
          emptyMessage={telemetry ? "No spans match your filter." : "Waiting for telemetry…"}
        />
      </div>
    </div>
  );
}
