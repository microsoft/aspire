// Shared diagnostics, GitHub rerun request, and reporting helpers.
const failureConclusions = new Set(['failure', 'cancelled', 'timed_out', 'startup_failure']);
const ignoredJobs = new Set(['Final Results', 'Tests / Final Test Results']);
const defaultMaxRetryableJobs = 5;
const defaultMaxRunAttempt = 3;
const defaultDelay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

const postTestCleanupFailureStepPatterns = [
    /^Upload logs, and test results$/i,
    /^Copy CLI E2E recordings for upload$/i,
    /^Upload CLI E2E recordings$/i,
    /^Generate test results summary$/i,
    /^Post Checkout code$/i,
];

const windowsProcessInitializationFailurePatterns = [
    /Process completed with exit code -1073741502/i,
    /\b0xC0000142\b/i,
];

function matchesAny(value, patterns) {
    return patterns.some(pattern => pattern.test(value));
}

function findMatchingPattern(value, patterns) {
    return patterns.find(pattern => pattern.test(value)) ?? null;
}

function createBoundedDiagnosticPairPatterns(firstPattern, secondPatterns, maxDistance) {
    return secondPatterns.flatMap(secondPattern => [
        new RegExp(`${firstPattern.source}[\\s\\S]{0,${maxDistance}}${secondPattern.source}`, 'i'),
        new RegExp(`${secondPattern.source}[\\s\\S]{0,${maxDistance}}${firstPattern.source}`, 'i'),
    ]);
}

function parseCheckRunId(checkRunUrl) {
    if (typeof checkRunUrl !== 'string') {
        return null;
    }

    const match = checkRunUrl.match(/\/check-runs\/(\d+)(?:\/|$)/);
    if (!match) {
        return null;
    }

    const checkRunId = Number(match[1]);
    return Number.isInteger(checkRunId) && checkRunId > 0 ? checkRunId : null;
}

async function getCheckRunIdForJob({ job, getJobForWorkflowRun }) {
    const checkRunIdFromJob = parseCheckRunId(job?.check_run_url);
    if (checkRunIdFromJob) {
        return checkRunIdFromJob;
    }

    if (!getJobForWorkflowRun || !Number.isInteger(job?.id) || job.id <= 0) {
        return null;
    }

    const workflowJob = await getJobForWorkflowRun(job.id);
    return parseCheckRunId(workflowJob?.check_run_url);
}

function getFailedSteps(job) {
    return (job.steps || [])
        .filter(step => failureConclusions.has(step.conclusion))
        .map(step => step.name);
}

function annotationText(annotations) {
    return (annotations || [])
        .flatMap(annotation => [annotation.title, annotation.message, annotation.raw_details].filter(Boolean))
        .join('\n');
}

function toAnnotationText(annotationsOrText) {
    if (!annotationsOrText) {
        return '';
    }

    if (typeof annotationsOrText === 'string') {
        return annotationsOrText;
    }

    return annotationText(annotationsOrText);
}

function computeRerunEligibility({
    retryableCount,
    maxRetryableJobs = defaultMaxRetryableJobs,
    runAttempt = 1,
    maxRunAttempt = defaultMaxRunAttempt,
    forceRerunAll = false
}) {
    // The attempt cap applies in every mode: never rerun past maxRunAttempt
    // failed source attempts.
    if (runAttempt > maxRunAttempt) {
        return false;
    }

    // FORCE_RERUN_ALL short-circuit: the run failed (YAML trigger gate) and has an open
    // PR (checked in the workflow), so it is eligible — only the attempt cap above
    // matters. There is no job-count analysis. See the file-level comment to disable.
    if (forceRerunAll) {
        return true;
    }

    if (retryableCount <= 0) {
        return false;
    }

    // For attempts after the first (runAttempt > 1) apply a stricter cap:
    // fewer than maxRetryableJobs jobs (i.e. strictly less than the cap rather
    // than less-than-or-equal).
    return runAttempt <= 1
        ? retryableCount <= maxRetryableJobs
        : retryableCount < maxRetryableJobs;
}

function buildSummaryReference(url, text) {
    return { url, text };
}

function addSummaryReference(summary, label, reference) {
    summary.addRaw(`${label}: `);

    if (reference?.url) {
        summary.addLink(reference.text, reference.url);
    }
    else {
        summary.addRaw(reference?.text || 'not available');
    }

    return summary.addBreak();
}

function addSummaryCommentReferences(summary, postedComments) {
    if (!postedComments?.length) {
        summary.addRaw('Pull request comments: none posted').addBreak();
        return summary;
    }

    summary.addRaw('Pull request comments:').addBreak();

    for (const comment of postedComments) {
        summary.addRaw('- ');

        if (comment.htmlUrl) {
            summary.addLink(`PR #${comment.pullRequestNumber} comment`, comment.htmlUrl);
        }
        else {
            summary.addRaw(`PR #${comment.pullRequestNumber} comment`);
        }

        summary.addBreak();
    }

    return summary;
}

async function writeAnalysisSummary({
    summary,
    failedJobs,
    retryableJobs,
    skippedJobs,
    maxRetryableJobs = defaultMaxRetryableJobs,
    dryRun,
    rerunEligible,
    sourceRunUrl,
    sourceRunAttempt,
    testPatternMatchedTests = [],
}) {
    const analyzedRunReference = buildSummaryReference(
        buildWorkflowRunAttemptUrl(sourceRunUrl, sourceRunAttempt),
        Number.isInteger(sourceRunAttempt) && sourceRunAttempt > 0
            ? `workflow run attempt ${sourceRunAttempt}`
            : 'workflow run'
    );
    const outcome = rerunEligible ? 'Rerun eligible' : 'Rerun skipped';
    const outcomeDetails = rerunEligible
        ? dryRun
            ? `Matched ${retryableJobs.length} retry-safe job${retryableJobs.length === 1 ? '' : 's'} that would be rerun if dry run were disabled.`
            : `Matched ${retryableJobs.length} retry-safe job${retryableJobs.length === 1 ? '' : 's'} for rerun.`
        : retryableJobs.length === 0
            ? 'No retry-safe jobs were found in the analyzed run.'
            : retryableJobs.length > maxRetryableJobs
                ? `Matched ${retryableJobs.length} jobs, which exceeds the cap of ${maxRetryableJobs}.`
                : 'The analyzed run did not satisfy the workflow safety rails for reruns.';
    const summaryRows = [
        [{ data: 'Category', header: true }, { data: 'Count', header: true }],
        ['Outcome', outcome],
        ['Failed jobs inspected', String(failedJobs.length)],
        ['Retryable jobs', String(retryableJobs.length)],
        ['Skipped jobs', String(skippedJobs.length)],
        ['Max retryable jobs', String(maxRetryableJobs)],
        ['Dry run', String(dryRun)],
        ['Eligible to rerun', String(rerunEligible)],
    ];

    await summary
        .addHeading(outcome)
        .addTable(summaryRows);

    addSummaryReference(summary, 'Analyzed run', analyzedRunReference)
        .addRaw(outcomeDetails)
        .addBreak()
        .addBreak();

    if (retryableJobs.length > 0) {
        await summary.addHeading('Retryable jobs', 2);
        await summary.addTable([
            [{ data: 'Job', header: true }, { data: 'Reason', header: true }],
            ...retryableJobs.map(job => [job.name, job.reason]),
        ]);
    }

    if (testPatternMatchedTests.length > 0) {
        await summary.addHeading('Matched test failure patterns', 2);
        const displayedTests = testPatternMatchedTests.slice(0, 25);
        await summary.addTable([
            [{ data: 'Test', header: true }, { data: 'Reason', header: true }],
            ...displayedTests.map(test => [test.testName, test.reason]),
        ]);

        if (testPatternMatchedTests.length > 25) {
            summary.addRaw(`...and ${testPatternMatchedTests.length - 25} more matched test(s).`).addBreak();
        }
    }

    if (skippedJobs.length > 0) {
        await summary.addHeading('Skipped jobs', 2);
        await summary.addTable([
            [{ data: 'Job', header: true }, { data: 'Reason', header: true }],
            ...skippedJobs.slice(0, 25).map(job => [job.name, job.reason]),
        ]);
    }

    await summary.write();
}

function buildWorkflowRunAttemptUrl(sourceRunUrl, runAttempt) {
    if (!sourceRunUrl || !Number.isInteger(runAttempt) || runAttempt <= 0) {
        return sourceRunUrl;
    }

    return `${sourceRunUrl.replace(/\/$/, '')}/attempts/${runAttempt}`;
}

function buildWorkflowRunReference(sourceRunUrl, runAttempt) {
    return buildSummaryReference(
        buildWorkflowRunAttemptUrl(sourceRunUrl, runAttempt),
        Number.isInteger(runAttempt) && runAttempt > 0
            ? `workflow run attempt ${runAttempt}`
            : 'workflow run'
    );
}

async function writeRerunOutcomeSummary({
    summary,
    sourceRunUrl,
    sourceRunAttempt,
    heading = 'Rerun skipped',
    message,
}) {
    const failedAttemptReference = buildWorkflowRunReference(sourceRunUrl, sourceRunAttempt);
    await summary.addHeading(heading);
    addSummaryReference(summary, 'Analyzed run', failedAttemptReference)
        .addRaw(message)
        .addBreak()
        .addBreak();
    await summary.write();
}

async function writeRerunRequestedSummary({
    summary,
    sourceRunUrl,
    sourceRunAttempt,
    rerunAttemptNumber,
    retryableJobs = [],
    postedComments = [],
    forceRerunAll = false,
}) {
    const normalizedSourceRunAttempt = Number.isInteger(sourceRunAttempt) && sourceRunAttempt > 0
        ? sourceRunAttempt
        : null;
    const failedAttemptReference = buildWorkflowRunReference(sourceRunUrl, normalizedSourceRunAttempt);
    const rerunAttemptReference = buildWorkflowRunReference(sourceRunUrl, rerunAttemptNumber);
    const summaryBuilder = summary
        .addHeading('Rerun requested');

    addSummaryReference(summaryBuilder, 'Failed attempt', failedAttemptReference);
    addSummaryReference(summaryBuilder, 'Rerun attempt', rerunAttemptReference);
    addSummaryCommentReferences(summaryBuilder, postedComments);

    // Force mode does not enumerate jobs, so there is no retry-safe job table to show.
    if (forceRerunAll) {
        summaryBuilder
            .addBreak()
            .addRaw('Force-rerun mode: the CI run failed, so GitHub was asked to rerun all failed jobs for the failed attempt. Transient-failure analysis was bypassed.')
            .addBreak();

        await summaryBuilder.write();
        return;
    }

    summaryBuilder
        .addBreak()
        .addRaw('The matched jobs below made the run eligible for rerun. GitHub was asked to rerun all failed jobs for the failed attempt.')
        .addBreak()
        .addBreak()
        .addHeading('Retryable jobs', 2);

    summaryBuilder
        .addTable([
            [{ data: 'Job', header: true }, { data: 'Reason', header: true }],
            ...retryableJobs.map(job => [job.name, job.reason]),
        ]);

    await summaryBuilder.write();
}

async function requestFailedJobsRerun({ github, owner, repo, sourceRunId }) {
    return github.request('POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs', {
        owner,
        repo,
        run_id: sourceRunId,
    });
}

function formatMatchedPatternForMarkdown(matchedPattern) {
    if (!matchedPattern) {
        return '';
    }

    const patternText = String(matchedPattern);
    let maxBacktickRun = 0;
    const backtickRunRegex = /`+/g;
    let match;

    while ((match = backtickRunRegex.exec(patternText)) !== null) {
        if (match[0].length > maxBacktickRun) {
            maxBacktickRun = match[0].length;
        }
    }

    const fence = '`'.repeat(maxBacktickRun + 1);
    return ` Matched pattern: ${fence}${patternText}${fence}.`;
}


module.exports = {
    failureConclusions,
    ignoredJobs,
    defaultMaxRetryableJobs,
    defaultMaxRunAttempt,
    defaultDelay,
    postTestCleanupFailureStepPatterns,
    windowsProcessInitializationFailurePatterns,
    matchesAny,
    findMatchingPattern,
    createBoundedDiagnosticPairPatterns,
    parseCheckRunId,
    getCheckRunIdForJob,
    getFailedSteps,
    annotationText,
    toAnnotationText,
    computeRerunEligibility,
    buildSummaryReference,
    addSummaryReference,
    addSummaryCommentReferences,
    writeAnalysisSummary,
    buildWorkflowRunAttemptUrl,
    buildWorkflowRunReference,
    writeRerunOutcomeSummary,
    writeRerunRequestedSummary,
    requestFailedJobsRerun,
    formatMatchedPatternForMarkdown,
};
