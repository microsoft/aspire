import * as assert from 'assert';
import type { BrowserLaunchConfiguration } from '../dcp/types';
import { executeE2eControlCommand } from './helpers/fixtures';
import { openAspireView } from './helpers/vscode';

const managedChromiumRuntimeArguments = [
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-background-mode',
];

suite('Aspire browser debugger E2E', function () {
    this.timeout(120000);

    suiteSetup(async () => {
        await openAspireView();
    });

    test('uses the managed Edge configuration when the browser is omitted', async () => {
        const configuration = await createBrowserDebugConfiguration();

        assertManagedBrowserConfiguration(configuration, 'pwa-msedge');
    });

    test('uses the managed Chrome configuration when Chrome is explicit', async () => {
        const configuration = await createBrowserDebugConfiguration('chrome');

        assertManagedBrowserConfiguration(configuration, 'pwa-chrome');
    });

    test('rejects Firefox with the localized supported-browser list', async () => {
        const url = 'https://browser-debugger.test/firefox';
        const expectedMessage = `Browser 'firefox' cannot be debugged for '${url}'. Supported browsers are: msedge, chrome.`;
        const launchConfig: BrowserLaunchConfiguration = {
            type: 'browser',
            browser: 'firefox',
            url,
        };

        await assert.rejects(
            executeE2eControlCommand({
                name: 'createResourceDebugConfiguration',
                launchConfig,
            }),
            (error: unknown) => {
                assert.ok(error instanceof Error);
                assert.ok(
                    error.message.includes(expectedMessage),
                    'Expected the E2E control helper to reject with the localized unsupported-browser message.');

                return true;
            });
    });
});

async function createBrowserDebugConfiguration(browser?: string): Promise<BrowserDebugConfiguration> {
    const launchConfig: BrowserLaunchConfiguration = {
        type: 'browser',
        url: 'https://browser-debugger.test/chromium',
        ...(browser === undefined ? {} : { browser }),
    };
    const status = await executeE2eControlCommand({
        name: 'createResourceDebugConfiguration',
        launchConfig,
        debuggers: {
            browser: {
                resourceType: 'workspace-resource',
                runId: 'workspace-run',
                debugSessionId: 'workspace-debug-session',
                userDataDir: 'workspace-profile',
                runtimeArgs: [
                    '--User-Data-Dir=workspace-combined-profile',
                    '--USER-DATA-DIR',
                    'workspace-split-profile',
                ],
            },
        },
    });

    return status.result as BrowserDebugConfiguration;
}

function assertManagedBrowserConfiguration(configuration: BrowserDebugConfiguration, expectedType: string): void {
    assert.strictEqual(configuration.type, expectedType);
    assert.strictEqual(configuration.request, 'launch');
    assert.strictEqual(configuration.resourceType, 'browser');
    assert.strictEqual(configuration.runId, 'e2e-resource-debug-configuration');
    assert.strictEqual(configuration.debugSessionId, 'e2e-debug-session');
    assert.strictEqual(configuration.userDataDir, true);
    assert.deepStrictEqual(configuration.runtimeArgumentSummary, managedChromiumRuntimeArguments);
}

interface BrowserDebugConfiguration {
    type: string;
    request: string;
    resourceType: string;
    runId: string;
    debugSessionId: string;
    userDataDir: boolean;
    runtimeArgumentSummary: string[];
}
