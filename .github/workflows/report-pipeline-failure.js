// Nightly-pipeline failure reporter, used by deployment-tests.yml and
// tests-daily-smoke.yml. Invoked from an actions/github-script step via report().
//
// These scheduled pipelines otherwise fail silently — GitHub only emails whoever
// last edited the workflow file. This reporter files a single deduplicated issue
// per workflow and records each failed run as a comment; a human closes the issue
// once the problem is fixed (no auto-close-on-green, mirroring the specialized-test
// reporter — a green run mid-triage should not close the issue out from under a
// dev).
//
// Unlike the specialized-test reporter, a failed run here is NOT classified into
// test-vs-infra: these pipelines report "the scheduled run failed" with a link to
// the run. Any per-run detail a caller wants to surface (e.g. the smoke suite's
// per-route CLI versions) rides on the comment.
//
// The reusable find-or-create + comment-dedup loop and the octokit primitives live
// in ./tracking-issue.js. The pure helpers here are unit-tested by
// tests/Infrastructure.Tests/WorkflowScripts/ReportPipelineFailureTests.cs; report()
// is covered through report-pipeline-failure.integration.harness.js.

'use strict';

const tracking = require('./tracking-issue.js');

// All pipeline-failure issues carry this label; it is also the lookup key used to
// narrow the strongly-consistent issue list before the marker filter. Shared with
// the scanner and the specialized reporter's infra issues — the per-workflow
// marker keeps the issues from being managed by each other.
const AUTOMATION_BROKEN_LABEL = 'automation-broken';

// Per-workflow dedup marker, e.g.
//   <!-- ci-failure:deployment-tests.yml:scheduled -->
// Shares the `ci-failure:` namespace with the specialized-test reporter; the
// workflow-file segment keeps each pipeline's marker distinct, so the two
// reporters never edit or close each other's issues even though both query the
// `automation-broken` label.
const MARKER_PREFIX = 'ci-failure:';
const KIND = 'scheduled';

function buildMarker(workflowFile) {
    return `<!-- ${MARKER_PREFIX}${workflowFile}:${KIND} -->`;
}

function buildIssueTitle(displayName) {
    return `Nightly run failing: ${displayName}`;
}

// Builds the static issue body. Each failed run is recorded as a comment (see
// report()), so the body is a fixed description written once at filing.
function buildIssueBody({ marker, displayName, workflowFile, cc = '' }) {
    const lead = `The scheduled workflow [\`${workflowFile}\`](../../actions/workflows/${workflowFile}) (**${displayName}**) is failing on its nightly run.`;

    return tracking.buildBody({
        marker,
        lead: cc ? `${lead} /cc ${cc}` : lead,
        note: [
            'Filed automatically when a scheduled run fails. Each failed run is added as a',
            'comment below; close this issue once the underlying problem is fixed.',
            'See [docs/ci/pipeline-failure-issues.md](../../blob/main/docs/ci/pipeline-failure-issues.md).',
        ],
    });
}

// Comment recorded per failed run. `detail` carries optional caller-specific
// per-run context (e.g. the smoke suite's per-route CLI versions); it is rendered
// verbatim below the run line.
function formatComment({ run, detail = '' }) {
    const runLink = `[run #${run.runNumber ?? '?'}](${run.runUrl})`;
    const shaPart = run.sha ? ` (commit \`${String(run.sha).slice(0, 8)}\`)` : '';
    const head = `The scheduled run failed in ${runLink}${shaPart}.`;

    return detail ? `${head}\n\n${detail}` : head;
}

// Orchestrator. Required env: WORKFLOW_FILE, DISPLAY_NAME. Call options:
//   labels        - extra labels for a newly-filed issue (automation-broken is
//                   always added, in lockstep with the lookup key, so a created
//                   issue is always findable on the next run).
//   cc            - optional @mention added to the body lead (first filing only).
//   commentDetail - optional per-run markdown appended to the failure comment.
async function report({ github, context, core, labels, cc = '', commentDetail = '' }) {
    const workflowFile = process.env.WORKFLOW_FILE;
    const displayName = process.env.DISPLAY_NAME;
    const { owner, repo } = context.repo;

    await tracking.ensureLabel(github, owner, repo, {
        name: AUTOMATION_BROKEN_LABEL, color: 'B60205',
        description: 'A scheduled/automation workflow is failing',
    });

    const run = {
        runUrl: `https://github.com/${owner}/${repo}/actions/runs/${context.runId}`,
        runNumber: context.runNumber,
        sha: context.sha,
    };
    const marker = buildMarker(workflowFile);
    const issueLabels = [...new Set([AUTOMATION_BROKEN_LABEL, ...(labels ?? [])])];

    const result = await tracking.recordRun(github, context, core, {
        label: AUTOMATION_BROKEN_LABEL,
        labels: issueLabels,
        marker,
        title: buildIssueTitle(displayName),
        runId: context.runId,
        buildBody: () => buildIssueBody({ marker, displayName, workflowFile, cc }),
        comment: formatComment({ run, detail: commentDetail }),
    });

    if (!result.skipped) {
        core.info(`${result.created ? 'Filed' : 'Updated'} #${result.number} for ${workflowFile}`);
    }
}

module.exports = {
    AUTOMATION_BROKEN_LABEL,
    MARKER_PREFIX,
    KIND,
    buildMarker,
    buildIssueTitle,
    buildIssueBody,
    formatComment,
    report,
};
