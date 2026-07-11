import { expect, test, type Page, type TestInfo } from "@playwright/test";
import { getMissingStressFeatures, type StressFeatureId } from "./stress-features";

const coveredFeatures = new Set<StressFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();

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
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
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

test(`${features("STRESS-NAVIGATION-001", "STRESS-EMPTY-TELEMETRY-001")} reaches every page against the live dashboard`, async ({ page }) => {
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
