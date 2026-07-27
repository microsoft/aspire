import { expect, test, type Frame, type Page, type TestInfo } from "@playwright/test";
import { getMissingCanvasFeatures, type CanvasFeatureId } from "./canvas-features";

const coveredFeatures = new Set<CanvasFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();

function features(...ids: CanvasFeatureId[]): string {
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

async function openCanvas(page: Page, title: string): Promise<Frame> {
  await page.getByText(title, { exact: true }).click();
  const iframe = page.getByTitle(title);
  await expect(iframe).toBeVisible();
  const handle = await iframe.elementHandle();
  const frame = await handle?.contentFrame();
  expect(frame).not.toBeNull();
  return frame!;
}

async function canvasRequest<T>(frame: Frame, method: string, params?: unknown): Promise<T> {
  return frame.evaluate(
    async ({ requestMethod, requestParams }) => {
      const canvasWindow = window as unknown as {
        request: (method: string, params?: unknown) => Promise<unknown>;
      };
      return canvasWindow.request(requestMethod, requestParams);
    },
    { requestMethod: method, requestParams: params },
  ) as Promise<T>;
}

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on("console", (message) => {
    if (message.type() === "error" || message.type() === "warning") {
      errors.push(`console: ${message.text()}`);
    }
  });
  page.on("pageerror", (error) => errors.push(`page: ${error.message}`));
  page.on("requestfailed", (request) => {
    errors.push(`request: ${request.method()} ${request.url()} (${request.failure()?.errorText ?? "unknown failure"})`);
  });

  await page.goto("/");
  await navigationButton(page, "Canvases").click();
  await expect(page.getByRole("main").locator(".page__title")).toHaveText("Canvases");
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser warnings or errors").toEqual([]);
});

test(`${features("CANVAS-CATALOG-001", "CANVAS-HOST-001", "CANVAS-SANDBOX-001", "CANVAS-BACK-001")} navigates the isolated canvas catalog`, async ({ page }, testInfo) => {
  const cards = page.locator(".canvas-card");
  await expect(cards).toHaveCount(2);
  await expect(cards.nth(0)).toContainText("Resource Radar");
  await expect(cards.nth(0)).toContainText("live resource health and telemetry counters");
  await expect(cards.nth(1)).toContainText("Service Topology");

  const frame = await openCanvas(page, "Resource Radar");
  await expect(page.getByTitle("Resource Radar")).toHaveAttribute("sandbox", "allow-scripts");
  await expect(frame.getByRole("heading", { name: "📡 Resource Radar" })).toBeVisible();
  await expect(frame.getByText("TestShop", { exact: true })).toBeVisible();
  await expect(frame.locator(".card")).toHaveCount(8);
  await attachScreenshot(page, testInfo, "dashboard-resource-radar");

  await page.getByRole("button", { name: "Back to canvases" }).click();
  await expect(cards).toHaveCount(2);
});

test(`${features("CANVAS-CONFIG-001", "CANVAS-RESOURCES-001", "CANVAS-TELEMETRY-001", "CANVAS-COMMAND-001")} exercises every canvas bridge capability`, async ({ page }) => {
  const frame = await openCanvas(page, "Resource Radar");
  await expect(frame.getByText("TestShop", { exact: true })).toBeVisible();

  const config = await canvasRequest<{ applicationName: string; version: string }>(frame, "getConfig");
  expect(config).toMatchObject({ applicationName: "TestShop", version: "9.0.0-dev (mock)" });

  const resources = await canvasRequest<Array<{ name: string; state: string | null }>>(frame, "listResources");
  expect(resources).toHaveLength(9);
  expect(resources.find((resource) => resource.name === "frontend")?.state).toBe("Running");
  expect(resources.find((resource) => resource.name === "hiddenContainer")).toBeDefined();

  const telemetry = await canvasRequest<{ logCount: number; spanCount: number; metricCount: number }>(frame, "getTelemetrySummary");
  expect(telemetry.logCount).toBeGreaterThan(0);
  expect(telemetry.spanCount).toBeGreaterThan(0);
  expect(telemetry.metricCount).toBe(7);

  const stop = await canvasRequest<{ kind: string; message: string }>(frame, "executeCommand", {
    resourceName: "frontend",
    resourceType: "Project",
    commandName: "resource-stop",
  });
  expect(stop.kind).toBe("succeeded");
  const frontendCard = frame.locator(".card").filter({
    has: frame.getByText("frontend", { exact: true }),
  });
  await expect(frontendCard.locator(".state")).toHaveText("Exited");

  const stoppedResources = await canvasRequest<Array<{ name: string; state: string | null }>>(frame, "listResources");
  expect(stoppedResources.find((resource) => resource.name === "frontend")?.state).toBe("Exited");

  const start = await canvasRequest<{ kind: string }>(frame, "executeCommand", {
    resourceName: "frontend",
    resourceType: "Project",
    commandName: "resource-start",
  });
  expect(start.kind).toBe("succeeded");
  await expect(frontendCard.locator(".state")).toHaveText("Running");

  const initialLogCount = Number(await frame.locator("#logCount").innerText());
  await expect
    .poll(async () => Number(await frame.locator("#logCount").innerText()), { timeout: 5_000 })
    .toBeGreaterThan(initialLogCount);
});

test(`${features("CANVAS-ISOLATION-001")} rejects bridge requests from outside the hosted canvas`, async ({ page }) => {
  await openCanvas(page, "Resource Radar");
  const receivedResponse = await page.evaluate(
    () =>
      new Promise<boolean>((resolve) => {
        const id = "parent-window-probe";
        const onMessage = (event: MessageEvent): void => {
          if (
            event.data?.channel === "aspire-deck" &&
            event.data?.kind === "response" &&
            event.data?.id === id
          ) {
            window.removeEventListener("message", onMessage);
            resolve(true);
          }
        };
        window.addEventListener("message", onMessage);
        window.postMessage(
          { channel: "aspire-deck", kind: "request", id, method: "getConfig" },
          "*",
        );
        window.setTimeout(() => {
          window.removeEventListener("message", onMessage);
          resolve(false);
        }, 250);
      }),
  );
  expect(receivedResponse).toBe(false);
});

test(`${features("CANVAS-TOPOLOGY-001")} renders the live topology extension`, async ({ page }) => {
  const frame = await openCanvas(page, "Service Topology");
  await expect(page.getByTitle("Service Topology")).toHaveAttribute("sandbox", "allow-scripts");
  await expect(frame.getByRole("heading", { name: "🕸️ Service Topology" })).toBeVisible();
  await expect(frame.getByText("TestShop · 8 resources", { exact: true })).toBeVisible();
  await expect(frame.locator(".node")).toHaveCount(8);
  await expect.poll(() => frame.locator(".edge").count()).toBeGreaterThan(0);
  await expect(frame.getByText("Waiting for resources…", { exact: true })).toBeHidden();
});

test(`${features("CANVAS-RESPONSIVE-001")} contains the canvas host on mobile`, async ({ page }, testInfo) => {
  await page.getByRole("button", { name: "Dismiss notification" }).click();
  await page.setViewportSize({ width: 390, height: 844 });
  await openCanvas(page, "Resource Radar");

  const geometry = await page.evaluate(() => {
    const iframe = document.querySelector(".canvas-viewer__frame");
    if (!(iframe instanceof HTMLIFrameElement)) {
      throw new Error("Canvas iframe was not rendered.");
    }
    const bounds = iframe.getBoundingClientRect();
    return {
      documentWidth: document.documentElement.scrollWidth,
      viewportWidth: innerWidth,
      frame: { x: bounds.x, width: bounds.width },
    };
  });
  expect(geometry.documentWidth).toBeLessThanOrEqual(geometry.viewportWidth);
  expect(geometry.frame.x).toBeGreaterThanOrEqual(0);
  expect(geometry.frame.width).toBeLessThanOrEqual(390);
  await attachScreenshot(page, testInfo, "dashboard-resource-radar-mobile");
});

const missingFeatures = getMissingCanvasFeatures(coveredFeatures);
if (missingFeatures.length > 0) {
  throw new Error(`Canvas features without Playwright coverage: ${missingFeatures.join(", ")}`);
}
