import { useCallback, useEffect, useMemo, useState } from "react";
import type { MetricSummary, MetricSeriesResponse } from "../api/types";
import { clearMetrics, getMetricSeries } from "../api/deck";
import { MetricChart, type ChartLine } from "../components/MetricChart";
import { MetricDimensionFilters } from "../components/MetricDimensionFilters";
import { MetricExemplars } from "../components/MetricExemplars";
import { MetricTreeSelector } from "../components/MetricTreeSelector";
import { displayUnit, formatMetricValue } from "../lib/format";
import { useResources, useTelemetry } from "../lib/useDeckEvent";
import {
  CommandMenu,
  Checkbox,
  EmptyState,
  MetricsIcon,
  NamedIcon,
  Page,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
  Select,
  Switch,
  Tabs,
} from "../toolkit";

const TIME_RANGES: ReadonlyArray<{ label: string; title: string; seconds: number }> = [
  { label: "1m", title: "Last minute", seconds: 60 },
  { label: "5m", title: "Last 5 minutes", seconds: 300 },
  { label: "15m", title: "Last 15 minutes", seconds: 900 },
  { label: "30m", title: "Last 30 minutes", seconds: 1800 },
  { label: "1h", title: "Last hour", seconds: 3600 },
  { label: "3h", title: "Last 3 hours", seconds: 10_800 },
  { label: "6h", title: "Last 6 hours", seconds: 21_600 },
  { label: "12h", title: "Last 12 hours", seconds: 43_200 },
];

const POLL_MS = 1500;

export interface MetricRouteState {
  resourceName: string | null;
  meterName: string | null;
  metricName: string | null;
  windowSeconds: number;
  view: "chart" | "table";
  paused: boolean;
  dimensions: Record<string, Array<string | null>>;
  showCount: boolean;
}

function cssVar(name: string, fallback: string): string {
  if (typeof window === "undefined") {
    return fallback;
  }
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}

function buildLines(series: MetricSeriesResponse): ChartLine[] {
  if (series.kind === "histogram" && !series.showCount) {
    return [
      { label: "p50", color: cssVar("--info", "#60a5fa"), values: series.p50 ?? [] },
      { label: "p90", color: cssVar("--accent", "#a855f7"), values: series.p90 ?? [] },
      { label: "p99", color: cssVar("--warning", "#fbbf24"), values: series.p99 ?? [] },
    ];
  }
  const label = series.kind === "counter" ? "rate" : series.showCount ? "count" : "value";
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

export function MetricsPage({
  routeResourceName,
  routeMeterName,
  routeMetricName,
  routeWindowSeconds,
  routeView,
  routePaused,
  routeDimensions,
  routeShowCount,
  onRouteChange,
  onNavigateToSpan,
}: {
  routeResourceName: string | null;
  routeMeterName: string | null;
  routeMetricName: string | null;
  routeWindowSeconds: number;
  routeView: "chart" | "table";
  routePaused: boolean;
  routeDimensions: Record<string, Array<string | null>>;
  routeShowCount: boolean;
  onRouteChange: (state: MetricRouteState) => void;
  onNavigateToSpan: (traceId: string, spanId: string) => void;
}) {
  const telemetry = useTelemetry();
  const { resources } = useResources();
  const [seriesState, setSeriesState] = useState<{ key: string; value: MetricSeriesResponse | null } | null>(null);
  const [clearing, setClearing] = useState(false);
  const [clearStatus, setClearStatus] = useState<{ message: string; error: boolean } | null>(null);

  const allMetrics = useMemo(
    () => [...(telemetry?.metrics ?? [])].sort((left, right) => left.name.localeCompare(right.name)),
    [telemetry],
  );
  const resourceOptions = useMemo(() => {
    const resourceTypes = new Map(resources.map((resource) => [resource.name, resource.resourceType]));
    return [...new Set(allMetrics.flatMap((metric) => metric.resourceName === null ? [] : [metric.resourceName]))]
      .sort((left, right) => left.localeCompare(right))
      .map((name) => ({ value: name, label: name, group: resourceTypes.get(name) ?? "Telemetry" }));
  }, [allMetrics, resources]);
  const selectedResource = routeResourceName !== null
    && resourceOptions.some((option) => option.value === routeResourceName)
    ? routeResourceName
    : resourceOptions[0]?.value ?? null;
  const metrics = useMemo(
    () => allMetrics.filter((metric) => metric.resourceName === selectedResource),
    [allMetrics, selectedResource],
  );
  const active: MetricSummary | null = metrics.find((metric) =>
    metric.name === routeMetricName && (routeMeterName === null || metric.meterName === routeMeterName))
    ?? metrics[0]
    ?? null;
  const selectedWindowSeconds = TIME_RANGES.some((range) => range.seconds === routeWindowSeconds)
    ? routeWindowSeconds
    : 300;
  const activeName = active?.name ?? null;
  const activeMeterName = active?.meterName ?? null;
  const activeResourceName = active?.resourceName ?? null;
  const activeKind = active?.kind ?? null;
  const activeSeriesKey = activeName === null
    ? null
    : `${activeResourceName ?? ""}\u0000${activeMeterName ?? ""}\u0000${activeName}`;
  const series = activeSeriesKey !== null && seriesState?.key === activeSeriesKey ? seriesState.value : null;

  const updateRoute = (changes: Partial<MetricRouteState>): void => {
    onRouteChange({
      resourceName: selectedResource,
      meterName: active?.meterName ?? null,
      metricName: active?.name ?? null,
      windowSeconds: selectedWindowSeconds,
      view: routeView,
      paused: routePaused,
      dimensions: routeDimensions,
      showCount: routeShowCount,
      ...changes,
    });
  };

  const fetchSeries = useCallback(async () => {
    if (activeName === null) {
      setSeriesState(null);
      return;
    }
    const value = await getMetricSeries({
      name: activeName,
      meterName: activeMeterName,
      resourceName: activeResourceName,
      windowSeconds: selectedWindowSeconds,
      maxPoints: 600,
      dimensions: routeDimensions,
      showCount: activeKind === "histogram" && routeShowCount,
    });
    setSeriesState({ key: activeSeriesKey!, value });
  }, [activeKind, activeMeterName, activeName, activeResourceName, activeSeriesKey, routeDimensions, routeShowCount, selectedWindowSeconds]);

  useEffect(() => {
    void fetchSeries();
  }, [fetchSeries]);

  useEffect(() => {
    if (routePaused || activeName === null) {
      return;
    }
    const id = window.setInterval(() => void fetchSeries(), POLL_MS);
    return () => window.clearInterval(id);
  }, [activeName, fetchSeries, routePaused]);

  const clearMetricData = async (resourceName: string | null): Promise<void> => {
    setClearing(true);
    setClearStatus(null);
    try {
      await clearMetrics(resourceName);
      setSeriesState(null);
      updateRoute({ resourceName: null, meterName: null, metricName: null, paused: false, dimensions: {}, showCount: false });
      setClearStatus({
        message: resourceName === null ? "Cleared all metrics." : `Cleared metrics for ${resourceName}.`,
        error: false,
      });
    } catch (error) {
      setClearStatus({ message: `Could not clear metrics: ${String(error)}`, error: true });
    } finally {
      setClearing(false);
    }
  };

  const lines = useMemo(() => (series ? buildLines(series) : []), [series]);
  const chart = series && series.timestampsMs.length > 0 ? (
    <MetricChart
      timestampsMs={series.timestampsMs}
      lines={lines}
      unit={series.unit}
      kind={series.kind}
      height={300}
      onUserZoom={() => updateRoute({ paused: true })}
    />
  ) : (
    <div className="center-fill cell-muted">
      {active === null ? "Select a metric" : series === null ? "Loading…" : "No samples in this window yet."}
    </div>
  );

  return (
    <Page aria-labelledby="deck-page-metrics-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-metrics-title">Metrics</PageTitle>
          <PageSubtitle>
            {telemetry === null
              ? "Loading…"
              : selectedResource === null
                ? "Select a resource"
                : `${metrics.length} instruments${routePaused ? " · paused" : ""}`}
          </PageSubtitle>
        </PageHeading>
      </PageHeader>

      <PageToolbar ariaLabel="Metric tools">
        <Select
          ariaLabel="Resource"
          options={resourceOptions}
          value={selectedResource ?? ""}
          placeholder="Select a resource"
          disabled={resourceOptions.length === 0}
          onValueChange={(resourceName) => updateRoute({ resourceName, meterName: null, metricName: null, dimensions: {}, showCount: false })}
        />
        <div className="seg" role="group" aria-label="Time range">
          {TIME_RANGES.map((range) => (
            <button
              key={range.seconds}
              type="button"
              className={`seg__btn ${range.seconds === selectedWindowSeconds ? "active" : ""}`}
              aria-pressed={range.seconds === selectedWindowSeconds}
              title={range.title}
              onClick={() => updateRoute({ windowSeconds: range.seconds })}
            >
              {range.label}
            </button>
          ))}
        </div>
        <div className="page__header-spacer" />
        <Switch
          label="Pause incoming data"
          checked={routePaused}
          disabled={active === null}
          onCheckedChange={(paused) => updateRoute({ paused })}
        />
        <CommandMenu
          ariaLabel="Clear metrics"
          triggerContent="Clear"
          triggerIcon={<NamedIcon name="Delete" size={16} />}
          placement="below-end"
          entries={[
            {
              id: "clear-all",
              label: "Clear all resources",
              icon: <NamedIcon name="BoxMultiple" size={16} />,
              tone: "danger",
              disabled: clearing || allMetrics.length === 0,
              onSelect: () => void clearMetricData(null),
            },
            {
              id: "clear-resource",
              label: selectedResource === null ? "Clear selected resource" : `Clear ${selectedResource}`,
              icon: <NamedIcon name="CheckmarkCircle" size={16} />,
              tone: "danger",
              disabled: clearing || selectedResource === null,
              onSelect: () => void clearMetricData(selectedResource),
            },
          ]}
        />
      </PageToolbar>

      <PageBody>
        {telemetry !== null && selectedResource !== null && metrics.length > 0 ? (
          <div className="metrics-layout">
            <div className="metric-list">
              <MetricTreeSelector
                metrics={metrics}
                active={active}
                onSelect={(metric) => updateRoute({ meterName: metric.meterName ?? null, metricName: metric.name, dimensions: {}, showCount: false })}
              />
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
                  </div>
                  <div className="cell-muted metric-detail__sub">
                    {kindLabel(active.kind)} · {active.resourceName ?? "—"} · {active.meterName ?? "Unknown meter"} · {active.pointCount.toLocaleString()} points
                  </div>
                  {active.description ? <div className="metric-detail__description">{active.description}</div> : null}
                  {series?.hasOverflow ? <div className="metric-overflow" role="status">Some metric dimensions exceeded the dashboard limit.</div> : null}
                  <MetricDimensionFilters
                    filters={series?.dimensionFilters ?? []}
                    selected={routeDimensions}
                    onChange={(dimensions) => updateRoute({ dimensions })}
                  />
                  {active.kind === "histogram" ? (
                    <div className="metric-histogram-options">
                      <Checkbox
                        checked={routeShowCount}
                        label="Show count"
                        onCheckedChange={(showCount) => updateRoute({ showCount })}
                      />
                    </div>
                  ) : null}
                  <Tabs
                    ariaLabel="Metric view"
                    selectedId={routeView}
                    onTabChange={(view) => updateRoute({ view: view as "chart" | "table" })}
                    tabs={[
                      {
                        id: "chart",
                        label: "Chart",
                        icon: <NamedIcon name="Graph" size={16} />,
                        content: <div className="metric-detail__chart">{chart}</div>,
                      },
                      {
                        id: "table",
                        label: "Table",
                        icon: <NamedIcon name="TableLightning" size={16} />,
                        content: <MetricSeriesTable series={series} />,
                      },
                    ]}
                  />
                  <MetricExemplars
                    exemplars={series?.exemplars ?? []}
                    unit={series?.unit ?? active.unit}
                    onNavigateToSpan={onNavigateToSpan}
                  />
                </>
              ) : null}
            </div>
          </div>
        ) : (
          <EmptyState icon={<MetricsIcon size={26} />} title={telemetry === null ? "Loading metrics" : "No metrics for this resource"}>
            {telemetry === null ? "Waiting for the telemetry snapshot." : "Metrics will appear here as OTLP data arrives."}
          </EmptyState>
        )}
      </PageBody>

      {clearStatus ? (
        <div className="toast" role="status" aria-live="polite">
          <span className={`state__dot ${clearStatus.error ? "error" : "success"}`} />
          {clearStatus.message}
        </div>
      ) : null}
    </Page>
  );
}

function MetricSeriesTable({ series }: { series: MetricSeriesResponse | null }) {
  if (series === null || series.timestampsMs.length === 0) {
    return <div className="center-fill cell-muted metric-series-empty">No samples in this window yet.</div>;
  }

  const lines = series.kind === "histogram" && !series.showCount
    ? [series.p50 ?? [], series.p90 ?? [], series.p99 ?? []]
    : [series.values ?? []];
  const headers = series.kind === "histogram" && !series.showCount ? ["p50", "p90", "p99"] : [series.showCount ? "Count" : "Value"];
  return (
    <div className="table-wrap metric-series-table">
      <table className="data">
        <thead>
          <tr>
            <th>Time</th>
            {headers.map((header) => <th key={header}>{header}</th>)}
          </tr>
        </thead>
        <tbody>
          {series.timestampsMs.map((timestamp, index) => (
            <tr key={`${timestamp}-${index}`}>
              <td className="cell-mono">{new Date(timestamp).toLocaleTimeString()}</td>
              {lines.map((values, lineIndex) => (
                <td key={headers[lineIndex]} className="cell-mono">
                  {formatMetricValue(values[index] ?? null, series.unit)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
