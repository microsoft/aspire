// Narrow current-main classification and final write-time freshness gates.
const common = require('./common.js');
const { createJobReader } = require('./github.js');
const {
    failureConclusions, ignoredJobs, defaultMaxRetryableJobs, defaultDelay,
    matchesAny, getFailedSteps, toAnnotationText, computeRerunEligibility,
    postTestCleanupFailureStepPatterns, windowsProcessInitializationFailurePatterns,
    writeRerunOutcomeSummary,
} = common;

const mainMaxRunAttempt = 1;
const mainRunListMaxAttempts = 4;
const mainRunListRetryDelayMs = 5000;
const mainExcludedJobPatterns = [
    /(?:^|\/ )Hosting-(?:1|5)(?: \(|$)/i,
];

const mainHostedRunnerLossPattern =
    /The hosted runner lost communication with the server\./i;
const mainDiagnosticPairMaxDistance = 500;
const mainAcrRegistryPattern = /\bnetaspireci\.azurecr\.io\b/i;
const mainAcrTransportFailurePatterns = [
    /\bconnect: connection refused\b/i,
    /\bConnection reset by peer\b/i,
];
const mainAcrFailurePatterns = common.createBoundedDiagnosticPairPatterns(
    mainAcrRegistryPattern,
    mainAcrTransportFailurePatterns,
    mainDiagnosticPairMaxDistance);
const mainMcrRegistryPattern = /\bmcr\.microsoft\.com\b/i;
const mainMcrServiceUnavailablePatterns = [
    /\bHTTP(?:\/[0-9.]+)?(?: status)?[: ]+503\b/i,
    /\bServiceUnavailable\b/i,
];
const mainMcrFailurePatterns = common.createBoundedDiagnosticPairPatterns(
    mainMcrRegistryPattern,
    mainMcrServiceUnavailablePatterns,
    mainDiagnosticPairMaxDistance);
const mainPostTestReportingFailureStepPatterns = [
    /^Check for hang dump files$/i,
    ...postTestCleanupFailureStepPatterns,
];
const mainTestExecutionStepPatterns = [
    /^Run tests\b/i,
    /^Run nuget dependent tests\b/i,
];

function classifyMainFailedJob(job, annotationsOrText, jobLogText = '') {
    const failedSteps = getFailedSteps(job);
    const failedStepText = failedSteps.join(' | ');

    if (matchesAny(job?.name || '', mainExcludedJobPatterns)) {
        return {
            retryable: false,
            failedSteps,
            reason: 'Hosting-1 and Hosting-5 are excluded from automatic main reruns because their runner-loss failures can mask DCP or process-lifecycle failures.',
        };
    }

    const annotationsText = toAnnotationText(annotationsOrText);
    const diagnosticTexts = [annotationsText, jobLogText].filter(Boolean);
    const diagnosticsText = diagnosticTexts.join('\n');

    if (mainHostedRunnerLossPattern.test(annotationsText)) {
        return {
            retryable: true,
            failedSteps,
            reason: 'The job annotation matched GitHub\'s hosted-runner communication-loss signal.',
        };
    }

    if (diagnosticTexts.some(text => matchesAny(text, mainAcrFailurePatterns))) {
        return {
            retryable: true,
            failedSteps,
            reason: 'The job diagnostics matched an Azure Container Registry transport failure.',
        };
    }

    if (diagnosticTexts.some(text => matchesAny(text, mainMcrFailurePatterns))) {
        return {
            retryable: true,
            failedSteps,
            reason: 'The job diagnostics matched a Microsoft Container Registry service-unavailable response.',
        };
    }

    const hasOnlyPostTestReportingFailures = failedSteps.length > 0 &&
        failedSteps.every(step => matchesAny(step, mainPostTestReportingFailureStepPatterns));
    const hasSuccessfulTestExecutionStep = (job?.steps || []).some(step =>
        step.conclusion === 'success' &&
        matchesAny(step.name || '', mainTestExecutionStepPatterns));
    const matchesWindowsProcessInitializationFailure =
        windowsProcessInitializationFailurePatterns.some(pattern => pattern.test(diagnosticsText));

    if (hasSuccessfulTestExecutionStep &&
        hasOnlyPostTestReportingFailures &&
        matchesWindowsProcessInitializationFailure) {
        return {
            retryable: true,
            failedSteps,
            reason: `Post-test/reporting steps '${failedStepText}' matched the Windows process initialization failure signal.`,
        };
    }

    return {
        retryable: false,
        failedSteps,
        reason: 'The job did not match the narrow current-main transient infrastructure allowlist.',
    };
}

async function analyzeMainFailedJobs({
    jobs,
    getAnnotationsForJob,
    getJobLogTextForJob,
    maxRetryableJobs = defaultMaxRetryableJobs,
    runAttempt = 1,
}) {
    const normalizedMaxRetryableJobs =
        Number.isInteger(maxRetryableJobs) && maxRetryableJobs >= 0
            ? maxRetryableJobs
            : defaultMaxRetryableJobs;
    const failedJobs = (jobs || []).filter(job => failureConclusions.has(job.conclusion) && !ignoredJobs.has(job.name));
    const retryableJobs = [];
    const skippedJobs = [];

    for (const job of failedJobs) {
        let classification = classifyMainFailedJob(job, '');

        if (!matchesAny(job?.name || '', mainExcludedJobPatterns)) {
            const annotations = getAnnotationsForJob
                ? await getAnnotationsForJob(job)
                : '';
            classification = classifyMainFailedJob(job, annotations);

            if (!classification.retryable && getJobLogTextForJob) {
                const jobLogText = await getJobLogTextForJob(job);
                classification = classifyMainFailedJob(job, annotations, jobLogText);
            }
        }

        const jobResult = {
            id: job.id,
            name: job.name,
            htmlUrl: job.html_url || null,
            failedSteps: classification.failedSteps,
            reason: classification.reason,
        };

        if (classification.retryable) {
            retryableJobs.push(jobResult);
        }
        else {
            skippedJobs.push(jobResult);
        }
    }

    const rerunEligible =
        failedJobs.length > 0 &&
        skippedJobs.length === 0 &&
        retryableJobs.length === failedJobs.length &&
        computeRerunEligibility({
            retryableCount: retryableJobs.length,
            maxRetryableJobs: normalizedMaxRetryableJobs,
            runAttempt,
            maxRunAttempt: mainMaxRunAttempt,
        });

    return { failedJobs, retryableJobs, skippedJobs, rerunEligible };
}


async function analyzeMainFailures({ github, core, owner, repo, workflowRun: run, token }) {
    const result = {
        run,
        policy: 'main',
        forceRerunAll: false,
        dryRun: false,
        executionEligible: false,
        retryableJobs: [],
    };
    if (run.run_attempt > mainMaxRunAttempt) {
        core.info(
            `Source attempt ${run.run_attempt} exceeds the current-main policy limit of ${mainMaxRunAttempt}. ` +
            'No jobs were inspected or rerun.');
        await core.summary
            .addHeading('Rerun skipped')
            .addRaw(`Source attempt ${run.run_attempt} exceeds the current-main policy limit of ${mainMaxRunAttempt}. No jobs were inspected or rerun.`)
            .addBreak()
            .write();
        return result;
    }
    const { listJobsForAttempt, listAnnotations, getJobLogText } =
        createJobReader({ github, owner, repo, core, token });
    const jobs = await listJobsForAttempt(run.id, run.run_attempt);
    const analysis = await analyzeMainFailedJobs({
        jobs,
        getAnnotationsForJob: listAnnotations,
        getJobLogTextForJob: job => getJobLogText(job.id),
        maxRetryableJobs: defaultMaxRetryableJobs,
        runAttempt: run.run_attempt,
    });
    if (!analysis.rerunEligible) {
        const reasons = analysis.skippedJobs.length > 0
            ? analysis.skippedJobs.map(job => `${job.name}: ${job.reason}`).join(' ')
            : 'No retry-safe failed jobs were found.';
        core.info(`Current-main rerun is not eligible. ${reasons}`);
    }
    await common.writeAnalysisSummary({
        summary: core.summary,
        ...analysis,
        maxRetryableJobs: defaultMaxRetryableJobs,
        dryRun: false,
        sourceRunUrl: run.html_url,
        sourceRunAttempt: run.run_attempt,
    });
    return { ...result, executionEligible: analysis.rerunEligible, retryableJobs: analysis.retryableJobs };
}

async function rerunMainFailures(options) {
    const {
        github, owner, repo, retryableJobs, summary,
        sourceRunId, sourceRunUrl, sourceRunAttempt, sourceHeadSha,
        maxRunAttempt, delay = defaultDelay,
    } = options;
    const state = {
        policy: 'current-main-failed-jobs-v1',
        source_run_id: sourceRunId,
        source_run_attempt: sourceRunAttempt,
        observed_run_attempt: null,
        max_run_attempt: maxRunAttempt,
        source_head_sha: sourceHeadSha,
        current_main_sha: null,
        superseding_run_id: null,
    };
    const skipMainRerun = async (reason, message) => {
        await writeRerunOutcomeSummary({
            summary,
            sourceRunUrl,
            sourceRunAttempt,
            message,
        });
        return {
            ...state,
            decision: 'skip',
            outcome: 'not-requested',
            reason,
        };
    };
    if (retryableJobs.length === 0) {
        return skipMainRerun(
            'no-retryable-jobs',
            'No retry-safe failed jobs were found. No jobs were rerun.');
    }

    const isTrustedMainRun = (run, expectedRunNumber, expectedWorkflowId) =>
        run &&
        run.id === sourceRunId &&
        run.run_attempt === sourceRunAttempt &&
        Number.isInteger(run.run_number) &&
        run.run_number === expectedRunNumber &&
        Number.isInteger(run.workflow_id) &&
        run.workflow_id === expectedWorkflowId &&
        run.event === 'push' &&
        run.head_branch === 'main' &&
        run.head_sha === sourceHeadSha &&
        run.path === '.github/workflows/ci.yml' &&
        run.status === 'completed' &&
        run.conclusion === 'failure';

    const { data: currentRun } = await github.request('GET /repos/{owner}/{repo}/actions/runs/{run_id}', {
        owner,
        repo,
        run_id: sourceRunId,
    });
    state.observed_run_attempt = currentRun?.run_attempt ?? null;

    const currentRunIsValid = isTrustedMainRun(
        currentRun,
        currentRun?.run_number,
        currentRun?.workflow_id);
    if (!currentRunIsValid) {
        return await skipMainRerun(
            'invalid-live-run',
            'The live workflow run no longer matches the trusted main CI run. No jobs were rerun.');
    }

    if (!Number.isInteger(maxRunAttempt) || currentRun.run_attempt > maxRunAttempt) {
        return await skipMainRerun(
            'attempt-cap-reached',
            'The workflow run reached the automatic rerun attempt cap. No jobs were rerun.');
    }

    const { data: mainRef } = await github.request('GET /repos/{owner}/{repo}/git/ref/{ref}', {
        owner,
        repo,
        ref: 'heads/main',
    });
    const currentMainSha = mainRef?.object?.sha;
    state.current_main_sha = currentMainSha ?? null;

    if (typeof sourceHeadSha !== 'string' ||
        sourceHeadSha.length === 0 ||
        typeof currentMainSha !== 'string' ||
        currentMainSha !== sourceHeadSha) {
        return await skipMainRerun(
            'main-sha-changed',
            'The failed run SHA is no longer the current main SHA. No jobs were rerun.');
    }

    // A workflow_run event can arrive before the workflow-runs list includes
    // the completed source run. Retry only that propagation gap; malformed
    // responses and superseding runs still fail closed immediately.
    let sourceRunIsPresent = false;
    for (let attempt = 1; attempt <= mainRunListMaxAttempts; attempt++) {
        const { data: mainRuns } = await github.request(
            'GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs',
            {
                owner,
                repo,
                workflow_id: currentRun.workflow_id,
                branch: 'main',
                event: 'push',
                per_page: 100,
            });
        const workflowRuns = mainRuns?.workflow_runs;
        if (!Array.isArray(workflowRuns) ||
            !workflowRuns.every(run => run &&
                Number.isInteger(run.id) &&
                Number.isInteger(run.run_number))) {
            return await skipMainRerun(
                'invalid-main-run-list',
                'The live main CI run list was incomplete or invalid. No jobs were rerun.');
        }

        const supersedingRun = workflowRuns
            .find(run => run.id !== sourceRunId && run.run_number > currentRun.run_number);

        if (supersedingRun) {
            state.superseding_run_id = supersedingRun.id;
            return await skipMainRerun(
                'superseded',
                `Newer main CI run ${supersedingRun.id} superseded this run. No jobs were rerun.`);
        }

        sourceRunIsPresent = workflowRuns
            .some(run => run.id === sourceRunId && run.run_number === currentRun.run_number);
        if (sourceRunIsPresent || attempt === mainRunListMaxAttempts) {
            break;
        }

        await delay(mainRunListRetryDelayMs);
    }

    if (!sourceRunIsPresent) {
        return await skipMainRerun(
            'invalid-main-run-list',
            'The live main CI run list did not contain the trusted source run. No jobs were rerun.');
    }

    // A push updates refs/heads/main before its workflow run necessarily appears in
    // the run list, so recheck the ref after that slower API call and immediately
    // before requesting the rerun.
    const { data: finalMainRef } = await github.request('GET /repos/{owner}/{repo}/git/ref/{ref}', {
        owner,
        repo,
        ref: 'heads/main',
    });
    const finalMainSha = finalMainRef?.object?.sha;
    state.current_main_sha = finalMainSha ?? null;

    if (typeof finalMainSha !== 'string' || finalMainSha !== sourceHeadSha) {
        return await skipMainRerun(
            'main-sha-changed',
            'The failed run SHA is no longer the current main SHA. No jobs were rerun.');
    }

    // GitHub's rerun endpoint has no expected-attempt precondition, so the
    // validation and write cannot be atomic. Re-fetch the exact run immediately
    // before the POST to minimize the window in which another rerun can advance it.
    const { data: finalRun } = await github.request('GET /repos/{owner}/{repo}/actions/runs/{run_id}', {
        owner,
        repo,
        run_id: sourceRunId,
    });
    state.observed_run_attempt = finalRun?.run_attempt ?? null;

    if (finalRun?.run_attempt !== sourceRunAttempt) {
        return await skipMainRerun(
            'attempt-changed',
            'The workflow run attempt changed before the rerun request. No jobs were rerun.');
    }

    if (!isTrustedMainRun(finalRun, currentRun.run_number, currentRun.workflow_id)) {
        return await skipMainRerun(
            'invalid-live-run',
            'The live workflow run no longer matches the trusted main CI run. No jobs were rerun.');
    }

    const mainRerunState = {
        ...state,
        decision: 'rerun',
        outcome: 'requested',
        reason: 'eligible',
    };

    try {
        await common.requestFailedJobsRerun({ github, owner, repo, sourceRunId });
    }
    catch {
        await writeRerunOutcomeSummary({
            summary,
            sourceRunUrl,
            sourceRunAttempt,
            heading: 'Rerun request failed',
            message: 'The failed-job rerun request did not complete successfully. GitHub may still have started a rerun.',
        });
        return {
            ...mainRerunState,
            outcome: 'failed',
            reason: 'request-failed',
        };
    }

    await common.writeRerunRequestedSummary({
        summary,
        sourceRunUrl,
        sourceRunAttempt,
        rerunAttemptNumber: sourceRunAttempt + 1,
        retryableJobs,
        postedComments: [],
    });
    return mainRerunState;
}

module.exports = {
    mainMaxRunAttempt,
    classifyMainFailedJob,
    analyzeMainFailedJobs,
    analyzeMainFailures,
    rerunMainFailures,
};
