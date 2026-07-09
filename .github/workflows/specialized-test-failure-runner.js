// Network orchestration for the specialized-test failure reporter, shared by
// tests-outerloop.yml and tests-quarantine.yml. Invoked from an
// actions/github-script step; all pure logic lives in (and is unit-tested via)
// report-specialized-test-failures.js, and the find-or-create + comment-dedup
// loop lives in the shared engine ./tracking-issue.js. This file reads the run's
// results, classifies the failure, and delegates, so it is thin and covered
// through specialized-test-failure-runner.harness.js.
//
// Required env:
//   WORKFLOW_FILE        - e.g. 'tests-outerloop.yml' (dedup key + links)
//   DISPLAY_NAME         - human label used in the issue title
//   IGNORE_TEST_FAILURES - 'true' for quarantine (a failed run is always infra)
//   FAILED_TESTS_PATH    - optional path to GenerateTestSummary --failed-tests-json output

'use strict';

const fs = require('node:fs');

const tracking = require('./tracking-issue.js');

module.exports = async function reportSpecializedTestFailure({ github, context, core, reporter }) {
    const { owner, repo } = context.repo;
    const workflowFile = process.env.WORKFLOW_FILE;
    const displayName = process.env.DISPLAY_NAME;
    const ignoreTestFailures = process.env.IGNORE_TEST_FAILURES === 'true';

    // The reporter job is gated on failure(), so the upstream run concluded 'failure'.
    let failedTests = [];
    let extractionFailed = false;
    const failedTestsPath = process.env.FAILED_TESTS_PATH;
    if (failedTestsPath && fs.existsSync(failedTestsPath)) {
        try {
            const parsed = JSON.parse(fs.readFileSync(failedTestsPath, 'utf8'));
            failedTests = Array.isArray(parsed.failedTests) ? parsed.failedTests : [];
            extractionFailed = parsed.extractionFailed === true;
        } catch (error) {
            // We were handed a results path but couldn't read it — treat as an
            // extraction failure rather than "zero failures" so a genuinely-red
            // outerloop run is not misfiled as infra.
            core.warning(`Could not parse failed-tests JSON: ${error.message}`);
            extractionFailed = true;
        }
    } else if (!ignoreTestFailures) {
        // The outerloop reporter expects a results file, but the extract step can
        // crash before emitting its path output (leaving FAILED_TESTS_PATH empty).
        // The run is red; treat unreadable results as a test failure, not infra, so
        // the failing-test signal is not silently dropped. (Quarantine sets
        // ignoreTestFailures and never inspects results, so this branch is skipped
        // there.)
        core.warning('No failed-tests results path was provided; treating as an extraction failure.');
        extractionFailed = true;
    }

    const kind = reporter.classifyFailure({
        result: 'failure',
        failedCount: failedTests.length,
        ignoreTestFailures,
        extractionFailed,
    });
    if (kind === 'none') {
        core.info('Run did not fail; nothing to report.');
        return;
    }

    const marker = reporter.buildMarker(workflowFile, kind);
    const label = reporter.labelForKind(kind);

    await tracking.ensureLabel(github, owner, repo, {
        name: label, color: 'B60205',
        description: kind === reporter.KIND_TEST_FAILURES ? 'A failing test' : 'A scheduled/automation workflow is failing',
    });

    const run = {
        runUrl: `https://github.com/${owner}/${repo}/actions/runs/${context.runId}`,
        runNumber: context.runNumber,
    };

    const result = await tracking.recordRun(github, context, core, {
        label,
        marker,
        title: reporter.buildIssueTitle(displayName, kind),
        runId: context.runId,
        buildBody: () => reporter.buildIssueBody({ marker, displayName, workflowFile, kind }),
        comment: reporter.formatComment({ kind, run, failedTests }),
    });

    if (result.skipped) {
        return;
    }
    core.info(`${result.created ? 'Filed' : 'Updated'} #${result.number} (${kind}) for ${workflowFile}`);
};
