import assert from "node:assert/strict";
import test from "node:test";
import { mock } from "node:test";

import {
  GITHUB_URL,
  MIRROR_ID,
  MIRROR_URL,
  loadMirrorObservation,
  updateMirrorState,
} from "./mirror.mjs";

const source = "a".repeat(40);
const mirror = "b".repeat(40);
const merge = "c".repeat(40);
const side = "d".repeat(40);
const older = "e".repeat(40);

test("exports stable mirror identifiers", () => {
  assert.equal(MIRROR_ID, "mirror:microsoft/aspire:main");
  assert.equal(MIRROR_URL, "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire");
  assert.equal(GITHUB_URL, "https://github.com/microsoft/aspire");
});

test("loadMirrorObservation returns synced when heads match without history requests", async () => {
  const requests = [];
  const observation = await loadMirrorObservation({
    token: "secret-token",
    now: date("2026-09-17T12:00:00Z"),
    runAz: azHead(source),
    fetchImpl: githubMock(requests, {
      head: source,
    }),
  });

  assert.equal(observation.status, "synced");
  assert.equal(observation.sourceSha, source);
  assert.equal(observation.mirrorSha, source);
  assert.equal(observation.missingCount, 0);
  assert.equal(observation.oldestSha, null);
  assert.equal(observation.landedAt, null);
  assert.deepEqual(paths(requests), ["/repos/microsoft/aspire/git/ref/heads/main"]);
});

test("loadMirrorObservation finds oldest missing first-parent commit and ignores old side branch commits", async () => {
  const requests = [];
  const observation = await loadMirrorObservation({
    token: "secret-token",
    now: date("2026-09-17T12:00:00Z"),
    runAz: azHead(mirror),
    fetchImpl: githubMock(requests, {
      head: source,
      compare: { status: "ahead", ahead_by: 42, html_url: "https://github.com/microsoft/aspire/compare/base...head" },
      commits: new Map([
        [source, [merge, side]],
        [merge, [mirror, older]],
      ]),
      pulls: new Map([
        [merge, [{
          merge_commit_sha: merge,
          merged_at: "2026-09-17T06:00:00Z",
          base: { repo: { full_name: "microsoft/aspire" }, ref: "main" },
        }]],
      ]),
    }),
  });

  assert.equal(observation.status, "behind");
  assert.equal(observation.missingCount, 42);
  assert.equal(observation.oldestSha, merge);
  assert.equal(observation.landedAt, "2026-09-17T06:00:00.000Z");
  assert.ok(paths(requests).includes(`/repos/microsoft/aspire/commits/${source}`));
  assert.ok(paths(requests).includes(`/repos/microsoft/aspire/commits/${merge}`));
  assert.ok(!paths(requests).includes(`/repos/microsoft/aspire/commits/${side}`));
});

test("loadMirrorObservation detects partial catch-up to a later first-parent commit", async () => {
  const observation = await loadMirrorObservation({
    now: date("2026-09-17T12:00:00Z"),
    runAz: azHead(merge),
    fetchImpl: githubMock([], {
      head: source,
      compare: { status: "ahead", ahead_by: 1 },
      commits: new Map([[source, [merge]]]),
      pulls: new Map([[source, []]]),
    }),
  });

  assert.equal(observation.status, "behind");
  assert.equal(observation.oldestSha, source);
  assert.equal(observation.landedAt, null);
});

test("loadMirrorObservation returns diverged only for confirmed compare divergence", async () => {
  const observation = await loadMirrorObservation({
    now: date("2026-09-17T12:00:00Z"),
    runAz: azHead(mirror),
    fetchImpl: githubMock([], {
      head: source,
      compare: { status: "diverged", ahead_by: 2 },
    }),
  });

  assert.equal(observation.status, "diverged");
  assert.equal(observation.missingCount, null);
  assert.equal(observation.oldestSha, null);
});

test("loadMirrorObservation does not treat unknown compare SHAs as divergence", async () => {
  await assert.rejects(
    loadMirrorObservation({
      now: date("2026-09-17T12:00:00Z"),
      runAz: azHead(mirror),
      fetchImpl: githubMock([], {
        head: source,
        compareStatus: 404,
      }),
    }),
    /ancestry is unknown/,
  );
});

test("loadMirrorObservation fails explicitly when first-parent bound is exceeded", async () => {
  const commits = new Map();
  let current = source;
  for (let i = 0; i < 501; i++) {
    const parent = `${String(i).padStart(40, "0")}`;
    commits.set(current, [parent]);
    current = parent;
  }

  await assert.rejects(
    loadMirrorObservation({
      now: date("2026-09-17T12:00:00Z"),
      runAz: azHead(mirror),
      fetchImpl: githubMock([], {
        head: source,
        compare: { status: "ahead", ahead_by: 300 },
        commits,
      }),
    }),
    /Unable to establish/,
  );
});

test("loadMirrorObservation enforces an overall GitHub observation deadline", async () => {
  const requests = [];
  const clock = mock.method(Date, "now", (() => {
    let value = date("2026-09-17T12:00:00Z").getTime();
    return () => {
      value += 30_001;
      return value;
    };
  })());

  try {
    await assert.rejects(
      loadMirrorObservation({
        now: date("2026-09-17T12:00:00Z"),
        runAz: azHead(mirror),
        fetchImpl: githubMock(requests, {
          head: source,
          compare: { status: "ahead", ahead_by: 10 },
          commits: new Map([
            [source, [merge]],
            [merge, [older]],
            [older, [side]],
            [side, ["f".repeat(40)]],
          ]),
        }),
      }),
      /Mirror observation timed out before GitHub ancestry could be established/,
    );
  } finally {
    clock.mock.restore();
  }

  assert.ok(requests.length < 10);
});

test("loadMirrorObservation ignores unreliable merge metadata and commit dates", async () => {
  const observation = await loadMirrorObservation({
    now: date("2026-09-17T12:00:00Z"),
    runAz: azHead(mirror),
    fetchImpl: githubMock([], {
      head: source,
      compare: { status: "ahead", ahead_by: 1 },
      commits: new Map([[source, [mirror]]]),
      pulls: new Map([
        [source, [
          {
            merge_commit_sha: source,
            merged_at: "2026-09-18T00:00:00Z",
            base: { repo: { full_name: "microsoft/aspire" }, ref: "main" },
          },
          {
            merge_commit_sha: source,
            merged_at: "2026-09-17T06:00:00Z",
            base: { repo: { full_name: "fork/aspire" }, ref: "main" },
          },
          {
            merge_commit_sha: mirror,
            merged_at: "2026-09-17T06:00:00Z",
            base: { repo: { full_name: "microsoft/aspire" }, ref: "main" },
          },
        ]],
      ]),
    }),
  });

  assert.equal(observation.oldestSha, source);
  assert.equal(observation.landedAt, null);
});

test("loadMirrorObservation throws sanitized actionable errors for invalid responses and auth failures", async () => {
  await assert.rejects(
    loadMirrorObservation({
      runAz: azHead("not-a-sha"),
      fetchImpl: githubMock([], { head: source }),
    }),
    /Azure DevOps mirror ref did not include a valid 40-character SHA/,
  );

  await assert.rejects(
    loadMirrorObservation({
      runAz: azHead(mirror),
      fetchImpl: githubMock([], { headStatus: 401 }),
    }),
    (error) => {
      assert.match(error.message, /Check the GitHub token permissions/);
      assert.doesNotMatch(error.message, /secret-token|raw response body/i);
      return true;
    },
  );

  await assert.rejects(
    loadMirrorObservation({
      runAz: async () => {
        const error = new Error("raw secret body");
        error.code = "azdo_auth_required";
        throw error;
      },
      fetchImpl: githubMock([], { head: source }),
    }),
    (error) => {
      assert.match(error.message, /Run az login or set AZURE_DEVOPS_EXT_PAT/);
      assert.doesNotMatch(error.message, /raw secret body/);
      return true;
    },
  );
});

test("updateMirrorState uses threshold boundaries and emits no repeat notifications after restart", () => {
  const now = date("2026-09-17T12:00:00Z");
  const warningObservation = observation({
    landedAt: "2026-09-17T06:00:00Z",
    checkedAt: now.toISOString(),
  });
  const warning = updateMirrorState(null, warningObservation, now);

  assert.equal(warning.level, "warning");
  assert.equal(warning.notifiedLevel, "warning");
  assert.equal(warning.notifications.length, 1);
  assert.deepEqual(Object.keys(warning.notifications[0]).sort(), ["at", "id", "level"]);

  const restarted = JSON.parse(JSON.stringify(warning));
  const repeated = updateMirrorState(restarted, warningObservation, date("2026-09-17T12:30:00Z"));

  assert.equal(repeated.level, "warning");
  assert.equal(repeated.notifications.length, 1);

  const critical = updateMirrorState(repeated, warningObservation, date("2026-09-18T06:00:00Z"));
  assert.equal(critical.level, "critical");
  assert.equal(critical.notifiedLevel, "critical");
  assert.deepEqual(critical.notifications.map((item) => item.level), ["critical", "warning"]);
  assert.deepEqual(Object.keys(critical.notifications[0]).sort(), ["at", "id", "level"]);
});

test("updateMirrorState ages from first observation when landedAt is unavailable", () => {
  const first = updateMirrorState(null, observation({ landedAt: null }), date("2026-09-17T00:00:00Z"));
  assert.equal(first.level, "catching-up");
  assert.equal(first.firstObservedAt, "2026-09-17T00:00:00.000Z");

  const warning = updateMirrorState(first, observation({ landedAt: null }), date("2026-09-17T06:00:00Z"));
  assert.equal(warning.level, "warning");
  assert.equal(warning.firstObservedAt, first.firstObservedAt);
});

test("updateMirrorState preserves age across source head movement and resets when oldest advances", () => {
  const first = updateMirrorState(null, observation({ sourceSha: source }), date("2026-09-17T00:00:00Z"));
  const movedHead = updateMirrorState(first, observation({ sourceSha: "f".repeat(40) }), date("2026-09-17T01:00:00Z"));
  assert.equal(movedHead.firstObservedAt, first.firstObservedAt);
  assert.equal(movedHead.incidentId, first.incidentId);

  const advanced = updateMirrorState(movedHead, observation({ oldestSha: source }), date("2026-09-17T02:00:00Z"));
  assert.equal(advanced.firstObservedAt, "2026-09-17T02:00:00.000Z");
  assert.equal(advanced.incidentId, first.incidentId);
});

test("updateMirrorState does not repeat warning notifications for partial catch-up in the same incident", () => {
  const warned = updateMirrorState(null, observation({ landedAt: "2026-09-17T00:00:00Z" }), date("2026-09-17T06:00:00Z"));
  const partiallyCaughtUp = updateMirrorState(warned, observation({
    oldestSha: source,
    landedAt: "2026-09-17T01:00:00Z",
  }), date("2026-09-17T07:00:00Z"));

  assert.equal(partiallyCaughtUp.level, "warning");
  assert.equal(partiallyCaughtUp.firstObservedAt, "2026-09-17T07:00:00.000Z");
  assert.equal(partiallyCaughtUp.incidentId, warned.incidentId);
  assert.equal(partiallyCaughtUp.notifiedLevel, "warning");
  assert.deepEqual(partiallyCaughtUp.notifications.map((item) => item.level), ["warning"]);
});

test("updateMirrorState emits one recovery after a warned incident syncs", () => {
  const warned = updateMirrorState(null, observation({ landedAt: "2026-09-17T00:00:00Z" }), date("2026-09-17T06:00:00Z"));
  const synced = updateMirrorState(warned, {
    status: "synced",
    sourceSha: source,
    mirrorSha: source,
    missingCount: 0,
    oldestSha: null,
    landedAt: null,
    checkedAt: "2026-09-17T07:00:00.000Z",
    compareUrl: `${GITHUB_URL}/commit/${source}`,
  }, date("2026-09-17T07:00:00Z"));

  assert.equal(synced.level, "synced");
  assert.equal(synced.notifiedLevel, "recovery");
  assert.deepEqual(synced.notifications.map((item) => item.level), ["recovery", "warning"]);

  const repeated = updateMirrorState(synced, synced.observation, date("2026-09-17T07:30:00Z"));
  assert.deepEqual(repeated.notifications.map((item) => item.level), ["recovery", "warning"]);
});

test("updateMirrorState recovers from parent-retained unknown error state", () => {
  const warned = updateMirrorState(null, observation({ landedAt: "2026-09-17T00:00:00Z" }), date("2026-09-17T06:00:00Z"));
  const unknown = {
    ...warned,
    level: "unknown",
    error: "GitHub compare request failed with HTTP 503.",
    checkedAt: "2026-09-17T06:30:00.000Z",
    lastSuccessAt: warned.lastSuccessAt,
  };

  const critical = updateMirrorState(unknown, observation({ landedAt: "2026-09-17T00:00:00Z" }), date("2026-09-18T00:00:00Z"));
  assert.equal(critical.level, "critical");
  assert.equal(critical.firstObservedAt, warned.firstObservedAt);
  assert.equal(critical.incidentId, warned.incidentId);
  assert.deepEqual(critical.notifications.map((item) => item.level), ["critical", "warning"]);

  const synced = updateMirrorState(critical, {
    status: "synced",
    sourceSha: source,
    mirrorSha: source,
    missingCount: 0,
    oldestSha: null,
    landedAt: null,
    checkedAt: "2026-09-18T01:00:00.000Z",
    compareUrl: null,
  }, date("2026-09-18T01:00:00Z"));
  assert.equal(synced.level, "synced");
  assert.deepEqual(synced.notifications.map((item) => item.level), ["recovery", "critical", "warning"]);
  assert.deepEqual(Object.keys(synced.notifications[0]).sort(), ["at", "id", "level"]);
});

test("updateMirrorState bounds the notification list", () => {
  const previous = {
    notifications: Array.from({ length: 25 }, (_, i) => ({ id: `old-${i}`, level: "warning" })),
  };
  const state = updateMirrorState(previous, observation({ landedAt: "2026-09-17T00:00:00Z" }), date("2026-09-17T06:00:00Z"));

  assert.equal(state.notifications.length, 20);
  assert.equal(state.notifications[0].level, "warning");
});

function observation(overrides = {}) {
  return {
    status: "behind",
    sourceSha: source,
    mirrorSha: mirror,
    missingCount: 1,
    oldestSha: merge,
    landedAt: null,
    checkedAt: "2026-09-17T00:00:00.000Z",
    compareUrl: `${GITHUB_URL}/compare/${mirror}...${source}`,
    ...overrides,
  };
}

function azHead(sha) {
  return async (args) => {
    assert.deepEqual(args.slice(0, 3), ["repos", "ref", "list"]);
    assert.ok(args.includes("--organization"));
    assert.ok(args.includes("--project"));
    assert.ok(args.includes("--repository"));
    return [{ name: "refs/heads/main", objectId: sha }];
  };
}

function githubMock(requests, options) {
  return async (url, init) => {
    assert.equal(init.method, "GET");
    assert.ok(init.signal);
    if (init.headers.Authorization) assert.equal(init.headers.Authorization, "Bearer secret-token");
    const parsed = new URL(url);
    requests.push({ path: parsed.pathname, search: parsed.search });

    if (parsed.pathname.endsWith("/git/ref/heads/main")) {
      if (options.headStatus) return jsonResponse({ message: "raw response body" }, { status: options.headStatus });
      return jsonResponse({ object: { sha: options.head } });
    }

    if (parsed.pathname.includes("/compare/")) {
      if (options.compareStatus) return jsonResponse({ message: "raw response body" }, { status: options.compareStatus });
      assert.equal(parsed.searchParams.get("per_page"), "1");
      return jsonResponse(options.compare ?? { status: "identical", ahead_by: 0 });
    }

    const commitMatch = parsed.pathname.match(/^\/repos\/microsoft\/aspire\/commits\/([0-9a-f]{40})(\/pulls)?$/);
    assert.ok(commitMatch, `Unexpected GitHub path ${parsed.pathname}`);
    const sha = commitMatch[1];
    if (commitMatch[2]) {
      assert.equal(parsed.searchParams.get("per_page"), "10");
      return jsonResponse(options.pulls?.get(sha) ?? []);
    }

    const parents = options.commits?.get(sha);
    if (!parents) return jsonResponse({ sha, parents: [] });
    return jsonResponse({ sha, parents: parents.map((parent) => ({ sha: parent })) });
  };
}

function paths(requests) {
  return requests.map((request) => request.path);
}

function jsonResponse(body, options = {}) {
  const status = options.status ?? 200;
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Error",
    json: async () => body,
  };
}

function date(value) {
  return new Date(value);
}
