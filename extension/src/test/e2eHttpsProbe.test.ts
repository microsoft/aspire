import * as assert from 'assert';
import * as fs from 'fs';
import * as http from 'http';
import * as https from 'https';
import type { AddressInfo } from 'net';
import * as os from 'os';
import * as path from 'path';
import { waitForHttpText } from '../test-e2e/helpers/assertions';
import { createSelfSignedCertAsync } from '../utils/security';

suite('E2E HTTPS probes', function () {
    this.timeout(120000);

    const responseText = 'Aspire HTTPS probe';
    const devCertEnvironmentName = 'ASPIRE_EXTENSION_E2E_DEV_CERT_PATH';
    let directory: string;
    let serverCertificate: Awaited<ReturnType<typeof createSelfSignedCertAsync>>;
    let otherCertificate: Awaited<ReturnType<typeof createSelfSignedCertAsync>>;
    let serverCertificatePath: string;
    let previousDevCertPath: string | undefined;
    let server: https.Server | undefined;

    suiteSetup(async () => {
        directory = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'aspire-e2e-https-'));
        [serverCertificate, otherCertificate] = await Promise.all([createSelfSignedCertAsync(), createSelfSignedCertAsync()]);
        serverCertificatePath = path.join(directory, 'server.pem');
        fs.writeFileSync(serverCertificatePath, serverCertificate.cert);
    });

    suiteTeardown(() => {
        fs.rmSync(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    });

    setup(() => {
        previousDevCertPath = process.env[devCertEnvironmentName];
        delete process.env[devCertEnvironmentName];
    });

    teardown(async () => {
        if (previousDevCertPath === undefined) {
            delete process.env[devCertEnvironmentName];
        }
        else {
            process.env[devCertEnvironmentName] = previousDevCertPath;
        }

        const runningServer = server;
        server = undefined;
        if (runningServer) {
            runningServer.closeAllConnections();
            await new Promise<void>(resolve => runningServer.close(() => resolve()));
        }
    });

    test('rejects a self-signed certificate when no development certificate was exported', async () => {
        const port = await startServer(serverCertificate);

        await assert.rejects(
            waitForHttpText(`https://localhost:${port}/`, responseText, 1000),
            /Last error: self[- ]signed certificate/);
    });

    test('trusts the development certificate exported by the runner', async () => {
        const port = await startServer(serverCertificate);
        process.env[devCertEnvironmentName] = serverCertificatePath;

        assert.strictEqual(await waitForHttpText(`https://localhost:${port}/`, responseText, 10000), responseText);
    });

    test('follows redirects between HTTPS endpoints that present the exported certificate', async () => {
        const port = await startServer(serverCertificate, (request, response) => {
            if (request.url === '/login') {
                response.writeHead(302, { location: '/home', 'set-cookie': 'session=1; path=/' });
                response.end();
                return;
            }

            response.writeHead(200, { 'content-type': 'text/plain' });
            response.end(request.headers.cookie === 'session=1' ? responseText : 'missing cookie');
        });
        process.env[devCertEnvironmentName] = serverCertificatePath;

        assert.strictEqual(await waitForHttpText(`https://localhost:${port}/login`, responseText, 10000), responseText);
    });

    test('rejects a server that presents a different certificate than the exported one', async () => {
        const port = await startServer(otherCertificate);
        process.env[devCertEnvironmentName] = serverCertificatePath;

        await assert.rejects(
            waitForHttpText(`https://localhost:${port}/`, responseText, 1000),
            /Last error: (self[- ]signed certificate|unable to verify the first certificate)/);
    });

    test('still verifies the host name against the exported certificate', async () => {
        const port = await startServer(serverCertificate);
        process.env[devCertEnvironmentName] = serverCertificatePath;

        await assert.rejects(
            waitForHttpText(`https://127.0.0.1:${port}/`, responseText, 1000),
            /Last error: .*does not match certificate's altnames/);
    });

    test('reports a missing exported certificate file instead of falling back to unverified requests', async () => {
        const port = await startServer(serverCertificate);
        process.env[devCertEnvironmentName] = path.join(directory, 'missing.pem');

        await assert.rejects(
            waitForHttpText(`https://localhost:${port}/`, responseText, 1000),
            /Last error: ENOENT/);
    });

    async function startServer(
        certificate: { key: string; cert: string },
        handler: http.RequestListener = (_request, response) => {
            response.writeHead(200, { 'content-type': 'text/plain' });
            response.end(responseText);
        }): Promise<number> {
        const listeningServer = https.createServer({ key: certificate.key, cert: certificate.cert }, handler);
        server = listeningServer;
        await new Promise<void>((resolve, reject) => {
            listeningServer.once('error', reject);
            listeningServer.listen(0, () => resolve());
        });

        return (listeningServer.address() as AddressInfo).port;
    }
});
