// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

// Infrastructure.Tests uses the runner's Node without a TypeScript toolchain.
// Native ESM avoids requiring a loader or Node's newer type-stripping support.
const source = readFileSync(process.argv[2], 'utf8');
const scenario = process.argv[3];
const reviewer = { login: 'copilot-pull-request-reviewer[bot]', type: 'Bot' };
const head = 'a'.repeat(40);
const previous = 'b'.repeat(40);
const now = Date.parse('2026-09-08T20:00:00Z');
const staleCutoff = now - 14 * 24 * 60 * 60 * 1000;
const pull = {
    number: 42,
    state: 'open',
    draft: false,
    created_at: '2025-01-01T00:00:00Z',
    updated_at: new Date(now).toISOString(),
    base: { ref: 'main', repo: { full_name: 'microsoft/aspire' } },
    head: { sha: head, ref: 'feature', repo: { full_name: 'external/fork' } },
    requested_reviewers: [],
    user: { login: 'external-contributor', type: 'User' },
    title: 'A contribution',
    body: ''
};
const env = { COPILOT_REVIEW_MODE: 'enabled', COPILOT_REVIEW_PR_NUMBER: '' };
const context = {
    repo: { owner: 'microsoft', repo: 'aspire' },
    eventName: 'pull_request_target',
    ref: 'refs/heads/main',
    payload: { pull_request: { number: 42 } }
};
let current;
let reviewPages = [[]];
let pullPages = [[42]];
let expectedWrites = 0;
let expectedDecision = '';
let expectedError = '';
let failEndpoint = '';
const writes = [];
const calls = [];
const messages = [];
const getCounts = new Map();
let authorPermission = { permission: 'write', role_name: 'write' };
let permissionChecks = 0;

switch (scenario) {
    case 'copilot-author':
    case 'copilot-author-manual':
    case 'copilot-author-dry-run':
        pull.user = { login: 'Copilot', type: 'Bot' };
        expectedDecision = 'PR author does not have repository write access';
        if (scenario === 'copilot-author-manual') {
            context.eventName = 'workflow_dispatch';
        } else if (scenario === 'copilot-author-dry-run') {
            env.COPILOT_REVIEW_MODE = 'dry-run';
        }
        break;
    case 'copilot-author-scan-continues':
        context.eventName = 'schedule';
        pullPages = [[43], [42]];
        expectedDecision = 'PR author does not have repository write access';
        expectedWrites = 1;
        break;
    case 'copilot-login-human':
        pull.user = { login: 'Copilot', type: 'User' };
        expectedError = 'Copilot is not a user';
        break;
    case 'api-permission-not-found':
        expectedError = 'Not Found';
        break;
    case 'author-read':
    case 'author-none':
    case 'author-triage':
    case 'external-author':
    case 'external-author-maintainer-push':
    case 'external-author-scheduled':
    case 'external-author-manual':
    case 'external-author-dry-run':
        authorPermission = { permission: scenario === 'author-none' ? 'none' : 'read', role_name: scenario === 'author-triage' ? 'triage' : 'read' };
        context.actor = 'maintainer';
        pull.author_association = 'MEMBER';
        if (scenario === 'external-author-scheduled') {
            context.eventName = 'schedule';
        } else if (scenario === 'external-author-manual') {
            context.eventName = 'workflow_dispatch';
        } else if (scenario === 'external-author-dry-run') {
            env.COPILOT_REVIEW_MODE = 'dry-run';
        }
        expectedDecision = 'PR author does not have repository write access';
        break;
    case 'author-maintain':
    case 'author-admin':
    case 'author-custom-write':
        authorPermission = { permission: scenario === 'author-admin' ? 'admin' : 'write', role_name: scenario === 'author-maintain' ? 'maintain' : 'custom-role' };
        expectedWrites = 1;
        break;
    case 'author-unknown-permission':
        authorPermission = { permission: 'unexpected' };
        expectedError = 'Unexpected author permission';
        break;
    case 'author-missing':
        pull.user = null;
        expectedError = 'Missing author login';
        break;
    case 'author-permission-revoked':
        expectedDecision = 'PR author does not have repository write access';
        break;
    case 'api-permission-error':
        failEndpoint = 'getCollaboratorPermissionLevel';
        expectedError = 'API unavailable';
        break;
    case 'default-dry-run':
        env.COPILOT_REVIEW_MODE = '';
        expectedDecision = 'Dry run: would request';
        break;
    case 'explicit-dry-run':
        env.COPILOT_REVIEW_MODE = 'dry-run';
        expectedDecision = 'Dry run: would request';
        break;
    case 'disabled':
        env.COPILOT_REVIEW_MODE = 'disabled';
        expectedDecision = 'automation is disabled';
        break;
    case 'invalid-mode':
        env.COPILOT_REVIEW_MODE = 'enable';
        expectedError = 'COPILOT_REVIEW_MODE must be';
        break;
    case 'invalid-pilot':
    case 'unsafe-pilot':
        env.COPILOT_REVIEW_PR_NUMBER = scenario === 'invalid-pilot' ? '42-untrusted' : '9007199254740992';
        expectedError = 'COPILOT_REVIEW_PR_NUMBER must be';
        break;
    case 'pilot-other-pr':
        env.COPILOT_REVIEW_PR_NUMBER = '43';
        expectedDecision = 'outside pilot scope';
        break;
    case 'pilot-scheduled':
        env.COPILOT_REVIEW_PR_NUMBER = '42';
        context.eventName = 'schedule';
        expectedWrites = 1;
        break;
    case 'scheduled-stale':
    case 'scheduled-stale-boundary':
    case 'scheduled-recent':
    case 'scheduled-stale-dry-run':
    case 'scheduled-stale-pilot':
    case 'scheduled-invalid-activity':
    case 'scheduled-missing-activity':
        context.eventName = 'schedule';
        pull.updated_at = new Date(staleCutoff - 1000).toISOString();
        expectedDecision = 'no PR activity in the last 14 days';
        if (scenario === 'scheduled-stale-boundary') {
            pull.updated_at = new Date(staleCutoff).toISOString();
        } else if (scenario === 'scheduled-recent') {
            pull.updated_at = new Date(staleCutoff + 1000).toISOString();
            expectedDecision = '';
            expectedWrites = 1;
        } else if (scenario === 'scheduled-stale-dry-run') {
            env.COPILOT_REVIEW_MODE = 'dry-run';
        } else if (scenario === 'scheduled-stale-pilot') {
            env.COPILOT_REVIEW_PR_NUMBER = '42';
        } else if (scenario === 'scheduled-invalid-activity' || scenario === 'scheduled-missing-activity') {
            if (scenario === 'scheduled-invalid-activity') {
                pull.updated_at = 'not-a-timestamp';
            } else {
                Reflect.deleteProperty(pull, 'updated_at');
            }
            expectedDecision = '';
            expectedError = 'Invalid updated_at timestamp';
        }
        break;
    case 'stale-push':
    case 'stale-manual':
        pull.updated_at = new Date(staleCutoff - 1000).toISOString();
        if (scenario === 'stale-manual') {
            context.eventName = 'workflow_dispatch';
        }
        expectedWrites = 1;
        break;
    case 'bot-author':
    case 'release-branch':
    case 'metadata-injection':
        if (scenario === 'bot-author') {
            pull.user = { login: 'dependabot[bot]', type: 'Bot' };
            authorPermission = { permission: 'none', role_name: '' };
            expectedDecision = 'PR author does not have repository write access';
        }
        if (scenario === 'release-branch') {
            pull.base.ref = 'release/13.5';
        }
        if (scenario === 'metadata-injection') {
            pull.title = pull.body = pull.head.ref = '${{ secrets.TOKEN }}"; throw new Error("injected"); //';
            pull.user.login = '<script>alert(1)</script>';
        }
        expectedWrites = scenario === 'bot-author' ? 0 : 1;
        break;
    case 'dependabot-scheduled':
    case 'dependabot-manual':
        pull.user = { login: 'dependabot[bot]', type: 'Bot' };
        context.eventName = scenario === 'dependabot-scheduled' ? 'schedule' : 'workflow_dispatch';
        authorPermission = { permission: 'none', role_name: '' };
        expectedDecision = 'PR author does not have repository write access';
        break;
    case 'draft':
    case 'draft-scheduled':
        pull.draft = true;
        if (scenario === 'draft-scheduled') {
            context.eventName = 'schedule';
        }
        expectedWrites = 1;
        break;
    case 'draft-before-write':
        current = structuredClone(pull);
        current.draft = true;
        expectedWrites = 1;
        break;
    case 'closed':
        pull.state = 'closed';
        expectedDecision = 'PR is not open';
        break;
    case 'unsupported-branch':
        pull.base.ref = 'unprotected-branch';
        expectedDecision = 'target branch is outside';
        break;
    case 'pending':
        pull.requested_reviewers = [reviewer];
        expectedDecision = 'review is pending';
        break;
    case 'reviewed':
    case 'dismissed':
        reviewPages = [[{ user: reviewer, commit_id: head, state: scenario === 'dismissed' ? 'DISMISSED' : 'COMMENTED' }]];
        expectedDecision = 'already reviewed this head';
        break;
    case 'older-review':
    case 'human-review':
    case 'spoofed-reviewer':
        reviewPages = [[{
            user: scenario === 'older-review' ? reviewer : { login: scenario === 'human-review' ? 'maintainer' : reviewer.login, type: 'User' },
            commit_id: scenario === 'older-review' ? previous : head,
            state: 'COMMENTED'
        }]];
        expectedWrites = 1;
        break;
    case 'head-changed':
    case 'closed-before-write':
    case 'retargeted-before-write':
    case 'pending-before-write':
        current = structuredClone(pull);
        if (scenario === 'head-changed') {
            current.head.sha = previous;
        } else if (scenario === 'closed-before-write') {
            current.state = 'closed';
        } else if (scenario === 'retargeted-before-write') {
            current.base.ref = 'release/13.5';
        } else {
            current.requested_reviewers = [reviewer];
        }
        expectedDecision = 'changed during reconciliation';
        break;
    case 'wrong-repository':
        context.repo.repo = 'fork';
        expectedError = 'only operates on microsoft/aspire';
        break;
    case 'wrong-pr-repository':
        pull.base.repo.full_name = 'external/fork';
        expectedError = 'Invalid repository, number, or head SHA';
        break;
    case 'wrong-number':
        pull.number = 43;
        expectedError = 'Invalid repository, number, or head SHA';
        break;
    case 'invalid-number':
        context.payload.pull_request.number = -1;
        expectedError = 'Invalid PR number';
        break;
    case 'invalid-sha':
        pull.head.sha = '<script>';
        expectedError = 'Invalid repository, number, or head SHA';
        break;
    case 'missing-reviewers':
        Reflect.deleteProperty(pull, 'requested_reviewers');
        expectedError = 'Missing requested reviewers';
        break;
    case 'unsupported-event':
        context.eventName = 'pull_request_review';
        expectedError = 'Unsupported event';
        break;
    case 'untrusted-dispatch':
        context.eventName = 'workflow_dispatch';
        context.ref = 'refs/heads/untrusted';
        expectedError = 'Unsupported event';
        break;
    case 'manual-dispatch':
        context.eventName = 'workflow_dispatch';
        expectedWrites = 1;
        break;
    case 'api-read-error':
    case 'api-history-error':
    case 'api-write-error':
        failEndpoint = scenario === 'api-read-error' ? 'get' : scenario === 'api-history-error' ? 'listReviews' : 'requestReviewers';
        expectedError = 'API unavailable';
        break;
    case 'pagination':
        context.eventName = 'schedule';
        pullPages = [[42], [43]];
        // Only the second page contains the current-head review.
        reviewPages = [[{ user: reviewer, commit_id: previous, state: 'COMMENTED' }], [{ user: reviewer, commit_id: head, state: 'COMMENTED' }]];
        expectedDecision = 'already reviewed this head';
        break;
    case 'push-during-review':
        pull.requested_reviewers = [reviewer];
        expectedWrites = 1;
        break;
    default:
        throw new Error(`Unknown scenario: ${scenario}`);
}

function request(endpoint, args) {
    calls.push(endpoint);
    assert.equal(args.owner, 'microsoft');
    assert.equal(args.repo, 'aspire');
    if (endpoint === failEndpoint) {
        throw new Error('API unavailable');
    }
}

const pulls = {
    get: async (args) => {
        request('get', args);
        const number = args.pull_number;
        const count = (getCounts.get(number) ?? 0) + 1;
        getCounts.set(number, count);
        const result = structuredClone(count > 1 && current ? current : pull);
        if (scenario === 'pagination' || scenario === 'copilot-author-scan-continues') {
            result.number = number;
        }
        if (scenario === 'copilot-author-scan-continues' && number === 43) {
            result.user = { login: 'Copilot', type: 'Bot' };
        }
        return { data: result };
    },
    listReviews: async (_args) => { throw new Error('Use paginated review history'); },
    list: async (_args) => { throw new Error('Use paginated PR enumeration'); },
    requestReviewers: async (args) => {
        request('requestReviewers', args);
        assert.equal(env.COPILOT_REVIEW_MODE, 'enabled');
        assert.deepEqual(Array.from(args.reviewers), [reviewer.login]);
        assert.equal(args.pull_number, 42);
        writes.push(args);
        pull.requested_reviewers = [reviewer];
    }
};
const repos = {
    getCollaboratorPermissionLevel: async (args) => {
        request('getCollaboratorPermissionLevel', args);
        if (args.username === 'Copilot' || scenario === 'api-permission-not-found') {
            throw Object.assign(new Error(args.username === 'Copilot' ? 'Copilot is not a user' : 'Not Found'), { status: 404 });
        }
        assert.equal(args.username, pull.user.login);
        permissionChecks++;
        return { data: scenario === 'author-permission-revoked' && permissionChecks > 1
            ? { permission: 'read', role_name: 'read' }
            : authorPermission };
    }
};
const paginate = Object.assign(
    async (endpoint, args) => {
        assert.equal(endpoint, pulls.listReviews);
        assert.equal(args.per_page, 100);
        request('listReviews', args);
        return reviewPages.flat();
    },
    {
        iterator: async function* (endpoint, args) {
            assert.equal(endpoint, pulls.list);
            assert.equal(args.per_page, 100);
            request('list', args);
            for (const numbers of pullPages) {
                yield { data: numbers.map(number => ({ number })) };
            }
        }
    }
);
const summary = {
    addHeading: (_heading) => summary,
    addTable: (_rows) => summary,
    write: async () => {}
};
async function execute() {
    // Execute the exact YAML-extracted script with only mocked APIs and env.
    // No production token, process object, or network implementation is exposed.
    await runInNewContext(`(async () => { ${source}\n })()`, {
        github: { rest: { pulls, repos }, paginate },
        core: { info: (message) => messages.push(message), summary },
        context,
        Date: class extends Date {
            static now() { return now; }
        },
        process: { env }
    });
}

if (expectedError) {
    await assert.rejects(execute, new RegExp(expectedError));
} else {
    await execute();
    if (scenario === 'push-during-review') {
        assert.equal(writes.length, 0);
        // Completion on an older SHA clears the pending request. The scheduled
        // pass must request the newer head exactly once, then defer while active.
        pull.requested_reviewers = [];
        reviewPages = [[{ user: reviewer, commit_id: previous, state: 'COMMENTED' }]];
        context.eventName = 'schedule';
        await execute();
        await execute();
        pull.requested_reviewers = [];
        reviewPages[0].push({ user: reviewer, commit_id: head, state: 'COMMENTED' });
        await execute();
    }
}
assert.equal(writes.length, expectedWrites);
if (expectedDecision) {
    assert.ok(messages.some(message => message.includes(expectedDecision)), messages.join('\n'));
}
if (scenario === 'disabled' || scenario === 'pilot-other-pr' || scenario === 'invalid-pilot' ||
    scenario === 'invalid-mode' || scenario === 'unsafe-pilot' || scenario === 'wrong-repository' ||
    scenario === 'unsupported-event' || scenario === 'untrusted-dispatch' || scenario === 'invalid-number') {
    assert.deepEqual(calls, []);
}
if (scenario === 'pilot-scheduled') {
    assert.deepEqual(calls, ['get', 'getCollaboratorPermissionLevel', 'listReviews', 'get', 'getCollaboratorPermissionLevel', 'requestReviewers']);
}
if (scenario.startsWith('scheduled-') && scenario !== 'scheduled-recent') {
    // Stale PRs and invalid activity metadata must never reach review-history
    // lookup or the write endpoint, even when the scan is limited to a pilot.
    assert.deepEqual(calls, scenario === 'scheduled-stale-pilot' ? ['get'] : ['list', 'get']);
}
if (scenario === 'pagination') {
    assert.deepEqual([...getCounts.keys()], [42, 43]);
}
if (['copilot-author', 'copilot-author-manual', 'copilot-author-dry-run'].includes(scenario)) {
    assert.deepEqual(calls, scenario === 'copilot-author-manual' ? ['list', 'get'] : ['get']);
}
if (scenario === 'copilot-author-scan-continues') {
    assert.deepEqual([...getCounts.keys()], [43, 42]);
    assert.deepEqual(calls, ['list', 'get', 'get', 'getCollaboratorPermissionLevel', 'listReviews', 'get', 'getCollaboratorPermissionLevel', 'requestReviewers']);
    assert.equal(writes[0].pull_number, 42);
}
if (scenario === 'copilot-login-human' || scenario === 'api-permission-not-found') {
    assert.deepEqual(calls, ['get', 'getCollaboratorPermissionLevel']);
}
if (scenario === 'api-write-error') {
    assert.equal(calls.filter(endpoint => endpoint === 'requestReviewers').length, 1);
}
if (scenario === 'metadata-injection') {
    assert.equal(messages.some(message => message.includes('injected') || message.includes('<script>')), false);
}
console.log(`Passed: ${scenario}`);
