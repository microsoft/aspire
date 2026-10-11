// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFile, unlink } from 'node:fs/promises';
import { resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import type { StartedWorkload } from './aspire.mjs';
import { randomBytes } from 'node:crypto';
import { NativeDashboardSmoke } from './NativeDashboardSmoke.mjs';

const [cliPath, serverPath, workspace] = process.argv.slice(2);
assert.ok(cliPath && serverPath && workspace && process.env.ASPIRE_NATIVE_DCP_PATH);
const resultsPath = resolve(workspace, 'cli-apphost-proof.json');
const dashboardKey = randomBytes(32).toString('hex');
try {
    await unlink(resultsPath);
} catch (error) {
    if (!(error && typeof error === 'object' && 'code' in error && error.code === 'ENOENT')) throw error;
}
const cli = spawn(cliPath, ['run', '--apphost', 'apphost.mts', '--non-interactive', '--nologo', '--log-level', 'Debug'], {
    cwd: workspace,
    env: {
        ...process.env, ASPIRE_CLI_NATIVE_APPHOST_SERVER: serverPath, ASPIRE_NATIVE_CLI_RESULTS_PATH: resultsPath,
        ...(process.env.ASPIRE_NATIVE_DASHBOARD_DLL ? {
            ASPIRE_NATIVE_DASHBOARD_PORT: '0', ASPIRE_NATIVE_DASHBOARD_API_KEY: dashboardKey,
            ASPIRE_DASHBOARD_PATH: process.env.ASPIRE_NATIVE_DASHBOARD_DLL,
            AppHost__BrowserToken: dashboardKey
        } : {})
    },
    stdio: ['ignore', 'pipe', 'pipe']
});
cli.stdout!.pipe(process.stderr);
cli.stderr!.pipe(process.stderr);
let proof: { instances: { cache: StartedWorkload; database: StartedWorkload; relay: StartedWorkload } } | undefined;
let exitCode: number | null = null;
let dashboard: NativeDashboardSmoke | undefined;
let browserWork: Promise<void> | undefined;
let browserFailure: unknown;
let serverPid: number | undefined;
let dashboardPid: number | undefined;
let buffered = '';
function observeCliOutput(chunk: Buffer): void {
    // CLI diagnostics can arrive on either stream, for example:
    // [dbug] NativeAppHostServerProject: Native AppHost stdout:
    // DCP-owned Dashboard (1234) listening on http://127.0.0.1:50123/.
    // Retain a bounded suffix because a diagnostic line may span chunks.
    buffered += String(chunk);
    const pid = /Native\.Server\((\d+)\) started/.exec(buffered);
    if (pid) serverPid = Number(pid[1]);
    const address = /DCP-owned Dashboard \((\d+)\) listening on (http:\/\/(?:127\.0\.0\.1|localhost):\d+\/)\./.exec(buffered);
    if (address && process.env.ASPIRE_NATIVE_DASHBOARD_DLL && !browserWork) {
        dashboardPid = Number(address[1]);
        const frontend = address[2]!;
        browserWork = (async () => {
            dashboard = await NativeDashboardSmoke.attach(`${frontend}login?t=${dashboardKey}`);
            await dashboard.confirm();
            await dashboard.verifyResources();
        })().catch(error => { browserFailure = error; });
    }
    if (buffered.length > 64 * 1024) buffered = buffered.slice(-32 * 1024);
}
cli.stdout!.on('data', observeCliOutput);
cli.stderr!.on('data', observeCliOutput);
let nativeResidentBytes: number | undefined;
try {
    const deadline = Date.now() + 90_000;
    while (!proof) {
        if (cli.exitCode !== null || cli.signalCode !== null) throw new Error(`aspire run exited before workload readiness (${cli.exitCode}).`);
        if (browserFailure) throw browserFailure;
        try {
            proof = JSON.parse(await readFile(resultsPath, 'utf8')) as typeof proof;
        } catch (error) {
            if (!(error && typeof error === 'object' && 'code' in error && error.code === 'ENOENT')) throw error;
        }
        if (Date.now() >= deadline) throw new Error('aspire run did not initialize the native workloads.');
        if (!proof) await delay(100);
    }
    if (process.env.ASPIRE_NATIVE_DASHBOARD_DLL) {
        assert.ok(browserWork, 'CLI must publish the native Dashboard adapter address.');
        await browserWork;
        if (browserFailure) throw browserFailure;
    }
    if (serverPid) {
        nativeResidentBytes = Number(execFileSync('ps', ['-o', 'rss=', '-p', String(serverPid)], { encoding: 'utf8' }).trim()) * 1024;
    }
    // Workload proof precedes the CLI's independent readiness observation.
    await delay(1000);
} finally {
    try {
        await browserWork;
        await dashboard?.dispose();
    } finally {
        if (cli.exitCode === null && cli.signalCode === null) {
            const exited = once(cli, 'exit');
            cli.kill('SIGINT');
            const timeout = setTimeout(() => cli.kill('SIGKILL'), 30_000);
            try {
                [exitCode] = await exited as [number | null];
            } finally {
                clearTimeout(timeout);
            }
        }
    }
}
assert.ok(proof);
assert.ok(serverPid, 'The real CLI must report its native server process identity.');
assert.throws(() => process.kill(serverPid!, 0), (error: unknown) =>
    error instanceof Error && 'code' in error && error.code === 'ESRCH');
assert.ok(exitCode === 0 || exitCode === 130, `Unexpected CLI shutdown exit code: ${exitCode}.`);
for (const endpoint of [proof.instances.cache, proof.instances.database]) {
    assert.ok(endpoint.instanceId);
    assert.equal(execFileSync('docker', ['ps', '-a', '--filter', `id=${endpoint.instanceId}`, '--format', '{{.ID}}'],
        { encoding: 'utf8' }).trim(), '');
}
assert.ok(proof.instances.relay.instanceId);
if (process.env.ASPIRE_NATIVE_DASHBOARD_DLL) {
    assert.ok(dashboardPid, 'DCP must report the actual Dashboard process identity.');
    assert.throws(() => process.kill(dashboardPid!, 0), (error: unknown) =>
        error instanceof Error && 'code' in error && error.code === 'ESRCH');
}
assert.throws(() => process.kill(Number(proof.instances.relay.instanceId), 0), (error: unknown) =>
    error instanceof Error && 'code' in error && error.code === 'ESRCH');
console.log(JSON.stringify({
    actualAspireRun: 'passed', publishedNativeServer: 'passed', cliGeneratedSdk: 'passed',
    separateIntegrationProcess: 'passed', actualWorkloadsAndCommand: 'passed', ownedWorkloadCleanup: 'passed',
    actualDashboardResourcesAndConfirmation: dashboard ? 'passed' : 'not exercised', nativeResidentBytes
}, null, 2));
