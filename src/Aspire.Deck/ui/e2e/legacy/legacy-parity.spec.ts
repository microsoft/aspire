import { expect, test, type Page, type TestInfo } from "@playwright/test";
import {
  getLegacyScenarioFeatures,
  type DashboardParityFeature,
  type LegacyScenario,
} from "../parity/dashboard-parity-features";

const registeredFeatures = new Set<string>();
const browserErrors = new WeakMap<Page, string[]>();
const legacyDashboardUrl = new URL(process.env.ASPIRE_LEGACY_DASHBOARD_URL!);
const loginPath = legacyDashboardUrl.pathname === "/login" && legacyDashboardUrl.searchParams.has("t")
  ? `${legacyDashboardUrl.pathname}${legacyDashboardUrl.search}`
  : "/";

function features(scenario: LegacyScenario): string {
  const scenarioFeatures = getLegacyScenarioFeatures(scenario);
  for (const feature of scenarioFeatures) {
    registeredFeatures.add(feature.id);
  }

  return `[${scenarioFeatures.map((feature) => feature.id).join(", ")}]`;
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

  await page.goto(loginPath, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("navigation")).toBeVisible();
  await expect(page.getByRole("main")).toBeVisible();
  await dismissBlockingInteraction(page);
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
});

test(`${features("shell")} inventories the legacy shell`, async ({ page }, testInfo) => {
  const navigation = page.getByRole("navigation");
  for (const name of ["Resources", "Parameters", "Graph", "Console", "Structured", "Traces", "Metrics"]) {
    await expect(navigation.getByRole("link", { name, exact: true })).toBeVisible();
  }

  const banner = page.getByRole("banner");
  await expect(banner.getByRole("link", { name: "Stress", exact: true })).toBeVisible();
  await expect(banner.getByRole("link", { name: "Aspire repo", exact: true })).toBeVisible();
  await expect(banner.getByRole("button", { name: "AI agents", exact: true })).toBeVisible();
  await expect(banner.getByRole("button", { name: "Notifications", exact: true })).toBeVisible();

  await banner.getByRole("button", { name: "Help", exact: true }).click();
  const help = page.getByRole("dialog", { name: "Help", exact: true });
  await expect(help.getByRole("link", { name: "Go to aspire.dev documentation", exact: true })).toBeVisible();
  await expect(help.getByRole("heading", { name: "Keyboard Shortcuts", exact: true })).toBeVisible();
  await help.getByRole("button", { name: "Close", exact: true }).click({ force: true });

  await banner.getByRole("button", { name: "Settings", exact: true }).click();
  const settings = page.getByRole("dialog", { name: "Settings", exact: true });
  await expect(settings.getByRole("radio", { name: "System", exact: true })).toHaveCount(2);
  await expect(settings.getByRole("radio", { name: "Light", exact: true })).toBeVisible();
  await expect(settings.getByRole("radio", { name: "Dark", exact: true })).toBeVisible();
  await expect(settings.getByRole("combobox")).toHaveValue("en");
  await expect(settings.getByRole("radio", { name: "12-hour", exact: true })).toBeVisible();
  await expect(settings.getByRole("radio", { name: "24-hour", exact: true })).toBeVisible();
  await expect(settings.getByRole("button", { name: "Manage", exact: true })).toBeVisible();
  await settings.getByRole("button", { name: "Close", exact: true }).click({ force: true });

  const notifications = page.getByRole("region", { name: "Notifications", exact: true });
  await expect(notifications.getByText("Unresolved parameters", { exact: true })).toBeVisible();
  await expect(notifications.getByRole("button", { name: "Enter values", exact: true })).toBeVisible();
  await expect(notifications.getByText("Update now", { exact: true })).toBeVisible();
  await expect(notifications.getByRole("link", { name: "Upgrade instructions", exact: true })).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-shell");

  for (const [key, path] of [["c", "/consolelogs"], ["s", "/structuredlogs"], ["t", "/traces"], ["m", "/metrics"], ["r", "/"]] as const) {
    await page.keyboard.press(key);
    await expect(page).toHaveURL(new RegExp(`${path.replace("/", "\\/")}(?:\\?.*)?$`));
  }

  await page.goBack();
  await expect(page).toHaveURL(/\/metrics(?:\?.*)?$/);
  await page.goForward();
  await expect(page).toHaveURL(/\/(?:\?.*)?$/);

  await page.keyboard.press("?");
  const shortcutHelp = page.getByRole("dialog", { name: "Help", exact: true });
  await expect(shortcutHelp).toBeVisible();
  await shortcutHelp.getByRole("button", { name: "Close", exact: true }).click({ force: true });

  await page.keyboard.press("Shift+S");
  const shortcutSettings = page.getByRole("dialog", { name: "Settings", exact: true });
  await expect(shortcutSettings).toBeVisible();
  await shortcutSettings.getByRole("button", { name: "Close", exact: true }).click({ force: true });

  await page.goto("/error/404");
  await expect(page.getByText("404", { exact: true })).toBeVisible();
  await expect(page.getByText("The page you requested could not be found", { exact: true })).toBeVisible();
});

test(`${features("resources")} inventories resources, details, and graph behavior`, async ({ page }, testInfo) => {
  const table = page.getByRole("table");
  for (const header of ["Name", "State", "Source", "URLs", "Start time"]) {
    await expect(table.getByRole("columnheader", { name: header, exact: true })).toBeVisible();
  }
  await expect(page.getByRole("button", { name: "No filters", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "View options", exact: true })).toBeVisible();
  await expect(table.getByRole("button", { name: "Toggle nesting", exact: true }).first()).toBeVisible();
  await expect(table.getByText("Aspire.Dashboard.csproj", { exact: true })).toBeVisible();
  await expect(table.getByRole("link", { name: "Dashboard (https)", exact: true })).toBeVisible();
  await expect(table.getByText("no-status-resource", { exact: true })).toBeVisible();

  const customIconRow = table.getByRole("row").filter({ hasText: "stress-apiservice" });
  const defaultIconRow = table.getByRole("row").filter({ hasText: "empty-0000" });
  await expect(customIconRow).toHaveCount(1);
  await expect(defaultIconRow).toHaveCount(1);
  const customIcon = customIconRow.locator("svg.resource-icon");
  const defaultIcon = defaultIconRow.locator("svg.resource-icon");
  await expect(customIcon).toHaveCount(1);
  await expect(defaultIcon).toHaveCount(1);
  expect(await customIcon.evaluate((element) => element.innerHTML)).not.toBe(
    await defaultIcon.evaluate((element) => element.innerHTML),
  );

  const filter = page.getByRole("textbox", { name: "Filter...", exact: true });
  await filter.fill("property-stress-resource");
  await expect(table.getByText("property-stress-resource", { exact: true })).toBeVisible();
  await expect(table.getByText("manual-container-args", { exact: true })).toHaveCount(0);
  await filter.fill("");

  const resourceRow = table.getByRole("row").filter({ hasText: "property-stress-resource" });
  await expect(resourceRow).toHaveCount(1);
  await resourceRow.getByText("property-stress-resource", { exact: true }).click();
  const details = page.getByRole("dialog").filter({ hasText: "property-stress-resource" });
  await expect(details.getByText("Overview", { exact: true })).toBeVisible();
  await expect(details.getByText("Properties", { exact: true })).toBeVisible();
  await expect(details.getByRole("button", { name: "Show value", exact: true })).toHaveCount(3);
  await attachScreenshot(page, testInfo, "legacy-resource-details");

  await page.goto("/?view=Graph");
  await expect(page.getByRole("button", { name: "Zoom in", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Zoom out", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Reset", exact: true })).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-resource-graph");
});

test(`${features("parameters")} inventories parameter states and secure reveal`, async ({ page }, testInfo) => {
  await page.goto("/parameters");
  const table = page.getByRole("table");
  await expect(table.getByText("api-key", { exact: true })).toBeVisible();
  await expect(table.getByText("db-connection-string", { exact: true })).toBeVisible();
  await expect(table.getByText("testParameterResource", { exact: true })).toBeVisible();
  await expect(table.getByText("Value not set", { exact: true })).toHaveCount(2);

  const showValue = table.getByRole("button", { name: "Show value", exact: true });
  await expect(showValue).toHaveCount(1);
  // The notification stack can overlap the value button at this viewport. Keyboard activation
  // exercises the same accessible control without dismissing server-scoped notifications.
  await showValue.press("Enter");
  await expect(table.getByText("value", { exact: true })).toBeVisible();

  const filter = page.getByRole("textbox", { name: "Filter...", exact: true });
  await filter.fill("api-key");
  await expect(table.getByText("api-key", { exact: true })).toBeVisible();
  await expect(table.getByText("db-connection-string", { exact: true })).toHaveCount(0);
  await attachScreenshot(page, testInfo, "legacy-parameters");
});

test(`${features("commands")} inventories command icons and argument input types`, async ({ page }, testInfo) => {
  const table = page.getByRole("table");
  const iconCommands = table.getByRole("row").filter({ hasText: "icon-commands" });
  await expect(iconCommands).toHaveCount(1);
  await iconCommands.click({ force: true });
  let details = page.getByRole("dialog").filter({ hasText: "icon-commands" });
  const iconCommand = details.getByRole("button", { name: "Icon test", exact: true });
  const highlightedIconCommand = details.getByRole("button", { name: "Icon test highlighted", exact: true });
  await expect(iconCommand).toBeVisible();
  await expect(highlightedIconCommand).toBeVisible();
  await highlightedIconCommand.click();
  await expect(page.getByText('"Icon test highlighted" succeeded', { exact: false })).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-icon-commands");
  await details.getByRole("button", { name: "Close", exact: true }).click({ force: true });

  const argumentCommands = table.getByRole("row").filter({ hasText: "argument-commands" });
  await expect(argumentCommands).toHaveCount(1);
  await argumentCommands.click({ force: true });
  details = page.getByRole("dialog").filter({ hasText: "argument-commands" });
  const echoArguments = details.getByRole("button", { name: "Echo arguments", exact: true });
  await echoArguments.click();

  const inputs = page.getByRole("dialog", { name: "Echo arguments", exact: true });
  await expect(inputs.getByRole("textbox", { name: "Hello from the Stress playground", exact: true })).toBeVisible();
  await expect(inputs.getByRole("spinbutton")).toHaveValue("1");
  await expect(inputs.getByRole("checkbox", { name: "Shout", exact: true })).not.toBeChecked();
  await expect(inputs.getByRole("combobox")).toHaveValue("vanilla");
  await expect(inputs.getByRole("textbox", { name: "Optional secret", exact: true })).toHaveAttribute("type", "password");
  await attachScreenshot(page, testInfo, "legacy-command-inputs");
  await inputs.getByRole("button", { name: "Cancel", exact: true }).click();
});

test(`${features("console")} inventories console streaming controls`, async ({ page }, testInfo) => {
  await page.goto("/consolelogs");
  await expect(page.getByText("Console logs", { exact: true })).toBeVisible();
  const main = page.getByRole("main");
  const resource = page.getByRole("combobox", { name: "Resource", exact: true });
  await expect(resource).toBeVisible();
  await resource.click();
  await page.getByRole("option", { name: "empty-0000", exact: true }).click();
  await expect(page).toHaveURL(/\/consolelogs\/resource\/empty-0000(?:\?.*)?$/);
  await expect(resource).toHaveAttribute("current-value", "empty-0000");
  await expect(main.getByRole("log").filter({ hasText: "empty-0001" })).toHaveCount(0);
  await expect.poll(() => main.getByRole("log").count()).toBeGreaterThan(0);

  const resourceActions = main.getByRole("button", { name: "Resource actions", exact: true });
  await resourceActions.click();
  const resourceMenu = page.getByRole("menu");
  await expect(resourceMenu.getByText("Restart", { exact: true })).toBeVisible();
  await expect(resourceMenu.getByText("Stop", { exact: true })).toBeVisible();
  await resourceMenu.getByText("View details", { exact: true }).click();
  const resourceDetails = page.getByRole("dialog").filter({ hasText: "empty-0000" });
  await expect(resourceDetails).toBeVisible();
  await resourceDetails.getByRole("button", { name: "Close", exact: true }).click({ force: true });
  await page.goBack();
  await expect(page).toHaveURL(/\/consolelogs\/resource\/empty-0000(?:\?.*)?$/);

  await resource.click();
  await page.getByRole("option", { name: "empty-0001", exact: true }).click();
  await expect(page).toHaveURL(/\/consolelogs\/resource\/empty-0001(?:\?.*)?$/);
  await expect(resource).toHaveAttribute("current-value", "empty-0001");
  await expect(main.getByRole("log").filter({ hasText: "empty-0000" })).toHaveCount(0);
  await expect.poll(() => main.getByRole("log").count()).toBeGreaterThan(0);

  const settings = main.getByRole("button", { name: "Settings", exact: true });
  await settings.click();
  await page.getByRole("menuitem", { name: "Show timestamps", exact: true }).click();
  await settings.click();
  await page.getByRole("menuitem", { name: "UTC timestamps", exact: true }).click();
  await settings.click();
  await page.getByRole("menuitem", { name: "Don't wrap log lines", exact: true }).click();
  await settings.click();
  await page.getByRole("menuitem", { name: "Wrap log lines", exact: true }).click();

  const download = page.waitForEvent("download");
  await settings.click();
  await page.getByRole("menuitem", { name: "Download logs", exact: true }).click();
  await download;
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-console");
});

test(`${features("structured-logs")} inventories structured log controls and rows`, async ({ page }, testInfo) => {
  await page.goto("/structuredlogs");
  await expect(page.getByRole("textbox", { name: "Filter...", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Add filter", exact: true })).toBeVisible();
  await expect(page.getByRole("combobox", { name: /^(Resource|Select a resource)$/ })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Level", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  for (const header of ["Resource", "Level", "Timestamp", "Message", "Trace", "Actions"]) {
    await expect(page.getByRole("columnheader", { name: header, exact: true })).toBeVisible();
  }
  await expect(page.getByText("No structured logs found", { exact: true })).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-structured-logs");
});

test(`${features("traces")} inventories trace controls and empty state`, async ({ page }, testInfo) => {
  await page.goto("/traces");
  await expect(page.getByRole("combobox", { name: /^(Resource|Select a resource)$/ })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Type", exact: true })).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Filter...", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Add filter", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  for (const header of ["Timestamp", "Name", "Spans", "Duration", "Actions"]) {
    await expect(page.getByRole("columnheader", { name: header, exact: true })).toBeVisible();
  }
  await attachScreenshot(page, testInfo, "legacy-traces");
});

test(`${features("metrics")} inventories metric controls and empty state`, async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  await page.goto("/metrics");
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  const duration = page.getByRole("group", { name: "Duration", exact: true });
  for (const value of ["1m", "5m", "15m", "30m", "1h", "3h", "6h", "12h"]) {
    await expect(duration.getByRole("button", { name: value, exact: true })).toBeVisible();
  }
  await expect(page.getByText("Select a resource to view metrics", { exact: true }).first()).toBeVisible();

  const resource = page.getByRole("combobox", { name: /^(Resource|Select a resource)$/ });
  await resource.click();
  await page.getByRole("option", { name: "TestResource", exact: true }).click();
  await expect(resource).toHaveAttribute("current-value", "TestResource");

  const meter = page.getByRole("treeitem", { name: "TestScope-<b>Bold</b>", exact: true });
  const instrument = meter.getByRole("treeitem", { name: "Test-<b>Bold</b>", exact: true });
  await expect(meter).toBeVisible();
  await expect(instrument).toBeVisible();
  await instrument.click();
  await expect(page).toHaveURL(/\/metrics\/resource\/TestResource\?meter=TestScope-%3Cb%3EBold%3C%2Fb%3E&instrument=Test-%3Cb%3EBold%3C%2Fb%3E&duration=5/);

  await expect(page.locator(".js-plotly-plot")).toBeVisible();
  await expect.poll(() => page.locator(".scatterlayer .trace").count()).toBeGreaterThan(0);
  await page.getByRole("tab", { name: "Table", exact: true }).click();
  const metricTable = page.getByRole("table");
  await expect(metricTable).toBeVisible();
  await expect.poll(() => metricTable.getByRole("row").count()).toBeGreaterThan(1);
  await expect(page).toHaveURL(/view=Table/);

  await page.reload();
  await expect(resource).toHaveAttribute("current-value", "TestResource");
  await expect(page.getByRole("tab", { name: "Table", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => page.getByRole("table").getByRole("row").count()).toBeGreaterThan(1);
  await attachScreenshot(page, testInfo, "legacy-metrics");
});

const expectedRegisteredFeatures = getRegisteredFeatures();
const missingRegisteredFeatures = expectedRegisteredFeatures.filter((feature) => !registeredFeatures.has(feature.id));
if (missingRegisteredFeatures.length > 0) {
  throw new Error(`Legacy features without a registered Playwright scenario: ${missingRegisteredFeatures.map((feature) => feature.id).join(", ")}`);
}

function getRegisteredFeatures(): DashboardParityFeature[] {
  const scenarios: LegacyScenario[] = [
    "shell",
    "resources",
    "parameters",
    "commands",
    "console",
    "structured-logs",
    "traces",
    "metrics",
  ];
  return scenarios.flatMap((scenario) => getLegacyScenarioFeatures(scenario));
}

async function dismissBlockingInteraction(page: Page): Promise<void> {
  // Interactions belong to the AppHost rather than a browser context. A command dialog left open
  // by a developer or an interrupted test therefore appears in the next context and blocks input.
  const interaction = page.locator("aside.interaction-pane[role='dialog']");
  if (await interaction.count() === 0) {
    return;
  }

  const cancel = interaction.getByRole("button", { name: "Cancel", exact: true });
  if (await cancel.count() === 1) {
    await cancel.click({ force: true });
  } else {
    await interaction.getByRole("button", { name: "Close", exact: true }).click({ force: true });
  }
  await expect(interaction).toHaveCount(0);
}
