import { expect, test, type Page, type TestInfo } from "@playwright/test";
import { getMissingStressFeatures, type StressFeatureId } from "./stress-features";

const coveredFeatures = new Set<StressFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();
const allowConsoleStreamAbort = new WeakSet<Page>();
const allowNavigationAbort = new WeakSet<Page>();

function features(...ids: StressFeatureId[]): string {
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

  await page.goto("/?backend=http");
  await expect(page.getByTitle("Resources: Connected")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Resources");
});

test.afterEach(async ({ page }) => {
  const errors = browserErrors.get(page) ?? [];
  let unexpected = allowConsoleStreamAbort.has(page)
    ? errors.filter((error) => !/^request: GET .*\/api\/deck\/resources\/[^/]+\/console-logs \(net::ERR_ABORTED\)$/.test(error))
    : errors;
  if (allowNavigationAbort.has(page)) {
    unexpected = unexpected.filter((error) =>
      !/^request: GET .*\/api\/deck\/(?:telemetry\/logs\?follow=true|interactions|resources) \(net::ERR_ABORTED\)$/.test(error));
  }
  expect(unexpected, "Unexpected browser errors").toEqual([]);
});

test(`${features("STRESS-CONFIG-001", "STRESS-RESOURCES-001", "STRESS-VISIBILITY-001", "STRESS-VISUAL-001")} renders the live Stress inventory`, async ({ page }, testInfo) => {
  await expect(page.getByRole("banner").locator(".topbar__app")).toHaveText("Stress");
  await expect(page.getByRole("navigation")).toContainText(/Aspire Deck 13\.5\.0/);

  const table = page.getByRole("table");
  await expect(table.getByRole("row")).toHaveCount(32);
  await expect(table).toContainText("stress-apiservice");
  await expect(table).toContainText("property-stress-resource");
  await expect(table).toContainText("interaction-commands");
  await expect(table).not.toContainText("hiddenContainer");
  await expect(table).not.toContainText("testParameterResource");
  await expect(table).not.toContainText("frontend");

  await attachScreenshot(page, testInfo, "stress-live-resources-desktop");
});

test(`${features("STRESS-DETAILS-001", "STRESS-SECRETS-001")} inspects live Stress resource details`, async ({ page }, testInfo) => {
  const row = page.getByRole("row").filter({ hasText: "property-stress-resource" });
  await expect(row).toHaveCount(1);
  await row.click();
  const dialog = page.getByRole("dialog", { name: "property-stress-resource" });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByText("Overview", { exact: true })).toBeVisible();
  await expect(dialog.getByText("Properties", { exact: true })).toBeVisible();
  await expect(dialog).toContainText("Executable");
  await expect(dialog).toContainText("UID");
  await expect(dialog).toContainText("/stress/known/path");
  await expect(dialog).toContainText("Visible highlighted value");
  await expect(dialog).not.toContainText("stress-secret-value");
  await expect(dialog).not.toContainText("Visible highlighted sensitive value");
  await expect(dialog).not.toContainText("Hidden sensitive value until Show all is selected");
  await expect(dialog.getByRole("button", { name: "Reveal value" })).toHaveCount(3);

  await attachScreenshot(page, testInfo, "stress-live-resource-details");
});

test(`${features("STRESS-RESOURCE-ICON-001", "STRESS-COMMAND-ICON-001", "STRESS-COMMAND-EXECUTE-001")} renders live Stress icon contracts and executes a command`, async ({ page }, testInfo) => {
  allowNavigationAbort.add(page);
  const response = await page.request.get("/api/deck/resources");
  expect(response.ok()).toBe(true);
  const resources = await response.json() as Array<{
    iconName: string | null;
    commands: Array<{ iconName: string | null }>;
  }>;
  const iconNames = [...new Set(resources.flatMap((resource) => [
    resource.iconName,
    ...resource.commands.map((command) => command.iconName),
  ]).filter((name): name is string => name !== null))].sort();

  const table = page.getByRole("table");
  const api = table.getByRole("row").filter({ hasText: "stress-apiservice" });
  const document = table.getByRole("row").filter({ hasText: "empty-0000" });
  await expect(api).toHaveCount(1);
  await expect(document).toHaveCount(1);
  await expect(api.locator('svg[data-icon-name="Server"][data-icon-variant="filled"]')).toHaveCount(1);
  await expect(document.locator('svg[data-icon-name="Document"][data-icon-variant="filled"]')).toHaveCount(1);

  const iconCommands = table.getByRole("row").filter({ hasText: "icon-commands" });
  await expect(iconCommands).toHaveCount(1);
  await iconCommands.click();
  let details = page.getByRole("dialog", { name: "icon-commands" });
  const highlightedIconCommand = details.getByRole("button", { name: "Icon test highlighted", exact: true });
  await expect(
    highlightedIconCommand.locator('svg[data-icon-name="CloudDatabase"][data-icon-variant="regular"]'),
  ).toHaveCount(1);
  await highlightedIconCommand.click();
  await expect(page.getByRole("status")).toHaveText("Icon test highlighted succeeded");
  await details.getByRole("button", { name: "Close details" }).click();

  const lifecycleCommands = table.getByRole("row").filter({ hasText: "lifecycle-commands" });
  await expect(lifecycleCommands).toHaveCount(1);
  await lifecycleCommands.click();
  details = page.getByRole("dialog", { name: "lifecycle-commands" });
  await details.getByRole("button", { name: "Resource commands" }).click();
  const menu = page.getByRole("menu", { name: "Resource commands" });
  await expect(
    menu.getByRole("menuitem", { name: /Stop all resources/ })
      .locator('svg[data-icon-name="Stop"][data-icon-variant="filled"]'),
  ).toHaveCount(1);
  await expect(
    menu.getByRole("menuitem", { name: /Start all resources/ })
      .locator('svg[data-icon-name="Play"][data-icon-variant="filled"]'),
  ).toHaveCount(1);

  await attachScreenshot(page, testInfo, "stress-live-icons");

  await page.goto(`/?view=toolkit&icons=${encodeURIComponent(iconNames.join(","))}`);
  const catalog = page.getByTestId("toolkit-icon-catalog");
  await expect(catalog.locator("svg[data-icon-name]")).toHaveCount(iconNames.length * 2);
  await attachScreenshot(page, testInfo, "stress-live-icon-catalog");
});

test(`${features("STRESS-COMMAND-ARGUMENTS-001")} submits every input type to a live Stress command`, async ({ page }, testInfo) => {
  const row = page.getByRole("table").getByRole("row").filter({ hasText: "argument-commands" });
  await expect(row).toHaveCount(1);
  await row.click();
  const details = page.getByRole("dialog", { name: "argument-commands" });
  await details.getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menuitem", { name: /Echo arguments/ }).click();

  const dialog = page.getByRole("dialog", { name: "Echo arguments" });
  const message = dialog.getByRole("textbox", { name: "Message" });
  const repeat = dialog.getByRole("spinbutton", { name: "Repeat" });
  const shout = dialog.getByRole("checkbox", { name: "Shout" });
  const flavor = dialog.getByRole("combobox", { name: "Flavor" });
  const secret = dialog.getByLabel("Secret");
  await expect(message).toHaveAttribute("placeholder", "Hello from the Stress playground");
  await expect(message).toHaveAttribute("maxlength", "80");
  await expect(repeat).toHaveValue("1");
  await expect(shout).not.toBeChecked();
  await expect(flavor).toHaveValue("vanilla");
  await expect(secret).toHaveAttribute("type", "password");

  await message.fill("Stress React interaction");
  await repeat.fill("2");
  await shout.check();
  await flavor.selectOption("chocolate");
  await secret.fill("dashboard-secret");
  await attachScreenshot(page, testInfo, "stress-live-command-inputs");
  await dialog.getByRole("button", { name: "Echo arguments", exact: true }).click();

  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole("status")).toHaveText("Echo arguments succeeded");
});

test(`${features("STRESS-PARAMETERS-001")} renders live parameters with secure defaults`, async ({ page }, testInfo) => {
  await navigationButton(page, "Parameters").click();
  const table = page.getByRole("table");
  await expect(table.getByRole("row")).toHaveCount(4);
  await expect(table).toContainText("api-key");
  await expect(table).toContainText("db-connection-string");
  await expect(table).toContainText("testParameterResource");
  await expect(table.getByText("Not set", { exact: true })).toHaveCount(2);
  await expect(table.getByRole("button", { name: "Reveal value" })).toHaveCount(1);

  await attachScreenshot(page, testInfo, "stress-live-parameters");
});

test(`${features("STRESS-CONSOLE-001")} renders a live resource console backlog`, async ({ page }, testInfo) => {
  allowConsoleStreamAbort.add(page);
  await navigationButton(page, "Console").click();
  const consoleRegion = page.getByRole("main").getByRole("region", { name: "Console" });
  await consoleRegion.getByRole("combobox", { name: "Resource" }).selectOption({ label: "empty-0000" });

  await expect(consoleRegion.getByText(/Application started\. Press Ctrl\+C to shut down\./)).toBeVisible();
  await expect(consoleRegion.getByText(/Hosting environment: Production/)).toBeVisible();
  await expect(consoleRegion.locator(".log-line")).toHaveCount(7);
  await expect(consoleRegion.locator(".console__footer")).toContainText("7 lines");

  const geometry = await page.evaluate(() => {
    const scroller = document.querySelector<HTMLElement>(".console__scroll");
    if (scroller === null) {
      throw new Error("Missing console scroller.");
    }

    return {
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: window.innerWidth,
      scrollerWidth: scroller.clientWidth,
      scrollerContentWidth: scroller.scrollWidth,
    };
  });
  expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
  expect(geometry.scrollerContentWidth).toBeGreaterThan(geometry.scrollerWidth);

  await attachScreenshot(page, testInfo, "stress-live-console");
});

test(`${features("STRESS-STRUCTURED-LOGS-001")} replays and streams live structured logs`, async ({ page }, testInfo) => {
  await navigationButton(page, "Structured Logs").click();
  const logs = page.getByRole("main").getByRole("region", { name: "Structured Logs" });
  const table = logs.getByRole("table");
  const rows = table.locator("tbody tr");
  const subtitle = logs.locator(".page__subtitle");

  await expect(table.getByRole("columnheader")).toHaveText(["Time", "Severity", "Resource", "Message"]);
  await expect.poll(() => rows.count(), { timeout: 30_000 }).toBeGreaterThan(10);
  await expect(table).toContainText("stress-telemetryservice");
  await expect(table).toContainText("Application started. Press Ctrl+C to shut down.");
  await expect(table.locator(".badge").filter({ hasText: "Information" }).first()).toBeVisible();

  const readTotal = async (): Promise<number> => {
    const text = await subtitle.innerText();
    const total = Number(text.match(/^(\d+) total/)?.[1]);
    if (!Number.isFinite(total)) {
      throw new Error(`Unable to read structured-log total from '${text}'.`);
    }
    return total;
  };
  const initialTotal = await readTotal();

  const resourcesResponse = await page.request.get("/api/deck/resources");
  expect(resourcesResponse.ok()).toBe(true);
  const resources = await resourcesResponse.json() as Array<{
    name: string;
    displayName: string;
    commands: Array<{ name: string; state: string }>;
  }>;
  const telemetryService = resources.find((resource) => resource.displayName === "stress-telemetryservice");
  expect(telemetryService).toBeDefined();
  expect(telemetryService!.commands).toContainEqual(expect.objectContaining({ name: "start", state: "enabled" }));

  const startResponse = await page.request.post("/api/deck/commands/execute", {
    data: { resourceName: telemetryService!.name, commandName: "start" },
  });
  expect(startResponse.ok()).toBe(true);
  await expect(startResponse.json()).resolves.toMatchObject({ kind: "succeeded" });
  await expect.poll(readTotal, { timeout: 45_000 }).toBeGreaterThan(initialTotal);

  const filter = logs.getByRole("textbox", { name: "Filter messages…" });
  await filter.fill("stress-telemetryservice");
  await expect.poll(() => rows.count()).toBeGreaterThan(0);
  await expect.poll(async () => (await rows.allTextContents()).every((row) => row.includes("stress-telemetryservice"))).toBe(true);

  await page.setViewportSize({ width: 1280, height: 900 });
  const geometry = await page.evaluate(() => {
    const tableWrap = document.querySelector<HTMLElement>(".table-wrap");
    const time = document.querySelector<HTMLElement>("tbody tr td:first-child .cell-time");
    if (tableWrap === null || time === null) {
      throw new Error("The structured-log table is missing its expected layout elements.");
    }
    return {
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: window.innerWidth,
      tableWidth: tableWrap.clientWidth,
      tableContentWidth: tableWrap.scrollWidth,
      timeWhiteSpace: getComputedStyle(time).whiteSpace,
    };
  });
  expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
  expect(geometry.tableContentWidth).toBeLessThanOrEqual(geometry.tableWidth);
  expect(geometry.timeWhiteSpace).toBe("nowrap");

  await attachScreenshot(page, testInfo, "stress-live-structured-logs");
});

test(`${features("STRESS-NAVIGATION-001", "STRESS-EMPTY-METRICS-001")} reaches every page against the live dashboard`, async ({ page }) => {
  const pages = [
    "Parameters",
    "Console",
    "Structured Logs",
    "Traces",
    "Metrics",
    "Canvases",
    "Resources",
  ] as const;

  for (const name of pages) {
    await navigationButton(page, name).click();
    await expect(page.getByRole("main").locator(".page__title")).toHaveText(name);
    await expect(navigationButton(page, name)).toHaveAttribute("aria-current", "page");

    if (name === "Metrics") {
      const metrics = page.getByRole("main").getByRole("region", { name: "Metrics" });
      await expect(metrics.locator(".page__subtitle")).toHaveText("0 instruments");
      await expect(metrics).not.toContainText("Loading…");
    }
  }
});

test(`${features("STRESS-RESPONSIVE-001")} keeps the live resource workflow usable on mobile`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 });

  const geometry = await page.evaluate(() => {
    const bounds = (selector: string): DOMRect => {
      const element = document.querySelector(selector);
      if (!element) {
        throw new Error(`Missing element '${selector}'.`);
      }
      return element.getBoundingClientRect();
    };
    const navigation = bounds(".sidebar");
    const banner = bounds(".topbar");
    const main = bounds(".app__content");
    return {
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: innerWidth,
      viewportHeight: innerHeight,
      navigation: { y: navigation.y, width: navigation.width },
      banner: { x: banner.x, width: banner.width },
      main: { x: main.x, width: main.width },
    };
  });

  expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
  expect(geometry.navigation.y).toBeGreaterThanOrEqual(geometry.viewportHeight - 72);
  expect(geometry.navigation.width).toBe(390);
  expect(geometry.banner).toEqual({ x: 0, width: 390 });
  expect(geometry.main).toEqual({ x: 0, width: 390 });

  const row = page.getByRole("row").filter({ hasText: "property-stress-resource" });
  await expect(row).toHaveCount(1);
  await row.click();
  const drawer = page.getByRole("dialog", { name: "property-stress-resource" });
  await expect.poll(async () => (await drawer.boundingBox())?.x).toBe(0);
  const drawerBounds = await drawer.boundingBox();
  expect(drawerBounds).not.toBeNull();
  expect(drawerBounds!.x).toBe(0);
  expect(drawerBounds!.width).toBe(390);

  await attachScreenshot(page, testInfo, "stress-live-resources-mobile");
});

const missingFeatures = getMissingStressFeatures(coveredFeatures);
if (missingFeatures.length > 0) {
  throw new Error(`Stress features without Playwright coverage: ${missingFeatures.join(", ")}`);
}
