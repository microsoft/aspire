// Canonical CI-failure cause rendering and memory integration.
//
// Generic issue lifecycle decisions belong to tracking-issue.js. This adapter
// supplies the cause-specific identity predicate and content, then persists the
// canonical issue URL selected by the shared reconciliation engine.

'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const tracking = require('./tracking-issue.js');

const CAUSE_LABEL = 'ci-failure-cause';
const CAUSE_ID_PATTERN = /^[a-z0-9][a-z0-9-]*$/;
const LEGACY_CAUSE_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;

function causeMarker(cause) {
    const causeId = typeof cause === 'string' ? cause : cause.id;
    return `<!-- ci-failure-cause:${causeId} -->`;
}

function causeTypeMarker(cause) {
    return `<!-- ci-failure-cause-type:${cause.type} -->`;
}

function normalizedBodyLines(body) {
    return (body ?? '').replaceAll('\r\n', '\n').split('\n');
}

function matchesCauseIssue(issue, cause) {
    const lines = normalizedBodyLines(issue?.body);
    const causeIds = [cause.id, ...(cause.aliases ?? [])]
        .filter(causeId => LEGACY_CAUSE_ID_PATTERN.test(causeId));
    if (!causeIds.some(causeId => lines[0] === causeMarker(causeId))) {
        return false;
    }

    const expectedTypeMarker = causeTypeMarker(cause);
    if (lines[1]?.startsWith('<!-- ci-failure-cause-type:')) {
        return lines[1] === expectedTypeMarker;
    }

    const legacyTypeLines = lines.filter(line => line.startsWith('**Type**: '));
    return legacyTypeLines.length === 1 && legacyTypeLines[0] === `**Type**: ${cause.type}`;
}

function escapeTableCell(value) {
    return String(value).replaceAll('|', '\\|');
}

function occurrenceRow(cause, run) {
    const date = run.analyzedAt.split('T')[0];
    const jobs = (cause.job_names ?? ['unknown']).map(escapeTableCell).join('<br>');
    const occurrenceContext = run.runScope === 'main'
        ? 'main'
        : run.prNumber > 0 ? `#${run.prNumber}` : 'unavailable';
    return `| ${date} | [${run.runId}](${run.runUrl}) | ${jobs} | ${occurrenceContext} |`;
}

function hasOccurrence(body, runId) {
    return (body ?? '').includes(`[${runId}](`);
}

function buildIssueTitle(cause) {
    const prefix = cause.type === 'main-repository-breakage'
        ? '[Main CI Failure]'
        : '[CI Failure]';
    return `${prefix} ${cause.title}`;
}

function labelsForCause(cause) {
    if (cause.type === 'flaky-test') {
        return [CAUSE_LABEL, 'test-failure'];
    }
    if (cause.type === 'main-repository-breakage') {
        return [CAUSE_LABEL, 'main-ci-break'];
    }
    return [CAUSE_LABEL];
}

function buildIssueBody(cause, run) {
    const jobs = (cause.job_names ?? ['unknown']).map(escapeTableCell).join('<br>');
    const lines = [
        causeMarker(cause),
        causeTypeMarker(cause),
        '',
        '## Build Information',
        '',
        `Build: ${run.runUrl}`,
    ];

    if (cause.type === 'main-repository-breakage') {
        lines.push(
            'Affected branch: `main`',
            `Last successful main SHA: \`${run.mainContext?.lastSuccessfulSha ?? 'unknown'}\``,
            `Failed main SHA: \`${run.mainContext?.failedSha ?? 'unknown'}\``,
            `Triggering merge PR (context only, not necessarily causal): ${run.mainContext?.triggeringMerge ?? 'Not found'}`);
    } else if (cause.test_name) {
        lines.push(`Build error leg or test failing: ${jobs} / \`${cause.test_name}\``);
    } else {
        lines.push(`Build error leg: ${jobs}`);
    }

    if (run.runScope === 'pull-request' && run.prNumber > 0) {
        lines.push(`Pull request: #${run.prNumber}`);
    }

    lines.push(
        '',
        '## Error Message',
        '',
        '```',
        cause.error_pattern,
        '```',
        '',
        '## Description',
        '',
        cause.title,
        '',
        `**Type**: ${cause.type}`,
        '',
        '## Occurrences',
        '',
        '| Date | Build | Job | Context |',
        '|------|-------|-----|----|',
        occurrenceRow(cause, run),
        '');
    return lines.join('\n');
}

async function readJson(filePath) {
    return JSON.parse(await fs.readFile(filePath, 'utf8'));
}

async function persistIssueUrl(filePath, fallbackCause, issueUrl) {
    let storedCause = fallbackCause;
    try {
        storedCause = await readJson(filePath);
    } catch (error) {
        if (error.code !== 'ENOENT') {
            throw error;
        }
    }

    const temporaryPath = `${filePath}.tmp`;
    await fs.mkdir(path.dirname(filePath), { recursive: true });
    await fs.writeFile(
        temporaryPath,
        `${JSON.stringify({ ...storedCause, issue_url: issueUrl }, null, 2)}\n`);
    await fs.rename(temporaryPath, filePath);
}

async function ensureCauseLabels(github, context, cause) {
    if (cause.type !== 'main-repository-breakage') {
        return;
    }

    await tracking.ensureLabel(github, context.repo.owner, context.repo.repo, {
        name: 'main-ci-break',
        color: 'b60205',
        description: 'Deterministic repository breakage on the main branch',
    });
}

async function publishCauseIssue(github, context, core, cause, run, memoryCausesDirectory) {
    await ensureCauseLabels(github, context, cause);
    const marker = causeMarker(cause);
    const alternateMarkers = (cause.aliases ?? [])
        .filter(causeId => LEGACY_CAUSE_ID_PATTERN.test(causeId))
        .map(causeMarker);
    const transport = tracking.createOctokitIssueTransport(github, context);
    const result = await tracking.executeIssueReconciliation(transport, core, {
        label: CAUSE_LABEL,
        labels: labelsForCause(cause),
        marker,
        alternateMarkers,
        title: buildIssueTitle(cause),
        buildBody: () => buildIssueBody(cause, run),
        closeDuplicates: true,
        reopen: 'when-changing',
        isMatchingIssue: issue => matchesCauseIssue(issue, cause),
        isCanonicalIssue: issue => normalizedBodyLines(issue.body)[0] === marker,
        actionsForCanonical: (issue, { created }) => {
            if (created || hasOccurrence(issue.body, run.runId)) {
                return [];
            }
            return [{
                type: 'update',
                body: `${(issue.body ?? '').trimEnd()}\n${occurrenceRow(cause, run)}\n`,
            }];
        },
    });

    const issueUrl = `https://github.com/${context.repo.owner}/${context.repo.repo}/issues/${result.number}`;
    await persistIssueUrl(
        path.join(memoryCausesDirectory, `${cause.id}.json`),
        cause,
        issueUrl);
    return {
        number: result.number,
        created: result.created,
        skipped: !result.created && !result.appliedActions.some(action => action.type === 'update'),
        duplicatesClosed: result.duplicatesClosed,
    };
}

async function publishCauseIssues(
    github,
    context,
    core,
    {
        causesDirectory,
        memoryCausesDirectory,
        runId,
        runUrl,
        runScope,
        prNumber,
        analyzedAt,
        mainContext,
    }) {
    let entries;
    try {
        entries = await fs.readdir(causesDirectory, { withFileTypes: true });
    } catch (error) {
        if (error.code === 'ENOENT') {
            return [];
        }
        throw error;
    }

    const run = {
        runId,
        runUrl,
        runScope,
        prNumber,
        analyzedAt,
        mainContext,
    };
    const results = [];
    for (const entry of entries) {
        if (!entry.isFile() || path.extname(entry.name) !== '.json') {
            continue;
        }

        const causePath = path.join(causesDirectory, entry.name);
        let cause;
        try {
            cause = await readJson(causePath);
        } catch (error) {
            core.warning(`Invalid JSON in cause file '${entry.name}': ${error.message}`);
            continue;
        }
        if (!CAUSE_ID_PATTERN.test(cause.id ?? '')) {
            core.warning(`Invalid cause ID '${cause.id}', skipping`);
            continue;
        }

        results.push(await publishCauseIssue(
            github,
            context,
            core,
            cause,
            run,
            memoryCausesDirectory));
    }
    return results;
}

module.exports = {
    matchesCauseIssue,
    buildIssueTitle,
    buildIssueBody,
    occurrenceRow,
    publishCauseIssues,
};
