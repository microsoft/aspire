import assert from "node:assert/strict";
import test from "node:test";

import { dayMs, personalPickActions } from "./constants.mjs";
import {
  computeCommunityItems,
  computeFocusExclusionItems,
  computeFocusItems,
  createAttentionBuckets,
  createDeveloperPullRequestCounts,
  createForMeItems,
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
  const checks = { state: "success", failureCount: 0, completedAt: null, ...(overrides.checks ?? {}) };
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
