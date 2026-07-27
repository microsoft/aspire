import { expect, test } from "@playwright/test";
import { orderedTraceResources, traceResourceTooltip } from "../src/lib/traceResources";

// Pure-logic coverage for the per-resource span breakdown shown in the traces list. Mirrors
// TraceHelpers.GetOrderedResources / Traces.razor.cs GetSpansTooltip in the Blazor dashboard.

function span(resourceName: string | null, statusCode: string | null = null) {
  return { resourceName, statusCode };
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
