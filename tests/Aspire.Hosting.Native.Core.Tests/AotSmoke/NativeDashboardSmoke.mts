// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { chromium, type Browser, type Page } from 'playwright';

/** Runs the actual Dashboard against the published native resource-service endpoint. */
export class NativeDashboardSmoke {
    private constructor(
        private readonly process: ReturnType<typeof spawn>,
        private readonly browser: Browser,
        readonly page: Page
    ) { }

    static async start(dashboardDll: string, resourceService: string, apiKey: string): Promise<NativeDashboardSmoke> {
        const dashboard = spawn('dotnet', [dashboardDll], {
            env: {
                ...process.env,
                ASPNETCORE_URLS: 'http://127.0.0.1:0',
                ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL: 'http://127.0.0.1:0',
                ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS: 'true',
                ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL: resourceService,
                DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE: 'ApiKey',
                DASHBOARD__RESOURCESERVICECLIENT__APIKEY: apiKey
            },
            stdio: ['ignore', 'pipe', 'pipe']
        });
        dashboard.stderr?.pipe(process.stderr);
        let browser: Browser | undefined;
        const startupTimeout = setTimeout(() => dashboard.kill('SIGTERM'), 30_000);
        try {
            const lines = createInterface({ input: dashboard.stdout! });
            const address = await Promise.race([
                (async () => {
                    for await (const line of lines) {
                        const match = /Now listening on: (http:\/\/127\.0\.0\.1:\d+)/.exec(line);
                        if (match) return match[1]!;
                    }
                    throw new Error('Dashboard did not publish its frontend address.');
                })(),
                once(dashboard, 'exit').then(([code]) => { throw new Error(`Dashboard startup failed (${code}).`); })
            ]);
            lines.close();
            dashboard.stdout?.pipe(process.stderr);
            browser = await chromium.launch({ headless: true });
            const page = await browser.newPage();
            page.on('pageerror', error => console.error(`Dashboard browser: ${error.message}`));
            await page.goto(address);
            await page.getByRole('heading', { name: 'Resources', exact: true }).waitFor();
            clearTimeout(startupTimeout);
            return new NativeDashboardSmoke(dashboard, browser, page);
        } catch (error) {
            try {
                await browser?.close();
            } finally {
                if (dashboard.exitCode === null && dashboard.signalCode === null) {
                    const exit = once(dashboard, 'exit');
                    dashboard.kill('SIGTERM');
                    const deadline = setTimeout(() => dashboard.kill('SIGKILL'), 10_000);
                    try {
                        await exit;
                    } finally {
                        clearTimeout(deadline);
                    }
                }
            }
            throw error;
        } finally {
            clearTimeout(startupTimeout);
        }
    }

    async confirm(): Promise<void> {
        try {
            await this.page.getByText('Keep the initialized database running?', { exact: true }).waitFor({ timeout: 30_000 });
            await this.page.getByText('Continue', { exact: true }).click();
        } catch (error) {
            console.error(await this.page.locator('body').innerText());
            throw error;
        }
    }

    async verifyResources(): Promise<void> {
        for (const name of ['cache', 'database', 'tunnel']) {
            const row = this.page.getByRole('row').filter({ hasText: name });
            await row.waitFor({ timeout: 30_000 });
            await row.getByText('Running', { exact: true }).waitFor({ timeout: 30_000 });
            assert.match(await row.innerText(), /Running/);
        }
        await this.page.screenshot({ path: 'artifacts/native-hosting/native-dashboard.png', fullPage: true });
    }

    async dispose(): Promise<void> {
        try {
            await this.browser.close();
        } finally {
            if (this.process.exitCode === null && this.process.signalCode === null) {
                const exit = once(this.process, 'exit');
                this.process.kill('SIGTERM');
                const deadline = setTimeout(() => this.process.kill('SIGKILL'), 10_000);
                try {
                    await exit;
                } finally {
                    clearTimeout(deadline);
                }
            }
        }
    }
}
