import { expect, test, type Page } from "@playwright/test";

interface TerminalResource {
  name: string;
  displayName: string;
  terminalReplicaIndex: number | null;
}

const dashboardBrowserToken = process.env.ASPIRE_DASHBOARD_BROWSER_TOKEN;
const browserErrors = new WeakMap<Page, string[]>();

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => errors.push(`page: ${error.message}`));

  if (dashboardBrowserToken) {
    await page.goto(`/login?t=${encodeURIComponent(dashboardBrowserToken)}`);
  }
  await page.goto("/?backend=http");
  await expect(page.getByTitle("Resources: Connected")).toBeVisible({ timeout: 30_000 });
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
});

test("[TERMINAL-LIVE-001] controls a live HMP terminal through the React dashboard", async ({ page }) => {
  const resources = await page.evaluate(async () => {
    const response = await fetch("/api/deck/resources", { cache: "no-store", credentials: "same-origin" });
    if (!response.ok) throw new Error(`Resource request failed with ${response.status}.`);
    return await response.json();
  }) as TerminalResource[];
  const terminalResource = resources.find((resource) => resource.terminalReplicaIndex !== null);
  if (terminalResource === undefined) throw new Error("No terminal-enabled resource was found.");

  await page.getByRole("navigation").getByRole("button", { name: "Console" }).click();
  await page.getByRole("combobox", { name: "Resource" }).selectOption(terminalResource.name);

  const terminal = page.getByLabel(`${terminalResource.displayName} terminal`);
  await expect(terminal.getByRole("button", { name: "Take control" })).toBeEnabled({ timeout: 30_000 });
  await expect(terminal.getByLabel("Terminal dimensions")).not.toHaveText("0 × 0");

  await terminal.getByRole("button", { name: "Take control" }).click();
  await expect(terminal.getByText("In control", { exact: true })).toBeVisible();
  await terminal.getByRole("combobox", { name: "Terminal grid size" }).selectOption("100x30");
  await expect(terminal.getByLabel("Terminal dimensions")).toHaveText("100 × 30");

  await terminal.getByRole("button", { name: "Release control" }).click();
  await expect(terminal.getByText("View only", { exact: true })).toBeVisible();
});
