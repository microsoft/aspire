import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { connect, createNativeBuilder } from './.aspire/modules/aspire.mjs';
import { CancellationToken } from './.aspire/modules/transport.mjs';

const directory = process.env.NATIVE_HOSTING_GUEST_DIRECTORY;
if (!directory) throw new Error('NATIVE_HOSTING_GUEST_DIRECTORY is required.');
const builder = await createNativeBuilder(await connect());
const cache = await builder.addRedis('cache');
const postgres = await builder.addPostgres('postgres');
const database = await postgres.addDatabase('app-db', 'native_app');
const web = await builder.addNuxt('web', join(directory, 'web'), cache);
const tunnel = await builder.addDevTunnel('tunnel', web, directory, true);
await builder.run(new CancellationToken());

assert.equal(await cache.redisCommand('PING', '', '', false, new CancellationToken()), 'PONG');
assert.equal(await database.query('SELECT 1', false, new CancellationToken()), '1');
const webUrl = await web.resolve('url', 'run', 'host', new CancellationToken());
const tunnelUrl = await tunnel.resolve('url', 'run', 'host', new CancellationToken());
assert.equal((await (await fetch(new URL('/api/health', webUrl))).json()).status, 'healthy');
assert.equal((await (await fetch(new URL('/api/health', tunnelUrl))).json()).status, 'healthy');
const stats = await builder.stats(new CancellationToken());
assert.equal(stats.dynamicCodeSupported, false);
const result = {
    realAspireRun: true, generatedSdk: true, realDcp: true, stats, webUrl, tunnelUrl,
    containerIds: [(await cache.status(new CancellationToken())).containerId,
        (await postgres.status(new CancellationToken())).containerId],
    liveRelayValidated: false,
};
await writeFile(join(directory, 'cli-result.json'), JSON.stringify(result, null, 2));
console.log(`Native AppHost ready: ${webUrl}; tunnel fixture: ${tunnelUrl}`);

// Run starts the graph rather than blocking for its lifetime. SDK sockets can
// be unreferenced, so a pending Promise alone lets Node exit with code 13.
// Keep a referenced timer until CLI shutdown; guest EOF disposes this generation.
while (true) await delay(60000);
