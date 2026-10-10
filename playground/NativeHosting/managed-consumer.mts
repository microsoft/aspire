import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';

const url = process.env.NATIVE_URL;
assert.ok(url);
assert.equal(process.env.services__web__http__0, url);
const response = await fetch(`${url}/api/redis`, { signal: AbortSignal.timeout(15000) });
assert.equal(response.status, 200);
const result = await response.json();
assert.equal(result.commands, 3);
assert.equal(result.value, 'Nuxt read/write through an owner-resolved Redis reference');
assert.ok(process.env.PROBE_RESULT);
await writeFile(process.env.PROBE_RESULT, JSON.stringify({
    url, serviceDiscovery: process.env.services__web__http__0, redisRoundTrip: 'passed',
}));
