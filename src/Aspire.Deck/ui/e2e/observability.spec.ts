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

test(`${features("CONSOLE-RESOURCE-001", "CONSOLE-ALL-001", "CONSOLE-STREAM-001", "CONSOLE-SWITCH-001", "CONSOLE-FOLLOW-001")} streams and follows all or one resource console`, async ({ page }) => {
  await navigationButton(page, "Console").click();

  const resourceSelect = page.getByRole("combobox");
  await expect(resourceSelect.locator("option")).toHaveCount(9);
  await expect(resourceSelect).toHaveValue("__all-resources__");

  const consolePanel = page.locator(".console");
  const lineText = consolePanel.locator(".log-line__text");
  await expect.poll(() => consolePanel.locator(".log-line__resource").count()).toBeGreaterThanOrEqual(12);
  await expect.poll(async () => new Set(await consolePanel.locator(".log-line__resource").allTextContents()).size).toBeGreaterThan(1);

  const combinedScrollport = consolePanel.locator(".console__scroll");
  await combinedScrollport.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    element.dispatchEvent(new Event("scroll", { bubbles: true }));
  });

  await resourceSelect.selectOption("frontend");
  await expect(resourceSelect).toHaveValue("frontend");
  await expect.poll(() => lineText.count()).toBeGreaterThanOrEqual(6);
  await expect.poll(async () => (await lineText.count()) - (await consolePanel.locator('.log-line[data-resource-name="frontend"]').count())).toBe(0);
  await expect(consolePanel.locator(".console__footer")).toContainText("lines");

  await resourceSelect.selectOption("cache");
  await expect(resourceSelect).toHaveValue("cache");
  await expect.poll(() => consolePanel.locator('.log-line[data-resource-name="cache"]').count()).toBeGreaterThanOrEqual(6);
  await expect(consolePanel.locator('.log-line[data-resource-name="frontend"]')).toHaveCount(0);

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

test(`${features("CONSOLE-PAUSE-001", "CONSOLE-CLEAR-001")} pauses, catches up, and clears console output`, async ({ page }) => {
  await navigationButton(page, "Console").click();
  await page.getByRole("combobox").selectOption("frontend");

  const consolePanel = page.locator(".console");
  const lineText = consolePanel.locator(".log-line__text");
  await expect.poll(() => lineText.count()).toBeGreaterThanOrEqual(6);

  const pause = page.getByRole("switch", { name: "Pause incoming data" });
  await pause.check();
  const pausedCount = await lineText.count();
  await expect(consolePanel.locator(".console__footer")).toContainText(/\d+ pending/, { timeout: 4_000 });
  expect(await lineText.count()).toBe(pausedCount);

  await pause.uncheck();
  await expect.poll(() => lineText.count()).toBeGreaterThan(pausedCount);

  await page.getByRole("button", { name: "Clear" }).click();
  await expect(consolePanel.locator(".console__footer")).toContainText("0 lines");
  await expect.poll(() => lineText.count(), { timeout: 4_000 }).toBeGreaterThan(0);
});

test(`${features("CONSOLE-COMMANDS-001")} executes selected resource commands with confirmation`, async ({ page }) => {
  await navigationButton(page, "Console").click();
  await page.getByRole("combobox").selectOption("frontend");

  await page.getByRole("button", { name: "Restart" }).click();
  const dialog = page.getByRole("dialog", { name: "Restart" });
  await expect(dialog).toContainText("Are you sure you want to restart this resource?");
  await dialog.getByRole("button", { name: "Restart" }).click();
  await expect(page.getByRole("status").filter({ hasText: "Restart succeeded" })).toBeVisible();

  await page.getByRole("button", { name: "Resource actions" }).click();
  await expect(page.getByRole("menuitem", { name: /Start/ })).toHaveAttribute("aria-disabled", "true");
  await expect(page.getByRole("menuitem", { name: /Stop/ })).toBeEnabled();
});

test(`${features("CONSOLE-ROUTE-001")} restores console resource and display options from the URL`, async ({ page }) => {
  await navigationButton(page, "Console").click();
  await page.getByRole("combobox", { name: "Resource" }).selectOption("cache");
  await expect(page).toHaveURL(/\/consolelogs\/resource\/cache$/);

  await page.getByRole("switch", { name: "Pause incoming data" }).check();
  await page.getByRole("button", { name: "Console settings" }).click();
  await page.getByRole("menuitem", { name: "Show timestamps" }).click();
  await page.getByRole("button", { name: "Console settings" }).click();
  await page.getByRole("menuitem", { name: "UTC timestamps" }).click();
  await page.getByRole("button", { name: "Console settings" }).click();
  await page.getByRole("menuitem", { name: "Wrap lines" }).click();

  const routeState = await page.evaluate(() => ({
    pathname: window.location.pathname,
    search: Object.fromEntries(new URLSearchParams(window.location.search)),
  }));
  expect(routeState).toEqual({
    pathname: "/consolelogs/resource/cache",
    search: { timestamps: "true", utc: "true", wrap: "true", paused: "true" },
  });

  await page.reload();
  await expect(page.getByRole("combobox", { name: "Resource" })).toHaveValue("cache");
  await expect(page.getByRole("switch", { name: "Pause incoming data" })).toBeChecked();
  await expect(page.locator(".console")).toHaveClass(/console--wrap/);
  await page.getByRole("button", { name: "Console settings" }).click();
  await expect(page.getByRole("menuitem", { name: "Hide timestamps" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "UTC timestamps" })).toBeEnabled();
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
  const selectedLogMessage = (await errorRow.locator("td").nth(3).innerText()).trim();

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
  const selectedRow = table.locator("tbody tr[aria-selected='true']");
  await expect(selectedRow).toHaveCount(1);
  await expect(selectedRow).toContainText(selectedLogMessage);
  await details.getByRole("button", { name: "Close details" }).click();
  await expect(details).toHaveCount(0);
});

test(`${features("LOG-TRACE-LINK-001", "TRACE-DETAIL-ROUTE-001")} opens and restores the related trace span`, async ({ page }) => {
  await navigationButton(page, "Structured Logs").click();

  const errorRow = page.getByRole("table").locator("tbody tr:has(.badge.error)").first();
  const traceLink = errorRow.getByRole("link", { name: /^Open trace / });
  const traceId = await traceLink.getAttribute("title");
  expect(traceId).toMatch(/^[0-9a-f]{32}$/);

  await traceLink.click();
  const detailUrl = new URL(page.url());
  expect(detailUrl.pathname).toBe(`/traces/detail/${traceId}`);
  const spanId = detailUrl.searchParams.get("span");
  expect(spanId).toMatch(/^[0-9a-f]{16}$/);

  const details = page.getByRole("dialog");
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Traces");
  await expect(page.locator(".wf__trace")).toHaveCount(1);
  await expect(details.getByRole("group", { name: "Span properties" })).toContainText(`SpanId${spanId}`);
  await expect(details.getByRole("group", { name: "Context properties" })
    .getByRole("link", { name: /^Open trace / })).toHaveAttribute("title", traceId!);

  await page.reload();
  const restoredDetails = page.getByRole("dialog");
  await expect(restoredDetails.getByRole("group", { name: "Span properties" })).toContainText(`SpanId${spanId}`);
  await expect(restoredDetails.getByRole("group", { name: "Context properties" })
    .getByRole("link", { name: /^Open trace / })).toHaveAttribute("title", traceId!);
  await expect(page).toHaveURL(detailUrl.toString());

  await page.goBack();
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Structured Logs");
  await expect(page).toHaveURL(/\/structuredlogs$/);
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

test(`${features("TRACE-LIST-001", "TRACE-LIVE-001", "TRACE-COLLAPSE-001", "TRACE-DETAILS-001", "TRACE-ERROR-001")} explores trace waterfalls and span details`, async ({ page }) => {
  await navigationButton(page, "Traces").click();

  const traces = page.locator(".wf__trace");
  await expect.poll(() => traces.count()).toBeGreaterThanOrEqual(4);
  const initialTraceCount = await traces.count();
  await expect.poll(() => traces.count()).toBeGreaterThan(initialTraceCount);
  const initialTrace = traces.first();
  const initialRedisSpan = initialTrace.locator(".wf__span").nth(1);
  await initialRedisSpan.press("Enter");
  const keyboardDialog = page.getByRole("dialog", { name: "redis GET" });
  const spanProperties = keyboardDialog.getByRole("group", { name: "Span properties" });
  const contextProperties = keyboardDialog.getByRole("group", { name: "Context properties" });
  const traceId = await contextProperties.getByRole("link", { name: /^Open trace / }).getAttribute("title");
  expect(traceId).not.toBeNull();
  expect(traceId).toMatch(/^[0-9a-f]{32}$/);
  await expect(spanProperties).toContainText("Duration18ms");
  await expect(spanProperties).toContainText("KindClient");
  await expect(spanProperties).toContainText("StatusOk");
  await expect(contextProperties).toContainText("SourceAspire.Deck.MockTelemetry");
  await expect(keyboardDialog.getByRole("group", { name: "Resource properties" })).toContainText("service.name");
  await expect(contextProperties.getByRole("link", { name: /^Open parent span / })).toBeVisible();
  await keyboardDialog.getByRole("button", { name: "Close" }).click();

  const query = page.getByRole("textbox", { name: "Filter traces…" });
  await query.fill(traceId!.slice(0, 8));
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

test(`${features("TRACE-EVENTS-001", "TRACE-LINKS-001", "TRACE-ACTIONS-001")} explores span events, links, backlinks, and actions`, async ({ page }, testInfo) => {
  await navigationButton(page, "Traces").click();

  const traces = page.locator(".wf__trace");
  await expect.poll(() => traces.count()).toBeGreaterThanOrEqual(4);

  const errorTraces = page.locator(".wf__trace--error");
  await expect.poll(() => errorTraces.count()).toBeGreaterThan(0);
  const errorSpan = errorTraces.first().locator(".wf__span").filter({ has: page.locator(".wf__error-dot") });
  await expect(errorSpan).toHaveCount(1);
  await errorSpan.click();

  const errorDetails = page.getByRole("dialog");
  const events = errorDetails.getByRole("group", { name: "Span events" });
  await expect(errorDetails.getByRole("button", { name: "Events 1" })).toBeVisible();
  await expect(events).toContainText("exception");
  await expect(events).toContainText("exception.typeSystem.InvalidOperationException");
  await expect(events).toContainText("exception.messageThe simulated dependency failed.");

  const propertyFilter = errorDetails.getByRole("textbox", { name: "Filter properties…" });
  await propertyFilter.fill("exception.message");
  await expect(errorDetails.getByRole("button", { name: "Events 1" })).toBeVisible();
  await expect(events).toContainText("exception.messageThe simulated dependency failed.");
  await expect(errorDetails.getByRole("button", { name: "Span 0" })).toBeVisible();
  await propertyFilter.clear();

  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await errorDetails.getByRole("button", { name: "Span actions" }).click();
  const errorActions = page.getByRole("menu", { name: "Span actions" });
  await expect(errorActions.getByRole("menuitem")).toHaveText([
    "View related structured logs",
    "View JSON",
  ]);
  await errorActions.getByRole("menuitem", { name: "View JSON" }).click();
  const jsonViewer = page.getByRole("dialog", { name: /\.json$/ });
  await expect(jsonViewer.locator("pre[data-format='json']")).toContainText('"name": "exception"');
  await expect(jsonViewer.locator("pre[data-format='json']")).toContainText('"status"');
  await jsonViewer.getByRole("button", { name: "Copy" }).click();
  await expect(jsonViewer.getByRole("status")).toHaveText("Copied");
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain('"events"');
  await jsonViewer.getByRole("button", { name: "Close visualizer" }).click();
  await errorDetails.getByRole("button", { name: "Close" }).click();

  const sourceRoot = traces.first().locator(".wf__span").first();
  await sourceRoot.click();
  const sourceDetails = page.getByRole("dialog");
  const links = sourceDetails.getByRole("group", { name: "Span links" });
  await expect(sourceDetails.getByRole("button", { name: "Links 1" })).toBeVisible();
  await expect(links).toContainText("link.reasonprevious request");
  const linkedSpan = links.getByRole("link", { name: /^Open linked span / });
  const linkedTraceId = await linkedSpan.getAttribute("title");
  const linkedSpanId = (await linkedSpan.innerText()).trim();
  await linkedSpan.click();
  await expect(page).toHaveURL(new RegExp(`/traces/detail/${linkedTraceId}\\?.*span=${linkedSpanId}`));

  const linkedDetails = page.getByRole("dialog");
  const backlinks = linkedDetails.getByRole("group", { name: "Span backlinks" });
  await expect(linkedDetails.getByRole("button", { name: "Backlinks 1" })).toBeVisible();
  await expect(backlinks).toContainText("link.reasonprevious request");
  await expect(backlinks.getByRole("link", { name: /^Open backlink span / })).toBeVisible();

  await linkedDetails.getByRole("button", { name: "Span actions" }).click();
  await page.getByRole("menu", { name: "Span actions" })
    .getByRole("menuitem", { name: "View related structured logs" }).click();
  await expect(page).toHaveURL(new RegExp(`/structuredlogs\\?.*span=${linkedSpanId}`));
  const logs = page.getByRole("main").getByRole("region", { name: "Structured Logs" });
  await expect(logs.getByRole("textbox", { name: "Filter messages…" })).toHaveValue(linkedSpanId);
  await expect(logs.getByRole("table").locator("tbody tr")).toHaveCount(1);

  await attachScreenshot(page, testInfo, "span-related-logs");
});

test(`${features("TRACE-FILTER-001", "TRACE-DURATION-001")} filters traces by content, identifier, and duration`, async ({ page }) => {
  await navigationButton(page, "Traces").click();

  const traces = page.locator(".wf__trace");
  await expect.poll(() => traces.count()).toBeGreaterThanOrEqual(4);
  const redisSpan = traces.first().locator(".wf__span").nth(1);
  await redisSpan.click();
  const dialog = page.getByRole("dialog", { name: "redis GET" });
  const traceId = await dialog.getByRole("group", { name: "Context properties" })
    .getByRole("link", { name: /^Open trace / }).getAttribute("title");
  expect(traceId).not.toBeNull();
  expect(traceId).toMatch(/^[0-9a-f]{32}$/);
  await dialog.getByRole("button", { name: "Close" }).click();

  const query = page.getByRole("textbox", { name: "Filter traces…" });
  await query.fill(traceId!.slice(0, 8));
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

test(`${features("TRACE-RESOURCE-001", "TRACE-TYPE-001", "TRACE-PAUSE-001", "TRACE-SESSION-001")} filters, pauses, and restores trace inventory state`, async ({ page }) => {
  await navigationButton(page, "Traces").click();

  const tracesPage = page.getByRole("main").getByRole("region", { name: "Traces" });
  const traces = tracesPage.locator(".wf__trace");
  await expect.poll(() => traces.count()).toBeGreaterThanOrEqual(4);

  const pause = tracesPage.getByRole("switch", { name: "Pause incoming data" });
  await pause.check();
  const pausedTraceCount = await traces.count();
  await expect(tracesPage.locator(".page__subtitle")).toContainText("paused");

  const readNavigationCount = async (): Promise<number> =>
    Number((await navigationButton(page, "Traces").innerText()).match(/(\d+)$/)?.[1]);
  await expect.poll(readNavigationCount, { timeout: 6_000 }).toBeGreaterThan(pausedTraceCount);
  expect(await traces.count()).toBe(pausedTraceCount);

  const resource = tracesPage.getByRole("combobox", { name: "Resource" });
  await expect(resource.locator("optgroup")).toHaveCount(4);
  await resource.selectOption("frontend");
  await expect.poll(() => traces.count()).toBeGreaterThan(0);
  expect(await traces.count()).toBeLessThan(pausedTraceCount);

  const type = tracesPage.getByRole("combobox", { name: "Span type" });
  await expect(type.locator("option")).toHaveText([
    "All span types",
    "HTTP",
    "Database",
    "Messaging",
    "RPC",
    "Generative AI",
    "Cloud",
    "Other",
  ]);
  await type.selectOption("genai");
  await expect(page.getByText("No traces match your filter.", { exact: true })).toBeVisible();
  await type.selectOption("database");
  await expect.poll(() => traces.count()).toBeGreaterThan(0);

  await tracesPage.getByRole("textbox", { name: "Filter traces…" }).fill("redis");
  await tracesPage.getByRole("combobox", { name: "Min duration" }).selectOption("5");
  const route = await page.evaluate(() => ({
    pathname: window.location.pathname,
    search: Object.fromEntries(new URLSearchParams(window.location.search)),
  }));
  expect(route).toEqual({
    pathname: "/traces",
    search: {
      resource: "frontend",
      type: "database",
      q: "redis",
      minDuration: "5",
      paused: "true",
    },
  });

  await page.reload();
  await expect(page.getByRole("combobox", { name: "Resource" })).toHaveValue("frontend");
  await expect(page.getByRole("combobox", { name: "Span type" })).toHaveValue("database");
  await expect(page.getByRole("textbox", { name: "Filter traces…" })).toHaveValue("redis");
  await expect(page.getByRole("combobox", { name: "Min duration" })).toHaveValue("5");
  await expect(page.getByRole("switch", { name: "Pause incoming data" })).toBeChecked();
  await expect(page.locator(".page__subtitle")).toContainText("paused");
});

test(`${features("TRACE-CLEAR-001")} clears selected and all trace telemetry`, async ({ page }) => {
  await navigationButton(page, "Traces").click();
  const tracesPage = page.getByRole("main").getByRole("region", { name: "Traces" });
  await expect.poll(() => tracesPage.locator(".wf__trace").count()).toBeGreaterThanOrEqual(4);

  await tracesPage.getByRole("switch", { name: "Pause incoming data" }).check();
  await tracesPage.getByRole("combobox", { name: "Resource" }).selectOption("frontend");
  await tracesPage.getByRole("button", { name: "Clear traces" }).click();
  await page.getByRole("menuitem", { name: "Clear frontend" }).click();
  await expect(page.getByRole("status")).toHaveText("Cleared traces for frontend.");
  await expect(tracesPage.getByRole("combobox", { name: "Resource" })).toHaveValue("all");

  await tracesPage.getByRole("button", { name: "Clear traces" }).click();
  await page.getByRole("menuitem", { name: "Clear all resources" }).click();
  await expect(page.getByRole("status")).toHaveText("Cleared all traces.");
  await expect(tracesPage).toContainText("No traces match your filter.");
});

test(`${features("METRIC-LIST-001", "METRIC-RESOURCE-001", "METRIC-SELECT-001", "METRIC-CHART-001", "METRIC-CURSOR-001", "METRIC-PAUSE-001", "METRIC-RANGE-001", "METRIC-TABLE-001", "METRIC-SESSION-001", "METRIC-ZOOM-001")} explores and restores live metric series`, async ({ page }, testInfo) => {
  await navigationButton(page, "Metrics").click();

  const metricsPage = page.getByRole("main").getByRole("region", { name: "Metrics" });
  const resource = metricsPage.getByRole("combobox", { name: "Resource" });
  await expect(resource.locator("option")).toHaveText(["Select a resource", "apiservice", "frontend", "cache", "postgres"]);
  await expect(resource).toHaveValue("apiservice");
  await resource.selectOption("frontend");

  const metricItems = page.locator(".metric-item");
  await expect(metricItems).toHaveCount(3);
  await expect(metricItems.locator(".metric-item__name")).toHaveText([
    "http.server.active_requests",
    "http.server.request.duration",
    "http.server.requests",
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

  const pause = metricsPage.getByRole("switch", { name: "Pause incoming data" });
  await pause.check();
  await expect(metricsPage.locator(".page__subtitle")).toContainText("paused");

  const timeRange = page.getByRole("group", { name: "Time range" });
  await expect(timeRange.getByRole("button")).toHaveText(["1m", "5m", "15m", "30m", "1h", "3h", "6h", "12h"]);
  await timeRange.getByRole("button", { name: "1m", exact: true }).click();
  await expect(timeRange.getByRole("button", { name: "1m", exact: true })).toHaveAttribute("aria-pressed", "true");
  await expect(timeRange.getByRole("button", { name: "5m", exact: true })).toHaveAttribute("aria-pressed", "false");

  await metricsPage.getByRole("tab", { name: "Table" }).click();
  const metricTable = metricsPage.locator(".metric-series-table");
  await expect(metricTable.getByRole("row")).not.toHaveCount(1);
  await expect(metricTable.getByRole("columnheader")).toHaveText(["Time", "p50", "p90", "p99"]);

  await timeRange.getByRole("button", { name: "12h", exact: true }).click();
  const route = await page.evaluate(() => ({
    pathname: window.location.pathname,
    search: Object.fromEntries(new URLSearchParams(window.location.search)),
  }));
  expect(route).toEqual({
    pathname: "/metrics/resource/frontend",
    search: {
      metric: "http.server.request.duration",
      range: "43200",
      view: "table",
      paused: "true",
    },
  });

  await page.reload();
  await expect(page.getByRole("combobox", { name: "Resource" })).toHaveValue("frontend");
  await expect(page.getByRole("tab", { name: "Table" })).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("button", { name: "12h", exact: true })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("switch", { name: "Pause incoming data" })).toBeChecked();
  await expect(page.locator(".metric-item.active")).toContainText("http.server.request.duration");

  await page.getByRole("tab", { name: "Chart" }).click();
  await page.getByRole("switch", { name: "Pause incoming data" }).uncheck();
  await expect(page.getByRole("switch", { name: "Pause incoming data" })).not.toBeChecked();
  const restoredChartOverlay = page.locator(".metric-chart .u-over");
  await expect(restoredChartOverlay).toBeVisible();
  const zoomBounds = await restoredChartOverlay.boundingBox();
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
  await expect(page.getByRole("switch", { name: "Pause incoming data" })).toBeChecked();

  await attachScreenshot(page, testInfo, "dashboard-metrics");
});

test(`${features("METRIC-CLEAR-001")} clears selected and all metric telemetry`, async ({ page }) => {
  await navigationButton(page, "Metrics").click();
  const metricsPage = page.getByRole("main").getByRole("region", { name: "Metrics" });
  await metricsPage.getByRole("combobox", { name: "Resource" }).selectOption("frontend");
  await expect(metricsPage.locator(".metric-item")).toHaveCount(3);

  await metricsPage.getByRole("button", { name: "Clear metrics" }).click();
  await page.getByRole("menuitem", { name: "Clear frontend" }).click();
  await expect(page.getByRole("status")).toHaveText("Cleared metrics for frontend.");
  await expect(metricsPage.getByRole("combobox", { name: "Resource" })).toHaveValue("apiservice");
  await expect(metricsPage.locator(".metric-item")).toHaveCount(2);

  await metricsPage.getByRole("button", { name: "Clear metrics" }).click();
  await page.getByRole("menuitem", { name: "Clear all resources" }).click();
  await expect(page.getByRole("status")).toHaveText("Cleared all metrics.");
  await expect(metricsPage.locator(".page__subtitle")).toHaveText("Select a resource");
  await expect(metricsPage).toContainText("No metrics for this resource");
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
