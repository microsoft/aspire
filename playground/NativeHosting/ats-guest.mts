import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import assert from 'node:assert/strict';
import { connect, createNativeBuilder } from './generated/aspire.mjs';
import { buildAppHost, exerciseAppHost } from './ats-apphost.mts';

const directory = process.env.NATIVE_HOSTING_GUEST_DIRECTORY;
if (!directory) throw new Error('NATIVE_HOSTING_GUEST_DIRECTORY is required.');
const client = await connect();
const generation = process.env.NATIVE_HOSTING_GUEST_GENERATION ?? 'standalone';
if (process.env.NATIVE_HOSTING_STALE_HANDLES) {
    const handles = JSON.parse(await readFile(process.env.NATIVE_HOSTING_STALE_HANDLES, 'utf8'));
    await assert.rejects(client.invokeCapability('NativeHosting/status', { context: handles[1] }), /stale|unknown/);
}
const builder = await createNativeBuilder(client);
const graph = await buildAppHost(builder, directory, generation);
const forged = { ...graph.cache.toJSON(), $type: 'NativeHosting.Ats/NativeHosting.NativeBuilder' };
await assert.rejects(client.invokeCapability('NativeHosting/status', { context: forged }), /type/);
await assert.rejects(graph.tunnel.updateCustom({
    generation: 1, revision: 1, state: 'Healthy', endpoints: { tunnel: 'http://127.0.0.1:1/' },
}, new AbortController().signal), /registered controller/);
const result = await exerciseAppHost(graph, directory, generation);
await writeFile(join(directory, 'ats-result.json'), JSON.stringify(result, null, 2));
console.log('ATS guest graph exercised.');
