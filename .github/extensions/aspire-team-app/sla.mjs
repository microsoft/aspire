// Review SLA engine for the first-party aspire-1p repo.
//
// The private mirror carries a 1-business-day review SLA: a PR that is genuinely
// ready (qualifies for the "Needs attention" focused queue), was authored by
// someone outside the core team, and has had no human review yet must be reviewed
// within a business-time budget. This module owns:
//
//   * Timezone-aware business-hours math (no external libraries) — the budget is
//     only consumed during Mon-Fri 09:00-17:00 Pacific, so nights and weekends
//     never burn the clock.
//   * A durable per-PR tracking store (firstQualifiedAt) so the clock survives
//     process restarts and is shared across the canvas server and the headless
//     CLI used by the hourly notifier workflow.
//   * annotateDashboardSla(), which reconciles tracking, computes stable anchors,
//     derives each PR's SLA state, decorates the dashboard cards with SLA pills,
//     and attaches the dedicated SLA panel data (dashboard.sla).
//
// CHURN CONSTRAINT (critical): server.dashboardChanged() JSON-stringifies the
// whole dashboard (minus seq/fetchedAt) to detect changes and re-broadcast. So we
// must only ever store STABLE anchors (firstQualifiedAt / warnAt / deadlineAt are
// fixed given fixed inputs) and a stepwise `state` that flips only at threshold
// crossings — never a live "waiting Xh" delta or a per-run timestamp. The live
// "due in ~2h" text is computed client-side in render.mjs from deadlineAt.

import { mkdir, readFile, writeFile, rename } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

import {
  SLA_REPOS,
  SLA_BUDGET_HOURS,
  SLA_WARN_HOURS,
  SLA_TIMEZONE,
  SLA_WORK_START_HOUR,
  SLA_WORK_END_HOUR,
  SLA_WORK_DAYS,
  hourMs,
} from "./constants.mjs";
import { isCoreTeamAuthor } from "./model.mjs";

const COPILOT_HOME = process.env.COPILOT_HOME || join(homedir(), ".copilot");
const ARTIFACT_DIR = join(COPILOT_HOME, "extensions", "aspire-team-app", "artifacts");
const TRACKING_FILE = join(ARTIFACT_DIR, "sla-tracking.json");

const SLA_REPO_SET = new Set(SLA_REPOS.map((r) => r.toLowerCase()));
const WORK_DAY_SET = new Set(SLA_WORK_DAYS);

// ---------------------------------------------------------------------------
// Candidate predicates
// ---------------------------------------------------------------------------

// Whether a repository is under the review SLA (case-insensitive).
export function isSlaRepo(repo) {
  return SLA_REPO_SET.has(String(repo || "").toLowerCase());
}

// A PR is SLA-eligible when it is on an SLA repo, authored outside the core team,
// and has not yet had a single *human* review (reviewerCount counts distinct human
// reviewers; Copilot/bot reviews are excluded upstream in deriveReview). The clock
// stops the moment any human comments on or approves the PR.
export function isSlaCandidatePr(pr) {
  if (!pr) return false;
  if (!isSlaRepo(pr.repository)) return false;
  if (isCoreTeamAuthor(pr.author)) return false;
  return (pr.review?.reviewerCount ?? 0) === 0;
}

// Stable key for a PR across runs and stores. "repo#number", lowercased repo.
export function slaCandidateKey(pr) {
  return `${String(pr.repository).toLowerCase()}#${pr.number}`;
}

// ---------------------------------------------------------------------------
// Timezone-aware business-hours math (pure, no libraries)
// ---------------------------------------------------------------------------
//
// All instants are epoch-millisecond numbers. "wall" refers to the local clock in
// SLA_TIMEZONE. Intl.DateTimeFormat resolves the timezone (including DST) for us;
// the 09:00-17:00 window never straddles the ambiguous 02:00 DST hour, so a simple
// two-pass offset correction is exact for our purposes.

// Break an instant into its wall-clock calendar parts in the given timezone.
function tzParts(instant, tz) {
  // en-US + h23 gives a stable, parseable 24-hour breakdown regardless of host locale.
  const dtf = new Intl.DateTimeFormat("en-US", {
    timeZone: tz,
    hourCycle: "h23",
    year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit", second: "2-digit",
  });
  const parts = dtf.formatToParts(new Date(instant));
  const m = {};
  for (const p of parts) {
    if (p.type !== "literal") m[p.type] = Number(p.value);
  }
  // Intl emits hour "24" at midnight under some engines/versions; normalize to 0.
  if (m.hour === 24) m.hour = 0;
  return m;
}

// Milliseconds to add to `instant` to get the wall clock as if it were UTC, i.e.
// the timezone offset at that instant. offset = wallAsUTC - instant.
export function getTzOffsetMs(instant, tz) {
  const m = tzParts(instant, tz);
  const asUtc = Date.UTC(m.year, m.month - 1, m.day, m.hour, m.minute, m.second);
  // Strip sub-second precision from instant so the (second-granular) asUtc lines up;
  // real timezone offsets are always whole minutes, so nothing is lost.
  const base = instant - (((instant % 1000) + 1000) % 1000);
  return asUtc - base;
}

// Resolve a local wall-clock time (y, mo[1-12], d, h, mi) in `tz` to an instant.
// Two-pass: guess treating the wall time as UTC, correct by the offset, then
// re-check the offset at the resolved instant to absorb a DST transition.
export function wallToInstant(y, mo, d, h, mi, tz) {
  const guess = Date.UTC(y, mo - 1, d, h, mi, 0);
  const offset1 = getTzOffsetMs(guess, tz);
  let inst = guess - offset1;
  const offset2 = getTzOffsetMs(inst, tz);
  if (offset2 !== offset1) inst = guess - offset2;
  return inst;
}

// UTC weekday (0=Sun..6=Sat) of a calendar date. Weekday is timezone-independent
// for a whole date, so a plain UTC construction is correct here.
function weekdayOf(y, mo, d) {
  return new Date(Date.UTC(y, mo - 1, d)).getUTCDay();
}

function isWorkDay(y, mo, d) {
  return WORK_DAY_SET.has(weekdayOf(y, mo, d));
}

// The [start, end) business-hours window (as instants) for a given local date.
function windowForDay(y, mo, d, tz) {
  return {
    start: wallToInstant(y, mo, d, SLA_WORK_START_HOUR, 0, tz),
    end: wallToInstant(y, mo, d, SLA_WORK_END_HOUR, 0, tz),
  };
}

// The business-hours start instant of the calendar day *after* (y, mo, d).
function nextDayWorkStart(y, mo, d, tz) {
  const n = new Date(Date.UTC(y, mo - 1, d + 1));
  return windowForDay(n.getUTCFullYear(), n.getUTCMonth() + 1, n.getUTCDate(), tz).start;
}

// Add `budgetMs` of *business time* to `start`, returning the deadline instant.
// Walks day by day: skips weekends, clamps into each day's [09:00,17:00) window,
// and consumes the available window until the budget is exhausted.
export function addBusinessMs(start, budgetMs, tz = SLA_TIMEZONE) {
  if (budgetMs <= 0) return start;
  let remaining = budgetMs;
  let cur = start;
  // Safety bound: ~10 years of days. A positive budget always resolves far sooner.
  for (let i = 0; i < 3660; i++) {
    const p = tzParts(cur, tz);
    if (isWorkDay(p.year, p.month, p.day)) {
      const win = windowForDay(p.year, p.month, p.day, tz);
      let cursor = cur < win.start ? win.start : cur;
      if (cursor < win.end) {
        const avail = win.end - cursor;
        if (remaining <= avail) return cursor + remaining;
        remaining -= avail;
      }
    }
    cur = nextDayWorkStart(p.year, p.month, p.day, tz);
  }
  return cur;
}

// Business time elapsed between two instants (used for human-readable message text
// in the notifier; NOT stored on the dashboard).
export function businessMsBetween(start, end, tz = SLA_TIMEZONE) {
  if (end <= start) return 0;
  let total = 0;
  let cur = start;
  for (let i = 0; i < 3660 && cur < end; i++) {
    const p = tzParts(cur, tz);
    if (isWorkDay(p.year, p.month, p.day)) {
      const win = windowForDay(p.year, p.month, p.day, tz);
      const lo = Math.max(cur, win.start);
      const hi = Math.min(end, win.end);
      if (hi > lo) total += hi - lo;
    }
    cur = nextDayWorkStart(p.year, p.month, p.day, tz);
  }
  return total;
}

// ---------------------------------------------------------------------------
// Anchors + state
// ---------------------------------------------------------------------------

// Precompute the stable warn/deadline instants for a PR from when it first
// qualified. Given a fixed firstQualifiedAt these never move, so they don't churn
// the dashboard JSON.
export function slaAnchors(firstQualifiedAtIso, opts = {}) {
  const tz = opts.tz ?? SLA_TIMEZONE;
  const budgetHours = opts.budgetHours ?? SLA_BUDGET_HOURS;
  const warnHours = opts.warnHours ?? SLA_WARN_HOURS;
  const start = new Date(firstQualifiedAtIso).getTime();
  return {
    firstQualifiedAt: firstQualifiedAtIso,
    warnAt: new Date(addBusinessMs(start, warnHours * hourMs, tz)).toISOString(),
    deadlineAt: new Date(addBusinessMs(start, budgetHours * hourMs, tz)).toISOString(),
  };
}

// Derive the stepwise SLA state by comparing wall-clock `nowMs` to the anchors.
// Stepwise: only flips at threshold crossings, so storing it does not cause churn.
export function slaState(anchors, nowMs) {
  const deadline = new Date(anchors.deadlineAt).getTime();
  const warn = new Date(anchors.warnAt).getTime();
  if (nowMs >= deadline) return "breached";
  if (nowMs >= warn) return "approaching";
  return "ok";
}

// ---------------------------------------------------------------------------
// Tracking store (durable firstQualifiedAt per PR)
// ---------------------------------------------------------------------------
//
// Shape: { prs: { "repo#num": { firstQualifiedAt: ISO } } }. Writes are serialized
// in-process and atomic (temp file + rename) to reduce cross-process races between
// the canvas session and the hourly workflow session. A rare lost update is
// self-healing: at worst firstQualifiedAt resets later, which pushes the deadline
// out — it never causes a false breach.

let trackingUpdate = Promise.resolve();

export async function loadTracking() {
  try {
    const raw = await readFile(TRACKING_FILE, "utf8");
    const parsed = JSON.parse(raw);
    return { prs: parsed && typeof parsed.prs === "object" && parsed.prs ? parsed.prs : {} };
  } catch {
    return { prs: {} };
  }
}

async function saveTracking(tracking) {
  await mkdir(ARTIFACT_DIR, { recursive: true });
  const tmp = `${TRACKING_FILE}.${process.pid}.${Date.now()}.tmp`;
  await writeFile(tmp, JSON.stringify(tracking, null, 2) + "\n", "utf8");
  await rename(tmp, TRACKING_FILE);
  return tracking;
}

// Add newly-qualifying keys (stamping firstQualifiedAt=nowIso) and prune keys that
// no longer qualify. Returns whether anything changed so callers can skip the write
// on the common no-op poll. Pure over its inputs (mutates the passed tracking).
export function reconcileTracking(tracking, candidateKeys, nowIso) {
  const prs = tracking.prs || (tracking.prs = {});
  const want = new Set(candidateKeys);
  let changed = false;
  for (const key of want) {
    if (!prs[key]) {
      prs[key] = { firstQualifiedAt: nowIso };
      changed = true;
    }
  }
  for (const key of Object.keys(prs)) {
    if (!want.has(key)) {
      delete prs[key];
      changed = true;
    }
  }
  return changed;
}

// Serialized load -> reconcile -> (persist if changed). Returns a snapshot of the
// per-key firstQualifiedAt map for the current candidates.
function reconcileAndPersist(candidateKeys, nowIso) {
  const run = trackingUpdate
    .catch(() => {})
    .then(async () => {
      const tracking = await loadTracking();
      const changed = reconcileTracking(tracking, candidateKeys, nowIso);
      if (changed) await saveTracking(tracking);
      return tracking.prs;
    });
  trackingUpdate = run.then(() => {}, () => {});
  return run;
}

// ---------------------------------------------------------------------------
// Dashboard annotation
// ---------------------------------------------------------------------------

function slaPill(state) {
  if (state === "breached") return { label: "Out of SLA", tone: "danger", kind: "sla" };
  if (state === "approaching") return { label: "Review SLA", tone: "warning", kind: "sla" };
  return null;
}

// Prepend the SLA pill to a card's signals (dropping any prior SLA pill so repeat
// annotations don't stack) and stamp the card with its SLA status for the renderer.
function decorateCard(card, status) {
  const pill = slaPill(status.state);
  card.sla = status;
  if (pill) {
    const rest = (card.signals || []).filter((s) => s && s.kind !== "sla");
    card.signals = [pill, ...rest];
  }
}

// Walk every card list in the attention model and decorate matching cards by key.
function decorateAttention(attention, statusByKey) {
  const lists = [];
  if (Array.isArray(attention.focus)) lists.push(attention.focus);
  if (Array.isArray(attention.forMe)) lists.push(attention.forMe);
  if (Array.isArray(attention.focusExclusions)) lists.push(attention.focusExclusions);
  for (const b of attention.buckets || []) {
    if (Array.isArray(b.items)) lists.push(b.items);
  }
  for (const list of lists) {
    for (const card of list) {
      const pr = card && card.pr;
      if (!pr) continue;
      const status = statusByKey.get(slaCandidateKey(pr));
      if (status) decorateCard(card, status);
    }
  }
}

function statusFor(pr, firstQualifiedAt, nowMs, opts) {
  const anchors = slaAnchors(firstQualifiedAt, opts);
  const state = slaState(anchors, nowMs);
  return {
    key: slaCandidateKey(pr),
    repository: pr.repository,
    number: pr.number,
    title: pr.title,
    url: pr.url,
    author: pr.author,
    firstQualifiedAt: anchors.firstQualifiedAt,
    warnAt: anchors.warnAt,
    deadlineAt: anchors.deadlineAt,
    state,
  };
}

// Reconcile tracking, compute SLA state for every candidate, decorate the dashboard
// cards, and attach dashboard.sla (the dedicated panel data). snapshot() attaches
// `attention.slaCandidates` (full cards for qualifying PRs); this removes that
// scratch field afterward to keep the broadcast JSON lean.
//
// opts: { now?: epoch ms, persist?: bool (default true) }.
export async function annotateDashboardSla(dashboard, opts = {}) {
  const attention = dashboard && dashboard.attention;
  const candidates = (attention && attention.slaCandidates) || [];
  const nowMs = opts.now ?? Date.now();
  const nowIso = new Date(nowMs).toISOString();
  const anchorOpts = {
    tz: SLA_TIMEZONE,
    budgetHours: SLA_BUDGET_HOURS,
    warnHours: SLA_WARN_HOURS,
  };

  // Re-filter defensively so this module remains the single source of truth for
  // who is under SLA, regardless of what snapshot() attached.
  const cards = candidates.filter((c) => c && c.pr && isSlaCandidatePr(c.pr));
  const keys = cards.map((c) => slaCandidateKey(c.pr));

  const persist = opts.persist !== false;
  const trackedPrs = persist
    ? await reconcileAndPersist(keys, nowIso)
    : (() => {
        const tracking = { prs: {} };
        reconcileTracking(tracking, keys, nowIso);
        return tracking.prs;
      })();

  const statusByKey = new Map();
  for (const card of cards) {
    const key = slaCandidateKey(card.pr);
    const firstQualifiedAt = trackedPrs[key]?.firstQualifiedAt || nowIso;
    const status = statusFor(card.pr, firstQualifiedAt, nowMs, anchorOpts);
    statusByKey.set(key, status);
  }

  // Decorate cards across the whole attention model (focus/forMe/exclusions/buckets)
  // so the SLA pill rides along wherever the PR appears.
  if (attention) decorateAttention(attention, statusByKey);

  // The dedicated panel shows only actionable states (approaching / breached),
  // breached first then approaching, each group by soonest deadline.
  const byDeadline = (a, b) => new Date(a.sla.deadlineAt) - new Date(b.sla.deadlineAt);
  const panelCards = cards.filter((c) => c.sla && c.sla.state !== "ok");
  const breached = panelCards.filter((c) => c.sla.state === "breached").sort(byDeadline);
  const approaching = panelCards.filter((c) => c.sla.state === "approaching").sort(byDeadline);

  dashboard.sla = {
    repos: SLA_REPOS,
    budgetHours: SLA_BUDGET_HOURS,
    warnHours: SLA_WARN_HOURS,
    tz: SLA_TIMEZONE,
    breached,
    approaching,
    // Count of tracked-but-comfortable candidates, for a quiet "N within budget" note.
    okCount: cards.length - panelCards.length,
    total: cards.length,
  };

  // Drop the scratch field so it doesn't bloat the broadcast payload.
  if (attention && "slaCandidates" in attention) delete attention.slaCandidates;

  return dashboard;
}
