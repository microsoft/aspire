// Pure decision/formatting helpers for the embedded test-failure reporter used by
// tests-outerloop.yml and tests-quarantine.yml
// (.github/workflows/specialized-test-failure-runner.js).
//
// These scheduled test workflows otherwise fail silently. This reporter files a
// single deduplicated GitHub issue per (workflow, failure-kind) and records each
// failed run as a comment; a human closes the issue once fixed (no
// auto-close-on-green).
//
// Two failure kinds:
//   - 'test-failures' : outerloop tests failed. Each failed run's comment lists
//     the failing tests; a dev can split them into per-test issues later.
//   - 'infra'         : the run broke before/around test execution (build/setup,
//     missing TRX). Quarantine runs swallow test failures (ignoreTestFailures),
//     so a *failed* quarantine run is always infra.
//
// This module performs NO network I/O; the shared engine (./tracking-issue.js)
// does the octokit calls and calls these helpers. It is unit-tested by
// tests/Infrastructure.Tests/WorkflowScripts/ReportSpecializedTestFailuresTests.cs.

'use strict';

const tracking = require('./tracking-issue.js');

const AUTOMATION_BROKEN_LABEL = 'automation-broken';
const FAILING_TEST_LABEL = 'failing-test';

const KIND_INFRA = 'infra';
const KIND_TEST_FAILURES = 'test-failures';

// Per-(workflow, kind) dedup marker, e.g.
//   <!-- ci-failure:tests-outerloop.yml:test-failures -->
// Distinct from the scheduled-workflow scanner's `automation-broken:<file>`
// marker so the two mechanisms never manage each other's issues.
const MARKER_PREFIX = 'ci-failure:';

// Cap inline test names in a comment so a large outerloop break doesn't post a
// multi-hundred-line comment; the full set is always in the run's TRX artifacts.
const DEFAULT_MAX_LISTED_TESTS = 50;

function buildMarker(workflowFile, kind) {
    return `<!-- ${MARKER_PREFIX}${workflowFile}:${kind} -->`;
}

function labelForKind(kind) {
    return kind === KIND_TEST_FAILURES ? FAILING_TEST_LABEL : AUTOMATION_BROKEN_LABEL;
}

function buildIssueTitle(displayName, kind) {
    return kind === KIND_TEST_FAILURES
        ? `Test failures: ${displayName}`
        : `CI infrastructure failing: ${displayName}`;
}

// Decides what a failed scheduled run represents.
//   ignoreTestFailures (quarantine): a failed run is always infra, because test
//   failures are swallowed upstream (run-tests.yml) and never red the run.
//   Otherwise (outerloop):
//     - failed tests extracted        => 'test-failures'
//     - extraction failed/unknown     => 'test-failures' (we can't prove it was
//       infra, and misfiling a red run as infra loses the failing-test signal —
//       prefer the test-failures issue with a "could not enumerate" note)
//     - clean extraction, zero failed => 'infra' (the run broke before/around
//       test execution; no test produced a failed result)
function classifyFailure({ result, failedCount = 0, ignoreTestFailures = false, extractionFailed = false }) {
    if (result !== 'failure') {
        return 'none';
    }
    if (ignoreTestFailures) {
        return KIND_INFRA;
    }
    if (extractionFailed) {
        return KIND_TEST_FAILURES;
    }

    return failedCount > 0 ? KIND_TEST_FAILURES : KIND_INFRA;
}

// Builds the static issue body. Each failed run is recorded as a comment (see the
// runner), so the body is a fixed description written once at filing; the marker
// is embedded so the issue can be found again.
function buildIssueBody({ marker, displayName, workflowFile, kind }) {
    const lead = kind === KIND_TEST_FAILURES
        ? `Outerloop tests are failing in [\`${workflowFile}\`](../../actions/workflows/${workflowFile}) (**${displayName}**). The failing tests for each run are listed in the comments below; split them into per-test issues as needed.`
        : `The scheduled workflow [\`${workflowFile}\`](../../actions/workflows/${workflowFile}) (**${displayName}**) is failing for an infrastructure reason (build/setup, or no test results were produced).`;

    return tracking.buildBody({
        marker,
        lead,
        note: [
            'Filed automatically by the specialized-test failure reporter. Each failed run',
            'is added as a comment below; close this issue once the underlying problem is',
            'fixed.',
            'See [docs/ci/specialized-test-failure-issues.md](../../blob/main/docs/ci/specialized-test-failure-issues.md).',
        ],
    });
}

// Normalizes a failed test name for safe inclusion inside a Markdown inline code
// span. Real test display names can contain backticks (e.g. interpolated theory
// data) and, rarely, newlines; left raw they can break out of the code span and
// inject arbitrary Markdown (headings, @mentions). Collapse CR/LF to spaces and
// wrap with a backtick fence longer than any backtick run in the name, padding
// with spaces when the name starts/ends with a backtick (CommonMark rule).
function toInlineCode(name) {
    const normalized = String(name).replace(/\r?\n/g, ' ');
    const longestRun = (normalized.match(/`+/g) ?? []).reduce((max, run) => Math.max(max, run.length), 0);
    const fence = '`'.repeat(longestRun + 1);
    const pad = normalized.startsWith('`') || normalized.endsWith('`') ? ' ' : '';

    return `${fence}${pad}${normalized}${pad}${fence}`;
}

// Comment recorded per failed run. For test failures it lists the failing tests
// (capped). When the kind is test-failures but no names were extracted (extraction
// failed on a genuinely-red run), it points at the run artifacts instead of
// claiming zero.
function formatComment({ kind, run, failedTests = [], maxListed = DEFAULT_MAX_LISTED_TESTS }) {
    const runLink = `[run #${run.runNumber ?? '?'}](${run.runUrl})`;
    if (kind !== KIND_TEST_FAILURES) {
        return `Infrastructure failure again in ${runLink}. See the run for build/setup logs.`;
    }

    if (failedTests.length === 0) {
        return `Tests failed in ${runLink}, but the failing test names could not be ` +
            `extracted from the run's results. See the run's test artifacts.`;
    }

    const shown = failedTests.slice(0, maxListed);
    const lines = [`${failedTests.length} test(s) failed in ${runLink}:`, ''];
    for (const test of shown) {
        lines.push(`- ${toInlineCode(test)}`);
    }
    if (failedTests.length > shown.length) {
        lines.push(`- _…and ${failedTests.length - shown.length} more (see run artifacts)_`);
    }

    return lines.join('\n');
}

module.exports = {
    AUTOMATION_BROKEN_LABEL,
    FAILING_TEST_LABEL,
    KIND_INFRA,
    KIND_TEST_FAILURES,
    MARKER_PREFIX,
    DEFAULT_MAX_LISTED_TESTS,
    buildMarker,
    labelForKind,
    buildIssueTitle,
    classifyFailure,
    buildIssueBody,
    formatComment,
};
