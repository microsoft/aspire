// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const [repo, scenario] = process.argv.slice(2);
const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-macos-stapler-test-'));
const helper = path.join(repo, 'tools/CreateLayout/macos-stapler-with-retry.sh');
const transient = 'Error Domain=NSURLErrorDomain Code=-1005 "The network connection was lost."';

try {
    const bin = path.join(workspace, 'commands with spaces');
    fs.mkdirSync(bin);
    for (const name of ['xcrun', 'sleep']) {
        fs.writeFileSync(path.join(bin, name), `#!/usr/bin/env node
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const root = process.env.STAPLER_TEST_ROOT;
const trace = path.join(root, 'trace');
const previous = fs.existsSync(trace) ? fs.readFileSync(trace, 'utf8').trim().split('\\n').map(JSON.parse) : [];
fs.appendFileSync(trace, JSON.stringify({ command: '${name}', args }) + '\\n');
if ('${name}' === 'xcrun') {
    const outcomes = JSON.parse(fs.readFileSync(path.join(root, 'outcomes'), 'utf8'));
    const outcome = outcomes[previous.filter(entry => entry.command === 'xcrun').length];
    if (!outcome) { throw new Error('Unexpected extra stapler attempt'); }
    console.log(outcome.stdout || '');
    console.error(outcome.stderr || '');
    process.exit(outcome.status);
}
`, { mode: 0o755 });
    }

    function run(operation, outcomes, expectedStatus, expectedDelays, extraArgs = []) {
        const tracePath = path.join(workspace, 'trace');
        fs.rmSync(tracePath, { force: true });
        fs.writeFileSync(path.join(workspace, 'outcomes'), JSON.stringify(outcomes));
        const app = path.join(workspace, "Aspire 'quoted' $tray.app");
        const result = spawnSync('bash', [helper, operation, app, ...extraArgs], {
            env: { ...process.env, PATH: `${bin}${path.delimiter}${process.env.PATH}`, STAPLER_TEST_ROOT: workspace, TMPDIR: workspace },
            encoding: 'utf8',
            timeout: 10000
        });
        assert.ifError(result.error);
        assert.equal(result.status, expectedStatus, result.stdout + result.stderr);
        const trace = fs.existsSync(tracePath) ? fs.readFileSync(tracePath, 'utf8').trim().split('\n').map(JSON.parse) : [];
        const expected = [];
        if (outcomes.length > 0) {
            for (let attempt = 0; attempt <= expectedDelays.length; attempt++) {
                expected.push({ command: 'xcrun', args: ['stapler', operation, app] });
                if (attempt < expectedDelays.length) {
                    expected.push({ command: 'sleep', args: [String(expectedDelays[attempt])] });
                    assert.match(result.stderr, new RegExp(`retrying in ${expectedDelays[attempt]}s`));
                }
                assert.ok(result.stdout.includes(outcomes[attempt].stdout || ''));
                assert.ok(result.stdout.includes(outcomes[attempt].stderr || ''));
            }
        }
        assert.deepEqual(trace, expected);
        assert.deepEqual(fs.readdirSync(workspace).sort(), ['commands with spaces', 'outcomes', ...(trace.length ? ['trace'] : [])].sort());
        return result;
    }

    for (const operation of ['staple', 'validate']) {
        switch (scenario) {
            case 'success':
                run(operation, [{ status: 0, stdout: 'Ticket verified.' }], 0, []);
                break;
            case 'transient-success':
                for (let failures = 1; failures <= 3; failures++) {
                    for (const stream of ['stdout', 'stderr']) {
                        run(operation, [
                            ...Array.from({ length: failures }, () => ({ status: 68, [stream]: transient })),
                            { status: 0, stdout: 'Ticket verified.' }
                        ], 0, [5, 15, 30].slice(0, failures));
                    }
                }
                break;
            case 'exhausted': {
                const result = run(operation, Array.from({ length: 4 }, () => ({ status: 68, stderr: transient })), 68, [5, 15, 30]);
                assert.match(result.stderr, /failed after 4 attempts; connection-loss retries exhausted/);
                break;
            }
            case 'nontransient':
                for (const stderr of [
                    'CloudKit response is inconsistent with expectations: (null)',
                    'The path has not been previously notarized.',
                    'Error Domain=NSURLErrorDomain Code=-10050 "Not the connection-loss code"',
                    'Error Domain=OtherDomain Code=-1005 "Not a URL error"',
                    'Error Domain=NSURLErrorDomain Code=-1001 "The request timed out."',
                    'The ticket has been revoked.'
                ]) {
                    run(operation, [{ status: 68, stderr }], 68, []);
                }
                run(operation, [{ status: 65, stderr: 'The ticket data is invalid.' }], 65, []);
                break;
            case 'changed-error':
                run(operation, [{ status: 68, stderr: transient }, { status: 65, stderr: 'The ticket data is invalid.' }], 65, [5]);
                break;
            case 'invalid-arguments':
                run('notarize', [], 1, []);
                run(operation, [], 1, [], ['unexpected']);
                break;
            default:
                throw new Error(`Unknown scenario: ${scenario}`);
        }
    }
    console.log(`Passed: ${scenario} (staple and validate)`);
} finally {
    fs.rmSync(workspace, { recursive: true, force: true });
}
