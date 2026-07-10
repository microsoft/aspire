import { expect, test, type Locator, type Page, type TestInfo } from "@playwright/test";
import {
  getMissingToolkitFeatures,
  type ToolkitFeatureId,
} from "./toolkit-features";

const coveredFeatures = new Set<ToolkitFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();

function features(...ids: ToolkitFeatureId[]): string {
  for (const id of ids) {
    coveredFeatures.add(id);
  }

  return `[${ids.join(", ")}]`;
}

async function attachScreenshot(page: Page, testInfo: TestInfo, name: string): Promise<void> {
  const body = await page.screenshot({
    animations: "disabled",
    fullPage: true,
  });
  await testInfo.attach(`${name}.png`, { body, contentType: "image/png" });
}

async function contrastRatio(locator: Locator): Promise<number> {
  return locator.evaluate((element) => {
    const parse = (color: string): [number, number, number] => {
      const values = color.match(/[\d.]+/g)?.slice(0, 3).map(Number);
      if (!values || values.length !== 3) {
        throw new Error(`Unable to parse color '${color}'.`);
      }
      return values as [number, number, number];
    };
    const luminance = ([red, green, blue]: [number, number, number]): number => {
      const channels = [red, green, blue].map((value) => {
        const channel = value / 255;
        return channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
      });
      return 0.2126 * channels[0]! + 0.7152 * channels[1]! + 0.0722 * channels[2]!;
    };

    const styles = getComputedStyle(element);
    const foreground = luminance(parse(styles.color));
    const background = luminance(parse(styles.backgroundColor));
    return (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05);
  });
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

  await page.goto("/?view=toolkit");
  await expect(page.getByTestId("toolkit-playground")).toBeVisible();
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
});

test(`${features("TK-BROWSER-001", "TK-STATUS-001", "TK-EMPTY-001", "TK-A11Y-001")} renders the reviewed toolkit contract`, async ({ page }) => {
  await expect(page).toHaveTitle("Aspire Deck");
  await expect(page.getByRole("heading", { level: 1, name: "Deck Toolkit" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Status" })).toContainText(
    "NeutralHealthyStartingDegradedFailedSelected",
  );
  await expect(page.getByText("No incidents", { exact: true })).toBeVisible();
  await expect(page.getByTestId("toolkit-playground")).toMatchAriaSnapshot({
    name: "toolkit.aria.yml",
  });
});

test(`${features("TK-ACTIONS-001")} exercises every button variant`, async ({ page }) => {
  const status = page.getByRole("region", { name: "Actions" }).getByRole("status");
  const actions = [
    ["Secondary", "Secondary selected"],
    ["Primary", "Primary selected"],
    ["Danger", "Danger selected"],
    ["Ghost", "Ghost selected"],
  ] as const;

  for (const [button, expectedStatus] of actions) {
    await page.getByRole("button", { name: button, exact: true }).click();
    await expect(status).toHaveText(expectedStatus);
  }

  await expect(page.getByRole("button", { name: "Use light theme" })).toHaveAttribute("title", "Use light theme");
});

test(`${features("TK-PAGE-001")} composes an accessible dashboard page`, async ({ page }) => {
  const sample = page.getByTestId("toolkit-page-sample");

  await expect(sample).toHaveAttribute("aria-labelledby", "toolkit-page-sample-title");
  await expect(sample.getByRole("heading", { level: 2, name: "Sample resources" })).toHaveAttribute(
    "id",
    "toolkit-page-sample-title",
  );
  await expect(sample.getByText("3 resources", { exact: true })).toBeVisible();
  const refresh = sample.getByRole("button", { name: "Refresh sample resources" });
  await expect(refresh).toBeVisible();
  await refresh.click();
  await expect(sample.getByRole("status")).toHaveText("Refreshed 1 time");

  const toolbar = sample.getByRole("toolbar", { name: "Sample resource tools" });
  await expect(toolbar.getByRole("textbox", { name: "Filter sample resources…" })).toBeVisible();
  const body = sample.getByTestId("toolkit-page-body");
  await expect(body).toHaveCSS("overflow", "auto");
  await expect(body).toContainText("frontend");
});

test(`${features("TK-MENU-001")} exercises the command menu with pointer and keyboard`, async ({ page }, testInfo) => {
  const trigger = page.getByRole("button", { name: "Resource commands" });

  await trigger.click();
  const menu = page.getByRole("menu", { name: "Resource commands" });
  await expect(menu).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: /Start/ })).toBeDisabled();
  await expect(menu.getByRole("menuitem", { name: /Restart/ })).toContainText("Restart the current process");
  await expect(menu.getByRole("menuitem", { name: /Stop/ })).toHaveClass(/command-menu__item--danger/);
  await attachScreenshot(page, testInfo, "toolkit-command-menu");

  await page.keyboard.press("Escape");
  await expect(menu).toHaveCount(0);
  await expect(trigger).toBeFocused();

  await trigger.press("ArrowDown");
  await expect(menu).toBeVisible();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Enter");
  await expect(page.getByRole("region", { name: "Actions" }).getByRole("status")).toHaveText("Restart selected");
  await expect(menu).toHaveCount(0);

  await trigger.click();
  await menu.getByRole("menuitem", { name: /Stop/ }).click();
  await expect(page.getByRole("region", { name: "Actions" }).getByRole("status")).toHaveText("Stop selected");
});

test(`${features("TK-DIALOG-001", "TK-DRAWER-001")} exercises modal surfaces`, async ({ page }) => {
  const openConfirmation = page.getByRole("button", { name: "Confirm command" });

  await openConfirmation.click();
  await expect(page.getByRole("dialog", { name: "Restart frontend" })).toBeVisible();
  await page.getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);

  await openConfirmation.click();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);

  await openConfirmation.click();
  await page.getByRole("button", { name: "Restart", exact: true }).click();
  await expect(page.getByRole("region", { name: "Actions" }).getByRole("status")).toHaveText("Restart confirmed");

  await page.getByRole("button", { name: "Open drawer" }).click();
  const drawer = page.getByRole("dialog", { name: "Toolkit resource details" });
  await expect(drawer.getByText("frontend", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Project", { exact: true })).toBeVisible();
  await expect(drawer.getByText("Running", { exact: true })).toBeVisible();
  await expect(drawer.getByText("https://localhost:7233", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Close details" }).click();
  await expect(drawer).toHaveCount(0);

  await page.getByRole("button", { name: "Open drawer" }).click();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test(`${features("TK-NOTIFICATION-001")} exercises reusable notification actions`, async ({ page }) => {
  const showNotification = page.getByRole("button", { name: "Show notification" });
  const status = page.getByRole("region", { name: "Actions" }).getByRole("status");

  await showNotification.click();
  const notification = page.getByRole("alert");
  await expect(notification).toContainText("Toolkit notification");
  await expect(notification).toContainText("Review the unresolved sample value.");
  await expect(notification).toHaveClass(/notif--warning/);
  await notification.getByRole("button", { name: "Open documentation" }).click();
  await expect(status).toHaveText("Notification link action");
  await expect(notification).toBeVisible();
  await notification.getByRole("button", { name: "Not now" }).click();
  await expect(notification).toHaveCount(0);
  await expect(status).toHaveText("Notification secondary action");

  await showNotification.click();
  await page.getByRole("alert").getByRole("button", { name: "Review" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expect(status).toHaveText("Notification primary action");

  await showNotification.click();
  await page.getByRole("button", { name: "Dismiss notification" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expect(status).toHaveText("Notification dismissed");
});

test(`${features("TK-SELECT-001", "TK-CHECKBOX-001", "TK-SECRET-001")} exercises input and sensitive-value controls`, async ({ page }) => {
  const inputs = page.getByRole("region", { name: "Inputs" });
  const environment = inputs.getByRole("combobox", { name: "Environment" });
  await expect(environment).toHaveValue("development");
  await expect(environment.locator("option")).toHaveText([
    "Choose an environment",
    "Development",
    "Staging",
    "Production",
    "Retired",
  ]);
  await expect(environment.locator("option", { hasText: "Retired" })).toBeDisabled();
  await environment.selectOption("production");
  await expect(environment).toHaveValue("production");

  const includeHidden = inputs.getByRole("checkbox", { name: "Include hidden resources" });
  await expect(includeHidden).not.toBeChecked();
  await includeHidden.check();
  await expect(includeHidden).toBeChecked();
  await expect(inputs.getByRole("checkbox", { name: "Select all resources" })).toBeChecked({ indeterminate: true });
  await expect(inputs.getByRole("checkbox", { name: "Unavailable option" })).toBeDisabled();

  await expect(inputs.getByText("deck-secret-123", { exact: true })).toHaveCount(0);
  await inputs.getByRole("button", { name: "Reveal API key" }).click();
  await expect(inputs.getByText("deck-secret-123", { exact: true })).toBeVisible();
  await inputs.getByRole("button", { name: "Hide API key" }).click();
  await expect(inputs.getByText("deck-secret-123", { exact: true })).toHaveCount(0);
});

test(`${features("TK-TABS-001", "TK-ACCORDION-001", "TK-DIVIDER-001", "TK-HIGHLIGHT-001")} exercises navigation and disclosure controls`, async ({ page }) => {
  const region = page.getByRole("region", { name: "Navigation and disclosure" });
  const tablist = region.getByRole("tablist", { name: "Toolkit views" });
  const overviewTab = tablist.getByRole("tab", { name: "Overview" });
  const logsTab = tablist.getByRole("tab", { name: "Logs 3" });
  const overviewPanel = region.locator("#deck-tab-panel-overview");
  const logsPanel = region.locator("#deck-tab-panel-logs");

  await expect(overviewTab).toHaveAttribute("aria-selected", "true");
  await expect(overviewPanel).toHaveAttribute("role", "tabpanel");
  await expect(overviewPanel).toHaveAttribute("aria-labelledby", "deck-tab-overview");
  await expect(overviewPanel).toBeVisible();
  await expect(logsPanel).toBeHidden();
  await overviewTab.focus();
  await page.keyboard.press("ArrowRight");
  await expect(logsTab).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(logsTab).toHaveAttribute("aria-selected", "true");
  await expect(logsPanel).toBeVisible();
  await expect(overviewPanel).toBeHidden();
  await expect(overviewPanel).toHaveCount(1);

  const environment = region.getByRole("button", { name: "Environment 2" });
  const endpoints = region.getByRole("button", { name: "Endpoints 1" });
  await expect(environment).toHaveAttribute("aria-expanded", "true");
  await expect(endpoints).toHaveAttribute("aria-expanded", "false");
  await endpoints.click();
  await expect(endpoints).toHaveAttribute("aria-expanded", "true");
  await environment.click();
  await expect(environment).toHaveAttribute("aria-expanded", "false");

  await expect(region.getByRole("separator", { name: "Horizontal divider" })).toHaveAttribute("aria-orientation", "horizontal");
  await expect(region.getByRole("separator", { name: "Vertical divider" })).toHaveAttribute("aria-orientation", "vertical");
  const highlighted = region.getByTestId("toolkit-highlight");
  await expect(highlighted).toHaveText("frontend calls FrontEnd API");
  await expect(highlighted.locator("mark")).toHaveText(["frontend", "FrontEnd"]);
});

test(`${features("TK-DATA-001")} filters semantic table rows and exposes empty results`, async ({ page }) => {
  const table = page.getByRole("table");
  const search = page.getByRole("textbox", { name: "Filter toolkit resources…" });

  await expect(table.getByRole("columnheader")).toHaveText(["State", "Name", "Type"]);
  await expect(table.getByRole("row")).toHaveCount(4);

  await search.fill("catalog");
  await expect(table.getByRole("row")).toHaveCount(2);
  await expect(table).toContainText("catalog-db");
  await expect(table).not.toContainText("frontend");

  await search.fill("missing-resource");
  await expect(table.getByRole("row")).toHaveCount(2);
  await expect(table).toContainText("No matching resources.");
});

test(`${features("TK-DATA-SORT-001")} sorts and activates data rows accessibly`, async ({ page }) => {
  const table = page.getByRole("table");
  const nameHeader = table.getByRole("columnheader", { name: "Name" });
  const sortByName = nameHeader.getByRole("button", { name: "Name" });
  const names = table.locator("tbody td:nth-child(2)");

  await expect(sortByName).toHaveCSS("text-transform", "uppercase");
  await sortByName.click();
  await expect(nameHeader).toHaveAttribute("aria-sort", "ascending");
  await expect(names).toHaveText(["catalog-db", "frontend", "migration"]);

  await sortByName.click();
  await expect(nameHeader).toHaveAttribute("aria-sort", "descending");
  await expect(names).toHaveText(["migration", "frontend", "catalog-db"]);

  const frontend = table.getByRole("row", { name: /frontend/ });
  await expect(frontend).toHaveAttribute("aria-selected", "false");
  await frontend.focus();
  await frontend.press("Enter");
  await expect(frontend).toHaveAttribute("aria-selected", "true");
  await expect(page.getByRole("region", { name: "Actions" }).getByRole("status")).toHaveText("frontend selected");
});

test(`${features("TK-SHELL-001")} switches and persists a readable Fluent theme`, async ({ page }) => {
  const root = page.locator("html");
  const secondary = page.getByRole("button", { name: "Secondary", exact: true });

  await expect(root).toHaveAttribute("data-theme", "dark");
  await expect(page.getByRole("button", { name: "Use light theme" })).toBeVisible();
  expect(await contrastRatio(secondary)).toBeGreaterThanOrEqual(4.5);

  await page.getByRole("button", { name: "Use light theme" }).click();
  await expect(root).toHaveAttribute("data-theme", "light");
  await expect(page.getByRole("button", { name: "Use dark theme" })).toBeVisible();
  expect(await contrastRatio(secondary)).toBeGreaterThanOrEqual(4.5);

  await page.reload();
  await expect(root).toHaveAttribute("data-theme", "light");
});

test(`${features("TK-RESPONSIVE-001")} contains the toolkit at desktop and mobile widths`, async ({ page }, testInfo) => {
  await attachScreenshot(page, testInfo, "toolkit-dark-desktop");

  await page.getByRole("button", { name: "Use light theme" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await attachScreenshot(page, testInfo, "toolkit-light-desktop");

  await page.setViewportSize({ width: 390, height: 844 });
  const geometry = await page.evaluate(() => {
    const table = document.querySelector<HTMLElement>(".table-wrap");
    if (!table) {
      throw new Error("Toolkit table was not rendered.");
    }
    return {
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: window.innerWidth,
      tableClientWidth: table.clientWidth,
      tableScrollWidth: table.scrollWidth,
    };
  });
  expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
  expect(geometry.tableScrollWidth).toBeGreaterThan(geometry.tableClientWidth);
  await attachScreenshot(page, testInfo, "toolkit-light-mobile");

  await page.getByRole("button", { name: "Open drawer" }).click();
  const bounds = await page.getByRole("dialog", { name: "Toolkit resource details" }).boundingBox();
  expect(bounds).not.toBeNull();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.width).toBeCloseTo(390, 2);
  await attachScreenshot(page, testInfo, "toolkit-light-mobile-drawer");
});

const missingFeatures = getMissingToolkitFeatures(coveredFeatures);
if (missingFeatures.length > 0) {
  throw new Error(`Toolkit features without Playwright coverage: ${missingFeatures.join(", ")}`);
}
