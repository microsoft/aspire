import { expect, test, type Page, type TestInfo } from "@playwright/test";
import type { DeckConfig, InteractionInfo, Resource } from "../src/api/types";
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
  commands: [
    {
      name: "check-health",
      displayName: "Check health",
      displayDescription: "Check the resource health.",
      confirmationMessage: null,
      iconName: "CheckmarkCircle",
      iconVariant: "regular",
      isHighlighted: true,
      state: "enabled",
    },
    {
      name: "echo-arguments",
      displayName: "Echo arguments",
      displayDescription: "Collect every supported command input type.",
      confirmationMessage: null,
      iconName: "Code",
      iconVariant: "regular",
      isHighlighted: false,
      state: "enabled",
    },
  ],
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
  await page.route("**/api/deck/interactions", async (route) => {
    await route.fulfill({ json: [] });
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

test(`${features("HTTP-COMMAND-001")} executes a resource command through the HTTP backend`, async ({ page }) => {
  let commandRequest: unknown;
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.route("**/api/deck/commands/execute", async (route) => {
    commandRequest = route.request().postDataJSON();
    await route.fulfill({ json: { kind: "succeeded", message: "Healthy." } });
  });

  await page.goto("/?backend=http");
  await page.getByRole("table").getByRole("row", { name: /stress-api Project/ }).click();
  const details = page.getByRole("dialog", { name: "stress-api" });
  await details.getByRole("button", { name: "Check health", exact: true }).click();

  await expect(page.getByRole("status")).toHaveText("Check health succeeded");
  expect(commandRequest).toEqual({ resourceName: "stress-api-abc123", commandName: "check-health" });
});

test(`${features("HTTP-INTERACTION-001")} submits every command input type through the HTTP backend`, async ({ page }, testInfo) => {
  let interactions: InteractionInfo[] = [];
  let interactionResponse: unknown;
  let completeCommand: () => void = () => undefined;
  const commandCompleted = new Promise<void>((resolve) => {
    completeCommand = resolve;
  });
  const interaction: InteractionInfo = {
    interactionId: 42,
    kind: "inputsDialog",
    title: "Echo arguments",
    message: "Provide command values.",
    primaryButtonText: "Run",
    secondaryButtonText: "Cancel",
    showSecondaryButton: true,
    showDismiss: true,
    enableMessageMarkdown: false,
    intent: "none",
    inputs: [
      {
        name: "message", label: "Message", placeholder: "Hello", inputType: "text", required: true,
        options: [], value: "", validationErrors: [], description: "Text value to echo.",
        enableDescriptionMarkdown: false, maxLength: 80, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
      {
        name: "repeat", label: "Repeat", placeholder: "", inputType: "number", required: true,
        options: [], value: "1", validationErrors: [], description: "Number of repetitions.",
        enableDescriptionMarkdown: false, maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
      {
        name: "shout", label: "Shout", placeholder: "", inputType: "boolean", required: false,
        options: [], value: "false", validationErrors: [], description: "Uppercase the message.",
        enableDescriptionMarkdown: false, maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
      {
        name: "flavor", label: "Flavor", placeholder: "", inputType: "choice", required: false,
        options: [["vanilla", "Vanilla"], ["chocolate", "Chocolate"]], value: "vanilla",
        validationErrors: [], description: "Select a flavor.", enableDescriptionMarkdown: false,
        maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
      {
        name: "secret", label: "Secret", placeholder: "Optional secret", inputType: "secretText", required: false,
        options: [], value: "", validationErrors: [], description: "The result only reports its length.",
        enableDescriptionMarkdown: false, maxLength: 0, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
      },
    ],
    linkText: "",
    linkUrl: "",
  };

  await page.unroute("**/api/deck/interactions");
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.route("**/api/deck/interactions", async (route) => {
    await route.fulfill({ json: interactions });
  });
  await page.route("**/api/deck/interactions/respond", async (route) => {
    interactionResponse = route.request().postDataJSON();
    interactions = [];
    await route.fulfill({ status: 204 });
    completeCommand();
  });
  await page.route("**/api/deck/commands/execute", async (route) => {
    interactions = [interaction];
    await commandCompleted;
    await route.fulfill({ json: { kind: "succeeded", message: "Echoed." } });
  });

  await page.goto("/?backend=http");
  await page.getByRole("table").getByRole("row", { name: /stress-api Project/ }).click();
  const details = page.getByRole("dialog", { name: "stress-api" });
  await details.getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menuitem", { name: /Echo arguments/ }).click();

  const dialog = page.getByRole("dialog", { name: "Echo arguments" });
  const message = dialog.getByRole("textbox", { name: "Message" });
  const repeat = dialog.getByRole("spinbutton", { name: "Repeat" });
  const shout = dialog.getByRole("checkbox", { name: "Shout" });
  const flavor = dialog.getByRole("combobox", { name: "Flavor" });
  const secret = dialog.getByLabel("Secret");
  await expect(message).toHaveAttribute("placeholder", "Hello");
  await expect(message).toHaveAttribute("maxlength", "80");
  await expect(dialog.getByText("Text value to echo.", { exact: true })).toBeVisible();
  await expect(repeat).toHaveValue("1");
  await expect(shout).not.toBeChecked();
  await expect(flavor).toHaveValue("vanilla");
  await expect(flavor.locator("option")).toHaveText(["Vanilla", "Chocolate"]);
  await expect(secret).toHaveAttribute("type", "password");

  await message.fill("Hello from React");
  await repeat.fill("3");
  await shout.check();
  await flavor.selectOption("chocolate");
  await secret.fill("s3cr3t");
  await testInfo.attach("http-command-inputs.png", {
    body: await page.screenshot({ animations: "disabled", fullPage: true }),
    contentType: "image/png",
  });
  const interactionResponseCompleted = page.waitForEvent("requestfinished", (request) =>
    request.url().endsWith("/api/deck/interactions/respond"));
  await dialog.getByRole("button", { name: "Run", exact: true }).click();
  await interactionResponseCompleted;

  await expect(dialog).toHaveCount(0);
  await expect(page.getByRole("status")).toHaveText("Echo arguments succeeded");
  expect(interactionResponse).toEqual({
    interactionId: 42,
    action: "submit",
    values: {
      message: "Hello from React",
      repeat: "3",
      shout: "true",
      flavor: "chocolate",
      secret: "s3cr3t",
    },
  });
});

test(`${features("HTTP-CONSOLE-001")} streams resource console logs through the HTTP backend`, async ({ page }, testInfo) => {
  let consoleLogRequests = 0;
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.route("**/api/deck/resources/stress-api-abc123/console-logs", async (route) => {
    consoleLogRequests++;
    await route.fulfill({
      contentType: "application/x-ndjson",
      body: [
        JSON.stringify({
          resourceName: resource.name,
          lines: [{ lineNumber: 41, text: "Listening on https://localhost:7443", isStdErr: false }],
        }),
        JSON.stringify({
          resourceName: resource.name,
          lines: [{ lineNumber: 42, text: "Transient connection failure", isStdErr: true }],
        }),
        "",
      ].join("\n"),
    });
  });

  await page.goto("/?backend=http");
  await page.getByRole("navigation").getByRole("button", { name: /^Console(?: \d+)?$/ }).click();

  const consoleRegion = page.getByRole("main").getByRole("region", { name: "Console" });
  await expect(consoleRegion.getByText("Listening on https://localhost:7443", { exact: true })).toBeVisible();
  await expect(consoleRegion.getByText("Transient connection failure", { exact: true })).toBeVisible();
  await expect(consoleRegion.locator(".log-line.stderr")).toHaveCount(1);
  await expect(consoleRegion.locator(".console__footer")).toContainText("2 lines");
  await expect(consoleRegion.locator(".console__footer")).toContainText("1 stderr");
  expect(consoleLogRequests).toBe(1);

  await testInfo.attach("http-backend-console.png", {
    body: await page.screenshot({ animations: "disabled", fullPage: true }),
    contentType: "image/png",
  });
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
