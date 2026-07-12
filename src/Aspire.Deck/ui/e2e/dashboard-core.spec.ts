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

test(`${features("APP-REPOSITORY-001", "APP-HELP-001", "APP-SETTINGS-001", "APP-KEYBOARD-001")} exposes shell utilities and keyboard navigation`, async ({ page }, testInfo) => {
  const banner = page.getByRole("banner");
  const repository = banner.getByRole("link", { name: "Aspire repository" });
  await expect(repository).toHaveAttribute("href", "https://aka.ms/aspire/repo");
  await expect(repository).toHaveAttribute("target", "_blank");
  await expect(repository).toHaveAttribute("rel", /noopener/);
  await expect(banner.locator("[data-icon-fallback]")).toHaveCount(0);

  await banner.getByRole("button", { name: "Help" }).click();
  let dialog = page.getByRole("dialog", { name: "Help" });
  await expect(dialog.getByRole("link", { name: "Aspire dashboard documentation" })).toHaveAttribute("href", "https://aka.ms/aspire/dashboard");
  await expect(dialog.getByRole("heading", { name: "Keyboard shortcuts" })).toBeVisible();
  await expect(dialog.locator("kbd")).toHaveText(["R", "C", "S", "T", "M", "?", "Shift + S"]);
  await dialog.getByRole("button", { name: "Close" }).click();

  await page.keyboard.press("?");
  dialog = page.getByRole("dialog", { name: "Help" });
  await expect(dialog).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(dialog).toBeHidden();

  const shortcuts = [["m", "Metrics"], ["c", "Console"], ["s", "Structured Logs"], ["t", "Traces"], ["r", "Resources"]] as const;
  for (const [key, title] of shortcuts) {
    await page.keyboard.press(key);
    await expect(page.getByRole("main").locator(".page__title")).toHaveText(title);
  }
  const search = page.getByPlaceholder("Filter by name, type or state…");
  await search.fill("frontend");
  await search.press("m");
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Resources");
  await search.fill("");

  await banner.locator(".topbar__title").click();
  await page.keyboard.press("Shift+S");
  const settings = page.getByRole("dialog", { name: "Settings" });
  await expect(settings).toContainText("Dashboard version9.0.0-dev (mock)");
  await expect(settings).toContainText("Runtime versionBrowser mock runtime");
  await settings.getByRole("radio", { name: "System" }).check();
  await expect.poll(() => page.evaluate(() => localStorage.getItem("aspire-deck-theme"))).toBe("system");
  await settings.getByRole("radio", { name: "Light" }).check();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await settings.getByRole("radio", { name: "Dark" }).check();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await attachScreenshot(page, testInfo, "dashboard-settings");
  await settings.getByRole("button", { name: "Close" }).click();
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
});

test(`${features("APP-ROUTES-001")} restores page routes and browser history`, async ({ page }) => {
  await navigationButton(page, "Structured Logs").click();
  await expect(page).toHaveURL(/\/structuredlogs$/);

  await page.reload();
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Structured Logs");
  await expect(navigationButton(page, "Structured Logs")).toHaveAttribute("aria-current", "page");

  await navigationButton(page, "Traces").click();
  await expect(page).toHaveURL(/\/traces$/);
  await page.goBack();
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Structured Logs");
  await expect(page).toHaveURL(/\/structuredlogs$/);
  await page.goForward();
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Traces");

  await page.goto("/metrics");
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Metrics");
  await expect(navigationButton(page, "Metrics")).toHaveAttribute("aria-current", "page");
});

test(`${features("APP-NOTFOUND-001", "APP-ERROR-001")} renders recoverable error routes`, async ({ page }, testInfo) => {
  await page.goto("/does-not-exist");
  const notFound = page.getByRole("main").getByRole("region", { name: "Page not found" });
  await expect(notFound).toContainText("404Page not foundThe requested dashboard page does not exist.");
  await notFound.getByRole("button", { name: "Go to resources" }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Resources");

  await page.goto("/error");
  const error = page.getByRole("main").getByRole("region", { name: "Dashboard error" });
  await expect(error).toContainText("Something went wrongThe dashboard could not complete this request.");
  await expect(error.getByRole("button")).toHaveText(["Go to resources", "Reload dashboard"]);
  await attachScreenshot(page, testInfo, "dashboard-error");
});

test(`${features("APP-PAGE-001")} composes every route from the page toolkit`, async ({ page }) => {
  const pages = [
    { navigation: "Resources", title: "Resources", toolbar: "Resource tools" },
    { navigation: "Parameters", title: "Parameters", toolbar: "Parameter tools" },
    { navigation: "Console", title: "Console", toolbar: "Console tools" },
    { navigation: "Structured Logs", title: "Structured Logs", toolbar: "Structured log tools" },
    { navigation: "Traces", title: "Traces", toolbar: "Trace tools" },
    { navigation: "Metrics", title: "Metrics", toolbar: "Metric tools" },
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

test(`${features("APP-NOTIFICATION-001", "PARAM-NOTIFICATION-001")} completes every notification action`, async ({ page }) => {
  const alert = page.getByRole("alert");
  await expect(alert).toContainText("Unresolved parameters");
  await alert.getByRole("button", { name: "No", exact: true }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);

  await page.reload();
  await page.getByRole("alert").getByRole("button", { name: "Enter values" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await expect(page).toHaveURL(/\/parameters$/);
  await expect(page.getByRole("heading", { level: 1, name: "Parameters" })).toBeVisible();

  await page.reload();
  await page.getByRole("button", { name: "Dismiss notification" }).click();
  await expect(page.getByRole("alert")).toHaveCount(0);
});

test(`${features("RES-LIST-001", "RES-SORT-001", "RES-FILTER-001", "RES-ENDPOINT-001")} lists, sorts, and filters resource endpoints`, async ({ page }) => {
  const table = page.getByRole("table");
  const rows = table.getByRole("row");
  await expect(rows).toHaveCount(6);
  await expect(rows.nth(1)).toContainText("apiservice");
  await expect(rows.nth(2)).toContainText("frontend");
  await expect(rows.nth(3)).toContainText("migration");
  await expect(rows.nth(4)).toContainText("postgres");
  await expect(rows.nth(5)).toContainText("cache");
  await expect(table).not.toContainText("hiddenContainer");
  await expect(table).not.toContainText("apikey");

  const nameHeader = table.getByRole("columnheader", { name: "Name" });
  const sortByName = nameHeader.getByRole("button", { name: "Name" });
  await expect(nameHeader).toHaveAttribute("aria-sort", "ascending");
  await sortByName.click();
  await expect(nameHeader).toHaveAttribute("aria-sort", "descending");
  await expect(page).toHaveURL(/sortDirection=descending/);
  await expect(table.locator("tbody td:first-child")).toContainText([
    "postgres",
    "cache",
    "migration",
    "frontend",
    "apiservice",
  ]);
  await page.reload();
  await expect(nameHeader).toHaveAttribute("aria-sort", "descending");
  await sortByName.click();
  await expect(nameHeader).toHaveAttribute("aria-sort", "ascending");

  const endpoint = table.getByRole("row", { name: /frontend/ }).getByRole("link", { name: "https", exact: true });
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

test(`${features("RES-STRUCTURED-FILTER-001", "RES-VIEW-OPTIONS-001", "RES-HIERARCHY-001", "RES-SOURCE-001", "RES-DETAILS-LINK-001")} restores resource filters, hierarchy, view options, sources, and details`, async ({ page }, testInfo) => {
  test.slow();
  const table = page.getByRole("table");
  const rows = table.getByRole("row");
  await expect(table).toContainText("Frontend.csproj");
  await expect(table).toContainText("docker.io/library/postgres:17.2");

  const postgres = table.getByRole("row", { name: /postgres/ });
  await expect(postgres.getByRole("button", { name: "Collapse postgres" })).toHaveAttribute("aria-expanded", "true");
  const cache = table.getByRole("row", { name: /cache/ });
  const postgresBox = await postgres.boundingBox();
  const cacheBox = await cache.boundingBox();
  expect(postgresBox).not.toBeNull();
  expect(cacheBox).not.toBeNull();
  expect(cacheBox!.y).toBeGreaterThan(postgresBox!.y);
  expect(await cache.locator(".resource-name").evaluate((element) => getComputedStyle(element).paddingInlineStart)).toBe("22px");

  await postgres.getByRole("button", { name: "Collapse postgres" }).click();
  await expect(page).toHaveURL(/collapsed=postgres/);
  await expect(table.getByRole("row", { name: /cache/ })).toHaveCount(0);
  await page.reload();
  await expect(table.getByRole("row", { name: /cache/ })).toHaveCount(0);

  await page.getByRole("button", { name: "Resource view options" }).click();
  await page.getByRole("menuitem", { name: "Expand all children" }).click();
  await expect(table.getByRole("row", { name: /cache/ })).toBeVisible();

  await page.getByRole("button", { name: "Resource view options" }).click();
  await page.getByRole("menuitem", { name: "Show resource types" }).click();
  await expect(table.getByRole("columnheader", { name: "Type" })).toBeVisible();
  await page.getByRole("button", { name: "Resource view options" }).click();
  await page.getByRole("menuitem", { name: "Show hidden resources" }).click();
  await expect(table).toContainText("hiddenContainer");
  await expect(page).toHaveURL(/showHiddenResources=true/);

  await page.getByRole("button", { name: "Resource filters" }).click();
  const filters = page.locator(".filter-menu");
  await filters.getByRole("checkbox", { name: "Container" }).uncheck();
  await filters.getByRole("checkbox", { name: "Running" }).uncheck();
  await filters.getByRole("checkbox", { name: "Healthy" }).uncheck();
  await expect(page).toHaveURL(/hiddenType=Container/);
  await expect(page).toHaveURL(/hiddenState=Running/);
  await expect(page).toHaveURL(/hiddenHealth=Healthy/);
  await filters.getByRole("button", { name: "Clear" }).click();
  await expect(page).not.toHaveURL(/hidden(Type|State|Health)=/);
  await filters.getByRole("button", { name: "Done" }).click();

  await page.getByRole("row", { name: /frontend/ }).click();
  await expect(page).toHaveURL(/resource=frontend/);
  await expect(page.getByRole("dialog", { name: "frontend" })).toBeVisible();
  await page.reload();
  const details = page.getByRole("dialog", { name: "frontend" });
  await expect(details).toBeVisible();
  await attachScreenshot(page, testInfo, "dashboard-resource-table-state");
  await details.getByRole("button", { name: "Close" }).click();
  await expect(page).not.toHaveURL(/resource=frontend/);

  const search = page.getByRole("textbox", { name: "Filter by name, type or state…" });
  await search.fill("project");
  await expect(page).toHaveURL(/q=project/);
  await page.reload();
  await expect(search).toHaveValue("project");
  await expect(rows).toHaveCount(3);
});

test(`${features("RES-ICON-001")} renders resource and command icon contracts`, async ({ page }) => {
  const table = page.getByRole("table");
  const frontend = table.getByRole("row", { name: /frontend/ });
  const cache = table.getByRole("row", { name: /cache/ });
  await expect(frontend.locator('svg[data-icon-name="Window"][data-icon-variant="regular"]')).toHaveCount(1);
  await expect(cache.locator('svg[data-icon-name="Database"][data-icon-variant="filled"]')).toHaveCount(1);

  await frontend.click();
  const details = page.getByRole("dialog", { name: "frontend" });
  await expect(details.locator('svg[data-icon-name="Window"][data-icon-variant="regular"]')).toHaveCount(1);
  await expect(
    details.getByRole("button", { name: "Restart", exact: true })
      .locator('svg[data-icon-name="ArrowClockwise"][data-icon-variant="regular"]'),
  ).toHaveCount(1);

  await details.getByRole("button", { name: "Resource commands" }).click();
  const menu = page.getByRole("menu", { name: "Resource commands" });
  await expect(
    menu.getByRole("menuitem", { name: /Start/ })
      .locator('svg[data-icon-name="Play"][data-icon-variant="filled"]'),
  ).toHaveCount(1);
  await expect(
    menu.getByRole("menuitem", { name: /Stop/ })
      .locator('svg[data-icon-name="Stop"][data-icon-variant="filled"]'),
  ).toHaveCount(1);
});

test(`${features("RES-GRAPH-001", "RES-GRAPH-ZOOM-001", "RES-GRAPH-CONTEXT-001")} explores the resource relationship graph`, async ({ page }, testInfo) => {
  await page.goto("/?view=Graph");
  await page.getByRole("alert").getByRole("button", { name: "Dismiss notification" }).click();
  await expect(page.getByRole("button", { name: "Graph view" })).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("table")).toHaveCount(0);
  const graph = page.getByRole("group", { name: "Resource graph" });
  await expect(graph).toBeVisible();
  await expect(graph.locator("[data-node-id]")).toHaveCount(5);
  await expect(graph.locator(".force-graph__edge")).toHaveCount(5);
  await expect(graph.locator("[data-icon-name]")).toHaveCount(5);
  await expect(graph).toHaveAttribute("data-zoom", "1");

  await page.getByRole("button", { name: "Zoom in" }).click();
  await expect(graph).toHaveAttribute("data-zoom", "1.5");
  await page.getByRole("button", { name: "Zoom out" }).click();
  await expect(graph).toHaveAttribute("data-zoom", "1");
  await page.getByRole("button", { name: "Zoom in" }).click();
  await page.getByRole("button", { name: "Reset view" }).click();
  await expect(graph).toHaveAttribute("data-zoom", "1");

  const frontend = graph.locator('[data-node-id="frontend"]');
  await frontend.click();
  await expect(page).toHaveURL(/view=Graph/);
  await expect(page).toHaveURL(/resource=frontend/);
  await expect(page.getByRole("dialog", { name: "frontend" })).toBeVisible();
  await page.getByRole("dialog", { name: "frontend" }).getByRole("button", { name: "Close" }).click();

  await frontend.click({ button: "right" });
  const contextMenu = page.getByRole("menu", { name: "Resource actions" });
  await expect(contextMenu.getByRole("menuitem")).toHaveText(["View details", "Start", "Stop", "Restart", "Scale…"]);
  await expect(contextMenu.getByRole("menuitem", { name: "Start", exact: true })).toBeDisabled();
  await contextMenu.getByRole("menuitem", { name: "View details" }).click();
  await expect(page.getByRole("dialog", { name: "frontend" })).toBeVisible();
  await attachScreenshot(page, testInfo, "dashboard-resource-graph");
  await page.getByRole("dialog", { name: "frontend" }).getByRole("button", { name: "Close" }).click();

  await page.reload();
  await expect(page.getByRole("group", { name: "Resource graph" })).toBeVisible();
  await page.getByRole("button", { name: "Table view" }).click();
  await expect(page).not.toHaveURL(/view=Graph/);
  await expect(page.getByRole("table")).toBeVisible();
});

test(`${features("RES-DETAILS-001", "RES-SECRETS-001")} inspects resource details with secure defaults`, async ({ page }) => {
  await page.getByRole("row", { name: /frontend/ }).click();
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

test(`${features("RES-PROPERTIES-001", "RES-COPY-001", "RES-CONTEXT-MENU-001")} copies complete resource values and opens row context actions`, async ({ page }, testInfo) => {
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  const frontend = page.getByRole("table").getByRole("row", { name: /frontend/ });
  await frontend.click({ button: "right" });
  let menu = page.getByRole("menu", { name: "Resource actions" });
  await expect(menu.getByRole("menuitem")).toHaveText(["View details", "Start", "Stop", "Restart", "Scale…"]);
  await menu.getByRole("menuitem", { name: "View details" }).click();

  const details = page.getByRole("dialog", { name: "frontend" });
  await expect(details).toContainText('Deployment metadata{"region":"west","replicas":2}');
  await expect(details).toContainText('Feature flags["catalog","checkout"]');
  await expect(details).toContainText("Optional ownernull");
  await expect(details.locator(".kv__val.highlight")).toContainText("src/TestShop/Frontend/Frontend.csproj");

  await details.getByRole("button", { name: "Copy Project path" }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe("src/TestShop/Frontend/Frontend.csproj");
  await expect(details.getByText("Project path copied", { exact: true })).toBeAttached();
  await expect(details.getByRole("button", { name: "Copy ASPNETCORE_ENVIRONMENT" })).toHaveCount(0);
  await details.getByRole("button", { name: "Reveal value" }).first().click();
  await details.getByRole("button", { name: "Copy ASPNETCORE_ENVIRONMENT" }).click();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe("Development");
  await attachScreenshot(page, testInfo, "dashboard-resource-values");
  await details.getByRole("button", { name: "Close" }).click();

  const cache = page.getByRole("table").getByRole("row", { name: /cache/ });
  await cache.focus();
  await cache.press("Shift+F10");
  menu = page.getByRole("menu", { name: "Resource actions" });
  await expect(menu).toBeVisible();
  await menu.getByRole("menuitem", { name: "View details" }).click();
  await expect(page.getByRole("dialog", { name: "cache" })).toBeVisible();
});

test(`${features("RES-NO-STATUS-001", "RES-LONG-URLS-001")} contains unknown resources and large endpoint sets`, async ({ page }, testInfo) => {
  await page.goto("/?showHiddenResources=true");
  const table = page.getByRole("table");
  const hidden = table.getByRole("row", { name: /hiddenContainer/ });
  await expect(hidden).toContainText("Unknown");
  await expect(hidden.getByRole("link")).toHaveText(["admin", "diagnostics-with-a-very-long-display-name", "metrics"]);
  await expect(hidden).not.toContainText("internal");
  await expect(hidden).not.toContainText("inactive");
  await expect(hidden.getByRole("link", { name: "diagnostics-with-a-very-long-display-name" })).toHaveAttribute(
    "title",
    "https://hidden.example.test/diagnostics/this/is/a/very/long/path/that/must/not/expand/the/resource/table",
  );
  const [bodyBounds, tableBounds] = await Promise.all([page.locator(".page__body").boundingBox(), page.locator(".table-wrap").boundingBox()]);
  expect(bodyBounds).not.toBeNull();
  expect(tableBounds).not.toBeNull();
  expect(tableBounds!.x + tableBounds!.width).toBeLessThanOrEqual(bodyBounds!.x + bodyBounds!.width);
  await attachScreenshot(page, testInfo, "dashboard-resource-long-urls");
});

test(`${features("RES-COMMANDS-001", "RES-ACTION-MENU-001", "RES-CONFIRM-001")} confirms commands and updates live resource state`, async ({ page }) => {
  await page.getByRole("row", { name: /frontend/ }).click();
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

test(`${features("RES-INTERACTION-001", "CMD-CUSTOM-CHOICE-001", "CMD-DYNAMIC-001", "CMD-LIVE-VALIDATION-001", "CMD-VALIDATION-001")} validates and submits an input command`, async ({ page }) => {
  await page.getByRole("row", { name: /frontend/ }).click();
  await page.getByRole("dialog", { name: "frontend" }).getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menu", { name: "Resource commands" }).getByRole("menuitem", { name: /Scale/ }).click();
  const interaction = page.getByRole("dialog", { name: "Scale resource" });
  const replicas = interaction.getByRole("spinbutton", { name: "Replicas" });
  const tier = interaction.getByRole("combobox", { name: "Tier" });
  const region = interaction.getByRole("combobox", { name: "Region" });
  const drain = interaction.getByRole("checkbox", { name: "Drain connections before scaling down" });

  await expect(replicas).toHaveValue("1");
  await expect(tier).toHaveValue("Standard");
  await expect(region).toHaveValue("US East");
  await expect(drain).toBeChecked();
  await replicas.fill("0");
  const validationSummary = interaction.getByRole("alert");
  await expect(validationSummary).toContainText("Replicas: Replicas must be a whole number between 1 and 10.");
  await expect(replicas).toHaveAttribute("aria-invalid", "true");
  await expect(replicas).toHaveAttribute("aria-describedby", /int-replicas-description int-replicas-errors/);
  await expect(replicas).toHaveValue("0");

  await replicas.fill("3");
  await drain.uncheck();
  await tier.click();
  await tier.press("ArrowDown");
  await page.getByRole("option", { name: "Premium", exact: true }).click();
  await expect(region).toBeDisabled();
  await expect(region).toHaveAttribute("placeholder", "Loading regions…");
  await expect(region).toBeEnabled();
  await expect(region).toHaveValue("Global");
  await tier.fill("private-tier");
  await expect(tier).toHaveValue("private-tier");
  await expect(region).toBeEnabled();
  await expect(region).toHaveValue("US East");
  const submit = interaction.getByRole("button", { name: "Scale", exact: true });
  await submit.focus();
  await page.keyboard.press("Enter");
  await expect(interaction).toHaveCount(0);
});

test(`${features("CMD-MANY-INPUTS-001")} scrolls and submits a 50-field command form`, async ({ page }) => {
  await page.getByRole("row", { name: /apiservice/ }).click();
  const details = page.getByRole("dialog", { name: "apiservice" });
  await details.getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menu", { name: "Resource commands" }).getByRole("menuitem", { name: /Many inputs/ }).click();

  const interaction = page.getByRole("dialog", { name: "Many inputs" });
  const inputs = interaction.getByRole("textbox");
  await expect(inputs).toHaveCount(50);
  const geometry = await interaction.locator(".drawer__body").evaluate((element) => ({
    clientHeight: element.clientHeight,
    scrollHeight: element.scrollHeight,
  }));
  expect(geometry.scrollHeight).toBeGreaterThan(geometry.clientHeight);

  await interaction.getByRole("textbox", { name: "Input 1", exact: true }).fill("first");
  await interaction.getByRole("textbox", { name: "Input 50", exact: true }).fill("final-value");
  await interaction.getByRole("button", { name: "Submit all" }).click();
  await expect(interaction).toHaveCount(0);
  await expect(details).toContainText("Submitted input count");
  await expect(details).toContainText("50");
  await expect(details).toContainText("Last input value");
  await expect(details).toContainText("final-value");
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

test(`${features("PARAM-SESSION-001")} restores parameter filter, sort, and selection from the URL`, async ({ page }) => {
  await page.goto("/parameters?resource=greeting&q=ValueMissing&sort=state&sortDirection=descending");

  const table = page.getByRole("table");
  await expect(page.getByRole("textbox", { name: "Filter by name or state…" })).toHaveValue("ValueMissing");
  await expect(table.getByRole("columnheader", { name: "State" })).toHaveAttribute("aria-sort", "descending");
  await expect(table.getByRole("row")).toHaveCount(2);
  await expect(page.getByRole("dialog", { name: "greeting" })).toBeVisible();

  await page.reload();
  await expect(page).toHaveURL(/\/parameters\?resource=greeting&q=ValueMissing&sort=state&sortDirection=descending$/);
  await expect(page.getByRole("textbox", { name: "Filter by name or state…" })).toHaveValue("ValueMissing");
  await expect(table.getByRole("columnheader", { name: "State" })).toHaveAttribute("aria-sort", "descending");
  await expect(page.getByRole("dialog", { name: "greeting" })).toBeVisible();
});

test(`${features("PARAM-SET-001")} sets missing and existing parameter values through command interactions`, async ({ page }) => {
  await page.goto("/parameters?resource=greeting");
  const parameterDetails = page.getByRole("dialog", { name: "greeting" });
  await parameterDetails.getByRole("button", { name: "Set parameter" }).click();

  const missingValueDialog = page.getByRole("dialog", { name: "Set greeting" });
  const missingValue = missingValueDialog.getByRole("textbox", { name: "Value" });
  await expect(missingValue).toHaveValue("");
  await missingValueDialog.getByRole("button", { name: "Set", exact: true }).click();
  await expect(missingValueDialog).toContainText("Value is required.");
  await missingValue.fill("Hello from Playwright");
  await missingValueDialog.getByRole("button", { name: "Set", exact: true }).click();
  await expect(missingValueDialog).toHaveCount(0);
  await expect(parameterDetails).toContainText("Running");
  await expect(parameterDetails).toContainText("Hello from Playwright");

  await parameterDetails.getByRole("button", { name: "Close" }).click();
  await page.getByRole("row", { name: /insertionrows/ }).click();
  const existingDetails = page.getByRole("dialog", { name: "insertionrows" });
  await existingDetails.getByRole("button", { name: "Set parameter" }).click();
  const existingValueDialog = page.getByRole("dialog", { name: "Set insertionrows" });
  const existingValue = existingValueDialog.getByRole("textbox", { name: "Value" });
  await expect(existingValue).toHaveValue("1000");
  await existingValue.fill("2000");
  await existingValueDialog.getByRole("button", { name: "Set", exact: true }).click();
  await expect(existingValueDialog).toHaveCount(0);
  await expect(existingDetails).toContainText("2000");
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

  await page.getByRole("row", { name: /frontend/ }).click();
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
