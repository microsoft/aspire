// Integration harness for the scheduled-workflow watchdog's run() orchestrator
// (.github/workflows/monitor-scheduled-workflows.js). Drives run() against an
// in-memory octokit fake so the dry-run no-mutation contract, comment-based
// dedup, and close-on-green — all outside the reach of the pure-helper tests —
// are exercised directly.
//
// Input payload (JSON, first argv):
//   {
//     "dryRun": true,
//     "runsByFile": { "generate-api-diffs.yml": { "conclusion": "failure", "html_url": "...", "run_number": 9, "head_sha": "abcd", "updated_at": "..." } },
//     "issues": [ { "number": 1, "body": "...", "state": "open" } ],
//     "failUpdate": false
//   }
// A workflow file absent from runsByFile yields no completed run (noop). Output:
//   { threw, calls, issues: [ { number, state, body, labels, comments } ] }

'use strict';

const fs = require('node:fs');
const path = require('node:path');

const monitor = require('../../../.github/workflows/monitor-scheduled-workflows.js');

function makeGithub(store, runsByFile, { failUpdate }) {
    const calls = [];
    const listRunRequests = [];
    return {
        calls,
        listRunRequests,
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
                update: async ({ issue_number, body, state }) => {
                    calls.push('update');
                    if (failUpdate) {
                        throw new Error('transient update failure');
                    }
                    const issue = store.issues.find(i => i.number === issue_number);
                    if (body !== undefined) { issue.body = body; }
                    if (state) { issue.state = state; }
                },
                listComments: async ({ issue_number }) => {
                    const issue = store.issues.find(i => i.number === issue_number);
                    if (!issue) {
                        const error = new Error(`issue #${issue_number} not found`);
                        error.status = 404;
                        throw error;
                    }

                    return { data: (issue?.comments ?? []).map(body => ({ body })) };
                },
                createComment: async ({ issue_number, body }) => {
                    calls.push('createComment');
                    const issue = store.issues.find(i => i.number === issue_number);
                    (issue.comments ??= []).push(body);
                },
            },
            actions: {
                listWorkflowRuns: async ({ workflow_id, branch, event, status, per_page }) => {
                    listRunRequests.push({ workflowId: workflow_id, perPage: per_page });
                    // The watchdog must scan only completed scheduled runs on main.
                    // A manual or push run leaking in would let a non-scheduled
                    // success auto-close a real scheduled-failure issue (or a
                    // non-scheduled failure file a false one). Fail loudly if any of
                    // those filters is ever dropped so the regression is caught here.
                    if (branch !== 'main' || event !== 'schedule' || status !== 'completed') {
                        throw new Error(`listWorkflowRuns called without the scheduled-run filters (branch=${branch}, event=${event}, status=${status})`);
                    }
                    const runs = runsByFile[workflow_id];
                    return { data: { workflow_runs: Array.isArray(runs) ? runs : (runs ? [runs] : []) } };
                },
            },
        },
    };
}

async function main() {
    const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));

    const store = {
        issues: (input.issues ?? []).map(issue => ({ comments: [], state: 'open', ...issue })),
        next: input.nextNumber ?? 1000,
    };
    const github = makeGithub(store, input.runsByFile ?? {}, { failUpdate: input.failUpdate === true });
    const logs = [];
    const warnings = [];
    const core = {
        info: message => logs.push(String(message)),
        warning: message => warnings.push(String(message)),
    };
    const context = { repo: { owner: 'microsoft', repo: 'aspire' } };

    let threw = false;
    try {
        await monitor.run({
            github,
            context,
            core,
            dryRun: input.dryRun === true,
            now: input.now ? new Date(input.now) : undefined,
        });
    } catch {
        threw = true;
    }

    process.stdout.write(JSON.stringify({
        result: {
            threw,
            calls: github.calls,
            logs,
            warnings,
            listRunRequests: github.listRunRequests,
            issues: store.issues.map(issue => ({
                number: issue.number,
                state: issue.state,
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
