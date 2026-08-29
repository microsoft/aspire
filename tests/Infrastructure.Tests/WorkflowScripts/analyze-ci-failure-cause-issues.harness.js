'use strict';

const fs = require('node:fs/promises');
const path = require('node:path');
const publisher = require('../../../.github/workflows/analyze-ci-failure-cause-issues.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    const result = await dispatch(request.operation, request.payload ?? {});
    process.stdout.write(JSON.stringify({ result }));
}

function makeGithub(store) {
    const calls = [];
    return {
        calls,
        paginate: async (fn, params) => (await fn(params)).data,
        rest: {
            issues: {
                listForRepo: async ({ labels, state }) => {
                    if (labels !== 'ci-failure-cause' || state !== 'all') {
                        throw new Error('Cause lookup must list all ci-failure-cause issues.');
                    }
                    return { data: store.issues };
                },
                create: async ({ title, body, labels }) => {
                    calls.push('create');
                    const issue = {
                        number: store.next++,
                        state: 'open',
                        title,
                        body,
                        labels,
                        comments: [],
                    };
                    store.issues.push(issue);
                    return { data: issue };
                },
                update: async ({ issue_number, state, state_reason, body }) => {
                    calls.push('update');
                    const issue = store.issues.find(candidate => candidate.number === issue_number);
                    if (!issue) {
                        throw new Error(`Issue #${issue_number} does not exist.`);
                    }
                    if (state !== undefined) { issue.state = state; }
                    if (state_reason !== undefined) { issue.stateReason = state_reason; }
                    if (body !== undefined) { issue.body = body; }
                    return { data: issue };
                },
                createComment: async ({ issue_number, body }) => {
                    calls.push('createComment');
                    const issue = store.issues.find(candidate => candidate.number === issue_number);
                    (issue.comments ??= []).push(body);
                },
                listComments: async ({ issue_number }) => {
                    const issue = store.issues.find(candidate => candidate.number === issue_number);
                    return { data: (issue?.comments ?? []).map(body => ({ body })) };
                },
                createLabel: async () => {
                    calls.push('createLabel');
                    return { data: {} };
                },
            },
        },
    };
}

async function dispatch(operation, payload) {
    if (operation === 'matchesCauseIssue') {
        return publisher.matchesCauseIssue(payload.issue, payload.cause);
    }
    if (operation !== 'publishCauseIssues') {
        throw new Error(`Unsupported operation '${operation}'.`);
    }

    const causesDirectory = path.join(payload.workspace, 'agent-causes');
    const memoryCausesDirectory = path.join(payload.workspace, 'memory-causes');
    await fs.rm(causesDirectory, { recursive: true, force: true });
    await fs.rm(memoryCausesDirectory, { recursive: true, force: true });
    await fs.mkdir(causesDirectory, { recursive: true });
    await fs.mkdir(memoryCausesDirectory, { recursive: true });
    await fs.writeFile(
        path.join(causesDirectory, `${payload.cause.id}.json`),
        `${JSON.stringify(payload.cause)}\n`);
    if (payload.storedCause) {
        await fs.writeFile(
            path.join(memoryCausesDirectory, `${payload.cause.id}.json`),
            `${JSON.stringify(payload.storedCause)}\n`);
    }

    const store = {
        next: 1000,
        issues: (payload.issues ?? []).map(issue => ({
            title: 'Existing issue',
            labels: ['ci-failure-cause'],
            comments: [],
            ...issue,
        })),
    };
    const github = makeGithub(store);
    const warnings = [];
    const core = {
        info: () => {},
        warning: message => warnings.push(message),
    };
    const context = { repo: { owner: 'microsoft', repo: 'aspire' } };
    const options = {
        causesDirectory,
        memoryCausesDirectory,
        runId: 991,
        runUrl: 'https://github.com/microsoft/aspire/actions/runs/991',
        runScope: payload.runScope ?? 'pull-request',
        prNumber: payload.prNumber ?? (payload.runScope === 'main' ? 0 : 19804),
        analyzedAt: '2026-08-29T18:30:00Z',
        mainContext: payload.mainContext,
    };

    let publish;
    const repeat = payload.repeat ?? 1;
    for (let index = 0; index < repeat; index++) {
        const [currentPublish] = await publisher.publishCauseIssues(github, context, core, options);
        publish ??= currentPublish;
    }

    const storedCause = JSON.parse(
        await fs.readFile(path.join(memoryCausesDirectory, `${payload.cause.id}.json`), 'utf8'));
    return {
        publish,
        calls: github.calls,
        warnings,
        issues: store.issues.map(issue => ({
            number: issue.number,
            state: issue.state,
            stateReason: issue.stateReason,
            title: issue.title,
            body: issue.body,
            labels: issue.labels,
        })),
        storedCause,
    };
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
