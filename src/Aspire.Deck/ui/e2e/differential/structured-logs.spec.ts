import { expect, test, type Browser, type Page } from "@playwright/test";

// Structured logs are append-only and both UIs render them as a table with the same columns, so the
// rows can be compared as *sets* rather than by position. They are deliberately not compared in
// order: the dashboard renders oldest-first (its telemetry repository keeps logs in arrival order
// and `GetLogs` applies no sort) while Deck renders newest-first via `compareNewestFirst`.
//
// The comparison is scoped to a low-volume project resource. Comparing the unfiltered view is not
// meaningful because each UI retains a bounded, differently-sized window over a stream that a
// chatty resource keeps advancing, so the two windows are never the same rows.

const blazorUrl = requiredEnv("ASPIRE_DASHBOARD_URL");
const loginToken = requiredEnv("ASPIRE_DASHBOARD_BROWSER_TOKEN");
const backend = process.env.ASPIRE_DASHBOARD_BACKEND ?? "aot";

const READY_TIMEOUT_MS = 90_000;

// Long enough for the live structured-log stream to deliver records after the initial snapshot,
// which is what triggers Deck to trim its retained buffer.
const LIVE_APPEND_SETTLE_MS = 15_000;

// The comparison resource is discovered at run time rather than hardcoded. Both sides retain a
// bounded number of records (10,000 by default), so a resource that only logs during startup ages
// out of the window once a long-running app has produced enough telemetry -- pinning one name makes
// the test pass or fail based on how long the playground has been up rather than on real parity.
// Any resource contributing few enough rows that neither UI virtualizes part of it away will do.
const MAX_ROWS_FOR_COMPARISON = 50;

interface LogRow {
  resource: string;
  level: string;
  timestamp: string;
  message: string;
  trace: string;
}

function requiredEnv(name: string): string {
  const value = process.env[name];
  if (value === undefined || value === "") {
    throw new Error(`${name} must be set for differential runs.`);
  }

  return value;
}

async function login(page: Page, origin: string): Promise<void> {
  await page.goto(`${origin}/login?t=${loginToken}`, { waitUntil: "domcontentloaded" }).catch(() => undefined);
}

/**
 * Reads the log grid as structured rows. Both grids virtualize, and the dashboard's virtualizer
 * brackets its rows with empty spacer `<tr>` elements, so rows without a full set of cells are
 * dropped rather than being compared as blanks.
 */
async function readRows(page: Page, rowSelector: string): Promise<LogRow[]> {
  return await page.evaluate((selector) => {
    return [...document.querySelectorAll(selector)].flatMap((row) => {
      const cells = [...row.querySelectorAll("td")]
        .map((cell) => (cell.textContent ?? "").replace(/\s+/g, " ").trim());
      if (cells.length < 5 || cells[0] === "") {
        return [];
      }

      return [{
        resource: cells[0] ?? "",
        level: cells[1] ?? "",
        timestamp: cells[2] ?? "",
        message: cells[3] ?? "",
        trace: cells[4] ?? "",
      }];
    });
  }, rowSelector);
}

function key(row: LogRow): string {
  return `${row.resource}|${row.level}|${row.timestamp}|${row.message}|${row.trace}`;
}

async function openBlazor(browser: Browser, resource: string): Promise<LogRow[]> {
  const page = await browser.newPage();
  await login(page, blazorUrl);
  await page.goto(`${blazorUrl}/structuredlogs/resource/${resource}`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".table-wrap tbody tr td").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
  const rows = await readRows(page, ".table-wrap tbody tr");
  await page.close();
  return rows;
}

/**
 * Picks a resource both UIs can render in full. The dashboard is read unfiltered and the resource
 * with the fewest rows in its current window is chosen, so the test tracks whatever the playground
 * happens to be emitting instead of assuming a particular resource is still retained.
 */
async function pickComparisonResource(browser: Browser): Promise<string | null> {
  const page = await browser.newPage();
  await login(page, blazorUrl);
  await page.goto(`${blazorUrl}/structuredlogs`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".table-wrap tbody tr td").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
  const rows = await readRows(page, ".table-wrap tbody tr");
  await page.close();

  const counts = new Map<string, number>();
  for (const row of rows) {
    counts.set(row.resource, (counts.get(row.resource) ?? 0) + 1);
  }

  const smallest = [...counts.entries()]
    .filter(([, count]) => count <= MAX_ROWS_FOR_COMPARISON)
    .sort((left, right) => left[1] - right[1])[0];

  return smallest?.[0] ?? null;
}

async function openDeck(browser: Browser, baseURL: string, resource: string): Promise<LogRow[]> {
  const page = await browser.newPage();
  // Deck talks to the AOT backend cross-origin, so it needs its own session on the Deck origin in
  // addition to the dashboard one; without it every request is redirected to /login.
  await login(page, baseURL);
  await page.goto(`${baseURL}/structuredlogs/resource/${resource}?backend=${backend}`, { waitUntil: "domcontentloaded" });
  // Deck renders a "No structured logs." placeholder row while the first snapshot is in flight, and
  // that row satisfies a plain `td` wait. Wait for the placeholder to be gone instead.
  await expect(page.locator("table tbody tr", { hasText: resource }).first())
    .toBeVisible({ timeout: READY_TIMEOUT_MS });
  // Deck trims its retained buffer only when a live record arrives, so reading immediately after the
  // initial snapshot would pass even with a buffer far smaller than the backend's. Let the live
  // stream deliver records first so the trim has actually run against this resource's rows.
  await page.waitForTimeout(LIVE_APPEND_SETTLE_MS);
  const rows = await readRows(page, "table tbody tr");
  await page.close();
  return rows;
}

test.describe("structured logs differential", () => {
  test("both dashboards render the same rows for a resource", async ({ browser, baseURL }) => {
    const resource = await pickComparisonResource(browser);
    test.skip(resource === null, "No resource is currently small enough to render in full on both UIs.");

    const blazorRows = await openBlazor(browser, resource!);
    const deckRows = await openDeck(browser, baseURL!, resource!);

    expect(blazorRows.length).toBeGreaterThan(0);

    // Set comparison, not sequence comparison: the two UIs sort in opposite directions.
    expect([...deckRows].map(key).sort()).toEqual([...blazorRows].map(key).sort());
  });

  test("the resource deep link filters instead of falling back to all resources", async ({ browser, baseURL }) => {
    const resource = await pickComparisonResource(browser);
    test.skip(resource === null, "No resource is currently small enough to render in full on both UIs.");

    const deckRows = await openDeck(browser, baseURL!, resource!);

    expect(deckRows.length).toBeGreaterThan(0);
    // Deck previously built its resource filter from the rows it had already retained, so a resource
    // whose logs had aged out of that buffer was not a selectable option and the deep link silently
    // degraded to the unfiltered view -- which showed a chatty unrelated resource instead.
    expect([...new Set(deckRows.map((row) => row.resource))]).toEqual([resource]);
  });

  test("timestamps outside today carry the date on both dashboards", async ({ browser, baseURL }) => {
    const resource = await pickComparisonResource(browser);
    test.skip(resource === null, "No resource is currently small enough to render in full on both UIs.");

    const blazorRows = await openBlazor(browser, resource!);
    const deckRows = await openDeck(browser, baseURL!, resource!);

    // The dashboard renders this column with FormatTimeWithOptionalDate(MillisecondsDisplay.Truncated),
    // which prefixes the short date once the timestamp is not from today. Deck rendered time-only,
    // so a log from yesterday was indistinguishable from one written minutes ago.
    const shape = (value: string): string => value
      .replace(/\d/g, "#")
      .replace(/(AM|PM)/, "%");

    expect(new Set(deckRows.map((row) => shape(row.timestamp))))
      .toEqual(new Set(blazorRows.map((row) => shape(row.timestamp))));
  });
});
