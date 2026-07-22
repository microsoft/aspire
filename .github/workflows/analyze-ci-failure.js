'use strict';

const fs = require('node:fs');

function escapeHtml(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
}

// TRX display names can contain backticks and line breaks. Collapse line breaks
// and use a fence longer than any backtick run so the name cannot inject Markdown.
function toInlineCode(value) {
    const normalized = String(value).replace(/\r?\n/g, ' ');
    const longestRun = (normalized.match(/`+/g) ?? []).reduce((max, run) => Math.max(max, run.length), 0);
    const fence = '`'.repeat(longestRun + 1);
    const pad = normalized.startsWith('`') || normalized.endsWith('`') ? ' ' : '';

    return `${fence}${pad}${normalized}${pad}${fence}`;
}

function getCauseJobName(analysis, cause) {
    return cause.job_name || analysis.failed_jobs?.[0]?.name || 'unknown';
}

function buildOccurrence(analysis, cause) {
    return {
        run_id: analysis.run_id,
        run_url: analysis.run_url || '',
        job: getCauseJobName(analysis, cause),
        pr_number: analysis.pr?.number || 0,
        observed_at: analysis.analyzed_at,
    };
}

function addOccurrence(analysis, cause) {
    return {
        ...cause,
        occurrences: [buildOccurrence(analysis, cause)],
    };
}

function buildOccurrenceRow(analysis, cause) {
    const occurrence = buildOccurrence(analysis, cause);
    const date = occurrence.observed_at.split('T', 1)[0];

    return `| ${date} | [${occurrence.run_id}](${occurrence.run_url}) | ${occurrence.job} | #${occurrence.pr_number} |`;
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

    const failedJob = analysis.failed_jobs?.find(job => job.name === jobName)
        || analysis.failed_jobs?.find(job => job.classification === 'transient-infra')
        || {};

    return {
        classificationAnalysis: failedJob.reason || cause.analysis || '',
        details: cause.failure_details || cause.error_pattern || '',
    };
}

function buildIssueBody(analysis, cause, marker) {
    const jobName = getCauseJobName(analysis, cause);
    const failureInformation = getFailureInformation(analysis, cause);
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

${escapeHtml(failureInformation.classificationAnalysis)}

## Failure Information

<details>
<summary>${outputSummary}</summary>

<pre>
${escapeHtml(failureInformation.details)}
</pre>

</details>

## Description

${cause.title}

**Type**: ${cause.type}

## Occurrences

| Date | Build | Job | PR |
|------|-------|-----|----|
${buildOccurrenceRow(analysis, cause)}
`;
}

function readJson(path) {
    return JSON.parse(fs.readFileSync(path, 'utf8'));
}

function main(args) {
    const [operation, analysisPath, causePath, marker] = args;
    const analysis = readJson(analysisPath);
    const cause = readJson(causePath);

    switch (operation) {
        case 'job-name':
            process.stdout.write(getCauseJobName(analysis, cause));
            break;
        case 'add-occurrence':
            process.stdout.write(JSON.stringify(addOccurrence(analysis, cause)));
            break;
        case 'occurrence-row':
            process.stdout.write(buildOccurrenceRow(analysis, cause));
            break;
        case 'issue-body':
            process.stdout.write(buildIssueBody(analysis, cause, marker));
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
    buildOccurrence,
    buildOccurrenceRow,
    escapeHtml,
    getCauseJobName,
    toInlineCode,
};