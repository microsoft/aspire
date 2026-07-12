import type { MetricExemplar } from "../api/types";
import { Button } from "../toolkit";
import { formatMetricValue } from "../lib/format";

export function MetricExemplars({
  exemplars,
  unit,
  onNavigateToSpan,
}: {
  exemplars: MetricExemplar[];
  unit: string | null;
  onNavigateToSpan: (traceId: string, spanId: string) => void;
}) {
  if (exemplars.length === 0) {
    return null;
  }

  return (
    <section className="metric-exemplars" aria-label="Metric exemplars">
      <h3>Exemplars</h3>
      <div className="table-wrap">
        <table className="data">
          <thead><tr><th>Time</th><th>Value</th><th>Attributes</th><th>Trace</th></tr></thead>
          <tbody>
            {exemplars.map((exemplar, index) => (
              <tr key={`${exemplar.timestampMs}-${exemplar.traceId}-${index}`}>
                <td className="cell-mono">{new Date(exemplar.timestampMs).toLocaleTimeString()}</td>
                <td className="cell-mono">{formatMetricValue(exemplar.value, unit)}</td>
                <td>{exemplar.attributes.length === 0 ? "—" : exemplar.attributes.map((attribute) => `${attribute.key}=${attribute.value}`).join(", ")}</td>
                <td>
                  <Button size="small" variant="ghost" onClick={() => onNavigateToSpan(exemplar.traceId, exemplar.spanId)}>
                    View trace
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
