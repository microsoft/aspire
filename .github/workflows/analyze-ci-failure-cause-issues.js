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
const MAX_ISSUE_BODY_BYTES = 65_000;
const OCCURRENCES_START = '<!-- ci-failure-occurrences:start -->';
const OCCURRENCES_END = '<!-- ci-failure-occurrences:end -->';
const OCCURRENCE_ROW_PATTERN = /^\| \d{4}-\d{2}-\d{2} \| \[\d+\]\(https:\/\/github\.com\/[^\n]+\) \| .* \| (main|unavailable|#\d+) \|$/;

class OccurrenceRenderError extends Error {}

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

function renderCodeSpan(value) {
    const text = String(value);
    const longestDelimiter = Math.max(
        0,
        ...Array.from(text.matchAll(/`+/g), match => match[0].length));
    const delimiter = '`'.repeat(longestDelimiter + 1);
    return `${delimiter} ${text} ${delimiter}`;
}

function renderIndentedBlock(value) {
    const text = String(value || 'No diagnostic pattern recorded.');
    return text.split('\n').map(line => `    ${line}`);
}

function sanitizeSingleLine(value, maxLength) {
    return String(value ?? '')
        .replace(/\x1B\[[0-9;?]*[ -/]*[@-~]/g, '')
        .replace(/[\r\n\t]+/g, ' ')
        .replace(/[\p{Cf}\u2028\u2029\uFE00-\uFE0F]/gu, '')
        .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/g, '')
        .slice(0, maxLength);
}

function renderJobNames(cause, separator, escapeForTable = false) {
    return (cause.job_names ?? ['unknown'])
        .map(name => renderCodeSpan(escapeForTable ? escapeTableCell(name) : name))
        .join(separator);
}

function formatTriggeringMerge(mainContext) {
    const triggeringMerge = mainContext?.triggeringMerge;
    if (mainContext?.candidateHistoryState !== 'available' ||
        !Number.isInteger(triggeringMerge?.number) ||
        triggeringMerge.number <= 0 ||
        typeof triggeringMerge.title !== 'string') {
        return undefined;
    }

    return `#${triggeringMerge.number} ${renderCodeSpan(sanitizeSingleLine(triggeringMerge.title, 238))}`;
}

function occurrenceRow(cause, run) {
    const date = run.analyzedAt.split('T')[0];
    const jobs = renderJobNames(cause, '<br>', true);
    const occurrenceContext = run.runScope === 'main'
        ? 'main'
        : run.prNumber > 0 ? `#${run.prNumber}` : 'unavailable';
    return `| ${date} | [${run.runId}](${run.runUrl}) | ${jobs} | ${occurrenceContext} |`;
}

function hasOccurrence(body, runId) {
    return (body ?? '').includes(`[${runId}](`);
}

function occurrenceSection(rows, totalOccurrenceCount) {
    return [
        OCCURRENCES_START,
        '## Occurrences',
        '',
        `Showing ${rows.length} most recent of ${totalOccurrenceCount} occurrences.`,
        '',
        '| Date | Build | Job | Context |',
        '|------|-------|-----|----|',
        ...rows,
        OCCURRENCES_END,
        '',
    ].join('\n');
}

function parseOccurrenceSection(body) {
    const normalizedBody = (body ?? '').replaceAll('\r\n', '\n');
    let prefix;
    let managed;
    let legacy = false;

    if (normalizedBody.includes(OCCURRENCES_START) || normalizedBody.includes(OCCURRENCES_END)) {
        const startParts = normalizedBody.split(OCCURRENCES_START);
        if (startParts.length !== 2) {
            throw new OccurrenceRenderError('ambiguous managed occurrence section');
        }
        const endParts = startParts[1].split(OCCURRENCES_END);
        if (endParts.length !== 2 || !/^\s*$/.test(endParts[1])) {
            throw new OccurrenceRenderError('ambiguous managed occurrence section');
        }
        [prefix] = startParts;
        [managed] = endParts;
    } else {
        const legacyParts = normalizedBody.split('\n## Occurrences\n');
        if (legacyParts.length !== 2) {
            throw new OccurrenceRenderError('unsupported legacy occurrence section');
        }
        [prefix] = legacyParts;
        managed = `## Occurrences\n${legacyParts[1]}`;
        legacy = true;
    }

    const lines = managed.split('\n');
    const isAllowedLine = line =>
        line.length === 0 ||
        line === '## Occurrences' ||
        line === '| Date | Build | Job | Context |' ||
        (legacy && line === '| Date | Build | Job | PR |') ||
        line === '|------|-------|-----|----|' ||
        /^Showing \d+ most recent of \d+ occurrences\.$/.test(line) ||
        OCCURRENCE_ROW_PATTERN.test(line);
    if (!lines.every(isAllowedLine)) {
        throw new OccurrenceRenderError('unsupported occurrence section contents');
    }

    const totalLines = lines
        .map(line => /^Showing \d+ most recent of (\d+) occurrences\.$/.exec(line))
        .filter(match => match !== null);
    return {
        prefix: prefix.replace(/\n+$/, ''),
        rows: lines.filter(line => OCCURRENCE_ROW_PATTERN.test(line)),
        totalOccurrenceCount: totalLines.length === 1
            ? Number.parseInt(totalLines[0][1], 10)
            : undefined,
    };
}

function renderOccurrenceHistory(body, newRow, totalOccurrenceCount) {
    if (!OCCURRENCE_ROW_PATTERN.test(newRow)) {
        throw new OccurrenceRenderError('invalid occurrence row');
    }

    const parsed = parseOccurrenceSection(body);
    const rows = [...parsed.rows, newRow];
    const total = totalOccurrenceCount ?? rows.length;
    if (!Number.isInteger(total) || total < rows.length) {
        throw new OccurrenceRenderError('occurrence total is smaller than the rendered history');
    }

    while (rows.length > 1) {
        const rendered = `${parsed.prefix}\n\n${occurrenceSection(rows, total)}`;
        if (isWithinIssueBodyBudget(rendered)) {
            return rendered;
        }
        rows.shift();
    }

    const rendered = `${parsed.prefix}\n\n${occurrenceSection(rows, total)}`;
    if (!isWithinIssueBodyBudget(rendered)) {
        throw new OccurrenceRenderError('occurrence section cannot fit within the publication budget');
    }
    return rendered;
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

function buildIssueBody(cause, run, totalOccurrenceCount = 1) {
    const jobs = renderJobNames(cause, '<br>');
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
            `Failed main SHA: \`${run.mainContext?.failedSha ?? 'unknown'}\``);
        const triggeringMerge = formatTriggeringMerge(run.mainContext);
        if (triggeringMerge) {
            lines.push(
                `Triggering merge PR (context only, not necessarily causal): ${triggeringMerge}`);
        }
    } else if (cause.test_name) {
        lines.push(`Build error leg or test failing: ${jobs} / ${renderCodeSpan(cause.test_name)}`);
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
        ...renderIndentedBlock(cause.error_pattern),
        '',
        '## Description',
        '',
        renderCodeSpan(cause.title),
        '',
        `**Type**: ${cause.type}`,
        '',
        occurrenceSection([occurrenceRow(cause, run)], totalOccurrenceCount));
    return lines.join('\n');
}

function isWithinIssueBodyBudget(body) {
    return Buffer.byteLength(body, 'utf8') <= MAX_ISSUE_BODY_BYTES;
}

async function readJson(filePath) {
    return JSON.parse(await fs.readFile(filePath, 'utf8'));
}

async function readStoredCause(filePath, fallbackCause) {
    try {
        return await readJson(filePath);
    } catch (error) {
        if (error.code === 'ENOENT') {
            return fallbackCause;
        }
        throw error;
    }
}

async function readStoredCauseFamily(memoryCausesDirectory, cause) {
    const storedCausePath = path.join(memoryCausesDirectory, `${cause.id}.json`);
    const storedCause = await readStoredCause(storedCausePath, cause);
    const storedRecordsById = new Map([[cause.id, storedCause]]);
    const pendingIds = [...new Set(cause.aliases ?? [])];
    while (pendingIds.length > 0) {
        const alias = pendingIds.shift();
        if (!LEGACY_CAUSE_ID_PATTERN.test(alias) || storedRecordsById.has(alias)) {
            continue;
        }

        try {
            const storedAlias = await readJson(
                path.join(memoryCausesDirectory, `${alias}.json`));
            if (storedAlias.id !== alias || storedAlias.type !== cause.type) {
                continue;
            }

            storedRecordsById.set(alias, storedAlias);
            if (LEGACY_CAUSE_ID_PATTERN.test(storedAlias.canonical_id ?? '')) {
                pendingIds.push(storedAlias.canonical_id);
            }
        } catch (error) {
            if (error.code !== 'ENOENT') {
                throw error;
            }
        }
    }

    const resolvesToCanonical = record => {
        const visited = new Set([record.id]);
        let canonicalId = record.canonical_id;
        while (typeof canonicalId === 'string' && !visited.has(canonicalId)) {
            if (canonicalId === cause.id) {
                return true;
            }
            visited.add(canonicalId);
            canonicalId = storedRecordsById.get(canonicalId)?.canonical_id;
        }
        return false;
    };
    const storedRecords = [
        storedCause,
        ...[...storedRecordsById.values()]
            .filter(record => record.id !== cause.id && resolvesToCanonical(record)),
    ];
    const occurrencesByRunId = new Map();
    for (const record of storedRecords) {
        for (const occurrence of record.occurrences ?? []) {
            const existing = occurrencesByRunId.get(occurrence.run_id);
            if (!existing) {
                occurrencesByRunId.set(occurrence.run_id, occurrence);
            } else if (occurrence.issue_published === true && existing.issue_published !== true) {
                occurrencesByRunId.set(occurrence.run_id, {
                    ...existing,
                    issue_published: true,
                });
            }
        }
    }

    return {
        storedCausePath,
        storedCause,
        storedOccurrences: [...occurrencesByRunId.values()],
    };
}

function storedOccurrenceCount(storedOccurrences) {
    return storedOccurrences.length > 0
        ? storedOccurrences.length
        : undefined;
}

function isOccurrencePublished(body, storedOccurrences, runId) {
    if (hasOccurrence(body, runId)) {
        return true;
    }

    const storedOccurrence = storedOccurrences.find(occurrence => occurrence.run_id === runId);
    if (!storedOccurrence) {
        return false;
    }
    if (storedOccurrence.issue_published === true) {
        return true;
    }

    try {
        const parsed = parseOccurrenceSection(body);
        const storedRunIds = new Set(storedOccurrences.map(occurrence => occurrence.run_id));
        const renderedRunIds = parsed.rows.map(row => Number.parseInt(/\[(\d+)\]\(/.exec(row)[1], 10));

        // Older records predate the durable publication receipt. A managed body whose
        // total already accounts for the complete stored history proves that a trimmed
        // run was published without confusing a memory-only write with publication.
        return parsed.totalOccurrenceCount === storedRunIds.size &&
            renderedRunIds.every(renderedRunId => storedRunIds.has(renderedRunId));
    } catch (error) {
        if (error instanceof OccurrenceRenderError) {
            return false;
        }
        throw error;
    }
}

async function persistIssuePublication(
    filePath,
    fallbackCause,
    issueUrl,
    publishedRunId) {
    let storedCause = fallbackCause;
    try {
        storedCause = await readJson(filePath);
    } catch (error) {
        if (error.code !== 'ENOENT') {
            throw error;
        }
    }

    const persistedCause = { ...storedCause, issue_url: issueUrl };
    if (publishedRunId !== undefined && Array.isArray(storedCause.occurrences)) {
        persistedCause.occurrences = storedCause.occurrences.map(occurrence =>
            occurrence.run_id === publishedRunId
                ? { ...occurrence, issue_published: true }
                : occurrence);
    }

    const temporaryPath = `${filePath}.tmp`;
    await fs.mkdir(path.dirname(filePath), { recursive: true });
    await fs.writeFile(
        temporaryPath,
        `${JSON.stringify(persistedCause, null, 2)}\n`);
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
    const {
        storedCausePath,
        storedCause,
        storedOccurrences,
    } = await readStoredCauseFamily(memoryCausesDirectory, cause);
    const totalOccurrenceCount = storedOccurrenceCount(storedOccurrences);
    const initialBody = buildIssueBody(cause, run, totalOccurrenceCount ?? 1);
    const marker = causeMarker(cause);
    const alternateMarkers = (cause.aliases ?? [])
        .filter(causeId => LEGACY_CAUSE_ID_PATTERN.test(causeId))
        .map(causeMarker);
    const transport = tracking.createOctokitIssueTransport(github, context);
    const issues = await transport.listIssues(CAUSE_LABEL);
    const matchingIssues = tracking.findIssuesForMarkers(
        issues,
        [marker, ...alternateMarkers],
        issue => matchesCauseIssue(issue, cause))
        .filter(issue => !tracking.isDuplicateExempt(issue));
    if (matchingIssues.length === 0 && !isWithinIssueBodyBudget(initialBody)) {
        core.warning(
            `Cause issue body exceeds the ${MAX_ISSUE_BODY_BYTES}-byte publication budget. Skipping issue creation.`);
        return undefined;
    }

    await ensureCauseLabels(github, context, cause);
    let occurrenceAlreadyPublished = false;
    const result = await tracking.executeIssueReconciliation(transport, core, {
        issues,
        label: CAUSE_LABEL,
        labels: labelsForCause(cause),
        marker,
        alternateMarkers,
        title: buildIssueTitle(cause),
        buildBody: () => initialBody,
        closeDuplicates: true,
        reopen: 'when-changing',
        isMatchingIssue: issue => matchesCauseIssue(issue, cause),
        isCanonicalIssue: issue => normalizedBodyLines(issue.body)[0] === marker,
        actionsForCanonical: (issue, { created }) => {
            if (created || isOccurrencePublished(issue.body, storedOccurrences, run.runId)) {
                occurrenceAlreadyPublished = true;
                return [];
            }
            let updatedBody;
            try {
                updatedBody = renderOccurrenceHistory(
                    issue.body,
                    occurrenceRow(cause, run),
                    totalOccurrenceCount);
            } catch (error) {
                if (!(error instanceof OccurrenceRenderError)) {
                    throw error;
                }
                core.warning(
                    `Issue #${issue.number} has an unsupported occurrence section: ${error.message}. Skipping occurrence update.`);
                return issue.state === 'closed' ? [{ type: 'reopen' }] : [];
            }
            return [{
                type: 'update',
                body: updatedBody,
            }];
        },
    });

    const issueUrl = `https://github.com/${context.repo.owner}/${context.repo.repo}/issues/${result.number}`;
    const occurrencePublished =
        occurrenceAlreadyPublished ||
        result.created ||
        result.appliedActions.some(action =>
            action.type === 'update' && action.issueNumber === result.number);
    await persistIssuePublication(
        storedCausePath,
        cause,
        issueUrl,
        occurrencePublished ? run.runId : undefined);
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

        const result = await publishCauseIssue(
            github,
            context,
            core,
            cause,
            run,
            memoryCausesDirectory);
        if (result) {
            results.push(result);
        }
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
