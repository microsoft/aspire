import { expect, test, type Browser, type Page } from "@playwright/test";

// These specs open the shipping Blazor dashboard and the Deck UI side by side against one live
// AppHost and compare the rendered result. Anything that differs here is a genuine behavioural
// divergence: both UIs are reading the same resource service and the same telemetry repository.

const blazorUrl = requiredEnv("ASPIRE_DASHBOARD_URL");
const loginToken = requiredEnv("ASPIRE_DASHBOARD_BROWSER_TOKEN");
const backend = process.env.ASPIRE_DASHBOARD_BACKEND ?? "aot";

// Cold Vite compiles the module graph on first navigation, and the live AppHost keeps streaming
// while we read. Both push well past Playwright's 5s default, so every wait here is explicit.
const READY_TIMEOUT_MS = 90_000;

function requiredEnv(name: string): string {
  const value = process.env[name];
  if (value === undefined || value === "") {
    throw new Error(`${name} must be set for differential runs.`);
  }

  return value;
}

interface Grid {
  headers: string[];
  rows: string[][];
}

/**
 * Both UIs render their grids as `.table-wrap > table > tbody > tr`. Reading `td` elements (rather
 * than the row's `innerText`) preserves column boundaries. Virtualized tables emit an
 * `aria-hidden` spacer row for the scrolled-past region, which is skipped.
 */
async function readGrid(page: Page): Promise<Grid> {
  return await page.evaluate(() => {
    const wrap = document.querySelector(".table-wrap");
    if (wrap === null) {
      return { headers: [], rows: [] };
    }

    const text = (element: Element): string => (element as HTMLElement).innerText.trim().replace(/\s+/g, " ");
    const headers = [...wrap.querySelectorAll("thead th")].map(text);
    const rows = [...wrap.querySelectorAll("tbody tr")]
      .filter((row) => row.getAttribute("aria-hidden") !== "true")
      .map((row) => [...row.querySelectorAll("td")].map(text))
      .filter((cells) => cells.length > 1);

    return { headers, rows };
  });
}

async function openBlazor(browser: Browser): Promise<Page> {
  const page = await browser.newPage();
  await page.goto(`${blazorUrl}/login?t=${loginToken}`, { waitUntil: "domcontentloaded" });
  await page.goto(`${blazorUrl}/`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".table-wrap tbody tr").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
  return page;
}

async function openDeck(browser: Browser, baseURL: string, path = "/"): Promise<Page> {
  const page = await browser.newPage();
  // The login handshake has to happen on the Deck origin so the auth cookie is scoped to the page
  // that will issue the API calls; Vite proxies /login through to the dashboard.
  await page.goto(`${baseURL}/login?t=${loginToken}`, { waitUntil: "domcontentloaded" }).catch(() => undefined);
  await page.goto(`${baseURL}${path}?backend=${backend}`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".table-wrap tbody tr").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });
  return page;
}

function columnIndex(headers: string[], ...candidates: string[]): number {
  const normalized = headers.map((header) => header.toLowerCase());
  for (const candidate of candidates) {
    const index = normalized.indexOf(candidate.toLowerCase());
    if (index >= 0) {
      return index;
    }
  }

  throw new Error(`None of [${candidates.join(", ")}] found in headers [${headers.join(", ")}].`);
}

async function readBothGrids(browser: Browser, baseURL: string): Promise<{ blazor: Grid; deck: Grid; close: () => Promise<void> }> {
  const blazorPage = await openBlazor(browser);
  const deckPage = await openDeck(browser, baseURL);
  const [blazor, deck] = await Promise.all([readGrid(blazorPage), readGrid(deckPage)]);
  return {
    blazor,
    deck,
    close: async () => {
      await blazorPage.close();
      await deckPage.close();
    },
  };
}

test.describe("dashboard/deck differential", () => {
  test("both UIs list the same resources", async ({ browser, baseURL }) => {
    const { blazor, deck, close } = await readBothGrids(browser, baseURL!);

    const blazorNames = blazor.rows.map((cells) => cells[columnIndex(blazor.headers, "name")]!).sort();
    const deckNames = deck.rows.map((cells) => cells[columnIndex(deck.headers, "name")]!).sort();

    expect(blazorNames.length).toBeGreaterThan(0);
    expect(deckNames).toEqual(blazorNames);

    await close();
  });

  test("both UIs report the same resource states", async ({ browser, baseURL }) => {
    const { blazor, deck, close } = await readBothGrids(browser, baseURL!);

    const toStates = (grid: Grid): Array<[string, string]> => {
      const nameIndex = columnIndex(grid.headers, "name");
      const stateIndex = columnIndex(grid.headers, "state");
      return grid.rows
        // Deck renders "Running · Healthy" where Blazor renders the lifecycle state and shows
        // health separately, so compare the lifecycle portion only.
        .map((cells) => [cells[nameIndex]!, cells[stateIndex]!.split("·")[0]!.trim()] as [string, string])
        .sort((left, right) => left[0].localeCompare(right[0]));
    };

    const blazorStates = toStates(blazor);
    expect(blazorStates.length).toBeGreaterThan(0);
    expect(toStates(deck)).toEqual(blazorStates);

    await close();
  });

  test("the resource start time column shows the same absolute time in both UIs", async ({ browser, baseURL }) => {
    const { blazor, deck, close } = await readBothGrids(browser, baseURL!);

    const toStartTimes = (grid: Grid): Array<[string, string]> => {
      const nameIndex = columnIndex(grid.headers, "name");
      const startIndex = columnIndex(grid.headers, "start time", "started");
      return grid.rows
        .map((cells) => [cells[nameIndex]!, cells[startIndex]!] as [string, string])
        .filter(([, value]) => value !== "" && value !== "—")
        .sort((left, right) => left[0].localeCompare(right[0]));
    };

    const blazorStarts = toStartTimes(blazor);
    const deckStarts = toStartTimes(deck);

    // Guard against the comparison passing vacuously if neither grid rendered a time.
    expect(blazorStarts.length).toBeGreaterThan(0);
    expect(deckStarts.map(([name]) => name)).toEqual(blazorStarts.map(([name]) => name));

    // Blazor renders the start timestamp as an absolute local time, prefixed with the date once the
    // resource is no longer from today (FormatHelpers.FormatTimeWithOptionalDate). A relative
    // rendering such as "10m ago" is information loss: it cannot express *when* a resource started,
    // which is the entire purpose of the column.
    expect(deckStarts).toEqual(blazorStarts);

    await close();
  });

  test("both UIs render the same number of resource rows", async ({ browser, baseURL }) => {
    const { blazor, deck, close } = await readBothGrids(browser, baseURL!);

    expect(blazor.rows.length).toBeGreaterThan(0);
    expect(deck.rows.length).toBe(blazor.rows.length);

    await close();
  });
});
