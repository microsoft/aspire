import { expect, test } from "@playwright/test";
import { orderedTraceResources, traceResourceTooltip } from "../src/lib/traceResources";

// Pure-logic coverage for the per-resource span breakdown shown in the traces list. Mirrors
// TraceHelpers.GetOrderedResources / Traces.razor.cs GetSpansTooltip in the Blazor dashboard.

let nextSpanId = 0;

function span(resourceName: string | null, statusCode: string | null = null) {
  return { spanId: `span-${nextSpanId++}`, resourceName, statusCode };
}

test.describe("orderedTraceResources", () => {
  test("groups spans by resource and counts them", () => {
    const result = orderedTraceResources([span("api"), span("api"), span("db")]);

    expect(result).toEqual([
      { resourceName: "api", totalSpans: 2, erroredSpans: 0 },
      { resourceName: "db", totalSpans: 1, erroredSpans: 0 },
    ]);
  });

  test("preserves first-appearance order rather than sorting by name", () => {
    const result = orderedTraceResources([span("zebra"), span("alpha"), span("zebra")]);

    expect(result.map((r) => r.resourceName)).toEqual(["zebra", "alpha"]);
  });

  test("counts errored spans separately from the total", () => {
    const result = orderedTraceResources([span("api", "Error"), span("api", "Ok"), span("api")]);

    expect(result).toEqual([{ resourceName: "api", totalSpans: 3, erroredSpans: 1 }]);
  });

  test("skips spans with no resource name", () => {
    const result = orderedTraceResources([span(null), span(""), span("api")]);

    expect(result).toEqual([{ resourceName: "api", totalSpans: 1, erroredSpans: 0 }]);
  });

  test("returns nothing for a trace with no spans", () => {
    expect(orderedTraceResources([])).toEqual([]);
  });

  test("attributes a span to its uninstrumented peer as well as its own resource", () => {
    const caller = span("api");
    const result = orderedTraceResources([caller], new Map([[caller.spanId, "redis"]]));

    // The single span is counted against both ends of the call, and the callee is grouped directly
    // after the caller that reached it.
    expect(result).toEqual([
      { resourceName: "api", totalSpans: 1, erroredSpans: 0 },
      { resourceName: "redis", totalSpans: 1, erroredSpans: 0 },
    ]);
  });

  test("counts an errored span against the peer as well as the caller", () => {
    const caller = span("api", "Error");
    const result = orderedTraceResources([caller], new Map([[caller.spanId, "redis"]]));

    expect(result).toEqual([
      { resourceName: "api", totalSpans: 1, erroredSpans: 1 },
      { resourceName: "redis", totalSpans: 1, erroredSpans: 1 },
    ]);
  });

  test("merges repeated calls to the same peer into one entry", () => {
    const first = span("api");
    const second = span("api");
    const result = orderedTraceResources(
      [first, second],
      new Map([[first.spanId, "redis"], [second.spanId, "redis"]]),
    );

    expect(result).toEqual([
      { resourceName: "api", totalSpans: 2, erroredSpans: 0 },
      { resourceName: "redis", totalSpans: 2, erroredSpans: 0 },
    ]);
  });
});

test.describe("traceResourceTooltip", () => {
  test("omits the error line when nothing errored", () => {
    const tooltip = traceResourceTooltip({ resourceName: "api", totalSpans: 4, erroredSpans: 0 });

    expect(tooltip).toBe("api spans\nTotal: 4");
  });

  test("includes the error line when spans errored", () => {
    const tooltip = traceResourceTooltip({ resourceName: "api", totalSpans: 4, erroredSpans: 2 });

    expect(tooltip).toBe("api spans\nTotal: 4\nErrored: 2");
  });
});
