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
import { buildAgentActionPrompt, buildAgentActionLog, resolveActionTarget } from "./agent.mjs";
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
// Maps each open SSE response to the instanceId whose server accepted it. sseClients is a
// module-global shared across every canvas instance (broadcasts fan out to all open
// iframes), but shutdown must be per-instance: closing one canvas must not end another
// still-open canvas's stream. A WeakMap avoids leaking entries once a response is GC'd.
const clientInstance = new WeakMap();
let cache = null;    // { dashboard, prefs, at }
let inflight = null;
let bgTimer = null;
// Monotonic snapshot revision stamped on every dashboard we cache/broadcast (partial and
// final, across every compute). The client applies snapshots strictly in seq order so a
// wall-clock fetchedAt collision can't drop the final snapshot and an out-of-order partial
// can't overwrite a newer one. Never reset — it only ever increments for the process.
let stateSeq = 0;
// Logger captured from the most recent startInstance so background (non-request) work —
// the poller and stale-while-revalidate refreshes — has somewhere to report failures.
let bgLog = null;

// Stale-while-revalidate window: /api/state serves the cached dashboard instantly and
// only kicks a background refresh once the cache is older than this.
const STATE_TTL = 45 * 1000;
// Background monitor cadence. While at least one iframe is connected we silently
// re-fetch on this interval so open canvases pick up new PRs without a manual refresh.
const POLL_INTERVAL = 90 * 1000;

// Bridge to the main Copilot session, injected once from extension.mjs after
// joinSession resolves (server.mjs can't import the SDK session itself). Card action
// buttons post a prompt through here so the agent — not this server — opens PR
// sub-sessions or does interactive work. Null until wired, so a click that races
// startup fails cleanly instead of throwing an undefined-call.
let agentSend = null;

// Called from extension.mjs. The injected fn receives { prompt, log } and returns
// { messageId, queued } — queued is true when the agent was already mid-turn, so the
// prompt waits behind the current task rather than starting immediately.
export function setAgentSend(fn) {
  agentSend = typeof fn === "function" ? fn : null;
}

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

// Decorate a loaded dashboard with the account context the canvas actions in extension.mjs
// read back off an active account: set_repos reads `repos`, summary reads
// `sourceKinds`/`status`/`repos`. Omitting them made set_repos return an empty repo list
// and summary report undefined sources/status for active accounts.
function decorateDashboard(dashboard, auth, active, prefs) {
  if (!dashboard || dashboard.authenticated === false) return;
  dashboard.accounts = auth.accounts;
  dashboard.activeAccounts = active.map((a) => ({ id: a.id, login: a.login, avatarUrl: a.avatarUrl, enterprise: a.enterprise, host: a.host, repos: a.repos, status: a.status, sourceKinds: a.sourceKinds }));
  dashboard.dismissedCount = (prefs.dismissedNotifications || []).length;
}

// Compute a fresh dashboard. When at least one iframe is connected we stream results:
// `progress` ticks drive the deterministic client bar, and throttled `partial` snapshots
// let cards fill in as each repo's PRs arrive, ending with a final authoritative `state`
// push. Background polls pass progress:false so the bar doesn't flash every cycle while
// the silent partial/final state pushes still refresh the UI.
async function computeDashboard({ progress = true } = {}) {
  const stream = sseClients.size > 0;
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
      onProgress: stream && progress ? broadcastProgress : undefined,
      onPartial: stream
        ? (partial) => {
            decorateDashboard(partial, auth, active, prefs);
            // Stamp a strictly increasing revision so the client can order/ignore snapshots.
            partial.seq = ++stateSeq;
            // Publish the partial as the current cache so a canvas opening mid-load gets
            // the freshest data-so-far, and push it to already-open iframes.
            cache = { dashboard: partial, prefs, at: Date.now() };
            broadcastState(partial, prefs);
          }
        : undefined,
    });
    decorateDashboard(dashboard, auth, active, prefs);
  }
  // Stamp the final (or unauthenticated) snapshot after any partials so it always carries the
  // highest seq of this compute; the POST response returns this same cached object.
  dashboard.seq = ++stateSeq;
  cache = { dashboard, prefs, at: Date.now() };
  if (stream) broadcastState(dashboard, prefs);
  return cache;
}

// Single-flight guard so a user refresh and the background poller share one in-flight load
// instead of fanning out duplicate GitHub requests. A forced refresh (force:true) must
// reflect state saved *after* an in-flight load began — e.g. a /api/mode, /api/prefs, or
// account mutation that just wrote prefs — so it never reuses the current computation.
// Instead it queues a fresh generation after any in-flight one settles (avoiding duplicate
// concurrent fan-out) and becomes the new in-flight, so the mutation response and streamed
// state reflect the latest prefs rather than the pre-change ones.
function startCompute(opts, force = false) {
  if (inflight && !force) return inflight;
  const prior = inflight;
  const run = (prior ? prior.catch(() => {}) : Promise.resolve())
    .then(() => computeDashboard(opts))
    .finally(() => {
      // Clear the in-flight marker whether the load resolved OR threw, but only if a newer
      // forced generation hasn't already superseded it. A rejected promise left here would
      // otherwise replay the same failure to every later request until the process restarted.
      if (inflight === run) inflight = null;
    });
  inflight = run;
  return inflight;
}

// Fire-and-forget background revalidation. startCompute() rejects on a transient
// credential/GitHub failure; without a handler that rejection would surface as an
// unhandledRejection and could terminate the extension host instead of merely leaving the
// stale cache in place. Swallow + log it so the next tick (poller or TTL) can retry safely.
function backgroundRefresh(opts) {
  Promise.resolve()
    .then(() => startCompute(opts))
    .catch((e) => {
      // bgLog wraps the async session logger (session.log). If the session has disconnected the
      // log call itself can reject, and returning that rejected promise from this handler would
      // recreate the very unhandled-rejection path this wrapper exists to prevent. Swallow the
      // logger's own failure (async via .catch, sync via try) so the chain always settles.
      try {
        const logged = bgLog?.(`background refresh failed: ${e?.message || e}`);
        logged?.catch?.(() => {});
      } catch { /* logger unavailable */ }
    });
}

async function getDashboard(force = false) {
  if (!force && cache) {
    // Stale-while-revalidate: hand back the cached dashboard immediately so (re)opening the
    // canvas is instant, and kick a silent background refresh once it's aged past the TTL.
    // progress:false so this passive revalidation doesn't flash the top bar — only the very
    // first load and explicit user refreshes drive it. New data still streams via `state`.
    if (Date.now() - (cache.at || 0) > STATE_TTL) backgroundRefresh({ progress: false });
    return cache;
  }
  return startCompute(undefined, force);
}

// Background monitor: while iframes are connected, silently revalidate on an interval so
// open canvases surface new/updated PRs without a manual refresh. Unref'd + gated on client
// count so it never keeps the process alive or works when nobody is watching.
function ensurePoller() {
  if (bgTimer) return;
  bgTimer = setInterval(() => {
    if (sseClients.size === 0) return;
    // Route through backgroundRefresh so a rejected poll can't become an unhandled rejection
    // on the timer path (which has no caller to await it) and crash the extension.
    backgroundRefresh({ progress: false });
  }, POLL_INTERVAL);
  if (typeof bgTimer.unref === "function") bgTimer.unref();
}

function writeSse(event, data) {
  for (const res of sseClients) {
    try {
      res.write(`event: ${event}\ndata: ${data}\n\n`);
    } catch {
      sseClients.delete(res);
    }
  }
}

// SSE data lines must be single-line; JSON.stringify escapes any newlines inside strings,
// so the whole dashboard/prefs payload is safe to emit as one `data:` line.
function broadcastState(dashboard, prefs) {
  writeSse("state", JSON.stringify({ dashboard, prefs }));
}

function broadcastProgress(p) {
  writeSse("progress", JSON.stringify(p));
}

// Legacy nudge kept for resilience: tells clients to re-pull /api/state. The streaming
// `state` push above is the primary path; this is a harmless fallback for any client that
// only listens for `refresh`.
function broadcastRefresh() {
  writeSse("refresh", "1");
}

// Resolve a client-supplied card-action PR descriptor against the server's own cached
// dashboard so a tampered url/title/author (ultimately sourced from attacker-controllable
// PR metadata) can't reach the agent prompt. Every card the user can click was computed by
// this server and lives in the current cache, keyed by "<owner/repo>#<number>". On GitHub a
// number is unique per repo across issues and PRs, so that key is unambiguous. When the PR
// is found we return the server's own canonical url/title/author; when it isn't (e.g. the
// cache aged out the PR) we return only the structural owner/repo/number and clear the url,
// letting agent.mjs reconstruct a canonical github.com link rather than trust the client.
function resolveActionPr(pr) {
  const repository = String(pr?.repository ?? "").trim();
  const number = Number.parseInt(pr?.number, 10);
  const canonical = Number.isInteger(number)
    ? findCachedPr(repository, number, pr?.url)
    : undefined;
  if (canonical) {
    return { repository: canonical.repository, number: canonical.number, url: canonical.url, title: canonical.title, author: canonical.author };
  }
  return { repository, number, url: "", title: "", author: "" };
}

// Parse the host out of a canonical PR url, lower-cased for comparison. Returns "" on any
// malformed/empty input so a tampered hint simply fails to select a candidate.
function hostOf(url) {
  try {
    return new URL(String(url)).host.toLowerCase();
  } catch {
    return "";
  }
}

// Walk the cached dashboard for PR/issue nodes matching owner/repo#number. repository#number
// is NOT globally unique across the GitHub hosts this app can watch at once (github.com plus a
// GHES/EMU instance): two active accounts on different hosts can each hold a distinct PR with
// the identical slug and number. So we collect EVERY match (deduped by canonical url, since one
// PR can surface in several lanes) instead of stopping at the first, then disambiguate by the
// host of the client-supplied url. That url is used ONLY to pick among the user's own cached
// PRs — the returned url/title/author always come from the cache, never the client, so a
// tampered hint can at worst fail to select a candidate. A single match resolves outright; when
// multiple hosts collide we require the hint's host to select exactly one and otherwise reject
// (undefined) rather than silently targeting whichever host the scan reached first.
function findCachedPr(repository, number, urlHint) {
  const dashboard = cache?.dashboard;
  if (!dashboard || !repository) return undefined;
  const target = `${repository}#${number}`.toLowerCase();
  const byUrl = new Map();
  const visit = (node) => {
    if (!node || typeof node !== "object") return;
    if (Array.isArray(node)) {
      for (const v of node) visit(v);
      return;
    }
    if (typeof node.repository === "string" && Number.isInteger(node.number) && typeof node.url === "string" && node.url
      && `${node.repository}#${node.number}`.toLowerCase() === target) {
      if (!byUrl.has(node.url)) {
        byUrl.set(node.url, {
          repository: node.repository,
          number: node.number,
          url: node.url,
          title: typeof node.title === "string" ? node.title : "",
          author: typeof node.author === "string" ? node.author : "",
        });
      }
      // A matched PR node's own properties are never other top-level PRs, so don't descend
      // into it (mirrors the original single-match traversal).
      return;
    }
    for (const v of Object.values(node)) visit(v);
  };
  visit(dashboard);
  const matches = [...byUrl.values()];
  if (matches.length <= 1) return matches[0];
  const wantHost = hostOf(urlHint);
  const onHost = wantHost ? matches.filter((m) => hostOf(m.url) === wantHost) : [];
  return onHost.length === 1 ? onHost[0] : undefined;
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
export function isAllowedPostRequest(req) {
  const host = req.headers.host;
  if (!host) {
    return false;
  }
  // Pin the Host header to THIS server's real loopback origin — a loopback hostname on the exact
  // ephemeral port this connection was accepted on (req.socket.localPort). We must not derive the
  // expected origin from the attacker-controlled Host header, because that enables DNS rebinding:
  // a page on evil.example.com (whose DNS is rebound to 127.0.0.1) is "same-origin" with its own
  // hostname, so Host, Origin, and Sec-Fetch-Site: same-origin would all agree and a forged
  // request could enqueue privileged Test/Review/conflict actions into the Copilot session. The
  // socket's localPort can't be spoofed by request headers, so it is the trustworthy anchor.
  // See https://en.wikipedia.org/wiki/DNS_rebinding.
  if (!isLoopbackHost(host, req.socket?.localPort)) {
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

// A legitimate request carries a Host header naming this server's own loopback origin: a loopback
// hostname (127.0.0.1 / localhost / [::1]) on the exact ephemeral port this connection landed on.
// The server always listens on 127.0.0.1:<random port> and the iframe url embeds that port, so a
// public hostname (even one that resolves to 127.0.0.1 via rebinding) or a mismatched/absent port
// is never legitimate and is rejected before any mutating handler runs.
function isLoopbackHost(hostHeader, localPort) {
  if (!Number.isInteger(localPort)) {
    return false;
  }
  let url;
  try {
    // Wrap the bare authority in a scheme so URL parses hostname/port (and normalizes IPv6 forms).
    url = new URL(`http://${hostHeader}`);
  } catch {
    return false;
  }
  // Host must carry an explicit port matching the accepted socket. A default/absent port ("" ->
  // 80) never matches an ephemeral listener, so a Host without a port is rejected too.
  if (url.port === "" || Number(url.port) !== localPort) {
    return false;
  }
  return url.hostname === "127.0.0.1" || url.hostname === "localhost" || url.hostname === "[::1]";
}

async function handle(req, res, log, instanceId) {
  const url = new URL(req.url, "http://127.0.0.1");
  const path = url.pathname;

  try {
    // Pin EVERY request to this server's own loopback origin before serving anything — not just
    // mutating POSTs. The iframe this server rendered at http://127.0.0.1:<our port>/ always calls
    // back with that loopback Host, so a request whose Host isn't our loopback origin isn't from
    // our iframe. This must gate reads too: the /events stream and /api/state response carry
    // private PR metadata and watched-repo preferences, so a DNS-rebinding page (evil.example
    // rebound to 127.0.0.1, "same-origin" with its own hostname) could otherwise READ that data
    // even though it can't POST. isLoopbackHost anchors on the accepted socket's localPort, which
    // request headers can't spoof. See https://en.wikipedia.org/wiki/DNS_rebinding.
    if (!isLoopbackHost(req.headers.host, req.socket?.localPort)) {
      return send(res, 403, { error: "forbidden" });
    }

    // Every mutating route on this API is a POST, so additionally gate POSTs on the CSRF origin
    // guard (Origin / Sec-Fetch-Site) before dispatching to any handler that reads the body or
    // writes preferences.
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
        // Pass an empty fallback so a cleared submission resets to the account's own
        // default (public vs EMU) inside setAccountRepos, rather than parseRepos
        // pre-filling the public default here.
        setAccountRepos(prefs, id, parseRepos(repos, []));
        await savePrefs(prefs);
        invalidateAuth();
      }
      const next = await getDashboard(true);
      return send(res, 200, next);
    }
    if (req.method === "POST" && path === "/api/agent/action") {
      // A card action button (Test / Review / Resolve conflicts / Address review)
      // posts { kind, target, pr }. target is "new-session" (open a sub-session in the
      // PR's repo) or "current-session" (work here). We build the prompt and hand it to
      // the main session via the injected bridge; the agent then opens a sub-session or
      // works the PR. This route does not touch the dashboard cache, so it neither
      // refreshes nor broadcasts.
      const { kind, target, pr } = await readBody(req);
      if (!agentSend) {
        return send(res, 503, { error: "The Copilot session is not ready yet. Try again in a moment." });
      }
      // Resolve the PR from server-side cache before building any prompt, so the operational
      // instruction handed to the agent uses this server's own canonical url and never a
      // client-tampered url/host/path (see resolveActionPr / agent.mjs safePrUrl).
      const resolvedPr = resolveActionPr(pr);
      let prompt;
      try {
        prompt = buildAgentActionPrompt(kind, resolvedPr, target);
      } catch (e) {
        return send(res, 400, { error: e.message });
      }
      const log = buildAgentActionLog(kind, resolvedPr, target);
      const result = await agentSend({ prompt, log });
      // Tolerate a bare messageId string in case an older bridge is wired.
      const messageId = typeof result === "string" ? result : (result && result.messageId) ?? null;
      const queued = typeof result === "object" && result ? !!result.queued : false;
      // Report the target actually used, not the one requested: buildAgentActionPrompt degrades
      // new-session to current-session for non-github.com (GHES) PRs, so echoing the raw request
      // would tell the client "Opened in a new session" when the work is really running in place.
      const effectiveTarget = resolveActionTarget(resolvedPr, target);
      return send(res, 200, { ok: true, kind, target: effectiveTarget, messageId, queued });
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
      // Remember which instance owns this stream so stopInstance ends only its own clients.
      clientInstance.set(res, instanceId);
      // A canvas is now watching — make sure the background monitor is running so its
      // queue keeps refreshing without a manual reload.
      ensurePoller();
      req.on("close", () => { sseClients.delete(res); clientInstance.delete(res); });
      return;
    }
    return send(res, 404, { error: "not found" });
  } catch (e) {
    // The error path's logger is the async session log, which can itself reject when the session
    // has disconnected — often the same failure that landed us here (e.g. a rejected agentSend).
    // Firing it without observing that rejection would surface as an unhandled rejection and can
    // terminate the extension host, so swallow the logger's own failure — sync via try, async via
    // .catch — exactly as backgroundRefresh does.
    try {
      const logged = log?.(`request error ${path}: ${e.message}`);
      logged?.catch?.(() => {});
    } catch { /* logger unavailable */ }
    return send(res, 500, { error: e.message });
  }
}

export async function startInstance(instanceId, log) {
  let entry = servers.get(instanceId);
  if (entry) return entry;
  if (log) bgLog = log;
  const server = createServer((req, res) => handle(req, res, log, instanceId));
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
  // SSE responses are long-lived, so server.close() would hang forever waiting for them
  // to drain. End the open event streams first, then force any lingering sockets closed so
  // shutdown completes promptly (e.g. when the canvas iframe is still connected). sseClients
  // is shared across all instances, so end ONLY the streams this instance accepted —
  // otherwise closing one canvas would disconnect every other still-open canvas.
  for (const res of [...sseClients]) {
    if (clientInstance.get(res) !== instanceId) continue;
    try { res.end(); } catch { /* already torn down */ }
    sseClients.delete(res);
    clientInstance.delete(res);
  }
  const closed = new Promise((resolve) => entry.server.close(() => resolve()));
  if (typeof entry.server.closeAllConnections === "function") {
    entry.server.closeAllConnections();
  }
  await closed;
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
  // Empty fallback: a cleared list resets to the account's own default in
  // setAccountRepos (public vs EMU) instead of parseRepos forcing the public one.
  setAccountRepos(prefs, id, parseRepos(repos, []));
  await savePrefs(prefs);
  invalidateAuth();
  const next = await getDashboard(true);
  broadcastRefresh();
  return next;
}

export { getDashboard };
