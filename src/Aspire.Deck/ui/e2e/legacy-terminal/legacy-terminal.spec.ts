import { expect, test, type Page, type TestInfo } from "@playwright/test";

const dashboardUrl = new URL(process.env.ASPIRE_LEGACY_TERMINAL_DASHBOARD_URL!);
const loginPath = dashboardUrl.pathname === "/login" && dashboardUrl.searchParams.has("t")
  ? `${dashboardUrl.pathname}${dashboardUrl.search}`
  : "/";
const terminalResource = process.env.ASPIRE_LEGACY_TERMINAL_RESOURCE ?? "shell";
const browserErrors = new WeakMap<Page, string[]>();

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => errors.push(`page: ${error.message}`));

  await page.goto(loginPath, { waitUntil: "domcontentloaded" });
  await expect(page.getByRole("navigation")).toBeVisible();
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
});

test("[SHELL-UNSECURED-001] warns about unsecured endpoints and persists dismissal", async ({ page }) => {
  await page.goto("/");

  const warning = page.locator(".fluent-messagebar").filter({ hasText: "Endpoint is unsecured" });
  await expect(warning).toContainText("Untrusted apps can send telemetry to the dashboard.");
  await expect(warning).toContainText("Untrusted apps can access telemetry data via the API.");
  await expect(warning.getByRole("link", { name: "More information", exact: true }))
    .toHaveAttribute("href", "https://aka.ms/aspire/api-endpoint-unsecured");

  await warning.locator(".fluent-messagebar-close").click();
  await expect(warning).toBeHidden();
  await expect.poll(() => page.evaluate(() => localStorage.getItem("Aspire_Security_UnsecuredEndpointMessageDismissed")))
    .toBe("true");

  await page.reload();
  await expect(page.locator(".fluent-messagebar").filter({ hasText: "Endpoint is unsecured" })).toHaveCount(0);
});

test("[CONSOLE-TERMINAL-001, CONSOLE-TERMINAL-FONT-001, CONSOLE-TERMINAL-SIZE-001] controls a live legacy terminal", async ({ page }, testInfo: TestInfo) => {
  await page.goto(`/consolelogs/resource/${encodeURIComponent(terminalResource)}`);

  await expect(page.locator(".terminal-container .xterm")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole("textbox", { name: "Terminal input", exact: true })).toHaveCount(1);
  const takeControl = page.getByRole("button", { name: "Take control", exact: true });
  await expect(takeControl).toBeEnabled({ timeout: 30_000 });
  await takeControl.click();
  await expect(page.getByRole("button", { name: "Primary", exact: true })).toBeDisabled();

  const fontSize = page.getByTitle("Terminal font size", { exact: true });
  await expect(fontSize).toHaveText("13");
  await page.getByTitle("Increase font size", { exact: true }).click();
  await expect(fontSize).toHaveText("14");
  await page.getByTitle("Decrease font size", { exact: true }).click();
  await expect(fontSize).toHaveText("13");

  await page.getByRole("combobox", { name: "Terminal grid size", exact: true }).selectOption("100x30");
  await expect(page.getByTitle("Current terminal grid", { exact: true })).toHaveText("100 × 30");

  const body = await page.screenshot({ animations: "disabled", fullPage: true });
  await testInfo.attach("legacy-terminal.png", { body, contentType: "image/png" });
});
