// Repository and delivery health model for the Aspire Team App canvas.
//
// GitHub health is derived from default-branch commit checks and associated pull
// request metadata. Azure DevOps health is normalized by azure-devops.mjs into the
// same provider-neutral card shape.

import { dayMs } from "./constants.mjs";
import {
  azurePipelineRemovalKey,
  discoverAzureDevOpsPipelines,
  loadAzureDevOpsPipelineHealth,
  unavailableAzureDevOpsHealth,
} from "./azure-devops.mjs";

const GRAPHQL = "https://api.github.com/graphql";
const HISTORY_PAGE_SIZE = 100;
const MAX_HISTORY_PAGES = 10;
const HISTORY_CACHE_LIMIT = 100;
const PARTIAL_MIN_MS = 250;
const historySearchCache = new Map();

const HEALTH_INITIAL_QUERY = `
query RepositoryHealth($owner:String!, $name:String!, $after:String) {
  repository(owner:$owner, name:$name) {
    nameWithOwner
    url
    defaultBranchRef {
      name
      head: target {
        ... on Commit {
          oid
          committedDate
          messageHeadline
          author { user { login } name }
          statusCheckRollup {
            state
            contexts(first:100) {
              pageInfo { hasNextPage endCursor }
              nodes {
                __typename
                ... on CheckRun { name status conclusion detailsUrl startedAt completedAt }
                ... on StatusContext { context state targetUrl createdAt }
              }
            }
          }
          associatedPullRequests(first:5) {
            nodes {
              number
              url
              mergedAt
              author { login }
              autoMergeRequest { enabledAt enabledBy { login } mergeMethod }
            }
          }
        }
      }
      historyTarget: target {
        ... on Commit {
          history(first:${HISTORY_PAGE_SIZE}, after:$after) {
            pageInfo { hasNextPage endCursor }
            nodes { oid committedDate statusCheckRollup { state } }
          }
        }
      }
    }
  }
}`;

const HEALTH_HISTORY_QUERY = `
query RepositoryHealthHistory($owner:String!, $name:String!, $after:String!) {
  repository(owner:$owner, name:$name) {
    defaultBranchRef {
      target {
        ... on Commit {
          history(first:${HISTORY_PAGE_SIZE}, after:$after) {
            pageInfo { hasNextPage endCursor }
            nodes { oid committedDate statusCheckRollup { state } }
          }
        }
      }
    }
  }
}`;

const HEALTH_CONTEXTS_QUERY = `
query RepositoryHealthContexts($owner:String!, $name:String!, $oid:GitObjectID!, $after:String!) {
  repository(owner:$owner, name:$name) {
    object(oid:$oid) {
      ... on Commit {
        oid
        statusCheckRollup {
          contexts(first:100, after:$after) {
            pageInfo { hasNextPage endCursor }
            nodes {
              __typename
              ... on CheckRun { name status conclusion detailsUrl startedAt completedAt }
              ... on StatusContext { context state targetUrl createdAt }
            }
          }
        }
      }
    }
  }
}`;

export async function loadHealthDashboard({
  accounts,
  pipelines = [],
  onProgress,
  onPartial,
  now = new Date(),
  githubLoader = loadGitHubRepositoryHealth,
  azureLoader = loadAzureDevOpsPipelineHealth,
  azureDiscovery = discoverAzureDevOpsPipelines,
  historyCache = historySearchCache,
} = {}) {
  const usable = (accounts ?? []).filter((account) => account?.token && account?.login);
  const configuredPipelines = Array.isArray(pipelines) ? pipelines : [];
  const repositories = [...new Set(usable.flatMap((account) => account.repos ?? []))];
  const discoverableRepositories = [...new Set(usable
    .filter((account) => githubHost(account.graphql) === "github.com")
    .flatMap((account) => account.repos ?? []))];
  const itemById = new Map();
  const successfulGitHubSources = new Set();
  const knownPipelineIds = new Set(configuredPipelines.map((pipeline) => pipeline?.id).filter(Boolean));
  const errorsRaw = [];
  const jobs = [];
  let total = 0;
  let done = 0;
  let lastPartialAt = 0;

  const snapshot = (loading) => {
    const items = associateHealthSources([...itemById.values()].sort(compareHealthItems));
    const counts = healthCounts(items);
    const errors = [...new Set(errorsRaw
      .filter((error) => !error.sourceKey || !successfulGitHubSources.has(error.sourceKey))
      .map((error) => error.message))].sort();
    const configured = usable.length > 0 || configuredPipelines.length > 0;
    return {
      authenticated: configured,
      message: configured ? null : "No health sources are configured. Enable a GitHub account or add an Azure DevOps pipeline.",
      viewer: usable[0]?.login ?? null,
      viewers: usable.map((account) => account.login),
      mode: "health",
      loading,
      repos: repositories,
      lanes: [],
      attention: null,
      notifications: [],
      counts,
      health: { items, counts, loading },
      errors,
      fetchedAt: new Date(now).toISOString(),
    };
  };

  const maybePartial = (force) => {
    if (typeof onPartial !== "function") return;
    const current = Date.now();
    if (!force && current - lastPartialAt < PARTIAL_MIN_MS) return;
    lastPartialAt = current;
    try {
      onPartial(snapshot(true));
    } catch {
      // A renderer callback cannot be allowed to abort provider queries.
    }
  };
  const reportDone = () => {
    done++;
    if (typeof onProgress === "function") onProgress({ done, total, phase: "fetch" });
    maybePartial(false);
  };
  const loadAzureSource = async (pipeline) => {
    try {
      const item = await azureLoader(pipeline, { now });
      itemById.set(item.id, item);
    } catch (error) {
      const item = unavailableAzureDevOpsHealth(pipeline, error);
      itemById.set(item.id, item);
    }
  };

  for (const account of usable) {
    for (const repository of account.repos ?? []) {
      const sourceKey = githubSourceKey(account.graphql, repository);
      jobs.push(
        (async () => {
          const parts = splitRepository(repository);
          if (!parts) {
            errorsRaw.push({ sourceKey, message: `Invalid repo "${repository}"` });
            return;
          }
          try {
            const item = await githubLoader({
              token: account.token,
              repository,
              graphqlUrl: account.graphql,
              now,
              historyCache,
            });
            successfulGitHubSources.add(sourceKey);
            if (!itemById.has(item.id)) itemById.set(item.id, item);
          } catch (error) {
            errorsRaw.push({ sourceKey, message: `${repository}: ${error.message}` });
          }
        })().then(reportDone),
      );
    }
  }

  for (const pipeline of configuredPipelines) {
    jobs.push(loadAzureSource(pipeline).then(reportDone));
  }

  if (discoverableRepositories.length > 0) {
    jobs.push(
      (async () => {
        try {
          const discovery = await azureDiscovery(
            { repositories: discoverableRepositories },
            { nowMs: new Date(now).getTime() },
          );
          const discovered = Array.isArray(discovery) ? discovery : discovery?.pipelines;
          for (const warning of Array.isArray(discovery?.warnings) ? discovery.warnings : []) {
            errorsRaw.push({ message: `Azure DevOps discovery: ${warning}` });
          }
          const loads = [];
          for (const pipeline of Array.isArray(discovered) ? discovered : []) {
            if (!pipeline?.id || knownPipelineIds.has(pipeline.id)) continue;
            knownPipelineIds.add(pipeline.id);
            loads.push(loadAzureSource(pipeline));
          }
          await Promise.all(loads);
        } catch (error) {
          errorsRaw.push({ message: `Azure DevOps discovery: ${error.message}` });
        }
      })().then(reportDone),
    );
  }

  total = jobs.length;
  await Promise.all(jobs);
  if (typeof onProgress === "function") onProgress({ done: total, total, phase: "done" });
  maybePartial(true);
  return snapshot(false);
}

export async function loadGitHubRepositoryHealth({
  token,
  repository,
  graphqlUrl = GRAPHQL,
  now = new Date(),
  fetchImpl = globalThis.fetch,
  historyCache = historySearchCache,
} = {}) {
  const parts = splitRepository(repository);
  if (!token || !parts) {
    throw new Error(!token ? "GitHub token is required" : `Invalid repo "${repository}"`);
  }

  const initial = await gql(fetchImpl, token, HEALTH_INITIAL_QUERY, {
    owner: parts.owner,
    name: parts.name,
    after: null,
  }, graphqlUrl);
  const repo = initial.repository;
  if (!repo) throw new Error("Repository was not found or is not accessible");

  const branch = repo.defaultBranchRef;
  if (!branch?.head) {
    const host = githubHost(graphqlUrl, repo.url);
    return {
      id: githubHealthId(host, repo.nameWithOwner || repository),
      provider: "github",
      name: repo.nameWithOwner || repository,
      repository: repo.nameWithOwner || repository,
      mappedRepository: host === "github.com" ? (repo.nameWithOwner || repository) : null,
      canOpenRepoSession: host === "github.com",
      host,
      branch: branch?.name ?? null,
      url: repo.url ?? repositoryUrl(host, repository),
      state: "unknown",
      latest: null,
      lastSuccessAt: null,
      daysSinceSuccess: null,
      failureStreak: 0,
      failedChecks: [],
      reasons: [{ code: "no_default_branch", tone: "muted", summary: "No default-branch commit is available." }],
      evidence: [],
      historyExamined: 0,
      successSearchTruncated: false,
    };
  }

  const head = branch.head;
  const host = githubHost(graphqlUrl, repo.url);
  const name = repo.nameWithOwner || repository;
  const historyKey = githubHealthId(host, name);
  const history = [];
  let connection = branch.historyTarget?.history;
  appendHistory(history, connection?.nodes);
  const firstPageLength = history.length;
  const cachedHistory = historyCache?.get?.(historyKey);
  let successSearchTruncated = false;
  // Check the newest page on every poll because rollups can settle without a new head.
  // Older pages are immutable enough to reuse while the head SHA is unchanged, avoiding
  // up to nine repeated history queries for repositories with no recent success.
  if (cachedHistory?.headOid === head.oid) {
    if (!findSuccessfulCommit(history)) appendHistory(history, cachedHistory.tail);
    successSearchTruncated = !findSuccessfulCommit(history) && cachedHistory.successSearchTruncated;
  } else {
    let pages = 1;
    while (!findSuccessfulCommit(history) && connection?.pageInfo?.hasNextPage && pages < MAX_HISTORY_PAGES) {
      const next = await gql(fetchImpl, token, HEALTH_HISTORY_QUERY, {
        owner: parts.owner,
        name: parts.name,
        after: connection.pageInfo.endCursor,
      }, graphqlUrl);
      connection = next.repository?.defaultBranchRef?.target?.history;
      appendHistory(history, connection?.nodes);
      pages++;
    }
    successSearchTruncated = !findSuccessfulCommit(history) && !!connection?.pageInfo?.hasNextPage;
    setHistorySearchCache(historyCache, historyKey, {
      headOid: head.oid,
      tail: history.slice(firstPageLength),
      successSearchTruncated,
    });
  }

  const checks = normalizeChecks(await loadCheckContexts({
    fetchImpl,
    token,
    graphqlUrl,
    parts,
    head,
  }));
  const failedChecks = checks.filter((check) => check.state === "failing" || check.state === "degraded");
  const linkedPullRequest = selectAssociatedPullRequest(head.associatedPullRequests?.nodes);
  const lastSuccess = findSuccessfulCommit(history);
  const state = githubRollupState(head.statusCheckRollup?.state);
  const failureStreak = consecutiveFailures(history);
  const lastSuccessAt = lastSuccess?.committedDate ?? null;
  const reasons = githubReasons({
    state,
    failedChecks,
    failureStreak,
    linkedPullRequest,
    lastSuccessAt,
    historyExamined: history.length,
    successSearchTruncated,
    now,
  });
  const url = repo.url ?? repositoryUrl(host, repository);

  return {
    id: githubHealthId(host, name),
    provider: "github",
    name,
    repository: name,
    mappedRepository: host === "github.com" ? name : null,
    canOpenRepoSession: host === "github.com",
    host,
    branch: branch.name,
    url,
    state,
    latest: {
      id: head.oid,
      at: head.committedDate,
      status: head.statusCheckRollup?.state ?? null,
      url: `${url}/commit/${head.oid}`,
      actor: head.author?.user?.login ?? head.author?.name ?? null,
      message: head.messageHeadline ?? null,
    },
    lastSuccessAt,
    daysSinceSuccess: daysSince(lastSuccessAt, now),
    failureStreak,
    failedChecks,
    linkedPullRequest,
    reasons,
    evidence: githubEvidence(failedChecks, linkedPullRequest),
    historyExamined: history.length,
    successSearchTruncated,
  };
}

export function healthCounts(items) {
  const counts = { total: 0, healthy: 0, running: 0, degraded: 0, failing: 0, unavailable: 0, unknown: 0 };
  for (const item of items ?? []) {
    counts.total++;
    const key = Object.hasOwn(counts, item?.state) ? item.state : "unknown";
    counts[key]++;
  }
  return counts;
}

// Azure Repos does not expose an upstream GitHub origin for independently mirrored
// repositories. Group a configured pipeline with a watched GitHub repository only
// when provider metadata names it directly or a normalized name has exactly one
// candidate. Ambiguous names remain separate rather than implying a false relationship.
export function associateHealthSources(items) {
  const sources = (Array.isArray(items) ? items : []).map((item) => ({ ...item }));
  const githubGroups = [];

  for (const source of sources) {
    if (source.provider !== "github") continue;
    const repository = String(source.repository || source.name || "").trim();
    const parts = splitRepository(repository);
    if (!parts) continue;
    const host = String(source.host || "github.com").trim().toLowerCase() || "github.com";
    const group = {
      id: `repository:${host}/${repository.toLowerCase()}`,
      name: repository,
      host,
      fullKey: repositoryMatchKey(repository),
      shortKey: repositoryMatchKey(parts.name),
    };
    source.groupId = group.id;
    source.groupName = group.name;
    source.groupMatch = "canonical";
    githubGroups.push(group);
  }

  for (let index = 0; index < sources.length; index++) {
    const source = sources[index];
    if (source.provider === "github") continue;

    let match = null;
    let matchKind = null;
    const mappedRepository = String(source.mappedRepository || "").trim();
    if (splitRepository(mappedRepository)) {
      const directMatches = uniqueGroups(githubGroups.filter((group) =>
        group.host === "github.com" && group.name.toLowerCase() === mappedRepository.toLowerCase()));
      if (directMatches.length === 1) {
        match = directMatches[0];
        matchKind = "provider";
      } else if (directMatches.length === 0) {
        match = {
          id: `repository:github.com/${mappedRepository.toLowerCase()}`,
          name: mappedRepository,
        };
        matchKind = "provider";
      }
    }

    if (!match) {
      const names = [source.repository?.name, source.name]
        .map(repositoryMatchKey)
        .filter(Boolean);
      const fullMatches = uniqueGroups(githubGroups.filter((group) => names.includes(group.fullKey)));
      if (fullMatches.length === 1) {
        match = fullMatches[0];
        matchKind = "name";
      } else if (fullMatches.length === 0) {
        const shortMatches = uniqueGroups(githubGroups.filter((group) => names.includes(group.shortKey)));
        if (shortMatches.length === 1) {
          match = shortMatches[0];
          matchKind = "name";
        }
      }
    }

    source.groupId = match?.id || `source:${String(source.id || index)}`;
    source.groupName = match?.name || String(source.repository?.name || source.name || "Health source");
    source.groupMatch = matchKind;
  }

  return sources;
}

export function healthSummaryForAgent(dashboard) {
  const health = dashboard?.health;
  if (!health || !Array.isArray(health.items)) return null;
  const countKeys = ["total", "healthy", "running", "degraded", "failing", "unavailable", "unknown"];
  const counts = Object.fromEntries(countKeys.map((key) => [key, nonnegativeInteger(health.counts?.[key])]));
  const items = health.items.map((item) => {
    const provider = item?.provider === "azure-devops" ? "azure-devops" : "github";
    const states = new Set(["healthy", "running", "degraded", "failing", "unavailable", "unknown"]);
    const summary = {
      provider,
      state: states.has(item?.state) ? item.state : "unknown",
      daysSinceSuccess: nullableNonnegativeInteger(item?.daysSinceSuccess),
      failureStreak: nonnegativeInteger(item?.failureStreak),
      reasonCodes: (Array.isArray(item?.reasons) ? item.reasons : [])
        .map((reason) => String(reason?.code ?? ""))
        .filter((code) => /^[a-z0-9_]{1,64}$/.test(code)),
    };
    if (provider === "github" && /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(String(item?.repository ?? ""))) {
      summary.repository = item.repository;
    } else if (provider === "azure-devops") {
      summary.definitionId = positiveInteger(item?.definitionId);
      const removalKey = item?.discovered ? null : azurePipelineRemovalKey(item?.id);
      if (removalKey) summary.removalKey = removalKey;
    }
    return summary;
  });
  return { loading: !!dashboard.loading, counts, items };
}

export function azurePipelineReferencesForAgent(pipelines) {
  return (Array.isArray(pipelines) ? pipelines : []).flatMap((pipeline) => {
    const definitionId = positiveInteger(pipeline?.definitionId);
    const removalKey = azurePipelineRemovalKey(pipeline?.id);
    if (!definitionId || !removalKey) return [];
    return [{
      organization: pipeline?.organizationName,
      definitionId,
      removalKey,
    }];
  });
}

export function githubReasons({
  state,
  failedChecks = [],
  failureStreak = 0,
  linkedPullRequest,
  lastSuccessAt,
  historyExamined = 0,
  successSearchTruncated = false,
  now = new Date(),
}) {
  const reasons = [];
  const author = String(linkedPullRequest?.author ?? "");
  if (state === "failing" && linkedPullRequest?.autoMerge?.enabledAt && /^dependabot(?:\[bot\])?$/i.test(author)) {
    reasons.push({
      code: "dependabot_auto_merge",
      tone: "danger",
      summary: `The failing head is auto-merged Dependabot PR #${linkedPullRequest.number}; it is a likely regression source.`,
      url: linkedPullRequest.url,
    });
  }

  if (state === "failing" && failedChecks.length) {
    const names = failedChecks.slice(0, 3).map((check) => check.name);
    const suffix = failedChecks.length > names.length ? ` and ${failedChecks.length - names.length} more` : "";
    reasons.push({
      code: "failing_checks",
      tone: "danger",
      summary: `${failedChecks.length} check${failedChecks.length === 1 ? "" : "s"} failing: ${names.join(", ")}${suffix}.`,
    });
  } else if (state === "failing") {
    reasons.push({ code: "default_branch_failing", tone: "danger", summary: "The default branch check rollup is failing." });
  } else if (state === "running") {
    reasons.push({ code: "checks_running", tone: "warning", summary: "Default-branch validation is still running or expected." });
  } else if (state === "unknown") {
    reasons.push({ code: "checks_unknown", tone: "muted", summary: "No default-branch CI rollup is available." });
  }

  if (failureStreak > 1) {
    reasons.push({
      code: "commit_failure_streak",
      tone: "danger",
      summary: `${failureStreak} consecutive default-branch commits have failing validation.`,
    });
  }
  if (state !== "healthy" && lastSuccessAt) {
    const days = daysSince(lastSuccessAt, now);
    reasons.push({
      code: "last_success_age",
      tone: "warning",
      summary: `Last successful default-branch validation was ${days} day${days === 1 ? "" : "s"} ago.`,
    });
  } else if (state !== "healthy" && historyExamined > 0 && !lastSuccessAt) {
    reasons.push({
      code: "no_success_found",
      tone: "warning",
      summary: successSearchTruncated
        ? `No successful validation was found in the first ${historyExamined} default-branch commits.`
        : `No successful validation was found across ${historyExamined} default-branch commit${historyExamined === 1 ? "" : "s"}.`,
    });
  }
  return reasons;
}

function normalizeChecks(nodes) {
  const checks = [];
  for (const node of Array.isArray(nodes) ? nodes : []) {
    if (node?.__typename === "CheckRun") {
      const status = String(node.status ?? "").toUpperCase();
      const conclusion = String(node.conclusion ?? "").toUpperCase();
      let state;
      if (status && status !== "COMPLETED") state = "running";
      else if (["SUCCESS", "NEUTRAL", "SKIPPED"].includes(conclusion)) state = "healthy";
      else if (["CANCELLED"].includes(conclusion)) state = "degraded";
      else if (["FAILURE", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE", "STALE"].includes(conclusion)) state = "failing";
      else state = "unknown";
      checks.push({
        name: String(node.name || "Unnamed check"),
        state,
        status: node.status ?? null,
        conclusion: node.conclusion ?? null,
        url: node.detailsUrl ?? null,
        startedAt: node.startedAt ?? null,
        completedAt: node.completedAt ?? null,
      });
    } else if (node?.__typename === "StatusContext") {
      const state = githubRollupState(node.state);
      checks.push({
        name: String(node.context || "Unnamed status"),
        state,
        status: node.state ?? null,
        conclusion: node.state ?? null,
        url: node.targetUrl ?? null,
        startedAt: node.createdAt ?? null,
        completedAt: node.createdAt ?? null,
      });
    }
  }
  return checks;
}

function githubRollupState(value) {
  switch (String(value ?? "").toUpperCase()) {
    case "SUCCESS": return "healthy";
    case "FAILURE":
    case "ERROR": return "failing";
    case "PENDING":
    case "EXPECTED": return "running";
    default: return "unknown";
  }
}

function selectAssociatedPullRequest(nodes) {
  const candidates = (Array.isArray(nodes) ? nodes : []).filter((pr) => pr?.number && pr?.url);
  const selected = candidates.find((pr) => pr.mergedAt) ?? candidates[0];
  if (!selected) return null;
  return {
    number: selected.number,
    url: selected.url,
    mergedAt: selected.mergedAt ?? null,
    author: selected.author?.login ?? null,
    autoMerge: selected.autoMergeRequest ? {
      enabledAt: selected.autoMergeRequest.enabledAt ?? null,
      enabledBy: selected.autoMergeRequest.enabledBy?.login ?? null,
      mergeMethod: selected.autoMergeRequest.mergeMethod ?? null,
    } : null,
  };
}

function githubEvidence(failedChecks, linkedPullRequest) {
  const evidence = failedChecks.slice(0, 6).map((check) => ({
    label: check.name,
    detail: check.conclusion || check.status || "Failure",
    url: check.url,
  }));
  if (linkedPullRequest?.autoMerge?.enabledAt) {
    evidence.unshift({
      label: `Auto-merged PR #${linkedPullRequest.number}`,
      detail: [linkedPullRequest.author, linkedPullRequest.autoMerge.enabledBy].filter(Boolean).join(" via "),
      url: linkedPullRequest.url,
    });
  }
  return evidence;
}

function appendHistory(target, nodes) {
  for (const node of Array.isArray(nodes) ? nodes : []) {
    if (node?.oid) target.push(node);
  }
}

async function loadCheckContexts({ fetchImpl, token, graphqlUrl, parts, head }) {
  const nodes = [...(head.statusCheckRollup?.contexts?.nodes ?? [])];
  let connection = head.statusCheckRollup?.contexts;
  const seenCursors = new Set();
  while (connection?.pageInfo?.hasNextPage) {
    const cursor = connection.pageInfo.endCursor;
    if (!cursor || seenCursors.has(cursor)) {
      throw new Error("GitHub check context pagination returned an invalid cursor");
    }
    seenCursors.add(cursor);
    // Pin pagination to the original commit so a default-branch update between requests
    // cannot combine check contexts from two different heads.
    const next = await gql(fetchImpl, token, HEALTH_CONTEXTS_QUERY, {
      owner: parts.owner,
      name: parts.name,
      oid: head.oid,
      after: cursor,
    }, graphqlUrl);
    const commit = next.repository?.object;
    const nextConnection = commit?.statusCheckRollup?.contexts;
    if (commit?.oid !== head.oid || !nextConnection) {
      throw new Error("GitHub check context pagination returned an unexpected commit");
    }
    nodes.push(...(nextConnection.nodes ?? []));
    connection = nextConnection;
  }
  return nodes;
}

function setHistorySearchCache(cache, key, value) {
  if (!cache?.set) return;
  cache.delete?.(key);
  cache.set(key, value);
  while (cache.size > HISTORY_CACHE_LIMIT) {
    const oldest = cache.keys().next().value;
    cache.delete(oldest);
  }
}

function findSuccessfulCommit(history) {
  return history.find((commit) => String(commit?.statusCheckRollup?.state).toUpperCase() === "SUCCESS") ?? null;
}

function consecutiveFailures(history) {
  let count = 0;
  for (const commit of history) {
    const state = String(commit?.statusCheckRollup?.state).toUpperCase();
    if (state !== "FAILURE" && state !== "ERROR") break;
    count++;
  }
  return count;
}

async function gql(fetchImpl, token, query, variables, graphqlUrl) {
  const response = await fetchImpl(graphqlUrl || GRAPHQL, {
    method: "POST",
    headers: {
      Authorization: `bearer ${token}`,
      "Content-Type": "application/json",
      "User-Agent": "aspire-team-app-canvas",
    },
    body: JSON.stringify({ query, variables }),
  });
  if (!response.ok) throw new Error(`GitHub API ${response.status} ${response.statusText}`);
  const json = await response.json();
  if (json.errors?.length) throw new Error(json.errors.map((error) => error.message).join("; "));
  return json.data;
}

function splitRepository(repository) {
  const match = /^([^/\s]+)\/([^/\s]+)$/.exec(String(repository ?? "").trim());
  return match ? { owner: match[1], name: match[2] } : null;
}

function repositoryMatchKey(value) {
  return String(value ?? "")
    .trim()
    .replace(/\.git$/i, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "");
}

function uniqueGroups(groups) {
  return [...new Map(groups.map((group) => [group.id, group])).values()];
}

function githubSourceKey(graphqlUrl, repository) {
  return `${githubHost(graphqlUrl)}\n${String(repository).toLowerCase()}`;
}

function githubHealthId(host, repository) {
  return `github:${String(host).toLowerCase()}/${String(repository).toLowerCase()}`;
}

function githubHost(graphqlUrl, repositoryUrl) {
  try {
    if (repositoryUrl) return new URL(repositoryUrl).hostname.toLowerCase();
    if (!graphqlUrl || new URL(graphqlUrl).hostname.toLowerCase() === "api.github.com") return "github.com";
    return new URL(graphqlUrl).hostname.toLowerCase();
  } catch {
    return "github.com";
  }
}

function repositoryUrl(host, repository) {
  return `https://${host}/${repository}`;
}

function daysSince(value, now) {
  if (!value) return null;
  const elapsed = new Date(now).getTime() - new Date(value).getTime();
  return Number.isFinite(elapsed) ? Math.max(0, Math.floor(elapsed / dayMs)) : null;
}

function nonnegativeInteger(value) {
  const number = Number(value);
  return Number.isInteger(number) && number >= 0 ? number : 0;
}

function nullableNonnegativeInteger(value) {
  if (value === null || value === undefined) return null;
  return nonnegativeInteger(value);
}

function positiveInteger(value) {
  const number = Number(value);
  return Number.isInteger(number) && number > 0 ? number : null;
}

function compareHealthItems(a, b) {
  const rank = { failing: 0, degraded: 1, running: 2, unavailable: 3, unknown: 4, healthy: 5 };
  return (rank[a?.state] ?? rank.unknown) - (rank[b?.state] ?? rank.unknown)
    || String(a?.name ?? "").localeCompare(String(b?.name ?? ""));
}
