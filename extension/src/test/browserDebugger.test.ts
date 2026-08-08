import * as assert from 'assert';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { browserDebuggerExtension } from '../debugger/languages/browser';
import { AspireResourceExtendedDebugConfiguration, BrowserLaunchConfiguration } from '../dcp/types';
import { unsupportedBrowserDebugTarget } from '../loc/strings';

suite('Browser Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    async function createConfiguration(launchConfig: BrowserLaunchConfiguration): Promise<AspireResourceExtendedDebugConfiguration> {
        const debugConfig = createDebugConfig();
        await browserDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, ['--ignored'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        return debugConfig;
    }

    test('defaults to the built-in js-debug Edge adapter', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173' });

        assert.strictEqual(debugConfig.type, 'pwa-msedge');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.url, 'http://localhost:5173');
        assert.strictEqual(debugConfig.sourceMaps, true);
        assert.deepStrictEqual(debugConfig.resolveSourceMapLocations, ['**', '!**/node_modules/**']);
        assert.strictEqual(debugConfig.userDataDir, true);
    });

    test('maps chrome to the built-in js-debug Chrome adapter', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173', browser: 'chrome' });

        assert.strictEqual(debugConfig.type, 'pwa-chrome');
    });

    test('drops process launch properties that browser debugging cannot use', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173' });

        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
        assert.strictEqual(debugConfig.cwd, undefined);
    });

    test('forwards a web root when the AppHost supplies one', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173', web_root: '/workspace/frontend/src' });

        assert.strictEqual(debugConfig.webRoot, '/workspace/frontend/src');
    });

    // js-debug resolves source maps against any non-empty webRoot, so a whitespace-only value is
    // just as invalid a source-map root as an empty one - it is only truthy.
    for (const blankWebRoot of ['', '   ', '\t', '\n', ' \t\r\n ']) {
        test(`omits a blank web root ${JSON.stringify(blankWebRoot)} instead of forwarding it to js-debug`, async () => {
            const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173', web_root: blankWebRoot });

            assert.strictEqual('webRoot' in debugConfig, false);
        });
    }

    test('forwards the trimmed web root so the validated value is the one js-debug receives', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173', web_root: '  /workspace/frontend/src\t' });

        assert.strictEqual(debugConfig.webRoot, '/workspace/frontend/src');
    });

    test('omits the web root when the AppHost does not send one', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173' });

        assert.strictEqual('webRoot' in debugConfig, false);
    });

    test('rejects a browser that has no built-in js-debug adapter', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'browser', url: 'http://localhost:5173', browser: 'firefox' }),
            new RegExp(escapeForRegExp(unsupportedBrowserDebugTarget('firefox', 'msedge, chrome'))));
    });

    // The hosting side's WithBrowserDebugger accepts an arbitrary string, so the allowlist lookup must
    // not resolve inherited Object.prototype members. A plain object literal would hand back a
    // function for these names and assign it to debugConfiguration.type.
    for (const inheritedMember of ['toString', 'constructor', '__proto__', 'hasOwnProperty', 'valueOf']) {
        test(`rejects '${inheritedMember}' instead of resolving it through Object.prototype`, async () => {
            await assert.rejects(
                () => createConfiguration({ type: 'browser', url: 'http://localhost:5173', browser: inheritedMember }),
                new RegExp(escapeForRegExp(unsupportedBrowserDebugTarget(inheritedMember, 'msedge, chrome'))));
        });
    }

    test('leaves the debug type untouched when the browser is not on the allowlist', async () => {
        const debugConfig = createDebugConfig();
        const launchConfig: BrowserLaunchConfiguration = { type: 'browser', url: 'http://localhost:5173', browser: '__proto__' };

        await assert.rejects(() => browserDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['--ignored'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig));

        assert.strictEqual(debugConfig.type, 'browser');
    });

    test('rejects a launch configuration for another resource type', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'node', script_path: '/workspace/app/server.js' } as unknown as BrowserLaunchConfiguration),
            /Invalid launch configuration/);
    });
});

function escapeForRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'browser',
        name: 'Browser',
        request: 'launch',
        program: '',
        args: ['--ignored'],
        cwd: '/workspace',
    };
}
