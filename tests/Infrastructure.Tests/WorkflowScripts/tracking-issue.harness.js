const fs = require('node:fs/promises');
const engine = require('../../../.github/workflows/tracking-issue.js');

async function main() {
    const inputPath = process.argv[2];
    if (!inputPath) {
        throw new Error('Expected the input payload file path as the first argument.');
    }

    const request = JSON.parse(await fs.readFile(inputPath, 'utf8'));
    const result = await dispatch(request.operation, request.payload ?? {});
    process.stdout.write(JSON.stringify({ result }));
}

// In-memory octokit fake mirroring the surface recordRun touches, so the
// find-or-create + comment-dedup contract can be exercised without a network.
// Comments are stored as plain strings; listComments adapts them to { body }.
function makeGithub(store) {
    const calls = [];
    return {
        calls,
        paginate: async (fn, params) => (await fn(params)).data,
        rest: {
            issues: {
                listForRepo: async ({ labels, state }) => {
                    // Production narrows by the lookup label before the marker filter
                    // (tracking-issue.js listOpenIssuesByLabel). Fail loudly if that
                    // narrowing is ever dropped so the regression is caught here
                    // rather than masked by the fake returning every open issue.
                    if (!labels) {
                        throw new Error('listForRepo must be called with a label filter');
                    }
                    const requestedState = state ?? 'open';
                    return {
                        data: store.issues.filter(issue => requestedState === 'all' || issue.state === requestedState),
                    };
                },
                create: async ({ title, body, labels }) => {
                    calls.push('create');
                    const issue = { number: store.next++, title, body, labels, state: 'open', comments: [] };
                    store.issues.push(issue);
                    return { data: issue };
                },
                update: async ({ issue_number, state }) => {
                    calls.push('update');
                    const issue = store.issues.find(i => i.number === issue_number);
                    if (state) { issue.state = state; }
                },
                listComments: async ({ issue_number }) => {
                    const issue = store.issues.find(i => i.number === issue_number);
                    return { data: (issue?.comments ?? []).map(body => ({ body })) };
                },
                createComment: async ({ issue_number, body }) => {
                    calls.push('createComment');
                    const issue = store.issues.find(i => i.number === issue_number);
                    (issue.comments ??= []).push(body);
                },
            },
        },
    };
}

async function dispatch(operation, payload) {
    switch (operation) {
        case 'findOpenIssueForMarker': {
            const issue = engine.findOpenIssueForMarker(payload.issues ?? [], payload.marker);
            return { number: issue ? issue.number : null };
        }

        case 'runMarker':
            return engine.runMarker(payload.runId);

        case 'buildBody':
            return engine.buildBody({
                marker: payload.marker,
                lead: payload.lead ?? 'lead',
                note: payload.note ?? ['note'],
                autoClose: payload.autoClose,
            });

        case 'readAutoClose':
            return { value: engine.readAutoClose(payload.body) };

        case 'recordRun': {
            const store = {
                issues: (payload.issues ?? []).map(issue => ({ comments: [], state: 'open', ...issue })),
                next: payload.nextNumber ?? 1000,
            };
            const github = makeGithub(store);
            const core = { info: () => {}, warning: () => {} };
            const context = { repo: { owner: 'microsoft', repo: 'aspire' } };

            const result = await engine.recordRun(github, context, core, {
                label: payload.label ?? 'automation-broken',
                labels: payload.labels,
                marker: payload.marker,
                title: payload.title ?? 'Tracking issue',
                runId: payload.runId,
                buildBody: () => payload.body ?? `${payload.marker}\n\nbody`,
                comment: payload.comment ?? 'failure',
            });

            return {
                result,
                calls: github.calls,
                issues: store.issues.map(issue => ({
                    number: issue.number,
                    state: issue.state,
                    body: issue.body,
                    labels: issue.labels ?? [],
                    comments: issue.comments ?? [],
                })),
            };
        }

        default:
            throw new Error(`Unsupported operation '${operation}'.`);
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
