// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(process.argv[2], 'utf8');
const scenario = process.argv[3];
const subscription = '11111111-1111-1111-1111-111111111111';
const client = '22222222-2222-2222-2222-222222222222';
const tenant = '33333333-3333-3333-3333-333333333333';
const principal = '44444444-4444-4444-4444-444444444444';
const role = '53ca6127-db72-4b80-b1b0-d745d6d5456d';
const scope = `/subscriptions/${subscription}`;
const claims = { oid: principal, tid: tenant, appid: client };
if (scenario === 'v2-token') {
    delete claims.appid;
    claims.azp = client;
}
if (scenario === 'wrong-app') claims.appid = tenant;
if (scenario === 'wrong-tenant') claims.tid = client;
if (scenario === 'missing-oid') delete claims.oid;
const token = scenario === 'invalid-token' ? 'invalid-token'
    : `header.${Buffer.from(JSON.stringify(claims)).toString('base64url')}.signature`;
const assignment = {
    principalId: principal,
    principalType: 'ServicePrincipal',
    scope,
    roleDefinitionId: `${scope}/providers/Microsoft.Authorization/roleDefinitions/${role}`,
    condition: null
};
if (scenario === 'wrong-scope') assignment.scope += '/resourceGroups/narrower';
if (scenario === 'wrong-principal') assignment.principalId = client;
if (scenario === 'wrong-role') assignment.roleDefinitionId = client;
if (scenario === 'conditioned') assignment.condition = 'restricted';
const calls = [];
const secrets = [];
const messages = [];
const cliError = new Error('Azure CLI authorization failed');
let created = false;
const exec = {
    getExecOutput: async (command, args, options) => {
        assert.equal(command, 'az');
        assert.deepEqual(JSON.parse(JSON.stringify(options)), { silent: true });
        calls.push([...args]);
        if (args[0] === 'account') {
            assert.deepEqual([...args], [
                'account', 'get-access-token', '--subscription', subscription,
                '--resource', 'https://management.azure.com/',
                '--query', 'accessToken', '--output', 'tsv', '--only-show-errors'
            ]);
            if (scenario === 'token-error') throw cliError;
            return { stdout: `${token}\n`, exitCode: 0 };
        }
        assert.deepEqual([...args], [
            'role', 'assignment', 'create', '--assignee-object-id', principal,
            '--assignee-principal-type', 'ServicePrincipal', '--role', role,
            '--scope', scope, '--subscription', subscription,
            '--output', 'json', '--only-show-errors'
        ]);
        if (scenario === 'assignment-error') throw cliError;
        // Model Azure CLI's idempotent create: a subsequent invocation returns the same grant.
        created = true;
        return { stdout: JSON.stringify(assignment), exitCode: 0 };
    }
};
const core = {
    setSecret: value => { secrets.push(value); },
    info: message => { messages.push(message); }
};
const env = {
    ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION: subscription,
    AZURE_CLIENT_ID: client,
    AZURE_TENANT_ID: tenant
};
if (scenario === 'missing-subscription') delete env.ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION;
const run = () => runInNewContext(`(async () => { ${source}\n })()`, {
    exec, core, Buffer, process: { env }
});
const success = ['grant', 'rerun', 'v2-token'].includes(scenario);
const attempts = scenario === 'rerun' ? 2 : 1;
for (let attempt = 0; attempt < attempts; attempt++) {
    if (success) {
        await run();
        assert.equal(created, true);
    } else {
        await assert.rejects(run, error => {
            if (['token-error', 'assignment-error'].includes(scenario)) return error === cliError;
            if (scenario === 'missing-subscription') return /IDs must be GUIDs/.test(error.message);
            if (scenario === 'invalid-token') return error instanceof Error || error.name === 'TypeError';
            if (['wrong-app', 'wrong-tenant', 'missing-oid'].includes(scenario)) {
                return /does not identify the configured deployment service principal/.test(error.message);
            }
            return /does not grant unrestricted subscription access/.test(error.message);
        });
    }
}
const noToken = ['missing-subscription', 'token-error'].includes(scenario);
const noGrant = noToken || ['wrong-app', 'wrong-tenant', 'missing-oid', 'invalid-token'].includes(scenario);
assert.equal(calls.length, scenario === 'missing-subscription' ? 0 : noGrant ? 1 : attempts * 2);
assert.deepEqual(secrets, noToken ? [] : Array(attempts).fill(token));
assert.deepEqual(messages, success
    ? Array(attempts).fill('Foundry User subscription role assignment confirmed. Data-plane propagation may take several minutes.')
    : []);
