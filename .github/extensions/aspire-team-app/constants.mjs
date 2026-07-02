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

// Single source of truth for the "For you" personal-pick action labels.
export const personalPickActions = {
  resolveConflicts: "Resolve conflicts",
  needsAttention: "Needs your attention",
  fixCi: "Fix CI",
  reviewThis: "Review this",
  respondHere: "Respond here",
  finishThis: "Finish this",
};
