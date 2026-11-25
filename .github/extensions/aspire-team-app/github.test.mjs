import assert from "node:assert/strict";
import test from "node:test";

import { dayMs } from "./constants.mjs";
import { loadDashboard } from "./github.mjs";

const originalFetch = globalThis.fetch;

test.afterEach(() => {
  globalThis.fetch = originalFetch;
});

test("GitHub connection timeouts explain connectivity failures without hiding other request errors", async () => {
  for (const [error, message] of [
    [new TypeError("fetch failed", { cause: { code: "UND_ERR_CONNECT_TIMEOUT" } }),
      "Connection to GitHub timed out. Check network connectivity and try refreshing again."],
    [new Error("GitHub API 403 Forbidden"), "GitHub API 403 Forbidden"],
  ]) {
    globalThis.fetch = async () => { throw error; };
    const dashboard = await loadReview();
    assert.deepEqual(dashboard.errors, [`microsoft/aspire: ${message}`]);
  }
});

test("GitHub 403 errors preserve the provider message and rate-limit headers without retrying", async () => {
  let calls = 0;
  globalThis.fetch = async () => {
    calls++;
    return {
      ok: false,
      status: 403,
      statusText: "Forbidden",
      headers: new Headers({
        "retry-after": "120",
        "x-ratelimit-remaining": "0",
        "x-ratelimit-reset": "1767225600",
      }),
      json: async () => ({ message: "API rate limit exceeded for this installation." }),
    };
  };
  const dashboard = await loadReview();
  assert.equal(calls, 1);
  assert.deepEqual(dashboard.errors, [
    "microsoft/aspire: GitHub API 403 Forbidden - API rate limit exceeded for this installation.; " +
      "Retry-After: 120; rate limit remaining: 0; rate limit resets: 2026-01-01T00:00:00.000Z",
  ]);
});

test("a forbidden cursor request does not retry the same repository serially", async () => {
  const queries = [];
  globalThis.fetch = async (_url, options) => {
    const body = JSON.parse(options.body);
    queries.push(body.query);
    if (body.query.includes("edges { cursor }")) {
      return {
        ok: false, status: 403, statusText: "Forbidden",
        headers: new Headers({ "retry-after": "120" }),
        json: async () => ({ message: "Secondary rate limit." }),
      };
    }
    return jsonResponse({ data: { repository: { pullRequests: {
      totalCount: 100,
      nodes: [prNode(1, isoAgo(hourMs))],
      pageInfo: { hasNextPage: true, endCursor: "first" },
    } } } });
  };
  const dashboard = await loadReview();
  assert.equal(queries.length, 2);
  assert.deepEqual(dashboard.errors, [
    "microsoft/aspire: GitHub API 403 Forbidden - Secondary rate limit.; Retry-After: 120",
  ]);
});

test("Review and Ship produce both snapshots from one readiness-aware PR load", async () => {
  for (const mode of ["review", "ship"]) {
    let calls = 0;
    globalThis.fetch = async (_url, options) => {
      calls++;
      assert.equal(JSON.parse(options.body).variables.includeReadiness, true);
      return jsonResponse({ data: { repository: { pullRequests: {
        nodes: [prNode(1, isoAgo(hourMs))],
      } } } });
    };
    let related;
    const dashboard = await loadDashboard({
      accounts: [{ token: "token", login: "octo", repos: ["microsoft/aspire"] }],
      mode, release: "9.5", prefs: {}, dismissed: [],
      onRelated: (value) => { related = value; },
    });
    const review = mode === "review" ? dashboard : related;
    const ship = mode === "ship" ? dashboard : related;
    assert.equal(calls, 1);
    assert.equal(review.mode, "review");
    assert.deepEqual(review.attention.focus.map((item) => item.pr.number), [1]);
    assert.equal(ship.mode, "ship");
    assert.deepEqual(ship.lanes.flatMap((lane) => lane.items.map((item) => item.pr.number)), [1]);
    assert.deepEqual(review.errors, []);
    assert.deepEqual(ship.errors, []);
  }
});

test("a cold PR board publishes trailing updates while a repository is still blocked", { timeout: 15_000 }, async (t) => {
  let releaseSlow;
  let signalPartial;
  let signalTrailing;
  const slow = new Promise((resolve) => { releaseSlow = resolve; });
  const firstPartial = new Promise((resolve) => { signalPartial = resolve; });
  const trailingPartial = new Promise((resolve) => { signalTrailing = resolve; });
  t.after(() => releaseSlow());
  globalThis.fetch = async (_url, options) => {
    const { variables } = JSON.parse(options.body);
    if (variables.name === "slow") await slow;
    if (variables.name === "fast2") await new Promise((resolve) => setTimeout(resolve, 10));
    const number = variables.name === "fast" ? 1 : variables.name === "fast2" ? 2 : 3;
    const node = prNode(number, isoAgo(hourMs));
    node.url = `https://github.com/microsoft/${variables.name}/pull/${node.number}`;
    return jsonResponse({ data: { repository: { pullRequests: { nodes: [node] } } } });
  };
  const loading = loadDashboard({
    accounts: [{ token: "token", login: "octo", repos: ["microsoft/fast", "microsoft/fast2", "microsoft/slow"] }],
    mode: "review", prefs: {}, dismissed: [],
    onPartial: (value) => {
      signalPartial(value);
      if (value.counts.total === 2) signalTrailing(value);
    },
  });
  const partial = await firstPartial;
  assert.equal(partial.loading, true);
  assert.deepEqual(partial.attention.focus.map((item) => item.pr.number), [1]);
  const trailing = await trailingPartial;
  assert.deepEqual(trailing.attention.focus.map((item) => item.pr.number), [1, 2]);
  releaseSlow();
  const complete = await loading;
  assert.notEqual(complete.loading, true);
  assert.deepEqual(complete.attention.focus.map((item) => item.pr.number), [1, 2, 3]);
});

test("a hung GitHub request is retried once on a fresh request instead of failing the board", async (t) => {
  const timeout = AbortSignal.timeout.bind(AbortSignal);
  t.mock.method(AbortSignal, "timeout", () => timeout(20));
  // AbortSignal.timeout timers are unref'd; keep the test process alive until the retry settles.
  const keepAlive = setInterval(() => {}, 1000);
  t.after(() => clearInterval(keepAlive));
  let calls = 0;
  globalThis.fetch = async (_url, options) => {
    if (++calls === 1) {
      return new Promise((_, reject) => options.signal.addEventListener("abort", () => reject(options.signal.reason)));
    }
    return jsonResponse({ data: { repository: { pullRequests: { nodes: [prNode(1, isoAgo(hourMs))] } } } });
  };
  const dashboard = await loadReview();
  assert.equal(calls, 2);
  assert.deepEqual(dashboard.errors, []);
  assert.equal(dashboard.counts.total, 1);
});

test("large repositories load later PR pages concurrently and follow a growing tail", async () => {
  // 130 open PRs at 40 per page when the cursor walk runs. The tail grows by 41 items before
  // the last page loads, so that page reports another page that the loader must still follow.
  const nodes = Array.from({ length: 171 }, (_, index) => prNode(index + 1, isoAgo(hourMs)));
  const cursorIndex = (after) => after === null ? -1 : Number(after.slice(1));
  const detailedPages = [];
  let inFlight = 0;
  let maxInFlight = 0;
  globalThis.fetch = async (_url, options) => {
    const { query, variables } = JSON.parse(options.body);
    const start = cursorIndex(variables.after) + 1;
    if (query.includes("edges { cursor }")) {
      const end = Math.min(start + 100, 130);
      return jsonResponse({ data: { repository: { pullRequests: {
        pageInfo: { hasNextPage: end < 130, endCursor: `c${end - 1}` },
        edges: Array.from({ length: end - start }, (_, offset) => ({ cursor: `c${start + offset}` })),
      } } } });
    }
    detailedPages.push(variables.after);
    inFlight++;
    maxInFlight = Math.max(maxInFlight, inFlight);
    await new Promise((resolve) => setTimeout(resolve, 10));
    inFlight--;
    const end = Math.min(start + 40, nodes.length);
    return jsonResponse({ data: { repository: { pullRequests: {
      totalCount: 130,
      nodes: nodes.slice(start, end),
      pageInfo: { hasNextPage: end < nodes.length, endCursor: `c${end - 1}` },
    } } } });
  };
  const dashboard = await loadDashboard({
    accounts: [{ token: "token", login: "octo", repos: ["microsoft/aspire"] }],
    mode: "ship",
    prefs: {},
    dismissed: [],
    showDrafts: true,
  });
  assert.deepEqual(detailedPages.toSorted(), ["c119", "c159", "c39", "c79", null].toSorted());
  assert.ok(maxInFlight > 1, "later pages load concurrently");
  assert.deepEqual(dashboard.errors, []);
  assert.equal(dashboard.counts.total, 171);
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

test("priority label refreshes reorder the full Needs attention queue before its ten-card split", async () => {
  const nodes = Array.from({ length: 12 }, (_, index) => {
    const node = prNode(index + 1, isoAgo(hourMs));
    node.createdAt = isoAgo((15 - index) * dayMs);
    return node;
  });
  mockPullRequests(nodes.toReversed());
  for (const { labels, expected } of [
    { labels: [[], []], expected: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12] },
    { labels: [[], ["FiXeS-CI"]], expected: [12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11] },
    { labels: [["urgent-review"], ["fixes-ci", "urgent-review"]], expected: [11, 12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10] },
    { labels: [["urgent-review"], []], expected: [11, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12] },
    { labels: [[], []], expected: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12] },
  ]) {
    nodes[10].labels.nodes = labels[0].map((name) => ({ name }));
    nodes[11].labels.nodes = labels[1].map((name) => ({ name }));
    const { attention, errors } = await loadReview();
    assert.deepEqual(errors, []);
    assert.deepEqual(attention.focus.map((item) => item.pr.number), expected.slice(0, 10));
    assert.deepEqual(attention.focusRemainder.map((item) => item.pr.number), expected.slice(10));
    assert.deepEqual([...attention.focus, ...attention.focusRemainder].map((item) => item.queuePosition),
      [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
    assert.equal(attention.focusTotal, 12);
  }
});

test("priority labels on a later PR label page reorder the complete queue on refresh", async () => {
  const graphql = "https://ghe.example.com/api/graphql";
  const accounts = [{ token: "enterprise", login: "davidfowl", repos: ["microsoft/aspire"], graphql }];
  const nodes = Array.from({ length: 12 }, (_, index) => {
    const node = prNode(index + 1, isoAgo(hourMs));
    node.createdAt = isoAgo((15 - index) * dayMs);
    return node;
  });
  const ordinaryLabels = Array.from({ length: 15 }, (_, index) => ({ name: `label-${index}` }));
  const continuedLabels = Array.from({ length: 100 }, (_, index) => ({ name: `continued-${index}` }));
  nodes[11].labels = transitionHistory(ordinaryLabels, true, "labels-15");
  let tail = [];
  let requests;
  globalThis.fetch = async (url, options) => {
    assert.equal(url, graphql);
    assert.equal(options.headers.Authorization, "bearer enterprise");
    const { query, variables } = JSON.parse(options.body);
    if (query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { pullRequests: { nodes: nodes.toReversed() } } } });
    }
    requests.push(variables);
    assert.equal(variables.owner, "microsoft");
    assert.equal(variables.name, "aspire");
    assert.equal(variables.number, 12);
    if (variables.after === "labels-15") {
      return jsonResponse({ data: { repository: { pullRequest: {
        labels: transitionHistory(continuedLabels, true, "labels-115"),
      } } } });
    }
    assert.equal(variables.after, "labels-115");
    return jsonResponse({ data: { repository: { pullRequest: {
      labels: transitionHistory(tail.map((name) => ({ name }))),
    } } } });
  };
  for (const labels of [["FiXeS-CI"], ["urgent-review"], []]) {
    tail = labels;
    requests = [];
    const dashboard = await loadReview(accounts);
    assert.deepEqual(dashboard.errors, []);
    const queue = [...dashboard.attention.focus, ...dashboard.attention.focusRemainder];
    const expected = labels.length ? [12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11] : [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
    assert.deepEqual(queue.map((item) => item.pr.number), expected);
    assert.deepEqual(queue.find((item) => item.pr.number === 12).pr.labels,
      [...ordinaryLabels, ...continuedLabels].map((label) => label.name).concat(labels));
    assert.deepEqual(queue.map((item) => item.queuePosition), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
    assert.deepEqual(requests.map((request) => request.after), ["labels-15", "labels-115"]);
  }
});

test("a PR label request failure preserves the PR outside Needs attention and continues pagination", async () => {
  for (const mode of ["review", "ship"]) {
    const nodes = [1, 2, 3, 4].map((number) => prNode(number, isoAgo(hourMs)));
    nodes[1].labels = transitionHistory([{ name: "urgent-review" }], true, "labels-15");
    nodes[1].author.login = "joperezr";
    nodes[1].reviewRequests.nodes = [{ requestedReviewer: { __typename: "User", login: "davidfowl" } }];
    const seenPages = [];
    globalThis.fetch = async (_url, options) => {
      const { query, variables } = JSON.parse(options.body);
      if (query.includes("pullRequests")) {
        seenPages.push(variables.after);
        const connection = variables.after === null
          ? transitionHistory(nodes.slice(0, 3), true, "prs-next")
          : transitionHistory(nodes.slice(3));
        return jsonResponse({ data: { repository: { pullRequests: connection } } });
      }
      assert.equal(variables.number, 2);
      throw new Error("Label access denied");
    };
    const dashboard = await loadDashboard({
      accounts: [{ token: "token", login: "davidfowl", repos: ["microsoft/aspire"] }],
      mode, release: "9.5", prefs: { reviewRequested: true }, dismissed: [],
    });
    const numbers = dashboard.lanes.flatMap((lane) => lane.items.map((item) => item.pr.number));
    assert.deepEqual(numbers.sort((a, b) => a - b), mode === "ship" ? [1, 2, 3, 4] : [1, 3, 4], mode);
    assert.deepEqual(seenPages, [null, "prs-next"], mode);
    assert.deepEqual(dashboard.errors, [
      "microsoft/aspire#2: labels unavailable; excluded from Needs attention. Label access denied",
    ], mode);
    assert.equal(dashboard.counts.total, 4, mode);
    if (mode === "ship") {
      assert.deepEqual(dashboard.notifications.map((item) => item.kind), ["review-requested"]);
      assert.equal(dashboard.notifications[0].number, 2);
    }
    if (mode === "review") {
      assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), [1, 3, 4]);
    }
  }
});

test("incomplete labels do not hide verified author actions outside Needs attention", async () => {
  const node = prNode(1, isoAgo(hourMs));
  node.labels = transitionHistory([{ name: "urgent-review" }], true, "labels-15");
  node.commits.nodes[0].commit.statusCheckRollup.state = "FAILURE";
  globalThis.fetch = async (_url, options) => {
    const { query } = JSON.parse(options.body);
    if (query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { pullRequests: transitionHistory([node]) } } });
    }
    throw new Error("Label page unavailable");
  };

  const dashboard = await loadReview();

  assert.deepEqual(dashboard.attention.focus, []);
  assert.deepEqual(dashboard.attention.forMe.map((item) => [item.pr.number, item.action]), [[1, "Fix CI"]]);
  assert.deepEqual(dashboard.attention.focusExclusions.map((item) => [item.pr.number, item.reason]), [
    [1, "Failing checks keep it out until CI is green again."],
  ]);
});

test("owned drafts with incomplete labels remain in draft buckets without leaking into shared review work", async () => {
  for (const showDrafts of [true, false]) {
    const updatedAt = isoAgo(hourMs);
    const nodes = [1, 2, 3].map((number) => prNode(number, updatedAt));
    nodes[0].isDraft = true;
    nodes[1].isDraft = true;
    nodes[1].author.login = "joperezr";
    nodes[2].author.login = "joperezr";
    nodes[2].reviewRequests.nodes = [{ requestedReviewer: { __typename: "User", login: "davidfowl" } }];
    for (const node of nodes) {
      node.labels = transitionHistory([{ name: "regression" }], true, "labels-15");
    }
    globalThis.fetch = async (_url, options) => {
      const { query } = JSON.parse(options.body);
      if (query.includes("pullRequests")) {
        return jsonResponse({ data: { repository: { pullRequests: transitionHistory(nodes) } } });
      }
      throw new Error("Label page unavailable");
    };

    const dashboard = await loadDashboard({
      accounts: [{ token: "token", login: "davidfowl", repos: ["microsoft/aspire"] }],
      mode: "review", prefs: {}, dismissed: [], showDrafts,
    });

    assert.deepEqual(dashboard.attention.buckets.map((bucket) => [
      bucket.label,
      bucket.items.map((item) => [item.pr.number, item.reason]),
    ]), [["Draft", [[1, "Draft"]]], ["My draft PRs", [[1, "Draft"]]]], `showDrafts=${showDrafts}`);
    assert.deepEqual(dashboard.attention.focus, []);
    assert.deepEqual(dashboard.attention.focusRemainder, []);
    assert.deepEqual(dashboard.attention.forMe, []);
    assert.deepEqual(dashboard.attention.community, []);
    assert.deepEqual(dashboard.lanes.map((lane) => [
      lane.id,
      lane.items.map((item) => item.pr.number),
    ]), showDrafts ? [["your-prs", [1]]] : []);
    assert.deepEqual(dashboard.errors, nodes.map((node) =>
      `microsoft/aspire#${node.number}: labels unavailable; excluded from Needs attention. Label page unavailable`));
  }
});

test("incomplete labels do not turn an explicit review request into a recommendation", async () => {
  const node = prNode(1, isoAgo(hourMs));
  node.author.login = "joperezr";
  node.labels = transitionHistory(
    Array.from({ length: 15 }, (_, index) => ({ name: `other-${index + 1}` })),
    true,
    "labels-15",
  );
  node.reviewRequests.nodes = [{ requestedReviewer: { __typename: "User", login: "davidfowl" } }];
  globalThis.fetch = async (_url, options) => {
    const { query } = JSON.parse(options.body);
    if (query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { pullRequests: transitionHistory([node]) } } });
    }
    throw new Error("Label page unavailable");
  };

  const dashboard = await loadDashboard({
    accounts: [{ token: "token", login: "davidfowl", repos: ["microsoft/aspire"] }],
    mode: "review", prefs: { reviewRequested: true }, dismissed: [],
  });

  assert.deepEqual(dashboard.attention.forMe, []);
  assert.deepEqual(dashboard.notifications.map((item) => item.kind), ["review-requested"]);
});

test("Ship signals retain labels verified before a later label page fails", async () => {
  const node = prNode(1, isoAgo(hourMs));
  node.author.login = "joperezr";
  node.labels = transitionHistory([{ name: "regression" }], true, "labels-15");
  node.reviews.nodes = [{
    state: "APPROVED",
    author: { login: "davidfowl" },
    submittedAt: isoAgo(dayMs),
  }];
  globalThis.fetch = async (_url, options) => {
    const { query } = JSON.parse(options.body);
    if (query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { pullRequests: transitionHistory([node]) } } });
    }
    throw new Error("Label page unavailable");
  };

  const dashboard = await loadDashboard({
    accounts: [{ token: "token", login: "davidfowl", repos: ["microsoft/aspire"] }],
    mode: "ship", release: "9.5", prefs: {}, dismissed: [],
  });

  const item = dashboard.lanes.flatMap((lane) => lane.items).find((candidate) => candidate.pr.number === 1);
  assert.deepEqual(item.pr.labels, ["regression"]);
  assert.ok(item.signals.some((signal) => signal.label === "regression"));
  assert.deepEqual(dashboard.errors, [
    "microsoft/aspire#1: labels unavailable; excluded from Needs attention. Label page unavailable",
  ]);
});

test("Ship signals retain valid labels from an incomplete later label page", async () => {
  for (const secondPage of [
    { labels: transitionHistory([{ name: "regression" }, null]) },
    {
      labels: transitionHistory([{ name: "regression" }]),
      errors: [{
        message: "Could not load a label name",
        path: ["repository", "pullRequest", "labels", "nodes", 1, "name"],
      }],
    },
  ]) {
    const node = prNode(1, isoAgo(hourMs));
    node.author.login = "joperezr";
    node.labels = transitionHistory([{ name: "ordinary" }], true, "labels-15");
    node.reviews.nodes = [{
      state: "APPROVED",
      author: { login: "davidfowl" },
      submittedAt: isoAgo(dayMs),
    }];
    globalThis.fetch = async (_url, options) => {
      const { query } = JSON.parse(options.body);
      if (query.includes("pullRequests")) {
        return jsonResponse({ data: { repository: { pullRequests: transitionHistory([node]) } } });
      }
      return jsonResponse({
        data: { repository: { pullRequest: { labels: secondPage.labels } } },
        errors: secondPage.errors,
      });
    };

    const dashboard = await loadDashboard({
      accounts: [{ token: "token", login: "davidfowl", repos: ["microsoft/aspire"] }],
      mode: "ship", release: "9.5", prefs: {}, dismissed: [],
    });

    const item = dashboard.lanes.flatMap((lane) => lane.items).find((candidate) => candidate.pr.number === 1);
    assert.ok(item.pr.labels.includes("regression"));
    assert.ok(item.signals.some((signal) => signal.label === "regression"));
    assert.ok(dashboard.errors.some((error) => error.includes("labels unavailable")));
  }
});

test("an initial PR label GraphQL error is isolated while later PRs and pages load", async () => {
  const nodes = [1, 2, 3].map((number) => prNode(number, isoAgo(hourMs)));
  nodes[0].labels = null;
  const seenPages = [];
  globalThis.fetch = async (_url, options) => {
    const { query, variables } = JSON.parse(options.body);
    assert.ok(query.includes("pullRequests"));
    seenPages.push(variables.after);
    if (variables.after === null) {
      return jsonResponse({
        data: { repository: { isPrivate: false, pullRequests: transitionHistory(nodes.slice(0, 2), true, "prs-next") } },
        errors: [{
          message: "Initial label connection unavailable",
          path: ["repository", "pullRequests", "nodes", 0, "labels", "pageInfo"],
        }],
      });
    }
    return jsonResponse({
      data: { repository: { isPrivate: false, pullRequests: transitionHistory(nodes.slice(2)) } },
    });
  };

  const dashboard = await loadReview();

  assert.deepEqual(seenPages, [null, "prs-next"]);
  assert.deepEqual(dashboard.errors, [
    "microsoft/aspire#1: labels unavailable; excluded from Needs attention. Initial label connection unavailable",
  ]);
  assert.equal(dashboard.counts.total, 3);
  assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), [2, 3]);
});

test("an error-only PR-list GraphQL response preserves GitHub's error message", async () => {
  const seenPages = [];
  globalThis.fetch = async (_url, options) => {
    const { query, variables } = JSON.parse(options.body);
    assert.ok(query.includes("pullRequests"));
    seenPages.push(variables.after);
    return jsonResponse({ data: null, errors: [{ message: "API rate limit exceeded" }] });
  };

  const dashboard = await loadReview();

  assert.deepEqual(seenPages, [null]);
  assert.deepEqual(dashboard.errors, ["microsoft/aspire: API rate limit exceeded"]);
  assert.deepEqual(dashboard.attention.focus, []);
});

test("multiple initial PR label GraphQL field errors retain whole messages for each PR", async () => {
  const updatedAt = isoAgo(hourMs);
  const nodes = [1, 2, 3].map((number) => prNode(number, updatedAt));
  nodes[0].labels = null;
  nodes[1].labels = null;
  globalThis.fetch = async () => jsonResponse({
    data: { repository: { pullRequests: transitionHistory(nodes) } },
    errors: [
      { message: "Label names unavailable", path: ["repository", "pullRequests", "nodes", 0, "labels", "nodes"] },
      { message: "Label cursors unavailable", path: ["repository", "pullRequests", "nodes", 0, "labels", "pageInfo"] },
      { message: "Other PR labels unavailable", path: ["repository", "pullRequests", "nodes", 1, "labels"] },
    ],
  });

  const dashboard = await loadReview();

  assert.deepEqual(dashboard.errors, [
    "microsoft/aspire#1: labels unavailable; excluded from Needs attention. Label names unavailable; Label cursors unavailable",
    "microsoft/aspire#2: labels unavailable; excluded from Needs attention. Other PR labels unavailable",
  ]);
  assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), [3]);
});

test("Your PRs outside Needs attention retains an author response with incomplete labels", async () => {
  const node = prNode(1, isoAgo(hourMs));
  node.commits.nodes[0].commit.committedDate = isoAgo(dayMs);
  node.reviews.nodes = [{
    state: "CHANGES_REQUESTED",
    author: { login: "joperezr" },
    submittedAt: isoAgo(hourMs / 2),
  }];
  node.labels = transitionHistory([{ name: "ordinary" }], true, "labels-15");
  globalThis.fetch = async (_url, options) => {
    const { query } = JSON.parse(options.body);
    if (query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { pullRequests: transitionHistory([node]) } } });
    }
    throw new Error("Label page unavailable");
  };

  const dashboard = await loadReview();

  assert.deepEqual(dashboard.attention.forMe.map((item) => item.action), ["Respond here"]);
  assert.deepEqual(dashboard.attention.focusExclusions.map((item) => [
    item.pr.number,
    item.reason,
  ]), [[1, "Changes were requested, so this is waiting on the author rather than the focused queue."]]);
});

test("Your PRs outside Needs attention explains an incomplete label result without another blocker", async () => {
  const node = prNode(1, isoAgo(hourMs));
  node.labels = transitionHistory([{ name: "ordinary" }], true, "labels-15");
  globalThis.fetch = async (_url, options) => {
    const { query } = JSON.parse(options.body);
    if (query.includes("pullRequests")) {
      return jsonResponse({ data: { repository: { pullRequests: transitionHistory([node]) } } });
    }
    throw new Error("Label page unavailable");
  };

  const dashboard = await loadReview();

  assert.deepEqual(dashboard.attention.focusExclusions.map((item) => [
    item.pr.number,
    item.signals[0].label,
    item.reason,
  ]), [[1, "Labels unavailable", "Labels could not be loaded, so this PR is not ranked in Needs attention."]]);
});

test("a duplicate-account success supersedes a PR label failure in either completion order", async () => {
  for (const mode of ["review", "ship"]) {
    for (const firstToken of ["missing", "complete"]) {
      let firstFinished;
      const firstDone = new Promise((resolve) => { firstFinished = resolve; });
      const accounts = ["missing", "complete"].map((token) => ({ token, login: "davidfowl", repos: ["microsoft/aspire"] }));
      globalThis.fetch = async (_url, options) => {
        const { query } = JSON.parse(options.body);
        const token = options.headers.Authorization.slice("bearer ".length);
        if (query.includes("pullRequests")) {
          if (token !== firstToken) await firstDone;
          const node = prNode(1, isoAgo(hourMs));
          node.labels = transitionHistory([], true, "labels-15");
          return jsonResponse({ data: { repository: { pullRequests: transitionHistory([node]) } } });
        }
        if (token === "missing") throw new Error("Label access denied");
        return jsonResponse({ data: { repository: { pullRequest: {
          labels: transitionHistory([{ name: "urgent-review" }]),
        } } } });
      };
      const dashboard = await loadDashboard({
        accounts, mode, release: "9.5", prefs: {}, dismissed: [],
        onProgress: ({ done }) => { if (done === 1) firstFinished(); },
      });
      assert.deepEqual(dashboard.errors, [], `${mode}: ${firstToken} finishes first`);
      const items = dashboard.lanes.flatMap((lane) => lane.items);
      assert.deepEqual(items.map((item) => item.pr.number), [1]);
      assert.deepEqual(items[0].pr.labels, ["urgent-review"]);
    }
  }
});

test("duplicate accounts combine complete labels and first-readiness history independently", async () => {
  const createdAt = isoAgo(5 * dayMs);
  const readyAt = isoAgo(4 * dayMs);
  const draftAt = isoAgo(3 * dayMs);
  for (const firstToken of ["labels", "readiness"]) {
    let firstFinished;
    const firstDone = new Promise((resolve) => { firstFinished = resolve; });
    const accounts = ["labels", "readiness"].map((token) => ({ token, login: "davidfowl", repos: ["microsoft/aspire"] }));
    const node = prNode(1, isoAgo(hourMs));
    node.createdAt = createdAt;
    node.readinessTransitions = transitionHistory([], true, "readiness-20");
    globalThis.fetch = async (_url, options) => {
      const { query } = JSON.parse(options.body);
      const token = options.headers.Authorization.slice("bearer ".length);
      if (query.includes("pullRequests")) {
        if (token !== firstToken) await firstDone;
        const accountNode = structuredClone(node);
        accountNode.isDraft = token === "readiness";
        accountNode.labels = token === "labels"
          ? transitionHistory([{ name: "fixes-ci" }])
          : transitionHistory([{ name: "ordinary" }], true, "labels-15");
        return jsonResponse({ data: { repository: { pullRequests: transitionHistory([accountNode]) } } });
      }
      if (query.includes("labels(first:100")) throw new Error("Label page unavailable");
      if (query.includes("readinessTransitions")) {
        if (token === "labels") throw new Error("Readiness history unavailable");
        return jsonResponse({ data: { repository: { pullRequest: {
          readinessTransitions: transitionHistory([
            { __typename: "ReadyForReviewEvent", createdAt: readyAt },
            { __typename: "ConvertToDraftEvent", createdAt: draftAt },
          ]),
        } } } });
      }
      throw new Error(`Unexpected GraphQL query: ${query.slice(0, 80)}`);
    };

    const dashboard = await loadDashboard({
      accounts, mode: "review", prefs: {}, dismissed: [],
      onProgress: ({ done }) => { if (done === 1) firstFinished(); },
    });

    assert.deepEqual(dashboard.errors, [], `${firstToken} finishes first`);
    assert.deepEqual(dashboard.attention.focus, [], `${firstToken} finishes first`);
    assert.deepEqual(dashboard.attention.focusRemainder, [], `${firstToken} finishes first`);
    const myDraftPr = dashboard.attention.buckets.find((bucket) => bucket.label === "My draft PRs")?.items[0]?.pr;
    assert.ok(myDraftPr, `${firstToken} finishes first`);
    assert.deepEqual(myDraftPr.labels, ["fixes-ci"], `${firstToken} finishes first`);
    assert.equal(myDraftPr.draft, true, `${firstToken} finishes first`);
    assert.equal(myDraftPr.firstReadyForReviewAt, readyAt, `${firstToken} finishes first`);
  }
});

test("duplicate incomplete label results merge verified Ship signals in either completion order", async () => {
  for (const firstToken of ["base", "richer"]) {
    let firstFinished;
    const firstDone = new Promise((resolve) => { firstFinished = resolve; });
    const accounts = ["base", "richer"].map((token) => ({ token, login: "davidfowl", repos: ["microsoft/aspire"] }));
    const node = prNode(1, isoAgo(hourMs));
    node.author.login = "joperezr";
    node.reviews.nodes = [{
      state: "APPROVED",
      author: { login: "davidfowl" },
      submittedAt: isoAgo(dayMs),
    }];
    globalThis.fetch = async (_url, options) => {
      const { query } = JSON.parse(options.body);
      const token = options.headers.Authorization.slice("bearer ".length);
      if (query.includes("pullRequests")) {
        if (token !== firstToken) await firstDone;
        const accountNode = structuredClone(node);
        accountNode.labels = transitionHistory(
          [{ name: token === "base" ? "ordinary" : "another-ordinary" }],
          true,
          "labels-15",
        );
        return jsonResponse({ data: { repository: { pullRequests: transitionHistory([accountNode]) } } });
      }
      if (query.includes("labels(first:100")) {
        if (token === "base") throw new Error("Label page unavailable");
        return jsonResponse({
          data: { repository: { pullRequest: {
            labels: transitionHistory([{ name: "regression" }]),
          } } },
          errors: [{
            message: "Partial label data",
            path: ["repository", "pullRequest", "labels", "nodes", 1, "name"],
          }],
        });
      }
      throw new Error(`Unexpected GraphQL query: ${query.slice(0, 80)}`);
    };

    const dashboard = await loadDashboard({
      accounts, mode: "ship", release: "9.5", prefs: {}, dismissed: [],
      onProgress: ({ done }) => { if (done === 1) firstFinished(); },
    });

    const item = dashboard.lanes.flatMap((lane) => lane.items).find((candidate) => candidate.pr.number === 1);
    assert.ok(item.pr.labels.includes("ordinary"), `${firstToken} finishes first`);
    assert.ok(item.pr.labels.includes("another-ordinary"), `${firstToken} finishes first`);
    assert.ok(item.pr.labels.includes("regression"), `${firstToken} finishes first`);
    assert.equal(item.pr.labelsUnavailable, true, `${firstToken} finishes first`);
    assert.ok(item.signals.some((signal) => signal.label === "regression"), `${firstToken} finishes first`);
  }
});

test("a later PR-list page failure still reports a repository-level error", async () => {
  const seenPages = [];
  globalThis.fetch = async (_url, options) => {
    const { query, variables } = JSON.parse(options.body);
    assert.ok(query.includes("pullRequests"));
    seenPages.push(variables.after);
    if (variables.after !== null) throw new Error("PR list access denied");
    return jsonResponse({ data: { repository: {
      pullRequests: transitionHistory([prNode(1, isoAgo(hourMs))], true, "prs-next"),
    } } });
  };
  const dashboard = await loadReview();
  assert.deepEqual(seenPages, [null, "prs-next"]);
  assert.deepEqual(dashboard.errors, ["microsoft/aspire: PR list access denied"]);
  assert.deepEqual(dashboard.attention.focus.map((item) => item.pr.number), [1]);
});

test("malformed PR label connections are reported without ranking a partial label set", async () => {
  for (const labels of [
    undefined,
    { nodes: [] },
    { nodes: [], pageInfo: { hasNextPage: "false" } },
    transitionHistory(null),
    transitionHistory([null]),
    transitionHistory([{ name: 42 }]),
  ]) {
    const node = prNode(1, isoAgo(hourMs));
    node.labels = labels;
    mockPullRequests([node]);
    const dashboard = await loadReview();
    assert.deepEqual(dashboard.errors, ["microsoft/aspire#1: labels unavailable; excluded from Needs attention. Missing or malformed pull-request labels for #1"]);
    assert.equal(dashboard.counts.total, 1);
    assert.deepEqual(dashboard.attention.focus, []);
  }
});

test("PR label pagination failures and cursor safety guards never accept partial labels", async () => {
  for (const failure of ["request", "missing-page", "missing-cursor", "invalid-cursor", "cursor", "limit"]) {
    const node = prNode(1, isoAgo(hourMs));
    node.labels = transitionHistory([{ name: "urgent-review" }], true,
      failure === "missing-cursor" ? null : failure === "invalid-cursor" ? 42 : "labels-0");
    let calls = 0;
    globalThis.fetch = async (_url, options) => {
      const { query } = JSON.parse(options.body);
      if (query.includes("pullRequests")) {
        return jsonResponse({ data: { repository: { pullRequests: { nodes: [node] } } } });
      }
      calls++;
      if (failure === "request") throw new Error("Label access denied");
      const labels = failure === "missing-page" ? null :
        transitionHistory([], true, failure === "cursor" ? "labels-0" : `labels-${calls}`);
      return jsonResponse({ data: { repository: { pullRequest: { labels } } } });
    };
    const dashboard = await loadReview();
    const error = failure === "request" ? "Label access denied" :
      failure === "missing-page" ? "Missing or malformed pull-request labels for #1" :
        "Incomplete pull-request labels for #1";
    assert.deepEqual(dashboard.errors, [`microsoft/aspire#1: labels unavailable; excluded from Needs attention. ${error}`]);
    assert.equal(dashboard.counts.total, 1);
    assert.deepEqual(dashboard.attention.focus, []);
    assert.equal(calls, failure === "limit" ? 24 : ["missing-cursor", "invalid-cursor"].includes(failure) ? 0 : 1);
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
    labels: transitionHistory([]),
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
