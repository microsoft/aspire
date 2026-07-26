import { expect, test } from "@playwright/test";

/**
 * Generous because the first test to reach this page pays for Vite compiling its module graph, and
 * the parity suite runs several workers in parallel against one dev server.
 */
const READY_TIMEOUT_MS = 60_000;

/**
 * The Blazor dashboard renders console output through the shared `LogParser`, which converts ANSI
 * SGR sequences into `<span class="ansi-*">` markup. The Deck UI receives that same markup on the
 * `html` field of each console line and renders it directly, so these tests assert on the produced
 * DOM rather than on pixels: they check that the colour spans survive into the document and that
 * the stylesheet actually resolves a colour for them.
 */
test.describe("console ANSI rendering", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/consolelogs");
  });

  test("renders server-supplied ANSI markup as styled spans", async ({ page }) => {
    const firstLine = page.locator(".log-line").first();
    await expect(firstLine).toBeVisible({ timeout: READY_TIMEOUT_MS });

    // The mock backend colours its backlog with alternating bright green / bright cyan, mirroring
    // what the real backends emit once LogParser has run.
    const colouredSpan = page.locator(".log-line__text span[class^='ansi-fg-']").first();
    await expect(colouredSpan).toBeVisible({ timeout: READY_TIMEOUT_MS });

    const className = await colouredSpan.getAttribute("class");
    expect(className).toMatch(/^ansi-fg-[a-z]+$/);
  });

  test("resolves a concrete colour for ANSI classes from the stylesheet", async ({ page }) => {
    const colouredSpan = page.locator(".log-line__text span[class^='ansi-fg-']").first();
    await expect(colouredSpan).toBeVisible({ timeout: READY_TIMEOUT_MS });

    const colour = await colouredSpan.evaluate((el) => getComputedStyle(el).color);

    // A missing rule leaves the span inheriting the surrounding text colour, so assert we got a
    // real rgb() value and that it is not the default black the UA would otherwise apply.
    expect(colour).toMatch(/^rgba?\(/);
    expect(colour).not.toBe("rgb(0, 0, 0)");
  });

  test("does not leak raw escape sequences into the rendered text", async ({ page }) => {
    await expect(page.locator(".log-line").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });

    const texts = await page.locator(".log-line__text").allTextContents();
    expect(texts.length).toBeGreaterThan(0);

    for (const text of texts) {
      expect(text).not.toContain("\u001b");
      expect(text).not.toMatch(/\[\d+m/);
    }
  });

  test("escapes markup inside console output", async ({ page }) => {
    await expect(page.locator(".log-line").first()).toBeVisible({ timeout: READY_TIMEOUT_MS });

    // LogParser HTML-encodes before colouring, so resource output can never introduce elements of
    // its own. Anything under .log-line__text must be an ANSI span or an anchor from linkification.
    const unexpected = await page.locator(".log-line__text *").evaluateAll((elements) =>
      elements
        .filter((el) => {
          const tag = el.tagName.toLowerCase();
          if (tag === "a") {
            return false;
          }

          return tag !== "span" || !el.className.startsWith("ansi-");
        })
        .map((el) => el.tagName.toLowerCase()));

    expect(unexpected).toEqual([]);
  });
});
