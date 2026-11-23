import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout } from 'node:timers/promises';

const execute = promisify(execFile);
const [mode, target] = process.argv.slice(2);
// Tests shorten this to exercise the timeout path; CI uses the four-minute default.
const appHostTimeoutMs = Number(process.env.NUXT_VERIFY_APPHOST_TIMEOUT_MS ?? 240_000);
let url;

if (mode === 'apphost') {
    try {
        url = await waitForHealthyAppHost(target);
    } catch (error) {
        await writeAppHostDiagnostics(target);
        throw error;
    }
} else if (mode === 'url' || mode === 'health') {
    url = target;
} else {
    throw new Error('Usage: node verify.mjs apphost <apphost.mts> | url <http://host:port> | health <http://host:port/health>');
}

const healthUrl = mode === 'health' ? url : `${url}/api/health`;
const deadline = Date.now() + 60_000;
let response;
while (Date.now() < deadline) {
    // A newly launched container can accept execs before its server is listening.
    // Retry only transport failures; HTTP error responses must fail verification.
    try {
        response = await fetch(healthUrl, { signal: AbortSignal.timeout(5000) });
        break;
    } catch (error) {
        if (!(error instanceof TypeError) || Date.now() + 1000 >= deadline) {
            throw error;
        }
        await setTimeout(1000);
    }
}
assert.ok(response, `Health endpoint ${healthUrl} did not begin listening within one minute.`);
assert.equal(response.status, 200);
assert.deepEqual(await response.json(), { status: 'ok' });

if (mode === 'health') {
    console.log('Verified healthy backend.');
} else {
    const message = await fetch(`${url}/api/message`, { signal: AbortSignal.timeout(10_000) });
    assert.equal(message.status, 200);
    assert.deepEqual(await message.json(), { message: 'Hello from the Aspire backend' });

    const page = await fetch(url, { signal: AbortSignal.timeout(10_000) });
    assert.equal(page.status, 200);
    assert.match(await page.text(), /<p>Hello from the Aspire backend<\/p>/);
    console.log('Verified healthy Nuxt server, runtime service reference, and server-rendered backend response.');
}

async function waitForHealthyAppHost(apphost) {
    const deadline = Date.now() + appHostTimeoutMs;
    while (Date.now() < deadline) {
        const { stdout } = await execute('aspire', [
            'describe', '--apphost', apphost, '--format', 'Json', '--nologo',
        ], { maxBuffer: 4 * 1024 * 1024 });
        const { resources } = JSON.parse(stdout);
        const workloads = resources.filter(resource => ['api', 'web'].includes(resource.displayName));
        for (const resource of workloads) {
            assert.ok(!['FailedToStart', 'Exited'].includes(resource.state),
                `${resource.displayName} entered ${resource.state}`);
        }
        if (workloads.length === 2 && workloads.every(resource =>
            resource.state === 'Running' && resource.healthStatus === 'Healthy')) {
            return workloads.find(resource => resource.displayName === 'web').urls.find(endpoint => endpoint.name === 'http').url;
        }
        await setTimeout(2000);
    }
    assert.fail(`Nuxt and the backend did not become healthy within ${appHostTimeoutMs / 1000} seconds.`);
}

// The AppHost is stopped and its workspace deleted after a failure, so print the state that
// explains why a resource never became healthy. Environment, properties and dashboard URLs
// are omitted because they can contain secrets or login tokens.
async function writeAppHostDiagnostics(apphost) {
    console.error('=== AppHost resource diagnostics ===');
    let resources;
    try {
        const { stdout } = await execute('aspire', [
            'describe', '--apphost', apphost, '--format', 'Json', '--include-hidden', '--nologo',
        ], { maxBuffer: 4 * 1024 * 1024, timeout: 60_000 });
        resources = JSON.parse(stdout).resources;
    } catch (error) {
        console.error(`Could not describe AppHost resources: ${error.message}`);
        return;
    }

    for (const resource of resources) {
        console.error(JSON.stringify({
            name: resource.name,
            type: resource.resourceType,
            state: resource.state,
            waitingFor: resource.waitingFor,
            exitCode: resource.exitCode,
            health: resource.healthStatus,
            healthReports: resource.healthReports,
            started: resource.startTimestamp,
            stopped: resource.stopTimestamp,
        }));
    }

    for (const resource of resources) {
        console.error(`=== Last 200 log lines for ${resource.name} ===`);
        try {
            const { stdout, stderr } = await execute('aspire', [
                'logs', resource.name, '--apphost', apphost, '--tail', '200', '--timestamps', '--nologo',
            ], { maxBuffer: 16 * 1024 * 1024, timeout: 60_000 });
            process.stderr.write(stdout);
            process.stderr.write(stderr);
        } catch (error) {
            console.error(`Could not read logs for ${resource.name}: ${error.message}`);
        }
    }
}
