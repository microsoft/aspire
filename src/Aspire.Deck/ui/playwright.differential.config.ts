import { defineConfig } from "@playwright/test";

// Differential runs drive the shipping Blazor dashboard and the Deck UI against the *same* live
// AppHost and compare what each renders. That makes them stronger than the mock-backed parity
// specs, which can only prove the Deck UI is self-consistent: here both sides read one source of
// truth, so a divergence is necessarily a real behavioural difference rather than a stale fixture.
const dashboardUrl = process.env.ASPIRE_DASHBOARD_URL;
const dashboardAotUrl = process.env.ASPIRE_DASHBOARD_AOT_URL;
const dashboardBackend = process.env.ASPIRE_DASHBOARD_BACKEND ?? "aot";
const reuseExistingServer = process.env.ASPIRE_REUSE_EXISTING_SERVER === "true";
const port = Number(process.env.ASPIRE_DECK_E2E_PORT ?? 1430);

if (!dashboardUrl) {
  throw new Error("ASPIRE_DASHBOARD_URL must point to a running Blazor dashboard.");
}
if (dashboardBackend !== "http" && dashboardBackend !== "aot") {
  throw new Error("ASPIRE_DASHBOARD_BACKEND must be either 'http' or 'aot'.");
}
if (dashboardBackend === "aot" && !dashboardAotUrl) {
  throw new Error("ASPIRE_DASHBOARD_AOT_URL must point to the AOT backend when ASPIRE_DASHBOARD_BACKEND=aot.");
}
if (!process.env.ASPIRE_DASHBOARD_BROWSER_TOKEN) {
  throw new Error("ASPIRE_DASHBOARD_BROWSER_TOKEN must hold the dashboard login token.");
}

const webServerEnvironment: Record<string, string> = {
  ASPIRE_DASHBOARD_URL: dashboardUrl,
  ASPIRE_DASHBOARD_BACKEND: dashboardBackend,
};
if (dashboardAotUrl) {
  webServerEnvironment.ASPIRE_DASHBOARD_AOT_URL = dashboardAotUrl;
}

export default defineConfig({
  testDir: "./e2e/differential",
  outputDir: "./test-results-differential",
  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  retries: 0,
  reporter: [
    ["list"],
    ["html", { open: "never", outputFolder: "playwright-report-differential" }],
  ],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    browserName: "chromium",
    viewport: { width: 1440, height: 1000 },
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
  },
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${port} --strictPort`,
    env: webServerEnvironment,
    url: `http://127.0.0.1:${port}/?backend=${dashboardBackend}`,
    reuseExistingServer,
    timeout: 120_000,
  },
});
