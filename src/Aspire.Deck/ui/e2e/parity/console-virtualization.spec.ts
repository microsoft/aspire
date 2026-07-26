import { expect, test, type Page, type Route } from "@playwright/test";

/**
 * The console buffers up to `Dashboard:Frontend:MaxConsoleLogCount` lines (10,000 by default).
 * Clipped mode has always windowed the list, but wrap mode used to render every buffered line into
 * the DOM, so turning wrapping on turned a bounded render into an unbounded one.
 *
 * These tests assert on the rendered DOM rather than on pixels: they count the `.log-line` elements
 * that actually exist, confirm the spacer is tall enough to scroll the whole buffer, and confirm
 * that scrolling swaps in later lines. Wrapped rows have variable height, so the window is driven
 * by measurements -- these tests are what prove those measurements produce usable offsets.
 */

const RESOURCE = "wrap-test";
const LINE_COUNT = 2_000;

/** A long line, so wrap mode genuinely produces multi-row lines rather than degenerate one-row ones. */
function bodyFor(index: number): string {
  return `line-${index} ${"lorem ipsum dolor sit amet consectetur adipiscing elit ".repeat(6)}`;
}

const CONSOLE_STREAM = `${JSON.stringify({
  resourceName: RESOURCE,
  lines: Array.from({ length: LINE_COUNT }, (_, index) => ({
    lineNumber: index + 1,
    text: `2024-01-01T00:00:00.000Z ${bodyFor(index)}`,
    isStdErr: false,
    html: null
  }))
})}\n`;

const RESOURCES = [
  {
    name: RESOURCE,
    resourceType: "Project",
    displayName: RESOURCE,
    uid: RESOURCE,
    state: "Running",
    stateStyle: "success",
    health: "Healthy",
    createdAt: "2024-01-01T00:00:00Z",
    startedAt: "2024-01-01T00:00:00Z",
    stoppedAt: null,
    urls: [],
    properties: [],
    environment: [],
    commands: [],
    relationships: [],
    healthReports: [],
    volumes: [],
    isHidden: false
  }
];

async function stubApi(page: Page): Promise<void> {
  await page.route(/\/api\/(deck|dashboard\/v1)\//, (route: Route) => {
    const url = route.request().url();

    if (url.includes("/console-logs")) {
      return route.fulfill({
        status: 200,
        contentType: "application/x-ndjson",
        body: CONSOLE_STREAM
      });
    }

    if (url.includes("/resources")) {
      return route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(RESOURCES)
      });
    }

    if (url.includes("follow=true")) {
      return route.fulfill({ status: 200, contentType: "application/x-ndjson", body: "" });
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

  // Registered last so it wins: Playwright evaluates route handlers in reverse registration order.
  await page.route(/\/api\/(deck|dashboard\/v1)\/config(\?.*)?$/, (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        applicationName: "wrap-test",
        resourceServiceUrl: null,
        otlpGrpcUrl: null,
        otlpHttpUrl: null,
        version: "0.0.0-test"
      })
    }));
}

/**
 * Generous because the first test to reach the console page pays for Vite compiling its module
 * graph, and the parity suite runs several workers in parallel against one dev server.
 */
const READY_TIMEOUT_MS = 60_000;

async function openConsole(page: Page, wrap: boolean): Promise<void> {
  const wrapParam = wrap ? "&wrap=true" : "";
  await page.goto(`/consolelogs/resource/${RESOURCE}?backend=http${wrapParam}`);
  await expect(page.locator(".log-line").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
  // The footer only reports the full count once the whole backlog has been ingested.
  await expect(page.locator(".console__footer")).toContainText(`${LINE_COUNT.toLocaleString()} lines`, {
    timeout: READY_TIMEOUT_MS
  });
}

test.describe("console wrap mode virtualization", () => {
  test("renders only a window of the buffer when wrapping is on", async ({ page }) => {
    await stubApi(page);
    await openConsole(page, true);

    await expect(page.locator(".console--wrap")).toHaveCount(1);

    // A window plus overscan; the exact number depends on viewport height and wrapped row heights,
    // so assert on the property that matters: it must not scale with the buffer.
    await expect.poll(() => page.locator(".log-line").count()).toBeGreaterThan(0);
    await expect.poll(() => page.locator(".log-line").count()).toBeLessThan(LINE_COUNT / 4);
  });

  test("keeps the scrollable height proportional to the whole buffer", async ({ page }) => {
    await stubApi(page);
    await openConsole(page, true);

    const scroll = page.locator(".console__scroll");
    const measure = () =>
      scroll.evaluate((el) => ({ scrollHeight: el.scrollHeight, clientHeight: el.clientHeight }));

    // Every line is at least one row tall, so the spacer must be able to scroll all of them even
    // though only a window is rendered.
    await expect.poll(async () => (await measure()).scrollHeight).toBeGreaterThanOrEqual(LINE_COUNT);
    const { scrollHeight, clientHeight } = await measure();
    expect(scrollHeight).toBeGreaterThan(clientHeight);
  });

  test("swaps in later lines as the window scrolls", async ({ page }) => {
    await stubApi(page);
    await openConsole(page, true);

    const firstNumberAtTop = await page.locator(".log-line__num").first().innerText();

    const scroll = page.locator(".console__scroll");
    await scroll.evaluate((el) => {
      el.scrollTop = el.scrollHeight;
      el.dispatchEvent(new Event("scroll"));
    });

    await expect
      .poll(async () => Number(await page.locator(".log-line__num").last().innerText()))
      .toBeGreaterThan(Number(firstNumberAtTop));

    // The last buffered line must be reachable, which only holds if the measured offsets line up
    // with the spacer height.
    await expect(page.locator(".log-line__num").last()).toHaveText(String(LINE_COUNT));

    // Still windowed after scrolling.
    await expect.poll(() => page.locator(".log-line").count()).toBeLessThan(LINE_COUNT / 4);
  });

  test("still windows the buffer when wrapping is off", async ({ page }) => {
    await stubApi(page);
    await openConsole(page, false);

    await expect(page.locator(".console--wrap")).toHaveCount(0);
    await expect.poll(() => page.locator(".log-line").count()).toBeLessThan(LINE_COUNT / 4);
  });
});
