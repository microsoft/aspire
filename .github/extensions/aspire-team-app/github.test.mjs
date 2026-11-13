import assert from "node:assert/strict";
import test from "node:test";

import { dayMs } from "./constants.mjs";
import { loadDashboard } from "./github.mjs";

const originalFetch = globalThis.fetch;

test.afterEach(() => {
  globalThis.fetch = originalFetch;
});

test("Needs attention serializes a strict ten-card prefix and the complete deduplicated remainder", async () => {
  for (const count of [0, 4, 10, 11, 25]) {
    const createdAt = isoAgo(3 * dayMs);
    const nodes = Array.from({ length: count }, (_, index) => {
      const node = prNode(index + 1, isoAgo(hourMs));
      node.createdAt = createdAt;
      return node;
    });
    // Equal readiness timestamps and reversed responses exercise the ordinal PR-number tie
    // across the split; duplicate accounts must not duplicate either side.
    mockPullRequests(nodes.toReversed());
    const dashboard = JSON.parse(JSON.stringify(await loadDashboard({
      accounts: [
        { token: "first", login: "davidfowl", repos: ["microsoft/aspire"] },
        { token: "second", login: "joperezr", repos: ["microsoft/aspire"] },
      ],
      mode: "review", prefs: { reviewLimit: 50 }, dismissed: [], reviewLimit: 50,
    })));
    const { focus, focusRemainder, focusTotal, focusLimit } = dashboard.attention;
    const positions = Array.from({ length: count }, (_, index) => index + 1);
    assert.deepEqual(dashboard.errors, []);
    assert.equal(dashboard.reviewLimit, 10);
    assert.equal(focusLimit, 10);
    assert.equal(focusTotal, count);
    assert.deepEqual(focus.map((item) => item.queuePosition), positions.slice(0, 10));
    assert.deepEqual(focusRemainder.map((item) => item.queuePosition), positions.slice(10));
    assert.deepEqual([...focus, ...focusRemainder].map((item) => item.pr.number), positions);
  }
});

test("review debt stays eligible in the remainder without spilling into the top ten or losing non-debt PRs", async () => {
  const nodes = Array.from({ length: 16 }, (_, index) => {
    const node = prNode(index + 1, isoAgo(index < 13 ? 20 * dayMs : hourMs));
    node.createdAt = isoAgo((index < 13 ? 40 - index : 16 - index) * dayMs);
    return node;
  });
  mockPullRequests(nodes.toReversed());
  const { attention } = await loadReview();
  assert.deepEqual(attention.focus.map((item) => item.pr.number), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  assert.deepEqual(attention.focusRemainder.map((item) => [item.pr.number, item.queuePosition, item.reviewDebt]), [
    [11, 11, true], [12, 12, true], [13, 13, true],
    [14, 14, false], [15, 15, false], [16, 16, false],
  ]);
  assert.equal(attention.focusTotal, 16);
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

test("Needs attention orders by first readiness, distinguishing PRs opened ready from drafts", async () => {
  const openedReady = prNode(1, isoAgo(hourMs));
  openedReady.createdAt = isoAgo(10 * dayMs);
  openedReady.readinessTransitions = transitionHistory([
    { __typename: "ConvertToDraftEvent", createdAt: isoAgo(8 * dayMs) },
    { __typename: "ReadyForReviewEvent", createdAt: isoAgo(3 * dayMs) },
  ]);
  const initiallyDraft = prNode(2, isoAgo(2 * hourMs), isoAgo(hourMs));
  initiallyDraft.createdAt = isoAgo(20 * dayMs);
  initiallyDraft.readinessTransitions = transitionHistory([
    { __typename: "ReadyForReviewEvent", createdAt: isoAgo(5 * dayMs) },
    { __typename: "ConvertToDraftEvent", createdAt: isoAgo(2 * dayMs) },
    { __typename: "ReadyForReviewEvent", createdAt: initiallyDraft.readyForReviewEvents.nodes[0].createdAt },
  ]);
  const alwaysReady = prNode(3, isoAgo(3 * hourMs));
  alwaysReady.createdAt = isoAgo(6 * dayMs);
  const neverReady = prNode(4, isoAgo(4 * hourMs));
  neverReady.isDraft = true;

  mockPullRequests([initiallyDraft, alwaysReady, neverReady, openedReady]);
  const dashboard = await loadReview();

  assert.deepEqual(dashboard.errors, []);
  assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), [1, 3, 2]);
  assert.deepEqual(dashboard.attention.focus.map((item) => item.queuePosition), [1, 2, 3]);
  assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.firstReadyForReviewAt), [
    openedReady.createdAt,
    alwaysReady.createdAt,
    initiallyDraft.readinessTransitions.nodes[0].createdAt,
  ]);
  assert.equal(dashboard.attention.focus[2].pr.readyForReviewAt, initiallyDraft.readyForReviewEvents.nodes[0].createdAt);
  const draft = dashboard.attention.buckets.find((bucket) => bucket.label === "Draft").items[0].pr;
  assert.equal(draft.firstReadyForReviewAt, null);
});

test("Needs attention paginates draft and ready transitions on the account's GitHub host", async () => {
  const nodes = [prNode(1, isoAgo(hourMs)), prNode(2, isoAgo(2 * hourMs))];
  const histories = [
    [
      { __typename: "ConvertToDraftEvent", createdAt: isoAgo(5 * dayMs) },
      { __typename: "ReadyForReviewEvent", createdAt: isoAgo(4 * dayMs) },
      { __typename: "ConvertToDraftEvent", createdAt: isoAgo(3 * dayMs) },
      { __typename: "ReadyForReviewEvent", createdAt: isoAgo(2 * dayMs) },
    ],
    [
      { __typename: "ReadyForReviewEvent", createdAt: isoAgo(5 * dayMs) },
      { __typename: "ConvertToDraftEvent", createdAt: isoAgo(4 * dayMs) },
      { __typename: "ReadyForReviewEvent", createdAt: isoAgo(3 * dayMs) },
    ],
  ];
  for (const [index, node] of nodes.entries()) {
    node.readinessTransitions = transitionHistory(histories[index].slice(0, 2), true, `history-${node.number}`);
  }
  const requests = [];
  const graphql = "https://ghe.example.com/api/graphql";
  globalThis.fetch = async (url, options) => {
    assert.equal(url, graphql);
    const body = JSON.parse(options.body);
    requests.push(body.variables);
    if (body.query.includes("pullRequests")) {
      assert.equal(body.variables.includeReadiness, true);
      assert.match(body.query, /readinessTransitions:\s*timelineItems\(first:20, itemTypes:\[CONVERT_TO_DRAFT_EVENT, READY_FOR_REVIEW_EVENT\]\)/);
      return jsonResponse({ data: { repository: { pullRequests: { nodes } } } });
    }
    assert.equal(body.variables.after, `history-${body.variables.number}`);
    return jsonResponse({ data: { repository: { pullRequest: {
      readinessTransitions: transitionHistory(histories[body.variables.number - 1].slice(2)),
    } } } });
  };

  const dashboard = await loadReview([{ token: "enterprise", login: "davidfowl", repos: ["microsoft/aspire"], graphql }]);

  assert.deepEqual(dashboard.errors, []);
  assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.firstReadyForReviewAt), [
    nodes[0].createdAt,
    histories[1][0].createdAt,
  ]);
  assert.deepEqual(requests.map((variables) => variables.after ?? null), [null, "history-1", "history-2"]);
});

test("Needs attention keeps first-readiness order across human and Copilot review rounds and other activity", async () => {
  const older = prNode(1, isoAgo(2 * dayMs), isoAgo(5 * dayMs));
  const newer = prNode(2, isoAgo(dayMs));
  newer.createdAt = isoAgo(3 * dayMs);
  const reviews = [
    [],
    [{ state: "COMMENTED", author: { login: "copilot-pull-request-reviewer[bot]" }, submittedAt: isoAgo(hourMs) }],
    [{ state: "COMMENTED", author: { login: "human-reviewer" }, submittedAt: isoAgo(hourMs) }],
    [{ state: "APPROVED", author: { login: "human-reviewer" }, submittedAt: isoAgo(hourMs) }],
    [{ state: "CHANGES_REQUESTED", author: { login: "human-reviewer" }, submittedAt: isoAgo(hourMs) }],
  ];
  for (const history of reviews) {
    older.updatedAt = new Date().toISOString();
    older.labels.nodes = [{ name: "enhancement" }];
    older.reviews.nodes = history;
    older.commits.nodes[0].commit.committedDate = new Date().toISOString();
    mockPullRequests([newer, older]);
    const dashboard = await loadReview();
    assert.deepEqual(dashboard.errors, []);
    assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), [1, 2]);
    assert.equal(dashboard.attention.focus[0].pr.firstReadyForReviewAt, older.readyForReviewEvents.nodes[0].createdAt);
  }
});

test("Needs attention retains the same relative order across repository fetch completion and watched scopes", async () => {
  const createdAt = isoAgo(3 * dayMs);
  const first = prNode(10, isoAgo(hourMs));
  const second = prNode(2, isoAgo(2 * hourMs));
  const otherRepo = prNode(1, isoAgo(3 * hourMs));
  otherRepo.url = "https://github.com/microsoft/dcp/pull/1";
  for (const node of [first, second, otherRepo]) node.createdAt = createdAt;
  for (const slowRepo of ["aspire", "dcp"]) {
    globalThis.fetch = async (_url, options) => {
      const { variables } = JSON.parse(options.body);
      if (variables.name === slowRepo) await new Promise((resolve) => setTimeout(resolve, 10));
      const nodes = variables.name === "aspire" ? [first, second] : [otherRepo];
      return jsonResponse({ data: { repository: { pullRequests: { nodes } } } });
    };
    const dashboard = await loadReview([{ token: "token", login: "davidfowl", repos: ["microsoft/dcp", "microsoft/aspire"] }]);
    assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.url), [second.url, first.url, otherRepo.url]);
    const personalScope = await loadReview([{ token: "token", login: "davidfowl", repos: ["microsoft/aspire"] }]);
    assert.deepEqual(personalScope.repos, ["microsoft/aspire"]);
    assert.deepEqual(personalScope.attention.focus.map((item) => item.pr.url), [second.url, first.url]);
  }
});

test("missing or inconsistent readiness history surfaces an error without guessing or hiding other lanes", async () => {
  const cases = [
    { history: undefined, error: "Missing draft/ready transition history" },
    { history: transitionHistory([], true, null), error: "Incomplete draft/ready transition history" },
    { history: transitionHistory([{ __typename: "ReadyForReviewEvent", createdAt: "invalid" }]), error: "Inconsistent draft/ready transition history" },
    { history: transitionHistory([{ __typename: "UnknownEvent", createdAt: isoAgo(dayMs) }]), error: "Inconsistent draft/ready transition history" },
    { history: transitionHistory([{ __typename: "ConvertToDraftEvent", createdAt: isoAgo(dayMs) }]), error: "Draft state changed while loading transition history; refresh to retry" },
  ];
  for (const { history, error } of cases) {
    const unknown = prNode(1, isoAgo(hourMs));
    unknown.readinessTransitions = history;
    mockPullRequests([unknown, prNode(2, isoAgo(dayMs))]);
    const dashboard = await loadReview();
    assert.deepEqual(dashboard.errors, [`microsoft/aspire#1: first readiness unavailable; omitted from Needs attention. ${error}`]);
    assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), [2]);
    const unranked = dashboard.attention.buckets.find((bucket) => bucket.label === "Quick wins").items.find((item) => item.pr.number === 1);
    assert.equal(unranked.pr.firstReadyForReviewAt, null);
  }
});

test("readiness pagination failures, repeated cursors, and the safety bound are reported", async () => {
  for (const failure of ["request", "cursor", "limit"]) {
    let calls = 0;
    const node = prNode(1, isoAgo(hourMs));
    node.readinessTransitions = transitionHistory([], true, "history-0");
    globalThis.fetch = async (_url, options) => {
      const body = JSON.parse(options.body);
      if (body.query.includes("pullRequests")) {
        return jsonResponse({ data: { repository: { pullRequests: { nodes: [node] } } } });
      }
      calls++;
      if (failure === "request") throw new Error("Timeline access denied");
      const cursor = failure === "cursor" ? "history-0" : `history-${calls}`;
      return jsonResponse({ data: { repository: { pullRequest: {
        readinessTransitions: transitionHistory([], true, cursor),
      } } } });
    };
    const dashboard = await loadReview();
    const error = failure === "request" ? "Timeline access denied" : "Incomplete draft/ready transition history";
    assert.deepEqual(dashboard.errors, [`microsoft/aspire#1: first readiness unavailable; omitted from Needs attention. ${error}`]);
    assert.deepEqual(dashboard.attention.focus, []);
    assert.equal(calls, failure === "limit" ? 24 : 1);
  }
});

test("another active account can supply unavailable first-readiness history", async () => {
  for (const isDraft of [false, true]) {
    globalThis.fetch = async (_url, options) => {
      const node = prNode(1, isoAgo(hourMs));
      node.isDraft = isDraft;
      if (options.headers.Authorization === "bearer missing") node.readinessTransitions = null;
      return jsonResponse({ data: { repository: { pullRequests: { nodes: [node] } } } });
    };
    const dashboard = await loadReview([
      { token: "missing", login: "davidfowl", repos: ["microsoft/aspire"] },
      { token: "complete", login: "joperezr", repos: ["microsoft/aspire"] },
    ]);
    assert.deepEqual(dashboard.errors, []);
    assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), isDraft ? [] : [1]);
  }
});

test("loadDashboard attributes Copilot PRs only to their sole human assignee", async () => {
  const cases = [
    { author: "copilot-swe-agent", assignee: "octocat", expected: "octocat/copilot" },
    { author: "copilot-swe-agent[bot]", assignee: "octocat", expected: "octocat/copilot" },
    { author: "copilot-swe-agent", assignee: "copilot-swe-agent", expected: "copilot-swe-agent" },
  ];

  for (const { author, assignee, expected } of cases) {
    const copilotPr = prNode(19893, new Date().toISOString());
    copilotPr.author = { __typename: "Bot", login: author, avatarUrl: null };
    copilotPr.assignees.nodes = [{ login: assignee }];

    globalThis.fetch = async (_url, options = {}) => {
      const body = JSON.parse(options.body);
      if (body.query.includes("pullRequests")) {
        return jsonResponse({ data: { repository: {
          isPrivate: false,
          pullRequests: {
            nodes: [copilotPr],
            pageInfo: { hasNextPage: false, endCursor: null },
          },
        } } });
      }
      throw new Error(`Unexpected query: ${body.query}`);
    };

    const dashboard = await loadDashboard({
      accounts: [{ token: "token", login: "octocat", repos: ["microsoft/aspire"] }],
      mode: "review",
      release: "9.5",
      prefs: {},
      dismissed: [],
    });

    const pullRequests = dashboard.attention.buckets.flatMap((bucket) => bucket.items.map((item) => item.pr));
    const attributed = pullRequests.find((pr) => pr.number === copilotPr.number);
    assert.equal(attributed.author, expected);
  }
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

const hourMs = dayMs / 24;

function transitionHistory(nodes, hasNextPage = false, endCursor = null) {
  return { nodes, pageInfo: { hasNextPage, endCursor } };
}

function mockPullRequests(nodes) {
  globalThis.fetch = async (_url, options) => {
    const body = JSON.parse(options.body);
    assert.equal(body.variables.includeReadiness, true);
    return jsonResponse({ data: { repository: { pullRequests: { nodes } } } });
  };
}

function loadReview(accounts = [{ token: "token", login: "davidfowl", repos: ["microsoft/aspire"] }]) {
  return loadDashboard({ accounts, mode: "review", prefs: {}, dismissed: [] });
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
    author: { __typename: "User", login: "davidfowl", avatarUrl: null },
    baseRefName: "main",
    mergeable: "MERGEABLE",
    reviewDecision: null,
    readyForReviewEvents: { nodes: readyForReviewAt ? [{ createdAt: readyForReviewAt }] : [] },
    readinessTransitions: transitionHistory(readyForReviewAt ? [{ __typename: "ReadyForReviewEvent", createdAt: readyForReviewAt }] : []),
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
