import * as assert from 'assert';
import { BrowserLaunchConfiguration } from '../dcp/types';
import { executeE2eControlCommand, runE2eTeardown } from './helpers/fixtures';

// E2E coverage for the browser resource debugger.
//
// Actually launching a browser debug session in headless CI is not feasible: the js-debug
// Chrome/Edge adapters spawn a real GUI browser, and the `firefox` adapter additionally
// requires the firefox-devtools.vscode-firefox-debug extension, which is not present on the
// hosted runners. Instead these tests drive the *real* extension host through the
// `getResourceDebuggerExtensions` / `createResourceDebugConfiguration` control commands to
// prove the adapter-selection path end-to-end: that browser resources resolve to the correct
// built-in adapter, and that requesting Firefox on a stock VS Code (without the Firefox
// Debugger extension) surfaces the actionable install message rather than an opaque
// "debug session failed to start" failure. The launch/teardown lifecycle itself is covered by
// the in-process unit tests in src/test/browserDebugger.test.ts.
interface EmittedDebugConfiguration {
    type?: string;
    request?: string;
    url?: string;
    webRoot?: string;
    sourceMaps?: boolean;
    runtimeArgs?: string[];
}

interface RegisteredDebuggerExtension {
    resourceType?: string;
    debugAdapter?: string;
    extensionId?: string | null;
}

suite('Aspire browser debugger E2E', function () {
    this.timeout(120000);

    teardown(async () => {
        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'stopDebugging' }).catch(() => undefined),
        ], 'Browser debugger E2E teardown failed.');
    });

    test('registers the built-in browser debugger with the Edge adapter', async () => {
        const registered = (await executeE2eControlCommand({ name: 'getResourceDebuggerExtensions' })).result as RegisteredDebuggerExtension[];

        const browser = registered.find(extension => extension.resourceType === 'browser');
        assert.ok(browser, 'The browser resource debugger should always be registered.');
        assert.strictEqual(browser.debugAdapter, 'pwa-msedge');
        // Built into VS Code via js-debug, so no owning extension id.
        assert.strictEqual(browser.extensionId, null);
    });

    test('emits a js-debug Chrome configuration for a browser resource', async () => {
        const configuration = (await executeE2eControlCommand({
            name: 'createResourceDebugConfiguration',
            launchConfig: { type: 'browser', url: 'https://localhost:5001', web_root: '/workspace/app', browser: 'chrome' } as BrowserLaunchConfiguration,
        })).result as EmittedDebugConfiguration;

        assert.strictEqual(configuration.type, 'pwa-chrome');
        assert.strictEqual(configuration.request, 'launch');
        assert.strictEqual(configuration.url, 'https://localhost:5001');
        assert.strictEqual(configuration.webRoot, '/workspace/app');
        assert.strictEqual(configuration.sourceMaps, true);
    });

    test('defaults browser resources to the built-in Edge adapter', async () => {
        const configuration = (await executeE2eControlCommand({
            name: 'createResourceDebugConfiguration',
            launchConfig: { type: 'browser', url: 'https://localhost:5001' } as BrowserLaunchConfiguration,
        })).result as EmittedDebugConfiguration;

        assert.strictEqual(configuration.type, 'pwa-msedge');
    });

    test('surfaces an actionable error when the Firefox debug adapter is missing', async () => {
        // The hosted runner does not have firefox-devtools.vscode-firefox-debug installed, so the
        // adapter-selection guard must reject with the actionable install message rather than
        // emitting a `firefox` configuration that VS Code cannot start.
        await assert.rejects(
            executeE2eControlCommand({
                name: 'createResourceDebugConfiguration',
                launchConfig: { type: 'browser', url: 'https://localhost:5001', browser: 'firefox' } as BrowserLaunchConfiguration,
            }),
            /Firefox Debugger extension/);
    });
});
