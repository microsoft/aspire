// Per-instance loopback server for the Aspire Team App canvas.
//
// Serves the iframe assets and a small JSON API. Dashboard data is cached and
// shared across instances (single user), with Server-Sent Events used to push a
// refresh signal to every open iframe when prefs change or a refresh completes.
//
// Several GitHub accounts can be active at once; each watches its own set of
// repositories and the dashboard interleaves results from all of them.

import { createServer } from "node:http";
import { HTML, STYLES, APP_JS } from "./render.mjs";
import { loadDashboard } from "./github.mjs";
import { resolveAccounts } from "./accounts.mjs";
import {
  loadPrefs,
  savePrefs,
  parseRepos,
  accountConfig,
  setAccountRepos,
  setAccountActive,
  activeIds,
} from "./state.mjs";

const servers = new Map(); // instanceId -> { server, url }
const sseClients = new Set();
let cache = null;
let inflight = null;

// Account resolution probes every candidate credential against its account's
// watched repos, so we cache the result and only re-probe when the cache is stale
// or the per-account configuration (repos / active flags) changed.
let authCache = null;
const AUTH_TTL = 10 * 60 * 1000;

function accountsKey(prefs) {
  return JSON.stringify(prefs.accounts || {});
}

async function resolveAuth(prefs, { reprobe = false } = {}) {
  const key = accountsKey(prefs);
  const fresh = authCache && Date.now() - authCache.at < AUTH_TTL && authCache.key === key;
  if (!reprobe && fresh) return authCache;

  const reposForId = (id) => accountConfig(prefs, id).repos;
  const isActive = (id) => accountConfig(prefs, id).active;
  const { accounts, tokenById } = await resolveAccounts(reposForId, isActive);

  // First-run convenience: if the user has never configured accounts and none are
  // active, auto-enable the strongest usable account so the canvas works out of the
  // box (preserves the old single-account behavior without being disruptive).
  if (activeIds(prefs).length === 0 && Object.keys(prefs.accounts || {}).length === 0) {
    const best = accounts.find((a) => a.status !== "failed" && a.accessible > 0)
      ?? accounts.find((a) => a.status !== "failed");
    if (best) {
      best.active = true;
      setAccountActive(prefs, best.id, true);
      await savePrefs(prefs);
    }
  }

  authCache = { key: accountsKey(prefs), accounts, tokenById, at: Date.now() };
  return authCache;
}

function invalidateAuth() {
  authCache = null;
}

async function getDashboard(force = false) {
  if (!force && cache) return cache;
  if (inflight) return inflight;
  inflight = (async () => {
    const prefs = await loadPrefs();
    const auth = await resolveAuth(prefs);
    const active = auth.accounts.filter((a) => a.active && a.status !== "failed");
    const accountsForLoad = active
      .map((a) => ({ token: auth.tokenById.get(a.id), login: a.login, repos: a.repos, graphql: a.graphql }))
      .filter((a) => a.token && a.login);

    let dashboard;
    if (accountsForLoad.length === 0) {
      const anyDetected = auth.accounts.length > 0;
      const anyActive = auth.accounts.some((a) => a.active);
      dashboard = {
        authenticated: false,
        message: !anyDetected
          ? "No GitHub credentials detected. Run `gh auth login` so the canvas can read your review queue."
          : anyActive
            ? "The active GitHub account can't read its watched repositories. Adjust its repos or enable another account below."
            : "No account is active. Enable an account in the Accounts tab to load your review queue.",
        accounts: auth.accounts,
        activeAccounts: [],
      };
    } else {
      dashboard = await loadDashboard({
        accounts: accountsForLoad,
        mode: prefs.mode,
        release: prefs.release,
        prefs: prefs.notifications,
        dismissed: prefs.dismissedNotifications,
        showDrafts: prefs.showDrafts,
      });
      dashboard.accounts = auth.accounts;
      // Carry the fields the canvas actions in extension.mjs read back off an active
      // account: set_repos reads `repos`, and summary reads `sourceKinds`/`status`/`repos`.
      // Omitting them made set_repos return an empty repo list and summary report
      // undefined sources/status for active accounts.
      dashboard.activeAccounts = active.map((a) => ({ id: a.id, login: a.login, avatarUrl: a.avatarUrl, enterprise: a.enterprise, host: a.host, repos: a.repos, status: a.status, sourceKinds: a.sourceKinds }));
      dashboard.dismissedCount = (prefs.dismissedNotifications || []).length;
    }
    cache = { dashboard, prefs };
    return cache;
  })().finally(() => {
    // Clear the in-flight marker whether the load resolved OR threw. If a rejected
    // promise were left cached here, the `if (inflight) return inflight` guard above
    // would replay that same failure to every later /api/state and /api/refresh request
    // until the extension process restarted. Resetting it lets the next request retry.
    inflight = null;
  });
  return inflight;
}

function broadcastRefresh() {
  for (const res of sseClients) {
    try {
      res.write("event: refresh\ndata: 1\n\n");
    } catch {
      sseClients.delete(res);
    }
  }
}

function send(res, status, body, type = "application/json") {
  res.writeHead(status, { "Content-Type": type + "; charset=utf-8", "Cache-Control": "no-store" });
  res.end(typeof body === "string" ? body : JSON.stringify(body));
}

async function readBody(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  if (!chunks.length) return {};
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    return {};
  }
}

// Reject cross-origin mutating requests. The iframe served by this instance calls the
// loopback API same-origin, so a present Origin header must match this server's host and
// a present Sec-Fetch-Site must indicate a same-origin (or non-site) navigation. Missing
// headers (older clients / direct navigations) are allowed through. This mirrors the
// origin guard used by the sibling issue-triage-canvas extension so any browser page that
// happens to reach the loopback port cannot drive preference/account/notification changes.
function isAllowedPostRequest(req) {
  const host = req.headers.host;
  if (!host) {
    return false;
  }

  const expectedOrigin = `http://${host}`;
  const origin = req.headers.origin;
  if (origin && !isSameOrigin(origin, expectedOrigin)) {
    return false;
  }

  const fetchSite = req.headers["sec-fetch-site"];
  if (fetchSite && fetchSite !== "same-origin" && fetchSite !== "none") {
    return false;
  }

  return true;
}

function isSameOrigin(origin, expectedOrigin) {
  try {
    return new URL(origin).origin === new URL(expectedOrigin).origin;
  } catch {
    return false;
  }
}

async function handle(req, res, log) {
  const url = new URL(req.url, "http://127.0.0.1");
  const path = url.pathname;

  try {
    // Every mutating route on this API is a POST, so gate POSTs on the origin guard
    // before dispatching to any handler that reads the body or writes preferences.
    if (req.method === "POST" && !isAllowedPostRequest(req)) {
      return send(res, 403, { error: "forbidden" });
    }

    if (req.method === "GET" && (path === "/" || path === "/index.html")) {
      return send(res, 200, HTML, "text/html");
    }
    if (req.method === "GET" && path === "/styles.css") {
      return send(res, 200, STYLES, "text/css");
    }
    if (req.method === "GET" && path === "/app.js") {
      return send(res, 200, APP_JS, "text/javascript");
    }
    if (req.method === "GET" && path === "/api/state") {
      return send(res, 200, await getDashboard(false));
    }
    if (req.method === "POST" && path === "/api/refresh") {
      return send(res, 200, await getDashboard(true));
    }
    if (req.method === "POST" && path === "/api/mode") {
      const { mode } = await readBody(req);
      const prefs = await loadPrefs();
      if (["review", "issues", "ship"].includes(mode)) prefs.mode = mode;
      await savePrefs(prefs);
      const next = await getDashboard(true);
      broadcastRefresh();
      return send(res, 200, next);
    }
    if (req.method === "POST" && path === "/api/prefs") {
      // Release milestone + notification preferences + draft visibility. Watched
      // repos are configured per account via /api/account/repos.
      const body = await readBody(req);
      const prefs = await loadPrefs();
      if (typeof body.release === "string" && body.release.trim()) prefs.release = body.release.trim();
      if (typeof body.showDrafts === "boolean") prefs.showDrafts = body.showDrafts;
      if (body.notifications) prefs.notifications = { ...prefs.notifications, ...body.notifications };
      await savePrefs(prefs);
      const next = await getDashboard(true);
      broadcastRefresh();
      return send(res, 200, next);
    }
    if (req.method === "POST" && path === "/api/account/toggle") {
      const { id, active } = await readBody(req);
      if (typeof id === "string" && id) {
        const prefs = await loadPrefs();
        setAccountActive(prefs, id, !!active);
        await savePrefs(prefs);
        invalidateAuth();
      }
      const next = await getDashboard(true);
      broadcastRefresh();
      return send(res, 200, next);
    }
    if (req.method === "POST" && path === "/api/account/repos") {
      // Persist a single account's watched repos. Deliberately does NOT broadcast:
      // the iframe that owns the repo editor is mid-edit and a broadcast would
      // clobber its local draft. The dashboard cache is still recomputed.
      const { id, repos } = await readBody(req);
      if (typeof id === "string" && id) {
        const prefs = await loadPrefs();
        setAccountRepos(prefs, id, parseRepos(repos));
        await savePrefs(prefs);
        invalidateAuth();
      }
      const next = await getDashboard(true);
      return send(res, 200, next);
    }
    if (req.method === "POST" && path === "/api/notifications/dismiss") {
      const { id } = await readBody(req);
      if (typeof id === "string" && id) {
        const prefs = await loadPrefs();
        if (!prefs.dismissedNotifications.includes(id)) {
          prefs.dismissedNotifications.push(id);
          await savePrefs(prefs);
        }
      }
      const next = await getDashboard(true);
      broadcastRefresh();
      return send(res, 200, next);
    }
    if (req.method === "POST" && path === "/api/notifications/dismiss-all") {
      const prefs = await loadPrefs();
      const current = await getDashboard(false);
      const ids = (current.dashboard.notifications || []).map((n) => n.id).filter(Boolean);
      const set = new Set(prefs.dismissedNotifications);
      for (const id of ids) set.add(id);
      prefs.dismissedNotifications = [...set];
      await savePrefs(prefs);
      const next = await getDashboard(true);
      broadcastRefresh();
      return send(res, 200, next);
    }
    if (req.method === "POST" && path === "/api/notifications/restore") {
      const prefs = await loadPrefs();
      prefs.dismissedNotifications = [];
      await savePrefs(prefs);
      const next = await getDashboard(true);
      broadcastRefresh();
      return send(res, 200, next);
    }
    if (req.method === "GET" && path === "/api/accounts") {
      // Re-probe every detected credential against its account's watched repos.
      const prefs = await loadPrefs();
      const auth = await resolveAuth(prefs, { reprobe: true });
      const next = await getDashboard(true);
      broadcastRefresh();
      return send(res, 200, { accounts: auth.accounts, ...next });
    }
    if (req.method === "GET" && path === "/events") {
      res.writeHead(200, {
        "Content-Type": "text/event-stream",
        "Cache-Control": "no-cache",
        Connection: "keep-alive",
      });
      res.write(": connected\n\n");
      sseClients.add(res);
      req.on("close", () => sseClients.delete(res));
      return;
    }
    return send(res, 404, { error: "not found" });
  } catch (e) {
    log?.(`request error ${path}: ${e.message}`);
    return send(res, 500, { error: e.message });
  }
}

export async function startInstance(instanceId, log) {
  let entry = servers.get(instanceId);
  if (entry) return entry;
  const server = createServer((req, res) => handle(req, res, log));
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  entry = { server, url: `http://127.0.0.1:${port}/` };
  servers.set(instanceId, entry);
  return entry;
}

export async function stopInstance(instanceId) {
  const entry = servers.get(instanceId);
  if (!entry) return;
  servers.delete(instanceId);
  await new Promise((resolve) => entry.server.close(() => resolve()));
}

export async function forceRefresh() {
  const next = await getDashboard(true);
  broadcastRefresh();
  return next;
}

export async function rescanAccounts() {
  const prefs = await loadPrefs();
  const auth = await resolveAuth(prefs, { reprobe: true });
  const next = await getDashboard(true);
  broadcastRefresh();
  return { accounts: auth.accounts, activeAccounts: next.dashboard.activeAccounts ?? [], dashboard: next.dashboard };
}

export async function toggleAccount(id, active) {
  const prefs = await loadPrefs();
  setAccountActive(prefs, id, !!active);
  await savePrefs(prefs);
  invalidateAuth();
  const next = await getDashboard(true);
  broadcastRefresh();
  return next;
}

export async function setReposFor(id, repos) {
  const prefs = await loadPrefs();
  setAccountRepos(prefs, id, parseRepos(repos));
  await savePrefs(prefs);
  invalidateAuth();
  const next = await getDashboard(true);
  broadcastRefresh();
  return next;
}

export { getDashboard };
