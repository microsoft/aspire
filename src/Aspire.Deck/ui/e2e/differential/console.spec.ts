import { expect, test, type Browser, type Page } from "@playwright/test";

// Console output is append-only: once a line is written its number and text never change. That
// makes it the one streaming surface that can be compared exactly, by intersecting the line numbers
// both UIs happen to have rendered rather than requiring them to render the same window.

const blazorUrl = requiredEnv("ASPIRE_DASHBOARD_URL");
const loginToken = requiredEnv("ASPIRE_DASHBOARD_BROWSER_TOKEN");
const backend = process.env.ASPIRE_DASHBOARD_BACKEND ?? "aot";

const READY_TIMEOUT_MS = 90_000;

// A project resource, so both UIs have real console output to compare. Telemetry-only resources
// (such as external-log-source) have no console stream at all, and the two UIs fall back
// differently when the named resource has none.
const CONSOLE_RESOURCE = "empty-0000";

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
 * Reads console lines as a line-number -> text map. The two UIs use different class names for the
 * same structure, and both virtualize, so each returns only the window it currently has mounted.
 */
async function readConsole(page: Page, lineSelector: string, numberSelector: string, textSelector: string): Promise<Map<number, string>> {
  const entries = await page.evaluate(({ lineSelector, numberSelector, textSelector }) => {
    const text = (element: Element | null): string =>
      element === null ? "" : (element as HTMLElement).innerText.replace(/\s+$/, "");

    return [...document.querySelectorAll(lineSelector)].flatMap((line) => {
      const number = Number.parseInt(text(line.querySelector(numberSelector)).trim(), 10);
      if (!Number.isFinite(number)) {
        return [];
      }

      return [[number, text(line.querySelector(textSelector))] as const];
    });
  }, { lineSelector, numberSelector, textSelector });

  return new Map(entries);
}

async function openBlazorConsole(browser: Browser): Promise<Page> {
  const page = await browser.newPage();
  await login(page, blazorUrl);
  await page.goto(`${blazorUrl}/consolelogs/resource/${CONSOLE_RESOURCE}`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".log-line-area").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
  return page;
}

async function openDeckConsole(browser: Browser, baseURL: string): Promise<Page> {
  const page = await browser.newPage();
  await login(page, baseURL);
  await page.goto(`${baseURL}/consolelogs/resource/${CONSOLE_RESOURCE}?backend=${backend}`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".log-line").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
  return page;
}

test.describe("dashboard/deck console differential", () => {
  test("both UIs render identical text for the console lines they share", async ({ browser, baseURL }) => {
    test.skip(baseURL === undefined, "baseURL is required.");

    const blazorPage = await openBlazorConsole(browser);
    const deckPage = await openDeckConsole(browser, baseURL!);

    try {
      const [blazor, deck] = await Promise.all([
        readConsole(blazorPage, ".log-line-area", ".log-line-number", ".log-content"),
        readConsole(deckPage, ".log-line", ".log-line__num", ".log-line__text"),
      ]);

      expect(blazor.size, "the dashboard rendered no console lines").toBeGreaterThan(0);
      expect(deck.size, "Deck rendered no console lines").toBeGreaterThan(0);

      const shared = [...deck.keys()].filter((lineNumber) => blazor.has(lineNumber)).sort((a, b) => a - b);
      expect(shared.length, "the two UIs rendered no overlapping line numbers").toBeGreaterThan(0);

      // Compare as a list of tuples so a mismatch names the offending line number in the diff.
      const blazorShared = shared.map((lineNumber) => [lineNumber, blazor.get(lineNumber)] as const);
      const deckShared = shared.map((lineNumber) => [lineNumber, deck.get(lineNumber)] as const);
      expect(deckShared).toEqual(blazorShared);
    } finally {
      await blazorPage.close();
      await deckPage.close();
    }
  });

  test("both UIs number console lines contiguously from the same origin", async ({ browser, baseURL }) => {
    test.skip(baseURL === undefined, "baseURL is required.");

    const blazorPage = await openBlazorConsole(browser);
    const deckPage = await openDeckConsole(browser, baseURL!);

    try {
      const [blazor, deck] = await Promise.all([
        readConsole(blazorPage, ".log-line-area", ".log-line-number", ".log-content"),
        readConsole(deckPage, ".log-line", ".log-line__num", ".log-line__text"),
      ]);

      // Line numbers are 1-based and gapless in both UIs. A divergence here means one of them is
      // dropping or re-basing lines, which would silently misalign every downstream comparison.
      for (const [label, lines] of [["dashboard", blazor], ["deck", deck]] as const) {
        const numbers = [...lines.keys()].sort((a, b) => a - b);
        const expected = Array.from({ length: numbers.length }, (_, index) => numbers[0]! + index);
        expect(numbers, `${label} console line numbers are not contiguous`).toEqual(expected);
      }
    } finally {
      await blazorPage.close();
      await deckPage.close();
    }
  });
});
