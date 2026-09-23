import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';
import { observerReady, redact, runDiagnostics, snapshotText, workloadEnvironment } from './RunDiagnosticSuite.ts';

test('observer readiness requires a real fresh sample and the target DCP binary', () => {
    const now = Date.parse('2026-09-23T17:00:00Z');
    const status = {
        state: 'running', dcpExecutablePresent: true,
        heartbeatAtUtc: '2026-09-23T16:59:59Z',
        counts: { successfulSamples: 1 },
    };
    assert.equal(observerReady(status, now), true);
    assert.equal(observerReady({ ...status, counts: { successfulSamples: 0 } }, now), false);
    assert.equal(observerReady({ ...status, dcpExecutablePresent: false }, now), false);
    assert.equal(observerReady({ ...status, state: 'failed' }, now), false);
    assert.equal(observerReady(status, now + 20000), false);
    assert.equal(observerReady({ ...status, heartbeatAtUtc: 'invalid' }, now), false);
    assert.equal(observerReady(null, now), false);
});

test('live diagnostic redaction removes tokens while preserving process evidence', () => {
    const input = 'PID=123 root=456 /login?t=abcdefghijklmnop\n'
        + '{"token":"abcdefghijklmnop"}\n'
        + '::add-mask::artifact-upload-signature\n'
        + 'Setting up RPC server with token: secret-rpc-value\n'
        + 'Authorization: Bearer secret-value\n'
        + 'https://example.invalid/blob?sig=secret-signature&other=kept';
    assert.equal(redact(input), 'PID=123 root=456 /login?t=<redacted>\n'
        + '{"token":"<redacted>"}\n'
        + '::add-mask::<redacted>\n'
        + 'Setting up RPC server with token: <redacted>\n'
        + 'Authorization: Bearer <redacted>\n'
        + 'https://example.invalid/blob?sig=<redacted>&other=kept');
});

test('workload and observer never inherit artifact or repository authority', () => {
    const result = workloadEnvironment({
        PATH: 'safe-path', ASPIRE_DCP_PATH: 'owned-dcp', TREE_ACTIONS_DIAGNOSTIC_CYCLES: '30',
        GH_TOKEN: 'secret', GITHUB_TOKEN: 'secret', ACTIONS_RUNTIME_TOKEN: 'secret',
        actions_results_url: 'private-endpoint', INPUT_SCRIPT: 'private-script',
        'INPUT_GITHUB-TOKEN': 'secret', NUGET_PASSWORD: 'secret',
    });

    assert.deepEqual(result, {
        PATH: 'safe-path', ASPIRE_DCP_PATH: 'owned-dcp', TREE_ACTIONS_DIAGNOSTIC_CYCLES: '30',
    });
});

test('native environment arrays are omitted without altering process-identity records', () => {
    const input = '{"msg":"Process details","Env":["KEY=value","OTHER=private"]}\n'
        + '{"msg":"Stopping process tree...","PID":1856,"Tree":[1856,3444]}\n';
    assert.equal(redact(input), '[omitted process environment record]\n'
        + '{"msg":"Stopping process tree...","PID":1856,"Tree":[1856,3444]}\n');
});

test('snapshots redact an immutable copy without changing live diagnostics', () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-20103-snapshot-'));
    try {
        const source = path.join(directory, 'live.log');
        const destination = path.join(directory, 'checkpoint', 'live.log');
        const original = 'resource=worker /login?t=abcdefghijklmnop\n';
        fs.writeFileSync(source, original);
        snapshotText(source, destination);
        assert.equal(fs.readFileSync(source, 'utf8'), original);
        assert.equal(fs.readFileSync(destination, 'utf8'), 'resource=worker /login?t=<redacted>\n');
        fs.appendFileSync(source, 'later event\n');
        assert.equal(fs.readFileSync(destination, 'utf8'), 'resource=worker /login?t=<redacted>\n');
    } finally {
        fs.rmSync(directory, { recursive: true, force: true });
    }
});

test('bounded snapshots report omitted data and do not emit partial token lines', () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-20103-bounded-'));
    try {
        const source = path.join(directory, 'live.log');
        const destination = path.join(directory, 'snapshot.log');
        fs.writeFileSync(source, 'token=abcdefghijklmnop\nsafe\n');
        snapshotText(source, destination, 12);
        assert.equal(fs.readFileSync(destination, 'utf8'),
            '[diagnostic snapshot truncated; omitted 16 bytes]\nsafe\n');
        fs.writeFileSync(source, 'token=abcdefghijklmnop');
        snapshotText(source, destination, 8);
        assert.equal(fs.readFileSync(destination, 'utf8'),
            '[diagnostic snapshot truncated; omitted 14 bytes]\n[partial single-line content omitted]\n');
    } finally {
        fs.rmSync(directory, { recursive: true, force: true });
    }
});

test('controller persists remote checkpoints before workload and after exit', { timeout: 30000 }, async () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-20103-controller-'));
    const previous = {
        token: process.env.ACTIONS_RUNTIME_TOKEN,
        url: process.env.ACTIONS_RESULTS_URL,
        remote: process.env.DIAGNOSTIC_TEST_REMOTE,
        dcp: process.env.ASPIRE_DCP_PATH,
    };
    try {
        const extensionRoot = path.join(directory, 'extension');
        const scripts = path.join(extensionRoot, 'scripts');
        const toolsRoot = path.join(directory, 'tools');
        const remote = path.join(directory, 'remote');
        for (const target of [scripts, toolsRoot, remote]) {
            fs.mkdirSync(target, { recursive: true });
        }
        // The integration fixture uses the documented upload-action inputs but
        // copies to a local "remote" store. It never calls an artifact service.
        const uploadActionPath = path.join(directory, 'upload.cjs');
        fs.writeFileSync(uploadActionPath, `
const fs = require('node:fs');
const path = require('node:path');
if (!fs.existsSync(process.env.GITHUB_OUTPUT)) { throw new Error('Missing action output command file'); }
fs.cpSync(process.env.INPUT_PATH, path.join(process.env.DIAGNOSTIC_TEST_REMOTE, process.env.INPUT_NAME), { recursive: true });
fs.appendFileSync(process.env.GITHUB_OUTPUT, 'artifact-id=fixture\\n');
`);
        fs.writeFileSync(path.join(toolsRoot, 'Observe-TreeActionsProcesses.ps1'), `
param($OutputDirectory, $DcpDirectory, $StopFile, $MaximumSeconds)
$ErrorActionPreference = 'Stop'
if ($env:ACTIONS_RUNTIME_TOKEN -or $env:GH_TOKEN -or $env:GITHUB_TOKEN) { throw 'Observer inherited credentials.' }
@{state='running';heartbeatAtUtc=[DateTime]::UtcNow.ToString('o');dcpExecutablePresent=$true;counts=@{successfulSamples=1}} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'observer-status.json')
while (-not (Test-Path -LiteralPath $StopFile)) { Start-Sleep -Milliseconds 50 }
'{"state":"stopped"}' | Set-Content (Join-Path $OutputDirectory 'observer-status.json')
`);
        fs.writeFileSync(path.join(scripts, 'run-e2e.js'), `
const fs = require('node:fs');
const path = require('node:path');
if (process.env.ACTIONS_RUNTIME_TOKEN || process.env.GH_TOKEN || process.env.GITHUB_TOKEN) { throw new Error('Workload inherited credentials'); }
const remote = process.env.DIAGNOSTIC_TEST_REMOTE;
if (fs.readdirSync(remote).length !== 1) { throw new Error('Workload started before first checkpoint persisted'); }
const output = process.env.TREE_ACTIONS_DIAGNOSTICS_DIRECTORY;
fs.writeFileSync(path.join(output, 'lifecycle.jsonl'), JSON.stringify({ phase: 'cycle-complete', cycle: 1 }) + '\\n');
console.log('[tree-actions-diag] fixture complete');
`);
        process.env.ACTIONS_RUNTIME_TOKEN = 'fixture-token-not-a-real-credential';
        process.env.ACTIONS_RESULTS_URL = 'https://example.invalid/';
        process.env.DIAGNOSTIC_TEST_REMOTE = remote;
        process.env.ASPIRE_DCP_PATH = toolsRoot;
        const outputRoot = path.join(directory, 'results');
        const result = await runDiagnostics({
            extensionRoot, outputRoot, temporaryRoot: path.join(directory, 'runtime'),
            toolsRoot, nodePath: process.execPath, uploadActionPath,
            runnerIndex: 'test', runAttempt: '1', sourceSha: 'fixture-source', workflowSha: 'fixture-workflow',
        });

        assert.equal(result.runnerExitCode, 0);
        assert.equal(result.observerExitCode, 0);
        assert.deepEqual(result.diagnosticErrors, []);
        assert.ok(result.checkpointCount >= 2);
        const checkpoints = fs.readdirSync(remote).sort();
        assert.equal(checkpoints.length, result.checkpointCount);
        const first = JSON.parse(fs.readFileSync(path.join(remote, checkpoints[0], 'checkpoint.json'), 'utf8'));
        const last = JSON.parse(fs.readFileSync(path.join(remote, checkpoints.at(-1)!, 'checkpoint.json'), 'utf8'));
        assert.equal(first.reason, 'before-workload');
        assert.equal(last.reason, 'final');
        assert.equal(last.runnerExitCode, 0);
        assert.ok(fs.existsSync(path.join(outputRoot, 'observer.stop')));
    } finally {
        for (const [key, value] of Object.entries({
            ACTIONS_RUNTIME_TOKEN: previous.token,
            ACTIONS_RESULTS_URL: previous.url,
            DIAGNOSTIC_TEST_REMOTE: previous.remote,
            ASPIRE_DCP_PATH: previous.dcp,
        })) {
            if (value === undefined) {
                delete process.env[key];
            } else {
                process.env[key] = value;
            }
        }
        fs.rmSync(directory, { recursive: true, force: true });
    }
});

test('failed checkpoint preflight prevents starting any workload', { timeout: 10000 }, async () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-20103-upload-failure-'));
    const token = process.env.ACTIONS_RUNTIME_TOKEN;
    const url = process.env.ACTIONS_RESULTS_URL;
    try {
        process.env.ACTIONS_RUNTIME_TOKEN = 'fixture-token-not-a-real-credential';
        process.env.ACTIONS_RESULTS_URL = 'https://example.invalid/';
        const uploadActionPath = path.join(directory, 'upload.cjs');
        fs.writeFileSync(uploadActionPath, 'process.exit(7);\n');
        const result = await runDiagnostics({
            extensionRoot: path.join(directory, 'no-workload'),
            outputRoot: path.join(directory, 'results'),
            temporaryRoot: path.join(directory, 'runtime'),
            toolsRoot: path.join(directory, 'no-observer'),
            nodePath: process.execPath, uploadActionPath,
            runnerIndex: 'failure', runAttempt: '1', sourceSha: 'fixture', workflowSha: 'fixture',
        });
        assert.equal(result.runnerExitCode, null);
        assert.equal(result.observerExitCode, null);
        assert.equal(result.checkpointCount, 0);
        assert.ok(result.diagnosticErrors.some(message => message.includes('Remote checkpoint 1 failed with exit 7')));
        assert.ok(fs.existsSync(path.join(directory, 'results', 'observations', 'abort')));
    } finally {
        if (token === undefined) { delete process.env.ACTIONS_RUNTIME_TOKEN; } else { process.env.ACTIONS_RUNTIME_TOKEN = token; }
        if (url === undefined) { delete process.env.ACTIONS_RESULTS_URL; } else { process.env.ACTIONS_RESULTS_URL = url; }
        fs.rmSync(directory, { recursive: true, force: true });
    }
});
