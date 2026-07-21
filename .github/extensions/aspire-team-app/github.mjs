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
  shouldHideFromSharedPullRequestLists,
  visibleCheckState,
} from "./model.mjs";
import { currentRelease } from "./constants.mjs";

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

// Enterprise Managed User (EMU) accounts (e.g. "dapine_microsoft") work against
// the private first-party mirror rather than the public Aspire repos, so they get
// a different default watch set. See accounts.isEmuAccountId for how an account is
// classified and state.defaultReposForId for how this default is applied.
export const DEFAULT_EMU_REPOS = [
  "devdiv-microsoft/aspire-1p",
];

const GRAPHQL = "https://api.github.com/graphql";

// ---------------------------------------------------------------------------
// GraphQL helper.
// ---------------------------------------------------------------------------

async function gql(token, query, variables, graphqlUrl = GRAPHQL) {
  const res = await fetch(graphqlUrl, {
    method: "POST",
    headers: {
      Authorization: `bearer ${token}`,
      "Content-Type": "application/json",
      "User-Agent": "aspire-team-app-canvas",
    },
    body: JSON.stringify({ query, variables }),
  });
  if (!res.ok) {
    throw new Error(`GitHub API ${res.status} ${res.statusText}`);
  }
  const json = await res.json();
  if (json.errors?.length) {
    throw new Error(json.errors.map((e) => e.message).join("; "));
  }
  return json.data;
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

const PR_QUERY = `
query($owner:String!, $name:String!, $after:String) {
  repository(owner:$owner, name:$name) {
    isPrivate
    pullRequests(states:OPEN, first:40, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
      pageInfo { hasNextPage endCursor }
      nodes {
        number title url isDraft state createdAt updatedAt
        author { __typename login avatarUrl }
        baseRefName
        mergeable
        reviewDecision
        additions deletions changedFiles
        milestone { title }
        labels(first:15) { nodes { name } }
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

const ISSUE_QUERY = `
query($owner:String!, $name:String!, $after:String) {
  repository(owner:$owner, name:$name) {
    issues(states:OPEN, first:40, after:$after, orderBy:{field:UPDATED_AT, direction:DESC}) {
      pageInfo { hasNextPage endCursor }
      nodes {
        number title url createdAt updatedAt
        author { __typename login avatarUrl }
        milestone { title }
        labels(first:15) { nodes { name } }
        assignees(first:10) { nodes { login } }
      }
    }
  }
}`;

function splitRepo(repo) {
  const [owner, ...rest] = repo.split("/");
  return { owner, name: rest.join("/") };
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
  return n === "copilot" || n === "copilot[bot]" || n === "github-copilot[bot]" || n.endsWith("/copilot");
}

// Copilot attribution (mirrors GitHubModels.ResolveAuthor): a Copilot-authored PR with
// exactly one human assignee is attributed to "{human}/copilot" so it classifies as the
// human's work rather than a bot.
function resolveAuthor(login, assignees) {
  if (!isCopilotLogin(login)) return login;
  const humans = assignees.filter((a) => a && !/\[bot\]$/i.test(a) && a.toLowerCase() !== "copilot");
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
    reviewReady(pr) &&
    !pr.isMine &&
    pr.review.state !== "approved" &&
    !shouldHideFromSharedPullRequestLists(pr)
  );
}

// Oldest waits float to the top so nothing starves, with nudges for explicitly
// requested reviewers and quick wins. Order is team-managed, not user-sortable.
function reviewQueueScore(pr) {
  const ageDays = (Date.now() - new Date(pr.createdAt).getTime()) / DAY_MS;
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

function bucketReview(prs, { showDrafts = false, limit = REVIEW_LIMIT } = {}) {
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
    if (queue.items.length > limit) queue.items = queue.items.slice(0, limit);
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

// The focused queue headline is capped to `limit` actionable cards, but review-debt cards sort
// last (they carry the oldest activity timestamp) and would be sliced away whenever `limit`
// fresher PRs exist — leaving their "Address review" button unreachable even though focusTotal
// still counts them. Keep the freshest `limit` cards plus any review-debt cards that spill past
// the cap, so the debt action stays reachable. The two slices are disjoint, so no card is
// duplicated, and focusTotal continues to report the true (uncapped) count.
export function capFocusKeepingDebt(cards, limit) {
  const head = cards.slice(0, limit);
  const spilloverDebt = cards.slice(limit).filter((c) => c && c.reviewDebt);
  return head.concat(spilloverDebt);
}

// ---------------------------------------------------------------------------
// Top-level loader
// ---------------------------------------------------------------------------

export async function loadDashboard({ accounts, mode, release, prefs, dismissed, showDrafts = false, reviewLimit = REVIEW_LIMIT, onProgress, onPartial }) {
  const usable = (accounts ?? []).filter((a) => a && a.token && a.login);
  if (usable.length === 0) {
    return { authenticated: false, message: "No active GitHub account. Enable an account in the Accounts tab so the canvas can read your review queue." };
  }

  const viewers = new Set(usable.map((a) => a.login.toLowerCase()));
  const wantIssues = mode === "issues";

  // Union of every active account's watched repos.
  const repos = [...new Set(usable.flatMap((a) => a.repos ?? []))];

  const prById = new Map();   // url -> normalized PR (dedup across accounts)
  const issueById = new Map();
  const okRepos = new Set();  // repos that succeeded under at least one account
  const errorsRaw = [];       // { repo, message }

  // Streaming progress + incremental snapshots. Each (account, repo) fetch reports when it
  // settles so the caller can drive a deterministic progress bar (onProgress) and receive
  // partial dashboards as data arrives (onPartial). Partials are throttled so a burst of
  // fast repos doesn't reshape on every single completion.
  let total = 0;
  let done = 0;
  let lastPartialAt = 0;
  const PARTIAL_MIN_MS = 250;
  const maybePartial = (force) => {
    if (typeof onPartial !== "function") return;
    const now = Date.now();
    if (!force && now - lastPartialAt < PARTIAL_MIN_MS) return;
    lastPartialAt = now;
    // A failed partial push must never abort the underlying load.
    try { onPartial(snapshot()); } catch { /* ignore */ }
  };
  const reportDone = () => {
    done++;
    if (typeof onProgress === "function") onProgress({ done, total, phase: "fetch" });
    maybePartial(false);
  };

  // Fetch each (account, repo) pair with that account's own token.
  const jobs = [];
  for (const acct of usable) {
    for (const repo of acct.repos ?? []) {
      jobs.push(
        (async () => {
          const { owner, name } = splitRepo(repo);
          if (!owner || !name) {
            errorsRaw.push({ repo, message: `Invalid repo "${repo}"` });
            return;
          }
          try {
            if (wantIssues) {
              let after = null;
              for (let page = 0; page < MAX_PAGES; page++) {
                const data = await gql(acct.token, ISSUE_QUERY, { owner, name, after }, acct.graphql);
                const conn = data.repository?.issues;
                for (const node of conn?.nodes ?? []) {
                  const issue = normalizeIssue(repo, node, viewers);
                  if (!issueById.has(issue.url)) issueById.set(issue.url, issue);
                }
                if (!conn?.pageInfo?.hasNextPage) break;
                after = conn.pageInfo.endCursor;
              }
            } else {
              let after = null;
              for (let page = 0; page < MAX_PAGES; page++) {
                const data = await gql(acct.token, PR_QUERY, { owner, name, after }, acct.graphql);
                const repoPrivate = !!data.repository?.isPrivate;
                const conn = data.repository?.pullRequests;
                for (const node of conn?.nodes ?? []) {
                  const pr = normalizePr(repo, node, viewers, repoPrivate);
                  if (!prById.has(pr.url)) prById.set(pr.url, pr);
                }
                if (!conn?.pageInfo?.hasNextPage) break;
                after = conn.pageInfo.endCursor;
              }
            }
            okRepos.add(repo);
          } catch (e) {
            errorsRaw.push({ repo, message: `${repo}: ${e.message}` });
          }
        })().then(reportDone),
      );
    }
  }
  total = jobs.length;

  // Reshape the accumulated PRs/issues into the dashboard shape the renderer consumes.
  // A hoisted function declaration so the streaming callbacks above can snapshot partial
  // results as repos arrive; also called once more below for the final, complete result.
  function snapshot() {
    const allPrs = [...prById.values()].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));
    const allIssues = [...issueById.values()].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));

    // Drafts are filtered out of the shared view by default (prototypes, not review work).
    const visiblePrs = showDrafts ? allPrs : allPrs.filter((p) => !p.draft);
    const draftCount = allPrs.filter((p) => p.draft).length;

    // Drop per-account errors for repos another active account could read.
    const errors = [...new Set(errorsRaw.filter((e) => !okRepos.has(e.repo)).map((e) => e.message))];

    let lanes;
    if (mode === "issues") lanes = bucketIssues(allIssues, usable[0].login);
    else if (mode === "ship") lanes = bucketShip(allPrs, release ?? CURRENT_RELEASE, { showDrafts });
    else lanes = bucketReview(allPrs, { showDrafts, limit: reviewLimit });

    // Full pr-dashboard attention engine (review mode only). The focused "Needs attention"
    // queue is the capped headline; attention buckets, the community list, the personal
    // "For you" picks and core-team developer counts sit beneath it. Items are reshaped into
    // the card shape the renderer consumes ({ pr, reason, signals }).
    let attention = null;
    if (mode === "review") {
      const login = usable[0].login;
      const viewerLogins = usable.map((a) => a.login);
      const buckets = createAttentionBuckets(allPrs, login);
      const focusAll = computeFocusItems(buckets);
      const focusExclusions = computeFocusExclusionItems(allPrs, buckets, focusAll, login);
      const card = (pr, reason, extra) =>
        Object.assign({ pr, reason: reason || "", signals: signalsFor(pr, reason || "") }, extra || {});
      const focusCards = focusAll.map((f) => {
        const c = card(f.pullRequest, f.reason, { bucketLabel: f.bucketLabel, bucketTone: f.bucketTone });
        // Flag review-debt cards (aged past the focus limit without a review) so the canvas can
        // offer an "Address review" button instead of Test/Review. Derive this from the shared
        // isReviewDebt predicate, NOT from c.signals: signalsFor truncates the display pills to a
        // few highest-priority signals, so a debt PR that also carries release/regression pills
        // would lose its "review debt" pill and be wrongly treated as non-debt — dropping it from
        // capFocusKeepingDebt's spillover and hiding "Address review". The predicate is the same
        // one createAttentionSignals and the focus-retention filter use, so they cannot drift.
        c.reviewDebt = isReviewDebt(f.pullRequest);
        return c;
      });
      const focus = capFocusKeepingDebt(focusCards, reviewLimit);
      attention = {
        forMe: createForMeItems(allPrs, viewerLogins).map((f) => {
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
        focus,
        focusTotal: focusAll.length,
        focusLimit: reviewLimit,
        // True when capFocusKeepingDebt appended review-debt cards from past the cap, so `focus`
        // is no longer a prefix of the sorted list: non-debt cards between retained debt cards are
        // skipped. A "top N of total" label would then be false, so the canvas shows an honest
        // "N shown" metric for this mixed selection instead.
        focusMixed: focus.length > reviewLimit,
        focusExclusions: focusExclusions.map((e) => ({
          pr: e.pullRequest,
          reason: e.reason.detail,
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
        community: computeCommunityItems(allPrs).map((c) => card(c.pullRequest, "Community")),
        developerCounts: createDeveloperPullRequestCounts(allPrs),
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
      mode,
      release: release ?? CURRENT_RELEASE,
      repos,
      lanes,
      attention,
      notifications,
      counts,
      showDrafts,
      reviewLimit,
      errors,
      fetchedAt: new Date().toISOString(),
    };
  }

  await Promise.all(jobs);
  // Final, authoritative progress tick so a deterministic bar can complete even if the
  // last throttled partial was skipped.
  if (typeof onProgress === "function") onProgress({ done: total, total, phase: "done" });
  return snapshot();
}
