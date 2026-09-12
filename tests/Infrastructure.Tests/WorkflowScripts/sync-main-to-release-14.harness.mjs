// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

// Match the other inline-workflow harnesses: no TypeScript loader/toolchain is
// available in Infrastructure.Tests, which runs the Node supplied by the runner.
const source = readFileSync(process.argv[2], 'utf8');
const scenario = process.argv[3];
const sha = 'a'.repeat(40);
const target = 'b'.repeat(40);
const head = `sync/main-to-release-14.0/${sha}`;
const bot = 'aspire-repo-bot[bot]';
const context = {
    repo: { owner: 'microsoft', repo: 'aspire' },
    ref: 'refs/heads/main',
    eventName: 'schedule'
};
const env = { SYNC_BOT_LOGIN: bot };
const pull = {
    number: 42,
    node_id: 'PR_42',
    state: 'open',
    draft: false,
    base: { ref: 'release/14.0', repo: { full_name: 'microsoft/aspire' } },
    head: { ref: head, sha, repo: { full_name: 'microsoft/aspire' } },
    user: { type: 'Bot', login: bot },
    body: '<!-- sync-main-to-release-14 -->',
    html_url: 'https://github.com/microsoft/aspire/pull/42',
    mergeable: true,
    mergeable_state: 'blocked',
    auto_merge: null
};
const settings = { allow_merge_commit: true, allow_auto_merge: true };
let existing = true;
let refExists = false;
let ahead = 1;
let expectedWrites = ['auto-merge'];
let expectedError = '';
let expectedMessage = 'Enabled merge-commit auto-merge';
let fail = '';
const writes = [];
const calls = [];
const messages = [];
let getCount = 0;
let delayCount = 0;
let mergeRejected = false;

switch (scenario) {
    case 'up-to-date':
        existing = false;
        ahead = 0;
        expectedWrites = [];
        expectedMessage = 'already contains all commits';
        break;
    case 'create':
    case 'orphaned-ref':
        existing = false;
        refExists = scenario === 'orphaned-ref';
        expectedWrites = refExists ? ['create-pr', 'auto-merge'] : ['create-ref', 'create-pr', 'auto-merge'];
        break;
    case 'existing':
        break;
    case 'manual-dispatch':
        context.eventName = 'workflow_dispatch';
        break;
    case 'ready':
    case 'ready-has-hooks':
    case 'ready-unstable':
    case 'merge-rejected':
    case 'has-hooks-merge-rejected':
    case 'unstable-merge-rejected':
    case 'merge-error':
        pull.mergeable_state = scenario.includes('has-hooks') ? 'has_hooks'
            : scenario.includes('unstable') ? 'unstable' : 'clean';
        mergeRejected = scenario.endsWith('merge-rejected');
        expectedWrites = ['merge'];
        expectedMessage = 'Merged the ready synchronization PR';
        if (mergeRejected) {
            expectedError = 'GitHub did not merge';
        } else if (scenario === 'merge-error') {
            fail = 'merge-error';
            expectedWrites = [];
            expectedError = 'API failed: merge-error';
        }
        break;
    case 'unrelated-pr':
        existing = false;
        expectedWrites = ['create-ref', 'create-pr', 'auto-merge'];
        break;
    case 'changed-ref':
        existing = false;
        refExists = true;
        expectedWrites = [];
        expectedError = 'no longer matches';
        break;
    case 'multiple-prs':
        expectedWrites = [];
        expectedError = 'Multiple synchronization PRs';
        break;
    case 'conflict':
        pull.mergeable = false;
        expectedWrites = [];
        expectedMessage = 'Merge conflicts need manual resolution';
        break;
    case 'unknown-mergeability':
        pull.mergeable = null;
        expectedWrites = [];
        expectedMessage = 'still computing mergeability';
        break;
    case 'mergeability-ready-on-retry':
        pull.mergeable = null;
        break;
    case 'merge-disabled':
    case 'auto-merge-disabled':
        settings[scenario === 'merge-disabled' ? 'allow_merge_commit' : 'allow_auto_merge'] = false;
        expectedWrites = [];
        expectedMessage = 'Enable repository merge commits and auto-merge';
        break;
    case 'already-enabled':
        pull.auto_merge = { merge_method: 'merge' };
        expectedWrites = [];
        expectedMessage = 'auto-merge is already enabled';
        break;
    case 'squash-enabled':
        pull.auto_merge = { merge_method: 'squash' };
        expectedWrites = [];
        expectedError = 'must use a merge commit';
        break;
    case 'closed':
    case 'draft':
    case 'retargeted':
    case 'wrong-author':
    case 'missing-marker':
    case 'fork-head':
    case 'changed-head-branch':
        if (scenario === 'closed') {
            pull.state = 'closed';
        } else if (scenario === 'draft') {
            pull.draft = true;
        } else if (scenario === 'retargeted') {
            pull.base.ref = 'main';
        } else if (scenario === 'wrong-author') {
            pull.user = { type: 'User', login: bot };
        } else if (scenario === 'fork-head') {
            pull.head.repo.full_name = 'external/fork';
        } else if (scenario === 'changed-head-branch') {
            pull.head.ref = 'main';
        } else {
            pull.body = '';
        }
        expectedWrites = [];
        expectedError = 'identity or state changed';
        break;
    case 'wrong-repository':
    case 'untrusted-dispatch':
    case 'unsupported-event':
        if (scenario === 'wrong-repository') {
            context.repo.owner = 'external';
        } else if (scenario === 'untrusted-dispatch') {
            context.eventName = 'workflow_dispatch';
            context.ref = 'refs/heads/feature';
        } else {
            context.eventName = 'pull_request';
        }
        expectedWrites = [];
        expectedError = 'only runs from main';
        break;
    case 'missing-bot':
        env.SYNC_BOT_LOGIN = '[bot]';
        expectedWrites = [];
        expectedError = 'Missing Aspire App bot identity';
        break;
    case 'list-error':
    case 'ref-read-error':
    case 'ref-write-error':
    case 'create-pr-error':
    case 'auto-merge-error':
        fail = scenario;
        existing = scenario === 'list-error' || scenario === 'auto-merge-error';
        expectedWrites = scenario === 'create-pr-error' ? ['create-ref'] : [];
        expectedError = `API failed: ${scenario}`;
        break;
    default:
        throw new Error(`Unknown scenario: ${scenario}`);
}

function check(endpoint, args) {
    calls.push(endpoint);
    assert.equal(args.owner, 'microsoft');
    assert.equal(args.repo, 'aspire');
    if (fail === endpoint) {
        throw Object.assign(new Error(`API failed: ${endpoint}`), { status: 403 });
    }
}
const github = {
    paginate: async (route, args) => {
        assert.equal(route, 'list-pulls');
        check('list-error', args);
        assert.equal(args.base, 'release/14.0');
        assert.equal(args.state, 'open');
        assert.equal(args.per_page, 100);
        // Listing and re-reading are separate API snapshots. Exercise retargeting
        // and head changes during reconciliation, not just preexisting bad data.
        const listed = { ...pull, head: { ref: head, repo: { full_name: 'microsoft/aspire' } } };
        if (scenario === 'unrelated-pr') {
            return [{ ...listed, head: { ref: 'unrelated-feature', repo: { full_name: 'microsoft/aspire' } } }];
        }
        return existing ? (scenario === 'multiple-prs' ? [listed, { ...listed, number: 43 }] : [listed]) : [];
    },
    rest: {
        pulls: {
            list: 'list-pulls',
            get: async args => {
                check('get-pr', args);
                assert.equal(args.pull_number, 42);
                getCount++;
                if (scenario === 'mergeability-ready-on-retry' && getCount === 3) {
                    pull.mergeable = true;
                }
                return { data: pull };
            },
            merge: async args => {
                check('merge-error', args);
                assert.equal(args.pull_number, 42);
                assert.equal(args.sha, sha);
                assert.equal(args.merge_method, 'merge');
                assert.deepEqual(Object.keys(args).sort(), ['merge_method', 'owner', 'pull_number', 'repo', 'sha']);
                writes.push('merge');
                return { data: { merged: !mergeRejected, message: 'Branch policy prevented merging.' } };
            },
            create: async args => {
                check('create-pr-error', args);
                assert.equal(args.head, head);
                assert.equal(args.base, 'release/14.0');
                assert.match(args.body, /Keep the 14\.0 version/);
                assert.match(args.body, /never squash or rebase/);
                assert.match(args.body, /<!-- sync-main-to-release-14 -->/);
                writes.push('create-pr');
                return { data: pull };
            }
        },
        repos: {
            getBranch: async args => {
                check('get-branch', args);
                assert.ok(['main', 'release/14.0'].includes(args.branch));
                return { data: { commit: { sha: args.branch === 'main' ? sha : target } } };
            },
            compareCommitsWithBasehead: async args => {
                check('compare', args);
                assert.equal(args.basehead, `${target}...${sha}`);
                return { data: { ahead_by: ahead } };
            },
            get: async args => {
                check('get-settings', args);
                return { data: settings };
            }
        },
        git: {
            getRef: async args => {
                check('ref-read-error', args);
                assert.equal(args.ref, `heads/${head}`);
                if (!refExists) {
                    throw Object.assign(new Error('Not found'), { status: 404 });
                }
                return { data: { object: { sha: scenario === 'changed-ref' ? target : sha } } };
            },
            createRef: async args => {
                check('ref-write-error', args);
                assert.equal(args.ref, `refs/heads/${head}`);
                assert.equal(args.sha, sha);
                writes.push('create-ref');
                return { data: { object: { sha } } };
            }
        }
    },
    graphql: async (query, args) => {
        if (fail === 'auto-merge-error') {
            throw new Error('API failed: auto-merge-error');
        }
        assert.match(query, /enablePullRequestAutoMerge/);
        assert.match(query, /mergeMethod: MERGE/);
        assert.equal(args.pullRequestId, 'PR_42');
        writes.push('auto-merge');
    }
};
const summary = {
    addRaw() { return this; },
    async write() {}
};
const core = {
    info: message => messages.push(message),
    warning: message => messages.push(message),
    summary
};
let error;
try {
    await runInNewContext(`(async () => { ${source}\n })()`, {
        github, context, core, process: { env },
        setTimeout: (callback, delay) => {
            assert.equal(delay, 2000);
            delayCount++;
            callback();
        }
    }, { timeout: 1000 });
} catch (caught) {
    error = caught;
}
if (expectedError) {
    assert.ok(error?.message.includes(expectedError), `Expected "${expectedError}", got ${error?.stack}`);
} else {
    assert.ifError(error);
    assert.ok(messages.some(message => message.includes(expectedMessage)), messages.join('\n'));
}
assert.deepEqual(writes, expectedWrites);
assert.equal(delayCount, scenario === 'unknown-mergeability' ? 4 : scenario === 'mergeability-ready-on-retry' ? 2 : 0);
if (existing) {
    assert.equal(calls.includes('get-branch'), false, 'Never advance or overwrite a pending PR branch');
}
if (['wrong-repository', 'untrusted-dispatch', 'unsupported-event', 'missing-bot'].includes(scenario)) {
    assert.deepEqual(calls, []);
}
console.log(`PASS: ${scenario}`);
