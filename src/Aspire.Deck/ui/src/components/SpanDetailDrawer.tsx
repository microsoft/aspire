import type { SpanSummary } from "../api/types";
import { formatDurationNanos, formatTimeWithMillis, dateFromUnixNano } from "../lib/format";
import { Badge, Drawer } from "../toolkit";

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

// Compact details pane for a single span, opened from the trace waterfall. Reuses
// the shared drawer chrome (overlay + aside) so it matches the resource details pane.
export function SpanDetailDrawer({
  span,
  color,
  onClose,
}: {
  span: SpanSummary;
  color: string;
  onClose: () => void;
}) {
  return (
    <Drawer
      title={span.name}
      subtitle={span.resourceName ?? "unknown resource"}
      leading={<span className="span-detail__swatch" style={{ background: color }} />}
      ariaLabel={span.name}
      closeLabel="Close"
      onClose={onClose}
    >
      <section className="drawer__section">
        <div className="drawer__section-title">Span</div>
        <div className="kv">
          <div className="kv__key">Duration</div>
          <div className="kv__val">{formatDurationNanos(span.durationNanos)}</div>
          <div className="kv__key">Kind</div>
          <div className="kv__val">{span.kind}</div>
          <div className="kv__key">Status</div>
          <div className="kv__val">
            <Badge tone={statusTone(span.statusCode)}>{span.statusCode ?? "Unset"}</Badge>
          </div>
          <div className="kv__key">Started</div>
          <div className="kv__val">{formatTimeWithMillis(dateFromUnixNano(span.startUnixNano))}</div>
        </div>
      </section>

      <section className="drawer__section">
        <div className="drawer__section-title">Identifiers</div>
        <div className="kv">
          <div className="kv__key">Trace ID</div>
          <div className="kv__val cell-mono">{span.traceId}</div>
          <div className="kv__key">Span ID</div>
          <div className="kv__val cell-mono">{span.spanId}</div>
          {span.parentSpanId ? (
            <>
              <div className="kv__key">Parent</div>
              <div className="kv__val cell-mono">{span.parentSpanId}</div>
            </>
          ) : null}
        </div>
      </section>
    </Drawer>
  );
}
