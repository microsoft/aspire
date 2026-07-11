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
  await page.route("**/api/deck/telemetry/logs?*", async (route) => {
    await route.fulfill({ contentType: "application/x-ndjson", body: "" });
  });
  await page.route("**/api/deck/telemetry/spans?*", async (route) => {
    await route.fulfill({ contentType: "application/x-ndjson", body: "" });
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

test(`${features("HTTP-STRUCTURED-LOGS-001", "HTTP-STRUCTURED-LOG-DETAILS-001")} streams detailed OTLP structured logs through the HTTP backend`, async ({ page }, testInfo) => {
  let structuredLogRequests = 0;
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.unroute("**/api/deck/telemetry/logs?*");
  await page.route("**/api/deck/telemetry/logs?*", async (route) => {
    structuredLogRequests++;
    await route.fulfill({
      contentType: "application/x-ndjson",
      body: [
        JSON.stringify({
          resourceLogs: [{
            resource: {
              attributes: [
                { key: "service.name", value: { stringValue: "stress-api" } },
                { key: "service.instance.id", value: { stringValue: "instance-1" } },
              ],
            },
            scopeLogs: [{
              scope: { name: "Stress.Telemetry" },
              logRecords: [{
                timeUnixNano: "1783670400000000000",
                severityNumber: 9,
                severityText: "Information",
                body: { stringValue: "HTTP request started" },
                attributes: [{ key: "aspire.log_id", value: { stringValue: "41" } }],
                traceId: "0123456789abcdef0123456789abcdef",
                spanId: "0123456789abcdef",
              }],
            }],
          }],
        }),
        JSON.stringify({
          resourceLogs: [{
            resource: {
              attributes: [
                { key: "service.name", value: { stringValue: "stress-worker" } },
                { key: "service.instance.id", value: { stringValue: "worker-1" } },
                { key: "deployment.environment.name", value: { stringValue: "Development" } },
              ],
              droppedAttributesCount: 4,
            },
            scopeLogs: [{
              scope: {
                name: "Stress.Telemetry",
                version: "2.1.0",
                attributes: [{ key: "scope.attribute", value: { stringValue: "scope value" } }],
                droppedAttributesCount: 3,
              },
              logRecords: [{
                timeUnixNano: "0",
                observedTimeUnixNano: "1783670401000000000",
                severityNumber: 17,
                severityText: "ERROR",
                body: { stringValue: "Queue processing failed" },
                attributes: [
                  { key: "aspire.log_id", value: { stringValue: "42" } },
                  { key: "event.name", value: { stringValue: "Worker.QueueFailed" } },
                  { key: "exception.type", value: { stringValue: "System.TimeoutException" } },
                  { key: "exception.message", value: { stringValue: "Queue receive timed out." } },
                  { key: "messaging.destination.name", value: { stringValue: "orders" } },
                  { key: "ParentId", value: { stringValue: "parent-span" } },
                  { key: "{OriginalFormat}", value: { stringValue: "Queue {QueueName} failed" } },
                ],
                droppedAttributesCount: 2,
                flags: 1,
                traceId: "fedcba9876543210fedcba9876543210",
                spanId: "fedcba9876543210",
              }],
            }],
          }],
        }),
        "",
      ].join("\n"),
    });
  });

  await page.goto("/?backend=http");
  await page.getByRole("navigation").getByRole("button", { name: /^Structured Logs(?: \d+)?$/ }).click();

  const logs = page.getByRole("main").getByRole("region", { name: "Structured Logs" });
  const table = logs.getByRole("table");
  await expect(table.getByRole("columnheader")).toHaveText([
    "Resource",
    "Level",
    "Timestamp",
    "Message",
    "Trace",
    "Actions",
  ]);
  await expect(table.locator("tbody tr")).toHaveCount(2);
  await expect(table).toContainText("HTTP request started");
  await expect(table).toContainText("Queue processing failed");
  await expect(table).toContainText("stress-api");
  await expect(table).toContainText("stress-worker");
  await expect(table.locator(".badge")).toHaveText(["Error", "Information"]);
  await expect(logs.locator(".page__subtitle")).toHaveText("2 total · showing 2");
  expect(structuredLogRequests).toBe(1);

  await table.locator("tbody tr", { hasText: "Queue processing failed" }).click();
  const details = page.getByRole("dialog", { name: "Structured log entry details" });
  await expect(details).toContainText("Worker.QueueFailed");
  await expect(details).toContainText("Stress.Telemetry");
  await expect(details.getByRole("group", { name: "Log entry properties" })).toContainText(
    "messaging.destination.nameorders",
  );
  await expect(details.getByRole("group", { name: "Context properties" })).toContainText(
    "Scope version2.1.0",
  );
  await expect(details.getByRole("group", { name: "Context properties" })).toContainText(
    "ParentIdparent-span",
  );
  await expect(details.getByRole("group", { name: "Exception properties" })).toContainText(
    "exception.typeSystem.TimeoutException",
  );
  await expect(details.getByRole("group", { name: "Resource properties" })).toContainText(
    "deployment.environment.nameDevelopment",
  );
  await expect(details.getByText("42", { exact: true })).toHaveCount(0);

  await testInfo.attach("http-backend-structured-logs.png", {
    body: await page.screenshot({ animations: "disabled", fullPage: true }),
    contentType: "image/png",
  });
  await details.getByRole("button", { name: "Close details" }).click();
});

test(`${features("HTTP-STRUCTURED-LOG-CLEAR-001")} clears selected and all structured logs through the HTTP backend`, async ({ page }, testInfo) => {
  interface TestLog {
    resourceName: string;
    timeUnixNano: string;
    severityNumber: number;
    severityText: string;
    body: string;
    id: string;
  }

  let records: TestLog[] = [
    {
      resourceName: "stress-api",
      timeUnixNano: "1783670400000000000",
      severityNumber: 9,
      severityText: "Information",
      body: "HTTP request started",
      id: "51",
    },
    {
      resourceName: "stress-worker",
      timeUnixNano: "1783670401000000000",
      severityNumber: 17,
      severityText: "Error",
      body: "Queue processing failed",
      id: "52",
    },
  ];
  const clearRequests: Array<string | null> = [];
  const toOtlpData = () => ({
    resourceLogs: records.map((record) => ({
      resource: {
        attributes: [{ key: "service.name", value: { stringValue: record.resourceName } }],
      },
      scopeLogs: [{
        scope: { name: "Stress.Telemetry" },
        logRecords: [{
          timeUnixNano: record.timeUnixNano,
          severityNumber: record.severityNumber,
          severityText: record.severityText,
          body: { stringValue: record.body },
          attributes: [{ key: "aspire.log_id", value: { stringValue: record.id } }],
        }],
      }],
    })),
  });

  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.unroute("**/api/deck/telemetry/logs?*");
  await page.route("**/api/deck/telemetry/logs*", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === "DELETE") {
      const resourceName = url.searchParams.get("resource");
      clearRequests.push(resourceName);
      records = resourceName === null
        ? []
        : records.filter((record) => record.resourceName !== resourceName);
      await route.fulfill({ status: 204 });
      return;
    }

    if (url.searchParams.get("follow") === "true") {
      await route.fulfill({
        contentType: "application/x-ndjson",
        body: `${JSON.stringify(toOtlpData())}\n`,
      });
      return;
    }

    await route.fulfill({
      json: {
        data: toOtlpData(),
        totalCount: records.length,
        returnedCount: records.length,
      },
    });
  });

  await page.goto("/?backend=http");
  await page.getByRole("navigation").getByRole("button", { name: /^Structured Logs(?: \d+)?$/ }).click();

  const logs = page.getByRole("main").getByRole("region", { name: "Structured Logs" });
  const rows = logs.getByRole("table").locator("tbody tr");
  await expect(rows).toHaveCount(2);

  await logs.getByRole("combobox", { name: "Resource" }).selectOption("stress-api");
  await logs.getByRole("button", { name: "Clear structured logs" }).click();
  await page.getByRole("menuitem", { name: "Clear stress-api" }).click();

  await expect(page.getByRole("status")).toHaveText("Cleared structured logs for stress-api.");
  await expect(rows).toHaveCount(1);
  await expect(logs.getByRole("table")).toContainText("stress-worker");
  await expect(logs.getByRole("table")).not.toContainText("stress-api");
  await expect(logs.locator(".page__subtitle")).toHaveText("1 total · showing 1");

  await logs.getByRole("button", { name: "Clear structured logs" }).click();
  await testInfo.attach("http-backend-structured-log-clear-menu.png", {
    body: await page.screenshot({ animations: "disabled", fullPage: true }),
    contentType: "image/png",
  });
  await page.getByRole("menuitem", { name: "Clear all resources" }).click();

  await expect(page.getByRole("status")).toHaveText("Cleared all structured logs.");
  await expect(logs.locator(".page__subtitle")).toHaveText("0 total · showing 0");
  await expect(logs).toContainText("No structured logs.");
  expect(clearRequests).toEqual(["stress-api", null]);
});

test(`${features("HTTP-TRACES-001")} streams OTLP spans through the HTTP backend`, async ({ page }, testInfo) => {
  let spanRequests = 0;
  const traceId = "0123456789abcdef0123456789abcdef";
  const rootSpanId = "0123456789abcdef";
  const childSpanId = "fedcba9876543210";
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.unroute("**/api/deck/telemetry/spans?*");
  await page.route("**/api/deck/telemetry/spans?*", async (route) => {
    spanRequests++;
    await route.fulfill({
      contentType: "application/x-ndjson",
      body: `${JSON.stringify({
        resourceSpans: [{
          resource: {
            attributes: [{ key: "service.name", value: { stringValue: "stress-api" } }],
          },
          scopeSpans: [{
            scope: { name: "Stress.Telemetry" },
            spans: [
              {
                traceId,
                spanId: rootSpanId,
                name: "GET /orders",
                kind: 2,
                startTimeUnixNano: "1783670400000000000",
                endTimeUnixNano: "1783670400200000000",
                status: { code: 1 },
              },
              {
                traceId,
                spanId: childSpanId,
                parentSpanId: rootSpanId,
                name: "SELECT orders",
                kind: 3,
                startTimeUnixNano: "1783670400050000000",
                endTimeUnixNano: "1783670400180000000",
                status: { code: 2, message: "Database unavailable" },
              },
            ],
          }],
        }],
      })}\n`,
    });
  });

  await page.goto("/traces?backend=http");

  const traces = page.getByRole("main").getByRole("region", { name: "Traces" });
  await expect(traces.locator(".page__subtitle")).toHaveText("1 traces · 2 spans");
  await expect(traces.locator(".wf__trace")).toHaveCount(1);
  await expect(traces.locator(".wf__span")).toHaveCount(2);
  await expect(traces).toContainText("GET /orders");
  await expect(traces).toContainText("SELECT orders");
  await expect(traces.locator(".wf__trace")).toHaveClass(/wf__trace--error/);
  expect(spanRequests).toBe(1);

  await traces.getByRole("button", { name: /SELECT orders/ }).click();
  const details = page.getByRole("dialog", { name: "SELECT orders" });
  await expect(details.locator(".kv__val.cell-mono")).toHaveText([traceId, childSpanId, rootSpanId]);
  await expect(page).toHaveURL(`/traces/detail/${traceId}?backend=http&span=${childSpanId}`);

  await testInfo.attach("http-backend-traces.png", {
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
