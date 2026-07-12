import { expect, test, type Page, type TestInfo } from "@playwright/test";
import type { DeckConfig, InteractionInfo, Resource } from "../src/api/types";
import {
  getMissingHttpBackendFeatures,
  type HttpBackendFeatureId,
} from "./http-backend-features";

const coveredFeatures = new Set<HttpBackendFeatureId>();
const browserErrors = new WeakMap<Page, string[]>();
const allowUnavailableResponses = new WeakSet<Page>();
const allowInterceptedImportAbort = new WeakSet<Page>();
const allowAuthenticationNavigation = new WeakSet<Page>();

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
  await page.route("**/api/deck/telemetry/metrics", async (route) => {
    await route.fulfill({ json: [] });
  });
});

test.afterEach(async ({ page }) => {
  const errors = browserErrors.get(page) ?? [];
  const unexpected = allowUnavailableResponses.has(page)
    ? errors.filter((error) => !error.startsWith("console: Failed to load resource: the server responded with a status of 503"))
    : errors;
  const filtered = allowInterceptedImportAbort.has(page)
    ? unexpected.filter((error) => !error.includes("/api/deck/manage-data/import (net::ERR_ABORTED)"))
    : unexpected;
  const navigationFiltered = allowAuthenticationNavigation.has(page)
    ? filtered.filter((error) =>
        error !== "console: Failed to load resource: the server responded with a status of 404 (Not Found)"
        && !(error.includes("/login?returnUrl=") && error.endsWith("(net::ERR_ABORTED)"))
        && !(error.includes("/api/deck/config") && error.endsWith("(net::ERR_ABORTED)")))
    : filtered;
  expect(navigationFiltered, "Unexpected browser errors").toEqual([]);
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
  const resourceRow = page.getByRole("table").getByRole("row", { name: /stress-api/ });
  await expect(resourceRow).toBeVisible();
  await expect(resourceRow.locator('svg[data-icon-name="Code"][data-icon-variant="filled"]')).toHaveCount(1);
  await expect(page.getByRole("table")).not.toContainText("frontend");
  await expect(page.getByTitle("Resources: Connected")).toBeVisible();
  expect(configRequests).toBeGreaterThan(0);
  expect(resourceRequests).toBeGreaterThan(0);

  const body = await page.screenshot({ animations: "disabled", fullPage: true });
  await testInfo.attach("http-backend-resources.png", { body, contentType: "image/png" });
});

test(`${features("HTTP-SHELL-UNSECURED-001")} warns about unsecured endpoints and persists dismissal`, async ({ page }) => {
  await page.route("**/api/deck/config", async (route) => route.fulfill({
    json: {
      ...config,
      isTelemetryEndpointUnsecured: true,
      isApiEndpointUnsecured: true,
    } satisfies DeckConfig,
  }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));

  await page.goto("/?backend=http");

  const notifications = page.getByRole("region", { name: "System notifications" });
  const warning = notifications.getByRole("alert");
  await expect(warning).toContainText("Endpoint is unsecured");
  await expect(warning).toContainText("Untrusted apps can send telemetry to the dashboard.");
  await expect(warning).toContainText("Untrusted apps can access telemetry data via the API.");
  await expect(warning.getByRole("button")).toHaveText(["More information", ""]);
  await page.evaluate(() => {
    window.open = (url) => {
      document.body.dataset.openedUrl = String(url);
      return null;
    };
  });
  await warning.getByRole("button", { name: "More information" }).click();
  await expect(page.locator("body")).toHaveAttribute("data-opened-url", "https://aka.ms/aspire/api-endpoint-unsecured");

  await warning.getByRole("button", { name: "Dismiss notification" }).click();
  await expect(notifications).toBeHidden();
  await expect.poll(() => page.evaluate(() => localStorage.getItem("Aspire_Security_UnsecuredEndpointMessageDismissed"))).toBe("true");

  await page.reload();
  await expect(page.getByRole("region", { name: "System notifications" })).toBeHidden();
});

test(`${features("HTTP-AUTH-001")} transfers an authentication challenge to the dashboard login flow`, async ({ page }) => {
  // Full-page login navigation intentionally cancels in-flight startup requests.
  allowAuthenticationNavigation.add(page);
  await page.route("**/api/deck/config", async (route) => route.fulfill({
    status: 302,
    headers: { Location: "/login?returnUrl=%2Fapi%2Fdeck%2Fconfig" },
  }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({
    status: 302,
    headers: { Location: "/login?returnUrl=%2Fapi%2Fdeck%2Fresources" },
  }));
  await page.route("**/login?*", async (route) => route.fulfill({
    contentType: "text/html",
    body: "<!doctype html><title>Dashboard login</title><h1>Log in to Aspire</h1>",
  }));

  await page.goto("/?backend=http");

  await expect(page).toHaveURL(/\/login\?returnUrl=/);
  await expect(page.getByRole("heading", { name: "Log in to Aspire" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Can't reach the AppHost" })).toHaveCount(0);
});

test(`${features("HTTP-USER-001")} shows the authenticated user and signs out`, async ({ page }) => {
  await page.route("**/api/deck/config", async (route) => route.fulfill({
    json: {
      ...config,
      frontendAuthMode: "OpenIdConnect",
      user: { name: "Ada Lovelace", username: "ada@example.com" },
    } satisfies DeckConfig,
  }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  let logoutMethod: string | null = null;
  await page.route("**/authentication/logout", async (route) => {
    logoutMethod = route.request().method();
    await route.fulfill({
      contentType: "text/html",
      body: "<!doctype html><title>Signed out</title><h1>Signed out</h1>",
    });
  });

  await page.goto("/?backend=http");
  const profile = page.getByRole("button", { name: "User profile for Ada Lovelace" });
  await expect(profile).toHaveText("AL");
  await profile.click();

  let menu = page.getByRole("menu", { name: "User profile" });
  await expect(menu.getByText("Logged in as", { exact: true })).toBeVisible();
  await expect(menu.getByText("Ada Lovelace", { exact: true })).toBeVisible();
  await expect(menu.getByText("ada@example.com", { exact: true })).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Sign out" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(menu).toBeHidden();

  await profile.click();
  menu = page.getByRole("menu", { name: "User profile" });
  await menu.getByRole("menuitem", { name: "Sign out" }).click();
  await expect(page).toHaveURL(/\/authentication\/logout$/);
  await expect(page.getByRole("heading", { name: "Signed out" })).toBeVisible();
  expect(logoutMethod).toBe("POST");
});

test(`${features("HTTP-LANGUAGE-001")} selects and applies a dashboard language`, async ({ page }) => {
  let culture = "en";
  let requestedLanguage: string | null = null;
  await page.route("**/api/deck/config", async (route) => route.fulfill({
    json: {
      ...config,
      culture,
      cultures: [
        { name: "en", displayName: "English" },
        { name: "fr", displayName: "Français" },
      ],
    } satisfies DeckConfig,
  }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  await page.route("**/api/set-language?*", async (route) => {
    const url = new URL(route.request().url());
    requestedLanguage = url.searchParams.get("language");
    culture = requestedLanguage ?? culture;
    await route.fulfill({
      status: 302,
      headers: {
        Location: url.searchParams.get("redirectUrl") ?? "/",
        "Set-Cookie": `.AspNetCore.Culture=c%3D${culture}%7Cuic%3D${culture}; Path=/; SameSite=Lax`,
      },
    });
  });

  await page.goto("/?backend=http");
  await page.getByRole("banner").getByRole("button", { name: "Settings" }).click();
  let settings = page.getByRole("dialog", { name: "Settings" });
  const language = settings.getByLabel("Language");
  await expect(language).toHaveValue("en");
  await language.selectOption("fr");

  await expect.poll(() => requestedLanguage).toBe("fr");
  await expect(page).toHaveURL(/\?backend=http$/);
  await page.getByRole("banner").getByRole("button", { name: "Settings" }).click();
  settings = page.getByRole("dialog", { name: "Settings" });
  await expect(settings.getByLabel("Language")).toHaveValue("fr");
  await expect.poll(async () => (await page.context().cookies()).find((cookie) => cookie.name === ".AspNetCore.Culture")?.value).toBe("c%3Dfr%7Cuic%3Dfr");
});

test(`${features("HTTP-AI-AGENTS-001")} opens backend-controlled AI agent guidance`, async ({ page }) => {
  await page.route("**/api/deck/config", async (route) => route.fulfill({
    json: {
      ...config,
      isAgentHelpEnabled: true,
      agentHelpMarkdown: "Give AI agents deep observability.\n\n- Resource state\n- Structured logs\n\n```bash\naspire agent init\n```\n\nSee [AI coding agents](https://aka.ms/aspire/ai-agents-apphost).",
    } satisfies DeckConfig,
  }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));

  await page.goto("/?backend=http");
  await page.getByRole("banner").getByRole("button", { name: "AI agents" }).click();

  const drawer = page.getByRole("dialog", { name: "AI agents" });
  await expect(drawer).toContainText("Give AI agents deep observability.");
  await expect(drawer.getByRole("listitem")).toHaveText(["Resource state", "Structured logs"]);
  await expect(drawer.locator("code")).toHaveText("aspire agent init");
  await page.evaluate(() => {
    window.open = (url) => {
      document.body.dataset.openedUrl = String(url);
      return null;
    };
  });
  await drawer.getByRole("link", { name: "AI coding agents" }).click();
  await expect(page.locator("body")).toHaveAttribute("data-opened-url", "https://aka.ms/aspire/ai-agents-apphost");
  await drawer.getByRole("button", { name: "Close AI agents" }).click();
  await expect(drawer).toBeHidden();
});

test(`${features("HTTP-ASSISTANT-001")} streams an assistant response from the backend`, async ({ page }) => {
  let chatRequest: unknown;
  await page.route("**/api/deck/config", async (route) => route.fulfill({
    json: { ...config, isAssistantEnabled: true } satisfies DeckConfig,
  }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  await page.route("**/api/deck/assistant/info", async (route) => route.fulfill({
    json: { models: [{ family: "gpt-5.4", displayName: "GPT-5.4" }, { family: "gpt-4.1", displayName: "GPT-4.1" }] },
  }));
  await page.route("**/api/deck/assistant/chat", async (route) => {
    chatRequest = route.request().postDataJSON();
    await route.fulfill({
      contentType: "application/x-ndjson",
      body: [
        { type: "start", content: null, message: null },
        { type: "content", content: "The API ", message: null },
        { type: "content", content: "has two failed requests.", message: null },
        { type: "complete", content: null, message: null },
      ].map((event) => JSON.stringify(event)).join("\n") + "\n",
    });
  });

  await page.goto("/?backend=http");
  await page.getByRole("banner").getByRole("button", { name: "Assistant" }).click();
  const assistant = page.getByRole("dialog", { name: "Assistant" });
  await expect(assistant.getByLabel("Assistant model")).toHaveValue("gpt-5.4");
  await assistant.getByLabel("Message the assistant").fill("Explain the current failures");
  await assistant.getByRole("button", { name: "Send" }).click();

  const conversation = assistant.getByRole("log", { name: "Assistant conversation" });
  await expect(conversation).toContainText("YouExplain the current failures");
  await expect(conversation).toContainText("AssistantThe API has two failed requests.");
  expect(chatRequest).toEqual({
    messages: [{ role: "user", content: "Explain the current failures" }],
    model: "gpt-5.4",
  });
});

test(`${features("HTTP-MANAGE-DATA-001")} manages dashboard data through the HTTP backend`, async ({ page }) => {
  // Chromium reports an intercepted file upload as aborted after Playwright fulfills
  // it, even though the response and payload complete. The real 204 endpoint is
  // exercised by DeckApiTests.ManageData_InventoryExportImportAndRemoveUseDeckContract.
  allowInterceptedImportAbort.add(page);
  const requests: Array<{ operation: string; body: unknown }> = [];
  let removed = false;
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  await page.route("**/api/deck/manage-data", async (route) => route.fulfill({
    json: {
      resources: [{
        name: resource.name,
        displayName: resource.displayName,
        dataTypes: removed ? ["ResourceDetails", "ConsoleLogs"] : ["ResourceDetails", "ConsoleLogs", "StructuredLogs", "Traces", "Metrics"],
      }],
      isImportEnabled: true,
    },
  }));
  await page.route("**/api/deck/manage-data/export", async (route) => {
    requests.push({ operation: "export", body: route.request().postDataJSON() });
    await route.fulfill({
      contentType: "application/zip",
      headers: { "Content-Disposition": "attachment; filename*=UTF-8''aspire-telemetry-export-test.zip" },
      body: "PK-test-archive",
    });
  });
  await page.route("**/api/deck/manage-data/import", async (route) => {
    requests.push({
      operation: "import",
      body: {
        fileName: route.request().headers()["x-aspire-file-name"],
        content: route.request().postDataBuffer()?.toString("utf8"),
      },
    });
    await route.fulfill({ status: 200, body: "" });
  });
  await page.route("**/api/deck/manage-data/remove", async (route) => {
    requests.push({ operation: "remove", body: route.request().postDataJSON() });
    removed = true;
    await route.fulfill({ status: 204 });
  });

  await page.goto("/?backend=http");
  await page.getByRole("banner").getByRole("button", { name: "Settings" }).click();
  await page.getByRole("dialog", { name: "Settings" }).getByRole("button", { name: "Manage data" }).click();

  const drawer = page.getByRole("dialog", { name: "Manage data" });
  await expect(page.getByRole("dialog", { name: "Settings" })).toBeHidden();
  await expect(drawer.getByRole("checkbox")).toHaveCount(7);
  await expect(drawer.getByRole("checkbox", { name: "Select all data", exact: true })).toBeChecked();
  await expect(drawer).toContainText("Resource detailsConsole logsStructured logsTracesMetrics");
  await expect(drawer.getByText("5 selected")).toBeVisible();

  const downloadPromise = page.waitForEvent("download");
  await drawer.getByRole("button", { name: "Export" }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toBe("aspire-telemetry-export-test.zip");

  const importResponse = page.waitForResponse("**/api/deck/manage-data/import");
  await drawer.getByLabel("Import telemetry file").setInputFiles({
    name: "telemetry.json",
    mimeType: "application/json",
    buffer: Buffer.from('{"resourceLogs":[]}'),
  });
  await importResponse;

  await drawer.getByRole("button", { name: "Remove" }).click();
  await expect(drawer.getByRole("checkbox")).toHaveCount(4);
  await expect(drawer).not.toContainText("Structured logs");
  await expect(drawer).not.toContainText("Traces");
  await expect(drawer).not.toContainText("Metrics");

  expect(requests).toEqual([
    {
      operation: "export",
      body: { resources: [{ resourceName: resource.name, dataTypes: ["ResourceDetails", "ConsoleLogs", "StructuredLogs", "Traces", "Metrics", "Resource"] }] },
    },
    { operation: "import", body: { fileName: "telemetry.json", content: '{"resourceLogs":[]}' } },
    {
      operation: "remove",
      body: { resources: [{ resourceName: resource.name, dataTypes: ["ResourceDetails", "ConsoleLogs", "StructuredLogs", "Traces", "Metrics", "Resource"] }] },
    },
  ]);
  await drawer.getByRole("button", { name: "Close manage data" }).click();
  await expect(drawer).toBeHidden();
});

test(`${features("HTTP-RESOURCE-VIRTUALIZATION-001")} virtualizes a 1000-resource inventory`, async ({ page }) => {
  const resources = Array.from({ length: 1_000 }, (_, index): Resource => ({
    ...resource,
    name: `stress-api-${index.toString().padStart(4, "0")}`,
    displayName: `stress-api-${index.toString().padStart(4, "0")}`,
    uid: `stress-resource-${index}`,
  }));
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: resources }));
  await page.goto("/?backend=http");

  const table = page.getByRole("table");
  const wrapper = table.locator("..");
  await expect(wrapper).toHaveAttribute("data-virtualized", "true");
  await expect(table).toHaveAttribute("aria-rowcount", "1001");
  expect(await table.locator("tbody tr:not(.data__virtual-spacer)").count()).toBeLessThan(100);
  await expect(table.getByText("stress-api-0000", { exact: true })).toBeVisible();
  await wrapper.evaluate((element) => { element.scrollTop = element.scrollHeight; element.dispatchEvent(new Event("scroll")); });
  const lastRow = table.getByRole("row").filter({ hasText: "stress-api-0999" });
  await expect(lastRow).toBeVisible();
  await lastRow.press("Enter");
  await expect(page.getByRole("dialog", { name: "stress-api-0999" })).toBeVisible();
});

test(`${features("HTTP-RESOURCES-001")} distinguishes the complete resource lifecycle`, async ({ page }) => {
  const lifecycle = [
    ["running", "Running", "success"],
    ["starting", "Starting", "info"],
    ["finished", "Finished", "success"],
    ["exited", "Exited", "warning"],
    ["not-started", "NotStarted", "neutral"],
    ["unknown", null, null],
  ] as const;
  const resources = lifecycle.map(([name, state, stateStyle], index): Resource => ({
    ...resource,
    name,
    displayName: name,
    uid: `lifecycle-${index}`,
    state,
    stateStyle,
    health: null,
  }));
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: resources }));
  await page.goto("/?backend=http");

  const table = page.getByRole("table");
  for (const [name, state] of lifecycle) {
    const row = table.getByRole("row", { name: new RegExp(name) });
    await expect(row).toContainText(state ?? "Unknown");
  }
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

test(`${features("HTTP-RECOVERY-001", "HTTP-RECONNECT-001")} retries and recovers when the HTTP backend returns`, async ({ page }) => {
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
  const retry = page.getByRole("main").getByRole("button", { name: "Retry" });
  await expect(retry).toBeVisible();

  available = true;
  await retry.click();

  await expect(page.getByRole("banner").locator(".topbar__app")).toHaveText("Stress AppHost");
  await expect(page.getByRole("table").getByRole("row", { name: /stress-api/ })).toBeVisible();
  await expect(page.getByTitle("Resources: Connected")).toBeVisible();
});

test(`${features("HTTP-RECONNECT-001")} retains resources and exposes retry during a backend outage`, async ({ page }) => {
  allowUnavailableResponses.add(page);
  let available = true;
  let resourceRequests = 0;
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => {
    resourceRequests++;
    await route.fulfill(available ? { json: [resource] } : { status: 503, body: "Dashboard backend unavailable" });
  });

  await page.goto("/?backend=http");
  await expect(page.getByRole("row", { name: /stress-api/ })).toBeVisible();
  available = false;
  await expect(page.getByTitle("Resources: Error")).toBeVisible();

  const warning = page.getByRole("region", { name: "System notifications" }).getByRole("alert");
  await expect(warning).toContainText("Lost connection to the AppHostAttempting to reconnect...");
  const requestsBeforeRetry = resourceRequests;
  available = true;
  await warning.getByRole("button", { name: "Retry" }).click();

  await expect.poll(() => resourceRequests).toBeGreaterThan(requestsBeforeRetry);
  await expect(page.getByTitle("Resources: Connected")).toBeVisible();
  await expect(warning).toBeHidden();
  await expect(page.getByRole("row", { name: /stress-api/ })).toBeVisible();
});

test(`${features("HTTP-COMMAND-001", "HTTP-COMMAND-OUTCOMES-001")} executes resource command outcomes through the HTTP backend`, async ({ page }) => {
  const requests: unknown[] = [];
  const commandResource: Resource = {
    ...resource,
    commands: [
      ...resource.commands,
      { ...resource.commands[0]!, name: "cancel-operation", displayName: "Cancel operation", isHighlighted: false },
      { ...resource.commands[0]!, name: "fail-operation", displayName: "Fail operation", isHighlighted: false },
    ],
  };
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [commandResource] });
  });
  await page.route("**/api/deck/commands/execute", async (route) => {
    const request = route.request().postDataJSON() as { commandName: string };
    requests.push(request);
    if (request.commandName === "cancel-operation") {
      await route.fulfill({ json: { kind: "cancelled", message: "Cancelled by user.", result: null } });
      return;
    }
    if (request.commandName === "fail-operation") {
      await route.fulfill({ json: { kind: "failed", message: "Health probe failed.", result: null } });
      return;
    }
    await route.fulfill({
      json: {
        kind: "succeeded",
        message: "Healthy.",
        result: {
          value: "## Health report\n\n| Check | Status |\n| --- | --- |\n| API | **Healthy** |",
          format: "markdown",
          displayImmediately: true,
        },
      },
    });
  });

  await page.goto("/?backend=http");
  await page.getByRole("table").getByRole("row", { name: /stress-api/ }).click();
  const details = page.getByRole("dialog", { name: "stress-api" });
  await details.getByRole("button", { name: "Check health", exact: true }).click();

  await expect(page.getByRole("status").filter({ hasText: "Check health succeeded" })).toContainText("Check health succeeded");
  const result = page.getByRole("dialog", { name: "Check health" });
  await expect(result.getByRole("heading", { name: "Health report" })).toBeVisible();
  await expect(result.getByRole("table")).toContainText("CheckStatusAPIHealthy");
  await result.getByRole("button", { name: "Close visualizer" }).click();
  await details.getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menuitem", { name: /Cancel operation/ }).click();
  await expect(page.getByRole("status")).toContainText("Cancelled by user.");
  await expect(page.getByRole("status").locator(".state__dot")).toHaveClass(/error/);
  await page.getByRole("button", { name: "Dismiss command result" }).click();
  await details.getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menuitem", { name: /Fail operation/ }).click();
  await expect(page.getByRole("status")).toContainText("Health probe failed.");
  await expect(page.getByRole("status").locator(".state__dot")).toHaveClass(/error/);
  expect(requests).toEqual([
    { resourceName: "stress-api-abc123", commandName: "check-health" },
    { resourceName: "stress-api-abc123", commandName: "cancel-operation" },
    { resourceName: "stress-api-abc123", commandName: "fail-operation" },
  ]);
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
    message: "Provide **command** values.",
    primaryButtonText: "Run",
    secondaryButtonText: "Cancel",
    showSecondaryButton: true,
    showDismiss: true,
    enableMessageMarkdown: true,
    intent: "none",
    inputs: [
      {
        name: "message", label: "Message", placeholder: "Hello", inputType: "text", required: true,
        options: [], value: "", validationErrors: [], description: "Text **value** to echo.",
        enableDescriptionMarkdown: true, maxLength: 80, allowCustomChoice: false, disabled: false, updateStateOnChange: false,
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
        name: "locked", label: "Locked option", placeholder: "", inputType: "boolean", required: false,
        options: [], value: "true", validationErrors: [], description: "Controlled by the AppHost.",
        enableDescriptionMarkdown: false, maxLength: 0, allowCustomChoice: false, disabled: true, updateStateOnChange: false,
      },
      {
        name: "flavor", label: "Flavor", placeholder: "Choose a flavor", inputType: "choice", required: false,
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
  await page.getByRole("table").getByRole("row", { name: /stress-api/ }).click();
  const details = page.getByRole("dialog", { name: "stress-api" });
  await details.getByRole("button", { name: "Resource commands" }).click();
  await page.getByRole("menuitem", { name: /Echo arguments/ }).click();

  const dialog = page.getByRole("dialog", { name: "Echo arguments" });
  const message = dialog.getByRole("textbox", { name: "Message" });
  const repeat = dialog.getByRole("spinbutton", { name: "Repeat" });
  const shout = dialog.getByRole("checkbox", { name: "Shout" });
  const locked = dialog.getByRole("checkbox", { name: "Locked option" });
  const flavor = dialog.getByRole("combobox", { name: "Flavor" });
  const secret = dialog.getByLabel("Secret", { exact: true });
  await expect(dialog.locator(".interaction-message strong")).toHaveText("command");
  await expect(message).toHaveAttribute("placeholder", "Hello");
  await expect(message).toHaveAttribute("maxlength", "80");
  await expect(dialog.locator("#int-message-description strong")).toHaveText("value");
  await expect(dialog.getByText("Text value to echo.", { exact: true })).toBeVisible();
  await expect(repeat).toHaveValue("1");
  await expect(repeat).toHaveAttribute("type", "number");
  await expect(shout).not.toBeChecked();
  await expect(locked).toBeChecked();
  await expect(locked).toBeDisabled();
  await expect(flavor).toHaveValue("Vanilla");
  await expect(flavor).toHaveAttribute("placeholder", "Choose a flavor");
  await expect(secret).toHaveAttribute("type", "password");
  await expect(secret).toHaveAttribute("autocomplete", "new-password");
  await dialog.getByRole("button", { name: "Reveal secret" }).click();
  await expect(secret).toHaveAttribute("type", "text");
  await dialog.getByRole("button", { name: "Hide secret" }).click();
  await expect(secret).toHaveAttribute("type", "password");

  await message.fill("Hello from React");
  await repeat.fill("3");
  await shout.check();
  await flavor.click();
  await flavor.press("ArrowDown");
  await expect(page.getByRole("option")).toHaveText(["Vanilla", "Chocolate"]);
  await page.getByRole("option", { name: "Chocolate", exact: true }).click();
  await expect(flavor).toHaveValue("Chocolate");
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
      locked: "true",
      flavor: "chocolate",
      secret: "s3cr3t",
    },
  });
});

test(`${features("HTTP-CONSOLE-001", "HTTP-CONSOLE-CONTROLS-001")} streams and controls resource console logs through the HTTP backend`, async ({ page }, testInfo) => {
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
          lines: [{ lineNumber: 41, text: "2026-07-10T08:01:02.123456789Z Listening on https://localhost:7443", isStdErr: false }],
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
  await page.getByRole("main").getByRole("combobox", { name: "Resource" }).selectOption(resource.name);

  const consoleRegion = page.getByRole("main").getByRole("region", { name: "Console" });
  await expect(consoleRegion.getByText("Listening on https://localhost:7443", { exact: true })).toBeVisible();
  await expect(consoleRegion.getByText("Transient connection failure", { exact: true })).toBeVisible();
  await expect(consoleRegion.locator(".log-line.stderr")).toHaveCount(1);
  await expect(consoleRegion.locator(".console__footer")).toContainText("2 lines");
  await expect(consoleRegion.locator(".console__footer")).toContainText("1 stderr");
  expect(consoleLogRequests).toBe(1);

  await page.getByRole("button", { name: "Console settings" }).click();
  await page.getByRole("menuitem", { name: "Show timestamps" }).click();
  await expect(consoleRegion.locator(".log-line__timestamp")).toHaveCount(1);

  await page.getByRole("button", { name: "Console settings" }).click();
  await page.getByRole("menuitem", { name: "UTC timestamps" }).click();
  await expect(consoleRegion.locator(".log-line__timestamp")).toHaveText("2026-07-10T08:01:02Z");

  await page.getByRole("button", { name: "Console settings" }).click();
  await page.getByRole("menuitem", { name: "Wrap lines" }).click();
  await expect(consoleRegion.locator(".console")).toHaveClass(/console--wrap/);

  await page.getByRole("button", { name: "Console settings" }).click();
  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("menuitem", { name: "Download logs" }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/^stress-api-abc123-.*\.txt$/);
  const stream = await download.createReadStream();
  let downloadedText = "";
  for await (const chunk of stream) {
    downloadedText += chunk.toString();
  }
  expect(downloadedText).toBe([
    "2026-07-10T08:01:02.123456789Z Listening on https://localhost:7443",
    "Transient connection failure",
    "",
  ].join("\n"));

  await testInfo.attach("http-backend-console.png", {
    body: await page.screenshot({ animations: "disabled", fullPage: true }),
    contentType: "image/png",
  });
});

test(`${features("HTTP-CONSOLE-VIRTUALIZATION-001")} virtualizes a 5000-line console backlog`, async ({ page }) => {
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  await page.route("**/api/deck/resources/stress-api-abc123/console-logs", async (route) => {
    await route.fulfill({
      contentType: "application/x-ndjson",
      body: `${JSON.stringify({
        resourceName: resource.name,
        lines: Array.from({ length: 5_000 }, (_, index) => ({
          lineNumber: index + 1,
          text: `virtualized line ${index + 1}`,
          isStdErr: false,
        })),
      })}\n`,
    });
  });

  await page.goto("/?backend=http");
  await page.getByRole("navigation").getByRole("button", { name: /^Console(?: \d+)?$/ }).click();
  await page.getByRole("main").getByRole("combobox", { name: "Resource" }).selectOption(resource.name);
  const consoleRegion = page.getByRole("main").getByRole("region", { name: "Console" });
  const scroller = consoleRegion.locator(".console__scroll");
  await expect(consoleRegion.locator(".console__footer")).toContainText("5,000 lines");
  await expect(consoleRegion.locator(".console__scroll > div")).toHaveAttribute("style", /height: 105000px/);
  expect(await consoleRegion.locator(".log-line").count()).toBeLessThan(100);

  await scroller.evaluate((element) => { element.scrollTop = 0; element.dispatchEvent(new Event("scroll")); });
  await expect(consoleRegion.getByText("virtualized line 1", { exact: true })).toBeVisible();
  await expect(consoleRegion.locator(".log-line__num").first()).toHaveText("1");
  await scroller.evaluate((element) => { element.scrollTop = element.scrollHeight; element.dispatchEvent(new Event("scroll")); });
  await expect(consoleRegion.getByText("virtualized line 5000", { exact: true })).toBeVisible();
  await expect(consoleRegion.locator(".log-line__num").last()).toHaveText("5000");
  expect(await consoleRegion.locator(".log-line").count()).toBeLessThan(100);
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

test(`${features("HTTP-STRUCTURED-LOG-VIRTUALIZATION-001")} virtualizes 1000 structured logs`, async ({ page }) => {
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  await page.unroute("**/api/deck/telemetry/logs?*");
  await page.route("**/api/deck/telemetry/logs?*", async (route) => route.fulfill({
    contentType: "application/x-ndjson",
    body: `${JSON.stringify({
      resourceLogs: [{
        resource: { attributes: [{ key: "service.name", value: { stringValue: "stress-api" } }] },
        scopeLogs: [{
          scope: { name: "Stress.Virtualization" },
          logRecords: Array.from({ length: 1_000 }, (_, index) => ({
            timeUnixNano: (1_783_670_400_000_000_000n + BigInt(index)).toString(),
            severityNumber: 9,
            severityText: "Information",
            body: { stringValue: `virtualized structured log ${index.toString().padStart(4, "0")}` },
            attributes: [],
            spanId: index.toString(16).padStart(16, "0"),
          })),
        }],
      }],
    })}\n`,
  }));
  await page.goto("/?backend=http");
  await page.getByRole("navigation").getByRole("button", { name: /^Structured Logs(?: \d+)?$/ }).click();

  const logs = page.getByRole("main").getByRole("region", { name: "Structured Logs" });
  const table = logs.getByRole("table");
  const wrapper = table.locator("..");
  await expect(logs.locator(".page__subtitle")).toHaveText("1,000 total · showing 1,000");
  await expect(wrapper).toHaveAttribute("data-virtualized", "true");
  await expect(table).toHaveAttribute("aria-rowcount", "1001");
  expect(await table.locator("tbody tr:not(.data__virtual-spacer)").count()).toBeLessThan(100);
  await wrapper.evaluate((element) => { element.scrollTop = element.scrollHeight; element.dispatchEvent(new Event("scroll")); });
  const tail = table.getByRole("row").filter({ hasText: "virtualized structured log 0000" });
  await expect(tail).toBeVisible();
  await tail.click();
  await expect(page.getByRole("dialog", { name: "Structured log entry details" })).toContainText("virtualized structured log 0000");
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
            attributes: [
              { key: "service.name", value: { stringValue: "stress-api" } },
              { key: "service.instance.id", value: { stringValue: "stress-api-1" } },
              { key: "deployment.environment.name", value: { stringValue: "Development" } },
            ],
            droppedAttributesCount: 3,
          },
          scopeSpans: [{
            scope: {
              name: "Stress.Telemetry",
              version: "2.1.0",
              attributes: [{ key: "scope.attribute", value: { stringValue: "scope value" } }],
              droppedAttributesCount: 2,
            },
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
                traceState: "vendor=state",
                parentSpanId: rootSpanId,
                flags: 257,
                name: "SELECT orders",
                kind: 3,
                startTimeUnixNano: "1783670400050000000",
                endTimeUnixNano: "1783670400180000000",
                attributes: [
                  { key: "db.system.name", value: { stringValue: "postgresql" } },
                  { key: "db.operation.name", value: { stringValue: "SELECT" } },
                ],
                droppedAttributesCount: 1,
                events: [{
                  timeUnixNano: "1783670400100000000",
                  name: "exception",
                  attributes: [
                    { key: "exception.type", value: { stringValue: "NpgsqlException" } },
                    { key: "exception.message", value: { stringValue: "Database unavailable" } },
                  ],
                  droppedAttributesCount: 2,
                }],
                droppedEventsCount: 4,
                links: [{
                  traceId,
                  spanId: rootSpanId,
                  traceState: "linked=state",
                  attributes: [{ key: "link.reason", value: { stringValue: "retry" } }],
                  droppedAttributesCount: 3,
                  flags: 1,
                }],
                droppedLinksCount: 5,
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
  const spanProperties = details.getByRole("group", { name: "Span properties" });
  const contextProperties = details.getByRole("group", { name: "Context properties" });
  const resourceProperties = details.getByRole("group", { name: "Resource properties" });
  const events = details.getByRole("group", { name: "Span events" });
  const links = details.getByRole("group", { name: "Span links" });
  await expect(spanProperties).toContainText(`SpanId${childSpanId}`);
  await expect(spanProperties).toContainText("StatusMessageDatabase unavailable");
  await expect(spanProperties).toContainText("db.system.namepostgresql");
  await expect(spanProperties).toContainText("Dropped attributes1");
  await expect(spanProperties).toContainText("Dropped events4");
  await expect(spanProperties).toContainText("Dropped links5");
  await expect(contextProperties).toContainText("SourceStress.Telemetry");
  await expect(contextProperties).toContainText("Version2.1.0");
  await expect(contextProperties).toContainText("TraceStatevendor=state");
  await expect(contextProperties).toContainText("Flags257");
  await expect(contextProperties).toContainText("scope.attributescope value");
  await expect(contextProperties).toContainText("Dropped scope attributes2");
  await expect(contextProperties.getByRole("link", { name: /^Open parent span / })).toHaveText(rootSpanId);
  await expect(resourceProperties).toContainText("service.instance.idstress-api-1");
  await expect(resourceProperties).toContainText("deployment.environment.nameDevelopment");
  await expect(resourceProperties).toContainText("Dropped resource attributes3");
  await expect(events).toContainText("exception");
  await expect(events).toContainText("exception.typeNpgsqlException");
  await expect(events).toContainText("2 dropped attributes");
  await expect(links).toContainText("link.reasonretry");
  await expect(links).toContainText("TraceState linked=state");
  await expect(links).toContainText("3 dropped attributes");
  await expect(page).toHaveURL(`/traces/detail/${traceId}?backend=http&span=${childSpanId}`);

  await testInfo.attach("http-backend-traces.png", {
    body: await page.screenshot({ animations: "disabled", fullPage: true }),
    contentType: "image/png",
  });

  await links.getByRole("link", { name: /^Open linked span / }).click();
  await expect(page).toHaveURL(`/traces/detail/${traceId}?backend=http&span=${rootSpanId}`);
  const rootDetails = page.getByRole("dialog", { name: "GET /orders" });
  await expect(rootDetails.getByRole("button", { name: "Backlinks 1" })).toBeVisible();
  await expect(rootDetails.getByRole("group", { name: "Span backlinks" })).toContainText("link.reasonretry");
});

test(`${features("HTTP-TRACE-VIRTUALIZATION-001")} virtualizes 1000 trace waterfalls`, async ({ page }) => {
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  await page.unroute("**/api/deck/telemetry/spans?*");
  await page.route("**/api/deck/telemetry/spans?*", async (route) => route.fulfill({
    contentType: "application/x-ndjson",
    body: `${JSON.stringify({
      resourceSpans: [{
        resource: { attributes: [{ key: "service.name", value: { stringValue: "stress-api" } }] },
        scopeSpans: [{
          scope: { name: "Stress.Virtualization" },
          spans: Array.from({ length: 1_000 }, (_, index) => ({
            traceId: index.toString(16).padStart(32, "0"),
            spanId: index.toString(16).padStart(16, "0"),
            name: `trace operation ${index.toString().padStart(4, "0")}`,
            kind: 2,
            startTimeUnixNano: (1_783_670_400_000_000_000n + BigInt(index) * 1_000_000n).toString(),
            endTimeUnixNano: (1_783_670_400_010_000_000n + BigInt(index) * 1_000_000n).toString(),
            status: { code: 1 },
          })),
        }],
      }],
    })}\n`,
  }));
  await page.goto("/traces?backend=http");

  const traces = page.getByRole("main").getByRole("region", { name: "Traces" });
  const scroller = traces.locator(".page__body");
  await expect(traces.locator(".page__subtitle")).toHaveText("1000 traces · 1,000 spans");
  await expect(scroller).toHaveAttribute("data-virtualized", "true");
  await expect(scroller).toHaveAttribute("aria-setsize", "1000");
  expect(await traces.locator(".wf__trace").count()).toBeLessThan(100);
  await scroller.evaluate((element) => { element.scrollTop = element.scrollHeight; element.dispatchEvent(new Event("scroll")); });
  const tail = traces.locator(".wf__trace").filter({ hasText: "trace operation 0000" });
  await expect(tail).toBeVisible();
  await tail.locator(".wf__span-open").press("Enter");
  await expect(page.getByRole("dialog", { name: "trace operation 0000" })).toBeVisible();
});

test(`${features("HTTP-TRACES-001")} formats trace durations consistently across scales`, async ({ page }) => {
  const durations = [
    ["micro trace", 500_000n, "500µs"],
    ["small trace", 5_000_000n, "5.0ms"],
    ["medium trace", 250_000_000n, "250ms"],
    ["seconds trace", 2_500_000_000n, "2.50s"],
    ["minute trace", 65_000_000_000n, "1m 5s"],
  ] as const;
  const start = 1_783_670_400_000_000_000n;
  await page.route("**/api/deck/config", async (route) => route.fulfill({ json: config }));
  await page.route("**/api/deck/resources", async (route) => route.fulfill({ json: [resource] }));
  await page.unroute("**/api/deck/telemetry/spans?*");
  await page.route("**/api/deck/telemetry/spans?*", async (route) => route.fulfill({
    contentType: "application/x-ndjson",
    body: `${JSON.stringify({
      resourceSpans: [{
        resource: { attributes: [{ key: "service.name", value: { stringValue: "stress-api" } }] },
        scopeSpans: [{
          scope: { name: "Stress.Duration" },
          spans: durations.map(([name, duration], index) => ({
            traceId: (index + 1).toString(16).padStart(32, "0"),
            spanId: (index + 1).toString(16).padStart(16, "0"),
            name,
            kind: 2,
            startTimeUnixNano: (start + BigInt(index) * 100_000_000_000n).toString(),
            endTimeUnixNano: (start + BigInt(index) * 100_000_000_000n + duration).toString(),
            status: { code: 1 },
          })),
        }],
      }],
    })}\n`,
  }));
  await page.goto("/traces?backend=http");

  for (const [name, , expected] of durations) {
    const trace = page.locator(".wf__trace").filter({ hasText: name });
    await expect(trace.locator(".wf__head-dur")).toHaveText(expected);
  }
});

test(`${features("HTTP-TRACE-CLEAR-001")} clears selected and all traces through the HTTP backend`, async ({ page }) => {
  interface TestSpan {
    resourceName: string;
    traceId: string;
    spanId: string;
    name: string;
    startUnixNano: string;
  }

  let records: TestSpan[] = [
    {
      resourceName: "stress-api",
      traceId: "11111111111111111111111111111111",
      spanId: "1111111111111111",
      name: "GET /orders",
      startUnixNano: "1783670400000000000",
    },
    {
      resourceName: "stress-worker",
      traceId: "22222222222222222222222222222222",
      spanId: "2222222222222222",
      name: "process order",
      startUnixNano: "1783670401000000000",
    },
  ];
  const clearRequests: Array<string | null> = [];
  const toOtlpData = () => ({
    resourceSpans: records.map((record) => ({
      resource: {
        attributes: [{ key: "service.name", value: { stringValue: record.resourceName } }],
      },
      scopeSpans: [{
        scope: { name: "Stress.Telemetry" },
        spans: [{
          traceId: record.traceId,
          spanId: record.spanId,
          name: record.name,
          kind: 2,
          startTimeUnixNano: record.startUnixNano,
          endTimeUnixNano: (BigInt(record.startUnixNano) + 10_000_000n).toString(),
          status: { code: 1 },
          attributes: [{ key: "http.request.method", value: { stringValue: "GET" } }],
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
  await page.unroute("**/api/deck/telemetry/spans?*");
  await page.route("**/api/deck/telemetry/spans*", async (route) => {
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

  await page.goto("/traces?backend=http");

  const traces = page.getByRole("main").getByRole("region", { name: "Traces" });
  await expect(traces.locator(".wf__trace")).toHaveCount(2);
  await traces.getByRole("combobox", { name: "Resource" }).selectOption("stress-api");
  await traces.getByRole("button", { name: "Clear traces" }).click();
  await page.getByRole("menuitem", { name: "Clear stress-api" }).click();

  await expect(page.getByRole("status")).toHaveText("Cleared traces for stress-api.");
  await expect(traces.locator(".wf__trace")).toHaveCount(1);
  await expect(traces).toContainText("process order");
  await expect(traces).not.toContainText("GET /orders");
  await expect(traces.locator(".page__subtitle")).toHaveText("1 traces · 1 spans");

  await traces.getByRole("button", { name: "Clear traces" }).click();
  await page.getByRole("menuitem", { name: "Clear all resources" }).click();

  await expect(page.getByRole("status")).toHaveText("Cleared all traces.");
  await expect(traces.locator(".page__subtitle")).toHaveText("0 traces · 0 spans");
  await expect(traces).toContainText("No traces match your filter.");
  expect(clearRequests).toEqual(["stress-api", null]);
});

test(`${features("HTTP-EMPTY-TELEMETRY-001")} distinguishes every settled empty metrics state`, async ({ page }) => {
  let summaries: Array<Record<string, unknown>> = [];
  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.unroute("**/api/deck/telemetry/metrics");
  await page.route("**/api/deck/telemetry/metrics", async (route) => {
    await route.fulfill({ json: summaries });
  });
  await page.route("**/api/deck/telemetry/metrics/series?*", async (route) => {
    await route.fulfill({
      json: {
        name: "requests",
        resourceName: resource.name,
        meterName: "Stress.Api",
        unit: "{request}",
        kind: "counter",
        timestampsMs: [],
        values: [],
      },
    });
  });

  await page.goto("/metrics?backend=http");
  const metrics = page.getByRole("main").getByRole("region", { name: "Metrics" });
  await expect(metrics.locator(".page__subtitle")).toHaveText("Select a resource");
  await expect(metrics.getByRole("heading", { name: "No metric resources" })).toBeVisible();
  await expect(metrics).not.toContainText("Loading…");

  await page.goto(`/metrics/resource/${resource.name}?backend=http`);
  await expect(metrics.getByRole("heading", { name: "No meters for this resource" })).toBeVisible();

  summaries = [{
    name: "requests",
    description: "Handled requests.",
    unit: "{request}",
    resourceName: resource.name,
    meterName: "Stress.Api",
    kind: "counter",
    lastValue: 0,
    pointCount: 0,
  }];
  await page.goto(`/metrics/resource/${resource.name}/meter/Missing?backend=http`);
  await expect(metrics.getByRole("heading", { name: "Meter not found" })).toBeVisible();

  await page.goto(`/metrics/resource/${resource.name}/meter/Stress.Api/instrument/missing?backend=http`);
  await expect(metrics.getByRole("heading", { name: "Instrument not found" })).toBeVisible();

  await page.goto(`/metrics/resource/${resource.name}/meter/Stress.Api/instrument/requests?backend=http`);
  await expect(metrics.getByText("No samples in this window yet.", { exact: true }).first()).toBeVisible();
});

test(`${features("HTTP-METRICS-001", "HTTP-METRIC-CLEAR-001")} loads, charts, and clears HTTP metric telemetry`, async ({ page }) => {
  let summaries = [
    {
      name: "http.server.request.duration",
      description: "Server request duration.",
      unit: "ms",
      resourceName: "stress-api",
      meterName: "OpenTelemetry.Instrumentation.AspNetCore",
      kind: "histogram",
      lastValue: 42,
      pointCount: 3,
    },
    {
      name: "worker.jobs",
      description: "Completed jobs.",
      unit: "{job}",
      resourceName: "stress-worker",
      meterName: "Stress.Worker",
      kind: "counter",
      lastValue: 7,
      pointCount: 3,
    },
  ];
  const seriesRequests: Array<Record<string, string>> = [];
  const clearRequests: Array<string | null> = [];

  await page.route("**/api/deck/config", async (route) => {
    await route.fulfill({ json: config });
  });
  await page.route("**/api/deck/resources", async (route) => {
    await route.fulfill({ json: [resource] });
  });
  await page.unroute("**/api/deck/telemetry/metrics");
  await page.route("**/api/deck/telemetry/metrics/series?*", async (route) => {
    const url = new URL(route.request().url());
    seriesRequests.push(Object.fromEntries(url.searchParams));
    const isHistogram = url.searchParams.get("instrument") === "http.server.request.duration";
    const histogramMode = url.searchParams.get("histogramMode") ?? "percentiles";
    await route.fulfill({
      json: isHistogram
        ? {
            name: "http.server.request.duration",
            resourceName: "stress-api",
            meterName: "OpenTelemetry.Instrumentation.AspNetCore",
            unit: "ms",
            kind: "histogram",
            timestampsMs: [1_783_670_400_000, 1_783_670_401_000, 1_783_670_402_000],
            histogramMode,
            ...(histogramMode === "count" ? { values: [3, 4, 5], showCount: true } : {}),
            ...(histogramMode === "sum" ? { sum: [90, 120, 150] } : {}),
            ...(histogramMode === "buckets" ? {
              bucketBounds: [25, 50],
              buckets: [
                { upperBound: 25, values: [1, 2, 2] },
                { upperBound: 50, values: [2, 1, 2] },
                { upperBound: null, values: [0, 1, 1] },
              ],
            } : {}),
            ...(histogramMode === "percentiles" ? {
              p50: [30, 35, 40],
              p90: [45, 50, 55],
              p99: [60, 65, 70],
            } : {}),
            dimensionFilters: [{ name: "http.method", values: ["GET", "POST"] }],
            dimensions: [],
            exemplars: [],
            hasOverflow: false,
          }
        : {
            name: "worker.jobs",
            resourceName: "stress-worker",
            meterName: "Stress.Worker",
            unit: "{job}",
            kind: "counter",
            timestampsMs: [1_783_670_400_000, 1_783_670_401_000, 1_783_670_402_000],
            values: [1, 2, 3],
          },
    });
  });
  await page.route("**/api/deck/telemetry/metrics*", async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === "DELETE") {
      const resourceName = url.searchParams.get("resource");
      clearRequests.push(resourceName);
      summaries = resourceName === null
        ? []
        : summaries.filter((summary) => summary.resourceName !== resourceName);
      await route.fulfill({ status: 204 });
      return;
    }
    await route.fulfill({ json: summaries });
  });

  await page.goto("/metrics?backend=http");

  const metrics = page.getByRole("main").getByRole("region", { name: "Metrics" });
  await expect(metrics.getByRole("combobox", { name: "Resource" })).toHaveValue("stress-api");
  await expect(metrics.locator(".metric-item")).toHaveCount(1);
  await expect(metrics.locator(".metric-chart canvas")).toBeVisible();
  await expect.poll(() => seriesRequests.length).toBeGreaterThan(0);
  expect(seriesRequests[0]).toMatchObject({
    resource: "stress-api",
    meter: "OpenTelemetry.Instrumentation.AspNetCore",
    instrument: "http.server.request.duration",
    windowSeconds: "300",
    maxPoints: "600",
    histogramMode: "percentiles",
  });
  const aggregation = metrics.getByRole("group", { name: "Histogram aggregation" });
  for (const mode of ["Count", "Sum", "Buckets"] as const) {
    await aggregation.getByRole("button", { name: mode }).click();
    await expect.poll(() => seriesRequests.some((request) => request.histogramMode === mode.toLowerCase())).toBe(true);
    await expect(metrics.locator(".metric-chart canvas")).toBeVisible();
  }
  await page.goto(
    "/metrics/resource/stress-api/meter/OpenTelemetry.Instrumentation.AspNetCore/instrument/http.server.request.duration"
      + `?backend=http&paused=true&dimension=${encodeURIComponent('["http.method",["GET"]]')}`,
  );
  await expect.poll(() => seriesRequests.some((request) => request["dimension.http.method"] === "s:GET")).toBe(true);

  await metrics.getByRole("button", { name: "Clear metrics" }).click();
  await page.getByRole("menuitem", { name: "Clear stress-api" }).click();
  await expect(page.getByRole("status")).toHaveText("Cleared metrics for stress-api.");
  await expect(metrics.getByRole("combobox", { name: "Resource" })).toHaveValue("stress-worker");
  await expect(metrics).toContainText("worker.jobs");

  await metrics.getByRole("button", { name: "Clear metrics" }).click();
  await page.getByRole("menuitem", { name: "Clear all resources" }).click();
  await expect(page.getByRole("status")).toHaveText("Cleared all metrics.");
  await expect(metrics).toContainText("No metric resources");
  expect(clearRequests).toEqual(["stress-api", null]);
});

const missingFeatures = getMissingHttpBackendFeatures(coveredFeatures);
if (missingFeatures.length > 0) {
  throw new Error(`HTTP backend features without Playwright coverage: ${missingFeatures.join(", ")}`);
}
