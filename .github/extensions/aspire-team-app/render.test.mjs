import assert from "node:assert/strict";
import vm from "node:vm";
import test from "node:test";

import { APP_JS } from "./render.mjs";

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

  vm.runInNewContext(`${APP_JS}\n;globalThis.__test = {\n  render,\n  deleteRepo,\n  persistAccountRepos,\n  draftReposByAcct,\n  editingByAcct,\n  setState(value) { state = value; },\n  setPrefs(value) { prefs = value; },\n  setView(value) { view = value; },\n  setLoadError(value) { loadError = value; },\n  getLoadError() { return loadError; },\n};`, sandbox);

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
