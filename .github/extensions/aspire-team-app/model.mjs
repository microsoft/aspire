// Review-queue model engine for the Aspire Team App canvas.
//
// A faithful, self-contained port of davidfowl/pr-dashboard's review intelligence
// (frontend/src/utils/models.ts + components/dashboard/focusQueue.ts): attention
// buckets, the focused "Needs attention" queue, the personal "For you" action
// picks, community lane, core-team ownership, and the "outside the queue" reasons.

import {
  afscromeIssueAuthor,
  coreTeamMembers,
  coreTeamMemberAliasSuffixes,
  ctiTeamTitleMarker,
  currentRelease,
  dayMs,
  hourMs,
  nonBlockingCheckFailureRules,
  personalPickActions,
  releaseBlockingLabelMarker,
  reviewDebtSignalLabel,
} from "./constants.mjs";

const approvedAgingMs = 2 * dayMs;
const communityWaitMs = 12 * hourMs;
const focusAgeLimitMs = 14 * dayMs;
const stalledPullRequestMs = 7 * dayMs;
const quickWinLineThreshold = 80;
const quickWinFileThreshold = 3;

// Faithful port of pr-dashboard's normalizeCheckFailureRules guard (dashboardConfig.ts,
// davidfowl/pr-dashboard#96): a non-blocking check-failure rule is only honored when it
// names a repository, a label, AND at least one concrete check matcher. Without this,
// a matcher-less rule (a plausible constants.mjs typo) makes hasNonBlockingCheckFailureRule
// treat every aggregate "failure" rollup on that repo as non-blocking, silently hiding all
// red CI for the repo. Dropping matcher-less rules keeps such a config mistake from
// suppressing genuine failures.
export function filterCheckFailureRules(rules) {
  return (rules ?? []).filter((rule) =>
    rule.repository
    && rule.label
    && ((rule.checkNames?.length ?? 0) > 0 || (rule.checkNameContains?.length ?? 0) > 0));
}
const activeNonBlockingCheckFailureRules = filterCheckFailureRules(nonBlockingCheckFailureRules);

const regressionBucketLabel = "Regression";
const approvedButAgingBucketLabel = "Approved but aging";
const agedOutCommunityBucketLabel = "Aged out community";
const myDraftPullRequestsBucketLabel = "My draft PRs";
const docsFromCodeRepository = "microsoft/aspire.dev";
const docsFromCodeLabel = "docs-from-code";
const doNotMergeLabels = new Set(["needs-author-action", "no-merge"]);
const knownBotAuthors = new Set(["copilot-swe-agent", "dotnet-maestro"]);

// ---------------------------------------------------------------------------
// Identity + author classification
// ---------------------------------------------------------------------------

export function actorIdentityKey(actor) {
  // A "{human}/copilot" author identifies as the human who started the Copilot PR, so
  // ownership, core-team matching, and dedupe all key off that human. We also strip any
  // non-alphanumeric characters so alias punctuation ("karolz-ms", "IEvangelist_microsoft")
  // normalizes consistently on both sides of a comparison. Ported from pr-dashboard
  // models.ts `actorIdentityKey`.
  const normalized = String(actor || "").toLowerCase();
  const human = normalized.endsWith("/copilot")
    ? normalized.slice(0, -"/copilot".length)
    : normalized;
  return human.replace(/[^a-z0-9]/g, "");
}
function sameLogin(a, b) {
  return actorIdentityKey(a) === actorIdentityKey(b);
}
function isCopilotAttributedAuthor(author) {
  // Check the raw author, not actorIdentityKey: the latter strips "/copilot" (and all
  // punctuation) as part of keying to the human owner, so it can never end with "/copilot".
  return String(author || "").toLowerCase().endsWith("/copilot");
}
function isBotAuthor(author, authorType) {
  if (isCopilotAttributedAuthor(author)) return false;
  // GraphQL __typename is the most precise signal when present; the string checks mirror
  // pr-dashboard models.ts `isBotAuthor` (raw lowercased login, not the punctuation-stripped
  // identity key). `github-actions` and the configured bot logins round out the list.
  if (authorType === "Bot") return true;
  const n = String(author || "").toLowerCase();
  if (knownBotAuthors.has(n)) return true;
  return n.endsWith("[bot]") || n.includes("bot") || n === "copilot" || n === "github-actions";
}
function isCoreTeamAuthor(author) {
  return coreTeamOwnershipActor(author) !== null;
}
// Resolves the core-team actor that "owns" a PR by an author, honoring alias
// suffixes (#95). Returns a canonical coreTeamMembers login when the author
// matches directly or via a stripped alias suffix; otherwise returns the alias
// login itself when the author carries a configured suffix (an MSFT alt account
// not individually listed); null for everyone else.
function coreTeamOwnershipActor(author) {
  const member = matchingCoreTeamMember(author);
  if (member) return member;
  return isConfiguredTeamAlias(author) ? author : null;
}
function matchingCoreTeamMember(author) {
  const authorKey = actorIdentityKey(author);
  const direct = coreTeamMembers.find((member) => actorIdentityKey(member) === authorKey);
  if (direct) return direct;
  const base = configuredTeamAliasBase(author);
  if (!base) return null;
  const baseKey = actorIdentityKey(base);
  return coreTeamMembers.find((member) => actorIdentityKey(member) === baseKey) ?? null;
}
function configuredTeamAliasBase(author) {
  const normalized = String(author || "").toLowerCase();
  const suffix = coreTeamMemberAliasSuffixes.find((candidate) => {
    const s = candidate.toLowerCase();
    return s.length > 0 && normalized.endsWith(s) && normalized.length > s.length;
  });
  return suffix ? String(author).slice(0, -suffix.length) : null;
}
function isConfiguredTeamAlias(author) {
  return configuredTeamAliasBase(author) !== null;
}
function isCommunityToolkitPullRequest(pr) {
  return pr.repository.toLowerCase() === "communitytoolkit/aspire";
}
function isCommunityAuthor(author, authorType) {
  return !isBotAuthor(author, authorType) && !isCoreTeamAuthor(author);
}
export function isCommunityPullRequest(pr) {
  return !pr.isMine && !pr.repoPrivate && isCommunityAuthor(pr.author, pr.authorType) && !isCommunityToolkitPullRequest(pr);
}
export function isAgedOutCommunityPullRequest(pr) {
  return isCommunityPullRequest(pr) && ageMs(pr.updatedAt) > focusAgeLimitMs;
}
function isCommunityWaiting(pr) {
  return !pr.isMine && !pr.repoPrivate && isCommunityAuthor(pr.author, pr.authorType) && (
    (pr.review.state === "waiting" && ageMs(pr.createdAt) >= communityWaitMs) ||
    (pr.review.state === "reviewed" && isIdle(pr))
  );
}
function isOwnCopilotAuthor(author, login) {
  // Match on the raw author: actorIdentityKey strips "/copilot", so the suffix test must
  // run against the original string before comparing the human base via sameLogin.
  const suffix = "/copilot";
  const raw = String(author || "");
  if (!raw.toLowerCase().endsWith(suffix)) return false;
  return sameLogin(raw.slice(0, raw.length - suffix.length), login);
}
function isOwnCopilotAuthorAny(author, logins) {
  return logins.some((l) => isOwnCopilotAuthor(author, l));
}

// ---------------------------------------------------------------------------
// PR predicates
// ---------------------------------------------------------------------------

export function isChecksFailing(pr) {
  // A "failure" rollup is only real red CI when it isn't driven purely by checks a
  // non-blocking rule marks informational. Ported from pr-dashboard models.ts.
  return pr.checks?.state === "failure"
    && !isNonBlockingAggregateFailure(pr)
    && !nonBlockingOnlyFailureRule(pr);
}

// The check state to display/act on after accounting for non-blocking rules. Mirrors
// pr-dashboard models.ts `visibleCheckState`:
//   * An aggregate failure on a rule-covered repo with no per-check detail yet is
//     "unknown" (the dashboard lazily fetches details; we surface it as indeterminate).
//   * When every failing check matches a non-blocking rule, the PR is effectively
//     pending (if anything is still running) or success.
//   * Otherwise the raw rollup state stands.
export function visibleCheckState(pr) {
  if (isNonBlockingAggregateFailure(pr)) {
    return "unknown";
  }
  if (!nonBlockingOnlyFailureRule(pr)) {
    return pr.checks?.state;
  }
  return (pr.checks?.pendingCount ?? 0) > 0 ? "pending" : "success";
}

// Every failing check on the PR maps to a non-blocking rule for its repo (and the rollup
// reported no other failures), so the failure is informational only. Returns the matched
// rule, else null. Defensive against the lean list-level checks shape (no failingChecks).
function nonBlockingOnlyFailureRule(pr) {
  const checks = pr.checks;
  if (!checks || checks.state !== "failure") {
    return null;
  }
  const failing = checks.failingChecks ?? [];
  if ((checks.failureCount ?? 0) !== failing.length || failing.length === 0) {
    return null;
  }
  const matchingRules = failing.map((check) => matchingNonBlockingCheckFailureRule(pr.repository, check.name));
  return matchingRules.every((rule) => rule != null) ? matchingRules[0] ?? null : null;
}

// A "failure" rollup on a rule-covered repo where no individual check detail is available
// yet (totalCount/failureCount/failingChecks all zero). This is the list-level shape the
// dashboard treats as indeterminate rather than red.
function isNonBlockingAggregateFailure(pr) {
  const checks = pr.checks;
  return checks?.state === "failure"
    && hasNonBlockingCheckFailureRule(pr.repository)
    && (checks.totalCount ?? 0) === 0
    && (checks.failureCount ?? 0) === 0
    && (checks.failingChecks ?? []).length === 0;
}

function hasNonBlockingCheckFailureRule(repository) {
  return activeNonBlockingCheckFailureRules.some((rule) => sameRepository(rule.repository, repository));
}
function matchingNonBlockingCheckFailureRule(repository, name) {
  return activeNonBlockingCheckFailureRules.find((rule) =>
    sameRepository(rule.repository, repository) && matchesNonBlockingCheckFailureName(rule, name)) ?? null;
}
function sameRepository(first, second) {
  return String(first).toLowerCase() === String(second).toLowerCase();
}
function matchesNonBlockingCheckFailureName(rule, name) {
  const normalized = String(name || "").trim().toLowerCase();
  return (rule.checkNames ?? []).some((checkName) => normalized === checkName.toLowerCase())
    || (rule.checkNameContains ?? []).some((fragment) => normalized.includes(fragment.toLowerCase()));
}
export function hasMergeConflicts(pr) {
  return pr.mergeableState === "dirty";
}
// GitHub's authoritative review decision. A PR whose branch protection still
// requires review (or where a required reviewer requested changes) cannot be
// merged, so it is not "Ready to merge" no matter how many stray approvals it
// has. null means the repo has no required-review gate, so we defer to the
// derived approval state. Guards the naive `review.state === "approved"` check.
export function isMergeReviewBlocked(pr) {
  return pr.reviewDecision === "REVIEW_REQUIRED" || pr.reviewDecision === "CHANGES_REQUESTED";
}
function matchingDoNotMergeLabel(pr) {
  return pr.labels.find((l) => doNotMergeLabels.has(l.toLowerCase())) ?? null;
}
export function hasNeedsAuthorActionLabel(pr) {
  return matchingDoNotMergeLabel(pr) !== null;
}
// A PR that belongs on its author's own plate, not the shared review/ship lists: still a
// draft, conflicting, or explicitly labeled do-not-merge. Ported from pr-dashboard
// models.ts `shouldHideFromSharedPullRequestLists`.
export function shouldHideFromSharedPullRequestLists(pr) {
  return pr.draft || hasMergeConflicts(pr) || hasNeedsAuthorActionLabel(pr);
}
function hasUnresolvedFeedback(pr) {
  return pr.review.unresolvedThreadCount > 0;
}
function isIdle(pr) {
  return Date.now() - new Date(pr.updatedAt).getTime() >= stalledPullRequestMs;
}
function hasRegressionLabel(labels) {
  return labels.some((l) => l.toLowerCase().includes("regression"));
}
function hasRegressionSignal(pr) {
  return hasRegressionLabel(pr.labels) || (pr.linkedIssues || []).some((i) => hasRegressionLabel(i.labels));
}
export function isGeneratedDocsPullRequest(pr) {
  return pr.repository.toLowerCase() === docsFromCodeRepository &&
    pr.labels.some((l) => l.toLowerCase() === docsFromCodeLabel);
}
function approvalAgeAt(pr) {
  return pr.review.lastApprovedAt ?? pr.review.lastReviewedAt;
}
function isApprovedButAging(pr) {
  const approvedAt = approvalAgeAt(pr);
  return pr.review.state === "approved" && approvedAt != null && ageMs(approvedAt) >= approvedAgingMs;
}
function needsReReview(pr) {
  return pr.review.lastReviewedAt != null && pr.lastCommitAt != null &&
    (pr.review.state === "reviewed" || pr.review.state === "changes_requested") &&
    new Date(pr.lastCommitAt).getTime() > new Date(pr.review.lastReviewedAt).getTime();
}
function changedLineCount(pr) {
  return pr.additions + pr.deletions;
}
function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}
function releaseSignalMatches(value, release) {
  return new RegExp(`(^|[^0-9])${escapeRegExp(release)}([^0-9]|$)`, "i").test(value);
}
function targetsCurrentRelease(pr) {
  return [
    pr.title,
    pr.milestone,
    ...(pr.labels || []),
    ...((pr.linkedIssues || []).flatMap((issue) => [issue.title, issue.milestone, ...(issue.labels || [])])),
  ].some((value) => value != null && releaseSignalMatches(value, currentRelease));
}
function isQuickWin(pr) {
  const lines = changedLineCount(pr);
  return pr.review.state === "waiting" && !hasUnresolvedFeedback(pr) && !hasMergeConflicts(pr) &&
    isCoreTeamAuthor(pr.author) && !targetsCurrentRelease(pr) && (pr.linkedIssues || []).length <= 1 &&
    pr.commitCount <= 2 && pr.changedFiles > 0 && pr.changedFiles <= quickWinFileThreshold &&
    lines > 0 && lines <= quickWinLineThreshold && !isIdle(pr);
}
function needsReview(pr) {
  return pr.review.state === "waiting" && !hasUnresolvedFeedback(pr) && !hasMergeConflicts(pr) &&
    isCoreTeamAuthor(pr.author);
}

// ---------------------------------------------------------------------------
// Bucket classification
// ---------------------------------------------------------------------------

function reviewBucketLabels(pr) {
  const labels = [];
  if (hasRegressionSignal(pr)) labels.push(regressionBucketLabel);
  if (pr.draft) { labels.push("Draft"); return labels; }
  if (isCommunityPullRequest(pr) && !isAgedOutCommunityPullRequest(pr)) return labels;
  if (isBotAuthor(pr.author, pr.authorType)) labels.push("Bots / automation");
  if (isGeneratedDocsPullRequest(pr)) labels.push("Docs");
  if (isCommunityToolkitPullRequest(pr)) labels.push("Community Toolkit");

  const ciFailing = isChecksFailing(pr);
  if (ciFailing) labels.push("CI failing");
  const checksPending = isChecksPending(pr);
  const mergeConflicts = hasMergeConflicts(pr);
  if (mergeConflicts) labels.push("Merge conflicts");
  const approvedButAging = isApprovedButAging(pr);
  if (approvedButAging) labels.push(approvedButAgingBucketLabel);
  const unresolved = hasUnresolvedFeedback(pr);
  if (unresolved) labels.push("Unresolved feedback");
  const unresolvedBlocksMerge = unresolved && pr.review.requiresConversationResolution;

  if (pr.review.state === "approved" && !approvedButAging && !ciFailing && !checksPending &&
      !unresolvedBlocksMerge && !mergeConflicts && !hasNeedsAuthorActionLabel(pr) &&
      !isMergeReviewBlocked(pr)) {
    labels.push("Ready to merge");
  }
  if (needsReReview(pr)) labels.push("Re-review needed");
  if (pr.review.state === "changes_requested") labels.push("Author response");
  if (isIdle(pr)) labels.push("Stalled");
  if (isCommunityPullRequest(pr)) {
    labels.push(isAgedOutCommunityPullRequest(pr) ? agedOutCommunityBucketLabel : "Community");
  }
  if (isQuickWin(pr) && !ciFailing) labels.push("Quick wins");
  if (needsReview(pr)) labels.push("Needs review");
  if (labels.length === 0) labels.push("Review started");
  return labels;
}

function reviewSignal(pr, bucketLabel) {
  if (pr.draft) return "Draft";
  const approvedAt = approvalAgeAt(pr);
  switch (bucketLabel) {
    case regressionBucketLabel: return hasRegressionLabel(pr.labels) ? "Regression label" : "Regression";
    case approvedButAgingBucketLabel: return approvedAt ? `Approved ${formatAge(approvedAt)}` : "Approved";
    case "CI failing": {
      const fc = pr.checks?.failureCount ?? 0;
      return fc > 0 ? formatCount(fc, "failing check") : "CI failing";
    }
    case "Unresolved feedback": return formatCount(pr.review.unresolvedThreadCount, "unresolved thread");
    case "Ready to merge": return formatCount(pr.review.approvalCount, "approval");
    case "Re-review needed": return pr.lastCommitAt ? `Pushed ${formatAge(pr.lastCommitAt)}` : "Pushed after review";
    case "Merge conflicts": return "Merge conflicts";
    case "Docs": return "generated docs";
    case "Community Toolkit": return "CommunityToolkit/Aspire";
    case "Bots / automation": return "bot";
    case "Community": return isCommunityWaiting(pr) ? `Community · waiting ${formatAge(pr.createdAt)}` : "community";
    case agedOutCommunityBucketLabel: return agedOutCommunityBucketLabel;
    case "Quick wins": return reviewFootprint(pr);
    case "Needs review": return "No reviews";
    case "Stalled": return `Idle ${formatRelative(pr.updatedAt)}`;
    case "Author response": return "Changes requested";
    default: return formatCount(pr.review.reviewerCount, "reviewer");
  }
}

function reviewFootprint(pr) {
  const parts = [
    pr.changedFiles > 0 ? formatCount(pr.changedFiles, "file") : null,
    changedLineCount(pr) > 0 ? formatCount(changedLineCount(pr), "line") : null,
    pr.commitCount > 0 ? formatCount(pr.commitCount, "commit") : null,
  ].filter(Boolean);
  return parts.slice(0, 2).join(" · ") || "size unknown";
}

// ---------------------------------------------------------------------------
// Attention signals (faithful port of models.ts signal generators)
// ---------------------------------------------------------------------------

function isChecksPending(pr) {
  const state = visibleCheckState(pr);
  return state === "pending" || state === "unknown";
}

export function createAttentionSignals(item) {
  const pullRequest = item.pullRequest;
  const action = actionSignal(pullRequest);
  const signals = [action];

  // Merge conflicts are a hard blocker, so surface the pill right after the action signal —
  // otherwise a stacked PR (release + regression + CI) can push it past the signal limit and
  // hide the conflict entirely.
  if (hasMergeConflicts(pullRequest) && action.label !== "merge conflicts") {
    signals.push({ label: "merge conflicts", tone: "danger" });
  }

  const progress = reviewProgressSignal(pullRequest);
  // Approval progress should survive card-level signal limits; other review progress can stay
  // with the lower-priority metadata below.
  const prioritizeProgress = progress?.tone === "success" && pullRequest.review.approvalCount > 0;
  if (progress && prioritizeProgress) {
    signals.push(progress);
  }

  if (targetsCurrentRelease(pullRequest)) {
    signals.push({ label: `release ${currentRelease}`, tone: "danger" });
  }

  if (hasRegressionSignal(pullRequest)) {
    signals.push({ label: regressionBucketLabel.toLowerCase(), tone: "danger" });
  }

  if (pullRequest.baseRef?.startsWith("release/")) {
    signals.push({ label: `base ${pullRequest.baseRef}`, tone: "danger" });
  }

  const checksSignal = checksAttentionSignal(pullRequest);
  if (checksSignal) {
    signals.push(checksSignal);
  }

  if (hasUnresolvedFeedback(pullRequest)) {
    signals.push({
      label: formatCount(pullRequest.review.unresolvedThreadCount, "unresolved", "unresolved"),
      tone: "danger",
    });
  }

  const approvedAt = approvalAgeAt(pullRequest);
  if (isApprovedButAging(pullRequest) && approvedAt) {
    signals.push({ label: `approved ${formatAge(approvedAt)}`, tone: "danger" });
  }

  if (needsReReview(pullRequest)) {
    signals.push({ label: "commit after review", tone: "warning" });
  }

  if (isGeneratedDocsPullRequest(pullRequest)) {
    signals.push({ label: "docs", tone: "accent" });
  }

  if (isCommunityToolkitPullRequest(pullRequest)) {
    signals.push({ label: "community toolkit", tone: "accent" });
  }

  if (isAgedOutCommunityPullRequest(pullRequest)) {
    signals.push({ label: agedOutCommunityBucketLabel.toLowerCase(), tone: "warning" });
  } else if (isCommunityWaiting(pullRequest)) {
    signals.push({ label: "community wait", tone: "warning" });
  }

  if (isQuickWin(pullRequest)) {
    signals.push({ label: "quick win", tone: "success" });
  }

  if (isIdle(pullRequest)) {
    signals.push({ label: `idle ${formatAge(pullRequest.updatedAt)}`, tone: "warning" });
  }

  const ageSignal = oldFirstSignal(pullRequest);
  if (ageSignal) {
    signals.push(ageSignal);
  }

  signals.push({
    label: `open ${formatAge(pullRequest.createdAt)}`,
    tone: Date.now() - new Date(pullRequest.createdAt).getTime() >= 7 * dayMs ? "warning" : "muted",
  });

  if (progress && !prioritizeProgress) {
    signals.push(progress);
  }

  if (pullRequest.review.lastReviewedAt && pullRequest.review.state !== "waiting") {
    signals.push({ label: `reviewed ${formatAge(pullRequest.review.lastReviewedAt)}`, tone: "muted" });
  }

  if (pullRequest.review.commentedReviewCount > 0) {
    signals.push({ label: formatCount(pullRequest.review.commentedReviewCount, "review comment"), tone: "muted" });
  }

  const computedLabels = isGeneratedDocsPullRequest(pullRequest)
    ? pullRequest.labels.filter((label) => label.toLowerCase() !== docsFromCodeLabel)
    : pullRequest.labels;

  for (const label of computedLabels.slice(0, 2)) {
    signals.push({ label, tone: "accent" });
  }

  if (isBotAuthor(pullRequest.author, pullRequest.authorType)) {
    signals.push({ label: "bot", tone: "accent" });
  } else if (isCopilotAttributedAuthor(pullRequest.author)) {
    signals.push({ label: "copilot", tone: "accent" });
  }

  return signals.slice(0, 7);
}

function checksAttentionSignal(pullRequest) {
  const checks = pullRequest.checks;
  if (!checks || checks.state === "none" || checks.state === "unknown") {
    return null;
  }
  // An aggregate failure that's only the non-blocking rule (or indeterminate detail) gets
  // no red CI pill. When every failing check maps to a rule, surface the rule's label as a
  // warning instead of "CI failing". Ported from pr-dashboard models.ts checksAttentionSignal.
  if (isNonBlockingAggregateFailure(pullRequest)) {
    return null;
  }
  const nonBlockingFailure = nonBlockingOnlyFailureRule(pullRequest);
  if (nonBlockingFailure) {
    return { label: nonBlockingFailure.label, tone: "warning" };
  }
  if (checks.state === "failure") {
    const label = checks.failureCount > 0
      ? `CI failing · ${formatCount(checks.failureCount, "check")}`
      : "CI failing";
    return { label, tone: "danger" };
  }
  if (checks.state === "pending") {
    return { label: "CI running", tone: "warning" };
  }
  // Successful CI on dashboard rows is intentionally suppressed to avoid pill noise.
  return null;
}

function oldFirstSignal(pullRequest) {
  const activityAge = ageMs(pullRequestAgingReferenceAt(pullRequest));
  // Route through isReviewDebt so the emitted signal and the focus-retention predicate share one
  // definition (notably its approved-PR exclusion) and cannot drift.
  if (isReviewDebt(pullRequest)) {
    return { label: reviewDebtSignalLabel, tone: "danger" };
  }
  if (activityAge >= 7 * dayMs) {
    return { label: "old first", tone: "warning" };
  }
  if (activityAge < 12 * hourMs) {
    return { label: "newer", tone: "muted" };
  }
  return null;
}

function pullRequestAgingReferenceAt(pullRequest) {
  if (isChecksFailing(pullRequest)) {
    return ciActivityAt(pullRequest);
  }
  if (pullRequest.review.state === "approved") {
    return reviewActivityAt(pullRequest);
  }
  if (needsReReview(pullRequest)) {
    return reReviewActivityAt(pullRequest);
  }
  if (pullRequest.review.state === "reviewed" || pullRequest.review.state === "changes_requested") {
    return reviewedActivityAt(pullRequest);
  }
  return pullRequest.updatedAt;
}

function reviewActivityAt(pullRequest) {
  return latestDate(pullRequest.review.lastApprovedAt, pullRequest.review.lastReviewedAt) ?? pullRequest.updatedAt;
}
function reReviewActivityAt(pullRequest) {
  return pullRequest.lastCommitAt ?? pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt;
}
function reviewedActivityAt(pullRequest) {
  return pullRequest.review.lastReviewedAt ?? pullRequest.updatedAt;
}
function ciActivityAt(pullRequest) {
  return latestDate(pullRequest.checks?.completedAt, pullRequest.lastCommitAt) ?? pullRequest.updatedAt;
}

function actionSignal(pullRequest) {
  if (hasRegressionSignal(pullRequest)) {
    return { label: regressionBucketLabel.toLowerCase(), tone: "danger" };
  }
  if (pullRequest.draft) {
    return { label: "draft", tone: "muted" };
  }
  if (isBotAuthor(pullRequest.author, pullRequest.authorType)) {
    return { label: "automation", tone: "accent" };
  }
  if (isGeneratedDocsPullRequest(pullRequest)) {
    return { label: "docs review", tone: "accent" };
  }
  if (isCommunityToolkitPullRequest(pullRequest)) {
    return { label: "toolkit review", tone: "accent" };
  }
  if (isChecksFailing(pullRequest)) {
    return { label: "fix CI", tone: "danger" };
  }
  if (hasMergeConflicts(pullRequest)) {
    return { label: "merge conflicts", tone: "danger" };
  }
  if (hasUnresolvedFeedback(pullRequest)) {
    return { label: "resolve feedback", tone: "danger" };
  }
  if (isApprovedButAging(pullRequest)) {
    return { label: "land approval", tone: "danger" };
  }
  if (pullRequest.review.state === "approved") {
    return isChecksPending(pullRequest)
      ? { label: "wait for CI", tone: "warning" }
      : { label: "merge", tone: "success" };
  }
  if (needsReReview(pullRequest)) {
    return { label: "re-review", tone: "warning" };
  }
  if (pullRequest.review.state === "changes_requested") {
    return { label: "author fix", tone: "danger" };
  }
  if (isAgedOutCommunityPullRequest(pullRequest)) {
    return { label: agedOutCommunityBucketLabel.toLowerCase(), tone: "warning" };
  }
  if (isIdle(pullRequest)) {
    return { label: "unstick", tone: "warning" };
  }
  if (!pullRequest.isMine && !pullRequest.repoPrivate && isCommunityAuthor(pullRequest.author, pullRequest.authorType)) {
    return isCommunityWaiting(pullRequest)
      ? { label: "community wait", tone: "warning" }
      : { label: "community", tone: "accent" };
  }
  if (isQuickWin(pullRequest)) {
    return { label: "quick win", tone: "success" };
  }
  if (pullRequest.review.state === "waiting") {
    return { label: "needs reviewer", tone: "warning" };
  }
  return { label: "finish review", tone: "accent" };
}

function reviewProgressSignal(pullRequest) {
  if (pullRequest.review.state === "waiting") {
    return { label: "no reviews", tone: "warning" };
  }
  if (pullRequest.review.state === "changes_requested") {
    return {
      label: formatCount(Math.max(1, pullRequest.review.changesRequestedCount), "change request"),
      tone: "danger",
    };
  }
  if (pullRequest.review.approvalCount > 0) {
    return { label: formatCount(pullRequest.review.approvalCount, "approval"), tone: "success" };
  }
  if (pullRequest.review.reviewerCount > 0) {
    return { label: `${formatCount(pullRequest.review.reviewerCount, "reviewer")} · 0 approvals`, tone: "accent" };
  }
  return null;
}

const BUCKET_DEFS = [
  { label: regressionBucketLabel, tone: "danger" },
  { label: approvedButAgingBucketLabel, tone: "danger" },
  { label: "CI failing", tone: "danger" },
  { label: "Merge conflicts", tone: "danger" },
  { label: "Unresolved feedback", tone: "danger" },
  { label: "Ready to merge", tone: "success" },
  { label: "Re-review needed", tone: "warning" },
  { label: "Docs", tone: "accent" },
  { label: "Community Toolkit", tone: "accent" },
  { label: "Bots / automation", tone: "accent" },
  { label: agedOutCommunityBucketLabel, tone: "warning" },
  { label: "Quick wins", tone: "success" },
  { label: "Needs review", tone: "warning" },
  { label: "Review started", tone: "accent" },
  { label: "Stalled", tone: "warning" },
  { label: "Author response", tone: "danger" },
  { label: "Draft", tone: "accent" },
];

export function createAttentionBuckets(prs, login) {
  const defs = [...BUCKET_DEFS];
  if (login) defs.push({ label: myDraftPullRequestsBucketLabel, tone: "accent" });
  const buckets = defs.map((d) => ({ label: d.label, tone: d.tone, items: [] }));
  const byLabel = new Map(buckets.map((b) => [b.label, b]));

  const visible = prs.filter((p) => p.state === "open" && !hasNeedsAuthorActionLabel(p));
  for (const pr of visible) {
    for (const label of reviewBucketLabels(pr)) {
      byLabel.get(label)?.items.push({ pullRequest: pr, reason: reviewSignal(pr, label) });
    }
  }
  if (login) {
    const myDrafts = byLabel.get(myDraftPullRequestsBucketLabel);
    for (const pr of prs) {
      if (pr.state === "open" && pr.draft && pr.isMine) {
        myDrafts?.items.push({ pullRequest: pr, reason: "Draft" });
      }
    }
  }
  return buckets.filter((b) => b.items.length > 0);
}

// ---------------------------------------------------------------------------
// Focus age + focused "Needs attention" queue
// ---------------------------------------------------------------------------

function pullRequestFocusActivityAt(pr, bucketLabel) {
  switch (bucketLabel) {
    case approvedButAgingBucketLabel:
    case "Ready to merge":
      return latestDate(pr.review.lastApprovedAt, pr.review.lastReviewedAt) ?? pr.updatedAt;
    case "Re-review needed":
      return pr.lastCommitAt ?? pr.review.lastReviewedAt ?? pr.updatedAt;
    case "Author response":
    case "Review started":
      return pr.review.lastReviewedAt ?? pr.updatedAt;
    case "CI failing":
      return latestDate(pr.checks?.completedAt, pr.lastCommitAt) ?? pr.updatedAt;
    default:
      return pr.updatedAt;
  }
}
function isWithinFocusAgeLimit(pr, bucketLabel) {
  return ageMs(pullRequestFocusActivityAt(pr, bucketLabel)) <= focusAgeLimitMs;
}
// A PR is in "review debt" once it has aged past the focus limit without yet earning an
// approving review. oldFirstSignal() calls this directly so the "review debt" signal and this
// retention predicate can never drift. Approved PRs are excluded: they have already been
// reviewed and carry a merge-oriented lane ("Approved but aging" -> "land approval"), so
// flagging them "review debt" / "Address review" would mislabel the action.
function isReviewDebt(pr) {
  return pr.review.state !== "approved" && ageMs(pullRequestAgingReferenceAt(pr)) >= focusAgeLimitMs;
}

const excludedFocusBucketLabels = new Set(["Stalled", "Draft", "My draft PRs", "Docs", "Community Toolkit", "Bots / automation", "Community", "Aged out community", "Unresolved feedback", "Merge conflicts", "CI failing", "Author response"]);
const disqualifyingFocusBucketLabels = new Set(["Draft", "My draft PRs", "Docs", "Community Toolkit", "Bots / automation", "Community", "Aged out community", "Unresolved feedback", "Merge conflicts"]);
const specializedFocusBucketLabels = new Set(["Docs", "Community Toolkit", "Bots / automation", "Community", "Aged out community"]);
const focusBucketRanks = new Map([
  ["Regression", -2], ["Approved but aging", 0], ["Re-review needed", 1], ["Ready to merge", 2],
  ["Author response", 3], ["Needs review", 4], ["Quick wins", 5], ["Review started", 6],
]);
function focusBucketRank(label) {
  return focusBucketRanks.has(label) ? focusBucketRanks.get(label) : Number.MAX_SAFE_INTEGER;
}
function prKey(pr) {
  return `${pr.repository.toLowerCase()}#${pr.number}`;
}

export function computeFocusItems(buckets) {
  const blocked = new Set(
    buckets.filter((b) => disqualifyingFocusBucketLabels.has(b.label))
      .flatMap((b) => b.items.map((i) => prKey(i.pullRequest))),
  );
  // Changes-requested PRs are waiting on the author, so they drop out of the focused
  // queue entirely (mirrors pr-dashboard #91) unless the author has already pushed a
  // response and the PR now needs re-review.
  for (const [key, labels] of bucketLabelsByPr(buckets)) {
    if (isWaitingOnAuthor(labels)) blocked.add(key);
  }
  const flat = buckets
    // "Stalled" is normally excluded so idle PRs don't flood focus, but a review-debt PR whose
    // ONLY lane is "Stalled" (e.g. reviewed-then-gone-quiet, so no "Needs review"/actionable
    // bucket) would never reach the retention filter below and the "Address review" action would
    // be unreachable — the exact case oldFirstSignal() still flags. Let those single Stalled
    // items through; the isReviewDebt gate keeps 7–14d stalled PRs out, and the age/community/CI
    // filters below still apply. Non-Stalled excluded buckets stay fully excluded.
    .filter((b) => !excludedFocusBucketLabels.has(b.label) || b.label === "Stalled")
    .flatMap((b) => b.items
      .filter((i) => b.label !== "Stalled" || isReviewDebt(i.pullRequest))
      .map((i) => ({ ...i, bucketLabel: b.label, bucketTone: b.tone })));

  const byPr = new Map();
  for (const item of flat) {
    const key = prKey(item.pullRequest);
    if (blocked.has(key)) continue;
    const existing = byPr.get(key);
    if (!existing || focusBucketRank(item.bucketLabel) < focusBucketRank(existing.bucketLabel)) {
      byPr.set(key, item);
    }
  }
  return [...byPr.values()]
    // Retain PRs within the focus window, plus review-debt PRs that have aged PAST the limit
    // without a review. Those are exactly the cards the "review debt" signal / "Address review"
    // action target (constants.mjs reviewDebtSignalLabel). Dropping them here previously made
    // that action unreachable: the signal only fires at/after the same age boundary this filter
    // cut them off at, so the two overlapped only at the exact 14-day instant. Keeping them
    // makes the focus source set consistent with the signal that flags it.
    .filter((i) => isWithinFocusAgeLimit(i.pullRequest, i.bucketLabel) || isReviewDebt(i.pullRequest))
    .filter((i) => !isCommunityPullRequest(i.pullRequest))
    .filter((i) => !isChecksFailing(i.pullRequest))
    .sort((a, b) => new Date(b.pullRequest.updatedAt) - new Date(a.pullRequest.updatedAt));
}

export function computeCommunityItems(prs) {
  return prs
    .filter((p) => p.state === "open" && !p.draft && !hasNeedsAuthorActionLabel(p) &&
      isCommunityPullRequest(p) && !isAgedOutCommunityPullRequest(p))
    .map((p) => ({ pullRequest: p, reason: "Community", bucketTone: "accent" }))
    .sort((a, b) => new Date(b.pullRequest.updatedAt) - new Date(a.pullRequest.updatedAt));
}

export function computeFocusExclusionItems(prs, buckets, focusItems, login) {
  if (!login) return [];
  const focusKeys = new Set(focusItems.map((i) => prKey(i.pullRequest)));
  const labelsByPr = bucketLabelsByPr(buckets);
  return prs
    .filter((p) => p.state === "open" && !p.draft &&
      p.isMine && !focusKeys.has(prKey(p)))
    .map((p) => {
      const bucketLabels = labelsByPr.get(prKey(p)) ?? [];
      return { pullRequest: p, reason: focusExclusionReason(p, bucketLabels) };
    })
    .sort((a, b) => exReasonRank(a.reason.kind) - exReasonRank(b.reason.kind) ||
      new Date(b.pullRequest.updatedAt) - new Date(a.pullRequest.updatedAt));
}

function focusExclusionReason(pr, bucketLabels) {
  if (isChecksFailing(pr)) return { kind: "ci-failing", label: "CI failing", detail: "Failing checks keep it out until CI is green again.", tone: "danger" };
  if (hasMergeConflicts(pr)) return { kind: "merge-conflicts", label: "Merge conflicts", detail: "The author needs to rebase before reviewers can finish it.", tone: "danger" };
  if (pr.review.unresolvedThreadCount > 0) return { kind: "unresolved-feedback", label: "Unresolved feedback", detail: "Open review threads make it author-blocked.", tone: "danger" };
  if (hasNeedsAuthorActionLabel(pr)) return { kind: "held-by-label", label: "Held by label", detail: "A do-not-merge label keeps it out of the focused queue.", tone: "danger" };
  if (isWaitingOnAuthor(bucketLabels)) return { kind: "author-response", label: "Author response", detail: "Changes were requested, so this is waiting on the author rather than the focused queue.", tone: "danger" };
  if (isCommunityPullRequest(pr) && !isAgedOutCommunityPullRequest(pr)) return { kind: "community-list", label: "Community list", detail: "Active external-contributor PRs show in the Community list.", tone: "accent" };
  const specialized = bucketLabels.find((l) => specializedFocusBucketLabels.has(l));
  if (specialized) return { kind: "specialized-lane", label: `${specialized} lane`, detail: `Routed to the ${specialized} lane instead of Needs attention.`, tone: "accent" };
  const candidate = [...bucketLabels].filter((l) => !excludedFocusBucketLabels.has(l)).sort((a, b) => focusBucketRank(a) - focusBucketRank(b))[0] ?? null;
  if (candidate && !isWithinFocusAgeLimit(pr, candidate)) return { kind: "stale-activity", label: "Stale activity", detail: "Its actionable lane has not had fresh activity in 14 days.", tone: "warning" };
  if (bucketLabels.includes("Stalled")) return { kind: "stalled-only", label: "Stalled only", detail: "It has gone quiet with no fresher actionable lane.", tone: "warning" };
  return { kind: "outside-queue", label: "Outside queue", detail: "It does not currently match a focused, actionable lane.", tone: "muted" };
}
function exReasonRank(kind) {
  return ["ci-failing", "merge-conflicts", "unresolved-feedback", "held-by-label", "author-response", "stale-activity", "community-list", "specialized-lane", "stalled-only", "outside-queue"].indexOf(kind);
}

// A PR with requested changes is waiting on its author, not the focused queue. Once the
// author pushes a response and it needs re-review, it re-enters under "Re-review needed".
function isWaitingOnAuthor(bucketLabels) {
  return bucketLabels.includes("Author response") && !bucketLabels.includes("Re-review needed");
}
function bucketLabelsByPr(buckets) {
  const map = new Map();
  for (const b of buckets) {
    for (const i of b.items) {
      const k = prKey(i.pullRequest);
      if (!map.has(k)) map.set(k, []);
      map.get(k).push(b.label);
    }
  }
  return map;
}

// ---------------------------------------------------------------------------
// Personal "For you" picks
// ---------------------------------------------------------------------------

export function createForMeItems(prs, logins) {
  const loginList = Array.isArray(logins) ? logins.filter(Boolean) : (logins ? [logins] : []);
  if (!loginList.length) return [];
  return prs
    .filter((p) => p.state === "open" && !p.draft && !isOwnCopilotAuthorAny(p.author, loginList))
    .map((p) => createPersonalPick(p))
    .filter(Boolean)
    .sort((a, b) => pickScore(b) - pickScore(a))
    .slice(0, 10);
}

function createPersonalPick(pr) {
  const mine = pr.isMine;
  if (hasMergeConflicts(pr)) {
    return mine ? { pullRequest: pr, action: personalPickActions.resolveConflicts, reason: `Your PR has merge conflicts · ${pickReason(pr)}`, tone: "danger", personal: true } : null;
  }
  if (hasNeedsAuthorActionLabel(pr)) {
    return mine ? { pullRequest: pr, action: personalPickActions.needsAttention, reason: `Your PR is labeled ${matchingDoNotMergeLabel(pr) ?? "no-merge"} · ${pickReason(pr)}`, tone: "danger", personal: true } : null;
  }
  if (mine && isChecksFailing(pr)) {
    const fc = pr.checks.failureCount ?? 0;
    return { pullRequest: pr, action: personalPickActions.fixCi, reason: `${fc > 0 ? `Your PR has ${formatCount(fc, "failing check")}` : "Your PR is failing CI"} · ${pickReason(pr)}`, tone: "danger", personal: true };
  }
  if (!mine && pr.review.reviewRequestedFromViewer) {
    const visibleState = visibleCheckState(pr);
    const ci = isChecksFailing(pr) ? " · CI failing" : visibleState === "pending" ? " · CI running" : "";
    return { pullRequest: pr, action: personalPickActions.reviewThis, reason: `Review requested from you · ${pickReason(pr)}${ci}`, tone: "warning", personal: true };
  }
  if (mine && pr.review.state === "changes_requested") {
    return { pullRequest: pr, action: personalPickActions.respondHere, reason: `Your PR has changes requested · ${pickReason(pr)}`, tone: "danger", personal: true };
  }
  if (mine && pr.review.state === "approved") {
    return { pullRequest: pr, action: personalPickActions.finishThis, reason: `Your PR is approved and still open · ${pickReason(pr)}`, tone: "success", personal: true };
  }
  return null;
}

function pickScore(item) {
  let score = item.personal ? 1000 : 0;
  const a = item.action;
  if (a === personalPickActions.resolveConflicts) score += 200;
  if (a === personalPickActions.needsAttention) score += 190;
  if (a === personalPickActions.fixCi) score += 110;
  if (a === personalPickActions.reviewThis) score += 90;
  if (a === personalPickActions.finishThis) score += 80;
  if (a === personalPickActions.respondHere) score += 75;
  const st = item.pullRequest.review.state;
  if (st === "changes_requested") score += 30;
  if (st === "waiting") score += 45;
  if (st === "reviewed") score += 25;
  if (st === "approved") score += 5;
  if (isBotAuthor(item.pullRequest.author, item.pullRequest.authorType)) score -= 120;
  return score + Math.min(3, Math.floor(ageMs(item.pullRequest.createdAt) / dayMs)) +
    Math.min(1, Math.floor(ageMs(item.pullRequest.updatedAt) / dayMs));
}

function pickReason(pr) {
  const signals = [`open ${formatAge(pr.createdAt)}`];
  if (pr.review.approvalCount > 0) signals.push(formatCount(pr.review.approvalCount, "approval"));
  else if (pr.review.reviewerCount > 0) signals.push(`${formatCount(pr.review.reviewerCount, "reviewer")} · 0 approvals`);
  else signals.push("no reviews");
  if (isIdle(pr)) signals.push(`idle ${formatAge(pr.updatedAt)}`);
  return signals.join(" · ");
}

// ---------------------------------------------------------------------------
// Core-team ownership
// ---------------------------------------------------------------------------

export function createDeveloperPullRequestCounts(prs) {
  const byDev = new Map(coreTeamMembers.map((m) => [actorIdentityKey(m), []]));
  const actorByKey = new Map(coreTeamMembers.map((m) => [actorIdentityKey(m), m]));
  for (const pr of prs) {
    if (pr.state !== "open" || isCommunityToolkitPullRequest(pr) || pr.draft || hasMergeConflicts(pr) || hasNeedsAuthorActionLabel(pr)) continue;
    const owner = coreTeamOwnershipActor(pr.author);
    if (!owner) continue;
    const key = actorIdentityKey(owner);
    if (!byDev.has(key)) { byDev.set(key, []); actorByKey.set(key, owner); }
    byDev.get(key).push(pr);
  }
  return [...byDev.entries()]
    .map(([key, list]) => {
      const latest = list.map((p) => p.updatedAt).sort((a, b) => new Date(b) - new Date(a))[0];
      return { actor: actorByKey.get(key) ?? key, openPullRequestCount: list.length, latestUpdatedAt: latest ?? null };
    })
    .filter((c) => c.openPullRequestCount > 0)
    .sort((a, b) => b.openPullRequestCount - a.openPullRequestCount || a.actor.localeCompare(b.actor));
}

// ---------------------------------------------------------------------------
// Issues focus buckets + signals (port of pr-dashboard models.ts ship-week issue
// intelligence). Adapted for the GraphQL issue shape this extension collects: we do
// NOT have per-issue linked-PR data (the dashboard's dedicated server endpoint supplies
// `linkedOpenPullRequests`), so any signal that depends on it ("Needs PR", the open-PR
// count pill, and the linked-PR validation branch) is skipped rather than guessed, which
// would otherwise mislabel every issue "Needs PR".
// ---------------------------------------------------------------------------

const ctiTeamIssueBucketLabel = "CTI team";
const afscromeIssueBucketLabel = "afscrome finds";
const myIssuesBucketLabel = "My issues";
const releaseBlockingSignalLabel = "Blocking release";
const staleIssueMs = 7 * dayMs;
const coldIssueMs = 14 * dayMs;

const validationTerms = ["validation", "validate", "verify", "verification", "test", "e2e", "servicing validation"];
const installerTerms = ["installer", "install", "workload", "acquisition", "setup", "visual studio", " vs ", "sdk"];
const typeScriptTerms = ["typescript", " ts ", "javascript", " js ", "node", "polyglot", "apphost"];
const cliTerms = ["cli", "channel", "version", "versioning", "feed", "template"];
const docsTerms = ["docs", "documentation", "release notes", "readme", "release readiness", "announcement"];

// Each definition matches a class of issue that deserves its own focus lane. `countsAsDomain`
// marks the issue as already owned by a domain (suppresses the "Unowned" pill); `needsValidation`
// forces the "Needs validation" pill.
const focusIssueBucketDefinitions = [
  { label: regressionBucketLabel, tone: "danger", matches: (issue) => hasRegressionLabel(issue.labels) },
  { label: ctiTeamIssueBucketLabel, tone: "warning", matches: isCtiTeamIssue, countsAsDomain: true, needsValidation: true, suppressNeedsPr: true },
  { label: afscromeIssueBucketLabel, tone: "success", matches: isAfscromeIssue },
];

const shipWeekIssueSignalToneByLabel = new Map([
  ...focusIssueBucketDefinitions.map((definition) => [definition.label, definition.tone]),
  ["Needs PR", "danger"],
  ["Needs validation", "warning"],
  ["Installer/acquisition", "accent"],
  ["TypeScript/polyglot", "accent"],
  ["CLI channel/versioning", "accent"],
  ["Docs/release readiness", "success"],
  ["Unowned", "warning"],
]);

function isCtiTeamIssue(issue) {
  return String(issue.title || "").toLowerCase().includes(ctiTeamTitleMarker);
}
function isAfscromeIssue(issue) {
  return sameLogin(issue.author, afscromeIssueAuthor);
}
function hasReleaseBlockingLabel(labels) {
  return (labels || []).some((label) => label.toLowerCase().includes(releaseBlockingLabelMarker));
}
function matchingFocusIssueBucketDefinitions(issue) {
  return focusIssueBucketDefinitions.filter((definition) => definition.matches(issue));
}
function firstFocusIssueBucketDefinition(issue) {
  return matchingFocusIssueBucketDefinitions(issue)[0];
}
function targetsCurrentReleaseIssue(issue) {
  if (!currentRelease) return false;
  return [issue.title, issue.milestone, ...(issue.labels || [])]
    .some((value) => value != null && releaseSignalMatches(value, currentRelease));
}
function issueMatchesTerms(issue, terms) {
  const searchText = ` ${[issue.title, issue.author, ...(issue.labels || [])].join(" ").toLowerCase()} `;
  return terms.some((term) => searchText.includes(term));
}
// Whether per-issue linked-PR data is available. This extension does not fetch it, so the
// value is treated as unknown and the linked-PR-derived signals are omitted.
function linkedOpenPullRequestCount(issue) {
  return Array.isArray(issue.linkedOpenPullRequests) ? issue.linkedOpenPullRequests.length : null;
}

// Focus lanes for the Issues view. Returns non-empty buckets (each sorted newest-first),
// plus a per-login "My issues" bucket when a login is supplied. Ported from pr-dashboard
// models.ts `createFocusIssueBuckets`.
export function createFocusIssueBuckets(issues, login) {
  const definitions = login
    ? [
      ...focusIssueBucketDefinitions,
      {
        label: myIssuesBucketLabel,
        tone: "accent",
        matches: (issue) => (issue.assignees || []).some((assignee) => sameLogin(assignee, login)),
      },
    ]
    : focusIssueBucketDefinitions;

  return definitions
    .map(({ matches, ...definition }) => {
      const bucketIssues = issues
        .filter(matches)
        .sort((first, second) => new Date(second.updatedAt).getTime() - new Date(first.updatedAt).getTime());
      return bucketIssues.length === 0 ? null : { ...definition, issues: bucketIssues };
    })
    .filter((bucket) => bucket !== null);
}

// Signal pills for a single issue (leading action pill + release/domain/idle/label context).
// Ported from pr-dashboard models.ts `createIssueSignals`, minus the linked-PR pills.
export function createIssueSignals(issue) {
  const action = issueActionSignal(issue);
  const signals = [action];

  if (currentRelease && targetsCurrentReleaseIssue(issue)) {
    signals.push({ label: `release ${currentRelease}`, tone: "danger" });
  }

  const linkedCount = linkedOpenPullRequestCount(issue);
  if (linkedCount != null && linkedCount > 0) {
    signals.push({ label: formatCount(linkedCount, "open PR"), tone: "warning" });
  }

  for (const signalLabel of shipWeekIssueSignalLabels(issue)) {
    if (signalLabel !== action.label) {
      signals.push({ label: signalLabel, tone: shipWeekIssueSignalToneByLabel.get(signalLabel) ?? "muted" });
    }
  }

  const updatedAge = ageMs(issue.updatedAt);
  if (updatedAge >= coldIssueMs) {
    signals.push({ label: `idle ${formatAge(issue.updatedAt)}`, tone: "danger" });
  } else if (updatedAge >= staleIssueMs) {
    signals.push({ label: `idle ${formatAge(issue.updatedAt)}`, tone: "warning" });
  }

  if (issue.milestone) {
    signals.push({ label: "milestone", tone: "accent" });
  }

  for (const label of (issue.labels || []).slice(0, 2)) {
    signals.push({ label, tone: "accent" });
  }

  if (isBotAuthor(issue.author, issue.authorType)) {
    signals.push({ label: "bot", tone: "accent" });
  }

  return dedupeIssueSignals(signals).slice(0, 7);
}

function issueActionSignal(issue) {
  if (hasReleaseBlockingLabel(issue.labels)) {
    return { label: releaseBlockingSignalLabel, tone: "danger" };
  }
  const focusDefinition = firstFocusIssueBucketDefinition(issue);
  if (focusDefinition) {
    return { label: focusDefinition.label, tone: focusDefinition.tone };
  }
  // "Needs PR" (linkedCount === 0) is intentionally omitted: without linked-PR data we
  // cannot tell an issue has no PR, and defaulting to "Needs PR" would tag everything.
  if ((issue.assignees || []).length === 0) {
    return { label: "Unowned", tone: "warning" };
  }
  return { label: "Needs validation", tone: "warning" };
}

function shipWeekIssueSignalLabels(issue) {
  const labels = [];
  let domainMatch = false;
  const focusDefinitions = matchingFocusIssueBucketDefinitions(issue);
  const linkedCount = linkedOpenPullRequestCount(issue);

  for (const definition of focusDefinitions) {
    labels.push(definition.label);
    domainMatch = domainMatch || definition.countsAsDomain === true;
  }

  // "Needs PR" only when we positively know there are zero linked PRs.
  if (linkedCount === 0 && !focusDefinitions.some((definition) => definition.suppressNeedsPr)) {
    labels.push("Needs PR");
  }

  if (
    focusDefinitions.some((definition) => definition.needsValidation)
    || (linkedCount != null && linkedCount > 0)
    || issueMatchesTerms(issue, validationTerms)
  ) {
    labels.push("Needs validation");
  }

  if (issueMatchesTerms(issue, installerTerms)) { labels.push("Installer/acquisition"); domainMatch = true; }
  if (issueMatchesTerms(issue, typeScriptTerms)) { labels.push("TypeScript/polyglot"); domainMatch = true; }
  if (issueMatchesTerms(issue, cliTerms)) { labels.push("CLI channel/versioning"); domainMatch = true; }
  if (issueMatchesTerms(issue, docsTerms)) { labels.push("Docs/release readiness"); domainMatch = true; }

  if ((issue.assignees || []).length === 0 && !domainMatch) {
    labels.push("Unowned");
  }

  return labels;
}

function dedupeIssueSignals(signals) {
  const seen = new Set();
  return signals.filter((signal) => {
    const key = signal.label.trim().toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

// ---------------------------------------------------------------------------
// Small formatting helpers (port of utils/format.ts essentials)
// ---------------------------------------------------------------------------

function ageMs(value) {
  return Date.now() - new Date(value).getTime();
}
function latestDate(...values) {
  let best = null;
  let bestTime = -Infinity;
  for (const v of values) {
    if (!v) continue;
    const t = new Date(v).getTime();
    if (t > bestTime) { bestTime = t; best = v; }
  }
  return best;
}
export function formatCount(n, singular, plural) {
  const word = n === 1 ? singular : (plural ?? singular + "s");
  return `${n} ${word}`;
}
export function formatAge(iso) {
  const s = Math.max(0, Math.floor(ageMs(iso) / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60); if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60); if (h < 24) return `${h}h`;
  const d = Math.floor(h / 24); if (d < 30) return `${d}d`;
  const mo = Math.floor(d / 30); if (mo < 12) return `${mo}mo`;
  return `${Math.floor(mo / 12)}y`;
}
export function formatRelative(iso) {
  return `${formatAge(iso)} ago`;
}
