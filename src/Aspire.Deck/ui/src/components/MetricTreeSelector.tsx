import { useMemo, useState } from "react";
import type { MetricSummary } from "../api/types";
import { Accordion, SearchBox } from "../toolkit";
import { formatMetricValue } from "../lib/format";

export function MetricTreeSelector({
  metrics,
  active,
  onSelect,
}: {
  metrics: MetricSummary[];
  active: MetricSummary | null;
  onSelect: (metric: MetricSummary) => void;
}) {
  const [query, setQuery] = useState("");
  const normalizedQuery = query.trim().toLocaleLowerCase();
  const meters = useMemo(() => {
    const grouped = new Map<string, MetricSummary[]>();
    for (const metric of metrics) {
      const meter = metric.meterName ?? "Unknown meter";
      if (normalizedQuery && !meter.toLocaleLowerCase().includes(normalizedQuery)
        && !metric.name.toLocaleLowerCase().includes(normalizedQuery)) {
        continue;
      }
      const values = grouped.get(meter) ?? [];
      values.push(metric);
      grouped.set(meter, values);
    }
    return [...grouped.entries()]
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([meter, instruments]) => ({
        meter,
        instruments: instruments.sort((left, right) => left.name.localeCompare(right.name)),
      }));
  }, [metrics, normalizedQuery]);
  const [collapsedMeters, setCollapsedMeters] = useState<string[]>([]);
  const openMeters = meters.map((item) => item.meter).filter((meter) => !collapsedMeters.includes(meter));

  return (
    <div className="metric-selector" aria-label="Metric instruments">
      <div className="metric-selector__search">
        <SearchBox value={query} onChange={setQuery} placeholder="Filter meters and instruments" />
      </div>
      {meters.length === 0 ? (
        <div className="metric-selector__empty cell-muted">No matching instruments.</div>
      ) : (
        <Accordion
          className="metric-tree"
          openItems={openMeters}
          onOpenItemsChange={(open) => setCollapsedMeters(meters.map((item) => item.meter).filter((meter) => !open.includes(meter)))}
          items={meters.map(({ meter, instruments }) => ({
            id: meter,
            heading: <span className="metric-tree__meter">{meter}</span>,
            count: instruments.length,
            content: instruments.map((metric) => (
              <button
                key={`${meter}/${metric.name}`}
                type="button"
                className={`metric-item ${metric === active ? "active" : ""}`}
                aria-current={metric === active ? "true" : undefined}
                onClick={() => onSelect(metric)}
              >
                <span className="metric-item__name">{metric.name}</span>
                <span className="metric-item__meta">
                  <span>{formatMetricValue(metric.lastValue, metric.unit)}</span>
                  <span>{metric.kind}</span>
                </span>
              </button>
            )),
          }))}
        />
      )}
    </div>
  );
}
