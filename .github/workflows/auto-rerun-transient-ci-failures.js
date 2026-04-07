// Shared matcher, summary, and rerun helpers for the transient CI rerun workflow.
const failureConclusions = new Set(['failure', 'cancelled', 'timed_out', 'startup_failure']);
const ignoredJobs = new Set(['Final Results', 'Tests / Final Test Results']);
const defaultMaxRetryableJobs = 5;
const defaultMaxRunAttempt = 3;

const retryableWithAnnotationStepPatterns = [
    /^Set up job$/i,
    /^Checkout code$/i,
    /^Set up \.NET Core$/i,
    /^Install sdk for nuget based testing$/i,
    /^Upload logs, and test results$/i,
];

const ignoredFailureStepPatterns = [
    /^Run tests\b/i,
    /^Run nuget dependent tests\b/i,
    /^Build test project$/i,
    /^Build and archive test project$/i,
    /^Build RID-specific packages\b/i,
    /^Build Python validation image$/i,
    /^Build with packages$/i,
    /^Run .*SDK validation$/i,
    /^Check validation results$/i,
    /^Generate test results summary$/i,
    /^Copy CLI E2E recordings for upload$/i,
    /^Upload CLI E2E recordings$/i,
    /^Post Checkout code$/i,
    /^Install dependencies$/i,
];

const testExecutionFailureStepPatterns = [
    /^Run tests\b/i,
    /^Run nuget dependent tests\b/i,
];

const transientAnnotationPatterns = [
    /The job was not acquired by Runner of type hosted even after multiple attempts/i,
    /The hosted runner lost communication with the server/i,
    /Failed to resolve action download info/i,
    /Failed to CreateArtifact: Unable to make request: ENOTFOUND/i,
    /\bENOTFOUND\b/i,
    /\bECONNRESET\b/i,
    /\bEPROTO\b/i,
    /\bBad Gateway\b/i,
    /\bCould not resolve host\b/i,
    /\bSSL connection could not be established\b/i,
    /getaddrinfo ENOTFOUND builds\.dotnet\.microsoft\.com/i,
    /(timed out|failed to connect|could not resolve|ENOTFOUND|ECONNRESET|EPROTO).{0,120}builds\.dotnet\.microsoft\.com/i,
    /builds\.dotnet\.microsoft\.com.{0,120}(timed out|failed to connect|could not resolve|ENOTFOUND|ECONNRESET|EPROTO)/i,
    /(timed out|failed to connect|failed to respond|ENOTFOUND|ECONNRESET|Bad Gateway|SSL connection could not be established).{0,120}api\.github\.com/i,
    /api\.github\.com.{0,120}(timed out|failed to connect|failed to respond|ENOTFOUND|ECONNRESET|Bad Gateway|SSL connection could not be established)/i,
    /expected 'packfile'/i,
    /\bRPC failed\b/i,
    /\bRecv failure\b/i,
    /Couldn't connect to server/i,
    /Failed to connect to github\.com port/i,
    /The requested URL returned error:\s*(502|503|504)/i,
];

const ignoredFailureStepOverridePatterns = [
    /The job was not acquired by Runner of type hosted even after multiple attempts/i,
    /The hosted runner lost communication with the server/i,
    /Failed to resolve action download info/i,
    /Failed to download action .*api\.github\.com.*(502|503|504|Bad Gateway)/i,
];

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

const infrastructureNetworkFailureLogOverridePatterns = [
    /Unable to load the service index for source https:\/\/(?:pkgs\.dev\.azure\.com\/dnceng|dnceng\.pkgs\.visualstudio\.com)\/public\/_packaging\//i,
    /(timed out|failed to connect|could not resolve|ENOTFOUND|ECONNRESET|EPROTO|Bad Gateway|SSL connection could not be established).{0,160}https:\/\/(?:pkgs\.dev\.azure\.com\/dnceng|dnceng\.pkgs\.visualstudio\.com)\/public\/_packaging\//i,
    /https:\/\/(?:pkgs\.dev\.azure\.com\/dnceng|dnceng\.pkgs\.visualstudio\.com)\/public\/_packaging\/.{0,160}(timed out|failed to connect|could not resolve|ENOTFOUND|ECONNRESET|EPROTO|Bad Gateway|SSL connection could not be established)/i,
    /(timed out|failed to connect|could not resolve|ENOTFOUND|ECONNRESET|EPROTO|Bad Gateway|SSL connection could not be established).{0,160}builds\.dotnet\.microsoft\.com/i,
    /builds\.dotnet\.microsoft\.com.{0,160}(timed out|failed to connect|could not resolve|ENOTFOUND|ECONNRESET|EPROTO|Bad Gateway|SSL connection could not be established)/i,
    /(timed out|failed to connect|failed to respond|could not resolve|ENOTFOUND|ECONNRESET|EPROTO|Bad Gateway|SSL connection could not be established).{0,160}api\.github\.com/i,
    /api\.github\.com.{0,160}(timed out|failed to connect|failed to respond|could not resolve|ENOTFOUND|ECONNRESET|EPROTO|Bad Gateway|SSL connection could not be established)/i,
    /fatal: unable to access 'https:\/\/github\.com\/.*': The requested URL returned error:\s*(502|503|504)/i,
    /Failed to connect to github\.com port/i,
    /expected 'packfile'/i,
    /\bRPC failed\b/i,
    /\bRecv failure\b/i,
];

function matchesAny(value, patterns) {
    return patterns.some(pattern => pattern.test(value));
}

function findMatchingPattern(value, patterns) {
    return patterns.find(pattern => pattern.test(value)) ?? null;
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

function getPullRequestNumbers(workflowRun) {
    return [...new Set((workflowRun?.pull_requests || [])
        .map(pullRequest => pullRequest.number)
        .filter(Number.isInteger))];
}

function getHeadRepositoryOwnerLogin(workflowRun) {
    return workflowRun?.head_repository?.owner?.login ?? workflowRun?.head_repository?.owner?.name ?? null;
}

function matchesWorkflowRunHead(pullRequest, workflowRun, headOwner, headBranch) {
    const pullRequestHead = pullRequest?.head;
    const pullRequestHeadOwner = pullRequestHead?.repo?.owner?.login ?? pullRequestHead?.user?.login ?? null;

    if (typeof pullRequestHeadOwner !== 'string' || pullRequestHeadOwner.toLowerCase() !== headOwner.toLowerCase()) {
        return false;
    }

    if (pullRequestHead?.ref !== headBranch) {
        return false;
    }

    const workflowHeadSha = workflowRun?.head_sha;
    const pullRequestHeadSha = pullRequestHead?.sha;

    return typeof workflowHeadSha !== 'string'
        || workflowHeadSha.length === 0
        || typeof pullRequestHeadSha !== 'string'
        || pullRequestHeadSha.length === 0
        || pullRequestHeadSha === workflowHeadSha;
}

async function listPullRequestsByHead({ github, owner, repo, head, warn }) {
    const pullRequests = [];

    try {
        for (let page = 1; ; page++) {
            const response = await github.request('GET /repos/{owner}/{repo}/pulls', {
                owner,
                repo,
                state: 'all',
                head,
                per_page: 100,
                page,
            });

            pullRequests.push(...(response.data || []));

            if (!response.headers?.link || !response.headers.link.includes('rel="next"')) {
                return pullRequests;
            }
        }
    }
    catch (error) {
        if (typeof warn === 'function') {
            warn(`Failed to resolve pull requests for head '${head}': ${error.message}`);
        }
        return [];
    }
}

async function getAssociatedPullRequestNumbers({ github, owner, repo, workflowRun, warn }) {
    const pullRequestNumbers = getPullRequestNumbers(workflowRun);
    if (pullRequestNumbers.length > 0) {
        return pullRequestNumbers;
    }

    const headOwner = getHeadRepositoryOwnerLogin(workflowRun);
    const headBranch = workflowRun?.head_branch;

    if (typeof headOwner !== 'string' || headOwner.length === 0 || typeof headBranch !== 'string' || headBranch.length === 0) {
        return [];
    }

    const responseData = await listPullRequestsByHead({
        github,
        owner,
        repo,
        head: `${headOwner}:${headBranch}`,
        warn,
    });

    const matchingPullRequests = responseData
        .filter(pullRequest => matchesWorkflowRunHead(pullRequest, workflowRun, headOwner, headBranch));
    const fallbackPullRequestNumbers = [...new Set(matchingPullRequests
        .map(pullRequest => pullRequest.number)
        .filter(Number.isInteger))];

    return fallbackPullRequestNumbers.length === 1 ? fallbackPullRequestNumbers : [];
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

function getFailureStepSignals(failedSteps) {
    const hasRetryableStep = failedSteps.some(step => matchesAny(step, retryableWithAnnotationStepPatterns));
    const hasIgnoredFailureStep = failedSteps.some(step => matchesAny(step, ignoredFailureStepPatterns));

    return {
        hasRetryableStep,
        hasIgnoredFailureStep,
        shouldInspectAnnotations: failedSteps.length === 0 || hasRetryableStep || hasIgnoredFailureStep,
    };
}

function canUseInfrastructureNetworkLogOverride(failedSteps) {
    return failedSteps.length > 0 && !failedSteps.some(step => matchesAny(step, testExecutionFailureStepPatterns));
}

function formatFailedStepLabel(failedSteps, failedStepText) {
    const label = failedSteps.length === 1 ? 'Failed step' : 'Failed steps';
    return `${label} '${failedStepText}'`;
}

function isSingleFailedStep(failedSteps) {
    return failedSteps.length === 1;
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

function findInfrastructureNetworkLogOverridePattern(jobLogText) {
    return findMatchingPattern(jobLogText, infrastructureNetworkFailureLogOverridePatterns);
}

function getInfrastructureNetworkLogOverrideReason(failedSteps, failedStepText, matchedPattern) {
    const patternText = formatMatchedPatternForMarkdown(matchedPattern);
    return `${formatFailedStepLabel(failedSteps, failedStepText)} will be retried because the job log shows a likely transient infrastructure network failure.${patternText}`;
}

function getOutsideRetryRulesReason(failedSteps, failedStepText) {
    return `${formatFailedStepLabel(failedSteps, failedStepText)} ${isSingleFailedStep(failedSteps) ? 'is' : 'are'} not covered by the retry-safe rerun rules.`;
}

function getNoRetryMatchReason({
    failedSteps,
    failedStepText,
    hasRetryableStep,
    hasIgnoredFailureStep,
    hasTestExecutionFailureStep,
    annotationsText,
}) {
    const failedStepLabel = formatFailedStepLabel(failedSteps, failedStepText);

    if (hasTestExecutionFailureStep) {
        return `${failedStepLabel} ${isSingleFailedStep(failedSteps) ? 'includes' : 'include'} a test execution failure, so the job was not retried without a high-confidence infrastructure override.`;
    }

    if (hasIgnoredFailureStep) {
        return `${failedStepLabel} ${isSingleFailedStep(failedSteps) ? 'is' : 'are'} only retried when the job shows a high-confidence infrastructure override, and none was found.`;
    }

    if (hasRetryableStep) {
        return `${failedStepLabel} did not include a retry-safe transient infrastructure signal in the job annotations.`;
    }

    if (annotationsText) {
        return 'The job annotations did not show a retry-safe transient infrastructure failure.';
    }

    return 'No retry-safe transient infrastructure signal was found in the available job diagnostics.';
}

function classifyFailedJob(job, annotationsOrText, jobLogText = '', options = {}) {
    const {
        matchedInfrastructureNetworkLogOverridePattern: preMatchedInfrastructureNetworkLogOverridePattern,
    } = options;
    const failedSteps = getFailedSteps(job);
    const failedStepText = failedSteps.join(' | ');
    const { hasRetryableStep, hasIgnoredFailureStep, shouldInspectAnnotations } = getFailureStepSignals(failedSteps);
    const hasTestExecutionFailureStep = failedSteps.some(step => matchesAny(step, testExecutionFailureStepPatterns));
    const matchedInfrastructureNetworkLogOverridePattern =
        !hasTestExecutionFailureStep
            ? preMatchedInfrastructureNetworkLogOverridePattern === undefined
                ? findInfrastructureNetworkLogOverridePattern(jobLogText)
                : preMatchedInfrastructureNetworkLogOverridePattern
            : null;
    const matchesInfrastructureNetworkLogOverride = matchedInfrastructureNetworkLogOverridePattern !== null;

    if (!shouldInspectAnnotations) {
        if (matchesInfrastructureNetworkLogOverride) {
            return {
                retryable: true,
                failedSteps,
                reason: getInfrastructureNetworkLogOverrideReason(failedSteps, failedStepText, matchedInfrastructureNetworkLogOverridePattern),
            };
        }

        return {
            retryable: false,
            failedSteps,
            reason: getOutsideRetryRulesReason(failedSteps, failedStepText),
        };
    }

    const annotationsText = toAnnotationText(annotationsOrText);
    const matchesTransientAnnotation = matchesAny(annotationsText, transientAnnotationPatterns);
    const matchesIgnoredFailureStepOverride = matchesAny(annotationsText, ignoredFailureStepOverridePatterns);
    const hasOnlyPostTestCleanupFailures = failedSteps.length > 0
        && failedSteps.every(step => matchesAny(step, postTestCleanupFailureStepPatterns));
    const matchesWindowsProcessInitializationFailure = matchesAny(annotationsText, windowsProcessInitializationFailurePatterns);

    if (matchesTransientAnnotation && failedSteps.length === 0) {
        return {
            retryable: true,
            failedSteps,
            reason: 'Job-level runner or infrastructure failure matched the transient allowlist.',
        };
    }

    if (hasOnlyPostTestCleanupFailures && matchesWindowsProcessInitializationFailure) {
        return {
            retryable: true,
            failedSteps,
            reason: `Post-test cleanup steps '${failedStepText}' matched the Windows process initialization failure override allowlist.`,
        };
    }

    if (hasIgnoredFailureStep && matchesIgnoredFailureStepOverride) {
        return {
            retryable: true,
            failedSteps,
            reason: `Ignored failed step '${failedStepText}' matched the job-level infrastructure override allowlist.`,
        };
    }

    if (hasRetryableStep && !hasIgnoredFailureStep && matchesTransientAnnotation) {
        return {
            retryable: true,
            failedSteps,
            reason: `Failed step '${failedStepText}' matched the transient annotation allowlist.`,
        };
    }

    if (matchesInfrastructureNetworkLogOverride) {
        return {
            retryable: true,
            failedSteps,
            reason: getInfrastructureNetworkLogOverrideReason(failedSteps, failedStepText, matchedInfrastructureNetworkLogOverridePattern),
        };
    }

    return {
        retryable: false,
        failedSteps,
        reason: getNoRetryMatchReason({
            failedSteps,
            failedStepText,
            hasRetryableStep,
            hasIgnoredFailureStep,
            hasTestExecutionFailureStep,
            annotationsText,
        }),
    };
}

async function analyzeFailedJobs({
    jobs,
    getAnnotationsForJob,
    getJobLogTextForJob,
    maxRetryableJobs = defaultMaxRetryableJobs,
}) {
    const normalizedMaxRetryableJobs =
        Number.isInteger(maxRetryableJobs) && maxRetryableJobs >= 0
            ? maxRetryableJobs
            : defaultMaxRetryableJobs;
    const failedJobs = (jobs || []).filter(job => failureConclusions.has(job.conclusion) && !ignoredJobs.has(job.name));
    const retryableJobs = [];
    const skippedJobs = [];

    for (const job of failedJobs) {
        const failedSteps = getFailedSteps(job);
        const { shouldInspectAnnotations } = getFailureStepSignals(failedSteps);
        const annotations = shouldInspectAnnotations && getAnnotationsForJob
            ? await getAnnotationsForJob(job)
            : '';
        let classification = classifyFailedJob(
            job,
            annotations
        );

        const shouldInspectLogs =
            !classification.retryable &&
            getJobLogTextForJob &&
            canUseInfrastructureNetworkLogOverride(failedSteps) &&
            normalizedMaxRetryableJobs > 0 &&
            retryableJobs.length <= normalizedMaxRetryableJobs;

        if (shouldInspectLogs) {
            const jobLogText = await getJobLogTextForJob(job);
            const matchedInfrastructureNetworkLogOverridePattern = findInfrastructureNetworkLogOverridePattern(jobLogText);
            classification = classifyFailedJob(
                job,
                annotations,
                jobLogText,
                { matchedInfrastructureNetworkLogOverridePattern }
            );
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

    return { failedJobs, retryableJobs, skippedJobs };
}

function computeRerunEligibility({
    retryableCount,
    maxRetryableJobs = defaultMaxRetryableJobs,
    runAttempt = 1,
    maxRunAttempt = defaultMaxRunAttempt
}) {
    if (retryableCount <= 0 || runAttempt > maxRunAttempt) {
        return false;
    }

    // For attempts after the first (runAttempt > 1) apply a stricter cap:
    // fewer than maxRetryableJobs jobs (i.e. strictly less than the cap rather
    // than less-than-or-equal).
    return runAttempt <= 1
        ? retryableCount <= maxRetryableJobs
        : retryableCount < maxRetryableJobs;
}

function computeRerunExecutionEligibility({
    dryRun,
    retryableCount,
    maxRetryableJobs = defaultMaxRetryableJobs,
    runAttempt = 1,
    maxRunAttempt = defaultMaxRunAttempt
}) {
    return !dryRun && computeRerunEligibility({ retryableCount, maxRetryableJobs, runAttempt, maxRunAttempt });
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

    if (skippedJobs.length > 0) {
        await summary.addHeading('Skipped jobs', 2);
        await summary.addTable([
            [{ data: 'Job', header: true }, { data: 'Reason', header: true }],
            ...skippedJobs.slice(0, 25).map(job => [job.name, job.reason]),
        ]);
    }

    await summary.write();
}

async function getOpenPullRequestNumbers({ github, owner, repo, pullRequestNumbers }) {
    const openPullRequestNumbers = [];

    for (const rawPullRequestNumber of new Set(pullRequestNumbers || [])) {
        const pullRequestNumber = Number(rawPullRequestNumber);

        if (!Number.isInteger(pullRequestNumber) || pullRequestNumber <= 0) {
            continue;
        }

        const response = await github.request('GET /repos/{owner}/{repo}/issues/{issue_number}', {
            owner,
            repo,
            issue_number: pullRequestNumber,
        });

        if (response.data.state === 'open' && response.data.pull_request) {
            openPullRequestNumbers.push(pullRequestNumber);
        }
    }

    return openPullRequestNumbers;
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

function formatMarkdownLink(text, url) {
    return url ? `[${text}](${url})` : text;
}

async function getLatestRunAttempt({ github, owner, repo, runId }) {
    if (!Number.isInteger(runId) || runId <= 0) {
        return null;
    }

    try {
        const response = await github.request('GET /repos/{owner}/{repo}/actions/runs/{run_id}', {
            owner,
            repo,
            run_id: runId,
        });

        const runAttempt = Number(response.data.run_attempt);
        return Number.isInteger(runAttempt) && runAttempt > 0 ? runAttempt : null;
    }
    catch {
        return null;
    }
}

function buildPullRequestCommentBody({
    failedAttemptUrl,
    rerunAttemptUrl,
    retryableJobs,
}) {
    return [
        `Re-running the failed jobs in the CI workflow for this pull request because ${retryableJobs.length} job${retryableJobs.length === 1 ? ' was' : 's were'} identified as retry-safe transient failures in ${formatMarkdownLink('the CI run attempt', failedAttemptUrl)}.`,
        `GitHub was asked to rerun all failed jobs for that attempt, and the rerun is being tracked in ${formatMarkdownLink('the rerun attempt', rerunAttemptUrl)}.`,
        'The job links below point to the failed attempt jobs that matched the retry-safe transient failure rules.',
        '',
        ...retryableJobs.map(job => {
            const jobReference = job.htmlUrl
                ? `[${job.name}](${job.htmlUrl})`
                : `\`${job.name}\``;

            return `- ${jobReference} - ${job.reason}`;
        }),
    ].join('\n');
}

async function addPullRequestComments({ github, owner, repo, pullRequestNumbers, body }) {
    const postedComments = [];

    for (const pullRequestNumber of pullRequestNumbers) {
        const response = await github.request('POST /repos/{owner}/{repo}/issues/{issue_number}/comments', {
            owner,
            repo,
            issue_number: pullRequestNumber,
            body,
        });

        postedComments.push({
            pullRequestNumber,
            htmlUrl: response.data?.html_url || null,
        });
    }

    return postedComments;
}

async function rerunMatchedJobs({
    github,
    owner,
    repo,
    retryableJobs,
    pullRequestNumbers = [],
    summary,
    sourceRunId,
    sourceRunUrl,
    sourceRunAttempt,
}) {
    if (retryableJobs.length === 0) {
        return;
    }

    const openPullRequestNumbers = await getOpenPullRequestNumbers({
        github,
        owner,
        repo,
        pullRequestNumbers,
    });

    if (pullRequestNumbers.length > 0 && openPullRequestNumbers.length === 0) {
        const failedAttemptReference = buildWorkflowRunReference(sourceRunUrl, sourceRunAttempt);
        await summary
            .addHeading('Rerun skipped');

        addSummaryReference(summary, 'Analyzed run', failedAttemptReference)
            .addRaw('All associated pull requests are closed. No jobs were rerun.')
            .addBreak()
            .addBreak();

        await summary
            .addHeading('Retryable jobs', 2)
            .addTable([
                [{ data: 'Job', header: true }, { data: 'Reason', header: true }],
                ...retryableJobs.map(job => [job.name, job.reason]),
            ])
            .write();
        return;
    }

    await github.request('POST /repos/{owner}/{repo}/actions/runs/{run_id}/rerun-failed-jobs', {
        owner,
        repo,
        run_id: sourceRunId,
    });

    const normalizedSourceRunAttempt = Number.isInteger(sourceRunAttempt) && sourceRunAttempt > 0
        ? sourceRunAttempt
        : null;
    const failedAttemptUrl = buildWorkflowRunAttemptUrl(sourceRunUrl, normalizedSourceRunAttempt);
    const latestRunAttempt = await getLatestRunAttempt({
        github,
        owner,
        repo,
        runId: sourceRunId,
    });
    const rerunAttemptNumber = latestRunAttempt && normalizedSourceRunAttempt && latestRunAttempt > normalizedSourceRunAttempt
        ? latestRunAttempt
        : normalizedSourceRunAttempt ? normalizedSourceRunAttempt + 1 : null;
    const rerunAttemptUrl = buildWorkflowRunAttemptUrl(sourceRunUrl, rerunAttemptNumber);
    const failedAttemptReference = buildWorkflowRunReference(sourceRunUrl, normalizedSourceRunAttempt);
    const rerunAttemptReference = buildWorkflowRunReference(sourceRunUrl, rerunAttemptNumber);
    let postedComments = [];

    if (openPullRequestNumbers.length > 0) {
        postedComments = await addPullRequestComments({
            github,
            owner,
            repo,
            pullRequestNumbers: openPullRequestNumbers,
            body: buildPullRequestCommentBody({
                failedAttemptUrl: failedAttemptReference.url,
                rerunAttemptUrl: rerunAttemptReference.url,
                retryableJobs,
            }),
        });
    }

    const summaryBuilder = summary
        .addHeading('Rerun requested');

    addSummaryReference(summaryBuilder, 'Failed attempt', failedAttemptReference);
    addSummaryReference(summaryBuilder, 'Rerun attempt', rerunAttemptReference);
    addSummaryCommentReferences(summaryBuilder, postedComments)
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

module.exports = {
    addPullRequestComments,
    analyzeFailedJobs,
    annotationText,
    buildPullRequestCommentBody,
    classifyFailedJob,
    computeRerunEligibility,
    computeRerunExecutionEligibility,
    defaultMaxRetryableJobs,
    formatMatchedPatternForMarkdown,
    findInfrastructureNetworkLogOverridePattern,
    getAssociatedPullRequestNumbers,
    getCheckRunIdForJob,
    getOpenPullRequestNumbers,
    getLatestRunAttempt,
    listPullRequestsByHead,
    rerunMatchedJobs,
    writeAnalysisSummary,
};
