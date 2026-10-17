import { runAzureCli } from "./azure-devops.mjs";

export const MIRROR_ID = "mirror:microsoft/aspire:main";
export const MIRROR_URL = "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire";
export const GITHUB_URL = "https://github.com/microsoft/aspire";

const GITHUB_API = "https://api.github.com";
const GITHUB_REPO = "microsoft/aspire";
const MIRROR_ORGANIZATION = "https://dev.azure.com/dnceng";
const MIRROR_PROJECT = "internal";
const MIRROR_REPOSITORY = "microsoft-aspire";
const BRANCH = "refs/heads/main";
const SHA_RE = /^[0-9a-f]{40}$/i;
const FETCH_TIMEOUT_MS = 10_000;
const OBSERVATION_TIMEOUT_MS = 120_000;
const MAX_FIRST_PARENT_REQUESTS = 500;
const MAX_PULLS_PER_COMMIT = 10;
const MAX_NOTIFICATIONS = 20;
const WARNING_MS = 6 * 60 * 60 * 1000;
const CRITICAL_MS = 24 * 60 * 60 * 1000;

export async function loadMirrorObservation({
  token,
  now = new Date(),
  runAz = runAzureCli,
  fetchImpl = fetch,
} = {}) {
  const checkedAt = checkedAtIso(now);
  const deadline = Date.now() + OBSERVATION_TIMEOUT_MS;
  const [sourceSha, mirrorSha] = await Promise.all([
    loadGitHubHead({ token, fetchImpl, deadline }),
    loadMirrorHead(runAz),
  ]);

  if (sourceSha === mirrorSha) {
    return {
      status: "synced",
      sourceSha,
      mirrorSha,
      missingCount: 0,
      oldestSha: null,
      landedAt: null,
      checkedAt,
      compareUrl: null,
    };
  }

  const compare = await loadCompare({ token, fetchImpl, baseSha: mirrorSha, headSha: sourceSha, deadline });
  if (compare.status === "identical") {
    return {
      status: "synced",
      sourceSha,
      mirrorSha,
      missingCount: 0,
      oldestSha: null,
      landedAt: null,
      checkedAt,
      compareUrl: compare.html_url ?? null,
    };
  }

  if (compare.status !== "ahead") {
    return {
      status: "diverged",
      sourceSha,
      mirrorSha,
      missingCount: null,
      oldestSha: null,
      landedAt: null,
      checkedAt,
      compareUrl: compare.html_url ?? null,
    };
  }

  const missingCount = nonNegativeInteger(compare.ahead_by, "GitHub compare response did not include a valid missing commit count.");
  const oldestSha = await findOldestMissingFirstParentCommit({
    token,
    fetchImpl,
    sourceSha,
    mirrorSha,
    deadline,
  });
  const landedAt = await loadVerifiedPullRequestLandedAt({
    token,
    fetchImpl,
    sha: oldestSha,
    now,
    deadline,
  });

  return {
    status: "behind",
    sourceSha,
    mirrorSha,
    missingCount,
    oldestSha,
    landedAt,
    checkedAt,
    compareUrl: compare.html_url ?? `${GITHUB_URL}/compare/${mirrorSha}...${sourceSha}`,
  };
}

export function updateMirrorState(previous, observation, now = new Date()) {
  const nowIso = checkedAtIso(now);
  const checkedAt = observation?.checkedAt ?? checkedAtIso(now);
  const previousNotifications = normalizeNotifications(previous?.notifications);
  const previousObservation = previous?.observation ?? null;
  const lastSuccessAt = checkedAt;

  if (observation?.status === "synced") {
    const previousWarned = notificationRank(previous?.notifiedLevel) >= notificationRank("warning");
    const incidentId = previous?.incidentId ?? null;
    const notifications = previousWarned
      ? addNotification(previousNotifications, {
          id: notificationId(incidentId, "recovery"),
          level: "recovery",
          at: checkedAt,
        })
      : previousNotifications;

    return {
      observation,
      level: "synced",
      checkedAt,
      firstObservedAt: null,
      incidentId,
      notifiedLevel: previousWarned ? "recovery" : null,
      notifications,
      lastSuccessAt,
      error: null,
    };
  }

  if (observation?.status === "diverged") {
    const incidentId = previous?.observation?.status === "diverged"
      ? previous?.incidentId ?? divergenceIncidentId(observation)
      : divergenceIncidentId(observation);

    return {
      observation,
      level: "diverged",
      checkedAt,
      firstObservedAt: previous?.observation?.status === "diverged"
        ? previous?.firstObservedAt ?? nowIso
        : nowIso,
      incidentId,
      notifiedLevel: previous?.observation?.status === "diverged" ? previous?.notifiedLevel ?? null : null,
      notifications: previousNotifications,
      lastSuccessAt,
      error: null,
    };
  }

  if (observation?.status !== "behind" || !isSha(observation.oldestSha)) {
    return {
      observation,
      level: "unknown",
      checkedAt,
      firstObservedAt: previous?.firstObservedAt ?? nowIso,
      incidentId: previous?.incidentId ?? null,
      notifiedLevel: previous?.notifiedLevel ?? null,
      notifications: previousNotifications,
      lastSuccessAt,
      error: null,
    };
  }

  const sameOldest = previousObservation?.status === "behind"
    && previousObservation.oldestSha === observation.oldestSha;
  const sameIncident = previousObservation?.status === "behind";
  const firstObservedAt = sameOldest
    ? previous?.firstObservedAt ?? nowIso
    : nowIso;
  const incidentId = sameIncident
    ? previous?.incidentId ?? incidentIdForOldest(observation.oldestSha)
    : incidentIdForOldest(observation.oldestSha);
  const ageStart = validPastIso(observation.landedAt, now) ?? firstObservedAt;
  const ageMs = Math.max(0, now.getTime() - Date.parse(ageStart));
  const level = ageMs >= CRITICAL_MS ? "critical" : ageMs >= WARNING_MS ? "warning" : "catching-up";
  let notifiedLevel = sameIncident ? previous?.notifiedLevel ?? null : null;
  let notifications = previousNotifications;

  if (level === "warning" || level === "critical") {
    if (notificationRank(level) > notificationRank(notifiedLevel)) {
      notifications = addNotification(notifications, {
        id: notificationId(incidentId, level),
        level,
        at: checkedAt,
      });
      notifiedLevel = level;
    }
  }

  return {
    observation,
    level,
    checkedAt,
    firstObservedAt,
    incidentId,
    notifiedLevel,
    notifications,
    lastSuccessAt,
    error: null,
  };
}

async function loadGitHubHead({ token, fetchImpl, deadline }) {
  const ref = await githubJson({
    token,
    fetchImpl,
    path: `/repos/${GITHUB_REPO}/git/ref/heads/main`,
    label: "GitHub main ref",
    deadline,
  });
  return validateSha(ref?.object?.sha, "GitHub main ref did not include a valid 40-character SHA.");
}

async function loadMirrorHead(runAz) {
  let refs;
  try {
    refs = await runAz([
      "repos", "ref", "list",
      "--organization", MIRROR_ORGANIZATION,
      "--project", MIRROR_PROJECT,
      "--repository", MIRROR_REPOSITORY,
      "--filter", "heads/main",
    ]);
  } catch (error) {
    throw new Error(`Azure DevOps mirror head query failed. ${azureAction(error)}`);
  }

  const values = Array.isArray(refs) ? refs : Array.isArray(refs?.value) ? refs.value : [];
  const main = values.find((ref) => String(ref?.name ?? "").toLowerCase() === BRANCH);
  if (!main) {
    throw new Error("Azure DevOps mirror head query did not return refs/heads/main.");
  }
  return validateSha(main.objectId ?? main.objectIdSha ?? main.peeledObjectId, "Azure DevOps mirror ref did not include a valid 40-character SHA.");
}

async function loadCompare({ token, fetchImpl, baseSha, headSha, deadline }) {
  try {
    return await githubJson({
      token,
      fetchImpl,
      path: `/repos/${GITHUB_REPO}/compare/${baseSha}...${headSha}?per_page=1`,
      label: "GitHub compare",
      deadline,
    });
  } catch (error) {
    if (error instanceof GitHubHttpError && error.status === 404) {
      throw new Error("GitHub could not compare the mirror SHA with public microsoft/aspire main. The mirror head may not be present in public history; ancestry is unknown.");
    }
    throw error;
  }
}

async function findOldestMissingFirstParentCommit({ token, fetchImpl, sourceSha, mirrorSha, deadline }) {
  let current = sourceSha;
  let oldest = null;

  for (let i = 0; i < MAX_FIRST_PARENT_REQUESTS; i++) {
    throwIfObservationDeadlineExpired(deadline);
    const commit = await githubJson({
      token,
      fetchImpl,
      path: `/repos/${GITHUB_REPO}/commits/${current}`,
      label: "GitHub first-parent commit",
      deadline,
    });
    const sha = validateSha(commit?.sha, "GitHub commit response did not include a valid 40-character SHA.");
    if (sha !== current) {
      throw new Error("GitHub commit response did not match the requested SHA.");
    }
    oldest = current;

    const parent = firstParentSha(commit);
    if (parent === mirrorSha) return oldest;
    current = parent;
  }

  throw new Error(`Unable to establish the oldest first-parent main commit within ${MAX_FIRST_PARENT_REQUESTS} GitHub requests.`);
}

async function loadVerifiedPullRequestLandedAt({ token, fetchImpl, sha, now, deadline }) {
  const pulls = await githubJson({
    token,
    fetchImpl,
    path: `/repos/${GITHUB_REPO}/commits/${sha}/pulls?per_page=${MAX_PULLS_PER_COMMIT}`,
    label: "GitHub pull request evidence",
    accept: "application/vnd.github+json",
    deadline,
  });

  for (const pull of Array.isArray(pulls) ? pulls : []) {
    if (String(pull?.merge_commit_sha ?? "").toLowerCase() !== sha) continue;
    if (pull?.base?.repo?.full_name !== GITHUB_REPO || pull?.base?.ref !== "main") continue;

    const mergedAt = validPastIso(pull.merged_at, now);
    if (mergedAt) return mergedAt;
  }

  return null;
}

async function githubJson({ token, fetchImpl, path, label, accept = "application/vnd.github+json", deadline }) {
  throwIfObservationDeadlineExpired(deadline);
  const controller = new AbortController();
  const remaining = deadline ? Math.max(1, deadline - Date.now()) : FETCH_TIMEOUT_MS;
  const timeout = setTimeout(() => controller.abort(), Math.min(FETCH_TIMEOUT_MS, remaining));
  try {
    const response = await fetchImpl(`${GITHUB_API}${path}`, {
      method: "GET",
      headers: {
        Accept: accept,
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "aspire-team-app-canvas",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      signal: controller.signal,
    });

    if (!response?.ok) {
      throw new GitHubHttpError(response?.status ?? 0, `${label} request failed with HTTP ${response?.status ?? "unknown"}. ${githubAction(response?.status)}`);
    }

    try {
      return await response.json();
    } catch {
      throw new Error(`${label} returned invalid JSON.`);
    }
  } catch (error) {
    if (error instanceof GitHubHttpError) throw error;
    if (error?.name === "AbortError") {
      if (deadline && Date.now() >= deadline) {
        throw new Error("Mirror observation timed out before GitHub ancestry could be established.");
      }
      throw new Error(`${label} request timed out.`);
    }
    if (error?.message?.startsWith(label)) throw error;
    throw new Error(`${label} request failed. Check network connectivity and access to api.github.com.`);
  } finally {
    clearTimeout(timeout);
  }
}

function throwIfObservationDeadlineExpired(deadline) {
  if (deadline && Date.now() >= deadline) {
    throw new Error("Mirror observation timed out before GitHub ancestry could be established.");
  }
}

function firstParentSha(commit) {
  const parent = commit?.parents?.[0]?.sha;
  if (!parent) {
    throw new Error("GitHub first-parent history ended before reaching the mirror SHA.");
  }
  return validateSha(parent, "GitHub commit parent did not include a valid 40-character SHA.");
}

function validateSha(value, message) {
  const sha = String(value ?? "").trim().toLowerCase();
  if (!isSha(sha)) throw new Error(message);
  return sha;
}

function isSha(value) {
  return typeof value === "string" && SHA_RE.test(value);
}

function nonNegativeInteger(value, message) {
  if (!Number.isInteger(value) || value < 0) throw new Error(message);
  return value;
}

function checkedAtIso(now) {
  const value = now instanceof Date ? now : new Date(now);
  if (!Number.isFinite(value.getTime())) throw new Error("now must be a valid Date.");
  return value.toISOString();
}

function validPastIso(value, now) {
  if (typeof value !== "string" || !value) return null;
  const date = new Date(value);
  if (!Number.isFinite(date.getTime()) || date.getTime() > now.getTime()) return null;
  return date.toISOString();
}

function normalizeNotifications(value) {
  return Array.isArray(value) ? value.slice(0, MAX_NOTIFICATIONS) : [];
}

function addNotification(notifications, notification) {
  if (!notification.id) return notifications;
  if (notifications.some((item) => item?.id === notification.id)) return notifications;
  return [notification, ...notifications].slice(0, MAX_NOTIFICATIONS);
}

function incidentIdForOldest(oldestSha) {
  return `${MIRROR_ID}:${oldestSha}`;
}

function divergenceIncidentId(observation) {
  return `${MIRROR_ID}:diverged:${observation?.mirrorSha ?? "unknown"}:${observation?.sourceSha ?? "unknown"}`;
}

function notificationId(incidentId, level) {
  return incidentId ? `${incidentId}:${level}` : null;
}

function notificationRank(level) {
  return { recovery: 3, critical: 2, warning: 1 }[level] ?? 0;
}

function githubAction(status) {
  if (status === 401 || status === 403) return "Check the GitHub token permissions and rate limits.";
  if (status === 404) return "Check that the public microsoft/aspire repository and requested commit exist.";
  return "Retry later or inspect GitHub API availability.";
}

function azureAction(error) {
  if (error?.code === "azdo_auth_required") return "Run az login or set AZURE_DEVOPS_EXT_PAT.";
  if (error?.code === "azdo_access_denied") return "Ensure the Azure DevOps credential can read dnceng/internal microsoft-aspire.";
  if (error?.code === "az_cli_missing") return "Install Azure CLI and the azure-devops extension.";
  if (error?.code === "azdo_timeout") return "Retry when Azure DevOps is responsive.";
  return "Check Azure CLI authentication and access to dnceng/internal microsoft-aspire.";
}

class GitHubHttpError extends Error {
  constructor(status, message) {
    super(message);
    this.name = "GitHubHttpError";
    this.status = status;
  }
}
