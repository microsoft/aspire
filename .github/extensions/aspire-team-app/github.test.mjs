import assert from "node:assert/strict";
import test from "node:test";

import { loadDashboard } from "./github.mjs";

const originalFetch = globalThis.fetch;

test.afterEach(() => {
  globalThis.fetch = originalFetch;
});

test("loadDashboard paginates open pull requests for each watched repo", async () => {
  const seenAfter = [];
  globalThis.fetch = async (_url, options = {}) => {
    const body = JSON.parse(options.body);
    seenAfter.push(body.variables.after ?? null);
    if (body.query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { isPrivate: false, pullRequests: page(
        body.variables.after,
        prNode(1, "2026-07-01T10:00:00Z"),
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
});

test("loadDashboard paginates open issues for each watched repo", async () => {
  const seenAfter = [];
  globalThis.fetch = async (_url, options = {}) => {
    const body = JSON.parse(options.body);
    seenAfter.push(body.variables.after ?? null);
    if (body.query.includes("issues")) {
      return jsonResponse({ data: { repository: { issues: page(
        body.variables.after,
        issueNode(1, "2026-07-01T10:00:00Z"),
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
});

function page(after, firstNode, secondNode) {
  if (after == null) {
    return { nodes: [firstNode], pageInfo: { hasNextPage: true, endCursor: "cursor-1" } };
  }
  assert.equal(after, "cursor-1");
  return { nodes: [secondNode], pageInfo: { hasNextPage: false, endCursor: null } };
}

function prNode(number, updatedAt) {
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

function issueNode(number, updatedAt) {
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
