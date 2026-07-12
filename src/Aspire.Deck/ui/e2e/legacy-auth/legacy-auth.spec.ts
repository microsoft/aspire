import { expect, test, type Page, type TestInfo } from "@playwright/test";

const browserErrors = new WeakMap<Page, string[]>();

test.beforeEach(async ({ page }) => {
  const errors: string[] = [];
  browserErrors.set(page, errors);
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(`console: ${message.text()}`);
  });
  page.on("pageerror", (error) => errors.push(`page: ${error.message}`));
});

test.afterEach(async ({ page }) => {
  expect(browserErrors.get(page) ?? [], "Unexpected browser errors").toEqual([]);
});

test("[SHELL-USER-001] shows the authenticated user and posts sign out", async ({ page }, testInfo: TestInfo) => {
  await page.goto("/");
  await expect(page.getByRole("navigation")).toBeVisible({ timeout: 30_000 });

  const profile = page.locator(".fluent-profile-menu");
  await expect(profile).toContainText("AL");
  await profile.click();
  await expect(page.getByText("Logged in as:", { exact: true })).toBeVisible();
  await expect(page.getByText("Ada Lovelace", { exact: true }).last()).toBeVisible();
  await expect(page.getByText("ada@example.com", { exact: true })).toBeVisible();

  const body = await page.screenshot({ animations: "disabled", fullPage: true });
  await testInfo.attach("legacy-user-profile.png", { body, contentType: "image/png" });

  const logoutRequest = page.waitForRequest((request) =>
    request.url().endsWith("/authentication/logout") && request.method() === "POST");
  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await logoutRequest;
});
