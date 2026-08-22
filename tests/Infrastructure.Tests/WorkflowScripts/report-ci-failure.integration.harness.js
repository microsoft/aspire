// Integration harness for the red-main reporter's reportFailure() and
// resolveSuccess() orchestrators (.github/workflows/report-ci-failure.js). These
// own the find-or-create + comment-dedup branching (reportFailure) and the
// find-and-close branching (resolveSuccess) that the pure-helper tests cannot
// reach, so this harness drives them against an in-memory fake of the octokit
// surface they use and reports the resulting state.
//
// Input payload (JSON, first argv):
//   {
//     "operation": "reportFailure" | "resolveSuccess" | "track",
//     "env": { "REF": "main", "CI_RED": "true", "CI_GREEN": "false" },
//     "issues": [ { "number": 1, "body": "...", "state": "open" } ],
//     "failComment": false,            // make createComment throw (transient failure)
//     "failUpdate": false,             // make issues.update throw (transient failure)
//     "runId": 12345, "runNumber": 7, "sha": "abcdef..."
//   }
// Output: { threw, calls, issues: [ { number, title, state, state_reason, body, labels, comments } ] }

'use strict';

const fs = require('node:fs');

const reporter = require('../../../.github/workflows/report-ci-failure.js');

function makeGithub(store, { failComment, failUpdate }) {
    const calls = [];
    return {
        calls,
        paginate: async (fn, params) => (await fn(params)).data,
        rest: {
            issues: {
                createLabel: async () => { calls.push('createLabel'); },
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
                update: async ({ issue_number, body, state, state_reason }) => {
                    calls.push('update');
                    if (failUpdate) {
                        throw new Error('transient update failure');
                    }
                    const issue = store.issues.find(i => i.number === issue_number);
                    if (body !== undefined) { issue.body = body; }
                    if (state) { issue.state = state; }
                    if (state_reason) { issue.state_reason = state_reason; }
                },
                listComments: async ({ issue_number }) => {
                    const issue = store.issues.find(i => i.number === issue_number);
                    return { data: (issue?.comments ?? []).map(body => ({ body })) };
                },
                createComment: async ({ issue_number, body }) => {
                    calls.push('createComment');
                    if (failComment) {
                        throw new Error('transient comment failure');
                    }
                    const issue = store.issues.find(i => i.number === issue_number);
                    (issue.comments ??= []).push(body);
                },
            },
        },
    };
}

async function main() {
    const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));

    for (const [key, value] of Object.entries(input.env ?? {})) {
        process.env[key] = value;
    }

    const store = {
        issues: (input.issues ?? []).map(issue => ({ comments: [], state: 'open', ...issue })),
        next: input.nextNumber ?? 1000,
    };
    const github = makeGithub(store, { failComment: input.failComment === true, failUpdate: input.failUpdate === true });
    const core = { info: () => {}, warning: () => {} };
    const context = {
        repo: { owner: 'microsoft', repo: 'aspire' },
        runId: input.runId ?? 12345,
        runNumber: input.runNumber ?? 7,
        sha: input.sha ?? 'abcdef0123456789',
    };

    const operation = input.operation ?? 'reportFailure';
    let threw = false;
    try {
        await reporter[operation]({ github, context, core });
    } catch {
        threw = true;
    }

    process.stdout.write(JSON.stringify({
        result: {
            threw,
            calls: github.calls,
            issues: store.issues.map(issue => ({
                number: issue.number,
                title: issue.title ?? null,
                state: issue.state,
                stateReason: issue.state_reason ?? null,
                body: issue.body,
                labels: issue.labels ?? [],
                comments: issue.comments ?? [],
            })),
        },
    }));
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
