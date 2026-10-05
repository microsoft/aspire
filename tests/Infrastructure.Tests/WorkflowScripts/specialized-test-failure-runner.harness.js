// Integration harness for the specialized-test failure reporter's network
// orchestration (.github/workflows/specialized-test-failure-runner.js). The runner
// owns the create-vs-append-vs-dedup branching and the comment/body ordering that
// pure-helper tests cannot reach, so this harness drives it against an in-memory
// fake of the octokit surface it uses and reports the resulting state.
//
// Input payload (JSON, first argv):
//   {
//     "env": { "WORKFLOW_FILE": "...", "DISPLAY_NAME": "...", "IGNORE_TEST_FAILURES": "false" },
//     "failedTests": ["A.B.C"] | null,   // when set, written to a temp file referenced by FAILED_TESTS_PATH
//     "omitFailedTestsPath": true,       // when true, FAILED_TESTS_PATH is left unset (extract-step crash)
//     "issues": [ { "number": 1, "body": "...", "state": "open" } ],
//     "failComment": false,              // make createComment throw (transient failure)
//     "runId": 12345, "runNumber": 7
//   }
// Output: { threw, calls, issues: [ { number, title, state, body, comments } ] }

'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const reporter = require('../../../.github/workflows/report-specialized-test-failures.js');
const runner = require('../../../.github/workflows/specialized-test-failure-runner.js');

function makeGithub(store, { failComment }) {
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
    delete process.env.FAILED_TESTS_PATH;
    if (!input.omitFailedTestsPath && Array.isArray(input.failedTests)) {
        const tempPath = path.join(os.tmpdir(), `failed-${Date.now()}-${Math.random().toString(36).slice(2)}.json`);
        fs.writeFileSync(tempPath, JSON.stringify({ failedTests: input.failedTests, count: input.failedTests.length }));
        process.env.FAILED_TESTS_PATH = tempPath;
    }

    const store = {
        issues: (input.issues ?? []).map(issue => ({ comments: [], state: 'open', ...issue })),
        next: input.nextNumber ?? 1000,
    };
    const github = makeGithub(store, { failComment: input.failComment === true });
    const core = { info: () => {}, warning: () => {} };
    const context = { repo: { owner: 'microsoft', repo: 'aspire' }, runId: input.runId ?? 12345, runNumber: input.runNumber ?? 7 };

    let threw = false;
    try {
        await runner({ github, context, core, reporter });
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
                body: issue.body,
                comments: issue.comments ?? [],
            })),
        },
    }));
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
