import { execFile } from 'node:child_process';
import { chmod, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { delimiter, join } from 'node:path';
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

describe('Nuxt AppHost readiness diagnostics', () => {
    const fakeAspire = `#!/usr/bin/env node
const { resources, logs } = JSON.parse(require('node:fs').readFileSync(process.env.FAKE_ASPIRE_STATE, 'utf8'));
const [command, resource] = process.argv.slice(2);
if (command === 'describe') {
    const visible = process.argv.includes('--include-hidden') ? resources : resources.filter(r => !r.hidden);
    process.stdout.write(JSON.stringify({ resources: visible }));
} else if (command === 'logs') {
    process.stdout.write(logs[resource] ?? '');
} else {
    process.exit(2);
}
`;

    async function verifyAppHost(resources: object[], logs: Record<string, string>) {
        const directory = await mkdtemp(join(tmpdir(), 'nuxt-verify-'));
        try {
            const aspire = join(directory, 'aspire');
            await writeFile(aspire, fakeAspire);
            await chmod(aspire, 0o755);
            const state = join(directory, 'state.json');
            await writeFile(state, JSON.stringify({ resources, logs }));
            return await execute(process.execPath, [verifier, 'apphost', 'apphost.mts'], {
                timeout: 10_000,
                env: {
                    ...process.env,
                    PATH: `${directory}${delimiter}${process.env.PATH}`,
                    FAKE_ASPIRE_STATE: state,
                    NUXT_VERIFY_APPHOST_TIMEOUT_MS: '500',
                },
            }).then(() => { throw new Error('verify.mjs unexpectedly succeeded.'); }, (error: { code: number; stderr: string }) => error);
        } finally {
            await rm(directory, { recursive: true, force: true });
        }
    }

    const api = { name: 'api', displayName: 'api', state: 'Running', healthStatus: 'Healthy' };

    it('reports every resource state and log when the workloads never become healthy', async () => {
        const { code, stderr } = await verifyAppHost([
            api,
            {
                name: 'web', displayName: 'web', state: 'Waiting', waitingFor: ['web-installer'],
                environment: { NUXT_SECRET: 'environment-secret' }, dashboardUrl: 'https://localhost/login?t=dashboard-secret',
            },
            { name: 'web-installer', displayName: 'web-installer', state: 'Running', hidden: true },
        ], {
            'web-installer': 'npm install: still fetching nuxt\n',
        });

        expect(code).toBe(1);
        expect(stderr).toContain('Nuxt and the backend did not become healthy within 0.5 seconds.');
        expect(stderr).toContain('"name":"web","state":"Waiting","waitingFor":["web-installer"]');
        expect(stderr).toContain('"name":"web-installer","state":"Running"');
        expect(stderr).toContain('=== Last 200 log lines for web-installer ===\nnpm install: still fetching nuxt');
        expect(stderr).not.toContain('environment-secret');
        expect(stderr).not.toContain('dashboard-secret');
    });

    it('reports resource logs when a workload fails to start', async () => {
        const { code, stderr } = await verifyAppHost([
            api,
            { name: 'web', displayName: 'web', state: 'FailedToStart', exitCode: 1 },
        ], {
            web: 'Error: Cannot find module nuxt\n',
        });

        expect(code).toBe(1);
        expect(stderr).toContain('web entered FailedToStart');
        expect(stderr).toContain('"name":"web","state":"FailedToStart","exitCode":1');
        expect(stderr).toContain('=== Last 200 log lines for web ===\nError: Cannot find module nuxt');
    });
});
