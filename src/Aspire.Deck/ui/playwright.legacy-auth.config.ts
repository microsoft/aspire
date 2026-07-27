import { defineConfig } from "@playwright/test";

const dashboardUrl = process.env.ASPIRE_LEGACY_AUTH_DASHBOARD_URL;

if (!dashboardUrl) {
  throw new Error("ASPIRE_LEGACY_AUTH_DASHBOARD_URL must point to a running OpenID Connect legacy dashboard.");
}

export default defineConfig({
  testDir: "./e2e/legacy-auth",
  outputDir: "./test-results-legacy-auth",
  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  retries: 0,
  reporter: [
    ["list"],
    ["html", { open: "never", outputFolder: "playwright-report-legacy-auth" }],
  ],
  use: {
    baseURL: dashboardUrl,
    browserName: "chromium",
    ignoreHTTPSErrors: true,
    viewport: { width: 1440, height: 1000 },
    screenshot: "on",
    trace: "on",
    video: "on",
  },
});
