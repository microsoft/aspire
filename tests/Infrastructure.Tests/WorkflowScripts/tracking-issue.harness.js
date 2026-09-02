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

// Issue list/create responses expose GitHub's integer comment count. Comment bodies
// are available only through listComments, matching the REST API boundary.
function toRestIssue(issue) {
    return {
        ...issue,
        comments: issue.commentBodies.length,
        commentBodies: undefined,
    };
}

function makeGithub(store, concurrentIssue) {
    const calls = [];
    let listCount = 0;
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
                    listCount++;
                    if (listCount === 2 && concurrentIssue) {
                        store.issues.push({
                            commentBodies: concurrentIssue.comments ?? [],
                            state: 'open',
                            ...concurrentIssue,
                            comments: undefined,
                        });
                    }
                    const requestedState = state ?? 'open';
                    return {
                        data: store.issues
                            .filter(issue => requestedState === 'all' || issue.state === requestedState)
                            .map(toRestIssue),
                    };
                },
                create: async ({ title, body, labels }) => {
                    calls.push('create');
                    const issue = {
                        number: store.next++,
                        title,
                        body,
                        labels,
                        state: 'open',
                        commentBodies: [],
                    };
                    store.issues.push(issue);
                    return { data: toRestIssue(issue) };
                },
                update: async ({ issue_number, state, state_reason, body }) => {
                    calls.push('update');
                    const issue = store.issues.find(i => i.number === issue_number);
                    if (state) { issue.state = state; }
                    if (state_reason) { issue.stateReason = state_reason; }
                    if (body !== undefined) { issue.body = body; }
                    return { data: issue };
                },
                listComments: async ({ issue_number }) => {
                    const issue = store.issues.find(i => i.number === issue_number);
                    return { data: (issue?.commentBodies ?? []).map(body => ({ body })) };
                },
                createComment: async ({ issue_number, body }) => {
                    calls.push('createComment');
                    const issue = store.issues.find(i => i.number === issue_number);
                    issue.commentBodies.push(body);
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

        case 'planAndExecute': {
            const store = {
                issues: (payload.issues ?? []).map(issue => ({
                    commentBodies: issue.comments ?? [],
                    state: 'open',
                    ...issue,
                    comments: undefined,
                })),
                next: payload.nextNumber ?? 1000,
            };
            const github = makeGithub(store);
            const core = { info: () => {}, warning: () => {} };
            const context = { repo: { owner: 'microsoft', repo: 'aspire' } };
            const transport = engine.createOctokitIssueTransport(github, context);
            const options = {
                label: payload.label ?? 'automation-broken',
                marker: payload.marker,
                title: payload.title ?? 'Tracking issue',
                buildBody: () => payload.body ?? `${payload.marker}\n\nbody`,
                closeDuplicates: payload.closeDuplicates,
                reopen: 'when-changing',
                actionsForCanonical: issue => {
                    const actions = [];
                    if (payload.updateBody !== undefined && issue.body !== payload.updateBody) {
                        actions.push({ type: 'update', body: payload.updateBody });
                    }
                    if (payload.comment !== undefined) {
                        actions.push({ type: 'comment', body: payload.comment });
                    }
                    return actions;
                },
            };
            const plan = engine.planIssueReconciliation({
                ...options,
                issues: store.issues.map(toRestIssue),
            });
            const execution = await engine.executeIssueReconciliation(transport, core, {
                ...options,
            });
            return {
                plan,
                appliedActions: execution.appliedActions,
            };
        }

        case 'recordRun': {
            const store = {
                issues: (payload.issues ?? []).map(issue => ({
                    commentBodies: issue.comments ?? [],
                    state: 'open',
                    ...issue,
                    comments: undefined,
                })),
                next: payload.nextNumber ?? 1000,
            };
            const github = makeGithub(store, payload.concurrentIssue);
            const core = { info: () => {}, warning: () => {} };
            const context = { repo: { owner: 'microsoft', repo: 'aspire' } };
            const isMatchingIssue = payload.requiredSecondLine
                ? issue => issue.body?.replaceAll('\r\n', '\n').split('\n')[1] === payload.requiredSecondLine
                : undefined;

            const result = await engine.recordRun(github, context, core, {
                label: payload.label ?? 'automation-broken',
                labels: payload.labels,
                marker: payload.marker,
                title: payload.title ?? 'Tracking issue',
                runId: payload.runId,
                buildBody: () => payload.body ?? `${payload.marker}\n\nbody`,
                comment: payload.comment ?? 'failure',
                closeDuplicates: payload.closeDuplicates,
                isMatchingIssue,
            });

            return {
                result,
                calls: github.calls,
                issues: store.issues.map(issue => ({
                    number: issue.number,
                    state: issue.state,
                    stateReason: issue.stateReason,
                    body: issue.body,
                    labels: issue.labels ?? [],
                    comments: issue.commentBodies,
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
