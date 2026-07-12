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
  await expect(customIconRow.locator('[data-icon-name="Server"][data-icon-variant="regular"]')).toHaveCount(1);
  await expect(defaultIconRow.locator('[data-icon-name="Document"][data-icon-variant="filled"]')).toHaveCount(1);
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
  await expect(page.locator('g[data-icon-name="Server"][data-icon-variant="regular"]')).toHaveCount(1);
  await expect(page.locator('g[data-icon-name="Document"][data-icon-variant="filled"]').first()).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-resource-graph");
});

test(`${features("resource-virtualization")} bounds a large resource inventory`, async ({ page }) => {
  await page.goto("/");
  await page.getByRole("button", { name: "View options", exact: true }).click();
  await page.getByRole("menuitem", { name: "Show hidden resources", exact: true }).click();

  const main = page.getByRole("main");
  const table = main.getByRole("table");
  const scroller = main.locator(".page__body");
  await expect(main.locator(".page__subtitle")).toContainText(/2\d{2} resources/);
  await expect.poll(() => table.locator("tbody tr.clickable").count()).toBeLessThan(100);

  await scroller.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    element.dispatchEvent(new Event("scroll"));
  });
  const tail = table.getByRole("row").filter({ hasText: "virtualized-0249" });
  await expect(tail).toBeVisible();
  await tail.getByText("virtualized-0249", { exact: true }).click();
  await expect(page.getByRole("dialog").filter({ hasText: "virtualized-0249" })).toBeVisible();
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
  await filter.fill("");

  const enterValues = page.getByRole("button", { name: "Enter values", exact: true });
  if (await enterValues.isVisible()) {
    await enterValues.click();
    const interaction = page.getByRole("dialog", { name: "Set unresolved parameters", exact: true });
    await interaction.getByPlaceholder("Enter value for api-key", { exact: true }).fill("legacy-api-key");
    await interaction.getByPlaceholder("Enter value for db-connection-string", { exact: true }).fill("Server=legacy;Database=stress");
    await interaction.getByRole("button", { name: "Save", exact: true }).click();
    await expect(interaction).toHaveCount(0);
  }
  await expect(table.getByRole("row").filter({ hasText: "api-key" })).not.toContainText("Value not set");
  await expect(table.getByRole("row").filter({ hasText: "db-connection-string" })).not.toContainText("Value not set");
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
  await expect(iconCommand.locator('[data-icon-name="CloudDatabase"][data-icon-variant="regular"]')).toHaveCount(1);
  await expect(highlightedIconCommand.locator('[data-icon-name="CloudDatabase"][data-icon-variant="regular"]')).toHaveCount(1);
  await highlightedIconCommand.click();
  await expect(page.getByText('"Icon test highlighted" succeeded', { exact: false })).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-icon-commands");
  await details.getByRole("button", { name: "Close", exact: true }).click({ force: true });

  const lifecycleCommands = table.getByRole("row").filter({ hasText: "lifecycle-commands" });
  await lifecycleCommands.click({ force: true });
  details = page.getByRole("dialog").filter({ hasText: "lifecycle-commands" });
  await expect(details.getByRole("button", { name: "Stop all resources", exact: true })
    .locator('[data-icon-name="Stop"][data-icon-variant="filled"]')).toHaveCount(1);
  await expect(details.getByRole("button", { name: "Start all resources", exact: true })
    .locator('[data-icon-name="Play"][data-icon-variant="filled"]')).toHaveCount(1);
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
  await details.getByRole("button", { name: "Close", exact: true }).click({ force: true });

  await page.goto("/consolelogs/resource/stress-apiservice");
  await page.getByRole("main").getByRole("button", { name: "Resource actions", exact: true }).click();
  await expect(page.locator('fluent-menu-item[title="Write to console"]')
    .locator('[data-icon-name="ContentViewGalleryLightning"][data-icon-variant="regular"]')).toHaveCount(1);
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
  await page.reload();
  await expect(resource).toHaveAttribute("current-value", "empty-0001");
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

test(`${features("console-virtualization")} bounds high-volume console rendering`, async ({ page }) => {
  test.setTimeout(60_000);
  await page.goto("/");
  const apiResourceRow = page.getByRole("table").getByRole("row").filter({ hasText: "stress-apiservice" });
  await apiResourceRow.getByText("stress-apiservice", { exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "stress-apiservice" })
    .getByRole("button", { name: "Write to console", exact: true }).click();

  await page.goto("/consolelogs/resource/stress-apiservice");
  const consoleScroller = page.locator(".console-overflow");
  const renderedLines = consoleScroller.locator(".log-line-row-container");
  await expect.poll(() => renderedLines.count(), { timeout: 30_000 }).toBeGreaterThan(200);
  expect(await renderedLines.count()).toBeLessThan(600);

  await consoleScroller.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    element.dispatchEvent(new Event("scroll"));
  });
  await expect(consoleScroller.getByText("4999 Out", { exact: true })).toBeVisible({ timeout: 10_000 });
  expect(await renderedLines.count()).toBeLessThan(600);
  await expect(consoleScroller.locator(".log-line-number").filter({ hasText: /^\d+$/ }).last()).not.toHaveText("");
});

test(`${features("structured-logs")} inventories structured log controls and rows`, async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  await page.goto("/");
  const resourcesTable = page.getByRole("table");
  const apiRow = resourcesTable.getByRole("row").filter({ hasText: "stress-apiservice" });
  await expect(apiRow).toHaveCount(1);
  await apiRow.getByText("stress-apiservice", { exact: true }).click();
  const resourceDetails = page.getByRole("dialog").filter({ hasText: "stress-apiservice" });
  await resourceDetails.getByRole("button", { name: "Log message", exact: true }).click();

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

  await page.getByRole("button", { name: "Add filter", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Add filter", exact: true })).toBeVisible();
  await page.getByRole("combobox", { name: "Value", exact: true }).fill("Hosting environment");
  await page.getByRole("button", { name: "Apply filter", exact: true }).click();

  const filterToolbar = page.locator(".deck-structured-toolbar");
  const filtersButton = filterToolbar.getByRole("button", { name: "Filters", exact: true });
  await expect(filtersButton).toBeVisible();
  await expect(filterToolbar.getByText("1", { exact: true })).toBeVisible();

  await filtersButton.click();
  await expect(page.getByRole("menuitem", { name: "Message contains Hosting environment", exact: true })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Disable all", exact: true })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Remove all", exact: true })).toBeVisible();
  await page.getByRole("menuitem", { name: "Disable all", exact: true }).click();
  await expect(filterToolbar.getByText("1", { exact: true })).toHaveCount(0);

  await filtersButton.click();
  await expect(page.getByRole("menuitem", { name: "Enable all", exact: true })).toBeVisible();
  await page.getByRole("menuitem", { name: "Enable all", exact: true }).click();
  await expect(filterToolbar.getByText("1", { exact: true })).toBeVisible();

  await filtersButton.click();
  await page.getByRole("menuitem", { name: "Message contains Hosting environment", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Edit filter", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Disable filter", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove filter", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();

  await filtersButton.click();
  await page.getByRole("menuitem", { name: "Remove all", exact: true }).click();
  await expect(filtersButton).toHaveCount(0);

  const table = page.getByRole("table");
  const startupRow = table.getByRole("row").filter({ hasText: "Now listening on:" }).first();
  await expect(startupRow).toBeVisible({ timeout: 45_000 });

  await startupRow.getByRole("cell").first().click();
  const details = page.locator("aside.drawer[role='dialog']");
  await expect(details).toBeVisible();
  await expect(details).toContainText("ListeningOnAddress");
  await expect(details).toContainText("Microsoft.Hosting.Lifetime");
  await expect(details).toContainText("Now listening on:");
  await expect(details).toContainText("Category");
  await expect(details).toContainText("service.name");
  await expect(details).toContainText("Stress.ApiService");

  await details.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Log message", exact: true }).click();
  let visualizer = page.locator("fluent-dialog");
  await expect(visualizer).toContainText("Now listening on:");
  await expect(visualizer.getByRole("button", { name: "Copy to clipboard", exact: true })).toBeVisible();
  await visualizer.getByRole("button", { name: "Close", exact: true }).click();

  await details.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "View JSON", exact: true }).click();
  visualizer = page.locator("fluent-dialog");
  await expect(visualizer).toContainText(/log-\d+\.json/);
  await expect(visualizer).toContainText('"name": "Microsoft.Hosting.Lifetime"');
  await expect(visualizer).toContainText('"key": "service.name"');
  await expect(visualizer).toContainText('"stringValue": "Stress.ApiService"');
  await expect(visualizer.getByRole("button", { name: "Copy to clipboard", exact: true })).toBeVisible();
  await visualizer.getByRole("button", { name: "Close", exact: true }).click();
  await details.getByRole("button", { name: "Close", exact: true }).click();

  await startupRow.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "View details", exact: true }).click();
  await expect(details).toBeVisible();
  await details.getByRole("button", { name: "Close", exact: true }).click();

  const tracedLogRows = table.getByRole("row").filter({ has: page.locator('a[href^="/traces/detail/"]') });
  await expect.poll(() => tracedLogRows.count(), { timeout: 30_000 }).toBeGreaterThan(0);
  const tracedLogRow = tracedLogRows.last();
  await tracedLogRow.getByRole("button", { name: "Actions", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: "View details", exact: true })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Log message", exact: true })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "View JSON", exact: true })).toBeVisible();
  await page.getByRole("menuitem", { name: "View details", exact: true }).click();
  await expect(details).toBeVisible();
  await details.getByRole("button", { name: "Close", exact: true }).click();

  const traceLink = tracedLogRow.getByRole("link");
  await expect(traceLink).toHaveAttribute("href", /\/traces\/detail\/[0-9a-f]{32}\?spanId=[0-9a-f]{16}$/);
  await traceLink.click();
  await expect(page).toHaveURL(/\/traces\/detail\/[0-9a-f]{32}\?spanId=[0-9a-f]{16}$/);
  await expect(page.getByText(/Total spans \d+/)).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-structured-logs");
});

test(`${features("structured-log-genai")} opens the GenAI log conversation visualizer`, async ({ page }) => {
  await page.goto("/");
  const apiResourceRow = page.getByRole("table").getByRole("row").filter({ hasText: "stress-apiservice" });
  await apiResourceRow.getByText("stress-apiservice", { exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "stress-apiservice" })
    .getByRole("button", { name: "Gen AI trace", exact: true }).click();
  await expect(page.getByText('"Gen AI trace" succeeded', { exact: false })).toBeVisible({ timeout: 30_000 });

  await page.goto("/structuredlogs/resource/Stress.ApiService");
  const genAIDetails = page.getByRole("button", { name: "GenAI details", exact: true });
  await expect.poll(() => genAIDetails.count(), { timeout: 30_000 }).toBeGreaterThan(0);
  await genAIDetails.last().click();

  const visualizer = page.locator("fluent-dialog");
  await expect(visualizer).toContainText("chat gpt");
  await expect(visualizer).toContainText("Tokens 470");
  await expect(visualizer).toContainText("This is the input prompt.");
  await expect(visualizer.getByRole("tab", { name: /^Tools \d+$/ })).toBeVisible();
  await visualizer.getByRole("button", { name: "Close", exact: true }).click();
});

test(`${features("structured-log-session")} restores resource, filters, and log deep links`, async ({ page }) => {
  await page.goto("/structuredlogs/resource/Stress.ApiService?logLevel=information");
  await expect(page.getByRole("combobox", { name: /^(Resource|Select a resource)$/ }))
    .toHaveAttribute("current-value", "Stress.ApiService");
  await expect(page.getByRole("combobox", { name: "Level", exact: true }).locator("option:checked"))
    .toHaveText("Information");

  await page.getByRole("button", { name: "Add filter", exact: true }).click();
  await page.getByRole("combobox", { name: "Value", exact: true }).fill("Hosting environment");
  await page.getByRole("button", { name: "Apply filter", exact: true }).click();
  await expect(page).toHaveURL(/\/structuredlogs\/resource\/Stress\.ApiService\?[^#]*logLevel=information[^#]*filters=/);

  const startupRow = page.getByRole("table").getByRole("row").filter({ hasText: "Hosting environment" }).first();
  await expect(startupRow).toBeVisible();
  const logEntryId = await startupRow.getAttribute("data-log-entry-id");
  expect(logEntryId).toMatch(/^\d+$/);

  const deepLink = new URL(page.url());
  deepLink.searchParams.set("logEntryId", logEntryId!);
  await page.goto(deepLink.toString());
  await expect(page.locator("aside.drawer[role='dialog']")).toContainText("Hosting environment");
  await expect.poll(() => new URL(page.url()).searchParams.has("logEntryId")).toBe(false);
  await expect(page).toHaveURL(/\/structuredlogs\/resource\/Stress\.ApiService\?[^#]*logLevel=information[^#]*filters=/);

  await page.reload();
  await expect(page.getByRole("combobox", { name: /^(Resource|Select a resource)$/ }))
    .toHaveAttribute("current-value", "Stress.ApiService");
  await expect(page.getByRole("combobox", { name: "Level", exact: true }).locator("option:checked"))
    .toHaveText("Information");
  await expect(page.locator(".deck-structured-toolbar").getByText("1", { exact: true })).toBeVisible();
});

test(`${features("structured-log-virtualization")} bounds high-volume structured log rendering`, async ({ page }) => {
  test.setTimeout(90_000);
  await page.goto("/");
  const apiResourceRow = page.getByRole("table").getByRole("row").filter({ hasText: "stress-apiservice" });
  await apiResourceRow.getByText("stress-apiservice", { exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "stress-apiservice" })
    .getByRole("button", { name: "Log message limit", exact: true }).click();

  await page.goto("/structuredlogs/resource/Stress.ApiService");
  await expect(page.getByRole("strong")).toHaveText("10000 structured logs", { timeout: 30_000 });
  const virtualizedBody = page.locator("#structured-logs-page-body-id");
  const virtualizedTable = virtualizedBody.getByRole("table");
  expect(await virtualizedTable.locator("tbody tr").count()).toBeLessThan(100);

  await virtualizedBody.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    element.dispatchEvent(new Event("scroll"));
  });
  const lastGeneratedLog = virtualizedTable.getByRole("row").filter({ hasText: "Log entry 99-99" });
  await expect(lastGeneratedLog).toBeVisible({ timeout: 10_000 });
  expect(await virtualizedTable.locator("tbody tr").count()).toBeLessThan(100);
});

test(`${features("traces")} inventories trace controls and nested span details`, async ({ page }, testInfo) => {
  test.setTimeout(75_000);
  await page.goto("/");
  const resourcesTable = page.getByRole("table");
  const apiRow = resourcesTable.getByRole("row").filter({ hasText: "stress-apiservice" });
  await expect(apiRow).toHaveCount(1);
  await apiRow.getByText("stress-apiservice", { exact: true }).click();
  const resourceDetails = page.getByRole("dialog").filter({ hasText: "stress-apiservice" });
  await resourceDetails.getByRole("button", { name: "Out of order nested spans", exact: true }).click();

  await page.goto("/traces");
  await expect(page.getByRole("combobox", { name: /^(Resource|Select a resource)$/ })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Type", exact: true })).toBeVisible();
  const traceFilter = page.getByRole("textbox", { name: "Filter...", exact: true });
  await expect(traceFilter).toBeVisible();
  await expect(page.getByRole("button", { name: "Add filter", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  for (const header of ["Timestamp", "Name", "Spans", "Duration", "Actions"]) {
    await expect(page.getByRole("columnheader", { name: header, exact: true })).toBeVisible();
  }

  await traceFilter.fill("GET /nested-trace-spans");
  const matchingTraceRows = page.getByRole("table").getByRole("row").filter({ hasText: "GET /nested-trace-spans" });
  await expect.poll(() => matchingTraceRows.count(), { timeout: 30_000 }).toBeGreaterThan(0);
  const traceRow = matchingTraceRows.last();
  await traceRow.getByRole("cell").first().click();
  await expect(page).toHaveURL(/\/traces\/detail\/[0-9a-f]{32}$/);
  await expect(page.getByText(/Depth 4/)).toBeVisible();
  await expect(page.getByText(/Total spans \d+/)).toBeVisible();

  const spansTable = page.getByRole("table");
  await expect(spansTable.getByText("ValidateAndUpdateCacheService.ExecuteAsync", { exact: true })).toBeVisible();
  await expect(spansTable.getByText("ValidateAndUpdateCacheService.activeUser", { exact: true })).toHaveCount(2);
  await expect(spansTable.getByText("Perform1", { exact: true })).toHaveCount(2);

  const notificationDismissButtons = page.getByRole("button", { name: "Dismiss notification", exact: true });
  await notificationDismissButtons.evaluateAll((buttons) => {
    for (const button of buttons) {
      (button as HTMLButtonElement).click();
    }
  });
  await expect(notificationDismissButtons).toHaveCount(0);

  const traceToolbar = page.locator(".traces-toolbar");
  await traceToolbar.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Collapse all", exact: true }).click();
  await expect(spansTable.getByText("Perform1", { exact: true })).toHaveCount(0);
  await traceToolbar.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "Expand all", exact: true }).click();
  await expect(spansTable.getByText("Perform1", { exact: true })).toHaveCount(2);

  const rootSpan = spansTable.getByRole("row").filter({ hasText: "GET /nested-trace-spans" });
  await rootSpan.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "View details", exact: true }).click();
  const spanDetails = page.locator("aside.drawer[role='dialog']");
  await expect(spanDetails).toContainText("SpanId");
  await expect(spanDetails).toContainText("TraceId");
  await expect(spanDetails).toContainText("http.request.method");
  await expect(spanDetails).toContainText("Microsoft.AspNetCore");
  await expect(spanDetails).toContainText("service.name");
  await expect(spanDetails).toContainText("Stress.ApiService");
  await spanDetails.getByRole("button", { name: "Close", exact: true }).click();

  const detailUrl = page.url();
  await page.reload();
  await expect(page).toHaveURL(detailUrl);
  await expect(page.getByText(/Total spans \d+/)).toBeVisible();
  await expect(page.getByRole("table").getByText("Perform1", { exact: true })).toHaveCount(2);
  await attachScreenshot(page, testInfo, "legacy-traces");
});

test(`${features("trace-details")} verifies error events and linked span navigation`, async ({ page }) => {
  await page.goto("/");
  const apiResourceRow = page.getByRole("table").getByRole("row").filter({ hasText: "stress-apiservice" });
  await apiResourceRow.getByText("stress-apiservice", { exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "stress-apiservice" })
    .getByRole("button", { name: "Trace details", exact: true }).click();

  await page.goto("/traces");
  await page.getByRole("textbox", { name: "Filter...", exact: true }).fill("trace-fixture-error");
  const matchingTraceRows = page.getByRole("table").getByRole("row").filter({ hasText: "trace-fixture-error" });
  await expect.poll(() => matchingTraceRows.count(), { timeout: 30_000 }).toBeGreaterThan(0);
  const traceRow = matchingTraceRows.last();
  await expect(traceRow).toHaveClass(/trace-row-error/);
  await traceRow.getByRole("cell").first().click();
  await expect(page).toHaveURL(/\/traces\/detail\/[0-9a-f]{32}$/);

  const notificationDismissButtons = page.getByRole("button", { name: "Dismiss notification", exact: true });
  await notificationDismissButtons.evaluateAll((buttons) => {
    for (const button of buttons) {
      (button as HTMLButtonElement).click();
    }
  });
  await expect(notificationDismissButtons).toHaveCount(0);

  const errorSpan = page.getByRole("table").getByRole("row").filter({ hasText: "trace-fixture-error" });
  await errorSpan.getByRole("button", { name: "Actions", exact: true }).click();
  await page.getByRole("menuitem", { name: "View details", exact: true }).click();
  const spanDetails = page.locator("aside.drawer[role='dialog']");
  await expect(spanDetails).toContainText("Error");
  await expect(spanDetails).toContainText("fixture.error");
  await expect(spanDetails).toContainText(/Events\s*1/);
  await expect(spanDetails).toContainText("exception");
  await expect(spanDetails).toContainText("exception.type");
  await expect(spanDetails).toContainText("FixtureException");
  await expect(spanDetails).toContainText(/Links\s*1/);
  await expect(spanDetails).toContainText("fixture.link");
  await expect(spanDetails).toContainText("linked-target");

  await spanDetails.getByRole("button", { name: /^[0-9a-f]{7}$/ }).click();
  await expect(page).toHaveURL(/\/traces\/detail\/[0-9a-f]{32}\?spanId=[0-9a-f]{16}$/);
  await expect(page.getByRole("table").getByText("trace-fixture-linked-target", { exact: true })).toBeVisible();
});

test(`${features("trace-genai")} opens the GenAI trace conversation visualizer`, async ({ page }) => {
  await page.goto("/");
  const apiResourceRow = page.getByRole("table").getByRole("row").filter({ hasText: "stress-apiservice" });
  await apiResourceRow.getByText("stress-apiservice", { exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "stress-apiservice" })
    .getByRole("button", { name: "Gen AI trace", exact: true }).click();

  await page.goto("/traces");
  await page.getByRole("textbox", { name: "Filter...", exact: true }).fill("GET /genai-trace");
  const matchingTraceRows = page.getByRole("table").getByRole("row").filter({ hasText: "GET /genai-trace" });
  await expect.poll(() => matchingTraceRows.count(), { timeout: 30_000 }).toBeGreaterThan(0);
  const traceRow = matchingTraceRows.last();
  await traceRow.getByRole("button", { name: "GenAI details", exact: true }).click();

  const visualizer = page.locator("fluent-dialog");
  await expect(visualizer).toContainText("chat gpt");
  await expect(visualizer).toContainText("Tokens 470");
  await expect(visualizer).toContainText("This is the input prompt.");
  await expect(visualizer).toContainText("The weather in Tokyo is currently sunny");
  await expect(visualizer.getByRole("tab", { name: /^Tools \d+$/ })).toBeVisible();
  await visualizer.getByRole("button", { name: "Close", exact: true }).click();
});

test(`${features("trace-session")} restores trace resource type filters and selection`, async ({ page }) => {
  await page.goto("/traces");
  const resource = page.getByRole("combobox", { name: /^(Resource|Select a resource)$/ });
  await resource.click();
  await page.getByRole("option", { name: "Stress.ApiService", exact: true }).click();
  const type = page.getByRole("combobox", { name: "Type", exact: true });
  await type.selectOption({ label: "(Other)" });

  await page.getByRole("button", { name: "Add filter", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Add filter", exact: true })).toBeVisible();
  await page.getByRole("combobox", { name: "Value", exact: true }).fill("trace-fixture-error");
  await page.getByRole("button", { name: "Apply filter", exact: true }).click();
  await expect.poll(() => page.url()).toContain("filters=");
  const traceListUrl = page.url();
  expect(traceListUrl).toContain("type=other");

  await page.reload();
  await expect(page).toHaveURL(traceListUrl);
  await expect(resource).toHaveAttribute("current-value", "Stress.ApiService");
  await expect(type.locator("option:checked")).toHaveText("(Other)");
  await expect(page.locator(".traces-toolbar").getByText("1", { exact: true })).toBeVisible();

  const traceRow = page.getByRole("table").getByRole("row").filter({ hasText: "trace-fixture-error" });
  await expect(traceRow).toBeVisible();
  await traceRow.getByRole("cell").first().click();
  await expect(page).toHaveURL(/\/traces\/detail\/[0-9a-f]{32}$/);
  await page.goBack();
  await expect(page).toHaveURL(traceListUrl);
  await expect(resource).toHaveAttribute("current-value", "Stress.ApiService");
  await expect(type.locator("option:checked")).toHaveText("(Other)");
});

test(`${features("trace-virtualization")} bounds a large trace inventory`, async ({ page }) => {
  test.setTimeout(60_000);
  await page.goto("/");
  const apiResourceRow = page.getByRole("table").getByRole("row").filter({ hasText: "stress-apiservice" });
  await apiResourceRow.getByText("stress-apiservice", { exact: true }).click();
  const traceCommand = page.getByRole("dialog").filter({ hasText: "stress-apiservice" })
    .getByRole("button", { name: "Trace virtualization", exact: true });
  await expect(traceCommand).toBeEnabled({ timeout: 30_000 });
  await traceCommand.click();
  await expect(page.getByText('"Trace virtualization" succeeded', { exact: false })).toBeVisible({ timeout: 30_000 });

  await page.goto("/traces");
  const main = page.getByRole("main");
  const table = main.getByRole("table");
  const scroller = main.locator(".page__body");
  await expect.poll(async () => {
    const subtitle = await main.locator(".page__subtitle").textContent();
    return Number.parseInt(subtitle?.replaceAll(",", "") ?? "0", 10);
  }, { timeout: 30_000 }).toBeGreaterThanOrEqual(500);
  await expect.poll(() => table.locator("tbody tr.clickable").count()).toBeLessThan(100);

  await scroller.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    element.dispatchEvent(new Event("scroll"));
  });
  const tail = table.getByRole("row").filter({ hasText: /tracelimit-/ }).last();
  await expect(tail).toBeVisible();
  await tail.click();
  await expect(page).toHaveURL(/\/traces\/detail\/[0-9a-f]{32}$/);
});

test(`${features("metrics")} inventories metric controls and empty state`, async ({ page }, testInfo) => {
  test.setTimeout(120_000);
  await page.goto("/");
  const resourcesTable = page.getByRole("table");
  const apiRow = resourcesTable.getByRole("row").filter({ hasText: "stress-apiservice" });
  await expect(apiRow).toHaveCount(1);
  await apiRow.getByText("stress-apiservice", { exact: true }).click();
  const resourceDetails = page.getByRole("dialog").filter({ hasText: "stress-apiservice" });
  await expect(resourceDetails.getByText("Healthy", { exact: true }).first()).toBeVisible({ timeout: 45_000 });
  await resourceDetails.getByRole("button", { name: "Out of order nested spans", exact: true }).click();
  await expect(page.getByText('"Out of order nested spans" succeeded', { exact: false })).toBeVisible({ timeout: 30_000 });
  await resourceDetails.getByRole("button", { name: "Log message", exact: true }).click();
  await expect(page.getByText('"Log message" succeeded', { exact: false })).toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(2_000);

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
  await page.getByRole("option", { name: "Stress.ApiService", exact: true }).click();
  await expect(resource).toHaveAttribute("current-value", "Stress.ApiService");

  const meter = page.getByRole("treeitem", { name: "Microsoft.AspNetCore.Hosting", exact: true });
  const instrument = meter.getByRole("treeitem", { name: "http.server.request.duration", exact: true });
  await expect(meter).toBeVisible();
  await expect(instrument).toBeVisible();
  await instrument.click();
  await expect(page).toHaveURL(/\/metrics\/resource\/Stress\.ApiService\?meter=Microsoft\.AspNetCore\.Hosting&instrument=http\.server\.request\.duration&duration=5/);
  await expect(page.getByRole("heading", { name: "http.server.request.duration", exact: true })).toBeVisible();
  await expect(page.getByText("Duration of HTTP server requests.", { exact: true })).toBeVisible();

  await expect(page.locator(".js-plotly-plot")).toBeVisible();
  await expect.poll(() => page.locator(".scatterlayer .trace").count()).toBeGreaterThan(0);
  const exemplarPoints = page.locator(".scatterlayer .point");
  await expect.poll(() => exemplarPoints.count(), { timeout: 30_000 }).toBeGreaterThan(0);
  const exemplarPoint = exemplarPoints.last();
  const exemplarBounds = await exemplarPoint.boundingBox();
  expect(exemplarBounds).not.toBeNull();
  await page.mouse.move(exemplarBounds!.x + exemplarBounds!.width / 2, exemplarBounds!.y + exemplarBounds!.height / 2);
  const hover = page.locator(".hoverlayer .hovertext");
  await expect(hover).toContainText("Value:");
  await expect(hover).toContainText("Time:");

  let activePanel = page.locator('[role="tabpanel"]:not([hidden])');
  const routeFilter = activePanel.getByRole("row").filter({ hasText: "http.route" });
  await expect(routeFilter).toContainText("/nested-trace-spans");
  await expect(routeFilter).toContainText("/log-message");
  const routeFilterButton = routeFilter.getByRole("button", { name: "All tags", exact: true });
  await routeFilterButton.click();
  const nestedRoute = routeFilter.getByRole("checkbox", { name: "/nested-trace-spans", exact: true });
  const logRoute = routeFilter.getByRole("checkbox", { name: "/log-message", exact: true });
  await expect(nestedRoute).toBeChecked();
  await expect(logRoute).toBeChecked();
  await logRoute.uncheck();
  await expect(routeFilter.getByRole("button", { name: "Filtered tags", exact: true })).toBeVisible();
  await logRoute.check();
  await routeFilterButton.click({ force: true });

  const showCount = activePanel.getByRole("checkbox", { name: "Show count", exact: true });
  await showCount.check();
  await page.getByRole("tab", { name: "Table", exact: true }).click();
  activePanel = page.locator('[role="tabpanel"]:not([hidden])');
  let metricTable = activePanel.getByRole("table").first();
  await expect(metricTable).toBeVisible();
  await expect.poll(() => metricTable.getByRole("row").count()).toBeGreaterThan(1);
  await expect(metricTable.getByRole("columnheader")).toHaveText(["Time", "Count"]);
  await expect(page).toHaveURL(/view=Table/);

  await activePanel.getByRole("checkbox", { name: "Show count", exact: true }).uncheck();
  metricTable = activePanel.getByRole("table").first();
  await expect(metricTable.getByRole("columnheader")).toHaveText(["Time", "P50 Seconds", "P90 Seconds", "P99 Seconds", "Exemplars"]);
  const viewExemplarButtons = metricTable.getByRole("button", { name: "View exemplars", exact: true });
  await expect.poll(() => viewExemplarButtons.count()).toBeGreaterThan(0);
  await viewExemplarButtons.last().click();
  const exemplars = page.locator("fluent-dialog").filter({ hasText: "Exemplars" });
  await expect(exemplars).toContainText("Trace");
  await expect(exemplars).toContainText("Timestamp");
  await expect(exemplars).toContainText("Value");
  await expect(exemplars).toContainText(/Stress\.ApiService: GET \//);
  const tracedExemplars = exemplars.getByRole("row").filter({ hasText: "Stress.ApiService: GET /" });
  await expect.poll(() => tracedExemplars.count()).toBeGreaterThan(0);
  await tracedExemplars.last().getByRole("button", { name: "View", exact: true }).click();
  await expect(page).toHaveURL(/\/traces\/detail\/[0-9a-f]{32}\?spanId=[0-9a-f]{16}$/);
  await page.goBack();

  await page.reload();
  await expect(resource).toHaveAttribute("current-value", "Stress.ApiService");
  await expect(page.getByRole("tab", { name: "Table", exact: true })).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => page.getByRole("table").getByRole("row").count()).toBeGreaterThan(1);
  await attachScreenshot(page, testInfo, "legacy-metrics");
});

test(`${features("console-follow")} preserves manual console position and restores tail-follow`, async ({ page }) => {
  test.setTimeout(60_000);
  await page.goto("/consolelogs/resource/stress-apiservice");
  const consoleScroller = page.locator(".console-overflow");
  await page.getByRole("button", { name: "Remove data", exact: true }).click();
  await page.getByRole("menuitem", { name: "Remove telemetry for stress-apiservice", exact: true }).click();
  await expect(consoleScroller.locator(".log-line-row-container")).toHaveCount(0, { timeout: 10_000 });

  await page.goto("/");
  const apiResourceRow = page.getByRole("table").getByRole("row").filter({ hasText: "stress-apiservice" });
  await apiResourceRow.getByText("stress-apiservice", { exact: true }).click();
  await page.getByRole("dialog").filter({ hasText: "stress-apiservice" })
    .getByRole("button", { name: "Write to console", exact: true }).click();
  await page.goto("/consolelogs/resource/stress-apiservice");

  await expect.poll(() => consoleScroller.evaluate((element) => element.scrollHeight - element.clientHeight), {
    timeout: 30_000,
  }).toBeGreaterThan(1_000);

  await expect.poll(() => consoleScroller.evaluate((element) => {
    element.scrollTop = 0;
    element.dispatchEvent(new Event("scroll"));
    return Boolean((window as Window & { getIsScrolledToContent?: () => boolean }).getIsScrolledToContent?.());
  })).toBe(true);
  const heightBeforeManualBurst = await consoleScroller.evaluate((element) => element.scrollHeight);
  await page.getByRole("main").getByRole("button", { name: "Stop resource", exact: true }).click();
  await expect.poll(() => consoleScroller.evaluate((element) => element.scrollHeight), { timeout: 30_000 })
    .toBeGreaterThan(heightBeforeManualBurst);
  expect(await consoleScroller.evaluate((element) => element.scrollTop)).toBeLessThan(100);

  await expect.poll(() => consoleScroller.evaluate((element) => {
    element.scrollTop = element.scrollHeight;
    element.dispatchEvent(new Event("scroll"));
    return Boolean((window as Window & { getIsScrolledToContent?: () => boolean }).getIsScrolledToContent?.());
  })).toBe(false);
  await page.getByRole("main").getByRole("button", { name: "Start resource", exact: true }).click();
  await expect(page.getByRole("main").getByRole("button", { name: "Stop resource", exact: true })).toBeVisible({ timeout: 30_000 });
  await expect.poll(() => consoleScroller.evaluate((element) =>
    element.scrollHeight - element.clientHeight - element.scrollTop)).toBeLessThan(10);
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
