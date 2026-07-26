import { expect, test, type Page, type Request, type Route } from "@playwright/test";

/**
 * Generous because the first test to reach this page pays for Vite compiling its module graph, and
 * the parity suite runs several workers in parallel against one dev server.
 */
const READY_TIMEOUT_MS = 60_000;

/**
 * The Native AOT backend is reached through a different client than the Blazor dashboard
 * (`src/api/native.ts` rather than `src/api/http.ts`), so the retention fix has to be proven
 * separately on that path. `native.ts` fetched the trace snapshot with a hardcoded `limit=10000`,
 * which silently truncated the view for anyone who raised
 * `Dashboard:TelemetryLimits:MaxTraceCount` above the default.
 *
 * These tests drive the browser with `?backend=aot`, stub the versioned discovery/shell/traces
 * contract, and assert on the *outgoing* request URL.
 */

const CONFIGURED_TRACE_COUNT = 87_654;
/** TelemetryLimitOptions.MaxTraceCount in the dashboard, used when a host omits the field. */
const DASHBOARD_DEFAULT_TRACE_COUNT = 10_000;

const BASE_PATH = "/api/dashboard/v1";

const CAPABILITIES = [
  "configuration",
  "shell",
  "culture",
  "authentication",
  "manage-data",
  "resources",
  "resources-live",
  "commands",
  "structured-logs",
  "structured-logs-live",
  "structured-logs-clear",
  "traces",
  "traces-live",
  "traces-clear",
  "metrics",
  "metrics-series",
  "metrics-clear",
  "console-logs",
  "console-logs-live",
  "terminal",
  "interactions"
];

function shellPayload(telemetryLimits: { maxTraceCount: number } | null): unknown {
  return {
    applicationName: "aot-limits-test",
    resourceServiceUrl: null,
    otlpGrpcUrl: null,
    otlpHttpUrl: null,
    version: "0.0.0-test",
    ...(telemetryLimits === null
      ? {}
      : {
          telemetryLimits: {
            maxLogCount: 10_000,
            maxTraceCount: telemetryLimits.maxTraceCount,
            maxMetricsCount: 50_000,
            maxConsoleLogCount: 10_000
          }
        })
  };
}

/**
 * Records every trace-snapshot URL the client requests.
 *
 * Route handlers are evaluated in reverse registration order, so the broad catch-all is registered
 * first and the specific stubs registered after it take precedence.
 */
async function stubAotApi(
  page: Page,
  telemetryLimits: { maxTraceCount: number } | null
): Promise<string[]> {
  const traceRequests: string[] = [];

  await page.route(/\/api\/dashboard/, (route: Route, request: Request) => {
    if (request.method() !== "GET") {
      return route.fulfill({ status: 204, body: "" });
    }
    return route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
  });

  await page.route(new RegExp(`${BASE_PATH}/traces(\\?.*)?$`), (route: Route, request: Request) => {
    traceRequests.push(request.url());
    return route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ totalCount: 0, returnedCount: 0, data: { resourceSpans: [] } })
    });
  });

  await page.route(new RegExp(`${BASE_PATH}/shell(\\?.*)?$`), (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(shellPayload(telemetryLimits))
    }));

  // Registered last so it wins: this is the discovery document the client negotiates against.
  await page.route(/\/api\/dashboard$/, (route: Route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        product: "Aspire.Dashboard",
        versions: [{ version: 1, basePath: BASE_PATH, capabilities: CAPABILITIES }]
      })
    }));

  return traceRequests;
}

async function readTraceLimit(page: Page, traceRequests: string[]): Promise<number> {
  await page.goto("/traces?backend=aot");
  await expect(page.getByRole("navigation")).toBeVisible({ timeout: READY_TIMEOUT_MS });

  await expect
    .poll(() => traceRequests.length, { timeout: READY_TIMEOUT_MS })
    .toBeGreaterThan(0);

  const limit = new URL(traceRequests[0]!).searchParams.get("limit");
  expect(limit, "the AOT trace snapshot must be sized explicitly").not.toBeNull();
  return Number(limit);
}

test.describe("configured telemetry retention on the AOT backend", () => {
  test("sizes the trace snapshot from the configured limit, not a hardcoded 10000", async ({ page }) => {
    const traceRequests = await stubAotApi(page, { maxTraceCount: CONFIGURED_TRACE_COUNT });

    expect(await readTraceLimit(page, traceRequests)).toBe(CONFIGURED_TRACE_COUNT);
  });

  test("falls back to the dashboard default when the host omits the limits", async ({ page }) => {
    const traceRequests = await stubAotApi(page, null);

    expect(await readTraceLimit(page, traceRequests)).toBe(DASHBOARD_DEFAULT_TRACE_COUNT);
  });
});
