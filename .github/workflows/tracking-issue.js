// Generic helpers for "tracking issues": a single deduplicated GitHub issue per
// subject, identified by a hidden HTML-comment marker, whose failures accrue as
// one comment per event (a failed run, a broken build, ...). The issue body is a
// fixed description written once at creation; every subsequent failure is a new
// comment. The comment both fires @notifications and — via a hidden per-run
// marker — is the dedup key, so there is no body rewriting and no ordering window.
//
// This module owns the reusable *mechanics* — marker-based dedup lookup, the
// per-run comment-recording loop, and the octokit primitives to
// create/comment/close an issue. Callers own the *content and policy*: they build
// the marker text, title, body, comments, and labels, and decide what action to
// take. Nothing here is specific to any repository, label, workflow, or product —
// keep it that way.
//
// The pure helpers (no network) are unit-tested via
// tests/Infrastructure.Tests/WorkflowScripts/TrackingIssueTests.cs. The octokit
// primitives and recordRun are exercised by the consumers' integration paths.

'use strict';

// ---------------------------------------------------------------------------
// Pure: marker mechanics
// ---------------------------------------------------------------------------

// Returns the oldest issue (lowest number) whose body carries the marker.
// "Oldest wins" gives a deterministic canonical issue. Concurrent double-filing is
// already unlikely — most consumer workflows serialize via a `concurrency` group,
// and the rest only file on their (infrequent) scheduled run — but if a duplicate
// is ever filed (e.g. one created manually) the older one stays canonical and the
// duplicate has to be closed by hand.
function findIssueForMarker(issues, marker) {
    const matches = (issues ?? []).filter(
        issue => typeof issue?.body === 'string' && issue.body.includes(marker));
    if (matches.length === 0) {
        return null;
    }

    return matches.reduce((oldest, issue) => (issue.number < oldest.number ? issue : oldest));
}

function findOpenIssueForMarker(issues, marker) {
    return findIssueForMarker((issues ?? []).filter(issue => issue?.state === undefined || issue.state === 'open'), marker);
}

// Hidden marker embedded in each failure comment, e.g.
//   <!-- run:12345678 -->
// `runId` is stable across re-runs of the same run, so this is the dedup key that
// stops a poller (or a re-run) from posting a second comment for a run already
// recorded. Kept in an HTML comment so it never renders in the issue thread.
function runMarker(runId) {
    return `<!-- run:${runId} -->`;
}

// Builds a standard tracking-issue body: the hidden marker, an optional hidden
// auto-close stamp, a one-line lead, then a short explanatory note (closing
// policy + docs link). Consumers supply only the parts that differ; the skeleton
// (marker placement, spacing) is shared so every tracking issue reads the same.
// The body is static — failures are recorded as comments, not appended here.
//
// `autoClose` (optional) embeds a hidden close-policy stamp right after the
// marker (see autoCloseStamp). Pass `true` for issues a green run should close
// automatically (infra/automation), `false` for issues a human must close after
// triage (test failures). Omit it when the body carries no close policy.
function buildBody({ marker, lead, note, autoClose }) {
    const head = autoClose === undefined ? [marker] : [marker, autoCloseStamp(autoClose)];
    return [
        ...head,
        '',
        lead,
        '',
        ...note,
        '',
    ].join('\n');
}

// Hidden close-policy stamp embedded in an issue body, e.g.
//   <!-- autoclose:true -->
// `true`  => a watchdog may close the issue when the subject's latest run is green.
// `false` => the issue must be closed by a human (e.g. a flaky test where a single
//            green run does not prove the problem is fixed).
// Kept in an HTML comment so it never renders in the issue.
function autoCloseStamp(autoClose) {
    return `<!-- autoclose:${autoClose ? 'true' : 'false'} -->`;
}

// Reads the close-policy stamp from an issue body. Returns `true`/`false` when a
// stamp is present, or `null` when it is missing or unparseable. Callers MUST
// treat `null` conservatively (do not auto-close) so a human-edited body or an
// issue filed before stamping was introduced is never closed out from under a
// triager. Matches the autoCloseStamp shape, tolerant of surrounding whitespace:
//   <!-- autoclose:true -->  /  <!--autoclose:false-->
function readAutoClose(body) {
    if (typeof body !== 'string') {
        return null;
    }

    const match = /<!--\s*autoclose:(true|false)\s*-->/i.exec(body);
    if (match === null) {
        return null;
    }

    return match[1].toLowerCase() === 'true';
}

// ---------------------------------------------------------------------------
// Octokit primitives (network). Thin wrappers; no policy.
// ---------------------------------------------------------------------------

// Idempotently ensures a label exists. A 422 means it already exists.
async function ensureLabel(github, owner, repo, { name, color, description }) {
    try {
        await github.rest.issues.createLabel({ owner, repo, name, color, description });
    } catch (error) {
        if (error.status !== 422) {
            throw error;
        }
    }
}

// Lists issues carrying a label. Uses the (strongly-consistent) list
// API rather than Search, whose eventual-consistency window would let
// near-simultaneous pollers each see "0 hits" and file duplicates.
async function listIssuesByLabel(github, owner, repo, label, { state = 'all' } = {}) {
    const items = await github.paginate(github.rest.issues.listForRepo, {
        owner, repo, labels: label, state, per_page: 100,
    });
    // listForRepo returns pull requests too (they are issues in the REST model).
    // Exclude them so a labeled PR whose body happens to carry a tracking marker is
    // never mistaken for the managed issue and then commented/closed in its place.
    return items.filter(item => !item.pull_request);
}

async function listOpenIssuesByLabel(github, owner, repo, label) {
    return await listIssuesByLabel(github, owner, repo, label, { state: 'open' });
}

async function createIssue(github, owner, repo, { title, body, labels }) {
    const created = await github.rest.issues.create({ owner, repo, title, body, labels });
    return created.data;
}

async function addComment(github, owner, repo, issueNumber, body) {
    await github.rest.issues.createComment({ owner, repo, issue_number: issueNumber, body });
}

async function closeIssue(github, owner, repo, issueNumber, { stateReason = 'completed' } = {}) {
    await github.rest.issues.update({ owner, repo, issue_number: issueNumber, state: 'closed', state_reason: stateReason });
}

async function reopenIssue(github, owner, repo, issueNumber) {
    await github.rest.issues.update({ owner, repo, issue_number: issueNumber, state: 'open' });
}

// True when a comment carrying `marker` already exists on the issue. The marker is
// the hidden per-run token (see runMarker); scanning comments — never collapsed or
// truncated — reliably detects "already recorded this exact run".
async function hasCommentForRun(github, owner, repo, issueNumber, marker) {
    const comments = await github.paginate(github.rest.issues.listComments, {
        owner, repo, issue_number: issueNumber, per_page: 100,
    });
    return comments.some(comment => typeof comment?.body === 'string' && comment.body.includes(marker));
}

// ---------------------------------------------------------------------------
// Orchestration: find-or-create the tracking issue, then record this run as a
// comment unless it is already recorded. This is the one place the dedup contract
// lives, so every consumer shares identical semantics.
// ---------------------------------------------------------------------------

// Options:
//   label       - the lookup label; the issue is found and (by default) filed with it.
//   labels      - full label set for a newly-filed issue (defaults to [label]).
//   marker      - per-subject body marker used to find the canonical issue.
//   title       - title for a newly-filed issue.
//   runId       - stable run identifier; the dedup key (see runMarker).
//   buildBody   - () => string, the static issue body for a fresh issue.
//   comment     - the failure comment text; the run marker is appended to it.
//   issues      - optional pre-fetched all-state issue list (a multi-subject caller,
//                 e.g. the watchdog, lists once and reuses it across subjects).
// Returns { number, created } when a comment was posted, or { number, skipped }
// when the run was already recorded.
async function recordRun(github, context, core, { label, labels, marker, title, runId, buildBody, comment, issues }) {
    const { owner, repo } = context.repo;
    const list = issues ?? await listIssuesByLabel(github, owner, repo, label);
    const issue = findOpenIssueForMarker(list, marker) ?? findIssueForMarker(list, marker);
    const runComment = runMarker(runId);
    const commentBody = `${comment}\n\n${runComment}`;

    if (issue === null) {
        const created = await createIssue(github, owner, repo, { title, body: buildBody(), labels: labels ?? [label] });
        // A fresh issue has no comments, so the first failure is always posted
        // without a dedup check; the comment is what notifies subscribers.
        await addComment(github, owner, repo, created.number, commentBody);
        return { number: created.number, created: true };
    }

    if (await hasCommentForRun(github, owner, repo, issue.number, runComment)) {
        core.info(`Run ${runId} already recorded in #${issue.number}; skipping duplicate comment.`);
        return { number: issue.number, skipped: true };
    }

    const reopened = issue.state === 'closed';
    if (reopened) {
        await reopenIssue(github, owner, repo, issue.number);
    }

    await addComment(github, owner, repo, issue.number, commentBody);
    return { number: issue.number, created: false, reopened };
}

module.exports = {
    findIssueForMarker,
    findOpenIssueForMarker,
    runMarker,
    buildBody,
    autoCloseStamp,
    readAutoClose,
    ensureLabel,
    listIssuesByLabel,
    listOpenIssuesByLabel,
    createIssue,
    addComment,
    closeIssue,
    reopenIssue,
    hasCommentForRun,
    recordRun,
};
