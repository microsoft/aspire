import { expect, test, type Page, type TestInfo } from "@playwright/test";
import type { DeckConfig, Resource } from "../src/api/types";
import {
  getMissingHttpBackendFeatures,
  type HttpBackendFeatureId,
} from "./http-backend-features";

const coveredFeatures = new Set<HttpBackendFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();
const allowUnavailableResponses = new WeakSet<Page>();

function features(...ids: HttpBackendFeatureId[]): string {
  for (const id of ids) {
    coveredFeatures.add(id);
  }

  return `[${ids.join(", ")}]`;
}

const config: DeckConfig = {
  applicationName: "Stress AppHost",
  resourceServiceUrl: null,
  otlpGrpcUrl: null,
  otlpHttpUrl: null,
  version: "13.5.0-live",
};

const resource: Resource = {
  name: "stress-api-abc123",
  resourceType: "Project",
  displayName: "stress-api",
  uid: "stress-resource-uid",
  state: "Running",
  stateStyle: "success",
  health: "Healthy",
  createdAt: "2026-07-10T08:00:00Z",
  startedAt: "2026-07-10T08:00:01Z",
  stoppedAt: null,
  urls: [],
  properties: [],
  environment: [],
  healthReports: [],
  commands: [],
  relationships: [],
  isHidden: false,
  supportsDetailedTelemetry: true,
  iconName: "Code",
  iconVariant: "filled",
};

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
});

test.afterEach(async ({ page }) => {
  const errors = browserErrors.get(page) ?? [];
  const unexpected = allowUnavailableResponses.has(page)
    ? errors.filter((error) => !error.startsWith("console: Failed to load resource: the server responded with a status of 503"))
    : errors;
  expect(unexpected, "Unexpected browser errors").toEqual([]);
});

test(`${features("HTTP-CONFIG-001", "HTTP-RESOURCES-001", "HTTP-MOCK-ISOLATION-001")} loads the dashboard from the HTTP backend`, async ({ page }, testInfo: TestInfo) => {
  let configRequests = 0;
  let resourceRequests = 0;
  await page.route("**/api/deck/config", async (route) => {
    configRequests++;
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    resourceRequests++;
    await route.fulfill({ json: [resource] });
  });

  await page.goto("/?backend=http");

  await expect(page.getByRole("banner").locator(".topbar__app")).toHaveText("Stress AppHost");
  await expect(page.getByRole("navigation")).toContainText("Aspire Deck 13.5.0-live");
  const resourceRow = page.getByRole("table").getByRole("row", { name: /stress-api Project/ });
  await expect(resourceRow).toBeVisible();
  await expect(resourceRow.locator('svg[data-icon-name="Code"][data-icon-variant="filled"]')).toHaveCount(1);
  await expect(page.getByRole("table")).not.toContainText("frontend");
  await expect(page.getByTitle("Resources: Connected")).toBeVisible();
  expect(configRequests).toBeGreaterThan(0);
  expect(resourceRequests).toBeGreaterThan(0);

  const body = await page.screenshot({ animations: "disabled", fullPage: true });
  await testInfo.attach("http-backend-resources.png", { body, contentType: "image/png" });
});

test(`${features("HTTP-FAILURE-001")} reports an unavailable HTTP backend`, async ({ page }, testInfo: TestInfo) => {
  allowUnavailableResponses.add(page);
  await page.route("**/api/deck/**", async (route) => {
    await route.fulfill({ status: 503, body: "Dashboard backend unavailable" });
  });

  await page.goto("/?backend=http");

  await expect(page.getByRole("heading", { level: 1, name: "Can't reach the AppHost" })).toBeVisible();
  await expect(page.getByTitle("Resources: Error")).toBeVisible();
  await expect(page.getByRole("table")).toHaveCount(0);
  await expect(page.getByText("frontend", { exact: true })).toHaveCount(0);

  const body = await page.screenshot({ animations: "disabled", fullPage: true });
  await testInfo.attach("http-backend-unavailable.png", { body, contentType: "image/png" });
});

test(`${features("HTTP-RECOVERY-001")} recovers when the HTTP backend returns`, async ({ page }) => {
  allowUnavailableResponses.add(page);
  let available = false;
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill(available ? { json: config } : { status: 503, body: "Dashboard backend unavailable" });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill(available ? { json: [resource] } : { status: 503, body: "Dashboard backend unavailable" });
  });

  await page.goto("/?backend=http");
  await expect(page.getByTitle("Resources: Error")).toBeVisible();

  available = true;

  await expect(page.getByRole("banner").locator(".topbar__app")).toHaveText("Stress AppHost");
  await expect(page.getByRole("table").getByRole("row", { name: /stress-api Project/ })).toBeVisible();
  await expect(page.getByTitle("Resources: Connected")).toBeVisible();
});

test(`${features("HTTP-EMPTY-TELEMETRY-001")} renders a settled empty metrics state`, async ({ page }) => {
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });

  await page.goto("/?backend=http");
  await page.getByRole("navigation").getByRole("button", { name: "Metrics 0" }).click();

  const metrics = page.getByRole("main").getByRole("region", { name: "Metrics" });
  await expect(metrics.locator(".page__subtitle")).toHaveText("0 instruments");
  await expect(metrics).not.toContainText("Loading…");
});

const missingFeatures = getMissingHttpBackendFeatures(coveredFeatures);
if (missingFeatures.length > 0) {
  throw new Error(`HTTP backend features without Playwright coverage: ${missingFeatures.join(", ")}`);
}
