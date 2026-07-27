import type { MetricDimensionFilter } from "../api/types";
import { Checkbox } from "../toolkit";

function valueLabel(value: string | null): string {
  if (value === null) {
    return "(unset)";
  }
  return value.length === 0 ? "(empty)" : value;
}

export function MetricDimensionFilters({
  filters,
  selected,
  onChange,
}: {
  filters: MetricDimensionFilter[];
  selected: Record<string, Array<string | null>>;
  onChange: (selected: Record<string, Array<string | null>>) => void;
}) {
  if (filters.length === 0) {
    return null;
  }

  const selectedValues = (filter: MetricDimensionFilter): Array<string | null> => selected[filter.name] ?? filter.values;
  const setValues = (filter: MetricDimensionFilter, values: Array<string | null>): void => {
    const next = { ...selected };
    if (values.length === filter.values.length && filter.values.every((value) => values.includes(value))) {
      delete next[filter.name];
    } else {
      next[filter.name] = values;
    }
    onChange(next);
  };

  return (
    <section className="metric-filters" aria-label="Metric dimension filters">
      <h3>Dimensions</h3>
      {filters.map((filter) => {
        const values = selectedValues(filter);
        const allSelected = values.length === filter.values.length && filter.values.every((value) => values.includes(value));
        return (
          <details key={filter.name} className="metric-filter">
            <summary>
              <span className="cell-mono">{filter.name}</span>
              <span className="cell-muted">{allSelected ? "All" : `${values.length} of ${filter.values.length}`}</span>
            </summary>
            <div className="metric-filter__values">
              <Checkbox
                checked={allSelected}
                indeterminate={!allSelected && values.length > 0}
                label="All"
                onCheckedChange={(checked) => setValues(filter, checked ? [...filter.values] : [])}
              />
              {filter.values.map((value) => (
                <Checkbox
                  key={value === null ? "__unset" : `value:${value}`}
                  checked={values.includes(value)}
                  label={valueLabel(value)}
                  title={valueLabel(value)}
                  onCheckedChange={(checked) => setValues(
                    filter,
                    checked ? [...values, value] : values.filter((candidate) => candidate !== value),
                  )}
                />
              ))}
            </div>
          </details>
        );
      })}
    </section>
  );
}
