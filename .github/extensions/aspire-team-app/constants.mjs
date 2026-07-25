// Shared constants ported from davidfowl/pr-dashboard frontend/src/constants.ts.

export const currentRelease = "13.4";
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
// "dapine_microsoft"). Any author ending in one of these is treated as core team,
// and if the base login (with the suffix stripped) matches a coreTeamMembers entry
// it is attributed to that member. Ported from pr-dashboard Dashboard config (#95).
export const coreTeamMemberAliasSuffixes = ["_microsoft"];

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
