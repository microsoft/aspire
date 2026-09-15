'use strict';

const fs = require('node:fs');
const { isDeepStrictEqual } = require('node:util');

const validVerdicts = new Set(['transient-infra', 'flaky-test', 'code-issue', 'pr-test-failure', 'mixed', 'unknown']);
const retryableVerdicts = new Set(['transient-infra', 'flaky-test', 'mixed', 'unknown']);
const maxTestResultsArtifactBytes = 100 * 1024 * 1024;

const redactedFieldLimits = {
    error: 1000,
    stack_trace: 2000,
    standard_output: 4000,
    standard_error: 4000,
};

function escapeHtml(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
}

function redactSensitiveData(value) {
    return String(value ?? '')
        .replace(/-----BEGIN ([A-Z ]*PRIVATE KEY)-----[\s\S]*?(?:-----END \1-----|$)/g, '[REDACTED]')
        .replace(/\b(authorization|proxy-authorization)(\s*:\s*)(basic|bearer)\s+[^\s,;]+/gi, '$1$2$3 [REDACTED]')
        .replace(/\b(x-api-key|api-key|access-token|client-secret)(\s*:\s*)[^\s,;]+/gi, '$1$2[REDACTED]')
        .replace(/\b([A-Za-z][A-Za-z0-9+.-]*:\/\/)[^\s/:@]*:[^\s/@]+@/g, '$1[REDACTED]:[REDACTED]@')
        .replace(/\b([A-Za-z][A-Za-z0-9+.-]*:\/\/)[^\s/:@]+@/g, '$1[REDACTED]@')
        .replace(/\b(eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})\b/g, '[REDACTED]')
        .replace(/\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|(?:npm|pypi)-[A-Za-z0-9_-]{20,})\b/g, '[REDACTED]')
        .replace(/([?&](?:sig|signature|token|access_token|api[_-]?key|password|secret|client_secret)=)[^&\s]+/gi, '$1[REDACTED]')
        .replace(/(["'](?:password|passwd|pwd|token|pgpassword|_?authToken|_?auth|accessToken|refreshToken|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|connection[_-]?string)["']\s*:\s*)(["'])(?:\\[^\r\n]|(?!\2)[^\\\r\n])*\2/gi, '$1$2[REDACTED]$2')
        .replace(/((?:^|\s)--(?:password|passwd|pwd|token|auth[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|connection[_-]?string|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)\s+)(["'])(?:\\[^\r\n]|(?!\2)[^\\\r\n])*\2/gim, '$1$2[REDACTED]$2')
        .replace(/((?:^|[^\S\r\n])--(?:password|passwd|pwd|token|auth[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|connection[_-]?string|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)[^\S\r\n]+)(?!["'])([^\s]+)/gim, '$1[REDACTED]')
        .replace(/\b((?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)(\s*[:=]\s*)(["'])(?:\\[^\r\n]|(?!\3)[^\\\r\n])*\3/gi, '$1$2$3[REDACTED]$3')
        .replace(/\b((?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)(\s*[:=]\s*)(?!["'])([^;\r\n]+)/gi, '$1$2[REDACTED]');
}

function redactJson(value, propertyName) {
    if (typeof value === 'string') {
        const redacted = redactSensitiveData(value);
        const limit = redactedFieldLimits[propertyName];

        // Redact before truncating so credentials that cross a field boundary are still recognized.
        return limit ? redacted.slice(0, limit) : redacted;
    }

    if (Array.isArray(value)) {
        return value.map(item => redactJson(item, propertyName));
    }

    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, redactJson(item, key)]));
    }

    return value;
}

function validatePublication(analysis, trustedContext, trustedEvidence) {
    const manifest = {
        run_id: Number(trustedContext.run_id),
        run_attempt: Number(trustedContext.run_attempt),
        run_url: trustedContext.run_url,
        run_event: trustedContext.run_event,
        analyzed_commit_sha: trustedContext.analyzed_commit_sha,
        pr_number: Number(trustedContext.pr_number),
        pr_state: trustedContext.pr_state,
        dry_run: trustedContext.dry_run === true,
        verdict: analysis.verdict,
        rerun_eligible: analysis.rerun?.eligible,
    };

    if (!Number.isInteger(manifest.run_id)
        || !Number.isInteger(manifest.run_attempt)
        || !Number.isInteger(manifest.pr_number)
        || Number(analysis.run_id) !== manifest.run_id
        || Number(analysis.run_attempt) !== manifest.run_attempt
        || analysis.run_url !== manifest.run_url
        || analysis.run_event !== manifest.run_event
        || analysis.analyzed_commit_sha !== manifest.analyzed_commit_sha
        || Number(analysis.pr?.number) !== manifest.pr_number
        || analysis.pr?.state !== manifest.pr_state
        || !isDeepStrictEqual(analysis.evidence, trustedEvidence)) {
        throw new Error('Agent analysis does not match the collected run, analyzed commit, or evidence metadata.');
    }

    if (!validVerdicts.has(manifest.verdict)) {
        throw new Error(`Invalid analysis verdict '${manifest.verdict}'.`);
    }

    if (typeof manifest.rerun_eligible !== 'boolean') {
        throw new Error('Analysis result must contain a boolean rerun.eligible decision.');
    }

    const expectedRerunEligible = manifest.run_event === 'pull_request'
        && retryableVerdicts.has(manifest.verdict)
        && manifest.run_attempt <= 3
        && manifest.pr_state === 'open';
    if (manifest.rerun_eligible !== expectedRerunEligible) {
        throw new Error(`Rerun decision conflicts with run event '${manifest.run_event}' and verdict '${manifest.verdict}'.`);
    }

    return manifest;
}

function selectTestResultsArtifact(artifacts) {
    const candidates = Array.isArray(artifacts)
        ? artifacts
            .filter(artifact => artifact?.name === 'All-TestResults' && artifact.expired !== true)
            .sort((left, right) => Date.parse(right.created_at) - Date.parse(left.created_at)
                || Number(right.id) - Number(left.id))
        : [];
    const selected = candidates[0];

    return selected && Number(selected.size_in_bytes) <= maxTestResultsArtifactBytes ? selected : null;
}

function validateRerunRequest(analysis, trustedContext, trustedEvidence, request) {
    const manifest = validatePublication(analysis, trustedContext, trustedEvidence);
    const requestedRunId = Number(request?.run_id);
    const requestedPrNumbers = String(request?.pr_numbers ?? '').split(',').map(Number).filter(number => number > 0);
    const requestedReason = String(request?.reason ?? '');

    if (requestedRunId !== manifest.run_id
        || requestedPrNumbers.length !== 1
        || requestedPrNumbers[0] !== manifest.pr_number) {
        throw new Error('Rerun request does not match the analyzed run and pull request.');
    }

    if (!manifest.rerun_eligible
        || typeof analysis.rerun?.reason !== 'string'
        || analysis.rerun.reason.length === 0
        || requestedReason !== analysis.rerun.reason
        || !retryableVerdicts.has(manifest.verdict)) {
        throw new Error(`Rerun request conflicts with analysis verdict '${manifest.verdict}'.`);
    }

    return manifest;
}

function getTrxText(value) {
    if (value === null || value === undefined) {
        return '';
    }

    return typeof value === 'object' ? String(value['+content'] ?? '') : String(value);
}

function extractTestFailures(trx) {
    const results = trx?.TestRun?.Results?.UnitTestResult ?? [];
    const resultList = Array.isArray(results) ? results : [results];

    return resultList
        .filter(result => result['+@outcome'] === 'Failed')
        .map(result => ({
            test: String(result['+@testName'] ?? ''),
            error: getTrxText(result.Output?.ErrorInfo?.Message),
            stack_trace: getTrxText(result.Output?.ErrorInfo?.StackTrace),
            standard_output: getTrxText(result.Output?.StdOut),
            standard_error: getTrxText(result.Output?.StdErr),
        }));
}

function extractMochaFailures(report, jobName) {
    const failures = Array.isArray(report?.failures) ? report.failures : [];

    // Mocha's JSON reporter emits failures as:
    //   { "fullTitle": "suite test", "err": { "message": "...", "stack": "..." } }
    // Hook failures use the same shape, with the hook description included in fullTitle.
    return failures.map(failure => ({
        test: String(failure.fullTitle ?? failure.title ?? ''),
        error: String(failure.err?.message ?? ''),
        stack_trace: String(failure.err?.stack ?? ''),
        standard_output: '',
        standard_error: '',
        ...(jobName ? { job: jobName } : {}),
    }));
}

// TRX display names can contain backticks and line breaks. Collapse line breaks
// and use a fence longer than any backtick run so the name cannot inject Markdown.
function toInlineCode(value) {
    const normalized = String(value).replace(/\r\n?|\n/g, ' ');
    const longestRun = (normalized.match(/`+/g) ?? []).reduce((max, run) => Math.max(max, run.length), 0);
    const fence = '`'.repeat(longestRun + 1);
    const pad = normalized.startsWith('`') || normalized.endsWith('`') ? ' ' : '';

    return `${fence}${pad}${normalized}${pad}${fence}`;
}

function toCodeBlock(value) {
    const content = String(value ?? '');
    const longestRun = (content.match(/`+/g) ?? []).reduce((max, run) => Math.max(max, run.length), 0);
    const fence = '`'.repeat(Math.max(3, longestRun + 1));

    return `${fence}\n${content}${content.endsWith('\n') ? '' : '\n'}${fence}`;
}

function formatTestFailures(failures) {
    const output = failures.map(failure => {
        const sections = [
            `### ${toInlineCode(failure.test)}`,
        ];

        if (failure.job) {
            sections.push('', `**Job:** ${toInlineCode(failure.job)}`);
        }

        sections.push('', '**Error:**', toCodeBlock(failure.error));

        for (const [label, value] of [
            ['Stack Trace', failure.stack_trace],
            ['Standard Output (sensitive values redacted)', failure.standard_output],
            ['Standard Error (sensitive values redacted)', failure.standard_error],
        ]) {
            if (value) {
                sections.push(`**${label}:**`, toCodeBlock(value));
            }
        }

        return sections.join('\n');
    }).join('\n');

    return output ? `${output}\n` : '';
}

function getCauseJobName(analysis, cause) {
    return cause.job_name || analysis.failed_jobs?.[0]?.name || 'unknown';
}

function getObservedAt(analysis) {
    const analyzedAt = analysis.analyzed_at;

    // The analysis is model-generated. Preserve its timestamp when valid, but do not let
    // a missing or malformed optional field prevent every cause from being published.
    return typeof analyzedAt === 'string' && !Number.isNaN(Date.parse(analyzedAt))
        ? analyzedAt
        : new Date().toISOString();
}

function buildOccurrence(analysis, cause) {
    return {
        run_id: analysis.run_id,
        run_url: analysis.run_url || '',
        job: getCauseJobName(analysis, cause),
        pr_number: analysis.pr?.number || 0,
        observed_at: getObservedAt(analysis),
    };
}

function addOccurrence(analysis, cause) {
    return redactJson({
        ...cause,
        occurrences: [buildOccurrence(analysis, cause)],
    });
}

function buildOccurrenceRow(analysis, cause) {
    const occurrence = buildOccurrence(analysis, cause);
    const date = occurrence.observed_at.split('T', 1)[0];

    return `| ${date} | [${occurrence.run_id}](${occurrence.run_url}) | ${occurrence.job} | #${occurrence.pr_number} |`;
}

function normalizeIssueTitle(value) {
    return redactSensitiveData(value).replace(/\r\n?|\n/g, ' ').trim();
}

function getValidatedJobUrl(runUrl, jobUrl) {
    try {
        const run = new URL(runUrl);
        const job = new URL(jobUrl);
        const escapedRunPath = run.pathname.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

        if (run.protocol === 'https:'
            && run.hostname === 'github.com'
            && !run.username
            && !run.password
            && job.origin === run.origin
            && !job.username
            && !job.password
            && !job.search
            && !job.hash
            && new RegExp(`^${escapedRunPath}/job/\\d+$`).test(job.pathname)) {
            return job.href;
        }
    } catch {
        // Model-authored URLs are optional; omit malformed or untrusted values.
    }

    return '';
}

function buildJobList(analysis, classification) {
    return (analysis.failed_jobs ?? [])
        .filter(job => !classification || job.classification === classification)
        .map(job => {
            if (!classification) {
                return `- ${toInlineCode(job.name)} — ${job.reason ?? ''} (${job.classification ?? ''})`;
            }

            const validatedJobUrl = getValidatedJobUrl(analysis.run_url, job.url);
            const jobLink = validatedJobUrl ? ` ([job](${validatedJobUrl}))` : '';
            return `- ${toInlineCode(job.name)}${jobLink}\n  - **Why likely flaky**: ${job.reason ?? ''}`;
        })
        .join('\n');
}

function buildFlakyTestList(analysis) {
    return (analysis.failed_tests ?? [])
        .filter(test => test.classification === 'flaky')
        .map(test => {
            const stackTrace = test.stack_trace
                ? `\n  - **Stack Trace** (first frames):\n${toCodeBlock(test.stack_trace.split('\n').slice(0, 5).join('\n'))}`
                : '';

            return `- ${toInlineCode(test.name)} in job ${toInlineCode(test.job)}\n  - **Error**: ${test.error ?? ''}${stackTrace}\n  - **Why likely flaky**: ${test.reason ?? ''}`;
        })
        .join('\n');
}

function buildEvidenceNote(analysis) {
    if (analysis.evidence?.completeness !== 'partial') {
        return '';
    }

    const gaps = Array.isArray(analysis.evidence.gaps)
        ? analysis.evidence.gaps.map(gap => `- ${toInlineCode(gap)}`).join('\n')
        : '';

    return `\n\n**Evidence completeness:** Partial${gaps ? `\n${gaps}` : ''}`;
}

function buildRerunNote(analysis, dryRun) {
    if (analysis.rerun?.eligible === true) {
        if (dryRun) {
            return '\n\nThe analysis requested an automatic rerun, but this manual dry run suppressed the rerun request.';
        }

        return '\n\nThe analysis requested an automatic rerun of the failed CI jobs.';
    }

    return '\n\nThe CI will not be automatically rerun.';
}

function buildPrComment(analysis, dryRun = false) {
    const marker = '<!-- analyze-ci-failure -->';
    const runUrl = analysis.run_url ?? '';
    const allJobs = buildJobList(analysis);
    const evidenceNote = buildEvidenceNote(analysis);
    const rerunNote = buildRerunNote(analysis, dryRun);

    if (analysis.verdict === 'transient-infra') {
        return `${marker}\n🔍 **CI Failure Analysis: Transient Infrastructure Failure**\n\nThe CI build failed due to transient infrastructure issues.\n\n**Failed jobs:**\n${allJobs}${evidenceNote}${rerunNote}\n\n[View the workflow run](${runUrl}).\n`;
    }

    if (analysis.verdict === 'flaky-test') {
        const flakyTests = buildFlakyTestList(analysis);
        const hasFlakyTests = flakyTests.length > 0;
        const heading = hasFlakyTests ? 'Suspected flaky test(s)' : 'Suspected flaky failure(s)';
        const failures = hasFlakyTests ? flakyTests : buildJobList(analysis, 'flaky-test');

        return `${marker}\n⚠️ **CI Failure Analysis: Possible Flaky Test(s)**\n\nThe CI build failed due to test failure(s) that appear unrelated to the PR changes. These may be flaky tests.\n\n**${heading}:**\n${failures}${evidenceNote}${rerunNote}\n\n**Suggested actions:**\n- If the test continues to fail, consider [quarantining it](https://github.com/microsoft/aspire/blob/main/docs/quarantined-tests.md) using \`/quarantine-test <test name> <issue URL>\`\n- Search [existing issues](https://github.com/microsoft/aspire/issues?q=is%3Aissue+label%3Atest-failure) to see if this test is already known to be flaky\n\n[View the workflow run](${runUrl}).\n`;
    }

    if (analysis.verdict === 'code-issue') {
        return `${marker}\n❌ **CI Failure Analysis: Code Issue Detected**\n\nThe CI build failed due to issue(s) caused by changes in this PR.\n\n**Failed jobs:**\n${allJobs}${evidenceNote}\n\nThe CI will not be automatically rerun. Please fix the issue and push an updated commit.\n`;
    }

    if (analysis.verdict === 'pr-test-failure') {
        return `${marker}\n❌ **CI Failure Analysis: Test Regression Detected**\n\nThe CI build contains test failure(s) caused by changes in this PR.\n\n**Failed jobs:**\n${allJobs}${evidenceNote}\n\nThe CI will not be automatically rerun. Please fix the failing behavior or update the affected test, then push an updated commit.\n`;
    }

    if (analysis.verdict === 'unknown') {
        return `${marker}\n❓ **CI Failure Analysis: Unable to Classify**\n\nThe available evidence was insufficient to determine whether this failure is transient or caused by the PR.\n\n**Failed jobs:**\n${allJobs}${evidenceNote}${rerunNote}\n\nReview the [workflow run logs](${runUrl}) if the next attempt fails.\n`;
    }

    return `${marker}\n⚠️ **CI Failure Analysis: Mixed Failures**\n\nThe CI build contains both transient and non-transient failures.\n\n**Failed jobs:**\n${allJobs}${evidenceNote}${rerunNote}\n\nPlease review the failures above.\n`;
}

function getFailureInformation(analysis, cause) {
    const jobName = getCauseJobName(analysis, cause);

    if (cause.type === 'flaky-test') {
        const failedTest = analysis.failed_tests?.find(test => test.name === cause.test_name && test.job === jobName) || {};
        const details = [
            ['Error', failedTest.error],
            ['Stack Trace', failedTest.stack_trace],
            ['Standard Output', failedTest.standard_output],
            ['Standard Error', failedTest.standard_error],
        ]
            .filter(([, value]) => value)
            .map(([label, value]) => `${label}:\n${value}`)
            .join('\n\n');

        return {
            classificationAnalysis: failedTest.reason || cause.analysis || '',
            details: details || cause.failure_details || cause.error_pattern || '',
        };
    }

    const failedJob = analysis.failed_jobs?.find(job => job.name === jobName) || {};

    return {
        classificationAnalysis: failedJob.reason || cause.analysis || '',
        details: cause.failure_details || cause.error_pattern || '',
    };
}

function buildIssueBody(analysis, cause, marker) {
    const jobName = getCauseJobName(analysis, cause);
    const failureInformation = getFailureInformation(analysis, cause);
    const title = normalizeIssueTitle(cause.title);
    const testName = cause.test_name || '';
    const outputSummary = cause.type === 'flaky-test' ? 'Test output' : 'Job output snippet';
    const buildError = testName
        ? `Build error leg or test failing: ${jobName} / ${toInlineCode(testName)}`
        : `Build error leg: ${jobName}`;

    return `${marker}

## Build Information

Build: ${analysis.run_url || ''}
${buildError}
Pull request: #${analysis.pr?.number || 0}

## Classification Analysis

<pre>
${escapeHtml(redactSensitiveData(failureInformation.classificationAnalysis))}
</pre>

## Failure Information

<details>
<summary>${outputSummary}</summary>

<pre>
${escapeHtml(redactSensitiveData(failureInformation.details))}
</pre>

</details>

## Description

<pre>
${escapeHtml(title)}
</pre>

**Type**: ${cause.type}

## Occurrences

| Date | Build | Job | PR |
|------|-------|-----|----|
${buildOccurrenceRow(analysis, cause)}
`;
}

function readJson(path) {
    return JSON.parse(fs.readFileSync(path === '-' ? 0 : path, 'utf8'));
}

function main(args) {
    const [operation, analysisPath, causePath, marker] = args;
    const analysis = readJson(analysisPath);
    let cause;
    const getCause = () => cause ??= readJson(causePath);

    switch (operation) {
        case 'redact':
            process.stdout.write(JSON.stringify(redactJson(analysis)));
            break;
        case 'extract-test-failures': {
            const failures = extractTestFailures(analysis);
            if (failures.length > 0) {
                process.stdout.write(`${failures.map(failure => JSON.stringify(failure)).join('\n')}\n`);
            }
            break;
        }
        case 'extract-mocha-failures': {
            const failures = extractMochaFailures(analysis, causePath);
            if (failures.length > 0) {
                process.stdout.write(`${failures.map(failure => JSON.stringify(failure)).join('\n')}\n`);
            }
            break;
        }
        case 'format-test-failures':
            process.stdout.write(formatTestFailures(analysis));
            break;
        case 'pr-comment':
            process.stdout.write(buildPrComment(analysis, causePath ? readJson(causePath).dry_run === true : false));
            break;
        case 'validate-publication':
            process.stdout.write(JSON.stringify(validatePublication(analysis, readJson(causePath), readJson(marker))));
            break;
        case 'validate-rerun-request':
            process.stdout.write(JSON.stringify(validateRerunRequest(analysis, readJson(causePath), readJson(marker), readJson(args[4]))));
            break;
        case 'select-test-results-artifact':
            process.stdout.write(JSON.stringify(selectTestResultsArtifact(analysis)));
            break;
        case 'job-name':
            process.stdout.write(getCauseJobName(analysis, getCause()));
            break;
        case 'add-occurrence':
            process.stdout.write(JSON.stringify(addOccurrence(analysis, getCause())));
            break;
        case 'occurrence-row':
            process.stdout.write(buildOccurrenceRow(analysis, getCause()));
            break;
        case 'issue-body':
            process.stdout.write(buildIssueBody(analysis, getCause(), marker));
            break;
        case 'issue-title':
            process.stdout.write(`[CI Failure] ${normalizeIssueTitle(getCause().title)}`);
            break;
        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

if (require.main === module) {
    main(process.argv.slice(2));
}

module.exports = {
    addOccurrence,
    buildIssueBody,
    buildPrComment,
    buildOccurrence,
    buildOccurrenceRow,
    escapeHtml,
    extractMochaFailures,
    extractTestFailures,
    formatTestFailures,
    getCauseJobName,
    getObservedAt,
    normalizeIssueTitle,
    redactJson,
    redactSensitiveData,
    selectTestResultsArtifact,
    toCodeBlock,
    toInlineCode,
    validatePublication,
    validateRerunRequest,
};