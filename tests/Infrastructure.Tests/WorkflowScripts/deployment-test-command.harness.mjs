// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(process.argv[2], 'utf8');
const scenario = process.argv[3];
const body = process.argv[4];
const validCommand = process.argv[5] === 'true';
const allowed = ['write', 'admin', 'maintain'].includes(scenario);
const apiError = scenario.startsWith('error-')
    ? Object.assign(new Error('Permission lookup failed'), { status: Number(scenario.slice(6)) })
    : undefined;
const outputs = {};
const failures = [];
const comments = [];
let lookups = 0;
const context = {
    repo: { owner: 'microsoft', repo: 'aspire' },
    issue: { number: 42 },
    payload: { comment: { body, user: { login: 'contributor' } } },
    actor: 'different-user'
};
const github = {
    rest: {
        repos: {
            getCollaboratorPermissionLevel: async ({ owner, repo, username }) => {
                lookups++;
                assert.equal(owner, 'microsoft');
                assert.equal(repo, 'aspire');
                assert.equal(username, 'contributor');
                if (apiError) {
                    throw apiError;
                }

                // GitHub normalizes the maintain role to permission: "write".
                // https://docs.github.com/en/rest/collaborators/collaborators#get-repository-permissions-for-a-user
                return { data: { permission: scenario === 'maintain' ? 'write' : scenario, role_name: scenario } };
            }
        },
        issues: {
            createComment: async ({ owner, repo, issue_number, body }) => {
                assert.equal(owner, 'microsoft');
                assert.equal(repo, 'aspire');
                assert.equal(issue_number, 42);
                comments.push(body);
            }
        }
    }
};
const core = {
    info: () => {},
    setOutput: (name, value) => { outputs[name] = value; },
    setFailed: message => { failures.push(message); }
};
const run = () => runInNewContext(`(async () => { ${source}\n })()`, { github, context, core });

if (!validCommand) {
    await run();
    assert.deepEqual(outputs, { has_write_access: 'false' });
    assert.deepEqual(failures, []);
    assert.deepEqual(comments, []);
} else if (apiError) {
    await assert.rejects(run, error => error === apiError);
    assert.deepEqual(outputs, {});
    assert.deepEqual(failures, []);
    assert.deepEqual(comments, []);
} else {
    await run();
    assert.deepEqual(outputs, { has_write_access: String(allowed) });
    assert.deepEqual(failures, allowed ? [] : ['@contributor does not have write access to this repository.']);
    assert.deepEqual(comments, allowed ? [] : [
        '@contributor The `/deployment-test` command requires write access to this repository for security reasons (it deploys to real Azure infrastructure).'
    ]);
}
assert.equal(lookups, validCommand ? 1 : 0);
