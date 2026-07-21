import assert from "node:assert/strict";
import vm from "node:vm";
import test from "node:test";

import { APP_JS, STYLES } from "./render.mjs";

test("renderer theme styles follow canvas tokens with accessible light fallbacks", () => {
  assert.match(STYLES, /--bg: var\(--bgColor-default, var\(--background-color-default, #ffffff\)\)/);
  assert.match(STYLES, /--fg: var\(--fgColor-default, var\(--text-color-default, #1f2328\)\)/);
  assert.match(STYLES, /--surface: color-mix\(in srgb, var\(--bg\), var\(--fg\) 5%\)/);
  assert.match(STYLES, /data-color-mode="light".*color-scheme: light/);
  assert.match(STYLES, /data-color-mode="dark".*color-scheme: dark/);
  assert.match(STYLES, /color: color-mix\(in srgb, var\(--pill-tone\), var\(--fg\) 25%\)/);
  assert.match(STYLES, /\.ent-badge \{[\s\S]*?color: color-mix\(in srgb, var\(--blue\), var\(--fg\) 25%\)/);
  assert.match(STYLES, /--shadow-floating: var\(--shadow-floating-small,/);
  assert.match(STYLES, /\.cb-menu \{[\s\S]*?box-shadow: var\(--shadow-floating\)/);
  // Deterministic load bar replaced the looping indeterminate one (no glow, no paintfill).
  assert.match(STYLES, /\.loadbar \{[\s\S]*?transition: width/);
  assert.doesNotMatch(STYLES, /animation: paintfill/);
  assert.doesNotMatch(STYLES, /box-shadow: 0 0 8px/);
  assert.doesNotMatch(STYLES, /var\(--n-/);
});

test("render keeps the current dashboard visible and surfaces later load errors", () => {
  const { app, api } = createRendererHarness();

  api.setState({
    authenticated: true,
    accounts: [],
    activeAccounts: [],
    notifications: [],
  });
  api.setView("accounts");
  api.setLoadError("GitHub API 500 unavailable");
  api.render();

  assert.match(app.innerHTML, /GitHub API 500 unavailable/);
  assert.match(app.innerHTML, /GitHub accounts/);
});

test("deleteRepo completes once when both the animation and fallback timeout fire", () => {
  const row = {
    classList: { add() {} },
    addEventListener(_event, handler) { this.animationEnd = handler; },
  };
  const timers = [];
  const { api } = createRendererHarness({ setTimeout: (handler) => { timers.push(handler); return timers.length; } });
  api.draftReposByAcct["acct:github.com/octo"] = ["microsoft/aspire", "microsoft/dcp", "microsoft/aspire.dev"];

  api.deleteRepo("acct:github.com/octo", 0, row);
  row.animationEnd();
  for (const timer of timers) timer();

  assert.deepEqual(api.draftReposByAcct["acct:github.com/octo"], ["microsoft/dcp", "microsoft/aspire.dev"]);
});

test("failed repo saves show the API error and revert the optimistic draft", async () => {
  const id = "acct:github.com/octo";
  const previousRepos = ["microsoft/aspire"];
  const errEl = errorElement();
  const { api } = createRendererHarness({
    fetch: async (url) => {
      if (String(url) === "api/account/repos") {
        return jsonResponse({ error: "GitHub API 500 unavailable" }, { ok: false, status: 500 });
      }
      return new Promise(() => {});
    },
    querySelector(selector) {
      return selector === '.repo-err[data-err="acct\\:github\\.com\\/octo"]' ? errEl : null;
    },
  });
  api.draftReposByAcct[id] = ["microsoft/aspire", "microsoft/dcp"];
  api.editingByAcct[id] = -1;

  await api.persistAccountRepos(id, previousRepos);

  assert.deepEqual(api.draftReposByAcct[id], previousRepos);
  assert.equal(errEl.textContent, "Couldn't save repositories: GitHub API 500 unavailable");
  assert.equal(errEl.classList.has("show"), true);
});

test("forYouCardActions maps review-requested picks to a Review action", () => {
  const { api } = createRendererHarness();

  const resolve = api.forYouCardActions({ action: "Resolve conflicts" });
  assert.equal(resolve.length, 1);
  assert.equal(resolve[0].kind, "resolve-conflicts");

  const review = api.forYouCardActions({ action: "Review this" });
  assert.equal(review.length, 1);
  assert.equal(review[0].kind, "review");
  assert.equal(review[0].label, "Review");

  assert.equal(api.forYouCardActions({ action: "Respond here" }), null);
  assert.equal(api.forYouCardActions(null), null);
});

test("queuePanel reports an honest 'N shown' metric for a mixed (non-prefix) selection", () => {
  const { api } = createRendererHarness();
  const items = [1, 2, 3, 4].map((n) => ({ pr: { url: "", title: "t" + n, author: "a", repository: "o/r", number: n } }));

  // A genuine prefix (top N of a larger sorted list) keeps the "top N of total" claim.
  assert.match(api.queuePanel({ id: "q", title: "Q", items, cappedTotal: 9 }), /top 4 of 9/);

  // A mixed selection (review-debt cards spilled past the cap, so `items` is not a prefix of the
  // sorted list) must NOT claim "top N of total" — non-debt cards between retained debt cards were
  // skipped, so that would be false. It reports the honest shown count instead.
  const mixed = api.queuePanel({ id: "q", title: "Q", items, cappedTotal: 9, exactCount: true });
  assert.doesNotMatch(mixed, /top 4 of 9/);
  assert.match(mixed, /4 shown/);
});

test("cardActionBtn re-renders a disabled button while its action's POST is still in flight", () => {
  const { api } = createRendererHarness();
  const pr = { url: "https://github.com/o/r/pull/1", number: 1, repository: "o/r", title: "t", author: "a" };
  const action = { kind: "review", label: "Review", done: "Review requested", icon: "" };

  // Default render: the split button is enabled so the user can click it.
  const enabled = api.cardActionBtn(pr, action);
  assert.match(enabled, /data-target="new-session"/);
  assert.doesNotMatch(enabled, /disabled/);

  // Mark this exact (kind, PR) action as in flight, then re-render the card the way a streamed
  // 'state' event would. The replacement main button and caret must come back disabled so a click
  // can't re-queue the same agent action mid-request.
  api.inflightActions.add(api.actionKey(action.kind, pr.url, pr.repository, pr.number));
  const busy = api.cardActionBtn(pr, action);
  assert.match(busy, /class="card-btn cb-main busy" data-target="new-session" disabled/);
  assert.match(busy, /class="card-btn cb-caret"[^>]*disabled/);

  // A different action on the same PR is unaffected — only the in-flight split is locked.
  const other = api.cardActionBtn(pr, { kind: "test", label: "Test", done: "Testing requested", icon: "" });
  assert.doesNotMatch(other, /disabled/);
});

test("withRefresh ignores a late older response so overlapping refreshes can't roll state back", async () => {
  // The module-init load() calls fetch("api/state"); a never-resolving fetch keeps it pending so it
  // can't clobber `state` mid-test. withRefresh takes its data from the fn argument, not fetch.
  const { api } = createRendererHarness({ fetch: () => new Promise(() => {}) });
  // authenticated:false keeps render() on the safe authPicker path; seq/marker are what we assert on.
  const dash = (seq, marker) => ({ dashboard: { seq, marker, authenticated: false, accounts: [], message: "" }, prefs: {} });

  // A newer refresh applies and advances lastAppliedSeq.
  await api.withRefresh(async () => dash(5, "new"));
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getState().marker, "new");

  // An older forced load that resolves after the newer one must NOT overwrite the newer state:
  // applying it would roll state and lastAppliedSeq backward and show stale data.
  await api.withRefresh(async () => dash(3, "old"));
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getState().marker, "new");

  // A strictly newer refresh still applies.
  await api.withRefresh(async () => dash(7, "newest"));
  assert.equal(api.getState().seq, 7);

  // Legacy payloads without a seq still apply (back-compat with pre-seq servers).
  await api.withRefresh(async () => ({ dashboard: { marker: "legacy", authenticated: false, accounts: [], message: "" }, prefs: {} }));
  assert.equal(api.getState().marker, "legacy");
});

test("load ignores a stale GET /api/state response so it can't rewind lastAppliedSeq", async () => {
  // GET /api/state may be served stale-while-revalidate: the cached payload (seq 3) can settle after
  // the background stream already delivered a newer snapshot (seq 5). fetch always returns the stale
  // seq-3 payload here to model that race.
  const stale = { dashboard: { seq: 3, marker: "stale", authenticated: false, accounts: [], message: "" }, prefs: {} };
  const { api } = createRendererHarness({ fetch: async () => jsonResponse(stale) });

  // Establish a newer applied revision (seq 5). withRefresh takes its data from fn, not fetch.
  await api.withRefresh(async () => ({ dashboard: { seq: 5, marker: "fresh", authenticated: false, accounts: [], message: "" }, prefs: {} }));
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getAppliedSeq(), 5);

  // A stale forced load must be gated out — applying it would roll state and lastAppliedSeq backward.
  await api.load();
  assert.equal(api.getState().seq, 5);
  assert.equal(api.getState().marker, "fresh");
  assert.equal(api.getAppliedSeq(), 5);
});

test("onCardAction re-renders when an SSE refresh detached the card mid-request so the visible button isn't stuck disabled", async () => {
  // The action POST resolves; api/state stays pending so the module-init load() can't render mid-test
  // and pollute the assertions below.
  const fetchMock = async (path) =>
    String(path).includes("api/agent/action")
      ? jsonResponse({ queued: false, target: "new-session" })
      : new Promise(() => {});
  const { app, api } = createRendererHarness({ fetch: fetchMock });
  // A non-null state so render() produces output (render() early-returns when state is null).
  api.setState({ authenticated: true, accounts: [], activeAccounts: [], notifications: [] });
  api.setView("accounts");

  const makeBtn = () => {
    const cls = new Set();
    return {
      disabled: false,
      innerHTML: "",
      classList: { add: (c) => cls.add(c), remove: (c) => cls.delete(c), contains: (c) => cls.has(c) },
    };
  };
  const makeSplit = (connected) => {
    const main = makeBtn();
    const caret = makeBtn();
    return {
      isConnected: connected,
      dataset: { kind: "review", prUrl: "https://github.com/o/r/pull/1", prRepo: "o/r", prNumber: "1" },
      querySelector: (sel) => (sel === ".cb-main" ? main : sel === ".cb-caret" ? caret : null),
    };
  };

  // Detached split: a streamed 'state' event replaced the card while the POST was pending, so the
  // visible replacement was rendered disabled. Settling must re-render so it reflects the cleared key.
  app.innerHTML = "";
  await api.onCardAction(makeSplit(false), "new-session");
  assert.notEqual(app.innerHTML, "");

  // Still-connected split: keep the deliberate no-re-render behavior so the inline confirmation stays.
  app.innerHTML = "";
  await api.onCardAction(makeSplit(true), "new-session");
  assert.equal(app.innerHTML, "");
});

function createRendererHarness(overrides = {}) {
  const app = {
    innerHTML: "",
    removeAttribute() {},
    classList: classList(),
  };
  const document = {
    getElementById(id) { return id === "app" ? app : null; },
    querySelector: overrides.querySelector ?? (() => null),
    querySelectorAll: () => [],
    addEventListener() {},
  };
  const sandbox = {
    document,
    window: { CSS: { escape: cssEscape } },
    CSS: { escape: cssEscape },
    EventSource: function () { throw new Error("disabled"); },
    ResizeObserver: undefined,
    requestAnimationFrame(handler) { handler(); },
    fetch: overrides.fetch ?? (async () => jsonResponse({ dashboard: null, prefs: null })),
    setTimeout: overrides.setTimeout ?? ((handler) => { handler(); return 1; }),
    clearTimeout: overrides.clearTimeout ?? (() => {}),
    console,
  };

  vm.runInNewContext(`${APP_JS}\n;globalThis.__test = {\n  render,\n  withRefresh,\n  load,\n  onCardAction,\n  deleteRepo,\n  persistAccountRepos,\n  draftReposByAcct,\n  editingByAcct,\n  forYouCardActions,\n  queuePanel,\n  cardActionBtn,\n  actionKey,\n  inflightActions,\n  setState(value) { state = value; },\n  getState() { return state; },\n  getAppliedSeq() { return lastAppliedSeq; },\n  setPrefs(value) { prefs = value; },\n  setView(value) { view = value; },\n  setLoadError(value) { loadError = value; },\n  getLoadError() { return loadError; },\n};`, sandbox);

  return { app, api: sandbox.__test };
}

function jsonResponse(body, options = {}) {
  return {
    ok: options.ok ?? true,
    status: options.status ?? 200,
    statusText: options.statusText ?? "OK",
    json: async () => body,
  };
}

function errorElement() {
  const classes = new Set();
  return {
    textContent: "",
    classList: {
      add(name) { classes.add(name); },
      remove(name) { classes.delete(name); },
      has(name) { return classes.has(name); },
    },
  };
}

function classList() {
  const classes = new Set();
  return {
    add(name) { classes.add(name); },
    remove(name) { classes.delete(name); },
    toggle(name, on) { if (on) classes.add(name); else classes.delete(name); },
    contains(name) { return classes.has(name); },
  };
}

function cssEscape(value) {
  return String(value).replace(/[^a-zA-Z0-9_-]/g, (ch) => `\\${ch}`);
}
