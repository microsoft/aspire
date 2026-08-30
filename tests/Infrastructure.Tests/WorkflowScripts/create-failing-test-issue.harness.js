const fs = require('node:fs/promises');
const helper = require('../../../.github/workflows/create-failing-test-issue.js');
const tracking = require('../../../.github/workflows/create-failing-test-issue-tracking.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    const result = await dispatch(request.operation, request.payload ?? {});
    process.stdout.write(JSON.stringify({ result }));
}

async function dispatch(operation, payload) {
    switch (operation) {
        case 'parseCommand':
            return helper.parseCommand(payload.body, payload.defaultSourceUrl ?? null);



        case 'formatListResponse':
            return helper.formatListResponse(payload.resolverOutcome, payload.resultJson ?? null);

        case 'reconcile': {
            if (typeof tracking.reconcile !== 'function') {
                return { available: false };
            }

            const issues = (payload.issues ?? []).map(issue => ({
                comments: [],
                state: 'open',
                ...issue,
            }));
            let nextNumber = payload.nextNumber ?? 1000;
            const calls = [];
            const transport = {
                ensureLabel: async () => { calls.push('ensureLabel'); },
                listIssues: async () => {
                    calls.push('listIssues');
                    return issues;
                },
                createIssue: async request => {
                    calls.push('createIssue');
                    const issue = {
                        number: nextNumber++,
                        state: 'open',
                        comments: [],
                        ...request,
                    };
                    issues.push(issue);
                    return issue;
                },
                updateIssue: async (issueNumber, patch) => {
                    calls.push('updateIssue');
                    Object.assign(issues.find(issue => issue.number === issueNumber), patch);
                },
                addComment: async (issueNumber, body) => {
                    calls.push('addComment');
                    issues.find(issue => issue.number === issueNumber).comments.push(body);
                },
                closeIssue: async (issueNumber, { stateReason }) => {
                    calls.push('closeIssue');
                    Object.assign(
                        issues.find(issue => issue.number === issueNumber),
                        { state: 'closed', stateReason });
                },
                reopenIssue: async issueNumber => {
                    calls.push('reopenIssue');
                    issues.find(issue => issue.number === issueNumber).state = 'open';
                },
                listComments: async issueNumber => {
                    calls.push('listComments');
                    return issues
                        .find(issue => issue.number === issueNumber).comments
                        .map(body => ({ body }));
                },
            };
            const result = await tracking.reconcile({
                transport,
                core: { info: () => {} },
                issue: {
                    title: payload.title ?? 'Failing test',
                    body: payload.body,
                    labels: payload.labels ?? ['failing-test'],
                    metadataMarker: payload.marker,
                    commentBody: payload.comment ?? 'Failure occurrence',
                },
                runId: payload.runId ?? 123,
                forceNew: payload.forceNew === true,
            });
            return {
                available: true,
                result,
                issues,
                calls,
            };
        }

        case 'ghTransport': {
            let transportFactory;
            try {
                transportFactory = require('../../../.github/workflows/tracking-issue-gh-api.js');
            } catch (error) {
                if (error.code === 'MODULE_NOT_FOUND') {
                    return { available: false };
                }
                throw error;
            }
            if (typeof transportFactory.createGhApiIssueTransport !== 'function') {
                return { available: false };
            }

            const calls = [];
            const runGh = async (args, input) => {
                calls.push({ args, input: input ?? null });
                if (payload.labelAlreadyExists && args.includes('repos/microsoft/aspire/labels')) {
                    throw new Error('gh api failed: Validation Failed (HTTP 422)');
                }
                if (args.includes('--paginate')) {
                    return '[]';
                }
                if (args.includes('POST') && args.includes('repos/microsoft/aspire/issues')) {
                    return '{"number":1000,"html_url":"https://github.com/microsoft/aspire/issues/1000"}';
                }
                return '{}';
            };
            const transport = transportFactory.createGhApiIssueTransport(
                'microsoft/aspire',
                { runGh });
            if (payload.operation === 'list') {
                await transport.listIssues('failing-test');
            } else if (payload.operation === 'ensure') {
                await transport.ensureLabel({
                    name: 'failing-test',
                    color: 'b60205',
                    description: 'A test is failing in CI',
                });
            } else {
                await transport.ensureLabel({
                    name: 'failing-test',
                    color: 'b60205',
                    description: 'A test is failing in CI',
                });
                await transport.createIssue({ title: 'title', body: 'body', labels: ['failing-test'] });
                await transport.updateIssue(1000, { body: 'updated' });
                await transport.addComment(1000, 'comment');
                await transport.closeIssue(1000, { stateReason: 'not_planned' });
                await transport.reopenIssue(1000);
                await transport.listComments(1000);
            }
            return { available: true, calls };
        }

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
