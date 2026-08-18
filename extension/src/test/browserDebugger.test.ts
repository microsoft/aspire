import * as assert from 'assert';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { browserDebuggerExtension } from '../debugger/languages/browser';
import { AspireResourceExtendedDebugConfiguration, BrowserLaunchConfiguration } from '../dcp/types';
import { unsupportedBrowserDebugTarget, unsupportedBrowserDebugTargetWithoutUrl } from '../loc/strings';

suite('Browser Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;
    const BROWSER_RESOURCE_URL = 'http://localhost:5173';

    async function createConfiguration(
        launchConfig: BrowserLaunchConfiguration,
        inheritedConfiguration: Partial<AspireResourceExtendedDebugConfiguration> = {}): Promise<AspireResourceExtendedDebugConfiguration> {
        const debugConfig = { ...createDebugConfig(), ...inheritedConfiguration };
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

    test('forwards a web root when the AppHost supplies one', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173', web_root: '/workspace/frontend/src' });

        assert.strictEqual(debugConfig.webRoot, '/workspace/frontend/src');
    });

    // js-debug has no way to express "no web root": it defaults webRoot to '${workspaceFolder}'
    // whenever a launch configuration omits the property. Omitting it therefore opts into that
    // documented default rather than disabling source-map resolution, and that is the intended
    // behaviour - forwarding the blank string instead makes js-debug resolve source maps against
    // '', which roots them at the filesystem root rather than at the workspace.
    for (const blankWebRoot of ['', '   ']) {
        test(`omits a blank web root ${JSON.stringify(blankWebRoot)} so js-debug applies its workspace-folder default`, async () => {
            const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173', web_root: blankWebRoot });

            assert.strictEqual('webRoot' in debugConfig, false);
            // The property is absent, not present-and-blank. js-debug only applies its default for
            // an absent property, so a `webRoot: undefined` would still defeat it.
            assert.strictEqual(debugConfig.webRoot, undefined);
        });
    }

    test('blank web roots remove an inherited web root', async () => {
        const debugConfig = await createConfiguration(
            { type: 'browser', url: 'http://localhost:5173', web_root: '' },
            { webRoot: '/workspace/previous' });

        assert.strictEqual('webRoot' in debugConfig, false);
    });

    // Leading and trailing spaces are valid characters in a POSIX path, so a padded value is a
    // different directory rather than a sloppy spelling of the unpadded one. The trim decides only
    // whether the value is blank; rewriting what the AppHost sent would silently point js-debug at
    // a directory the AppHost never named.
    test('forwards a padded web root unchanged instead of rewriting the path', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173', web_root: ' /workspace/frontend ' });

        assert.strictEqual(debugConfig.webRoot, ' /workspace/frontend ');
    });

    test('omits the web root when the AppHost does not send one', async () => {
        const debugConfig = await createConfiguration({ type: 'browser', url: 'http://localhost:5173' });

        assert.strictEqual('webRoot' in debugConfig, false);
    });

    test('rejects a browser that has no built-in js-debug adapter', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'browser', url: 'http://localhost:5173', browser: 'firefox' }),
            new RegExp(escapeForRegExp(unsupportedBrowserDebugTarget('firefox', BROWSER_RESOURCE_URL, 'msedge, chrome'))));
    });

    // The failure surfaces as a toast carrying only this message. An AppHost can declare several
    // browser resources, so a message naming just the offending value leaves the user with no way
    // to tell which resource to go and fix.
    test('names the resource that could not be debugged', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'browser', url: 'http://localhost:7654/admin', browser: 'firefox' }),
            (err: Error) => {
                assert.ok(
                    err.message.includes('http://localhost:7654/admin'),
                    `Unsupported-browser failure must identify the resource: ${err.message}`);
                return true;
            });
    });

    // The DCP run_session handler that turns this rejection into an HTTP 500 already prefixes the
    // message with "Failed to start debug session for run ID <runId>", so repeating the run ID here
    // would print it twice. Without a URL the message drops the identifier clause entirely rather
    // than rendering an empty one.
    test('omits the resource clause when the browser resource has no URL', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'browser', browser: 'firefox' }),
            (err: Error) => {
                assert.strictEqual(err.message, unsupportedBrowserDebugTargetWithoutUrl('firefox', 'msedge, chrome'));
                assert.ok(
                    !err.message.includes('1'),
                    `Message must not repeat the run ID the DCP error response already carries: ${err.message}`);
                return true;
            });
    });

    // WithBrowserDebugger(string browser = "msedge") takes an arbitrary string, so an explicit
    // empty value is a caller choice and not an absent field. Falling back to the default for it
    // would silently launch Edge for a value the allowlist does not accept.
    test('rejects an explicitly empty browser instead of silently defaulting to Edge', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'browser', url: 'http://localhost:5173', browser: '' }),
            new RegExp(escapeForRegExp(unsupportedBrowserDebugTarget('', BROWSER_RESOURCE_URL, 'msedge, chrome'))));
    });

    // An AppHost predating the `browser` field omits it entirely, and a null survives untyped
    // JSON. Both mean "not specified" and must keep the Edge default.
    for (const [label, absentBrowser] of [['undefined', undefined], ['null', null]] as const) {
        test(`defaults to Edge when the browser is ${label}`, async () => {
            const debugConfig = await createConfiguration({
                type: 'browser',
                url: 'http://localhost:5173',
                browser: absentBrowser as unknown as string | undefined,
            });

            assert.strictEqual(debugConfig.type, 'pwa-msedge');
        });
    }

    // The hosting side's WithBrowserDebugger accepts an arbitrary string, so the allowlist lookup must
    // not resolve inherited Object.prototype members. A plain object literal would hand back a
    // function for these names and assign it to debugConfiguration.type.
    for (const inheritedMember of ['toString', '__proto__']) {
        test(`rejects '${inheritedMember}' instead of resolving it through Object.prototype`, async () => {
            await assert.rejects(
                () => createConfiguration({ type: 'browser', url: 'http://localhost:5173', browser: inheritedMember }),
                new RegExp(escapeForRegExp(unsupportedBrowserDebugTarget(inheritedMember, BROWSER_RESOURCE_URL, 'msedge, chrome'))));
        });
    }
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
