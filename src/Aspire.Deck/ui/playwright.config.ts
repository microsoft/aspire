import { defineConfig } from "@playwright/test";

const isCi = Boolean(process.env.CI);

// The dev server port is overridable because `reuseExistingServer` will happily adopt a Vite
// process started from a *different* checkout that happens to hold the default port, which makes
// the suite silently exercise another worktree's code. Set ASPIRE_DECK_E2E_PORT to get an isolated
// server when several checkouts are active at once.
const port = Number(process.env.ASPIRE_DECK_E2E_PORT ?? 1430);
const baseURL = `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: "./e2e",
  testIgnore: ["legacy/**", "legacy-auth/**", "legacy-terminal/**", "live/**"],
  outputDir: "./test-results",
  fullyParallel: true,
  forbidOnly: isCi,
  retries: isCi ? 1 : 0,
  reporter: [
    ["list"],
    ["html", { open: "never", outputFolder: "playwright-report" }],
  ],
  use: {
    baseURL,
    browserName: "chromium",
    viewport: { width: 1440, height: 1000 },
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
    video: "retain-on-failure",
  },
  webServer: {
    command: `npm run dev -- --host 127.0.0.1 --port ${port} --strictPort`,
    url: `${baseURL}/?view=toolkit`,
    reuseExistingServer: !isCi,
    timeout: 120_000,
  },
});
