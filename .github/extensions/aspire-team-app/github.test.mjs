import assert from "node:assert/strict";
import test from "node:test";

import { dayMs } from "./constants.mjs";
import { capFocusKeepingDebt, loadDashboard } from "./github.mjs";

const originalFetch = globalThis.fetch;

test.afterEach(() => {
  globalThis.fetch = originalFetch;
});

test("capFocusKeepingDebt keeps the head cap plus review-debt cards that spill past it", () => {
  const cards = [
    { pr: { number: 1 }, reviewDebt: false },
    { pr: { number: 2 }, reviewDebt: false },
    { pr: { number: 3 }, reviewDebt: false },
    { pr: { number: 4 }, reviewDebt: true },
    { pr: { number: 5 }, reviewDebt: false },
    { pr: { number: 6 }, reviewDebt: true },
  ];

  const kept = capFocusKeepingDebt(cards, 2);

  // First 2 (the actionable headline) plus the two review-debt cards beyond the cap; the
  // non-debt spillover (#3, #5) is dropped, and no card is duplicated.
  assert.deepEqual(kept.map((c) => c.pr.number), [1, 2, 4, 6]);

  // A debt card already inside the cap is not re-added.
  const debtInHead = capFocusKeepingDebt([{ pr: { number: 1 }, reviewDebt: true }, { pr: { number: 2 }, reviewDebt: false }], 2);
  assert.deepEqual(debtInHead.map((c) => c.pr.number), [1, 2]);

  // No spillover at all when everything fits under the cap.
  assert.deepEqual(capFocusKeepingDebt(cards.slice(0, 2), 5).map((c) => c.pr.number), [1, 2]);
});

test("loadDashboard paginates open pull requests for each watched repo", async () => {
  const seenAfter = [];
  globalThis.fetch = async (_url, options = {}) => {
    const body = JSON.parse(options.body);
    seenAfter.push(body.variables.after ?? null);
    if (body.query.includes("pullRequests")) {
      assert.match(body.query, /readyForReviewEvents:\s*timelineItems\(last:1, itemTypes:\[READY_FOR_REVIEW_EVENT\]\)/);
      return jsonResponse({ data: { repository: { isPrivate: false, pullRequests: page(
        body.variables.after,
        prNode(1, "2026-07-01T10:00:00Z", "2026-07-01T09:30:00Z"),
        prNode(2, "2026-07-01T11:00:00Z"),
      ) } } });
    }
    throw new Error(`Unexpected query: ${body.query}`);
  };

  const dashboard = await loadDashboard({
    accounts: [{ token: "token", login: "octo", repos: ["microsoft/aspire"] }],
    mode: "ship",
    release: "9.5",
    prefs: {},
    dismissed: [],
    showDrafts: true,
  });

  assert.deepEqual(seenAfter, [null, "cursor-1"]);
  assert.equal(dashboard.counts.total, 2);
  assert.deepEqual(dashboard.lanes.flatMap((lane) => lane.items.map((item) => item.pr.number)).sort((a, b) => a - b), [1, 2]);
  assert.equal(
    dashboard.lanes.flatMap((lane) => lane.items).find((item) => item.pr.number === 1).pr.readyForReviewAt,
    "2026-07-01T09:30:00Z",
  );
});

test("loadDashboard ranks review-ready PRs by time waiting for review", async () => {
  const oldDraft = prNode(1, new Date().toISOString(), isoAgo(dayMs));
  oldDraft.createdAt = isoAgo(28 * dayMs);
  oldDraft.author.login = "old-draft-author";

  const longerWait = prNode(2, new Date().toISOString());
  longerWait.createdAt = isoAgo(3 * dayMs);
  longerWait.author.login = "longer-wait-author";

  globalThis.fetch = async (_url, options = {}) => {
    const body = JSON.parse(options.body);
    if (body.query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { isPrivate: false, pullRequests: {
        nodes: [oldDraft, longerWait],
        pageInfo: { hasNextPage: false, endCursor: null },
      } } } });
    }
    throw new Error(`Unexpected query: ${body.query}`);
  };

  const dashboard = await loadDashboard({
    accounts: [{ token: "token", login: "octo", repos: ["microsoft/aspire"] }],
    mode: "review",
    release: "9.5",
    prefs: {},
    dismissed: [],
  });

  const reviewQueue = dashboard.lanes.find((lane) => lane.id === "review-queue");
  assert.deepEqual(reviewQueue.items.map((item) => item.pr.number), [2, 1]);
});

test("loadDashboard paginates open issues for each watched repo", async () => {
  const seenAfter = [];
  globalThis.fetch = async (_url, options = {}) => {
    const body = JSON.parse(options.body);
    seenAfter.push(body.variables.after ?? null);
    if (body.query.includes("issues")) {
      assert.match(body.query, /closedByPullRequestsReferences\(first:5, includeClosedPrs:true\)/);
      return jsonResponse({ data: { repository: { issues: page(
        body.variables.after,
        issueNode(1, "2026-07-01T10:00:00Z", [{
          number: 10,
          title: "Fix issue 1",
          url: "https://github.com/microsoft/aspire/pull/10",
          state: "OPEN",
          repository: { nameWithOwner: "microsoft/aspire" },
        }, {
          number: 11,
          title: "Merged fix for issue 1",
          url: "https://github.com/microsoft/aspire/pull/11",
          state: "MERGED",
          repository: { nameWithOwner: "microsoft/aspire" },
        }, {
          number: 12,
          title: "Abandoned fix for issue 1",
          url: "https://github.com/microsoft/aspire/pull/12",
          state: "CLOSED",
          repository: { nameWithOwner: "microsoft/aspire" },
        }]),
        issueNode(2, "2026-07-01T11:00:00Z"),
      ) } } });
    }
    throw new Error(`Unexpected query: ${body.query}`);
  };

  const dashboard = await loadDashboard({
    accounts: [{ token: "token", login: "octo", repos: ["microsoft/aspire"] }],
    mode: "issues",
    release: "9.5",
    prefs: {},
    dismissed: [],
  });

  assert.deepEqual(seenAfter, [null, "cursor-1"]);
  assert.equal(dashboard.counts.issues, 2);
  assert.deepEqual(dashboard.lanes.flatMap((lane) => lane.items.map((item) => item.issue.number)).sort((a, b) => a - b), [1, 2]);
  const issue = dashboard.lanes.flatMap((lane) => lane.items).find((item) => item.issue.number === 1).issue;
  assert.deepEqual(issue.linkedPullRequests, [{
    repository: "microsoft/aspire",
    number: 10,
    title: "Fix issue 1",
    url: "https://github.com/microsoft/aspire/pull/10",
    state: "OPEN",
  }, {
    repository: "microsoft/aspire",
    number: 11,
    title: "Merged fix for issue 1",
    url: "https://github.com/microsoft/aspire/pull/11",
    state: "MERGED",
  }]);
});

test("loadDashboard reports fetch progress and returns one complete snapshot", async () => {
  globalThis.fetch = async (_url, options = {}) => {
    const body = JSON.parse(options.body);
    return jsonResponse({ data: { repository: { isPrivate: false, pullRequests: {
      nodes: [prNode(1, "2026-07-01T10:00:00Z")],
      pageInfo: { hasNextPage: false, endCursor: null },
    } } } });
  };

  const progress = [];
  const dashboard = await loadDashboard({
    accounts: [{ token: "token", login: "octo", repos: ["microsoft/aspire", "microsoft/aspire.dev"] }],
    mode: "ship",
    release: "9.5",
    prefs: {},
    dismissed: [],
    showDrafts: true,
    onProgress: (p) => progress.push(p),
  });

  // Two (account, repo) jobs → total of 2, ending with an authoritative done tick.
  assert.equal(progress.at(-1).total, 2);
  assert.equal(progress.at(-1).done, 2);
  assert.equal(progress.at(-1).phase, "done");
  assert.ok(progress.some((p) => p.phase === "fetch"), "expected at least one fetch-phase tick");
  assert.equal(dashboard.counts.total, 1);
});

test("loadDashboard surfaces a repo error on one host even when the same slug succeeds on another host", async () => {
  // The same owner/repo slug can exist on github.com and on a GHES/EMU host at once. Here the
  // dotcom account reads "org/repo" fine while the enterprise account fails on its own "org/repo".
  globalThis.fetch = async (url, options = {}) => {
    const body = JSON.parse(options.body);
    if (String(url).includes("ghe.example.com")) {
      throw new Error("GHES boom");
    }
    return jsonResponse({ data: { repository: { isPrivate: false, pullRequests: {
      nodes: [prNode(1, "2026-07-01T10:00:00Z")],
      pageInfo: { hasNextPage: false, endCursor: null },
    } } } });
  };

  const dashboard = await loadDashboard({
    accounts: [
      { token: "dotcom", login: "octo", repos: ["org/repo"] },
      { token: "ghes", login: "octo-ghe", repos: ["org/repo"], graphql: "https://ghe.example.com/api/graphql" },
    ],
    mode: "ship",
    release: "9.5",
    prefs: {},
    dismissed: [],
    showDrafts: true,
  });

  // The dotcom success must NOT suppress the enterprise host's failure for the same slug — errors
  // are keyed by (host, repo), so the GHES error is surfaced instead of silently dropping its PRs.
  assert.ok(
    dashboard.errors.some((m) => m.includes("org/repo") && m.includes("GHES boom")),
    `expected a surfaced GHES error, got ${JSON.stringify(dashboard.errors)}`,
  );
});

function page(after, firstNode, secondNode) {
  if (after == null) {
    return { nodes: [firstNode], pageInfo: { hasNextPage: true, endCursor: "cursor-1" } };
  }
  assert.equal(after, "cursor-1");
  return { nodes: [secondNode], pageInfo: { hasNextPage: false, endCursor: null } };
}

function isoAgo(ms) {
  return new Date(Date.now() - ms).toISOString();
}

function prNode(number, updatedAt, readyForReviewAt = null) {
  return {
    number,
    title: `PR ${number}`,
    url: `https://github.com/microsoft/aspire/pull/${number}`,
    isDraft: false,
    state: "OPEN",
    createdAt: "2026-07-01T09:00:00Z",
    updatedAt,
    author: { __typename: "User", login: "octo", avatarUrl: null },
    baseRefName: "main",
    mergeable: "MERGEABLE",
    reviewDecision: null,
    readyForReviewEvents: { nodes: readyForReviewAt ? [{ createdAt: readyForReviewAt }] : [] },
    additions: 1,
    deletions: 0,
    changedFiles: 1,
    milestone: { title: "9.5" },
    labels: { nodes: [] },
    assignees: { nodes: [] },
    reviewRequests: { nodes: [] },
    reviews: { nodes: [] },
    reviewThreads: { nodes: [] },
    commits: { totalCount: 1, nodes: [{ commit: { committedDate: updatedAt, statusCheckRollup: { state: "SUCCESS" } } }] },
    closingIssuesReferences: { nodes: [] },
  };
}

function issueNode(number, updatedAt, linkedPullRequests = []) {
  return {
    number,
    title: `Issue ${number}`,
    url: `https://github.com/microsoft/aspire/issues/${number}`,
    createdAt: "2026-07-01T09:00:00Z",
    updatedAt,
    author: { __typename: "User", login: "octo", avatarUrl: null },
    milestone: null,
    labels: { nodes: [] },
    assignees: { nodes: [] },
    closedByPullRequestsReferences: { nodes: linkedPullRequests },
  };
}

function jsonResponse(body, options = {}) {
  return {
    ok: options.ok ?? true,
    status: options.status ?? 200,
    statusText: options.statusText ?? "OK",
    json: async () => body,
  };
}
