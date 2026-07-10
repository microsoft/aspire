import { expect, test, type Page, type TestInfo } from "@playwright/test";
import {
  getMissingDashboardCoreFeatures,
  type DashboardCoreFeatureId,
} from "./dashboard-core-features";

const coveredFeatures = new Set<DashboardCoreFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();

function features(...ids: DashboardCoreFeatureId[]): string {
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
  await expect(page.getByRole("table")).toBeVisible();
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
});

test(`${features("APP-BROWSER-001", "APP-SHELL-001", "APP-CONNECTION-001")} renders the connected dashboard shell`, async ({ page }) => {
  const navigation = page.getByRole("navigation");
  await expect(navigation).toContainText("Aspire DeckDistributed app dashboardObserve");
  await expect(navigation).toContainText("Aspire Deck 9.0.0-dev (mock)");
  await expect(navigation).toMatchAriaSnapshot(`
    - navigation:
      - button /Resources \\d+/
      - button /Parameters \\d+/
      - button "Console"
      - button /Structured Logs \\d+/
      - button /Traces \\d+/
      - button /Metrics \\d+/
      - button "Canvases"
  `);
  await expect(navigationButton(page, "Resources")).toHaveAttribute("aria-current", "page");
  await expect(page.getByRole("banner").locator(".topbar__app")).toHaveText("TestShop");
  await expect(page.getByTitle("Resources: Connected")).toBeVisible();
  await expect(page.getByTitle("OTLP gRPC: Connected")).toBeVisible();
  await expect(page.getByTitle("OTLP HTTP: Connected")).toBeVisible();
});

test(`${features("APP-NAV-001", "APP-APPHOST-001", "APP-THEME-001")} navigates, switches AppHosts, and persists theme`, async ({ page }) => {
  const pages = [
    ["Parameters", "Parameters"],
    ["Console", "Console"],
    ["Structured Logs", "Structured Logs"],
    ["Traces", "Traces"],
    ["Metrics", "Metrics"],
    ["Canvases", "Canvases"],
    ["Resources", "Resources"],
  ] as const;

  for (const [button, title] of pages) {
    await navigationButton(page, button).click();
    await expect(page.getByRole("main").locator(".page__title")).toHaveText(title);
    await expect(navigationButton(page, button)).toHaveAttribute("aria-current", "page");
  }

  await page.getByRole("button", { name: /^TestShop 2$/ }).click();
  const listbox = page.getByRole("listbox");
  await expect(listbox.getByRole("option")).toHaveCount(2);
  await expect(listbox.getByRole("option", { name: "TestShop" })).toHaveAttribute("aria-selected", "true");
  await listbox.getByRole("option", { name: "OrdersService" }).getByRole("button").click();
  await expect(page.getByRole("banner").locator(".topbar__app")).toHaveText("OrdersService");
  await expect(page.getByRole("banner").locator(".topbar__app-sub")).toHaveText("https://localhost:18055");

  const root = page.locator("html");
  await expect(root).toHaveAttribute("data-theme", "dark");
  await page.getByRole("button", { name: "Toggle theme" }).click();
  await expect(root).toHaveAttribute("data-theme", "light");
  await page.reload();
  await expect(root).toHaveAttribute("data-theme", "light");
});

test(`${features("APP-PAGE-001")} composes every route from the page toolkit`, async ({ page }) => {
  const pages = [
    { navigation: "Resources", title: "Resources", toolbar: "Resource tools" },
    { navigation: "Parameters", title: "Parameters", toolbar: "Parameter tools" },
    { navigation: "Console", title: "Console", toolbar: "Console tools" },
    { navigation: "Structured Logs", title: "Structured Logs", toolbar: "Structured log tools" },
    { navigation: "Traces", title: "Traces", toolbar: "Trace tools" },
    { navigation: "Metrics", title: "Metrics" },
    { navigation: "Canvases", title: "Canvases" },
  ] as const;

  for (const item of pages) {
    if (item.navigation !== "Resources") {
      await navigationButton(page, item.navigation).click();
    }

    const route = page.getByRole("main").getByRole("region", { name: item.title });
    await expect(route).toBeVisible();
    await expect(route.getByRole("heading", { level: 1, name: item.title })).toBeVisible();
    await expect(route.locator(":scope > .page__body")).toHaveCount(1);

    if ("toolbar" in item) {
      await expect(route.getByRole("toolbar", { name: item.toolbar })).toBeVisible();
    } else {
      await expect(route.getByRole("toolbar")).toHaveCount(0);
    }
  }
});

test(`${features("APP-NOTIFICATION-001")} completes every notification action`, async ({ page }) => {
  const alert = page.getByRole("alert");
  await expect(alert).toContainText("Unresolved parameters");
  await alert.getByRole("button", { name: "No", exact: true }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);

  await page.reload();
  await page.getByRole("alert").getByRole("button", { name: "Enter values" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);

  await page.reload();
  await page.getByRole("button", { name: "Dismiss notification" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
});

test(`${features("RES-LIST-001", "RES-SORT-001", "RES-FILTER-001", "RES-ENDPOINT-001")} lists, sorts, and filters resource endpoints`, async ({ page }) => {
  const table = page.getByRole("table");
  const rows = table.getByRole("row");
  await expect(rows).toHaveCount(6);
  await expect(rows.nth(1)).toContainText("apiservice");
  await expect(rows.nth(2)).toContainText("cache");
  await expect(rows.nth(3)).toContainText("frontend");
  await expect(rows.nth(4)).toContainText("migration");
  await expect(rows.nth(5)).toContainText("postgres");
  await expect(table).not.toContainText("hiddenContainer");
  await expect(table).not.toContainText("apikey");

  const nameHeader = table.getByRole("columnheader", { name: "Name" });
  const sortByName = nameHeader.getByRole("button", { name: "Name" });
  await expect(nameHeader).toHaveAttribute("aria-sort", "ascending");
  await sortByName.click();
  await expect(nameHeader).toHaveAttribute("aria-sort", "descending");
  await expect(table.locator("tbody td:nth-child(2)")).toHaveText([
    "postgres",
    "migration",
    "frontend",
    "cache",
    "apiservice",
  ]);
  await sortByName.click();
  await expect(nameHeader).toHaveAttribute("aria-sort", "ascending");

  const endpoint = page.getByRole("link", { name: "https://localhost:7233" });
  await expect(endpoint).toHaveAttribute("href", "https://localhost:7233");
  await endpoint.focus();
  await expect(page.getByRole("dialog")).toHaveCount(0);

  const search = page.getByRole("textbox", { name: "Filter by name, type or state…" });
  await search.fill("project");
  await expect(rows).toHaveCount(3);
  await expect(table).toContainText("apiservice");
  await expect(table).toContainText("frontend");

  await search.fill("Exited");
  await expect(rows).toHaveCount(2);
  await expect(table).toContainText("migration");

  await search.fill("does-not-exist");
  await expect(table).toContainText("No resources match your filter.");
});

test(`${features("RES-DETAILS-001", "RES-SECRETS-001")} inspects resource details with secure defaults`, async ({ page }) => {
  await page.getByRole("row", { name: /frontend Project/ }).click();
  const dialog = page.getByRole("dialog", { name: "frontend" });
  for (const section of ["Overview", "Endpoints", "Properties", "Environment variables", "Health reports", "Relationships"]) {
    await expect(dialog.getByText(section, { exact: true })).toBeVisible();
  }
  await expect(dialog).toContainText("uid-frontend");
  await expect(dialog).toContainText("apiserviceReference");
  await expect(dialog.getByText("Development", { exact: true })).toHaveCount(0);
  await expect(dialog.getByText("p@ssw0rd-redis", { exact: false })).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Reveal value" })).toHaveCount(4);

  await dialog.getByRole("button", { name: "Reveal value" }).first().click();
  await expect(dialog.getByText("Development", { exact: true })).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Hide value" })).toHaveCount(1);
});

test(`${features("RES-COMMANDS-001", "RES-ACTION-MENU-001", "RES-CONFIRM-001")} confirms commands and updates live resource state`, async ({ page }) => {
  await page.getByRole("row", { name: /frontend Project/ }).click();
  const details = page.getByRole("dialog", { name: "frontend" });
  await expect(details.getByRole("button", { name: "Restart", exact: true })).toBeEnabled();
  const commands = details.getByRole("button", { name: "Resource commands" });
  await commands.click();
  let menu = page.getByRole("menu", { name: "Resource commands" });
  await expect(menu.getByRole("menuitem", { name: /Start/ })).toBeDisabled();
  await expect(menu.getByRole("menuitem", { name: /Stop/ })).toBeEnabled();
  await expect(menu.getByRole("menuitem", { name: /Scale/ })).toBeEnabled();
  const [drawerBounds, menuBounds] = await Promise.all([details.boundingBox(), menu.boundingBox()]);
  expect(drawerBounds).not.toBeNull();
  expect(menuBounds).not.toBeNull();
  expect(menuBounds!.x).toBeGreaterThanOrEqual(drawerBounds!.x);
  expect(menuBounds!.x + menuBounds!.width).toBeLessThanOrEqual(drawerBounds!.x + drawerBounds!.width);

  await menu.getByRole("menuitem", { name: /Stop/ }).click();
  const confirmation = page.getByRole("dialog", { name: "Stop" });
  await expect(confirmation).toContainText("Are you sure you want to stop this resource?");
  await confirmation.getByRole("button", { name: "Stop", exact: true }).click();
  await expect(page.getByRole("status")).toHaveText("Stop succeeded");
  await expect(details).toContainText("Exited");
  await expect(details.getByRole("button", { name: "Start", exact: true })).toBeEnabled();
  await commands.click();
  menu = page.getByRole("menu", { name: "Resource commands" });
  await expect(menu.getByRole("menuitem", { name: /Stop/ })).toBeDisabled();
  await expect(menu.getByRole("menuitem", { name: /Restart/ })).toBeDisabled();
  await page.keyboard.press("Escape");

  await details.getByRole("button", { name: "Start", exact: true }).click();
  await expect(page.getByRole("status")).toHaveText("Start succeeded");
  await expect(details).toContainText("Running");
});

test(`${features("RES-INTERACTION-001")} validates and submits an input command`, async ({ page }) => {
  await page.getByRole("row", { name: /frontend Project/ }).click();
  await page.getByRole("dialog", { name: "frontend" }).getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menu", { name: "Resource commands" }).getByRole("menuitem", { name: /Scale/ }).click();
  const interaction = page.getByRole("dialog", { name: "Scale resource" });
  const replicas = interaction.getByRole("spinbutton", { name: "Replicas" });
  const tier = interaction.getByRole("combobox", { name: "Tier" });
  const drain = interaction.getByRole("checkbox", { name: "Drain connections before scaling down" });

  await expect(replicas).toHaveValue("1");
  await expect(tier).toHaveValue("standard");
  await expect(drain).toBeChecked();
  await replicas.fill("0");
  await expect(interaction).toContainText("Replicas must be a whole number between 1 and 10.");
  await expect(replicas).toHaveValue("0");

  await replicas.fill("3");
  await tier.selectOption("premium");
  await drain.uncheck();
  await interaction.getByRole("button", { name: "Scale", exact: true }).click();
  await expect(interaction).toHaveCount(0);
});

test(`${features("PARAM-LIST-001", "PARAM-SORT-001", "PARAM-FILTER-001", "PARAM-SECRET-001")} sorts, filters, and reveals parameter values`, async ({ page }) => {
  await navigationButton(page, "Parameters").click();
  const table = page.getByRole("table");
  await expect(table.getByRole("row")).toHaveCount(4);
  await expect(table).toContainText("Not set");
  await expect(table).toContainText("1000");
  await expect(table).not.toContainText("sk-9f2b7c1e4a8d");

  const nameHeader = table.getByRole("columnheader", { name: "Name" });
  const sortByName = nameHeader.getByRole("button", { name: "Name" });
  await expect(nameHeader).toHaveAttribute("aria-sort", "ascending");
  await sortByName.click();
  await expect(table.locator("tbody td:nth-child(2)")).toHaveText(["insertionrows", "greeting", "apikey"]);
  await sortByName.click();

  await table.getByRole("button", { name: "Reveal value" }).click();
  await expect(table).toContainText("sk-9f2b7c1e4a8d");
  await expect(page.getByRole("dialog")).toHaveCount(0);

  const search = page.getByRole("textbox", { name: "Filter by name or state…" });
  await search.fill("ValueMissing");
  await expect(table.getByRole("row")).toHaveCount(2);
  await expect(table).toContainText("greeting");
  await search.clear();
  await table.getByRole("row", { name: /greeting Not set/ }).click();
  await expect(page.getByRole("dialog", { name: "greeting" })).toBeVisible();
});

test(`${features("APP-RESPONSIVE-001")} keeps core workflows usable on mobile`, async ({ page }, testInfo) => {
  await page.getByRole("button", { name: "Dismiss notification" }).click();
  await page.setViewportSize({ width: 390, height: 844 });

  const geometry = await page.evaluate(() => {
    const box = (selector: string): DOMRect => {
      const element = document.querySelector(selector);
      if (!element) {
        throw new Error(`Missing element '${selector}'.`);
      }
      return element.getBoundingClientRect();
    };
    const navigation = box(".sidebar");
    const banner = box(".topbar");
    const main = box(".app__content");
    return {
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: innerWidth,
      viewportHeight: innerHeight,
      navigation: { x: navigation.x, y: navigation.y, width: navigation.width, height: navigation.height },
      banner: { x: banner.x, width: banner.width },
      main: { x: main.x, width: main.width },
    };
  });

  expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
  expect(geometry.navigation.y).toBeGreaterThanOrEqual(geometry.viewportHeight - 72);
  expect(geometry.navigation.width).toBe(390);
  expect(geometry.banner).toEqual({ x: 0, width: 390 });
  expect(geometry.main).toEqual({ x: 0, width: 390 });
  await expect(page.getByRole("main").locator(".table-wrap")).toBeVisible();

  await page.getByRole("row", { name: /frontend Project/ }).click();
  const drawer = page.getByRole("dialog", { name: "frontend" });
  await expect.poll(async () => (await drawer.boundingBox())?.x).toBe(0);
  const bounds = await drawer.boundingBox();
  expect(bounds).not.toBeNull();
  expect(bounds!.x).toBe(0);
  expect(bounds!.width).toBe(390);
  await attachScreenshot(page, testInfo, "dashboard-core-mobile");
});

const missingFeatures = getMissingDashboardCoreFeatures(coveredFeatures);
if (missingFeatures.length > 0) {
  throw new Error(`Dashboard core features without Playwright coverage: ${missingFeatures.join(", ")}`);
}
