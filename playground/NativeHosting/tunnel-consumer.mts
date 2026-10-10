import { writeFile } from 'node:fs/promises';

const endpoint = process.env.TUNNEL_URL;
const result = process.env.TUNNEL_RESULT;
if (!endpoint || !result) throw new Error('Tunnel consumer requires deferred URL and result path.');
const response = await fetch(new URL('api/health', endpoint), {
    headers: { 'X-Tunnel-Skip-AntiPhishing-Page': 'true' }, signal: AbortSignal.timeout(15000),
});
const body = await response.json();
if (!response.ok || body.status !== 'healthy') throw new Error('Tunnel did not forward the native Nuxt health endpoint.');
await writeFile(result, JSON.stringify({ endpoint, status: response.status, body }));
// Keep the executable observable until DCP owns its shutdown.
setInterval(() => {}, 1000);
