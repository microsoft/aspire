import assert from "node:assert/strict";
import test from "node:test";

import { hourMs, SLA_REPOS } from "./constants.mjs";
import {
  addBusinessMs,
  annotateDashboardSla,
  businessMsBetween,
  isSlaCandidatePr,
  isSlaRepo,
  reconcileTracking,
  slaAnchors,
  slaCandidateKey,
  slaState,
  wallToInstant,
} from "./sla.mjs";

const TZ = "America/Los_Angeles";

// Resolve a Pacific wall-clock time to an instant for readable expectations. This is the
// same primitive addBusinessMs is built on, so comparing instants keeps the tests exact
// across DST without hardcoding UTC offsets.
function pt(y, mo, d, h, mi = 0) {
  return wallToInstant(y, mo, d, h, mi, TZ);
}

test("addBusinessMs: whole budget inside one workday", () => {
  // Mon 2025-01-06 09:00 PT + 8h = Mon 17:00 PT (a full business day).
  const start = pt(2025, 1, 6, 9);
  assert.equal(addBusinessMs(start, 8 * hourMs, TZ), pt(2025, 1, 6, 17));
  assert.equal(addBusinessMs(start, 4 * hourMs, TZ), pt(2025, 1, 6, 13));
});

test("addBusinessMs: rolls onto the next workday when the day runs out", () => {
  // Mon 15:00 has 2h left; +4h consumes 2h Mon then 2h Tue 09:00-11:00.
  const start = pt(2025, 1, 6, 15);
  assert.equal(addBusinessMs(start, 4 * hourMs, TZ), pt(2025, 1, 7, 11));
});

test("addBusinessMs: clamps a pre-work start up to 09:00", () => {
  // Mon 06:00 (before work) + 8h should behave like starting at 09:00 => Mon 17:00.
  assert.equal(addBusinessMs(pt(2025, 1, 6, 6), 8 * hourMs, TZ), pt(2025, 1, 6, 17));
});

test("addBusinessMs: an after-hours start rolls to the next morning", () => {
  // Mon 20:00 + 2h => Tue 09:00-11:00.
  assert.equal(addBusinessMs(pt(2025, 1, 6, 20), 2 * hourMs, TZ), pt(2025, 1, 7, 11));
});

test("addBusinessMs: skips the weekend", () => {
  // Fri 2025-01-10 15:00 + 4h => 2h Fri, skip Sat/Sun, 2h Mon 09:00-11:00.
  assert.equal(addBusinessMs(pt(2025, 1, 10, 15), 4 * hourMs, TZ), pt(2025, 1, 13, 11));
});

test("addBusinessMs: a weekend start begins Monday morning", () => {
  // Sat 2025-01-11 12:00 + 8h => the clock only starts Mon 09:00 => Mon 17:00.
  assert.equal(addBusinessMs(pt(2025, 1, 11, 12), 8 * hourMs, TZ), pt(2025, 1, 13, 17));
});

test("addBusinessMs: weekend spanning the spring-forward DST transition", () => {
  // US DST 2025 begins Sun 2025-03-09. Fri 2025-03-07 15:00 PST + 4h => 2h Fri, skip the
  // weekend (incl. the transition), 2h Mon 09:00-11:00 PDT. Comparing wall-clock instants
  // proves the result is 11:00 *local* on Monday regardless of the offset change.
  assert.equal(addBusinessMs(pt(2025, 3, 7, 15), 4 * hourMs, TZ), pt(2025, 3, 10, 11));
});

test("businessMsBetween: only counts in-window business time", () => {
  // Mon 15:00 -> Tue 11:00 spans 2h Mon + overnight (0) + 2h Tue = 4 business hours.
  assert.equal(businessMsBetween(pt(2025, 1, 6, 15), pt(2025, 1, 7, 11), TZ), 4 * hourMs);
  // Fri 16:00 -> Mon 10:00 = 1h Fri + weekend (0) + 1h Mon = 2 business hours.
  assert.equal(businessMsBetween(pt(2025, 1, 10, 16), pt(2025, 1, 13, 10), TZ), 2 * hourMs);
  assert.equal(businessMsBetween(pt(2025, 1, 6, 12), pt(2025, 1, 6, 12), TZ), 0);
});

test("slaAnchors: warn and deadline are stable and correctly spaced", () => {
  const first = new Date(pt(2025, 1, 6, 9)).toISOString();
  const a = slaAnchors(first, { tz: TZ, budgetHours: 8, warnHours: 6 });
  assert.equal(a.firstQualifiedAt, first);
  assert.equal(new Date(a.warnAt).getTime(), pt(2025, 1, 6, 15)); // +6h
  assert.equal(new Date(a.deadlineAt).getTime(), pt(2025, 1, 6, 17)); // +8h
  // Recomputing from the same input yields identical anchors (no churn).
  const b = slaAnchors(first, { tz: TZ, budgetHours: 8, warnHours: 6 });
  assert.deepEqual(a, b);
});

test("slaState: flips only at the warn and deadline thresholds", () => {
  const first = new Date(pt(2025, 1, 6, 9)).toISOString();
  const a = slaAnchors(first, { tz: TZ, budgetHours: 8, warnHours: 6 });
  const warn = new Date(a.warnAt).getTime();
  const deadline = new Date(a.deadlineAt).getTime();
  assert.equal(slaState(a, warn - 1), "ok");
  assert.equal(slaState(a, warn), "approaching");
  assert.equal(slaState(a, deadline - 1), "approaching");
  assert.equal(slaState(a, deadline), "breached");
  assert.equal(slaState(a, deadline + hourMs), "breached");
});

test("isSlaRepo: case-insensitive match against the configured repos", () => {
  assert.equal(isSlaRepo("devdiv-microsoft/aspire-1p"), true);
  assert.equal(isSlaRepo("DevDiv-Microsoft/Aspire-1p"), true);
  assert.equal(isSlaRepo("microsoft/aspire"), false);
  assert.equal(isSlaRepo(""), false);
  assert.equal(isSlaRepo(null), false);
});

test("slaCandidateKey: lowercased repo plus number", () => {
  assert.equal(slaCandidateKey({ repository: "DevDiv-Microsoft/Aspire-1p", number: 42 }), "devdiv-microsoft/aspire-1p#42");
});

function candidatePr(overrides = {}) {
  return {
    repository: "devdiv-microsoft/aspire-1p",
    number: 1,
    author: "external-dev",
    review: { reviewerCount: 0 },
    ...overrides,
  };
}

test("isSlaCandidatePr: only non-team, un-reviewed PRs on an SLA repo qualify", () => {
  assert.equal(isSlaCandidatePr(candidatePr()), true);
  // A core-team author is exempt (they are not the review target).
  assert.equal(isSlaCandidatePr(candidatePr({ author: "joperezr" })), false);
  // A core-team enterprise alias is exempt too.
  assert.equal(isSlaCandidatePr(candidatePr({ author: "joperezr_microsoft" })), false);
  // A listed EMU login whose base differs from the public login is exempt via the allowlist.
  assert.equal(isSlaCandidatePr(candidatePr({ author: "dapine_microsoft" })), false);
  // An UNLISTED "*_microsoft" account is a review target — the suffix alone is not core team.
  assert.equal(isSlaCandidatePr(candidatePr({ author: "andreas_microsoft" })), true);
  // Any human review stops the clock.
  assert.equal(isSlaCandidatePr(candidatePr({ review: { reviewerCount: 1 } })), false);
  // Repos outside the SLA never qualify.
  assert.equal(isSlaCandidatePr(candidatePr({ repository: "microsoft/aspire" })), false);
  assert.equal(isSlaCandidatePr(null), false);
});

test("reconcileTracking: adds new keys, preserves firstQualifiedAt, prunes gone keys", () => {
  const tracking = { prs: {} };
  const now1 = "2025-01-06T17:00:00.000Z";
  assert.equal(reconcileTracking(tracking, ["r#1", "r#2"], now1), true);
  assert.equal(tracking.prs["r#1"].firstQualifiedAt, now1);
  assert.equal(tracking.prs["r#2"].firstQualifiedAt, now1);

  // Same candidates on the next poll: no change, original stamp preserved.
  const now2 = "2025-01-06T18:00:00.000Z";
  assert.equal(reconcileTracking(tracking, ["r#1", "r#2"], now2), false);
  assert.equal(tracking.prs["r#1"].firstQualifiedAt, now1);

  // r#2 stops qualifying (got reviewed) => pruned; r#3 appears => added.
  assert.equal(reconcileTracking(tracking, ["r#1", "r#3"], now2), true);
  assert.equal("r#2" in tracking.prs, false);
  assert.equal(tracking.prs["r#3"].firstQualifiedAt, now2);
  // r#1 keeps its original clock start throughout.
  assert.equal(tracking.prs["r#1"].firstQualifiedAt, now1);
});

test("reconcileTracking: does not prune keys whose repo was not authoritatively fetched", () => {
  const tracking = { prs: {} };
  const now1 = "2025-01-06T17:00:00.000Z";
  // Two SLA PRs on the same repo start the clock.
  reconcileTracking(tracking, ["devdiv-microsoft/aspire-1p#1", "devdiv-microsoft/aspire-1p#2"], now1);
  const now2 = "2025-01-06T18:00:00.000Z";

  // Simulate a failed fetch this run: candidateKeys is empty and the repo is NOT authoritative.
  // Nothing should be pruned, so the breach clocks survive the transient outage.
  const auth = new Set(); // no repo fetched successfully
  assert.equal(reconcileTracking(tracking, [], now2, auth), false);
  assert.equal(tracking.prs["devdiv-microsoft/aspire-1p#1"].firstQualifiedAt, now1);
  assert.equal(tracking.prs["devdiv-microsoft/aspire-1p#2"].firstQualifiedAt, now1);

  // Now the repo fetched OK and only #1 still qualifies (#2 got reviewed) => #2 is pruned,
  // #1 keeps its original stamp.
  const authOk = new Set(["devdiv-microsoft/aspire-1p"]);
  assert.equal(reconcileTracking(tracking, ["devdiv-microsoft/aspire-1p#1"], now2, authOk), true);
  assert.equal("devdiv-microsoft/aspire-1p#2" in tracking.prs, false);
  assert.equal(tracking.prs["devdiv-microsoft/aspire-1p#1"].firstQualifiedAt, now1);
});

test("annotateDashboardSla: seeded past clocks populate the breached and approaching panels", async () => {
  const repo = SLA_REPOS[0];
  const mk = (number, author) => ({
    pr: {
      repository: repo,
      number,
      author,
      title: `PR ${number}`,
      url: `https://example/${number}`,
      review: { reviewerCount: 0 },
    },
  });
  // Three external PRs, each qualified at a different past time so they land in distinct states
  // at the evaluation instant. Budget is 8h business, warn at 6h (see constants).
  const breachedKey = slaCandidateKey(mk(10).pr);
  const approachingKey = slaCandidateKey(mk(11).pr);
  const okKey = slaCandidateKey(mk(12).pr);
  // Evaluate Tue 2025-01-07 11:00 PT.
  const now = pt(2025, 1, 7, 11);
  const seedTracking = {
    // Qualified Mon 09:00 => deadline Mon 17:00, already past => breached.
    [breachedKey]: { firstQualifiedAt: new Date(pt(2025, 1, 6, 9)).toISOString() },
    // Qualified Mon 12:00 => by Tue 11:00 that is 5h Mon + 2h Tue = 7h business elapsed,
    // past the 6h warn but short of the 8h deadline => approaching.
    [approachingKey]: { firstQualifiedAt: new Date(pt(2025, 1, 6, 12)).toISOString() },
    // Qualified Tue 10:30 => only 30m in => ok.
    [okKey]: { firstQualifiedAt: new Date(pt(2025, 1, 7, 10, 30)).toISOString() },
  };

  const focusBreached = mk(10, "external-a");
  const dashboard = {
    attention: {
      focus: [focusBreached],
      slaCandidates: [mk(10, "external-a"), mk(11, "external-b"), mk(12, "external-c")],
    },
  };
  await annotateDashboardSla(dashboard, { now, persist: false, seedTracking });

  const s = dashboard.sla;
  assert.equal(s.total, 3);
  assert.equal(s.breached.length, 1);
  assert.equal(s.breached[0].pr.number, 10);
  assert.equal(s.breached[0].sla.state, "breached");
  assert.equal(s.approaching.length, 1);
  assert.equal(s.approaching[0].pr.number, 11);
  assert.equal(s.approaching[0].sla.state, "approaching");
  assert.equal(s.ok.length, 1);
  assert.equal(s.ok[0].pr.number, 12);
  assert.equal(s.okCount, 1);
  // The breached PR's focus card (a distinct reference) is decorated with the SLA pill too.
  assert.ok(focusBreached.sla);
  assert.equal(focusBreached.sla.state, "breached");
  const pill = (focusBreached.signals ?? []).find((sig) => sig.kind === "sla");
  assert.ok(pill);
  assert.equal(pill.label, "Out of SLA");
});

test("annotateDashboardSla: freshly-qualified candidates populate the ok panel list", async () => {
  const repo = SLA_REPOS[0];
  const mk = (number, author) => ({
    pr: {
      repository: repo,
      number,
      author,
      title: `PR ${number}`,
      url: `https://example/${number}`,
      review: { reviewerCount: 0 },
    },
  });
  // A focus card sharing PR #1's key proves the SLA record also rides the attention
  // lists, which are *different object references* than attention.slaCandidates.
  const focusCard = mk(1, "external-one");
  const dashboard = {
    attention: {
      focus: [focusCard],
      slaCandidates: [mk(1, "external-one"), mk(2, "external-two")],
    },
  };
  // Mon 2025-01-06 10:00 PT: both PRs are freshly qualified, so with a same-run start
  // stamp they sit comfortably inside the 8h budget (warn at 16:00, due Tue).
  const now = pt(2025, 1, 6, 10);
  await annotateDashboardSla(dashboard, { now, persist: false });

  const s = dashboard.sla;
  assert.equal(s.total, 2);
  assert.equal(s.okCount, 2);
  assert.equal(s.breached.length, 0);
  assert.equal(s.approaching.length, 0);
  // The regression guard: candidate cards are stamped directly, so the ok list is
  // populated even though slaCandidates are not the same references as focus/buckets.
  assert.equal(s.ok.length, 2);
  for (const c of s.ok) {
    assert.equal(c.sla.state, "ok");
    assert.ok(c.sla.deadlineAt);
    assert.ok(c.sla.firstQualifiedAt);
  }
  // The scratch field is stripped from the broadcast payload.
  assert.equal("slaCandidates" in dashboard.attention, false);
  // The focus card (separate reference, same PR) is decorated too.
  assert.ok(focusCard.sla);
  assert.equal(focusCard.sla.state, "ok");
});

test("annotateDashboardSla: an unfetched SLA repo marks the report partial", async () => {
  const repo = SLA_REPOS[0];
  // An empty candidate list is ambiguous on its own: it can mean "nothing to review" OR "the
  // watched repo failed to fetch this run". expectedSlaRepos vs authoritativeRepos disambiguates.
  const dashboard = { attention: { focus: [], slaCandidates: [] } };
  await annotateDashboardSla(dashboard, {
    now: pt(2025, 1, 6, 10),
    persist: false,
    expectedSlaRepos: new Set([repo.toLowerCase()]),
    authoritativeRepos: new Set(),
  });
  assert.equal(dashboard.sla.partial, true);
  assert.deepEqual(dashboard.sla.unfetchedRepos, [repo.toLowerCase()]);
});

test("annotateDashboardSla: a fully-fetched run is not partial", async () => {
  const repo = SLA_REPOS[0];
  const dashboard = { attention: { focus: [], slaCandidates: [] } };
  await annotateDashboardSla(dashboard, {
    now: pt(2025, 1, 6, 10),
    persist: false,
    expectedSlaRepos: new Set([repo.toLowerCase()]),
    authoritativeRepos: new Set([repo.toLowerCase()]),
  });
  assert.equal(dashboard.sla.partial, false);
  assert.deepEqual(dashboard.sla.unfetchedRepos, []);
});

test("annotateDashboardSla: absent expectedSlaRepos defaults to a complete fetch", async () => {
  // Focused callers (unit tests, persist:false paths) omit the fetch-scope sets entirely; the
  // report must default to partial:false so they don't spuriously look incomplete.
  const dashboard = { attention: { focus: [], slaCandidates: [] } };
  await annotateDashboardSla(dashboard, { now: pt(2025, 1, 6, 10), persist: false });
  assert.equal(dashboard.sla.partial, false);
  assert.deepEqual(dashboard.sla.unfetchedRepos, []);
});
