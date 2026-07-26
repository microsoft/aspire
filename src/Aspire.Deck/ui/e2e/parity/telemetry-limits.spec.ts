import { expect, test, type Page, type Request, type Route } from "@playwright/test";

/**
 * Generous because the first test to reach this page pays for Vite compiling its module graph, and
 * the parity suite runs several workers in parallel against one dev server.
 */
const READY_TIMEOUT_MS = 60_000;

/**
 * The Blazor dashboard exposes its retention ceilings through `Dashboard:TelemetryLimits:*` and
 * `Dashboard:Frontend:MaxConsoleLogCount`. The Deck UI previously ignored that configuration and
 * asked for a fixed 200 records, so raising the server-side limits had no effect on the bounded
 * fetches.
 *
 * These tests drive the real browser with `?backend=http` (the HTTP client rather than the
 * in-process mock), stub the API surface, and assert on the *outgoing* request URLs. The bounded
 * `?limit=` fetch is the post-mutation resynchronisation path -- the live view is fed by the
 * `follow=true` NDJSON streams -- so the tests trigger it the way a user does, by clearing logs.
 */

const CONFIGURED_LIMITS = {
  maxLogCount: 12_345,
  maxTraceCount: 23_456,
  maxMetricsCount: 34_567,
  maxConsoleLogCount: 45_678
};

/** TelemetryLimitOptions.MaxLogCount in the dashboard, used when a backend omits the field. */
const DASHBOARD_DEFAULT_LOG_COUNT = 10_000;

const CONFIG_ROUTE = /\/api\/(deck|dashboard\/v1)\/config(\?.*)?$/;
const API_ROUTE = /\/api\/(deck|dashboard\/v1)\//;

/** One OTLP log record, enough to make the "clear" commands selectable. */
const SEEDED_LOG_STREAM = `${JSON.stringify({
  resourceLogs: [
    {
      resource: { attributes: [{ key: "service.name", value: { stringValue: "limits-test" } }] },
      scopeLogs: [
        {
          scope: { name: "limits.test" },
          logRecords: [
            {
              timeUnixNano: "1700000000000000000",
              observedTimeUnixNano: "1700000000000000000",
              severityNumber: 9,
              severityText: "Information",
              body: { stringValue: "seeded" },
              attributes: [{ key: "aspire.log_id", value: { stringValue: "seed-1" } }]
            }
          ]
        }
      ]
    }
  ]
})}\n`;

async function stubApi(page: Page, telemetryLimits: typeof CONFIGURED_LIMITS | null): Promise<void> {
  // Playwright evaluates route handlers in reverse registration order, so this broad
  // catch-all is registered first and the specific config stub below takes precedence.
  // The Vite dev server has no API behind it, so anything unstubbed would 404 and tear the shell
  // down before it reaches the bounded fetch. Requests are still observed by the capture listener
  // before being fulfilled here, which is what these tests assert on.
  await page.route(API_ROUTE, (route: Route) => {
    const url = route.request().url();

    if (url.includes("follow=true")) {
      return route.fulfill({
        status: 200,
        contentType: "application/x-ndjson",
        body: url.includes("telemetry/logs") ? SEEDED_LOG_STREAM : ""
      });
    }

    if (/telemetry\/(logs|spans)/.test(url)) {
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ data: {}, totalCount: 0 })
      });
    }

    return route.fulfill({ status: 200, contentType: "application/json", body: "[]" });
  });

  await page.route(CONFIG_ROUTE, (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        applicationName: "limits-test",
        resourceServiceUrl: null,
        otlpGrpcUrl: null,
        otlpHttpUrl: null,
        version: "0.0.0-test",
        ...(telemetryLimits === null ? {} : { telemetryLimits })
      })
    }));
}

/**
 * Collects GET request URLs matching `pattern`, skipping the `follow=true` streams. Those endpoints
 * intentionally carry no limit: they tail new records rather than fetching a bounded backlog. The
 * DELETE that clears the logs shares the same path, so the method filter keeps it out too.
 */
async function captureRequests(page: Page, pattern: RegExp, action: () => Promise<void>): Promise<string[]> {
  const seen: string[] = [];
  const listener = (request: Request): void => {
    const url = request.url();
    if (request.method() === "GET" && pattern.test(url) && !url.includes("follow=true")) {
      seen.push(url);
    }
  };

  page.on("request", listener);
  try {
    await action();
  } finally {
    page.off("request", listener);
  }

  return seen;
}

/** Reads the `limit` query parameter from every captured URL, failing if any request omits it. */
function limitsFrom(urls: string[]): number[] {
  return urls.map((url) => {
    const limit = new URL(url).searchParams.get("limit");
    expect(limit, `expected a limit query parameter on ${url}`).not.toBeNull();
    return Number(limit);
  });
}

/**
 * Opens the structured logs page against the HTTP client and invokes a clear command, which is the
 * user-facing action that resynchronises from the bounded endpoints.
 */
async function clearLogsViaUi(page: Page, commandId: string): Promise<void> {
  await page.goto("/structuredlogs?backend=http");

  // The clear commands stay disabled until the page has at least one record, so wait for the seeded
  // row to arrive over the NDJSON stream instead of racing it.
  await expect(page.getByText("seeded").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });

  const command = page.locator(`[data-command-id="${commandId}"], #${commandId}`).first();
  if ((await command.count()) > 0) {
    await command.click();
  } else {
    // Fall back to the visible menu when the command surface has no stable id.
    await page.getByRole("button", { name: /clear/i }).first().click();
    await page.getByRole("menuitem", { name: /all/i }).first().click();
  }

  // The resynchronisation fetch is issued after the DELETE resolves.
  await expect(page.getByText(/Cleared .*structured logs/i).first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
}

test.describe("configured telemetry retention", () => {
  test("resynchronises structured logs with the configured limit, not a hardcoded 200", async ({ page }) => {
    await stubApi(page, CONFIGURED_LIMITS);

    const requests = await captureRequests(page, /telemetry\/logs/, () => clearLogsViaUi(page, "clear-all"));

    expect(requests.length).toBeGreaterThan(0);
    const limits = limitsFrom(requests);
    expect(limits).not.toContain(200);
    expect(limits).toContain(CONFIGURED_LIMITS.maxLogCount);
  });

  test("falls back to the dashboard default when the backend omits the limits", async ({ page }) => {
    // A dashboard predating the telemetryLimits field must not regress to the old 200-record cap,
    // nor omit the limit entirely and let the server apply its own 200-record default.
    await stubApi(page, null);

    const requests = await captureRequests(page, /telemetry\/logs/, () => clearLogsViaUi(page, "clear-all"));

    expect(requests.length).toBeGreaterThan(0);
    for (const limit of limitsFrom(requests)) {
      expect(limit).toBe(DASHBOARD_DEFAULT_LOG_COUNT);
    }
  });
});
