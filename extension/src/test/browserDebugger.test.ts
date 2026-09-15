import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import {
    getCsharpBlazorWasmDebuggingSupport,
    minimumCsharpBlazorWasmDebuggingVersion,
    useCsharpExtensionVersionProviderForTests,
} from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { BrowserDebugSessionTermination } from '../debugger/browserDebugSessionTermination';
import { prepareDebugSession } from '../debugger/debuggerExtensions';
import { browserDebuggerExtension } from '../debugger/languages/browser';
import { cleanupRun, registerRunCleanup } from '../debugger/runCleanupRegistry';
import { AspireResourceExtendedDebugConfiguration, BrowserLaunchConfiguration } from '../dcp/types';
import {
    csharpExtensionMissingForBlazorDebugging,
    csharpExtensionOutdatedForBlazorDebugging,
    missingBlazorClientProject,
    unsupportedBrowserDebugTarget,
    unsupportedBrowserDebugTargetWithoutUrl,
} from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

suite('Browser Debugger Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;
    const BROWSER_RESOURCE_URL = 'http://localhost:5173';
    const BLAZOR_PROJECT_PATH = path.resolve(__dirname, '..', '..', '..', 'src', 'Aspire.Cli', 'Aspire.Cli.csproj');

    teardown(() => {
        cleanupRun('run-1');
        sinon.restore();
    });

    async function createConfiguration(
        launchConfig: BrowserLaunchConfiguration,
        inheritedConfiguration: Partial<AspireResourceExtendedDebugConfiguration> = {}): Promise<AspireResourceExtendedDebugConfiguration> {
        const debugConfig = { ...createDebugConfig(), ...inheritedConfiguration };
        await browserDebuggerExtension.createDebugSessionConfigurationCallback!(launchConfig, ['--ignored'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        return debugConfig;
    }

    async function createManagedConfiguration(
        browser: 'msedge' | 'chrome',
        inheritedConfiguration: Partial<AspireResourceExtendedDebugConfiguration> = {}): Promise<AspireResourceExtendedDebugConfiguration> {
        const provider = useCsharpExtensionVersionProviderForTests(() => minimumCsharpBlazorWasmDebuggingVersion);
        try {
            return await createConfiguration(
                { type: 'browser', url: BROWSER_RESOURCE_URL, browser, web_root: BLAZOR_PROJECT_PATH },
                inheritedConfiguration);
        }
        finally {
            provider.dispose();
        }
    }

    test('keeps Aspire metadata authoritative after merging browser workspace settings', async () => {
        const configuration = await createBrowserConfiguration({
            runtimeArgs: ['--start-maximized'],
            runId: 'workspace-run',
            debugSessionId: 'workspace-dcp',
            isApphost: true,
            resourceType: 'node',
        }, { type: 'browser', url: 'https://localhost:5001', browser: 'chrome' });

        assert.deepStrictEqual(configuration.runtimeArgs, [
            '--start-maximized',
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode',
        ]);
        assert.strictEqual(configuration.runId, 'run-1');
        assert.strictEqual(configuration.debugSessionId, 'dcp-1');
        assert.strictEqual(configuration.isApphost, false);
        assert.strictEqual(configuration.resourceType, 'browser');
    });

    test('keeps managed Aspire metadata authoritative over nested C# workspace overrides', async () => {
        const provider = useCsharpExtensionVersionProviderForTests(() => minimumCsharpBlazorWasmDebuggingVersion);
        try {
            const launchConfig: BrowserLaunchConfiguration = {
                type: 'browser',
                url: BROWSER_RESOURCE_URL,
                browser: 'chrome',
                web_root: BLAZOR_PROJECT_PATH,
            };
            const configuration = await createBrowserConfiguration({
                runId: 'workspace-run',
                debugSessionId: 'workspace-dcp',
                resourceType: 'node',
                browserConfig: {
                    type: 'node',
                    request: 'attach',
                    runId: 'nested-run',
                    debugSessionId: 'nested-dcp',
                    resourceType: 'node',
                    cascadeTerminateToConfigurations: [],
                },
                dotNetConfig: {
                    type: 'coreclr',
                    request: 'attach',
                    cascadeTerminateToConfigurations: [],
                },
            }, launchConfig);
            const expected = await createBrowserConfiguration({}, launchConfig);

            assert.deepStrictEqual(configuration, expected);
            assert.strictEqual(configuration.type, 'blazorwasm');
            assert.strictEqual(configuration.runId, 'run-1');
            assert.strictEqual(configuration.debugSessionId, 'dcp-1');
            assert.strictEqual(configuration.resourceType, 'browser');
        }
        finally {
            provider.dispose();
        }
    });

    test('reports a natural root browser termination exactly once and ignores child sessions', () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const send = sinon.stub();
        const cleanup = sinon.stub();
        registerRunCleanup('run-1', cleanup);
        const session = createDebugSession('browser-root');
        new BrowserDebugSessionTermination(session, 'run-1', 'dcp-1', send);

        terminateListener!(createDebugSession('browser-child', session));
        assert.strictEqual(send.called, false);

        const listener = terminateListener!;
        listener(session);
        listener(session);

        assert.deepStrictEqual(send.firstCall.args, ['run-1', 'dcp-1']);
        assert.strictEqual(send.calledOnce, true);
        assert.strictEqual(cleanup.calledOnce, true);
        assert.strictEqual(terminateListener, undefined);
    });

    test('explicit stop waits for root browser termination confirmation', async () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const stopRequest = deferred<void>();
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').returns(stopRequest.promise);
        const send = sinon.stub();
        const cleanup = sinon.stub();
        registerRunCleanup('run-1', cleanup);
        const session = createDebugSession('browser-root');
        const termination = new BrowserDebugSessionTermination(session, 'run-1', 'dcp-1', send);

        let completed = false;
        const stop = termination.stop().then(() => { completed = true; });
        await Promise.resolve();

        assert.strictEqual(stopDebugging.calledOnceWithExactly(session), true);
        stopRequest.resolve();
        await stopRequest.promise;
        await Promise.resolve();

        assert.strictEqual(completed, false);
        assert.strictEqual(send.notCalled, true);
        assert.strictEqual(cleanup.notCalled, true);
        terminateListener!(session);
        await stop;

        assert.strictEqual(send.calledOnceWithExactly('run-1', 'dcp-1'), true);
        assert.strictEqual(cleanup.calledOnce, true);
    });

    test('retries after synchronous and asynchronous browser stop failures', async () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().throws(new Error('synchronous stop failure'));
        stopDebugging.onSecondCall().rejects(new Error('asynchronous stop failure'));
        stopDebugging.onThirdCall().resolves();
        const send = sinon.stub();
        const session = createDebugSession('browser-root');
        const termination = new BrowserDebugSessionTermination(session, 'run-1', 'dcp-1', send);

        await assert.rejects(termination.stop(), /synchronous stop failure/);
        await assert.rejects(termination.stop(), /asynchronous stop failure/);
        const finalStop = termination.stop();
        terminateListener!(session);
        await finalStop;

        assert.strictEqual(stopDebugging.callCount, 3);
        assert.strictEqual(send.calledOnce, true);
    });

    test('keeps a newer browser stop cached when a stale attempt rejects', async () => {
        const firstStop = deferred<void>();
        const secondStop = deferred<void>();
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().returns(firstStop.promise);
        stopDebugging.onSecondCall().returns(secondStop.promise);
        const session = createDebugSession('browser-root');
        const termination = new BrowserDebugSessionTermination(
            session,
            'run-1',
            'dcp-1',
            sinon.stub());

        const first = termination.stop();
        termination.resetStopAttempt(first);
        const retry = termination.stop();

        firstStop.reject(new Error('stale stop failed'));
        await assert.rejects(first, /stale stop failed/);
        assert.strictEqual(termination.stop(), retry);
        assert.strictEqual(stopDebugging.callCount, 2);

        secondStop.resolve();
        terminateListener!(session);
        await retry;
    });

    test('stale browser stop response waits with a newer attempt for shared root termination', async () => {
        const firstStop = deferred<void>();
        const secondStop = deferred<void>();
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().returns(firstStop.promise);
        stopDebugging.onSecondCall().returns(secondStop.promise);
        const send = sinon.stub();
        const session = createDebugSession('browser-root');
        const termination = new BrowserDebugSessionTermination(
            session,
            'run-1',
            'dcp-1',
            send);

        const first = termination.stop();
        termination.resetStopAttempt(first);
        const retry = termination.stop();
        let firstCompleted = false;
        let retryCompleted = false;
        void first.then(() => { firstCompleted = true; });
        void retry.then(() => { retryCompleted = true; });

        firstStop.resolve();
        await firstStop.promise;
        await Promise.resolve();
        await Promise.resolve();

        assert.strictEqual(firstCompleted, false);
        assert.strictEqual(retryCompleted, false);
        assert.strictEqual(stopDebugging.callCount, 2);
        assert.strictEqual(send.notCalled, true);

        terminateListener!(session);
        await Promise.all([first, retry]);

        assert.strictEqual(send.calledOnceWithExactly('run-1', 'dcp-1'), true);
    });

    test('keeps natural termination armed after an explicit stop failure', async () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        sinon.stub(vscode.debug, 'stopDebugging').rejects(new Error('stop failed'));
        const send = sinon.stub();
        const cleanup = sinon.stub();
        registerRunCleanup('run-1', cleanup);
        const session = createDebugSession('browser-root');
        const termination = new BrowserDebugSessionTermination(session, 'run-1', 'dcp-1', send);

        await assert.rejects(termination.stop(), /stop failed/);
        terminateListener!(session);

        assert.strictEqual(send.calledOnceWithExactly('run-1', 'dcp-1'), true);
        assert.strictEqual(cleanup.calledOnce, true);
    });

    test('disposal releases the root termination listener after a browser stop failure', async () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        const disposeListener = sinon.stub().callsFake(() => { terminateListener = undefined; });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: disposeListener };
        });
        sinon.stub(vscode.debug, 'stopDebugging').rejects(new Error('stop failed'));
        const termination = new BrowserDebugSessionTermination(
            createDebugSession('browser-root'),
            'run-1',
            'dcp-1',
            sinon.stub());
        termination.stopAndDisposeOnFailure();
        await new Promise(resolve => setImmediate(resolve));

        assert.strictEqual(disposeListener.calledOnce, true);
        assert.strictEqual(terminateListener, undefined);
    });

    test('warns with the run ID and cleans up when the DCP session ID is missing', () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const warn = sinon.stub(extensionLogOutputChannel, 'warn');
        const cleanup = sinon.stub();
        registerRunCleanup('run-1', cleanup);
        const session = createDebugSession('browser-root');
        new BrowserDebugSessionTermination(session, 'run-1', null, sinon.stub());

        terminateListener!(session);

        assert.strictEqual(
            warn.calledOnceWithExactly('Unable to report termination for run run-1 because the DCP session ID is missing.'),
            true);
        assert.strictEqual(cleanup.calledOnce, true);
    });

    test('cleans up exactly once when synchronous termination delivery throws', () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const notificationError = new Error('notification failed');
        const send = sinon.stub().throws(notificationError);
        const cleanup = sinon.stub();
        registerRunCleanup('run-1', cleanup);
        const session = createDebugSession('browser-root');
        new BrowserDebugSessionTermination(session, 'run-1', 'dcp-1', send);
        const listener = terminateListener!;

        assert.throws(() => listener(session), notificationError);
        listener(session);

        assert.strictEqual(send.calledOnce, true);
        assert.strictEqual(cleanup.calledOnce, true);
    });

    test('classifies C# extension versions for Blazor WebAssembly debugging', () => {
        const cases = [
            { version: undefined, expected: { status: 'missing' } },
            { version: '2.145.14', expected: { status: 'outdated', installedVersion: '2.145.14' } },
            { version: '2.145.15-alpha', expected: { status: 'outdated', installedVersion: '2.145.15-alpha' } },
            { version: '2.145.15-prerelease', expected: { status: 'supported', installedVersion: '2.145.15-prerelease' } },
            { version: '2.145.15-prerelease.0', expected: { status: 'supported', installedVersion: '2.145.15-prerelease.0' } },
            { version: '2.145.15-prerelease.01', expected: { status: 'outdated', installedVersion: '2.145.15-prerelease.01' } },
            { version: '2.145.15-prerelease+build', expected: { status: 'supported', installedVersion: '2.145.15-prerelease+build' } },
            { version: '2.145.15-prerelease.', expected: { status: 'outdated', installedVersion: '2.145.15-prerelease.' } },
            { version: '2.145.15', expected: { status: 'supported', installedVersion: '2.145.15' } },
            { version: '2.145.15+build', expected: { status: 'supported', installedVersion: '2.145.15+build' } },
            { version: '2.145.16-alpha', expected: { status: 'supported', installedVersion: '2.145.16-alpha' } },
            { version: '2.146.0', expected: { status: 'supported', installedVersion: '2.146.0' } },
            { version: '3.0.0', expected: { status: 'supported', installedVersion: '3.0.0' } },
            { version: '02.145.15', expected: { status: 'outdated', installedVersion: '02.145.15' } },
            { version: '', expected: { status: 'outdated', installedVersion: '' } },
            { version: 'not-a-version', expected: { status: 'outdated', installedVersion: 'not-a-version' } },
            { version: '2.145', expected: { status: 'outdated', installedVersion: '2.145' } },
            { version: '9007199254740992.0.0', expected: { status: 'outdated', installedVersion: '9007199254740992.0.0' } },
        ] as const;

        for (const { version, expected } of cases) {
            const provider = useCsharpExtensionVersionProviderForTests(() => version);
            try {
                assert.deepStrictEqual(getCsharpBlazorWasmDebuggingSupport(), expected);
            }
            finally {
                provider.dispose();
            }
        }
    });

    for (const [browser, expectedBrowser] of [['msedge', 'edge'], ['chrome', 'chrome']] as const) {
        test(`maps a ${browser} Blazor client project to the C# extension attach contract`, async () => {
            const debugConfig = await createManagedConfiguration(browser, {
                projectFile: '/workspace/AppHost.csproj',
                isApphost: false,
                webRoot: '/workspace/previous',
                sourceMaps: true,
                resolveSourceMapLocations: ['**'],
                sourceMapPathOverrides: { '/src/*': '/workspace/*' },
                outFiles: ['/workspace/**/*.js'],
                userDataDir: '/workspace/profile',
                runtimeArgs: ['--user-data-dir=/workspace/profile'],
            });

            assert.deepStrictEqual(debugConfig, {
                runId: '1',
                debugSessionId: '1',
                type: 'blazorwasm',
                name: 'Browser',
                request: 'attach',
                projectFile: '/workspace/AppHost.csproj',
                isApphost: false,
                projectPath: BLAZOR_PROJECT_PATH,
                url: BROWSER_RESOURCE_URL,
                browser: expectedBrowser,
            });
        });
    }

    test('detects Blazor client project extensions case-insensitively', async () => {
        const upperCaseProjectPath = path.join(path.dirname(BLAZOR_PROJECT_PATH), 'Missing.Client.CSPROJ');
        const provider = useCsharpExtensionVersionProviderForTests(() => minimumCsharpBlazorWasmDebuggingVersion);
        try {
            await assert.rejects(
                () => createConfiguration({
                    type: 'browser',
                    url: BROWSER_RESOURCE_URL,
                    browser: 'chrome',
                    web_root: upperCaseProjectPath,
                }),
                new RegExp(escapeForRegExp(missingBlazorClientProject(upperCaseProjectPath))));
        }
        finally {
            provider.dispose();
        }
    });

    test('reports when the C# extension is missing for a Blazor client project', async () => {
        const provider = useCsharpExtensionVersionProviderForTests(() => undefined);
        try {
            await assert.rejects(
                () => createConfiguration({ type: 'browser', url: BROWSER_RESOURCE_URL, web_root: BLAZOR_PROJECT_PATH }),
                new RegExp(escapeForRegExp(csharpExtensionMissingForBlazorDebugging(
                    'ms-dotnettools.csharp',
                    minimumCsharpBlazorWasmDebuggingVersion))));
        }
        finally {
            provider.dispose();
        }
    });

    for (const version of ['2.145.14', 'not-a-version']) {
        test(`reports when C# extension version ${version} cannot debug a Blazor client project`, async () => {
            const provider = useCsharpExtensionVersionProviderForTests(() => version);
            try {
                await assert.rejects(
                    () => createConfiguration({ type: 'browser', url: BROWSER_RESOURCE_URL, web_root: BLAZOR_PROJECT_PATH }),
                    new RegExp(escapeForRegExp(csharpExtensionOutdatedForBlazorDebugging(
                        'ms-dotnettools.csharp',
                        version,
                        minimumCsharpBlazorWasmDebuggingVersion))));
            }
            finally {
                provider.dispose();
            }
        });
    }

    test('reports a missing Blazor client project without falling back to js-debug', async () => {
        const missingProjectPath = path.join(path.dirname(BLAZOR_PROJECT_PATH), 'Missing.Client.csproj');
        const provider = useCsharpExtensionVersionProviderForTests(() => minimumCsharpBlazorWasmDebuggingVersion);
        try {
            await assert.rejects(
                () => createConfiguration({ type: 'browser', url: BROWSER_RESOURCE_URL, web_root: missingProjectPath }),
                new RegExp(escapeForRegExp(missingBlazorClientProject(missingProjectPath))));
        }
        finally {
            provider.dispose();
        }
    });

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

    test('hardens generic Chromium arguments and removes user data directory switches', async () => {
        const debugConfig = await createConfiguration(
            { type: 'browser', url: BROWSER_RESOURCE_URL, browser: 'chrome', web_root: '/workspace/frontend/src' },
            {
                runtimeArgs: [
                    '--preserved',
                    '--USER-DATA-DIR=/workspace/combined',
                    '--user-data-dir',
                    '/workspace/split',
                    '--no-first-run',
                    '--no-first-run',
                    '--USER-DATA-DIR',
                    '--disable-extensions',
                ],
            });

        assert.strictEqual(debugConfig.userDataDir, true);
        assert.deepStrictEqual(debugConfig.runtimeArgs, [
            '--preserved',
            '--disable-extensions',
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode',
        ]);
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

async function createBrowserConfiguration(
    workspaceSettings: Record<string, unknown>,
    launchConfig: BrowserLaunchConfiguration): Promise<AspireResourceExtendedDebugConfiguration> {
    const prepared = await prepareDebugSession(
        {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
            debuggers: { browser: workspaceSettings as never },
        },
        launchConfig,
        [],
        [],
        {
            debug: true,
            runId: 'run-1',
            debugSessionId: 'dcp-1',
            isApphost: false,
            debugSession: {} as AspireDebugSession,
        },
        browserDebuggerExtension);

    return prepared.debugConfiguration;
}

function createDebugSession(id: string, parentSession?: vscode.DebugSession): vscode.DebugSession {
    return {
        id,
        type: 'pwa-msedge',
        name: 'Browser',
        parentSession,
        workspaceFolder: undefined,
        configuration: {
            type: 'pwa-msedge',
            name: 'Browser',
            request: 'launch',
            runId: 'run-1',
            debugSessionId: 'dcp-1',
            resourceType: 'browser',
        },
        customRequest: sinon.stub(),
        getDebugProtocolBreakpoint: sinon.stub(),
    };
}

function deferred<T>(): { promise: Promise<T>; reject(reason?: unknown): void; resolve(value: T): void } {
    let resolve!: (value: T) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((promiseResolve, promiseReject) => {
        resolve = promiseResolve;
        reject = promiseReject;
    });

    return { promise, reject, resolve };
}
