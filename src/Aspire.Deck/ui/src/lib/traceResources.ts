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
  spanId: string;
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
export function orderedTraceResources(
  spans: readonly TraceResourceSpanInput[],
  uninstrumentedPeers?: ReadonlyMap<string, string>,
): TraceResourceSpans[] {
  const byResource = new Map<string, TraceResourceSpans>();

  const record = (resourceName: string | null, statusCode: string | null): void => {
    if (resourceName === null || resourceName === "") {
      return;
    }

    let entry = byResource.get(resourceName);
    if (entry === undefined) {
      entry = { resourceName, totalSpans: 0, erroredSpans: 0 };
      byResource.set(resourceName, entry);
    }

    entry.totalSpans++;
    if (statusCode === "Error") {
      entry.erroredSpans++;
    }
  };

  for (const span of spans) {
    record(span.resourceName, span.statusCode);

    // `GetOrderedResources` processes a span's uninstrumented peer immediately after the span's own
    // resource and with the same timestamp, so the callee is grouped directly after its caller and
    // the span is counted against both ends of the call.
    const peer = uninstrumentedPeers?.get(span.spanId);
    if (peer !== undefined) {
      record(peer, span.statusCode);
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
