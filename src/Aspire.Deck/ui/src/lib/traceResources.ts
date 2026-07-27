/**
 * Per-resource span breakdown for a trace, mirroring the Blazor dashboard's
 * `TraceHelpers.GetOrderedResources` (src/Aspire.Dashboard/Model/TraceHelpers.cs).
 */
export interface TraceResourceSpans {
  resourceName: string;
  totalSpans: number;
  erroredSpans: number;
}

interface TraceResourceSpanInput {
  resourceName: string | null;
  statusCode: string | null;
}

/**
 * Groups a trace's spans by resource, in the order each resource first appears in the
 * trace's span tree.
 *
 * The C# implementation sorts by `FirstDateTime` and then by insertion `Index`, where
 * `FirstDateTime` is a running *maximum* of the visited spans' start times (despite the
 * name) captured at the moment the resource is first seen. Because that running value is
 * non-decreasing across the visit, sorting by it and then by index is equivalent to plain
 * first-appearance order, which is what we do here. Callers must pass the spans already
 * flattened in tree order (parents before children), which is how the waterfall orders them.
 */
export function orderedTraceResources(spans: readonly TraceResourceSpanInput[]): TraceResourceSpans[] {
  const byResource = new Map<string, TraceResourceSpans>();

  for (const span of spans) {
    const resourceName = span.resourceName;
    if (resourceName === null || resourceName === "") {
      continue;
    }

    let entry = byResource.get(resourceName);
    if (entry === undefined) {
      entry = { resourceName, totalSpans: 0, erroredSpans: 0 };
      byResource.set(resourceName, entry);
    }

    entry.totalSpans++;
    if (span.statusCode === "Error") {
      entry.erroredSpans++;
    }
  }

  return [...byResource.values()];
}

/**
 * Tooltip text for a resource tag. Mirrors `Traces.razor.cs`'s `GetSpansTooltip`, which
 * joins the resource header, the total, and the (optional) error count with newlines.
 */
export function traceResourceTooltip(resource: TraceResourceSpans): string {
  const lines = [`${resource.resourceName} spans`, `Total: ${resource.totalSpans}`];
  if (resource.erroredSpans > 0) {
    lines.push(`Errored: ${resource.erroredSpans}`);
  }

  return lines.join("\n");
}
