import { useEffect, useMemo, useRef, useState } from "react";
import { useTelemetry } from "../lib/useDeckEvent";
import { formatMetricValue, displayUnit } from "../lib/format";
import { Sparkline } from "../components/Sparkline";
import { EmptyState } from "../components/EmptyState";
import { MetricsIcon } from "../components/Icons";

const RING_CAPACITY = 80;

export function MetricsPage() {
  const telemetry = useTelemetry();
  const [selected, setSelected] = useState<string | null>(null);

  // Client-side ring buffer per metric. The telemetry summary only exposes
  // lastValue, so we append it on every push to animate a time series.
  const buffersRef = useRef<Map<string, number[]>>(new Map());
  const [, forceTick] = useState(0);

  const metrics = useMemo(
    () => [...(telemetry?.metrics ?? [])].sort((a, b) => a.name.localeCompare(b.name)),
    [telemetry],
  );

  useEffect(() => {
    if (!telemetry) {
      return;
    }
    const buffers = buffersRef.current;
    for (const metric of telemetry.metrics) {
      if (metric.lastValue === null) {
        continue;
      }
      // Append immutably: Sparkline (and uPlot) only redraw when the `values`
      // array reference changes. Mutating the existing buffer in place would keep
      // the same reference and freeze the chart after the first render.
      const previous = buffers.get(metric.name) ?? [];
      const next = [...previous, metric.lastValue];
      if (next.length > RING_CAPACITY) {
        next.splice(0, next.length - RING_CAPACITY);
      }
      buffers.set(metric.name, next);
    }
    // Trigger a re-render so the chart reflects the appended sample.
    forceTick((n) => n + 1);
  }, [telemetry]);

  useEffect(() => {
    if (selected === null && metrics.length > 0) {
      setSelected(metrics[0]?.name ?? null);
    }
  }, [metrics, selected]);

  const activeMetric = metrics.find((m) => m.name === selected) ?? null;
  const activeBuffer = activeMetric ? buffersRef.current.get(activeMetric.name) ?? [] : [];

  if (!telemetry || metrics.length === 0) {
    return (
      <div className="page">
        <div className="page__header">
          <div>
            <div className="page__title">Metrics</div>
            <div className="page__subtitle">Loading…</div>
          </div>
        </div>
        <div className="page__body">
          <EmptyState icon={<MetricsIcon size={26} />} title="No metrics yet">
            Metrics will appear here as OTLP data arrives.
          </EmptyState>
        </div>
      </div>
    );
  }

  return (
    <div className="page">
      <div className="page__header">
        <div>
          <div className="page__title">Metrics</div>
          <div className="page__subtitle">{metrics.length} instruments</div>
        </div>
      </div>

      <div className="page__body">
        <div className="metrics-layout">
          <div className="metric-list">
            {metrics.map((metric) => (
              <button
                key={metric.name}
                className={`metric-item ${metric.name === selected ? "active" : ""}`}
                onClick={() => setSelected(metric.name)}
              >
                <div className="metric-item__name">{metric.name}</div>
                <div className="metric-item__meta">
                  <span>{formatMetricValue(metric.lastValue, metric.unit)}</span>
                  <span>·</span>
                  <span>{metric.resourceName ?? "—"}</span>
                </div>
              </button>
            ))}
          </div>

          <div className="metric-detail">
            {activeMetric ? (
              <>
                <div className="metric-detail__head">
                  <span className="metric-detail__value">
                    {formatMetricValue(activeMetric.lastValue, activeMetric.unit)}
                  </span>
                  <span className="cell-muted">
                    {activeMetric.name}
                    {displayUnit(activeMetric.unit) ? ` (${displayUnit(activeMetric.unit)})` : ""}
                  </span>
                </div>
                <div className="cell-muted" style={{ fontSize: 12 }}>
                  {activeMetric.resourceName ?? "—"} · {activeMetric.pointCount.toLocaleString()} points
                </div>
                <div className="metric-detail__chart">
                  <Sparkline values={activeBuffer} unit={displayUnit(activeMetric.unit)} height={260} />
                </div>
              </>
            ) : (
              <div className="center-fill">Select a metric</div>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
