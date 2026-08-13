// Multi-account credential detection and probing for the Aspire Team App canvas.
//
// The Copilot App can have several GitHub credentials available at once (multiple
// `gh` CLI accounts, the GH_TOKEN/GITHUB_TOKEN env tokens, and per-account
// COPILOT_GH_ACCOUNT_* tokens). They do not all have the same scopes or repo
// access. This module enumerates every candidate, resolves each to a GitHub
// login, then probes that login's *own* configured repositories. Several accounts
// can be active at once; the server interleaves their results.

import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { createHash } from "node:crypto";

import { coreTeamMemberAliasSuffixes } from "./constants.mjs";

const execFileAsync = promisify(execFile);

const API = "https://api.github.com";
const GRAPHQL = `${API}/graphql`;
const UA = "aspire-team-app-canvas";

// REST + GraphQL endpoints for a given host. github.com uses the public API
// origin; GitHub Enterprise Server (GHES) exposes /api/v3 and /api/graphql on the
// instance host itself.
function normalizeHost(host) {
  const h = String(host ?? "")
    .trim()
    .toLowerCase()
    .replace(/^https?:\/\//, "")
    .replace(/\/.*$/, "");
  return !h || h === "github.com" || h === "api.github.com" ? "github.com" : h;
}

function endpoints(host) {
  const h = normalizeHost(host);
  if (h === "github.com") {
    return { rest: "https://api.github.com", graphql: "https://api.github.com/graphql" };
  }
  return { rest: `https://${h}/api/v3`, graphql: `https://${h}/api/graphql` };
}

export function isEnterpriseHost(host) {
  return normalizeHost(host) !== "github.com";
}

function tokenHash(token) {
  return createHash("sha256").update(token).digest("hex").slice(0, 10);
}

// ---------------------------------------------------------------------------
// Candidate enumeration
// ---------------------------------------------------------------------------

async function ghAccounts() {
  try {
    const { stdout } = await execFileAsync("gh", ["auth", "status"], { timeout: 8000 });
    const accounts = [];
    const re = /Logged in to (\S+) account (\S+)/g;
    let m;
    while ((m = re.exec(stdout)) !== null) {
      accounts.push({ host: m[1], login: m[2] });
    }
    return accounts;
  } catch {
    return [];
  }
}

async function ghToken(host, login) {
  try {
    const { stdout } = await execFileAsync(
      "gh",
      ["auth", "token", "--hostname", host, "--user", login],
      { timeout: 8000 },
    );
    return stdout.trim() || null;
  } catch {
    return null;
  }
}

function decodeCopilotAccount(varName) {
  // COPILOT_GH_ACCOUNT_github_2E_com_dapine_5F_microsoft -> { host:"github.com", login:"dapine_microsoft" }
  // Enterprise: COPILOT_GH_ACCOUNT_github_2E_acme_2E_com_someone -> { host:"github.acme.com", login:"someone" }
  let s = varName.replace(/^COPILOT_GH_ACCOUNT_/, "");
  // Encode the escaped tokens to placeholders so the host/login separator (a bare
  // underscore) can be found unambiguously: dots are "_2E_", literal underscores
  // in the login are "_5F_".
  s = s.replace(/_2E_/g, "\u0001").replace(/_5F_/g, "\u0002");
  const sep = s.indexOf("_");
  let host;
  let login;
  if (sep === -1) {
    host = "github.com";
    login = s;
  } else {
    host = s.slice(0, sep);
    login = s.slice(sep + 1);
  }
  host = host.replace(/\u0001/g, ".").replace(/\u0002/g, "_") || "github.com";
  login = login.replace(/\u0001/g, ".").replace(/\u0002/g, "_") || null;
  return { host, login };
}

export async function detectCandidates() {
  const out = [];
  const seenHash = new Set();
  const add = (source, token, login, host) => {
    if (!token) return;
    const hash = tokenHash(token);
    if (seenHash.has(hash)) return; // identical token already captured
    seenHash.add(hash);
    out.push({ source, token, hash, login: login ?? null, host: normalizeHost(host) });
  };

  // gh CLI accounts (each may have distinct scopes / hosts).
  const accounts = await ghAccounts();
  for (const a of accounts) {
    add("gh", await ghToken(a.host, a.login), a.login, a.host);
  }

  // Plain env tokens (host from GH_HOST when targeting an enterprise instance).
  const envHost = normalizeHost(process.env.GH_HOST || "github.com");
  add("env", process.env.GH_TOKEN, null, envHost);
  add("env", process.env.GITHUB_TOKEN, null, envHost);

  // Per-account Copilot tokens injected by the host (host encoded in the var name).
  for (const [k, v] of Object.entries(process.env)) {
    if (k.startsWith("COPILOT_GH_ACCOUNT_") && v) {
      const { host, login } = decodeCopilotAccount(k);
      add("copilot", v, login, host);
    }
  }

  return out;
}

// ---------------------------------------------------------------------------
// Probing
// ---------------------------------------------------------------------------

async function gql(token, query, host) {
  const { graphql } = endpoints(host);
  const res = await fetch(graphql, {
    method: "POST",
    headers: { Authorization: `bearer ${token}`, "Content-Type": "application/json", "User-Agent": UA },
    body: JSON.stringify({ query }),
  });
  const json = await res.json().catch(() => ({}));
  return { ok: res.ok, data: json.data, errors: json.errors };
}

async function fetchScopes(token, host) {
  try {
    const { rest } = endpoints(host);
    const res = await fetch(`${rest}/`, {
      headers: { Authorization: `bearer ${token}`, "User-Agent": UA },
    });
    const raw = res.headers.get("x-oauth-scopes");
    return raw ? raw.split(",").map((s) => s.trim()).filter(Boolean) : [];
  } catch {
    return [];
  }
}

function aliasFor(i) {
  return `r${i}`;
}

// Pass 1: resolve the credential to a GitHub identity (login + avatar + scopes).
async function probeIdentity(candidate) {
  const { token } = candidate;
  const host = normalizeHost(candidate.host);
  const base = {
    source: candidate.source,
    hash: candidate.hash,
    host: host || "github.com",
    enterprise: isEnterpriseHost(host),
    login: candidate.login ?? null,
    avatarUrl: null,
    scopes: [],
  };
  try {
    const res = await gql(token, `query { viewer { login avatarUrl } }`, host);
    if (!res.data?.viewer?.login) {
      return { ...base, login: base.login ?? "unknown", ok: false, status: "failed",
        reason: res.errors?.[0]?.message ?? "Authentication failed" };
    }
    const scopes = await fetchScopes(token, host);
    return { ...base, login: res.data.viewer.login, avatarUrl: res.data.viewer.avatarUrl ?? null, scopes, ok: true };
  } catch (e) {
    return { ...base, login: base.login ?? "unknown", ok: false, status: "failed", reason: e.message };
  }
}

// Pass 2: probe how many of `repos` a credential can actually read.
async function probeRepoAccess(token, repos, host) {
  if (!repos.length) return { accessible: 0, total: 0, status: "ok" };
  const selections = repos
    .map((repo, i) => {
      const [owner, ...rest] = repo.split("/");
      const name = rest.join("/");
      return `${aliasFor(i)}: repository(owner:${JSON.stringify(owner)}, name:${JSON.stringify(name)}) { nameWithOwner }`;
    })
    .join("\n");
  const res = await gql(token, `query { ${selections} }`, host);
  let accessible = 0;
  if (res.data) {
    for (let i = 0; i < repos.length; i++) {
      if (res.data[aliasFor(i)]) accessible++;
    }
  }
  const total = repos.length;
  let status = "ok";
  if (accessible === 0) status = "limited";
  else if (accessible < total) status = "partial";
  return { accessible, total, status };
}

// A single account is keyed by its host + GitHub login. Logins are only unique
// within a host, so a github.com user and a GitHub Enterprise Server user that
// happen to share a login are distinct accounts and must not collapse together.
// The same (host, login) can still surface through several credentials (gh CLI,
// env, Copilot); we keep them all as "sources" under one account and use the
// strongest for API calls.
function loginKey(login) {
  return String(login ?? "unknown").trim().toLowerCase();
}

// The account id includes the normalized host so a github.com account and a GHES
// account with the same login never share repository preferences. state.mjs keeps
// a fallback for the previous github.com-only "acct:<login>" keys.
export function accountId(login, host) {
  const key = loginKey(login);
  const h = normalizeHost(host);
  return `acct:${h}/${key}`;
}

// Extract the login portion from an account id. Handles both the current
// "acct:<host>/<login>" shape and the legacy github.com-only "acct:<login>" shape.
function loginFromAccountId(id) {
  const raw = String(id ?? "");
  const withoutPrefix = raw.startsWith("acct:") ? raw.slice("acct:".length) : raw;
  const slash = withoutPrefix.lastIndexOf("/");
  return slash === -1 ? withoutPrefix : withoutPrefix.slice(slash + 1);
}

// Enterprise Managed User (EMU) accounts live on github.com like any other user,
// but their login carries an org alias suffix (e.g. "dapine_microsoft"). Those
// accounts see the private first-party repos rather than the public defaults, so
// repo defaults are keyed off this classification. The suffix list is the same one
// used for core-team author attribution (constants.coreTeamMemberAliasSuffixes),
// keeping a single source of truth for what an alias looks like.
export function isEmuLogin(login) {
  const normalized = String(login ?? "").trim().toLowerCase();
  return coreTeamMemberAliasSuffixes.some((suffix) => {
    const s = String(suffix).toLowerCase();
    return s.length > 0 && normalized.endsWith(s) && normalized.length > s.length;
  });
}

export function isEmuAccountId(id) {
  // EMU aliases only exist on github.com (see isEmuLogin), so classification must honor the
  // account's host. A host-scoped id ("acct:<host>/<login>") therefore has to name github.com
  // to be treated as EMU — otherwise a GHES account whose login happens to end in an org-alias
  // suffix (e.g. "acct:ghe.example.com/alice_microsoft") would be misclassified and handed the
  // dotcom-only devdiv-microsoft/aspire-1p default it cannot read. Legacy hostless ids
  // ("acct:<login>") predate host scoping and are github.com by definition, so they keep the
  // login-only classification.
  const host = hostFromAccountId(id);
  if (host !== null && normalizeHost(host) !== "github.com") {
    return false;
  }
  return isEmuLogin(loginFromAccountId(id));
}

// Extract the host portion from a host-scoped account id ("acct:<host>/<login>"). Returns
// null for the legacy hostless shape ("acct:<login>"), which callers treat as github.com.
function hostFromAccountId(id) {
  const raw = String(id ?? "");
  const withoutPrefix = raw.startsWith("acct:") ? raw.slice("acct:".length) : raw;
  const slash = withoutPrefix.lastIndexOf("/");
  return slash === -1 ? null : withoutPrefix.slice(0, slash);
}

function score(probe) {
  if (probe.status === "failed") return -1;
  let s = 100000; // authenticated
  s += (probe.accessible || 0) * 1000;
  if (probe.hasReadOrg) s += 100;
  // Prefer keyring/gh and copilot sources over a bare env token on ties.
  s += probe.source === "gh" ? 3 : probe.source === "copilot" ? 2 : 1;
  return s;
}

function accountScore(acct) {
  if (acct.status === "failed") return -1;
  let s = 100000;
  s += (acct.accessible || 0) * 1000;
  if (acct.hasReadOrg) s += 100;
  s += Math.min(acct.sources.length, 5);
  return s;
}

const SOURCE_LABEL = { gh: "GitHub CLI", env: "Environment", copilot: "Copilot" };

// Probe every credential, collapse them into one account per login (each with the
// repositories that account watches and whether the user marked it active), and
// return the chosen token per account. `reposForId(id)` returns the repos to probe
// for a given account; `isActive(id)` reports whether the user enabled it.
export async function resolveAccounts(reposForId, isActive) {
  const candidates = await detectCandidates();

  const idents = [];
  for (const c of candidates) {
    const p = await probeIdentity(c);
    p.token = c.token;
    idents.push(p);
  }

  // Group every credential under its (host, login) account identity so accounts
  // that share a login across github.com and an enterprise host stay separate.
  const groups = new Map();
  for (const p of idents) {
    const key = accountId(p.login, p.host);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(p);
  }

  const accounts = [];
  const tokenById = new Map();

  for (const [id, list] of groups) {
    const repos = reposForId(id);

    // Probe repo access for each credential against this account's own repos.
    for (const p of list) {
      if (p.ok) {
        const acc = await probeRepoAccess(p.token, repos, p.host);
        p.accessible = acc.accessible;
        p.total = acc.total;
        p.status = acc.status;
        p.hasReadOrg = (p.scopes || []).includes("read:org");
      } else {
        p.accessible = 0;
        p.total = repos.length;
        p.status = "failed";
        p.hasReadOrg = false;
      }
    }

    list.sort((a, b) => score(b) - score(a));
    const best = list[0];
    tokenById.set(id, best.token);

    const sources = list.map((p, i) => ({
      source: p.source,
      label: SOURCE_LABEL[p.source] ?? p.source,
      hash: p.hash,
      host: p.host,
      enterprise: p.enterprise,
      status: p.status,
      scopes: p.scopes,
      accessible: p.accessible,
      total: p.total,
      hasReadOrg: p.hasReadOrg,
      reason: p.reason ?? null,
      chosen: i === 0,
    }));

    accounts.push({
      id,
      login: best.login,
      avatarUrl: best.avatarUrl ?? (list.find((p) => p.avatarUrl) || {}).avatarUrl ?? null,
      host: best.host || "github.com",
      enterprise: isEnterpriseHost(best.host),
      graphql: endpoints(best.host).graphql,
      status: best.status,
      accessible: best.accessible,
      total: best.total,
      hasReadOrg: sources.some((s) => s.hasReadOrg),
      reason: best.reason ?? null,
      sources,
      sourceKinds: [...new Set(list.map((p) => p.source))],
      repos,
      active: !!isActive(id),
    });
  }

  accounts.sort((a, b) => accountScore(b) - accountScore(a));

  return { accounts, tokenById };
}
