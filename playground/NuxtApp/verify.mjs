import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { setTimeout } from 'node:timers/promises';

const execute = promisify(execFile);
const [mode, target] = process.argv.slice(2);
let url;

if (mode === 'apphost') {
    const deadline = Date.now() + 240_000;
    while (Date.now() < deadline) {
        const { stdout } = await execute('aspire', [
            'describe', '--apphost', target, '--format', 'Json', '--nologo',
        ], { maxBuffer: 4 * 1024 * 1024 });
        const { resources } = JSON.parse(stdout);
        const workloads = resources.filter(resource => ['api', 'web'].includes(resource.displayName));
        for (const resource of workloads) {
            assert.ok(!['FailedToStart', 'Exited'].includes(resource.state),
                `${resource.displayName} entered ${resource.state}`);
        }
        if (workloads.length === 2 && workloads.every(resource =>
            resource.state === 'Running' && resource.healthStatus === 'Healthy')) {
            url = workloads.find(resource => resource.displayName === 'web').urls.find(endpoint => endpoint.name === 'http').url;
            break;
        }
        await setTimeout(2000);
    }
    assert.ok(url, 'Nuxt and the backend did not become healthy within four minutes.');
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
