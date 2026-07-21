import assert from "node:assert/strict";
import test from "node:test";

import { accountId, isEmuAccountId, isEmuLogin } from "./accounts.mjs";
import { accountConfig } from "./state.mjs";

const originalEnv = { ...process.env };
const originalFetch = globalThis.fetch;

test.afterEach(() => {
  restoreEnvironment();
  globalThis.fetch = originalFetch;
});

test("accountId includes normalized host and login", () => {
  assert.equal(accountId("Octo", "HTTPS://API.GITHUB.COM/foo"), "acct:github.com/octo");
  assert.equal(accountId("Octo", "GHE.EXAMPLE.COM"), "acct:ghe.example.com/octo");
});

test("isEmuLogin matches org alias suffixes but not the bare suffix or plain logins", () => {
  assert.equal(isEmuLogin("dapine_microsoft"), true);
  assert.equal(isEmuLogin("DAPINE_MICROSOFT"), true);
  assert.equal(isEmuLogin("octocat"), false);
  assert.equal(isEmuLogin("_microsoft"), false);
  assert.equal(isEmuLogin(""), false);
  assert.equal(isEmuLogin(null), false);
});

test("isEmuAccountId classifies host-scoped and legacy ids by their login", () => {
  assert.equal(isEmuAccountId("acct:github.com/dapine_microsoft"), true);
  assert.equal(isEmuAccountId("acct:dapine_microsoft"), true);
  assert.equal(isEmuAccountId("acct:github.com/octo"), false);
  assert.equal(isEmuAccountId("acct:octo"), false);
});

test("resolveAccounts keys accounts by normalized host and login", async () => {
  resetAccountEnv({
    COPILOT_GH_ACCOUNT_github_2E_com_octo: "token-dotcom",
    COPILOT_GH_ACCOUNT_ghe_2E_example_2E_com_octo: "token-ghe",
  });
  globalThis.fetch = accountFetch();
  const { resolveAccounts } = await import(`./accounts.mjs?test=host-login-${Date.now()}`);

  const { accounts } = await resolveAccounts(() => ["microsoft/aspire"], () => true);

  assert.deepEqual(accounts.map((a) => a.id).sort(), [
    "acct:ghe.example.com/octo",
    "acct:github.com/octo",
  ]);
  assert.equal(new Set(accounts.map((a) => a.host)).size, 2);
});

test("resolveAccounts reads legacy login-only preferences for github.com accounts", async () => {
  resetAccountEnv({ COPILOT_GH_ACCOUNT_github_2E_com_octo: "token-dotcom" });
  globalThis.fetch = accountFetch();
  const { resolveAccounts } = await import(`./accounts.mjs?test=legacy-${Date.now()}`);
  const legacyPrefs = {
    "acct:octo": { repos: ["microsoft/aspire.dev"], active: true },
  };

  const { accounts } = await resolveAccounts(
    (id) => accountConfig({ accounts: legacyPrefs }, id).repos,
    (id) => accountConfig({ accounts: legacyPrefs }, id).active,
  );

  assert.equal(accounts[0].id, "acct:github.com/octo");
  assert.deepEqual(accounts[0].repos, ["microsoft/aspire.dev"]);
  assert.equal(accounts[0].active, true);
});

function resetAccountEnv(extra) {
  for (const key of Object.keys(process.env)) {
    if (key.startsWith("COPILOT_GH_ACCOUNT_")) {
      delete process.env[key];
    }
  }
  delete process.env.GH_TOKEN;
  delete process.env.GITHUB_TOKEN;
  delete process.env.GH_HOST;
  process.env.PATH = "";
  Object.assign(process.env, extra);
}

function accountFetch() {
  return async (url, options = {}) => {
    const requestUrl = String(url);
    const body = options.body ? JSON.parse(options.body) : {};
    const query = body.query ?? "";
    if (requestUrl.endsWith("/api/v3/") || requestUrl === "https://api.github.com/") {
      return jsonResponse({}, { headers: { "x-oauth-scopes": "read:org" } });
    }
    if (query.includes("viewer { login")) {
      return jsonResponse({ data: { viewer: { login: "octo", avatarUrl: `${requestUrl}/octo.png` } } });
    }
    if (query.includes("r0: repository")) {
      return jsonResponse({ data: { r0: { nameWithOwner: "microsoft/aspire" } } });
    }
    throw new Error(`Unexpected fetch: ${requestUrl} ${query}`);
  };
}

function jsonResponse(body, options = {}) {
  const headers = options.headers ?? {};
  return {
    ok: options.ok ?? true,
    status: options.status ?? 200,
    statusText: options.statusText ?? "OK",
    headers: {
      get(name) { return headers[name.toLowerCase()] ?? null; },
    },
    json: async () => body,
  };
}

function restoreEnvironment() {
  for (const key of Object.keys(process.env)) {
    if (key.startsWith("COPILOT_GH_ACCOUNT_") && !(key in originalEnv)) {
      delete process.env[key];
    }
  }
  for (const name of ["GH_TOKEN", "GITHUB_TOKEN", "GH_HOST", "PATH"]) {
    if (originalEnv[name] === undefined) {
      delete process.env[name];
    } else {
      process.env[name] = originalEnv[name];
    }
  }
  for (const [key, value] of Object.entries(originalEnv)) {
    if (key.startsWith("COPILOT_GH_ACCOUNT_")) {
      process.env[key] = value;
    }
  }
}
