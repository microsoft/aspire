// Shared constants ported from davidfowl/pr-dashboard frontend/src/constants.ts.

export const currentRelease = "13.5";
export const hourMs = 1000 * 60 * 60;
export const dayMs = hourMs * 24;

export const coreTeamMembers = [
  "davidfowl",
  "mitchdenny",
  "sebastienros",
  "IEvangelist",
  "danegsta",
  "radical",
  "JamesNK",
  "adamint",
  "joperezr",
  "maddymontaquila",
  "DamianEdwards",
  "eerhardt",
  "ellahathaway",
  "karolz-ms",
];

// Author login suffixes that mark a core-team member's alt/enterprise alias (e.g.
// "dapine_microsoft"). When the base login (with the suffix stripped) matches a
// coreTeamMembers entry the PR is attributed to that member. Ported from pr-dashboard
// Dashboard config (#95). NOTE: carrying a suffix is NOT by itself proof of core-team
// membership — see coreTeamEmuLogins below. This list is also consumed by accounts.mjs
// (isEmuLogin) to detect Enterprise Managed User accounts for per-account repo defaults.
export const coreTeamMemberAliasSuffixes = ["_microsoft"];

// Allowlist of first-party logins that count as Aspire core team. An author in this
// list is treated as core team; anyone else is a review-SLA candidate. This is an
// explicit roster (not a suffix rule) because these logins don't always match a
// person's public login. Compared case-insensitively; keep in sync with the roster.
export const coreTeamEmuLogins = [
  "eerhardt_microsoft",
  "ankj_microsoft",
  "dapine_microsoft",
  "midenn_microsoft",
  "adamratzman_microsoft",
  "danegsta_microsoft",
  "jamesnk_microsoft",
  "karolz_microsoft",
  "sebros_microsoft",
  "ellahathaway_microsoft",
  "joperezr_microsoft",
  "dedward_microsoft",
  "maleger_microsoft",
];

// Repos where specific check failures are non-blocking (informational only), so an
// aggregate "failure" rollup driven solely by these checks should not read as red CI.
// Ported from pr-dashboard server appsettings.json `NonBlockingCheckFailureRules`.
// A rule matches a failing check when its trimmed, lowercased name equals one of
// `checkNames` OR contains one of `checkNameContains`. Example: the aspire-1p
// "GitOps/GitHubPop" proof-of-presence gate stays green in the review queue while
// still being visible to the owning team.
export const nonBlockingCheckFailureRules = [
  {
    repository: "devdiv-microsoft/aspire-1p",
    label: "proof of presence",
    checkNames: ["GitOps/GitHubPop"],
    checkNameContains: ["proof of presence"],
  },
];

// ---------------------------------------------------------------------------
// Review SLA (first-party aspire-1p repo).
// ---------------------------------------------------------------------------
//
// The private first-party mirror carries a 1-business-day review SLA: a PR that
// is genuinely ready (i.e. it qualifies for the "Needs attention" focused queue),
// was authored by someone outside the core team, and has not yet had a single
// human review must not sit unreviewed past the budget. The clock is measured in
// *business time* (Mon-Fri, 09:00-17:00 Pacific) so overnight and weekend hours do
// not burn the budget. See sla.mjs for the business-hours math and tracking store.

// Repos the SLA applies to. Matched case-insensitively against pr.repository.
export const SLA_REPOS = ["devdiv-microsoft/aspire-1p"];

// Total business-time budget before a ready, un-reviewed external PR is "out of SLA".
export const SLA_BUDGET_HOURS = 8;

// Business-time elapsed at which a PR is flagged "approaching" (early warning) so the
// team can prioritize it before it breaches. Must be < SLA_BUDGET_HOURS.
export const SLA_WARN_HOURS = 6;

// IANA timezone the business-hours window is expressed in. Intl handles DST, so the
// 09:00-17:00 window tracks Pacific daylight/standard transitions automatically.
export const SLA_TIMEZONE = "America/Los_Angeles";

// Business-day window [start, end) in whole local hours. 9 => 09:00, 17 => 17:00.
export const SLA_WORK_START_HOUR = 9;
export const SLA_WORK_END_HOUR = 17;

// Weekdays counted as business days (0 = Sunday .. 6 = Saturday). Mon-Fri.
export const SLA_WORK_DAYS = [1, 2, 3, 4, 5];

// Markers for the Issues focus buckets (ported from pr-dashboard models.ts). These
// are matched case-insensitively via substring (title/label) or login equality.
export const ctiTeamTitleMarker = "[aspiree2e]";
export const afscromeIssueAuthor = "afscrome";
export const releaseBlockingLabelMarker = "blocking-release";

// Single source of truth for the "For you" personal-pick action labels.
export const personalPickActions = {
  resolveConflicts: "Resolve conflicts",
  needsAttention: "Needs your attention",
  fixCi: "Fix CI",
  reviewThis: "Review this",
  respondHere: "Respond here",
  finishThis: "Finish this",
};

// The signal label emitted for a PR that has aged past the focus limit without an approving
// review — a commented or previously reviewed PR still counts as review debt; only an approving
// review clears it (see model.mjs isReviewDebt / oldFirstSignal). Shared so github.mjs can flag
// these cards and the canvas can offer an "Address review" action, without the string drifting
// between the producer (model) and the consumer (github serialization).
export const reviewDebtSignalLabel = "review debt";
