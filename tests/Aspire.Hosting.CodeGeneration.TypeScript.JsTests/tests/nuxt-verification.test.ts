import { execFile } from 'node:child_process';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { describe, expect, it } from 'vitest';

const execute = promisify(execFile);
const verifier = fileURLToPath(new URL('../../../playground/NuxtApp/verify.mjs', import.meta.url));

describe('Nuxt production backend readiness', () => {
    it.each([
        { name: 'accepts a healthy backend', status: 200, disconnect: false, attempts: 1 },
        { name: 'retries transport failures before the backend is ready', status: 200, disconnect: true, attempts: 2 },
        { name: 'fails immediately on an unhealthy HTTP response', status: 503, disconnect: false, attempts: 1 },
    ])('$name', async ({ status, disconnect, attempts }) => {
        const requests: string[] = [];
        const server = createServer((request, response) => {
            requests.push(request.url ?? '');
            if (disconnect && requests.length === 1) {
                request.socket.destroy();
                return;
            }
            response.writeHead(status, { 'Content-Type': 'application/json' });
            response.end(JSON.stringify({ status: 'ok' }));
        });
        await new Promise<void>((resolve, reject) => {
            server.once('error', reject);
            server.listen(0, '127.0.0.1', resolve);
        });

        try {
            const address = server.address();
            if (address === null || typeof address === 'string') {
                throw new Error('The test backend did not bind a TCP endpoint.');
            }
            const verification = execute(process.execPath, [
                verifier, 'health', `http://127.0.0.1:${address.port}/health`,
            ], { timeout: 10_000 });
            if (status === 200) {
                const { stdout, stderr } = await verification;
                expect(stdout).toBe('Verified healthy backend.\n');
                expect(stderr).toBe('');
            } else {
                await expect(verification).rejects.toMatchObject({ code: 1 });
            }
            expect(requests).toEqual(Array(attempts).fill('/health'));
        } finally {
            await new Promise<void>((resolve, reject) => {
                server.close(error => error ? reject(error) : resolve());
                server.closeAllConnections();
            });
        }
    });
});
