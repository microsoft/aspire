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
import { DEFAULT_REPOS, CURRENT_RELEASE } from "./github.mjs";

const COPILOT_HOME = process.env.COPILOT_HOME || join(homedir(), ".copilot");
const ARTIFACT_DIR = join(COPILOT_HOME, "extensions", "aspire-team-app", "artifacts");
const PREFS_FILE = join(ARTIFACT_DIR, "preferences.json");

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
  dismissedNotifications: [],
  notifications: { ...DEFAULT_NOTIFICATIONS },
  // Per-account configuration keyed by account id ("acct:<login>"):
  //   { [id]: { repos: string[], active: boolean } }
  accounts: {},
};

function normalizeAccounts(raw) {
  const out = {};
  if (!raw || typeof raw !== "object") return out;
  for (const [id, cfg] of Object.entries(raw)) {
    const repos = Array.isArray(cfg?.repos) && cfg.repos.length ? cfg.repos : [...DEFAULT_REPOS];
    out[id] = { repos: [...new Set(repos)], active: !!cfg?.active };
  }
  return out;
}

function migrate(parsed) {
  const prefs = {
    ...DEFAULT_PREFS,
    ...parsed,
    showDrafts: !!parsed.showDrafts,
    notifications: { ...DEFAULT_NOTIFICATIONS, ...(parsed.notifications ?? {}) },
    dismissedNotifications: Array.isArray(parsed.dismissedNotifications) ? parsed.dismissedNotifications : [],
    accounts: normalizeAccounts(parsed.accounts),
  };
  // Upgrade the legacy single-account shape ({ repos, account }) to the per-account map.
  if (Object.keys(prefs.accounts).length === 0 && parsed.account) {
    const repos = Array.isArray(parsed.repos) && parsed.repos.length ? parsed.repos : [...DEFAULT_REPOS];
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
      accounts: {},
    };
  }
}

export async function savePrefs(prefs) {
  await mkdir(ARTIFACT_DIR, { recursive: true });
  await writeFile(PREFS_FILE, JSON.stringify(prefs, null, 2) + "\n", "utf8");
  return prefs;
}

// ---------------------------------------------------------------------------
// Per-account helpers
// ---------------------------------------------------------------------------

export function accountConfig(prefs, id) {
  const cfg = prefs.accounts?.[id];
  return {
    repos: Array.isArray(cfg?.repos) && cfg.repos.length ? cfg.repos : [...DEFAULT_REPOS],
    active: !!cfg?.active,
    // Whether the user has ever explicitly configured this account.
    configured: !!cfg,
  };
}

export function setAccountRepos(prefs, id, repos) {
  if (!prefs.accounts) prefs.accounts = {};
  const cfg = accountConfig(prefs, id);
  const clean = [...new Set((Array.isArray(repos) ? repos : []).map((r) => String(r).trim()).filter(Boolean))];
  prefs.accounts[id] = { repos: clean.length ? clean : [...DEFAULT_REPOS], active: cfg.active };
  return prefs;
}

export function setAccountActive(prefs, id, active) {
  if (!prefs.accounts) prefs.accounts = {};
  const cfg = accountConfig(prefs, id);
  prefs.accounts[id] = { repos: cfg.repos, active: !!active };
  return prefs;
}

export function activeIds(prefs) {
  return Object.entries(prefs.accounts || {})
    .filter(([, c]) => c && c.active)
    .map(([id]) => id);
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
