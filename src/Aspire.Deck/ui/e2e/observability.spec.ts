import { expect, test, type Page, type TestInfo } from "@playwright/test";
import {
  getMissingObservabilityFeatures,
  type ObservabilityFeatureId,
} from "./observability-features";

const coveredFeatures = new Set<ObservabilityFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();

function features(...ids: ObservabilityFeatureId[]): string {
  for (const id of ids) {
    coveredFeatures.add(id);
  }

  return `[${ids.join(", ")}]`;
}

function navigationButton(page: Page, name: string) {
  return page.getByRole("navigation").getByRole("button", {
    name: new RegExp(`^${name}(?: \\d+)?$`),
  });
}

async function attachScreenshot(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const body = await page.screenshot({ animations: "disabled", fullPage: true });
  await testInfo.attach(`${name}.png`, { body, contentType: "image/png" });
}

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on("console", (message) => {
    if (message.type() === "error") {
      errors.push(`console: ${message.text()}`);
    }
  });
  page.on("pageerror", (error) => errors.push(`page: ${error.message}`));
  page.on("requestfailed", (request) => {
    errors.push(`request: ${request.method()} ${request.url()} (${request.failure()?.errorText ?? "unknown failure"})`);
  });

  await page.goto("/");
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Resources");
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
});

test(`${features("CONSOLE-RESOURCE-001", "CONSOLE-STREAM-001", "CONSOLE-SWITCH-001", "CONSOLE-FOLLOW-001")} streams and follows each resource console`, async ({ page }) => {
  await navigationButton(page, "Console").click();

  const resourceSelect = page.getByRole("combobox");
  await expect(resourceSelect.locator("option")).toHaveCount(8);
  await expect(resourceSelect).toHaveValue("frontend");

  const consolePanel = page.locator(".console");
  const lineText = consolePanel.locator(".log-line__text");
  await expect.poll(() => lineText.count()).toBeGreaterThanOrEqual(6);
  await expect(lineText.first()).toContainText("frontend:");
  await expect(consolePanel.locator(".console__footer")).toContainText("lines");

  await resourceSelect.selectOption("cache");
  await expect(resourceSelect).toHaveValue("cache");
  await expect(lineText.first()).toContainText("cache:");
  await expect(consolePanel).not.toContainText("frontend:");

  await page.setViewportSize({ width: 1000, height: 320 });
  const scrollport = consolePanel.locator(".console__scroll");
  await expect
    .poll(() => scrollport.evaluate((element) => element.scrollHeight - element.clientHeight), {
      timeout: 12_000,
    })
    .toBeGreaterThan(24);
  await scrollport.evaluate((element) => {
    element.scrollTop = 0;
    element.dispatchEvent(new Event("scroll", { bubbles: true }));
  });

  const scrollToBottom = page.getByRole("button", { name: "Scroll to bottom" });
  await expect(scrollToBottom).toBeVisible();
  await scrollToBottom.click();
  await expect(page.getByText("Live · following", { exact: true })).toBeVisible();
  await expect
    .poll(() =>
      scrollport.evaluate(
        (element) => element.scrollHeight - element.scrollTop - element.clientHeight,
      ),
    )
    .toBeLessThan(24);
});

test(`${features("LOG-LIST-001", "LOG-FILTER-001", "LOG-SEVERITY-001", "LOG-LIVE-001")} lists, filters, and updates structured logs`, async ({ page }) => {
  await navigationButton(page, "Structured Logs").click();

  const table = page.getByRole("table");
  await expect(table.getByRole("columnheader")).toHaveText([
    "Resource",
    "Level",
    "Timestamp",
    "Message",
    "Trace",
    "Actions",
  ]);
  const dataRows = table.locator("tbody tr");
  await expect.poll(() => dataRows.count()).toBeGreaterThanOrEqual(4);

  const firstRow = dataRows.first();
  await expect(firstRow.locator("td").nth(2)).toHaveText(/^\d{2}:\d{2}:\d{2}\.\d{3} (?:AM|PM)$/);
  const firstMessage = (await firstRow.locator("td").nth(3).innerText()).trim();
  const firstSeverity = (await firstRow.locator("td").nth(1).innerText()).trim();
  expect(firstMessage).not.toBe("");
  expect(firstSeverity).not.toBe("");

  const subtitle = page.getByRole("main").locator(".page__subtitle");
  const initialTotal = Number((await subtitle.innerText()).match(/^(\d+)/)?.[1]);
  expect(initialTotal).toBeGreaterThan(0);
  await expect
    .poll(async () => Number((await subtitle.innerText()).match(/^(\d+)/)?.[1]), {
      timeout: 5_000,
    })
    .toBeGreaterThan(initialTotal);

  const query = page.getByRole("textbox", { name: "Filter messages…" });
  await query.fill(firstMessage);
  await expect.poll(() => dataRows.count()).toBeGreaterThan(0);
  await expect
    .poll(async () => (await dataRows.allTextContents()).every((row) => row.includes(firstMessage)))
    .toBe(true);

  await query.clear();
  const severity = page.getByRole("combobox", { name: "Severity" });
  await expect(severity.locator("option")).toHaveText([
    "All",
    "Trace",
    "Debug",
    "Information",
    "Warning",
    "Error",
    "Critical",
  ]);
  await severity.selectOption("Warning");
  await expect.poll(() => dataRows.count()).toBeGreaterThan(0);
  await expect
    .poll(async () => (await table.locator("tbody .badge").allTextContents()).every((level) =>
      ["Warning", "Error", "Critical"].includes(level)),
    )
    .toBe(true);

  await query.fill("this-log-value-does-not-exist");
  await expect(table).toContainText("No logs match your filter.");
});

test(`${features("LOG-DETAILS-001", "LOG-ACTIONS-001")} opens complete structured log details and visualizers`, async ({ page }, testInfo) => {
  await navigationButton(page, "Structured Logs").click();

  const table = page.getByRole("table");
  const errorRow = table.locator("tbody tr:has(.badge.error)").first();
  await expect(errorRow).toBeVisible();

  const rowActions = errorRow.getByRole("button", { name: "Log actions" });
  await rowActions.click();
  const rowMenu = page.getByRole("menu", { name: "Log actions" });
  await expect(rowMenu.getByRole("menuitem")).toHaveText([
    "View details",
    "Open message in text visualizer",
    "View JSON",
  ]);
  await rowMenu.getByRole("menuitem", { name: "View details" }).click();

  const details = page.getByRole("dialog", { name: "Structured log entry details" });
  await expect(details).toContainText("Catalog.RequestFailed");
  await expect(details).toContainText("Aspire.Deck.MockTelemetry");
  await expect(details.getByRole("toolbar", { name: "Structured log detail tools" })).toContainText(/Resource .*Timestamp/);
  await expect(details.getByRole("group", { name: "Log entry properties" }).getByRole("term")).toContainText([
    "Level",
    "Message",
    "http.request.method",
  ]);
  await expect(details.getByRole("group", { name: "Context properties" })).toContainText("EventNameCatalog.RequestFailed");
  await expect(details.getByRole("group", { name: "Context properties" })).toContainText("TraceId");
  await expect(details.getByRole("group", { name: "Exception properties" })).toContainText(
    "exception.typeSystem.InvalidOperationException",
  );
  await expect(details.getByRole("group", { name: "Resource properties" })).toContainText(
    "deployment.environment.nameDevelopment",
  );

  const propertyFilter = details.getByRole("textbox", { name: "Filter properties…" });
  await propertyFilter.fill("exception.message");
  await expect(details.getByRole("group", { name: "Exception properties" }).getByRole("term")).toHaveText([
    "exception.message",
  ]);
  await expect(details.getByRole("button", { name: "Log entry 0" })).toBeVisible();
  await propertyFilter.clear();

  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await details.getByRole("button", { name: "Log actions" }).click();
  await page.getByRole("menu", { name: "Log actions" }).getByRole("menuitem", {
    name: "Open message in text visualizer",
  }).click();
  const messageViewer = page.getByRole("dialog", { name: "Structured log message" });
  await expect(messageViewer.locator("pre[data-format='text']")).not.toBeEmpty();
  await messageViewer.getByRole("button", { name: "Copy" }).click();
  await expect(messageViewer.getByRole("status")).toHaveText("Copied");
  expect(await page.evaluate(() => navigator.clipboard.readText())).not.toBe("");
  await messageViewer.getByRole("button", { name: "Close visualizer" }).click();

  await details.getByRole("button", { name: "Log actions" }).click();
  await page.getByRole("menu", { name: "Log actions" }).getByRole("menuitem", { name: "View JSON" }).click();
  const jsonViewer = page.getByRole("dialog", { name: "Catalog.RequestFailed.json" });
  await expect(jsonViewer.locator("pre[data-format='json']")).toContainText('"eventName": "Catalog.RequestFailed"');
  await expect(jsonViewer.locator("pre[data-format='json']")).toContainText('"exception.type": "System.InvalidOperationException"');
  await expect(jsonViewer.locator("pre[data-format='json']")).toContainText('"deployment.environment.name": "Development"');
  await attachScreenshot(page, testInfo, "structured-log-json-viewer");
  await jsonViewer.getByRole("button", { name: "Close visualizer" }).click();

  await attachScreenshot(page, testInfo, "structured-log-details");
  await expect(errorRow).toHaveAttribute("aria-selected", "true");
  await details.getByRole("button", { name: "Close details" }).click();
  await expect(details).toHaveCount(0);
});

test(`${features("LOG-RESOURCE-001", "LOG-PAUSE-001")} filters resources and pauses incoming structured logs`, async ({ page }) => {
  await navigationButton(page, "Structured Logs").click();

  const logs = page.getByRole("main").getByRole("region", { name: "Structured Logs" });
  const rows = logs.getByRole("table").locator("tbody tr");
  await expect.poll(() => rows.count()).toBeGreaterThanOrEqual(4);

  const resource = logs.getByRole("combobox", { name: "Resource" });
  await expect.poll(() => resource.locator("option").count()).toBeGreaterThan(1);
  const resourceName = await resource.locator("option").nth(1).getAttribute("value");
  expect(resourceName).not.toBeNull();
  await resource.selectOption(resourceName!);
  await expect.poll(() => rows.count()).toBeGreaterThan(0);
  await expect.poll(async () =>
    (await rows.locator("td:nth-child(1)").allTextContents()).every((value) => value.trim() === resourceName),
  ).toBe(true);
  await resource.selectOption("all");

  const subtitle = logs.locator(".page__subtitle");
  const readPageTotal = async (): Promise<number> => Number((await subtitle.innerText()).match(/^(\d+) total/)?.[1]);
  const readNavigationTotal = async (): Promise<number> =>
    Number((await navigationButton(page, "Structured Logs").innerText()).match(/(\d+)$/)?.[1]);

  const pause = logs.getByRole("switch", { name: "Pause incoming data" });
  await pause.check();
  const pausedTotal = await readPageTotal();
  await expect(subtitle).toContainText("paused");
  await expect.poll(readNavigationTotal, { timeout: 6_000 }).toBeGreaterThan(pausedTotal);
  expect(await readPageTotal()).toBe(pausedTotal);

  await pause.uncheck();
  await expect(subtitle).not.toContainText("paused");
  await expect.poll(readPageTotal).toBeGreaterThan(pausedTotal);
});

test(`${features("TRACE-LIST-001", "TRACE-COLLAPSE-001", "TRACE-DETAILS-001", "TRACE-ERROR-001")} explores trace waterfalls and span details`, async ({ page }) => {
  await navigationButton(page, "Traces").click();

  const traces = page.locator(".wf__trace");
  await expect.poll(() => traces.count()).toBeGreaterThanOrEqual(4);
  const initialTrace = traces.first();
  const initialRedisSpan = initialTrace.locator(".wf__span").nth(1);
  await initialRedisSpan.press("Enter");
  const keyboardDialog = page.getByRole("dialog", { name: "redis GET" });
  const traceId = (await keyboardDialog.locator(".kv__val.cell-mono").first().innerText()).trim();
  expect(traceId).toMatch(/^[0-9a-f]{32}$/);
  await expect(keyboardDialog).toContainText("Duration18msKindClientStatusOk");
  await expect(keyboardDialog).toContainText("Span ID");
  await expect(keyboardDialog).toContainText("Parent");
  await keyboardDialog.getByRole("button", { name: "Close" }).click();

  const query = page.getByRole("textbox", { name: "Filter traces…" });
  await query.fill(traceId.slice(0, 8));
  await expect(traces).toHaveCount(1);
  const firstTrace = traces.first();
  const traceHeader = firstTrace.locator(".wf__head");
  const traceSpans = firstTrace.locator(".wf__span");
  await expect(traceHeader).toHaveAttribute("aria-expanded", "true");
  await expect(traceSpans).toHaveCount(5);

  await traceHeader.click();
  await expect(traceHeader).toHaveAttribute("aria-expanded", "false");
  await expect(traceSpans).toHaveCount(0);
  await traceHeader.click();
  await expect(traceSpans).toHaveCount(5);

  const redisSpan = traceSpans.nth(1);
  await redisSpan.click();
  const pointerDialog = page.getByRole("dialog", { name: "redis GET" });
  await expect(pointerDialog).toBeVisible();
  await pointerDialog.getByRole("button", { name: "Close" }).click();

  await query.clear();
  const errorTraces = page.locator(".wf__trace--error");
  await expect.poll(() => errorTraces.count()).toBeGreaterThan(0);
  const errorSpan = errorTraces.first().locator(".wf__span").filter({ has: page.locator(".wf__error-dot") });
  await expect(errorSpan).toHaveCount(1);
  await errorSpan.click();
  await expect(page.getByRole("dialog")).toContainText("StatusError");
});

test(`${features("TRACE-FILTER-001", "TRACE-DURATION-001")} filters traces by content, identifier, and duration`, async ({ page }) => {
  await navigationButton(page, "Traces").click();

  const traces = page.locator(".wf__trace");
  await expect.poll(() => traces.count()).toBeGreaterThanOrEqual(4);
  const redisSpan = traces.first().locator(".wf__span").nth(1);
  await redisSpan.click();
  const dialog = page.getByRole("dialog", { name: "redis GET" });
  const traceId = (await dialog.locator(".kv__val.cell-mono").first().innerText()).trim();
  expect(traceId).toMatch(/^[0-9a-f]{32}$/);
  await dialog.getByRole("button", { name: "Close" }).click();

  const query = page.getByRole("textbox", { name: "Filter traces…" });
  await query.fill(traceId.slice(0, 8));
  await expect(traces).toHaveCount(1);

  await query.fill("redis GET");
  await expect.poll(() => traces.count()).toBeGreaterThan(0);
  await expect
    .poll(async () => (await traces.allTextContents()).every((trace) => trace.includes("redis GET")))
    .toBe(true);

  const minDuration = page.getByRole("combobox", { name: "Min duration" });
  await minDuration.selectOption("250");
  await expect(page.getByText("No traces match your filter.", { exact: true })).toBeVisible();

  await minDuration.selectOption("0");
  await query.fill("this-trace-value-does-not-exist");
  await expect(page.getByText("No traces match your filter.", { exact: true })).toBeVisible();
});

test(`${features("METRIC-LIST-001", "METRIC-SELECT-001", "METRIC-CHART-001", "METRIC-CURSOR-001", "METRIC-PAUSE-001", "METRIC-RANGE-001", "METRIC-ZOOM-001")} explores live metric series`, async ({ page }, testInfo) => {
  await navigationButton(page, "Metrics").click();

  const metricItems = page.locator(".metric-item");
  await expect(metricItems).toHaveCount(7);
  await expect(metricItems.locator(".metric-item__name")).toHaveText([
    "cache.hit_ratio",
    "db.client.connections.usage",
    "http.client.request.duration",
    "http.server.active_requests",
    "http.server.request.duration",
    "http.server.requests",
    "process.runtime.dotnet.gc.heap.size",
  ]);
  await expect(metricItems.first()).toHaveClass(/active/);

  const durationMetric = metricItems.filter({ hasText: "http.server.request.duration" });
  await durationMetric.click();
  await expect(durationMetric).toHaveClass(/active/);
  const detail = page.locator(".metric-detail");
  await expect(detail).toContainText("http.server.request.duration (milliseconds)");
  await expect(detail).toContainText("Histogram · percentiles · frontend");

  const chart = detail.locator(".metric-chart");
  await expect(chart).toBeVisible();
  await expect(chart.locator("canvas")).not.toHaveCount(0);
  await expect
    .poll(() =>
      chart.locator("canvas").evaluateAll((canvases) =>
        canvases.some((element) => {
          const canvas = element as HTMLCanvasElement;
          const context = canvas.getContext("2d");
          if (!context || canvas.width === 0 || canvas.height === 0) {
            return false;
          }
          const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
          for (let alpha = 3; alpha < pixels.length; alpha += 4) {
            if (pixels[alpha] !== 0) {
              return true;
            }
          }
          return false;
        }),
      ),
    )
    .toBe(true);

  const chartOverlay = chart.locator(".u-over");
  const chartBounds = await chartOverlay.boundingBox();
  expect(chartBounds).not.toBeNull();
  await page.mouse.move(
    chartBounds!.x + chartBounds!.width * 0.6,
    chartBounds!.y + chartBounds!.height * 0.5,
  );
  await expect
    .poll(async () => (await chart.locator(".u-value").allTextContents()).some((value) => value !== "—"))
    .toBe(true);

  const pause = page.getByTitle("Pause live updates");
  await pause.click();
  await expect(page.getByTitle("Resume live updates")).toBeVisible();
  await expect(detail).toContainText("paused");

  const timeRange = page.getByRole("group", { name: "Time range" });
  await timeRange.getByRole("button", { name: "1m", exact: true }).click();
  await expect(timeRange.getByRole("button", { name: "1m", exact: true })).toHaveAttribute("aria-pressed", "true");
  await expect(timeRange.getByRole("button", { name: "5m", exact: true })).toHaveAttribute("aria-pressed", "false");

  await page.getByTitle("Resume live updates").click();
  await expect(page.getByTitle("Pause live updates")).toBeVisible();
  const zoomBounds = await chartOverlay.boundingBox();
  expect(zoomBounds).not.toBeNull();
  await page.mouse.move(
    zoomBounds!.x + zoomBounds!.width * 0.2,
    zoomBounds!.y + zoomBounds!.height * 0.5,
  );
  await page.mouse.down();
  await page.mouse.move(
    zoomBounds!.x + zoomBounds!.width * 0.7,
    zoomBounds!.y + zoomBounds!.height * 0.5,
    { steps: 8 },
  );
  await page.mouse.up();
  await expect(page.getByTitle("Resume live updates")).toBeVisible();

  await attachScreenshot(page, testInfo, "dashboard-metrics");
});

test(`${features("OBS-RESPONSIVE-001")} keeps every observability surface contained on mobile`, async ({ page }, testInfo) => {
  await page.getByRole("button", { name: "Dismiss notification" }).click();
  await page.setViewportSize({ width: 390, height: 844 });

  const pages = [
    ["Console", "console"],
    ["Structured Logs", "structured-logs"],
    ["Traces", "traces"],
    ["Metrics", "metrics"],
    ["Canvases", "canvases"],
  ] as const;

  for (const [pageName, screenshotName] of pages) {
    await navigationButton(page, pageName).click();
    await expect(page.getByRole("main").locator(".page__title")).toHaveText(pageName);
    const geometry = await page.evaluate(() => {
      const main = document.querySelector("main");
      const pageElement = document.querySelector("main .page");
      if (!(main instanceof HTMLElement) || !(pageElement instanceof HTMLElement)) {
        throw new Error("The active dashboard page was not rendered.");
      }
      const mainBox = main.getBoundingClientRect();
      const pageBox = pageElement.getBoundingClientRect();
      return {
        documentWidth: document.documentElement.scrollWidth,
        viewportWidth: innerWidth,
        main: { x: mainBox.x, width: mainBox.width },
        page: { x: pageBox.x, width: pageBox.width },
      };
    });

    expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
    expect(geometry.main).toEqual({ x: 0, width: 390 });
    expect(geometry.page).toEqual({ x: 0, width: 390 });
    await attachScreenshot(page, testInfo, `dashboard-${screenshotName}-mobile`);
  }

  const metricsLayout = page.locator(".metrics-layout");
  await navigationButton(page, "Metrics").click();
  await expect(metricsLayout).toHaveCSS("grid-template-columns", "362px");
});

const missingFeatures = getMissingObservabilityFeatures(coveredFeatures);
if (missingFeatures.length > 0) {
  throw new Error(`Observability features without Playwright coverage: ${missingFeatures.join(", ")}`);
}
