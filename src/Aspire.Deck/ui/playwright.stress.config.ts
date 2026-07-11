import { defineConfig } from "@playwright/test";

const dashboardUrl = process.env.ASPIRE_DASHBOARD_URL;

if (!dashboardUrl) {
  throw new Error("ASPIRE_DASHBOARD_URL must point to a running Stress dashboard.");
}

export default defineConfig({
  testDir: "./e2e/live",
  outputDir: "./test-results-stress",
  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  retries: 0,
  reporter: [
    ["list"],
    ["html", { open: "never", outputFolder: "playwright-report-stress" }],
  ],
  use: {
    baseURL: "http://127.0.0.1:1430",
    browserName: "chromium",
    viewport: { width: 1440, height: 1000 },
    screenshot: "on",
    trace: "on",
    video: "on",
  },
  webServer: {
    command: "npm run dev -- --host 127.0.0.1",
    env: { ASPIRE_DASHBOARD_URL: dashboardUrl },
    url: "http://127.0.0.1:1430/?backend=http",
    reuseExistingServer: false,
    timeout: 120_000,
  },
});
