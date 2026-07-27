import { expect, test, type Page } from "@playwright/test";

// The two UIs sort the trace list in opposite directions (Blazor oldest-first, Deck newest-first),
// so their default viewports rarely overlap. Rather than compare positions, we join on the trace's
// display name -- which is stable and immutable once the trace is recorded -- and assert that the
// per-resource span breakdown matches. That breakdown is the content this test exists to pin: Deck
// used to render only a bare total ("3 spans") while Blazor rendered one tag per resource.

const blazorUrl = requiredEnv("ASPIRE_DASHBOARD_URL");
const loginToken = requiredEnv("ASPIRE_DASHBOARD_BROWSER_TOKEN");
const backend = process.env.ASPIRE_DASHBOARD_BACKEND ?? "aot";

const READY_TIMEOUT_MS = 90_000;

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

/** Normalizes a resource tag ("stress-telemetryservice (2)") for comparison across UIs. */
function normalizeTags(raw: string[]): string[] {
  return raw
    .map((tag) => tag.replace(/\s+/g, " ").trim())
    .filter((tag) => tag !== "")
    .sort();
}

/**
 * Reads the Blazor traces grid as a trace-name -> resource-tag-list map. The grid is virtualized and
 * leads with an empty spacer <tr>, so callers must wait on a `td` rather than a `tr`.
 */
async function readBlazor(page: Page): Promise<Map<string, string[]>> {
  await login(page, blazorUrl);
  await page.goto(`${blazorUrl}/traces`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".table-wrap tbody tr td").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });

  const rows = await page.evaluate(() => {
    const out: Array<[string, string[]]> = [];
    for (const tr of Array.from(document.querySelectorAll(".table-wrap tbody tr"))) {
      const cells = Array.from(tr.querySelectorAll("td"));
      if (cells.length < 3) {
        continue;
      }

      // The NAME cell reads "resource: operation idPrefix"; the trailing id prefix is a link target,
      // not part of the name, so drop it and keep "resource: operation".
      const name = (cells[1]?.textContent ?? "").replace(/\s+/g, " ").trim().replace(/\s+[0-9a-f]{6,}$/i, "");
      const tags = Array.from(cells[2]?.querySelectorAll(".trace-tag") ?? []).map((t) => t.textContent ?? "");
      if (name !== "") {
        out.push([name, tags]);
      }
    }

    return out;
  });

  return new Map(rows.map(([name, tags]) => [name, normalizeTags(tags)]));
}

/** Reads Deck's trace cards as a trace-name -> resource-tag-list map. */
async function readDeck(page: Page, baseURL: string): Promise<Map<string, string[]>> {
  await login(page, baseURL);
  await page.goto(`${baseURL}/traces?backend=${backend}`, { waitUntil: "domcontentloaded" });
  await expect(page.locator(".wf__trace").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });

  const rows = await page.evaluate(() => {
    const out: Array<[string, string[]]> = [];
    for (const card of Array.from(document.querySelectorAll(".wf__trace"))) {
      const resource = (card.querySelector(".wf__head-res")?.textContent ?? "").trim();
      const operation = (card.querySelector(".wf__head-name")?.textContent ?? "").trim();
      const tags = Array.from(card.querySelectorAll(".trace-tag")).map((t) => t.textContent ?? "");
      if (operation !== "") {
        out.push([resource === "" ? operation : `${resource}: ${operation}`, tags]);
      }
    }

    return out;
  });

  return new Map(rows.map(([name, tags]) => [name, normalizeTags(tags)]));
}

test.describe("traces differential", () => {
  test("both UIs report the same per-resource span breakdown for shared traces", async ({ page, browser, baseURL }) => {
    const blazorPage = await (await browser.newContext()).newPage();
    const blazor = await readBlazor(blazorPage);
    const deck = await readDeck(page, baseURL!);

    const shared = [...deck.keys()].filter((name) => blazor.has(name));
    expect(shared.length, `no trace name was rendered by both UIs (blazor=${[...blazor.keys()].length}, deck=${[...deck.keys()].length})`).toBeGreaterThan(0);

    // Blazor additionally attributes spans to *uninstrumented peers* -- a resource inferred from a
    // client span's peer.service/server.address attributes when the callee emits no spans of its own
    // (TelemetryRepository.CalculateTraceUninstrumentedPeers). Deck has no peer resolution yet, so it
    // can render a strict subset of Blazor's tags. We assert that every tag Deck *does* render agrees
    // exactly with Blazor's, which is what the per-resource breakdown fix is responsible for.
    let compared = 0;
    for (const name of shared) {
      const blazorTags = new Set(blazor.get(name)!);
      for (const tag of deck.get(name)!) {
        expect(blazorTags, `trace "${name}": deck rendered ${tag}, blazor rendered ${[...blazorTags].join(", ")}`).toContain(tag);
        compared++;
      }
    }

    expect(compared, "no resource tags were compared").toBeGreaterThan(0);
  });

  test("deck renders a per-resource tag rather than a bare span total", async ({ page, baseURL }) => {
    const deck = await readDeck(page, baseURL!);
    const allTags = [...deck.values()].flat();

    expect(allTags.length, "deck rendered no resource tags at all").toBeGreaterThan(0);
    // Every tag must name a resource and carry its own count, e.g. "stress-telemetryservice (2)".
    for (const tag of allTags) {
      expect(tag).toMatch(/^\S.*\(\d+\)$/);
    }
  });
});
