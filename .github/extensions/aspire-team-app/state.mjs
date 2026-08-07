// Durable preferences for the Aspire Team App canvas.
//
// Watched repos and notification settings follow the user across sessions, so per
// the canvas state model they live under $COPILOT_HOME/extensions/<name>/artifacts/
// rather than being keyed by the transient instanceId.
//
// Repositories are configured *per GitHub account* (keyed by account id), so each
// account watches its own set and any number of accounts can be active at once.
// Results from every active account are interleaved into the same tabs.

import { mkdir, readFile, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import { DEFAULT_REPOS, DEFAULT_EMU_REPOS, CURRENT_RELEASE } from "./github.mjs";
import { isEmuAccountId } from "./accounts.mjs";

const COPILOT_HOME = process.env.COPILOT_HOME || join(homedir(), ".copilot");
const ARTIFACT_DIR = join(COPILOT_HOME, "extensions", "aspire-team-app", "artifacts");
const PREFS_FILE = join(ARTIFACT_DIR, "preferences.json");
let prefsUpdate = Promise.resolve();

export const DEFAULT_NOTIFICATIONS = {
  reviewRequested: true,
  readyToMerge: true,
  changesRequested: true,
  ciFailing: true,
};

export const DEFAULT_PREFS = {
  mode: "review",
  release: CURRENT_RELEASE,
  showDrafts: false,
  autoApplyUpdates: true,
  dismissedNotifications: [],
  notifications: { ...DEFAULT_NOTIFICATIONS },
  azurePipelines: [],
  healthOrder: [],
  // Per-account configuration keyed by account id ("acct:<host>/<login>"):
  //   { [id]: { repos: string[], active: boolean } }
  accounts: {},
};

function normalizeAccounts(raw) {
  const out = {};
  if (!raw || typeof raw !== "object") return out;
  for (const [id, cfg] of Object.entries(raw)) {
    const repos = Array.isArray(cfg?.repos) && cfg.repos.length ? cfg.repos : [...defaultReposForId(id)];
    out[id] = { repos: [...new Set(repos)], active: !!cfg?.active };
  }
  return out;
}

function legacyIdFor(id) {
  const prefix = "acct:github.com/";
  const value = String(id || "").toLowerCase();
  return value.startsWith(prefix) ? `acct:${value.slice(prefix.length)}` : null;
}

function migrate(parsed) {
  const prefs = {
    ...DEFAULT_PREFS,
    ...parsed,
    showDrafts: !!parsed.showDrafts,
    autoApplyUpdates: parsed.autoApplyUpdates !== false,
    notifications: { ...DEFAULT_NOTIFICATIONS, ...(parsed.notifications ?? {}) },
    dismissedNotifications: Array.isArray(parsed.dismissedNotifications) ? parsed.dismissedNotifications : [],
    azurePipelines: normalizeAzurePipelines(parsed.azurePipelines),
    healthOrder: normalizeHealthOrder(parsed.healthOrder),
    accounts: normalizeAccounts(parsed.accounts),
  };
  // Upgrade the legacy single-account shape ({ repos, account }) to the per-account map.
  if (Object.keys(prefs.accounts).length === 0 && parsed.account) {
    const repos = Array.isArray(parsed.repos) && parsed.repos.length ? parsed.repos : [...defaultReposForId(parsed.account)];
    prefs.accounts[parsed.account] = { repos: [...new Set(repos)], active: true };
  }
  delete prefs.repos;
  delete prefs.account;
  return prefs;
}

export async function loadPrefs() {
  try {
    const raw = await readFile(PREFS_FILE, "utf8");
    return migrate(JSON.parse(raw));
  } catch {
    return {
      ...DEFAULT_PREFS,
      notifications: { ...DEFAULT_NOTIFICATIONS },
      dismissedNotifications: [],
      azurePipelines: [],
      healthOrder: [],
      accounts: {},
    };
  }
}

export async function savePrefs(prefs) {
  await mkdir(ARTIFACT_DIR, { recursive: true });
  await writeFile(PREFS_FILE, JSON.stringify(prefs, null, 2) + "\n", "utf8");
  return prefs;
}

// Every canvas instance shares this preference file. Serialize read-modify-write operations so
// simultaneous toolbar, settings, and account changes cannot overwrite one another with stale copies.
export function updatePrefs(mutator) {
  const run = prefsUpdate
    .catch(() => {})
    .then(async () => {
      const prefs = await loadPrefs();
      await mutator(prefs);
      return savePrefs(prefs);
    });
  prefsUpdate = run;
  return run;
}

// ---------------------------------------------------------------------------
// Per-account helpers
// ---------------------------------------------------------------------------

// The default repo watch set for an account that the user has not configured. EMU
// accounts default to the private first-party repos; everyone else gets the public
// Aspire repos. This only fills in the fallback — it never overrides repos a user
// has explicitly configured (see accountConfig/setAccountRepos below).
function defaultReposForId(id) {
  return isEmuAccountId(id) ? DEFAULT_EMU_REPOS : DEFAULT_REPOS;
}

export function accountConfig(prefs, id, legacyId = legacyIdFor(id)) {
  const cfg = prefs.accounts?.[id] ?? (legacyId ? prefs.accounts?.[legacyId] : undefined);
  return {
    repos: Array.isArray(cfg?.repos) && cfg.repos.length ? cfg.repos : [...defaultReposForId(id)],
    active: !!cfg?.active,
    // Whether the user has ever explicitly configured this account.
    configured: !!cfg,
  };
}

export function setAccountRepos(prefs, id, repos) {
  if (!prefs.accounts) prefs.accounts = {};
  const legacyId = legacyIdFor(id);
  const cfg = accountConfig(prefs, id, legacyId);
  const clean = [...new Set((Array.isArray(repos) ? repos : []).map((r) => String(r).trim()).filter(Boolean))];
  prefs.accounts[id] = { repos: clean.length ? clean : [...defaultReposForId(id)], active: cfg.active };
  if (legacyId) delete prefs.accounts[legacyId];
  return prefs;
}

export function setAccountActive(prefs, id, active) {
  if (!prefs.accounts) prefs.accounts = {};
  const legacyId = legacyIdFor(id);
  const cfg = accountConfig(prefs, id, legacyId);
  prefs.accounts[id] = { repos: cfg.repos, active: !!active };
  if (legacyId) delete prefs.accounts[legacyId];
  return prefs;
}

export function activeIds(prefs) {
  return Object.entries(prefs.accounts || {})
    .filter(([, c]) => c && c.active)
    .map(([id]) => id);
}

export function normalizeAzurePipelines(value) {
  const out = [];
  const seen = new Set();
  for (const pipeline of Array.isArray(value) ? value : []) {
    const id = String(pipeline?.id ?? "").trim();
    const url = String(pipeline?.url ?? "").trim();
    const definitionId = Number(pipeline?.definitionId);
    if (!id || !url || !Number.isInteger(definitionId) || definitionId <= 0 || seen.has(id)) continue;
    seen.add(id);
    out.push({
      id,
      url,
      organization: String(pipeline.organization ?? "").trim(),
      organizationName: String(pipeline.organizationName ?? "").trim(),
      project: String(pipeline.project ?? "").trim(),
      definitionId,
      name: String(pipeline.name ?? `Pipeline ${definitionId}`).trim(),
      branch: String(pipeline.branch ?? "refs/heads/main").trim() || "refs/heads/main",
      repository: pipeline.repository && typeof pipeline.repository === "object"
        ? {
            id: pipeline.repository.id ?? null,
            name: pipeline.repository.name ?? null,
            type: pipeline.repository.type ?? null,
            url: pipeline.repository.url ?? null,
            defaultBranch: pipeline.repository.defaultBranch ?? null,
          }
        : null,
    });
  }
  return out;
}

export function addAzurePipeline(prefs, pipeline) {
  const next = normalizeAzurePipelines([pipeline]);
  if (next.length !== 1) throw new Error("Invalid Azure DevOps pipeline configuration");
  if (!Array.isArray(prefs.azurePipelines)) prefs.azurePipelines = [];
  prefs.azurePipelines = normalizeAzurePipelines([
    ...prefs.azurePipelines.filter((item) => item?.id !== next[0].id),
    next[0],
  ]);
  return prefs;
}

export function removeAzurePipeline(prefs, id) {
  const key = String(id ?? "").trim();
  prefs.azurePipelines = normalizeAzurePipelines(prefs.azurePipelines).filter((pipeline) => pipeline.id !== key);
  return prefs;
}

export function normalizeHealthOrder(value) {
  const out = [];
  const seen = new Set();
  for (const raw of Array.isArray(value) ? value : []) {
    const id = String(raw ?? "").trim();
    if (!id || id.length > 512 || seen.has(id) || out.length >= 500) continue;
    seen.add(id);
    out.push(id);
  }
  return out;
}

export function setHealthOrder(prefs, order) {
  prefs.healthOrder = normalizeHealthOrder(order);
  return prefs;
}

export function parseRepos(value, fallback = DEFAULT_REPOS) {
  const source = Array.isArray(value) ? value.join(" ") : String(value || "");
  const repos = source
    .split(/[,\s]+/)
    .map((r) => r.trim())
    .filter(Boolean);
  const unique = [...new Set(repos)];
  return unique.length > 0 ? unique : [...fallback];
}
