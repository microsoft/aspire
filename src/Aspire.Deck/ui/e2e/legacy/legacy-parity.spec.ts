import { expect, test, type Page, type TestInfo } from "@playwright/test";
import {
  getLegacyScenarioFeatures,
  type DashboardParityFeature,
  type LegacyScenario,
} from "../parity/dashboard-parity-features";

const registeredFeatures = new Set<string>();
const browserErrors = new WeakMap<Page, string[]>();

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

  await page.goto("/");
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
  await expect(page.getByText("Endpoint is unsecured", { exact: true })).toBeVisible();

  await attachScreenshot(page, testInfo, "legacy-shell");
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
  await expect(details.getByRole("button", { name: "Icon test", exact: true })).toBeVisible();
  await expect(details.getByRole("button", { name: "Icon test highlighted", exact: true })).toBeVisible();
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
  await expect(page.getByRole("combobox", { name: "Resource", exact: true })).toBeVisible();
  await expect(page.getByRole("main").getByRole("button", { name: "Settings", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  await expect(page.getByRole("log").first()).toBeVisible();
  await attachScreenshot(page, testInfo, "legacy-console");
});

test(`${features("structured-logs")} inventories structured log controls and rows`, async ({ page }, testInfo) => {
  await page.goto("/structuredlogs");
  await expect(page.getByRole("textbox", { name: "Filter...", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Add filter", exact: true })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Select a resource", exact: true })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Level", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  for (const header of ["Resource", "Level", "Timestamp", "Message", "Trace", "Actions"]) {
    await expect(page.getByRole("columnheader", { name: header, exact: true })).toBeVisible();
  }
  await expect(page.getByRole("button", { name: "Open in text visualizer", exact: true }).first()).toBeVisible();
  await expect(page.getByRole("button", { name: "Actions", exact: true }).first()).toBeVisible();
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
  await page.goto("/metrics");
  await expect(page.getByRole("button", { name: "Pause incoming data", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Remove data", exact: true })).toBeVisible();
  const duration = page.getByRole("group", { name: "Duration", exact: true });
  for (const value of ["1m", "5m", "15m", "30m", "1h", "3h", "6h", "12h"]) {
    await expect(duration.getByRole("button", { name: value, exact: true })).toBeVisible();
  }
  await expect(page.getByText("Select a resource to view metrics", { exact: true }).first()).toBeVisible();
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
