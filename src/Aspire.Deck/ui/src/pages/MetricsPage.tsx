import { useCallback, useEffect, useMemo, useState } from "react";
import type { MetricSummary, MetricSeriesResponse } from "../api/types";
import { useTelemetry } from "../lib/useDeckEvent";
import { getMetricSeries } from "../api/deck";
import { formatMetricValue, displayUnit } from "../lib/format";
import { MetricChart, type ChartLine } from "../components/MetricChart";
import { EmptyState } from "../components/EmptyState";
import { MetricsIcon, PauseIcon, PlayIcon } from "../components/Icons";

const TIME_RANGES: { label: string; seconds: number }[] = [
  { label: "1m", seconds: 60 },
  { label: "5m", seconds: 300 },
  { label: "15m", seconds: 900 },
  { label: "30m", seconds: 1800 },
  { label: "1h", seconds: 3600 },
];

const POLL_MS = 1500;

// Stable identity for a (name, resource) series.
function metricKey(m: { name: string; resourceName: string | null }): string {
  return `${m.name}\u0000${m.resourceName ?? ""}`;
}

function cssVar(name: string, fallback: string): string {
  if (typeof window === "undefined") {
    return fallback;
  }
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

// Builds the chart lines for a series response: one line for gauge/counter/
// up-down, three percentile lines for histograms.
function buildLines(series: MetricSeriesResponse): ChartLine[] {
  if (series.kind === "histogram") {
    return [
      { label: "p50", color: cssVar("--info", "#60a5fa"), values: series.p50 ?? [] },
      { label: "p90", color: cssVar("--accent", "#a855f7"), values: series.p90 ?? [] },
      { label: "p99", color: cssVar("--warning", "#fbbf24"), values: series.p99 ?? [] },
    ];
  }
  const label = series.kind === "counter" ? "rate" : "value";
  return [{ label, color: cssVar("--accent", "#a855f7"), values: series.values ?? [] }];
}

function kindLabel(kind: string): string {
  switch (kind) {
    case "counter":
      return "Counter · rate/s";
    case "upDownCounter":
      return "Up/down counter";
    case "histogram":
      return "Histogram · percentiles";
    default:
      return "Gauge";
  }
}

export function MetricsPage() {
  const telemetry = useTelemetry();
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [windowSeconds, setWindowSeconds] = useState(300);
  const [paused, setPaused] = useState(false);
  const [series, setSeries] = useState<MetricSeriesResponse | null>(null);

  const metrics = useMemo(
    () => [...(telemetry?.metrics ?? [])].sort((a, b) => a.name.localeCompare(b.name)),
    [telemetry],
  );

  // Default to the first metric, and reset the selection when the current one is
  // no longer present — e.g. after switching to a different AppHost, whose metric
  // set is entirely different.
  useEffect(() => {
    const exists = selectedKey !== null && metrics.some((m) => metricKey(m) === selectedKey);
    if (!exists) {
      setSelectedKey(metrics.length > 0 ? metricKey(metrics[0]!) : null);
    }
  }, [metrics, selectedKey]);

  const active: MetricSummary | null = useMemo(
    () => metrics.find((m) => metricKey(m) === selectedKey) ?? null,
    [metrics, selectedKey],
  );

  const fetchSeries = useCallback(async () => {
    if (!active) {
      return;
    }
    const result = await getMetricSeries({
      name: active.name,
      resourceName: active.resourceName,
      windowSeconds,
      maxPoints: 600,
    });
    setSeries(result);
  }, [active, windowSeconds]);

  // Fetch immediately when the selection or window changes.
  useEffect(() => {
    setSeries(null);
    void fetchSeries();
  }, [fetchSeries]);

  // Poll while live (not paused).
  useEffect(() => {
    if (paused || !active) {
      return;
    }
    const id = window.setInterval(() => void fetchSeries(), POLL_MS);
    return () => window.clearInterval(id);
  }, [paused, active, fetchSeries]);

  const lines = useMemo(() => (series ? buildLines(series) : []), [series]);

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
            {metrics.map((metric) => {
              const key = metricKey(metric);
              return (
                <button
                  key={key}
                  className={`metric-item ${key === selectedKey ? "active" : ""}`}
                  onClick={() => setSelectedKey(key)}
                >
                  <div className="metric-item__name">{metric.name}</div>
                  <div className="metric-item__meta">
                    <span>{formatMetricValue(metric.lastValue, metric.unit)}</span>
                    <span>·</span>
                    <span>{metric.resourceName ?? "—"}</span>
                  </div>
                </button>
              );
            })}
          </div>

          <div className="metric-detail">
            {active ? (
              <>
                <div className="metric-detail__head">
                  <div>
                    <span className="metric-detail__value">
                      {formatMetricValue(active.lastValue, active.unit)}
                    </span>
                    <span className="cell-muted">
                      {active.name}
                      {displayUnit(active.unit) ? ` (${displayUnit(active.unit)})` : ""}
                    </span>
                  </div>
                  <div className="metric-toolbar">
                    <button
                      className={`btn btn--sm ${paused ? "btn--primary" : ""}`}
                      onClick={() => setPaused((p) => !p)}
                      title={paused ? "Resume live updates" : "Pause live updates"}
                    >
                      {paused ? <PlayIcon size={13} /> : <PauseIcon size={13} />}
                      {paused ? "Resume" : "Pause"}
                    </button>
                    <div className="seg" role="group" aria-label="Time range">
                      {TIME_RANGES.map((r) => (
                        <button
                          key={r.seconds}
                          className={`seg__btn ${r.seconds === windowSeconds ? "active" : ""}`}
                          aria-pressed={r.seconds === windowSeconds}
                          onClick={() => setWindowSeconds(r.seconds)}
                        >
                          {r.label}
                        </button>
                      ))}
                    </div>
                  </div>
                </div>
                <div className="cell-muted metric-detail__sub">
                  {kindLabel(active.kind)} · {active.resourceName ?? "—"} ·{" "}
                  {active.pointCount.toLocaleString()} points
                  {paused ? <span className="metric-paused"> · paused</span> : null}
                </div>
                <div className="metric-detail__chart">
                  {series && series.timestampsMs.length > 0 ? (
                    <MetricChart
                      timestampsMs={series.timestampsMs}
                      lines={lines}
                      unit={series.unit}
                      kind={series.kind}
                      height={300}
                      onUserZoom={() => setPaused(true)}
                    />
                  ) : (
                    <div className="center-fill cell-muted">
                      {series === null ? "Loading…" : "No samples in this window yet."}
                    </div>
                  )}
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
