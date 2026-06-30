// Red-main CI reporter, used by ci.yml. Files/updates a single deduplicated issue
// when a push to a protected branch (main, release/**) fails CI, and closes that
// issue again when a later push to the same branch is green.
//
// Self-closing by design: ci.yml runs on every push, so a green push is the most
// timely signal that the branch is no longer red — there is no need to wait for an
// external 2h poll. The issue still carries an autoClose:true stamp (see
// tracking-issue.js) so the scheduled watchdog can also close it as a backstop.
//
// Per-branch keying: a red `main` and a red `release/13.3` are different problems,
// so the marker embeds the ref and each branch gets its own issue. Example markers:
//   <!-- ci-failure:ci.yml:push:main -->
//   <!-- ci-failure:ci.yml:push:release/13.3 -->
//
// The reusable find-or-create + comment-dedup loop and the octokit primitives live
// in ./tracking-issue.js. The pure helpers here are unit-tested by
// tests/Infrastructure.Tests/WorkflowScripts/ReportCiFailureTests.cs; reportFailure()
// and resolveSuccess() are covered through report-ci-failure.integration.harness.js.

'use strict';

const tracking = require('./tracking-issue.js');

// All red-main issues carry this label; it is also the lookup key used to narrow
// the strongly-consistent issue list before the marker filter. Shared with the
// scanner and the nightly-pipeline reporter — the per-branch marker keeps the
// issues from being managed by each other.
const AUTOMATION_BROKEN_LABEL = 'automation-broken';

// Per-branch dedup marker; the ref segment makes main and release/** distinct.
const MARKER_PREFIX = 'ci-failure:ci.yml:push:';

function buildMarker(ref) {
    return `<!-- ${MARKER_PREFIX}${ref} -->`;
}

function buildIssueTitle(ref) {
    return `CI failing on \`${ref}\``;
}

// Builds the static issue body, stamped autoClose:true (a green push closes it).
// Each failed run is recorded as a comment (see reportFailure), so the body is a
// fixed description written once at filing.
function buildIssueBody({ marker, ref }) {
    return tracking.buildBody({
        marker,
        autoClose: true,
        lead: `CI (\`ci.yml\`) is failing on push to \`${ref}\`. The branch is red.`,
        note: [
            'Filed automatically when a push to this branch fails CI. Each failed run is',
            'recorded as a comment below. The issue is **closed automatically** when a',
            'later push to the same branch passes CI.',
            'See [docs/ci/ci-failure-issues.md](../../blob/main/docs/ci/ci-failure-issues.md).',
        ],
    });
}

// Comment recorded per failed run. Intentionally does NOT parse test results:
// a push can fail for non-test reasons (setup, build, a non-TRX job), so the
// comment links the run rather than asserting a failing-test list.
function formatComment({ run }) {
    const runLink = `[run #${run.runNumber ?? '?'}](${run.runUrl})`;
    const shaPart = run.sha ? ` (commit \`${String(run.sha).slice(0, 8)}\`)` : '';

    return `CI failed in ${runLink}${shaPart}.`;
}

// The branch under test, from github.ref_name (e.g. `main`, `release/13.3`).
function readRef() {
    const ref = process.env.REF;
    if (!ref) {
        throw new Error('REF env var is required (set it to github.ref_name).');
    }

    return ref;
}

function runContext(context) {
    const { owner, repo } = context.repo;

    return {
        runUrl: `https://github.com/${owner}/${repo}/actions/runs/${context.runId}`,
        runNumber: context.runNumber,
        sha: context.sha,
    };
}

// Files or updates the branch's red-main issue. Required env: REF.
async function reportFailure({ github, context, core }) {
    const ref = readRef();
    const { owner, repo } = context.repo;

    await tracking.ensureLabel(github, owner, repo, {
        name: AUTOMATION_BROKEN_LABEL, color: 'B60205',
        description: 'A scheduled/automation workflow is failing',
    });

    const marker = buildMarker(ref);
    const result = await tracking.recordRun(github, context, core, {
        label: AUTOMATION_BROKEN_LABEL,
        labels: [AUTOMATION_BROKEN_LABEL],
        marker,
        title: buildIssueTitle(ref),
        runId: context.runId,
        buildBody: () => buildIssueBody({ marker, ref }),
        comment: formatComment({ run: runContext(context) }),
    });

    if (!result.skipped) {
        core.info(`${result.created ? 'Filed' : 'Updated'} #${result.number} for ci.yml on ${ref}`);
    }
}

// Closes the branch's red-main issue when a later push is green. A no-op when no
// issue is open for the branch. Required env: REF.
async function resolveSuccess({ github, context, core }) {
    const ref = readRef();
    const { owner, repo } = context.repo;

    const marker = buildMarker(ref);
    const open = await tracking.listOpenIssuesByLabel(github, owner, repo, AUTOMATION_BROKEN_LABEL);
    const issue = tracking.findOpenIssueForMarker(open, marker);

    if (issue === null) {
        core.info(`No open CI-failure issue for ${ref}; nothing to close.`);
        return;
    }

    const run = runContext(context);
    await tracking.addComment(github, owner, repo, issue.number,
        `CI is green again on \`${ref}\` ([run #${run.runNumber}](${run.runUrl})). Closing automatically.`);
    await tracking.closeIssue(github, owner, repo, issue.number);
    core.info(`Closed #${issue.number} for ci.yml on ${ref}`);
}

// Single entry point for ci.yml's tracker job, which runs on every push
// (if: always()). The workflow computes the aggregate CI result from
// needs.*.result and passes it in env:
//   CI_RED   = 'true' when any dependency failed (the branch is red).
//   CI_GREEN = 'true' when every dependency succeeded (the branch is green).
// A run that is neither (a skipped no-relevant-changes push, or a cancelled run)
// is a no-op: there is no CI signal, so an open issue is left untouched. CI_RED is
// checked first so a genuine failure is always reported even if env is malformed.
async function track({ github, context, core }) {
    if (process.env.CI_RED === 'true') {
        await reportFailure({ github, context, core });
        return;
    }

    if (process.env.CI_GREEN === 'true') {
        await resolveSuccess({ github, context, core });
        return;
    }

    core.info('CI run is neither cleanly red nor green (skipped or cancelled); nothing to do.');
}

module.exports = {
    AUTOMATION_BROKEN_LABEL,
    MARKER_PREFIX,
    buildMarker,
    buildIssueTitle,
    buildIssueBody,
    formatComment,
    reportFailure,
    resolveSuccess,
    track,
};
