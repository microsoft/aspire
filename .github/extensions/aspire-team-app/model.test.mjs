import assert from "node:assert/strict";
import test from "node:test";

import { dayMs, personalPickActions, reviewDebtSignalLabel } from "./constants.mjs";
import {
  actorIdentityKey,
  computeCommunityItems,
  computeFocusExclusionItems,
  computeFocusItems,
  createAttentionBuckets,
  createAttentionSignals,
  createDeveloperPullRequestCounts,
  createFocusIssueBuckets,
  createForMeItems,
  createIssueSignals,
  filterCheckFailureRules,
  isChecksFailing,
  isReviewDebt,
  shouldHideFromSharedPullRequestLists,
  visibleCheckState,
} from "./model.mjs";

// The review-mode engine consumes the *normalized* PR shape produced by
// normalizePr() in github.mjs (not raw GraphQL nodes). makePr mirrors that shape
// exactly so these unit tests exercise the ranking logic without the fetch layer.
// The github.test.mjs suite covers the ship/issues fetch path; this file covers
// the review-mode buckets/focus/personal-pick functions that path never reaches.
function isoAgo(ms) {
  return new Date(Date.now() - ms).toISOString();
}

function makePr(overrides = {}) {
  const review = {
    state: "waiting",
    approvalCount: 0,
    changesRequestedCount: 0,
    commentedReviewCount: 0,
    reviewerCount: 0,
    lastApprovedAt: null,
    lastReviewedAt: null,
    copilotReviewed: false,
    unresolvedThreadCount: 0,
    requiresConversationResolution: false,
    reviewRequestedFromViewer: false,
    viewerApproved: false,
    ...(overrides.review ?? {}),
  };
  // Mirrors the engine-shaped ChecksStatus normalizePr() emits: the lean list-level query
  // yields zeroed per-check detail, so failingChecks defaults empty and the counts are 0.
  const checks = {
    state: "success",
    totalCount: 0,
    successCount: 0,
    failureCount: 0,
    pendingCount: 0,
    neutralCount: 0,
    skippedCount: 0,
    completedAt: null,
    failingChecks: [],
    ...(overrides.checks ?? {}),
  };
  return {
    repository: "microsoft/aspire",
    number: 1,
    title: "Test PR",
    url: "https://github.com/microsoft/aspire/pull/1",
    state: "open",
    draft: false,
    author: "davidfowl",
    authorType: "User",
    authorAvatarUrl: null,
    createdAt: isoAgo(2 * dayMs),
    updatedAt: isoAgo(dayMs),
    baseRef: "main",
    milestone: null,
    labels: [],
    assignees: [],
    requestedReviewers: [],
    additions: 200,
    deletions: 10,
    changedFiles: 10,
    commitCount: 3,
    lastCommitAt: null,
    linkedIssues: [],
    mergeableState: "clean",
    unresolvedThreadCount: 0,
    checksState: "success",
    mergeable: "MERGEABLE",
    reviewDecision: null,
    isMine: false,
    repoPrivate: false,
    ...overrides,
    review,
    checks,
  };
}

function bucketWith(label, ...items) {
  return { label, tone: "accent", items: items.map((pr) => ({ pullRequest: pr, reason: "" })) };
}

test("createAttentionBuckets drops closed PRs and PRs held by a do-not-merge label", () => {
  const needsReview = makePr({ number: 1, author: "davidfowl", review: { state: "waiting" } });
  const held = makePr({ number: 2, author: "davidfowl", labels: ["needs-author-action"] });
  const closed = makePr({ number: 3, author: "davidfowl", state: "closed" });

  const buckets = createAttentionBuckets([needsReview, held, closed]);
  const numbers = buckets.flatMap((b) => b.items.map((i) => i.pullRequest.number));

  assert.ok(numbers.includes(1));
  assert.ok(!numbers.includes(2));
  assert.ok(!numbers.includes(3));
  const review = buckets.find((b) => b.label === "Needs review");
  assert.ok(review);
  assert.deepEqual(review.items.map((i) => i.pullRequest.number), [1]);
});

test("createAttentionBuckets only surfaces the My draft PRs bucket when a login is supplied", () => {
  const draft = makePr({ number: 7, draft: true, isMine: true, author: "octo" });

  const withLogin = createAttentionBuckets([draft], "octo");
  const myDrafts = withLogin.find((b) => b.label === "My draft PRs");
  assert.ok(myDrafts);
  assert.deepEqual(myDrafts.items.map((i) => i.pullRequest.number), [7]);

  const withoutLogin = createAttentionBuckets([draft]);
  assert.equal(withoutLogin.find((b) => b.label === "My draft PRs"), undefined);
});

test("computeFocusItems keeps the highest-priority lane per PR and excludes CI-failing PRs", () => {
  const readyAndNeedsReview = makePr({ number: 10, updatedAt: isoAgo(dayMs) });
  const failing = makePr({ number: 11, checks: { state: "failure", failureCount: 2 }, updatedAt: isoAgo(dayMs) });

  const buckets = [
    bucketWith("Ready to merge", readyAndNeedsReview),
    bucketWith("Needs review", readyAndNeedsReview, failing),
  ];

  const focus = computeFocusItems(buckets);

  assert.equal(focus.length, 1);
  assert.equal(focus[0].pullRequest.number, 10);
  // Lower focusBucketRank wins: Ready to merge (2) beats Needs review (4).
  assert.equal(focus[0].bucketLabel, "Ready to merge");
});

test("computeFocusItems keys focus identity by host so same-slug PRs on different hosts stay distinct", () => {
  // The loader aggregates accounts across github.com and GHES/EMU, where the same owner/repo slug and
  // PR number can both exist as two different PRs. Without the host in the focus identity, byPr would
  // collapse them into a single card (and a disqualifying bucket on one host would block the other).
  const comPr = makePr({ number: 500, url: "https://github.com/microsoft/aspire/pull/500", updatedAt: isoAgo(dayMs) });
  const ghePr = makePr({ number: 500, url: "https://ghe.example.com/microsoft/aspire/pull/500", updatedAt: isoAgo(dayMs) });

  const focus = computeFocusItems([bucketWith("Needs review", comPr, ghePr)]);

  const urls = focus.map((i) => i.pullRequest.url).sort();
  assert.deepEqual(urls, [
    "https://ghe.example.com/microsoft/aspire/pull/500",
    "https://github.com/microsoft/aspire/pull/500",
  ]);
});

test("computeFocusItems retains review-debt PRs through the full createAttentionBuckets pipeline", () => {
  // Fresh unreviewed PR: comfortably within the focus window.
  const fresh = makePr({ number: 40, updatedAt: isoAgo(dayMs) });
  // Unreviewed and untouched for 20 days: still lands in "Needs review" but is aged past the 14d
  // focus limit, so it only survives because isReviewDebt() retains it.
  const needsReviewDebt = makePr({ number: 41, updatedAt: isoAgo(20 * dayMs) });
  // Reviewed once, then gone quiet for 20 days with no newer commit: its ONLY bucket is "Stalled"
  // (normally excluded from focus), yet oldFirstSignal() flags it "review debt". It must still
  // reach the queue through the real createAttentionBuckets() -> computeFocusItems() path so the
  // "Address review" action stays reachable.
  const stalledDebt = makePr({
    number: 42,
    updatedAt: isoAgo(20 * dayMs),
    lastCommitAt: isoAgo(21 * dayMs),
    review: { state: "reviewed", reviewerCount: 1, commentedReviewCount: 1, lastReviewedAt: isoAgo(20 * dayMs) },
  });
  // Reviewed and idle for only 10 days: stalled but NOT review debt, so the Stalled gate keeps it
  // out of focus (guards against re-flooding the queue with every idle PR).
  const stalledFresh = makePr({
    number: 43,
    updatedAt: isoAgo(10 * dayMs),
    lastCommitAt: isoAgo(11 * dayMs),
    review: { state: "reviewed", reviewerCount: 1, commentedReviewCount: 1, lastReviewedAt: isoAgo(10 * dayMs) },
  });

  // Approved but untouched for 20 days: it lands in "Approved but aging" (a focus-eligible lane),
  // but an approving review already exists, so it is NOT review debt. It must drop out of focus
  // past the 14d window rather than being mislabeled "review debt" / offered "Address review"
  // instead of its land-approval action.
  const approvedAging = makePr({
    number: 44,
    updatedAt: isoAgo(20 * dayMs),
    lastCommitAt: isoAgo(21 * dayMs),
    review: { state: "approved", approvalCount: 1, reviewerCount: 1, lastApprovedAt: isoAgo(20 * dayMs), lastReviewedAt: isoAgo(20 * dayMs) },
  });

  const buckets = createAttentionBuckets([fresh, needsReviewDebt, stalledDebt, stalledFresh, approvedAging]);
  const focus = computeFocusItems(buckets);

  assert.deepEqual(focus.map((i) => i.pullRequest.number).sort((a, b) => a - b), [40, 41, 42]);
  // The Stalled-only debt PR is surfaced on its sole lane.
  assert.equal(focus.find((i) => i.pullRequest.number === 42).bucketLabel, "Stalled");
  // The approved-but-aging PR is excluded: it is aged past the window and no longer review debt.
  assert.equal(focus.find((i) => i.pullRequest.number === 44), undefined);
});

test("isReviewDebt survives display-signal truncation that a pill-derived flag would drop", () => {
  // Regression guard for the review-debt derivation in github.mjs focusCards: the "review debt"
  // pill is emitted last by createAttentionSignals, so a PR stacked with higher-priority danger
  // pills (regression + merge conflicts + release base + CI failing + unresolved) pushes it past the
  // 4-signal card cap. Deriving reviewDebt from those truncated display pills silently loses it and
  // drops the PR from the review-debt spillover, hiding "Address review". The shared predicate does not.
  const debtWithDangerPills = makePr({
    number: 50,
    updatedAt: isoAgo(20 * dayMs),
    mergeableState: "dirty",
    baseRef: "release/9.6",
    labels: ["regression"],
    checks: { state: "failure", failureCount: 2 },
    review: { unresolvedThreadCount: 3 },
  });

  assert.equal(isReviewDebt(debtWithDangerPills), true);

  // signalsFor() caps card pills via createAttentionSignals(...).slice(0, limit) (limit 4); mirror
  // that truncation here to prove the review-debt pill is exactly what falls off the end.
  const displayedPills = createAttentionSignals({ pullRequest: debtWithDangerPills })
    .slice(0, 4)
    .map((s) => s.label);
  assert.ok(
    !displayedPills.includes(reviewDebtSignalLabel),
    `expected review-debt pill to be truncated out of ${JSON.stringify(displayedPills)}`,
  );

  // Approved + fresh PRs are never review debt, so the predicate cannot false-positive.
  const approvedFresh = makePr({
    number: 51,
    updatedAt: isoAgo(dayMs),
    review: { state: "approved", approvalCount: 1, lastApprovedAt: isoAgo(dayMs) },
  });
  assert.equal(isReviewDebt(approvedFresh), false);
});

test("computeCommunityItems includes active external contributors and excludes team, bots, and aged-out PRs", () => {
  const community = makePr({ number: 20, author: "randocontributor", authorType: "User", updatedAt: isoAgo(dayMs) });
  const mine = makePr({ number: 21, author: "octo", isMine: true });
  const bot = makePr({ number: 22, author: "dependabot[bot]", authorType: "Bot" });
  const agedOut = makePr({ number: 23, author: "oldcontributor", authorType: "User", updatedAt: isoAgo(20 * dayMs) });

  const items = computeCommunityItems([community, mine, bot, agedOut]);

  assert.deepEqual(items.map((i) => i.pullRequest.number), [20]);
  assert.equal(items[0].reason, "Community");
});

test("computeFocusExclusionItems explains why my own PRs are outside the focused queue", () => {
  const failing = makePr({ number: 30, author: "octo", isMine: true, checks: { state: "failure", failureCount: 1 } });
  const notMine = makePr({ number: 31, author: "davidfowl", isMine: false, checks: { state: "failure" } });

  assert.deepEqual(computeFocusExclusionItems([failing, notMine], [], [], ""), []);

  const excluded = computeFocusExclusionItems([failing, notMine], [], [], "octo");
  assert.equal(excluded.length, 1);
  assert.equal(excluded[0].pullRequest.number, 30);
  assert.equal(excluded[0].reason.kind, "ci-failing");
});

test("createForMeItems returns personal picks only when a login is supplied", () => {
  const reviewRequested = makePr({ number: 40, author: "davidfowl", isMine: false, review: { state: "waiting", reviewRequestedFromViewer: true } });
  const approvedMine = makePr({ number: 41, author: "octo", isMine: true, review: { state: "approved", approvalCount: 1, lastApprovedAt: isoAgo(dayMs), lastReviewedAt: isoAgo(dayMs) } });

  assert.deepEqual(createForMeItems([reviewRequested, approvedMine], []), []);

  const picks = createForMeItems([reviewRequested, approvedMine], ["octo"]);
  const byNumber = new Map(picks.map((p) => [p.pullRequest.number, p]));
  assert.equal(byNumber.get(40)?.action, personalPickActions.reviewThis);
  assert.equal(byNumber.get(41)?.action, personalPickActions.finishThis);
});

test("createDeveloperPullRequestCounts attributes open PRs to core-team members and honors alias suffixes", () => {
  const prs = [
    makePr({ number: 50, author: "davidfowl" }),
    makePr({ number: 51, author: "davidfowl" }),
    makePr({ number: 52, author: "IEvangelist_microsoft" }),
    makePr({ number: 53, author: "davidfowl", draft: true }),
    makePr({ number: 54, author: "randocontributor", authorType: "User" }),
  ];

  const counts = createDeveloperPullRequestCounts(prs);
  const byActor = new Map(counts.map((c) => [c.actor, c.openPullRequestCount]));

  assert.equal(byActor.get("davidfowl"), 2);
  // The "_microsoft" alias attributes to the canonical core-team login.
  assert.equal(byActor.get("IEvangelist"), 1);
  assert.equal(byActor.has("randocontributor"), false);
});

function isoAgoIssue(ms) {
  return new Date(Date.now() - ms).toISOString();
}

function makeIssue(overrides = {}) {
  return {
    repository: "microsoft/aspire",
    number: 1,
    title: "Test issue",
    url: "https://github.com/microsoft/aspire/issues/1",
    author: "octo",
    authorType: "User",
    authorAvatarUrl: null,
    createdAt: isoAgoIssue(2 * dayMs),
    updatedAt: isoAgoIssue(dayMs),
    milestone: null,
    labels: [],
    assignees: [],
    isMine: false,
    assignedToMe: false,
    ...overrides,
  };
}

test("visibleCheckState and isChecksFailing honor non-blocking check rules", () => {
  const normalFailure = makePr({ repository: "microsoft/aspire", checks: { state: "failure", totalCount: 2, failureCount: 2 } });
  assert.equal(visibleCheckState(normalFailure), "failure");
  assert.equal(isChecksFailing(normalFailure), true);

  // aspire-1p reports a bare aggregate "failure" before per-check detail is fetched. With a
  // non-blocking rule for that repo and zero per-check detail, it is indeterminate, not red.
  const aggregateFailure = makePr({ repository: "devdiv-microsoft/aspire-1p", checks: { state: "failure" } });
  assert.equal(visibleCheckState(aggregateFailure), "unknown");
  assert.equal(isChecksFailing(aggregateFailure), false);

  // A failure whose only failing check matches the rule downgrades to success (nothing pending).
  const nonBlockingOnly = makePr({
    repository: "devdiv-microsoft/aspire-1p",
    checks: { state: "failure", totalCount: 1, failureCount: 1, failingChecks: [{ name: "GitOps/GitHubPop" }] },
  });
  assert.equal(visibleCheckState(nonBlockingOnly), "success");
  assert.equal(isChecksFailing(nonBlockingOnly), false);
});

test("filterCheckFailureRules drops rules missing a repository, label, or concrete matcher", () => {
  const valid = { repository: "devdiv-microsoft/aspire-1p", label: "proof of presence", checkNames: ["GitOps/GitHubPop"], checkNameContains: [] };
  const containsOnly = { repository: "org/repo", label: "informational", checkNames: [], checkNameContains: ["proof of presence"] };
  const noMatchers = { repository: "org/repo", label: "informational", checkNames: [], checkNameContains: [] };
  const noRepo = { repository: "", label: "informational", checkNames: ["x"], checkNameContains: [] };
  const noLabel = { repository: "org/repo", label: "", checkNames: ["x"], checkNameContains: [] };

  // A rule with no concrete matcher would otherwise mark every aggregate failure on its
  // repo non-blocking, hiding all red CI. Only rules that actually name a check survive.
  assert.deepEqual(filterCheckFailureRules([valid, containsOnly, noMatchers, noRepo, noLabel]), [valid, containsOnly]);
  assert.deepEqual(filterCheckFailureRules([]), []);
  assert.deepEqual(filterCheckFailureRules(undefined), []);
});

test("shouldHideFromSharedPullRequestLists hides drafts, conflicts, and needs-author-action PRs", () => {
  assert.equal(shouldHideFromSharedPullRequestLists(makePr()), false);
  assert.equal(shouldHideFromSharedPullRequestLists(makePr({ draft: true })), true);
  assert.equal(shouldHideFromSharedPullRequestLists(makePr({ mergeableState: "dirty" })), true);
  assert.equal(shouldHideFromSharedPullRequestLists(makePr({ labels: ["needs-author-action"] })), true);
});

test("actorIdentityKey attributes a Copilot-authored PR to its human owner", () => {
  assert.equal(actorIdentityKey("davidfowl/copilot"), "davidfowl");
  assert.equal(actorIdentityKey("IEvangelist_microsoft"), "ievangelistmicrosoft");

  const counts = createDeveloperPullRequestCounts([makePr({ number: 60, author: "davidfowl/copilot" })]);
  const byActor = new Map(counts.map((c) => [c.actor, c.openPullRequestCount]));
  assert.equal(byActor.get("davidfowl"), 1);
});

test("createFocusIssueBuckets groups regression, CTI team, afscrome finds, and my issues", () => {
  const regression = makeIssue({ number: 1, labels: ["regression"] });
  const cti = makeIssue({ number: 2, title: "Flaky [aspiree2e] run" });
  const afscrome = makeIssue({ number: 3, author: "afscrome" });
  const mine = makeIssue({ number: 4, assignees: ["octo"] });
  const other = makeIssue({ number: 5 });

  const buckets = createFocusIssueBuckets([regression, cti, afscrome, mine, other], "octo");
  const byLabel = new Map(buckets.map((b) => [b.label, b.issues.map((i) => i.number)]));

  assert.deepEqual(byLabel.get("Regression"), [1]);
  assert.deepEqual(byLabel.get("CTI team"), [2]);
  assert.deepEqual(byLabel.get("afscrome finds"), [3]);
  assert.deepEqual(byLabel.get("My issues"), [4]);
  // Issue 5 matches no focus definition, so no bucket surfaces it.
  assert.equal(buckets.some((b) => b.issues.some((i) => i.number === 5)), false);

  // Without a login the per-viewer "My issues" lane is omitted.
  const withoutLogin = createFocusIssueBuckets([mine], undefined);
  assert.equal(withoutLogin.find((b) => b.label === "My issues"), undefined);
});

test("createIssueSignals leads with the highest-priority action pill", () => {
  const releaseBlocking = makeIssue({ number: 1, labels: ["blocking-release"] });
  assert.equal(createIssueSignals(releaseBlocking)[0].label, "Blocking release");

  const regression = makeIssue({ number: 2, labels: ["regression"] });
  assert.equal(createIssueSignals(regression)[0].label, "Regression");

  const unowned = makeIssue({ number: 3, assignees: [] });
  assert.equal(createIssueSignals(unowned)[0].label, "Unowned");

  const owned = makeIssue({ number: 4, assignees: ["octo"] });
  assert.equal(createIssueSignals(owned)[0].label, "Needs validation");

  // Pills are capped at 7 even with many labels.
  const noisy = makeIssue({ number: 5, milestone: "13.4", labels: ["a", "b", "c", "d", "e"] });
  assert.ok(createIssueSignals(noisy).length <= 7);

  // The precise GraphQL __typename === "Bot" fast-path earns a bot pill even when the
  // login has no "bot" substring (normalizeIssue now carries authorType for issues).
  const botTyped = makeIssue({ number: 6, author: "dependa", authorType: "Bot" });
  assert.ok(createIssueSignals(botTyped).some((s) => s.label === "bot"));
});
