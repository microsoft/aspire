// GitHub data layer for the Aspire Team App canvas.
//
// Resolves the logged-in user's token, queries open PRs/issues across the watched
// repos via the GraphQL API, then buckets them into review lanes and computes
// signal pills. This is a faithful, self-contained port of the lane + signal logic
// from davidfowl/pr-dashboard (frontend/src/utils/{models,signals,pullRequests}.ts).

import {
  actorIdentityKey,
  createAttentionBuckets,
  createForMeItems,
  createDeveloperPullRequestCounts,
  createAttentionSignals,
  createFocusIssueBuckets,
  createIssueSignals,
  computeFocusItems,
  computeFocusExclusionItems,
  computeCommunityItems,
  isChecksFailing,
  isReviewDebt,
  isUrgentPullRequest,
  reviewAgeStartedAt,
  shouldHideFromSharedPullRequestLists,
  visibleCheckState,
} from "./model.mjs";
import { currentRelease } from "./constants.mjs";
import { throwGitHubResponseError, withGitHubTimeout } from "./github-request.mjs";

// The current milestone has a single source of truth in constants.mjs
// (`currentRelease`). Re-export it here under the name the run-mode lane logic
// (`bucketShip`) and the default `release` preference (via state.mjs) already
// consume, so bumping the milestone in one place keeps release detection and the
// default milestone from silently desyncing.
export const CURRENT_RELEASE = currentRelease;

export const DEFAULT_REPOS = [
  "microsoft/aspire",
  "microsoft/aspire.dev",
  "microsoft/aspire-skills",
  "microsoft/dcp",
  "CommunityToolkit/Aspire",
];

// The Proxima enterprise account has its own host and repository location. Keep this
// separate from the public defaults because account-specific GraphQL requests target
// the account's host.
export const DEFAULT_PROXIMA_REPOS = [
  "coreai/aspire-1p",
  "coreai/sisyphus",
];

const GRAPHQL = "https://api.github.com/graphql";

// ---------------------------------------------------------------------------
// GraphQL helper.
// ---------------------------------------------------------------------------

async function gql(token, query, variables, graphqlUrl = GRAPHQL, { allowPartialData = false } = {}) {
  return withGitHubTimeout(async (signal) => {
    const res = await fetch(graphqlUrl, {
      method: "POST",
      headers: {
        Authorization: `bearer ${token}`,
        "Content-Type": "application/json",
        "User-Agent": "aspire-team-app-canvas",
      },
      body: JSON.stringify({ query, variables }),
      signal,
    });
    if (!res.ok) {
      await throwGitHubResponseError(res, signal);
    }
    const json = await res.json();
    if (allowPartialData) {
      return { data: json.data, errors: json.errors ?? [] };
    }
    if (json.errors?.length) throw new Error(json.errors.map((e) => e.message).join("; "));
    return json.data;
  });
}

// ---------------------------------------------------------------------------
// Queries
// ---------------------------------------------------------------------------

// GitHub's GraphQL connections cap `first` at 100 and page the remainder behind a
// cursor. High-volume repos (e.g. microsoft/aspire) have far more than one page of
// open PRs/issues, so a single `first:40` fetch silently truncates the queue. We
// follow `pageInfo.endCursor` until GitHub reports no more pages. MAX_PAGES is an
// explicit safety bound (25 pages * 40 = up to 1000 open items per repo per account)
// so a pathological repo can't spin the loader indefinitely.
const MAX_PAGES = 25;
const PAGE_SIZE = 40;
const PARTIAL_MIN_MS = 250;

// Each detailed PR page costs GitHub several seconds to resolve (reviews, threads, checks,
// timelines, closing issues), and microsoft/aspire has hundreds of open PRs, so walking those
// pages one cursor at a time took over a minute per Review/Ship load. When the first page reports
// more items, a cheap cursor-only walk finds every later page boundary and the detailed pages
// load a few at a time instead of serially.
const PAGE_CONCURRENCY = 4;
const CURSOR_PAGE_SIZE = 100;

const pageCursorQuery = (connection) => `
query($owner:String!, $name:String!, $after:String) {
  repository(owner:$owner, name:$name) {
    ${connection}(states:OPEN, first:${CURSOR_PAGE_SIZE}, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
      pageInfo { hasNextPage endCursor }
      edges { cursor }
    }
  }
}`;

const READINESS_TRANSITIONS = `
  pageInfo { hasNextPage endCursor }
  nodes {
    __typename
    ... on ConvertToDraftEvent { createdAt }
    ... on ReadyForReviewEvent { createdAt }
  }
`;

const PR_QUERY = `
query($owner:String!, $name:String!, $after:String, $includeReadiness:Boolean!) {
  repository(owner:$owner, name:$name) {
    isPrivate
    pullRequests(states:OPEN, first:${PAGE_SIZE}, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
      totalCount
      pageInfo { hasNextPage endCursor }
      nodes {
        number title url isDraft state createdAt updatedAt
        author { __typename login avatarUrl }
        baseRefName
        mergeable
        reviewDecision
        readyForReviewEvents: timelineItems(last:1, itemTypes:[READY_FOR_REVIEW_EVENT]) {
          nodes { ... on ReadyForReviewEvent { createdAt } }
        }
        readinessTransitions: timelineItems(first:20, itemTypes:[CONVERT_TO_DRAFT_EVENT, READY_FOR_REVIEW_EVENT]) @include(if:$includeReadiness) {
          ${READINESS_TRANSITIONS}
        }
        additions deletions changedFiles
        milestone { title }
        labels(first:15) { pageInfo { hasNextPage endCursor } nodes { name } }
        assignees(first:10) { nodes { login } }
        reviewRequests(first:20) { nodes { requestedReviewer { __typename ... on User { login } } } }
        reviews(first:60) { nodes { state author { login } submittedAt } }
        reviewThreads(first:60) { nodes { isResolved } }
        commits(last:1) { totalCount nodes { commit { committedDate statusCheckRollup { state } } } }
        closingIssuesReferences(first:10) {
          nodes {
            number title url
            repository { nameWithOwner }
            milestone { title }
            labels(first:15) { nodes { name } }
          }
        }
      }
    }
  }
}`;

const READINESS_QUERY = `
query($owner:String!, $name:String!, $number:Int!, $after:String!) {
  repository(owner:$owner, name:$name) {
    pullRequest(number:$number) {
      readinessTransitions: timelineItems(first:100, after:$after, itemTypes:[CONVERT_TO_DRAFT_EVENT, READY_FOR_REVIEW_EVENT]) {
        ${READINESS_TRANSITIONS}
      }
    }
  }
}`;

const PR_LABELS_QUERY = `
query($owner:String!, $name:String!, $number:Int!, $after:String!) {
  repository(owner:$owner, name:$name) {
    pullRequest(number:$number) {
      labels(first:100, after:$after) {
        pageInfo { hasNextPage endCursor }
        nodes { name }
      }
    }
  }
}`;

const ISSUE_QUERY = `
query($owner:String!, $name:String!, $after:String) {
  repository(owner:$owner, name:$name) {
    issues(states:OPEN, first:${PAGE_SIZE}, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
      totalCount
      pageInfo { hasNextPage endCursor }
      nodes {
        number title url createdAt updatedAt
        author { __typename login avatarUrl }
        milestone { title }
        labels(first:15) { nodes { name } }
        assignees(first:10) { nodes { login } }
        closedByPullRequestsReferences(first:5, includeClosedPrs:true) {
          nodes { number title url state repository { nameWithOwner } }
        }
      }
    }
  }
}`;

function splitRepo(repo) {
  const [owner, ...rest] = repo.split("/");
  return { owner, name: rest.join("/") };
}

function getVerifiedLabelNodes(connection) {
  return Array.isArray(connection?.nodes)
    ? connection.nodes
      .filter((label) => typeof label?.name === "string" && label.name)
      .map((label) => ({ name: label.name }))
    : [];
}

function getErrorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

function mergeErrors(...messages) {
  const errors = [...new Set(messages.filter(Boolean))];
  return errors.length ? errors.join("; ") : null;
}

function mergePullRequestSnapshots(existing, incoming, mode) {
  const completeness = (pr) => Number(!pr.labelsUnavailable) + Number(!pr.firstReadinessError);
  const completeLabels = !existing.labelsUnavailable ? existing : !incoming.labelsUnavailable ? incoming : null;
  const completeReadiness = !existing.firstReadinessError ? existing : !incoming.firstReadinessError ? incoming : null;
  const preferredSnapshot = mode === "review" && completeReadiness
    ? completeReadiness
    : completeness(incoming) > completeness(existing) ? incoming : existing;
  const labels = completeLabels
    ? completeLabels.labels
    : [...new Set([...existing.labels, ...incoming.labels])];

  return {
    ...preferredSnapshot,
    labels,
    labelsUnavailable: completeLabels === null,
    labelsError: completeLabels ? null : mergeErrors(existing.labelsError, incoming.labelsError),
    firstReadyForReviewAt: completeReadiness?.firstReadyForReviewAt ?? null,
    firstReadinessError: completeReadiness ? null : mergeErrors(existing.firstReadinessError, incoming.firstReadinessError),
  };
}

// GraphQL field errors can preserve sibling data. Only tolerate paths scoped to one PR's
// labels, e.g. ["repository", "pullRequests", "nodes", 0, "labels", "pageInfo"].
function getPullRequestLabelErrors(errors, nodes) {
  const errorsByNode = new Map();
  for (const error of errors) {
    const path = error?.path;
    const index = path?.[3];
    if (!Array.isArray(path) || path[0] !== "repository" || path[1] !== "pullRequests" ||
        path[2] !== "nodes" || !Number.isInteger(index) || index < 0 || index >= nodes.length ||
        path[4] !== "labels") {
      throw new Error(errors.map((item) => item?.message ?? "Unknown GraphQL error").join("; "));
    }
    const message = error.message ?? "Unknown GraphQL error";
    errorsByNode.set(index, mergeErrors(errorsByNode.get(index), message));
  }
  return errorsByNode;
}

// Loads every open page of a repository connection. `loadPage(after)` fetches and records one
// detailed page and returns its connection ({ totalCount, pageInfo }).
async function loadOpenPages(account, owner, name, connection, loadPage) {
  const first = await loadPage(null);
  if (!first?.pageInfo?.hasNextPage) return;
  let starts = null;
  if (Number.isInteger(first.totalCount) && first.totalCount > PAGE_SIZE * 2) {
    try {
      starts = await loadPageStarts(account, owner, name, connection, first.pageInfo.endCursor);
    } catch (error) {
      // Do not turn a rate-limit or permission response into an immediate serial retry.
      if (error?.status === 403 || error?.status === 429) throw error;
      // Other cursor-walk failures can fall back to the serial walk.
      starts = null;
    }
  }
  if (!starts?.length) {
    await loadSerialPages(loadPage, first, 1);
    return;
  }

  // Middle pages are bounded by the next page's start cursor, so only the last page follows
  // `hasNextPage` to discover a growing tail after cursor discovery.
  const results = [];
  let next = 0;
  let failure = null;
  const worker = async () => {
    while (next < starts.length && !failure) {
      const index = next++;
      try {
        const page = await loadPage(starts[index]);
        if (index === starts.length - 1) await loadSerialPages(loadPage, page, starts.length + 1, () => !failure);
      } catch (error) {
        failure ??= error;
        throw error;
      }
    }
  };
  for (let i = 0; i < Math.min(PAGE_CONCURRENCY, starts.length); i++) results.push(worker());
  // Wait for every in-flight page before reporting a failure so no request keeps mutating the
  // dashboard maps after this repository has settled.
  const settled = await Promise.allSettled(results);
  const rejected = settled.find((result) => result.status === "rejected");
  if (rejected) throw rejected.reason;
}

async function loadSerialPages(loadPage, page, pagesLoaded, shouldContinue = () => true) {
  for (let loaded = pagesLoaded; loaded < MAX_PAGES && page?.pageInfo?.hasNextPage && shouldContinue(); loaded++) {
    page = await loadPage(page.pageInfo.endCursor);
  }
}

// Returns the `after` cursor for each detailed page following the first one, capped by MAX_PAGES.
async function loadPageStarts(account, owner, name, connection, firstEndCursor) {
  if (typeof firstEndCursor !== "string" || !firstEndCursor) return null;
  const starts = [firstEndCursor];
  const query = pageCursorQuery(connection);
  let after = firstEndCursor;
  let sincePageStart = 0;
  while (starts.length < MAX_PAGES - 1) {
    const data = await gql(account.token, query, { owner, name, after }, account.graphql);
    const conn = data.repository?.[connection];
    const edges = conn?.edges;
    if (!Array.isArray(edges) || typeof conn.pageInfo?.hasNextPage !== "boolean") return null;
    for (const [index, edge] of edges.entries()) {
      if (typeof edge?.cursor !== "string" || !edge.cursor) return null;
      sincePageStart++;
      const isLast = !conn.pageInfo.hasNextPage && index === edges.length - 1;
      if (sincePageStart === PAGE_SIZE && !isLast && starts.length < MAX_PAGES - 1) {
        starts.push(edge.cursor);
        sincePageStart = 0;
      }
    }
    if (!conn.pageInfo.hasNextPage) break;
    after = conn.pageInfo.endCursor;
  }
  return starts;
}

async function loadPullRequestLabels(account, owner, name, node) {
  let connection = node.labels;
  const labels = [];
  const cursors = new Set();
  for (let page = 0; ; page++) {
    if (!Array.isArray(connection?.nodes) || typeof connection.pageInfo?.hasNextPage !== "boolean" ||
        connection.nodes.some((label) => typeof label?.name !== "string" || !label.name)) {
      return {
        labels: { nodes: [...labels, ...getVerifiedLabelNodes(connection)] },
        error: `Missing or malformed pull-request labels for #${node.number}`,
      };
    }
    labels.push(...connection.nodes);
    if (!connection.pageInfo.hasNextPage) return { labels: { nodes: labels }, error: null };
    const after = connection.pageInfo.endCursor;
    if (typeof after !== "string" || !after || cursors.has(after) || page + 1 >= MAX_PAGES) {
      return { labels: { nodes: labels }, error: `Incomplete pull-request labels for #${node.number}` };
    }
    cursors.add(after);
    let response;
    try {
      response = await gql(
        account.token,
        PR_LABELS_QUERY,
        { owner, name, number: node.number, after },
        account.graphql,
        { allowPartialData: true },
      );
    } catch (error) {
      return { labels: { nodes: labels }, error: getErrorMessage(error) };
    }
    connection = response.data?.repository?.pullRequest?.labels;
    if (response.errors.length) {
      labels.push(...getVerifiedLabelNodes(connection));
      return {
        labels: { nodes: labels },
        error: response.errors.map((error) => error?.message ?? "Unknown GraphQL error").join("; "),
      };
    }
  }
}

async function loadFirstReadyForReviewAt(account, owner, name, node) {
  let connection = node.readinessTransitions;
  const transitions = [];
  const cursors = new Set();
  for (let page = 0; ; page++) {
    if (!Array.isArray(connection?.nodes) || typeof connection.pageInfo?.hasNextPage !== "boolean") {
      throw new Error("Missing draft/ready transition history");
    }
    transitions.push(...connection.nodes);
    if (!connection.pageInfo.hasNextPage) break;
    const after = connection.pageInfo.endCursor;
    if (!after || cursors.has(after) || page + 1 >= MAX_PAGES) {
      throw new Error("Incomplete draft/ready transition history");
    }
    cursors.add(after);
    const data = await gql(account.token, READINESS_QUERY, { owner, name, number: node.number, after }, account.graphql);
    connection = data.repository?.pullRequest?.readinessTransitions;
  }

  // The filtered timeline is chronological. Ready -> Draft -> Ready means the PR
  // opened as a draft; Draft -> Ready means it opened ready, so creation wins.
  // A later draft cycle must never reset its first-readiness priority.
  // https://docs.github.com/en/graphql/reference/pulls#object-readyforreviewevent
  let draft = transitions.length ? transitions[0]?.__typename === "ReadyForReviewEvent" : node.isDraft;
  let firstReadyAt = draft ? null : node.createdAt;
  let previousTime = Date.parse(node.createdAt);
  if (!Number.isFinite(previousTime) || typeof node.isDraft !== "boolean") {
    throw new Error("Invalid PR creation or draft state");
  }
  for (const transition of transitions) {
    const time = Date.parse(transition?.createdAt);
    const type = transition?.__typename;
    if (!Number.isFinite(time) || time < previousTime ||
        (type !== "ReadyForReviewEvent" && type !== "ConvertToDraftEvent") ||
        (type === "ReadyForReviewEvent") !== draft) {
      throw new Error("Inconsistent draft/ready transition history");
    }
    draft = type === "ConvertToDraftEvent";
    if (!draft && firstReadyAt === null) firstReadyAt = transition.createdAt;
    previousTime = time;
  }
  // A transition during pagination can disagree with the original PR snapshot.
  // Retry on refresh instead of assigning a guessed priority to an incomplete view.
  if (draft !== node.isDraft) {
    throw new Error("Draft state changed while loading transition history; refresh to retry");
  }
  return firstReadyAt;
}

// ---------------------------------------------------------------------------
// Normalization
// ---------------------------------------------------------------------------

const DAY_MS = 1000 * 60 * 60 * 24;

function mapCheckState(rollup) {
  const state = rollup?.state;
  if (state === "SUCCESS") return "success";
  if (state === "FAILURE" || state === "ERROR") return "failure";
  if (state === "PENDING" || state === "EXPECTED") return "pending";
  return "none";
}

// Build the engine-shaped ChecksStatus from the list-level rollup. The PR-list GraphQL query
// deliberately carries only `statusCheckRollup { state }` (mirroring pr-dashboard's lean list
// query, which enriches per-check detail lazily server-side). We therefore report zero per-check
// detail: the non-blocking-check helpers in model.mjs treat this indeterminate shape correctly
// (an aggregate failure on a rule-covered repo reads as "unknown" rather than red).
function mapChecks(rollup) {
  return {
    state: mapCheckState(rollup),
    totalCount: 0,
    successCount: 0,
    failureCount: 0,
    pendingCount: 0,
    neutralCount: 0,
    skippedCount: 0,
    completedAt: null,
    failingChecks: [],
  };
}

function mapMergeable(m) {
  if (m === "MERGEABLE") return "clean";
  if (m === "CONFLICTING") return "dirty";
  return "unknown";
}

// Reviewer-actor classification (mirrors GitHubClient.IsBotActor / IsCopilotReviewer).
const BOT_REVIEWERS = new Set(["dependabot", "dependabot-preview", "github-actions", "renovate"]);
function isBotReviewer(login) {
  const n = String(login || "").toLowerCase();
  return n.endsWith("[bot]") || n === "copilot" || BOT_REVIEWERS.has(n);
}
function isCopilotReviewer(login) {
  const n = String(login || "").toLowerCase();
  return n === "copilot-pull-request-reviewer" || n === "copilot-pull-request-reviewer[bot]";
}
function isCopilotLogin(login) {
  const n = String(login || "").toLowerCase();
  return n === "copilot" || n === "copilot-swe-agent" || n === "copilot-swe-agent[bot]" || n === "copilot[bot]" || n === "github-copilot[bot]" || n.endsWith("/copilot");
}

// Copilot attribution (mirrors GitHubModels.ResolveAuthor): a Copilot-authored PR with
// exactly one human assignee is attributed to "{human}/copilot" so it classifies as the
// human's work rather than a bot.
function resolveAuthor(login, assignees) {
  if (!isCopilotLogin(login)) return login;
  const humans = assignees.filter((a) => a && !/\[bot\]$/i.test(a) && !isCopilotLogin(a));
  return humans.length === 1 ? `${humans[0]}/copilot` : login;
}

// Server-parity review summary (mirrors GitHubClient.CreateReviewStatusFromGraphQlAsync).
// `rawUnresolved` is the count of unresolved review threads before policy gating.
const RESOLUTION_REQUIRED_REPOS = new Set(["microsoft/aspire"]);
function deriveReview(reviews, requestedReviewers, viewers, repo, rawUnresolved) {
  const human = reviews
    .filter((r) => r.author?.login && !isBotReviewer(r.author.login) && r.submittedAt)
    .sort((a, b) => new Date(a.submittedAt) - new Date(b.submittedAt));
  const copilotReviewed = reviews.some((r) => isCopilotReviewer(r.author?.login));

  // Latest review per human reviewer decides the headline state.
  const latestByReviewer = new Map();
  for (const r of human) latestByReviewer.set(r.author.login.toLowerCase(), r);
  let latestChanges = 0, latestApprovals = 0, latestCommented = 0;
  for (const r of latestByReviewer.values()) {
    if (r.state === "CHANGES_REQUESTED") latestChanges++;
    else if (r.state === "APPROVED") latestApprovals++;
    else if (r.state === "COMMENTED") latestCommented++;
  }
  let state = "waiting";
  if (latestChanges > 0) state = "changes_requested";
  else if (latestApprovals > 0) state = "approved";
  else if (latestCommented > 0) state = "reviewed";

  // Aggregate counts span every human review, not just the latest per reviewer.
  let approvalCount = 0, changesRequestedCount = 0, commentedReviewCount = 0;
  let lastApprovedAt = null, lastReviewedAt = null;
  for (const r of human) {
    if (r.state === "APPROVED") { approvalCount++; lastApprovedAt = r.submittedAt; }
    else if (r.state === "CHANGES_REQUESTED") changesRequestedCount++;
    else if (r.state === "COMMENTED") commentedReviewCount++;
    lastReviewedAt = r.submittedAt;
  }
  const reviewerCount = latestByReviewer.size;

  // Whether one of the logged-in accounts is a current approver (their latest review is
  // an approval). Used to nag approvers, not just the author, when a PR is ready to merge.
  const viewerApproved = [...latestByReviewer.values()]
    .some((r) => r.state === "APPROVED" && viewers.has(r.author.login.toLowerCase()));

  // Unresolved threads only count once a human (or Copilot, pre-human) has weighed in.
  let unresolvedThreadCount = 0;
  if (state === "approved" || state === "reviewed" || (state === "waiting" && copilotReviewed)) {
    unresolvedThreadCount = rawUnresolved;
  }

  return {
    state,
    approvalCount,
    changesRequestedCount,
    commentedReviewCount,
    reviewerCount,
    lastApprovedAt,
    lastReviewedAt,
    copilotReviewed,
    unresolvedThreadCount,
    requiresConversationResolution: RESOLUTION_REQUIRED_REPOS.has(String(repo).toLowerCase()),
    reviewRequestedFromViewer: requestedReviewers.some((rr) => viewers.has(String(rr).toLowerCase())),
    viewerApproved,
  };
}

function normalizePr(repo, node, viewers, repoPrivate = false) {
  const requestedReviewers = (node.reviewRequests?.nodes ?? [])
    .map((rr) => rr.requestedReviewer?.login ?? rr.requestedReviewer?.name)
    .filter(Boolean);
  const labels = (node.labels?.nodes ?? []).map((l) => l.name);
  const assignees = (node.assignees?.nodes ?? []).map((a) => a.login);
  const rawUnresolved = (node.reviewThreads?.nodes ?? []).filter((t) => !t.isResolved).length;
  const checks = mapChecks(node.commits?.nodes?.[0]?.commit?.statusCheckRollup);
  // Visible state honors non-blocking check rules: an aggregate failure driven only by
  // informational checks (e.g. aspire-1p proof-of-presence) reads as unknown/pending here,
  // so the simplified lane/ship/count paths don't treat it as red CI.
  const checksState = visibleCheckState({ repository: repo, checks }) ?? "none";
  const review = deriveReview(node.reviews?.nodes ?? [], requestedReviewers, viewers, repo, rawUnresolved);
  const mergeable = node.mergeable; // MERGEABLE | CONFLICTING | UNKNOWN
  const mergeableState = mapMergeable(mergeable);
  const author = resolveAuthor(node.author?.login ?? "ghost", assignees);
  const authorType = node.author?.__typename ?? null;
  const state = (node.state ?? "OPEN").toLowerCase();

  const commitNodes = node.commits?.nodes ?? [];
  const committedDate = commitNodes.length ? commitNodes[commitNodes.length - 1]?.commit?.committedDate ?? null : null;
  // Only meaningful as a "pushed after review" signal once a human has reviewed.
  const lastCommitAt =
    review.lastReviewedAt != null && (review.state === "reviewed" || review.state === "changes_requested")
      ? committedDate
      : null;

  const linkedIssues = (node.closingIssuesReferences?.nodes ?? []).map((i) => ({
    repository: i.repository?.nameWithOwner ?? repo,
    number: i.number,
    title: i.title,
    url: i.url,
    milestone: i.milestone?.title ?? null,
    labels: (i.labels?.nodes ?? []).map((l) => l.name),
  }));

  return {
    repository: repo,
    number: node.number,
    title: node.title,
    url: node.url,
    state,
    draft: node.isDraft,
    author,
    authorType,
    authorAvatarUrl: node.author?.avatarUrl ?? null,
    createdAt: node.createdAt,
    readyForReviewAt: node.readyForReviewEvents?.nodes?.[0]?.createdAt ?? null,
    firstReadyForReviewAt: null,
    firstReadinessError: null,
    updatedAt: node.updatedAt,
    baseRef: node.baseRefName,
    milestone: node.milestone?.title ?? null,
    labels,
    assignees,
    requestedReviewers,
    additions: node.additions,
    deletions: node.deletions,
    changedFiles: node.changedFiles,
    commitCount: node.commits?.totalCount ?? commitNodes.length,
    lastCommitAt,
    linkedIssues,
    // Engine-shaped fields (read by model.mjs).
    checks,
    mergeableState,
    // Back-compat fields read by the simplified Fowler lanes/signals path.
    unresolvedThreadCount: rawUnresolved,
    checksState,
    mergeable,
    // GitHub's authoritative review gate: APPROVED | CHANGES_REQUESTED |
    // REVIEW_REQUIRED | null (null when the repo has no required reviews).
    reviewDecision: node.reviewDecision ?? null,
    review,
    // Key off the human identity so a Copilot PR attributed to me ("me/copilot") still
    // counts as mine. actorIdentityKey strips the "/copilot" suffix and punctuation.
    isMine: [...viewers].some((viewer) => actorIdentityKey(viewer) === actorIdentityKey(author)),
    // Private repos cannot have external community contributors, so PRs on them are
    // never "Community" — they flow through the normal team attention engine instead.
    repoPrivate: !!repoPrivate,
  };
}

function normalizeIssue(repo, node, viewers) {
  const labels = (node.labels?.nodes ?? []).map((l) => l.name);
  const assignees = (node.assignees?.nodes ?? []).map((a) => a.login);
  const linkedPullRequests = (node.closedByPullRequestsReferences?.nodes ?? [])
    .filter((pr) => pr.state !== "CLOSED")
    .map((pr) => ({
      repository: pr.repository?.nameWithOwner ?? repo,
      number: pr.number,
      title: pr.title,
      url: pr.url,
      state: pr.state,
    }));
  const author = node.author?.login ?? "ghost";
  return {
    repository: repo,
    number: node.number,
    title: node.title,
    url: node.url,
    author,
    authorAvatarUrl: node.author?.avatarUrl ?? null,
    // __typename is the precise bot signal used by isBotAuthor's fast path. Issues
    // fetch it just like PRs (normalizePr) so a Bot-typed author whose login lacks
    // "bot" still earns the bot pill; the login string heuristics remain the fallback.
    authorType: node.author?.__typename ?? null,
    createdAt: node.createdAt,
    updatedAt: node.updatedAt,
    milestone: node.milestone?.title ?? null,
    labels,
    assignees,
    linkedPullRequests,
    isMine: viewers.has(author.toLowerCase()),
    assignedToMe: assignees.some((a) => viewers.has(a.toLowerCase())),
  };
}

// ---------------------------------------------------------------------------
// Signals (mirrors utils/signals.ts vocabulary + dedupe)
// ---------------------------------------------------------------------------

const SYNONYMS = [
  ["needs review", "needs reviewer", "no reviews"],
  ["ready to merge", "merge"],
  ["re-review needed", "re-review", "commit after review"],
  ["ci failing", "fix ci"],
  ["wait for ci", "ci running"],
  ["author response", "author fix", "changes requested"],
  ["quick wins", "quick win"],
  ["stalled", "unstick"],
  ["docs", "docs review"],
  ["community toolkit", "toolkit review"],
  ["bots / automation", "automation", "bot"],
];
const conceptByLabel = new Map(SYNONYMS.flatMap((g) => g.map((l) => [l, g[0]])));

function concept(label) {
  const n = label.trim().toLowerCase();
  if (n.startsWith("ci failing")) return "ci failing";
  if (n.includes("unresolved")) return "unresolved feedback";
  return conceptByLabel.get(n) ?? n;
}

export function dedupeSignals(signals) {
  const seen = new Set();
  return signals.filter((s) => {
    const c = concept(s.label);
    if (seen.has(c)) return false;
    seen.add(c);
    return true;
  });
}

// Faithful pr-dashboard pills: delegate to the ported attention-signal engine
// (model.mjs `createAttentionSignals`) instead of the old hand-rolled set. The
// engine emits `[actionSignal, ...stateSignals]`; mirror the dashboard card
// composition (PullRequestSignalPills): optionally drop the action pill, exclude
// the lane reason, cap the computed signals, then dedupe by concept.
export function signalsFor(pr, reason = "", opts = {}) {
  const { includeAction = true, limit = 4 } = opts;
  const all = createAttentionSignals({ pullRequest: pr, reason: "" });
  let computed = includeAction ? all : all.slice(1);
  if (reason) {
    const rc = concept(reason);
    computed = computed.filter((s) => concept(s.label) !== rc);
  }
  return dedupeSignals(computed.slice(0, limit));
}

function isReadyToMerge(pr) {
  return (
    !pr.draft &&
    pr.review.state === "approved" &&
    // Respect GitHub's authoritative decision: a PR that still requires review
    // (branch protection unmet, e.g. a lone approval on a repo that needs a
    // CODEOWNER or a second reviewer) or has a required reviewer requesting
    // changes cannot actually be merged, so it is not ready.
    pr.reviewDecision !== "REVIEW_REQUIRED" &&
    pr.reviewDecision !== "CHANGES_REQUESTED" &&
    pr.checksState === "success" &&
    pr.unresolvedThreadCount === 0 &&
    pr.mergeable !== "CONFLICTING"
  );
}

function isQuickWin(pr) {
  return !pr.draft && pr.changedFiles <= 3 && pr.additions + pr.deletions <= 40;
}

function isStalled(pr) {
  return Date.now() - new Date(pr.updatedAt).getTime() > 14 * DAY_MS;
}

// Team review policy (mirrors how davidfowl/pr-dashboard manages the shared queue):
//   * Drafts are noise and stay out of the shared view.
//   * A PR is not "review ready" until checks are green AND all feedback is resolved.
//     Anything unfinished bounces back to the author's own focus queue.
//   * The shared queue is team-managed: ranked deterministically and capped at 10 so
//     the team scans a focused set instead of a wall of open PRs.
export const REVIEW_LIMIT = 10;

function reviewReady(pr) {
  return (
    !pr.draft &&
    pr.checksState === "success" &&
    pr.unresolvedThreadCount === 0 &&
    pr.review.state !== "changes_requested" &&
    pr.mergeable !== "CONFLICTING"
  );
}

// Review-ready PRs that still need a review (not the viewer's own, not yet approved).
// Also honor the shared-list hide rule so PRs flagged for author action (draft, conflicts,
// or a needs-author-action label) never surface in the team's shared review queue.
function awaitingReview(pr) {
  return (
    !pr.labelsUnavailable &&
    reviewReady(pr) &&
    !pr.isMine &&
    pr.review.state !== "approved" &&
    !shouldHideFromSharedPullRequestLists(pr)
  );
}

// Oldest waits float to the top so nothing starves, with nudges for explicitly
// requested reviewers and quick wins. Order is team-managed, not user-sortable.
function reviewQueueScore(pr) {
  const ageDays = (Date.now() - new Date(reviewAgeStartedAt(pr)).getTime()) / DAY_MS;
  let s = Math.min(ageDays, 90);
  if (pr.requestedReviewers.length > 0) s += 20;
  if (isQuickWin(pr)) s += 12;
  if (pr.review.state === "reviewed") s += 6;
  return s;
}

// ---------------------------------------------------------------------------
// Lane bucketing per mode
// ---------------------------------------------------------------------------

const LANE_DEFS = {
  review: [
    { id: "review-queue", label: "Review queue", tone: "danger", match: awaitingReview },
    { id: "ready-to-merge", label: "Ready to merge", tone: "success", match: (pr) => isReadyToMerge(pr) },
    { id: "your-prs", label: "Your PRs", tone: "accent", match: (pr) => pr.isMine },
  ],
};

function laneReason(laneId, pr) {
  switch (laneId) {
    case "review-queue": return "Green and resolved \u00b7 ready for review";
    case "ready-to-merge": return "Approved and green, ready to land";
    case "your-prs":
      if (pr.checksState === "failure") return "CI is failing \u00b7 back in your court";
      if (pr.mergeable === "CONFLICTING") return "Merge conflicts \u00b7 back in your court";
      if (pr.review.state === "changes_requested") return "Changes requested \u00b7 back in your court";
      if (pr.unresolvedThreadCount > 0) return `${pr.unresolvedThreadCount} unresolved thread${pr.unresolvedThreadCount === 1 ? "" : "s"} to resolve`;
      if (pr.draft) return "Draft \u00b7 still cooking";
      return "You opened this";
    default: return "";
  }
}

function bucketReview(prs, { showDrafts = false } = {}) {
  // Drafts are filtered out by default; they are prototypes, not review work.
  const source = showDrafts ? prs : prs.filter((pr) => !pr.draft);
  const defs = LANE_DEFS.review;
  const lanes = defs.map((d) => ({ id: d.id, label: d.label, tone: d.tone, items: [] }));
  for (const pr of source) {
    for (let i = 0; i < defs.length; i++) {
      if (defs[i].match(pr)) {
        const r = laneReason(defs[i].id, pr);
        lanes[i].items.push({ pr, reason: r, signals: signalsFor(pr, r) });
        break;
      }
    }
  }
  // The shared review queue is ranked deterministically and capped so the team scans
  // a focused set. `cappedTotal` lets the UI show "top N of M".
  const queue = lanes.find((l) => l.id === "review-queue");
  if (queue) {
    queue.items.sort((a, b) => reviewQueueScore(b.pr) - reviewQueueScore(a.pr));
    queue.cappedTotal = queue.items.length;
    if (queue.items.length > REVIEW_LIMIT) queue.items = queue.items.slice(0, REVIEW_LIMIT);
  }
  return lanes.filter((l) => l.items.length > 0);
}

// Issues view: pr-dashboard focus buckets (Regression / CTI team / afscrome finds, plus a
// per-viewer "My issues" lane) using the ported `createFocusIssueBuckets` + `createIssueSignals`.
// Focus buckets only cover a curated subset, so residual lanes catch everything else and keep
// no open issue from silently dropping out of the view.
function bucketIssues(issues, login) {
  const covered = new Set();
  const toItem = (issue) => {
    covered.add(issue.url);
    return { issue, signals: createIssueSignals(issue) };
  };

  const lanes = createFocusIssueBuckets(issues, login).map((bucket) => ({
    id: `focus-${bucket.label.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "")}`,
    label: bucket.label,
    tone: bucket.tone,
    items: bucket.issues.map(toItem),
  }));

  const residual = issues.filter((issue) => !covered.has(issue.url));
  const isUntriaged = (issue) => issue.labels.length === 0 && issue.assignees.length === 0;
  const triage = residual.filter(isUntriaged);
  const active = residual.filter((issue) => !isUntriaged(issue));

  if (triage.length) {
    lanes.push({ id: "triage", label: "Needs triage", tone: "warning", items: triage.map(toItem) });
  }
  if (active.length) {
    lanes.push({ id: "active", label: "Recently active", tone: "muted", items: active.map(toItem) });
  }

  return lanes.filter((lane) => lane.items.length > 0);
}

function bucketShip(prs, release, { showDrafts = false } = {}) {
  const inMilestone = prs.filter((pr) => pr.milestone === release && (showDrafts || !pr.draft));
  const lanes = [
    { id: "ready", label: "Ready to ship", tone: "success", items: [] },
    { id: "in-progress", label: "In progress", tone: "accent", items: [] },
    { id: "blocked", label: "Blocked", tone: "danger", items: [] },
  ];
  for (const pr of inMilestone) {
    let laneId;
    if (pr.checksState === "failure" || pr.mergeable === "CONFLICTING" || pr.review.state === "changes_requested")
      laneId = "blocked";
    else if (isReadyToMerge(pr)) laneId = "ready";
    else laneId = "in-progress";
    const r = pr.milestone ? `Milestone ${pr.milestone}` : "";
    lanes.find((l) => l.id === laneId).items.push({
      pr, reason: r, signals: signalsFor(pr, r),
    });
  }
  return lanes.filter((l) => l.items.length > 0);
}

// ---------------------------------------------------------------------------
// Notifications (derived from the viewer's current state)
// ---------------------------------------------------------------------------

export function buildNotifications(prs, prefs, dismissed = []) {
  const skip = new Set(dismissed);
  const items = [];
  const push = (n) => {
    n.id = `${n.kind}:${n.repository}#${n.number}`;
    if (!skip.has(n.id)) items.push(n);
  };
  for (const pr of prs) {
    if (prefs.reviewRequested && !pr.draft && !pr.isMine && pr.review.reviewRequestedFromViewer) {
      push({ kind: "review-requested", tone: "danger", title: pr.title, repository: pr.repository,
        number: pr.number, url: pr.url, detail: "Review requested from you" });
    }
    // Ready to merge nags both the author and any approver (mirrors pr-dashboard #86), so it
    // lives outside the author-only block. A PR you both authored and approved notifies once
    // (the shared kind:repo#number id dedupes), as the author.
    if (prefs.readyToMerge && isReadyToMerge(pr) && (pr.isMine || pr.review.viewerApproved)) {
      push({ kind: "ready-to-merge", tone: "success", title: pr.title, repository: pr.repository,
        number: pr.number, url: pr.url,
        detail: pr.isMine ? "Your PR is ready to merge" : "A PR you approved is ready to merge" });
    }
    if (pr.isMine) {
      if (prefs.changesRequested && pr.review.state === "changes_requested") {
        push({ kind: "changes-requested", tone: "warning", title: pr.title, repository: pr.repository,
          number: pr.number, url: pr.url, detail: "Changes requested on your PR" });
      }
      if (prefs.ciFailing && pr.checksState === "failure") {
        push({ kind: "ci-failing", tone: "danger", title: pr.title, repository: pr.repository,
          number: pr.number, url: pr.url, detail: "CI is failing on your PR" });
      }
    }
  }
  return items;
}

// ---------------------------------------------------------------------------
// Top-level loader
// ---------------------------------------------------------------------------

export async function loadDashboard({ accounts, mode, release, prefs, dismissed, showDrafts = false, onProgress, onPartial, onRelated }) {
  const usable = (accounts ?? []).filter((a) => a && a.token && a.login);
  if (usable.length === 0) {
    return { authenticated: false, message: "No active GitHub account. Enable an account in the Accounts tab so the canvas can read your review queue." };
  }

  const viewers = new Set(usable.map((a) => a.login.toLowerCase()));
  const wantIssues = mode === "issues";
  // Review and Ship use the same PRs. When the caller wants both snapshots, collect
  // readiness once so switching between those tabs does not refetch every repository.
  const wantReadiness = mode === "review" || (mode === "ship" && typeof onRelated === "function");

  // Union of every active account's watched repos.
  const repos = [...new Set(usable.flatMap((a) => a.repos ?? []))];

  const prById = new Map();   // url -> normalized PR (dedup across accounts)
  const issueById = new Map();
  // Success/error are tracked per (host, repo), NOT per repo alone. The same owner/repo slug can
  // exist on github.com and on a GHES/EMU host simultaneously, so a success on one host must not
  // suppress the other host's failure — that would silently drop the failing host's PRs with no
  // surfaced error. The GraphQL origin (acct.graphql) identifies the host (undefined for the
  // github.com default). Accounts that share a host still collapse to one key, so duplicate
  // credentials for the same slug keep de-duping as before.
  const hostRepoKey = (graphql, repo) => `${graphql ?? ""}\n${repo}`;
  const okRepos = new Set();  // hostRepoKey(host, repo) that succeeded under at least one account
  const errorsRaw = [];       // { repo, host, message }

  // Partial snapshots are for a board without a complete cached result. Refreshes of an
  // existing board remain atomic so cards do not disappear while repositories are loading.
  let total = 0;
  let done = 0;
  let lastPartialAt = -Infinity;
  let partialTimer = null;
  const emitPartial = () => {
    partialTimer = null;
    lastPartialAt = Date.now();
    try {
      onPartial(snapshot(mode, true));
    } catch (error) {
      console.error("Failed to publish a partial GitHub dashboard:", error);
    }
  };
  const maybePartial = () => {
    if (typeof onPartial !== "function") return;
    const delay = PARTIAL_MIN_MS - (Date.now() - lastPartialAt);
    if (delay <= 0) {
      if (partialTimer) clearTimeout(partialTimer);
      emitPartial();
    } else if (!partialTimer) {
      // Emit the trailing update even if every remaining repository is still blocked.
      partialTimer = setTimeout(emitPartial, delay);
    }
  };
  const reportDone = () => {
    done++;
    if (typeof onProgress === "function") onProgress({ done, total, phase: "fetch" });
    maybePartial();
  };

  // Fetch each (account, repo) pair with that account's own token.
  const jobs = [];
  for (const acct of usable) {
    for (const repo of acct.repos ?? []) {
      jobs.push(
        (async () => {
          const { owner, name } = splitRepo(repo);
          if (!owner || !name) {
            errorsRaw.push({ repo, host: acct.graphql, message: `Invalid repo "${repo}"` });
            return;
          }
          try {
            if (wantIssues) {
              await loadOpenPages(acct, owner, name, "issues", async (after) => {
                const data = await gql(acct.token, ISSUE_QUERY, { owner, name, after }, acct.graphql);
                const conn = data.repository?.issues;
                for (const node of conn?.nodes ?? []) {
                  const issue = normalizeIssue(repo, node, viewers);
                  if (!issueById.has(issue.url)) issueById.set(issue.url, issue);
                }
                maybePartial();
                return conn;
              });
            } else {
              await loadOpenPages(acct, owner, name, "pullRequests", async (after) => {
                const { data, errors: graphqlErrors = [] } = await gql(
                  acct.token,
                  PR_QUERY,
                  { owner, name, after, includeReadiness: wantReadiness },
                  acct.graphql,
                  { allowPartialData: true },
                );
                const conn = data?.repository?.pullRequests;
                const nodes = conn?.nodes ?? [];
                const initialLabelErrors = getPullRequestLabelErrors(graphqlErrors, nodes);
                if (!data) {
                  throw new Error("Missing GitHub GraphQL response data");
                }
                const repoPrivate = !!data.repository?.isPrivate;
                for (const [nodeIndex, node] of nodes.entries()) {
                  // Priority labels may follow the list query's first 15 labels.
                  // Keep the PR for views that do not need labels, but exclude it from attention
                  // ranking if the complete label connection cannot be loaded.
                  let labelResult = {
                    labels: { nodes: getVerifiedLabelNodes(node.labels) },
                    error: initialLabelErrors.get(nodeIndex) ?? null,
                  };
                  if (labelResult.error === null) {
                    try {
                      labelResult = await loadPullRequestLabels(acct, owner, name, node);
                    } catch (error) {
                      labelResult.error = getErrorMessage(error);
                    }
                  }
                  const pr = normalizePr(repo, { ...node, labels: labelResult.labels }, viewers, repoPrivate);
                  pr.labelsUnavailable = labelResult.error !== null;
                  pr.labelsError = labelResult.error;
                  if (wantReadiness) {
                    try {
                      pr.firstReadyForReviewAt = await loadFirstReadyForReviewAt(acct, owner, name, node);
                    } catch (error) {
                      pr.firstReadinessError = error.message;
                    }
                  }
                  const existing = prById.get(pr.url);
                  prById.set(pr.url, existing ? mergePullRequestSnapshots(existing, pr, wantReadiness ? "review" : mode) : pr);
                }
                maybePartial();
                return conn;
              });
            }
            okRepos.add(hostRepoKey(acct.graphql, repo));
          } catch (e) {
            errorsRaw.push({ repo, host: acct.graphql, message: `${repo}: ${e.message}` });
          }
        })().then(reportDone),
      );
    }
  }
  total = jobs.length;

  // Reshape the accumulated PRs/issues into the dashboard shape the renderer consumes.
  function snapshot(snapshotMode = mode, loading = false) {
    const allPrs = [...prById.values()].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));
    const allIssues = [...issueById.values()].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));

    // Drafts are filtered out of the shared view by default (prototypes, not review work).
    const visiblePrs = showDrafts ? allPrs : allPrs.filter((p) => !p.draft);
    const draftCount = allPrs.filter((p) => p.draft).length;

    // Drop per-account errors for (host, repo) pairs another active account could read on that same
    // host — a success on a different host must not mask this host's failure.
    const errors = [...new Set(errorsRaw.filter((e) => !okRepos.has(hostRepoKey(e.host, e.repo))).map((e) => e.message))];
    for (const pr of allPrs) {
      if (pr.labelsUnavailable) {
        errors.push(`${pr.repository}#${pr.number}: labels unavailable; excluded from PR queue. ${pr.labelsError}`);
      }
      if (snapshotMode === "review" && pr.firstReadinessError) {
        errors.push(`${pr.repository}#${pr.number}: first readiness unavailable; omitted from PR queue. ${pr.firstReadinessError}`);
      }
    }

    let lanes;
    if (snapshotMode === "issues") lanes = bucketIssues(allIssues, usable[0].login);
    else if (snapshotMode === "ship") lanes = bucketShip(allPrs, release ?? CURRENT_RELEASE, { showDrafts });
    else lanes = bucketReview(allPrs, { showDrafts });

    // Full pr-dashboard attention engine (review mode only). The focused "PR queue"
    // queue is the capped headline; attention buckets, the community list, the personal
    // "For you" picks and core-team developer counts sit beneath it. Items are reshaped into
    // the card shape the renderer consumes ({ pr, reason, signals }).
    let attention = null;
    if (snapshotMode === "review") {
      const login = usable[0].login;
      const viewerLogins = usable.map((a) => a.login);
      const attentionPrs = allPrs.filter((pr) => !pr.labelsUnavailable);
      const focusExclusionPrs = allPrs.filter((pr) => !pr.labelsUnavailable || pr.isMine);
      const personalActionPrs = allPrs.filter((pr) => !pr.labelsUnavailable ||
        (pr.isMine && (isChecksFailing(pr) || pr.review.state === "changes_requested" ||
          pr.review.state === "approved" || pr.review.unresolvedThreadCount > 0 ||
          shouldHideFromSharedPullRequestLists(pr))));
      // Draft ownership does not depend on complete labels. Retain owned drafts only in
      // draft buckets, never in shared label-dependent buckets such as Regression.
      const buckets = createAttentionBuckets(allPrs, login).map((bucket) => ({
        ...bucket,
        items: bucket.items.filter(({ pullRequest: pr }) => !pr.labelsUnavailable ||
          (pr.isMine && pr.draft && (bucket.label === "Draft" || bucket.label === "My draft PRs"))),
      })).filter((bucket) => bucket.items.length > 0);
      const focusAll = computeFocusItems(buckets);
      const focusExclusions = computeFocusExclusionItems(focusExclusionPrs, buckets, focusAll, login);
      // Every review-mode card carries a reviewDebt flag (aged past the focus limit without an
      // approving review) so the canvas can offer Address review / Discuss review even when the
      // "review debt" pill is truncated off the displayed signals. Derive it from the shared
      // isReviewDebt predicate, NOT from c.signals: signalsFor caps the display pills, so a debt PR
      // that also carries release/regression pills would lose its "review debt" pill and be wrongly
      // treated as non-debt. The predicate is shared with createAttentionSignals and the
      // focus-retention filter so they cannot drift.
      const card = (pr, reason, extra) =>
        Object.assign({ pr, reason: reason || "", signals: signalsFor(pr, reason || ""), reviewDebt: isReviewDebt(pr) }, extra || {});
      const focusCards = focusAll.map((f, index) => {
        return card(f.pullRequest, f.reason, {
          bucketLabel: f.bucketLabel, bucketTone: f.bucketTone,
          queuePosition: index + 1, urgent: isUrgentPullRequest(f.pullRequest),
        });
      });
      attention = {
        forMe: createForMeItems(personalActionPrs, viewerLogins).map((f) => {
          // Personal pick leads with its own call-to-action; the engine's action pill is
          // dropped (includeAction:false) so we don't double up, then state signals follow.
          const c = card(f.pullRequest, f.reason);
          c.signals = dedupeSignals([
            { label: f.action, tone: f.tone || "accent" },
            ...signalsFor(f.pullRequest, f.reason, { includeAction: false, limit: 3 }),
          ]);
          // Preserve the raw action label (e.g. "Resolve conflicts") as a field so the
          // canvas can offer a matching action button; the label is otherwise only
          // reachable folded into signals[0].
          c.action = f.action;
          return c;
        }),
        // Keep the entire ranked queue across the split, including debt cards. The remainder
        // is position lookup, not an exception to the team's ten-card review priority.
        focus: focusCards.slice(0, REVIEW_LIMIT),
        focusRemainder: focusCards.slice(REVIEW_LIMIT),
        focusTotal: focusAll.length,
        focusLimit: REVIEW_LIMIT,
        focusExclusions: focusExclusions.map((e) => ({
          pr: e.pullRequest,
          reason: e.reason.detail,
          // Serialize the debt flag here too (this shape is built inline, not via card()) so a stacked
          // "Your PRs outside" card keeps Address review / Discuss review when its pill is truncated.
          reviewDebt: isReviewDebt(e.pullRequest),
          signals: dedupeSignals([
            { label: e.reason.label, tone: e.reason.tone },
            ...signalsFor(e.pullRequest, e.reason.label, { includeAction: false, limit: 4 }),
          ]),
        })),
        buckets: buckets.map((b) => ({
          label: b.label,
          tone: b.tone,
          items: b.items.map((i) => card(i.pullRequest, i.reason)),
        })),
        community: computeCommunityItems(attentionPrs).map((c) => card(c.pullRequest, "Community")),
        developerCounts: createDeveloperPullRequestCounts(attentionPrs),
      };
    }

    const notifications = buildNotifications(allPrs, prefs ?? {}, dismissed ?? []);

    const counts = {
      prs: visiblePrs.length,
      total: allPrs.length,
      drafts: draftCount,
      issues: allIssues.length,
      mine: allPrs.filter((p) => p.isMine).length,
      needsReview: allPrs.filter(awaitingReview).length,
      readyToMerge: allPrs.filter(isReadyToMerge).length,
      ciFailing: visiblePrs.filter((p) => p.checksState === "failure").length,
    };

    return {
      authenticated: true,
      viewer: usable[0].login,
      viewers: usable.map((a) => a.login),
      mode: snapshotMode,
      ...(loading ? { loading: true } : {}),
      release: release ?? CURRENT_RELEASE,
      repos,
      lanes,
      attention,
      notifications,
      counts,
      showDrafts,
      reviewLimit: REVIEW_LIMIT,
      errors,
      fetchedAt: new Date().toISOString(),
    };
  }

  try {
    await Promise.all(jobs);
  } finally {
    if (partialTimer) clearTimeout(partialTimer);
  }
  // Final, authoritative progress tick so the deterministic bar completes with the snapshot.
  if (typeof onProgress === "function") onProgress({ done: total, total, phase: "done" });
  if (typeof onRelated === "function" && (mode === "review" || mode === "ship")) {
    onRelated(snapshot(mode === "review" ? "ship" : "review"));
  }
  return snapshot();
}
