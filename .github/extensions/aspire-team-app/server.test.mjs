import assert from "node:assert/strict";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const artifactsRoot = fileURLToPath(new URL("../../../artifacts/copilot-extension-server-tests/", import.meta.url));
const copilotHome = join(artifactsRoot, "copilot-home");
const preferencesPath = join(copilotHome, "extensions", "aspire-team-app", "artifacts", "preferences.json");
const originalEnv = {
  GH_TOKEN: process.env.GH_TOKEN,
  GITHUB_TOKEN: process.env.GITHUB_TOKEN,
  COPILOT_HOME: process.env.COPILOT_HOME,
  PATH: process.env.PATH,
};
const originalFetch = globalThis.fetch;

process.env.COPILOT_HOME = copilotHome;

test.after(async () => {
  restoreEnvironment();
  await rm(artifactsRoot, { recursive: true, force: true });
});

test("mutating POST rejects cross-site loopback requests before saving preferences", async (t) => {
  await resetTestHome();
  delete process.env.GH_TOKEN;
  delete process.env.GITHUB_TOKEN;
  process.env.PATH = "";

  const server = await import(`./server.mjs?test=guard-${Date.now()}`);
  const entry = await server.startInstance("origin-guard-test", () => {});
  t.after(() => server.stopInstance("origin-guard-test"));

  const response = await fetch(new URL("api/mode", entry.url), {
    method: "POST",
    headers: {
      "content-type": "application/json",
      origin: "http://malicious.example",
      "sec-fetch-site": "cross-site",
    },
    body: JSON.stringify({ mode: "ship" }),
  });

  assert.equal(response.status, 403);
  await assert.rejects(readFile(preferencesPath, "utf8"), { code: "ENOENT" });
});

test("dashboard load retries after an inflight account probe rejection", async (t) => {
  await resetTestHome({
    accounts: {
      "acct:octo": {
        repos: ["microsoft/aspire"],
        active: true,
      },
    },
  });
  process.env.GH_TOKEN = "test-token";
  delete process.env.GITHUB_TOKEN;
  process.env.PATH = "";

  let failRepoProbe = true;
  globalThis.fetch = async (url, options = {}) => {
    const requestUrl = String(url);
    if (requestUrl.startsWith("http://127.0.0.1:")) {
      return originalFetch(url, options);
    }

    const body = options.body ? JSON.parse(options.body) : {};
    const query = body.query ?? "";

    if (requestUrl === "https://api.github.com/") {
      return jsonResponse({}, { headers: { "x-oauth-scopes": "read:org" } });
    }

    if (query.includes("viewer { login")) {
      return jsonResponse({ data: { viewer: { login: "octo", avatarUrl: null } } });
    }

    if (query.includes("r0: repository")) {
      if (failRepoProbe) {
        throw new Error("repo probe unavailable");
      }

      return jsonResponse({ data: { r0: { nameWithOwner: "microsoft/aspire" } } });
    }

    if (query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { isPrivate: false, pullRequests: { nodes: [] } } } });
    }

    throw new Error(`Unexpected fetch: ${requestUrl} ${query}`);
  };
  t.after(() => {
    globalThis.fetch = originalFetch;
  });

  const server = await import(`./server.mjs?test=inflight-${Date.now()}`);
  const entry = await server.startInstance("inflight-retry-test", () => {});
  t.after(() => server.stopInstance("inflight-retry-test"));

  const failed = await fetch(new URL("api/state", entry.url));
  assert.equal(failed.status, 500);

  failRepoProbe = false;
  const retried = await fetch(new URL("api/state", entry.url));
  assert.equal(retried.status, 200);
  const payload = await retried.json();
  assert.equal(payload.dashboard.authenticated, true);
});

test("card action route bridges { prompt, log } to the session and echoes the queued flag", async (t) => {
  await resetTestHome();

  const server = await import(`./server.mjs?test=agent-${Date.now()}`);
  const entry = await server.startInstance("agent-action-test", () => {});
  t.after(() => {
    server.setAgentSend(null);
    return server.stopInstance("agent-action-test");
  });

  const pr = { url: "https://github.com/microsoft/aspire/pull/123", number: 123, repository: "microsoft/aspire", title: "Add widget", author: "octocat" };

  // Not wired yet: a click that races startup fails cleanly rather than throwing.
  const early = await postAction(entry.url, { kind: "test", pr });
  assert.equal(early.status, 503);

  let received = null;
  server.setAgentSend(async (payload) => {
    received = payload;
    return { messageId: "m-1", queued: true };
  });

  const ok = await postAction(entry.url, { kind: "test", pr });
  assert.equal(ok.status, 200);
  const body = await ok.json();
  assert.equal(body.ok, true);
  assert.equal(body.kind, "test");
  assert.equal(body.messageId, "m-1");
  assert.equal(body.queued, true);
  assert.match(received.prompt, /\/pr-testing/);
  assert.equal(received.log, 'Test PR microsoft/aspire#123 \u2014 "Add widget"');

  // An unknown kind is rejected before the bridge is ever called.
  received = null;
  const bad = await postAction(entry.url, { kind: "nope", pr });
  assert.equal(bad.status, 400);
  assert.equal(received, null);
});

async function postAction(baseUrl, payload) {
  return fetch(new URL("api/agent/action", baseUrl), {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload),
  });
}

async function resetTestHome(prefs = {}) {
  await rm(artifactsRoot, { recursive: true, force: true });
  await mkdir(dirname(preferencesPath), { recursive: true });
  if (Object.keys(prefs).length > 0) {
    await writeFile(preferencesPath, JSON.stringify({
      mode: "review",
      release: "9.5",
      showDrafts: false,
      dismissedNotifications: [],
      notifications: {
        reviewRequested: true,
        readyToMerge: true,
        changesRequested: true,
        ciFailing: true,
      },
      ...prefs,
    }, null, 2), "utf8");
  }
}

function jsonResponse(body, options = {}) {
  const headers = options.headers ?? {};

  return {
    ok: options.ok ?? true,
    status: options.status ?? 200,
    statusText: options.statusText ?? "OK",
    headers: {
      get(name) {
        return headers[name.toLowerCase()] ?? null;
      },
    },
    json: async () => body,
  };
}

function restoreEnvironment() {
  setOrDeleteEnv("GH_TOKEN", originalEnv.GH_TOKEN);
  setOrDeleteEnv("GITHUB_TOKEN", originalEnv.GITHUB_TOKEN);
  setOrDeleteEnv("COPILOT_HOME", originalEnv.COPILOT_HOME);
  setOrDeleteEnv("PATH", originalEnv.PATH);
  globalThis.fetch = originalFetch;
}

function setOrDeleteEnv(name, value) {
  if (value === undefined) {
    delete process.env[name];
  } else {
    process.env[name] = value;
  }
}
