import assert from "node:assert/strict";
import test from "node:test";

import { hourMs } from "./constants.mjs";
import {
  addBusinessMs,
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
