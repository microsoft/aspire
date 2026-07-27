import { defineConfig } from "@playwright/test";

const dashboardUrl = process.env.ASPIRE_DASHBOARD_URL;
const dashboardAotUrl = process.env.ASPIRE_DASHBOARD_AOT_URL;
const dashboardBackend = process.env.ASPIRE_DASHBOARD_BACKEND ?? "http";
const reuseExistingServer = process.env.ASPIRE_REUSE_EXISTING_SERVER === "true";

if (!dashboardUrl) {
  throw new Error("ASPIRE_DASHBOARD_URL must point to a running Stress dashboard.");
}
if (dashboardBackend !== "http" && dashboardBackend !== "aot") {
  throw new Error("ASPIRE_DASHBOARD_BACKEND must be either 'http' or 'aot'.");
}
if (dashboardBackend === "aot" && !dashboardAotUrl) {
  throw new Error("ASPIRE_DASHBOARD_AOT_URL must point to the AOT backend when ASPIRE_DASHBOARD_BACKEND=aot.");
}

const webServerEnvironment: Record<string, string> = {
  ASPIRE_DASHBOARD_URL: dashboardUrl,
  ASPIRE_DASHBOARD_BACKEND: dashboardBackend,
};
if (dashboardAotUrl) {
  webServerEnvironment.ASPIRE_DASHBOARD_AOT_URL = dashboardAotUrl;
}

export default defineConfig({
  testDir: "./e2e/live",
  testIgnore: ["terminal.spec.ts"],
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
    env: webServerEnvironment,
    url: `http://127.0.0.1:1430/?backend=${dashboardBackend}`,
    reuseExistingServer,
    timeout: 120_000,
  },
});
