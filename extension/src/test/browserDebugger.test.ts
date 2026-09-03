import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { BrowserDebugSessionTermination } from '../debugger/browserDebugSessionTermination';
import { browserDebuggerExtension } from '../debugger/languages/browser';
import { prepareDebugSession } from '../debugger/debuggerExtensions';
import { cleanupRun, registerRunCleanup } from '../debugger/runCleanupRegistry';
import { AspireResourceDebugSession, AspireResourceExtendedDebugConfiguration, BrowserLaunchConfiguration, SessionTerminatedNotification } from '../dcp/types';
import { unsupportedBrowserDebugTarget, unsupportedBrowserDebugTargetWithoutUrl } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

suite('Browser Debugger Tests', () => {
    teardown(() => {
        cleanupRun('run-1');
        sinon.restore();
    });

    test('configures Chromium for an isolated browser that exits with the debug session', async () => {
        const configuration = await createBrowserConfiguration(
            { type: 'browser', url: 'https://localhost:5001', browser: 'chrome' },
            {
                runtimeArgs: ['--start-maximized', '--user-data-dir', '/workspace/profile'],
                runId: 'workspace-run',
                debugSessionId: 'workspace-dcp',
                resourceType: 'node'
            });

        assert.strictEqual(configuration.type, 'pwa-chrome');
        assert.strictEqual(configuration.request, 'launch');
        assert.strictEqual(configuration.url, 'https://localhost:5001');
        assert.strictEqual(configuration.userDataDir, true);
        assert.deepStrictEqual(configuration.runtimeArgs, [
            '--start-maximized',
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode'
        ]);
        assert.strictEqual(configuration.runId, 'run-1');
        assert.strictEqual(configuration.debugSessionId, 'dcp-1');
        assert.strictEqual(configuration.resourceType, 'browser');
    });

    test('removes a mixed-case combined Chromium user data directory argument without consuming the following argument', async () => {
        const configuration = await createBrowserConfiguration(
            { type: 'browser', url: 'https://localhost:5001', browser: 'chrome' },
            {
                runtimeArgs: ['--User-Data-Dir=C:\\profile', 'unrelated-value', '--start-maximized']
            });

        assert.deepStrictEqual(configuration.runtimeArgs, [
            'unrelated-value',
            '--start-maximized',
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode'
        ]);
    });

    test('removes a mixed-case split Chromium user data directory argument and its value only', async () => {
        const configuration = await createBrowserConfiguration(
            { type: 'browser', url: 'https://localhost:5001', browser: 'msedge' },
            {
                runtimeArgs: ['--USER-DATA-DIR', 'C:\\profile', '--start-maximized', 'unrelated-value']
            });

        assert.deepStrictEqual(configuration.runtimeArgs, [
            '--start-maximized',
            'unrelated-value',
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode'
        ]);
    });

    test('reports a natural root browser termination exactly once', () => {
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

        terminateListener!(session);

        assert.deepStrictEqual(send.firstCall.args, ['run-1', 'dcp-1']);
        assert.strictEqual(send.calledOnce, true);
        assert.strictEqual(cleanup.calledOnce, true);
        assert.strictEqual(terminateListener, undefined);
    });

    test('cleans up exactly once when reporting browser termination throws', () => {
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
        assert.strictEqual(terminateListener, undefined);
    });

    test('warns when browser termination cannot be reported without a DCP session ID', () => {
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

    test('wires the started browser root session to DCP termination', async () => {
        let startListener: ((session: vscode.DebugSession) => void) | undefined;
        const terminateListeners = new Set<(session: vscode.DebugSession) => void>();
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startListener = listener;
            return { dispose: () => { startListener = undefined; } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListeners.add(listener);
            return { dispose: () => { terminateListeners.delete(listener); } };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            startListener!(createDebugSession('browser-root', undefined, configuration as vscode.DebugConfiguration));
            return true;
        });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        const sendNotification = sinon.stub();
        const dcpServer = {
            sendNotification,
            takeDebugSessionAggregateStats: sinon.stub().returns(undefined)
        };
        const parent = createDebugSession('aspire-parent', undefined, {
            type: 'aspire',
            name: 'Aspire',
            request: 'launch',
            program: '/workspace/apphost.cs'
        });
        const aspireSession = new AspireDebugSession(
            parent,
            {} as never,
            dcpServer as never,
            { isDebugConfigEnvironmentLoggingEnabled: () => false } as never,
            () => { });
        const configuration = await createBrowserConfiguration(
            { type: 'browser', url: 'https://localhost:5001' },
            {});

        const resourceSession = await aspireSession.startAndGetDebugSession(configuration);
        assert.ok(resourceSession);

        for (const listener of [...terminateListeners]) {
            listener(resourceSession.session);
        }
        for (const listener of [...terminateListeners]) {
            listener(resourceSession.session);
        }

        const terminations = sendNotification.getCalls()
            .map(call => call.args[0])
            .filter((notification): notification is SessionTerminatedNotification => notification.notification_type === 'sessionTerminated');
        assert.deepStrictEqual(terminations, [{
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1'
        }]);

        aspireSession.dispose();
    });

    test('awaits one browser stop before reporting termination', async () => {
        const stop = deferred<void>();
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: () => { } });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').returns(stop.promise);
        const send = sinon.stub();
        const termination = new BrowserDebugSessionTermination(createDebugSession('browser-root'), 'run-1', 'dcp-1', send);

        let resolved = false;
        const first = termination.stop().then(() => { resolved = true; });
        const second = termination.stop();
        await Promise.resolve();

        assert.strictEqual(stopDebugging.calledOnce, true);
        assert.strictEqual(resolved, false);
        assert.strictEqual(send.called, false);

        stop.resolve();
        await first;
        await second;

        assert.strictEqual(send.calledOnce, true);
    });

    test('keeps a failed browser stop retryable and does not report termination', async () => {
        let terminateListener: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose: () => { terminateListener = undefined; } };
        });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().rejects(new Error('stop failed'));
        stopDebugging.onSecondCall().resolves();
        const send = sinon.stub();
        const session = createDebugSession('browser-root');
        const termination = new BrowserDebugSessionTermination(session, 'run-1', 'dcp-1', send);

        await assert.rejects(termination.stop(), /stop failed/);
        assert.strictEqual(send.called, false);
        assert.ok(terminateListener);

        await termination.stop();

        assert.strictEqual(stopDebugging.callCount, 2);
        assert.strictEqual(send.calledOnce, true);
    });

    test('retries a permanently pending browser stop and deduplicates the new attempt', async () => {
        const secondStop = deferred<void>();
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: () => { } });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().returns(new Promise<void>(() => { }));
        stopDebugging.onSecondCall().returns(secondStop.promise);
        const termination = new BrowserDebugSessionTermination(
            createDebugSession('browser-root'),
            'run-1',
            'dcp-1',
            sinon.stub());

        const first = termination.stop();
        termination.resetStopAttempt(first);
        const retry = termination.stop();
        const concurrent = termination.stop();

        assert.strictEqual(concurrent, retry);
        assert.strictEqual(stopDebugging.callCount, 2);

        secondStop.resolve();
        await retry;
        await concurrent;
    });

    test('stale browser stop success settles a pending retry through shared completion', async () => {
        const firstStop = deferred<void>();
        const secondStop = deferred<void>();
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: () => { } });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().returns(firstStop.promise);
        stopDebugging.onSecondCall().returns(secondStop.promise);
        const send = sinon.stub();
        const cleanup = sinon.stub();
        registerRunCleanup('run-1', cleanup);
        const termination = new BrowserDebugSessionTermination(
            createDebugSession('browser-root'),
            'run-1',
            'dcp-1',
            send);

        const first = termination.stop();
        termination.resetStopAttempt(first);
        const retry = termination.stop();

        firstStop.resolve();
        await first;
        const retryOutcome = await Promise.race([
            retry.then(() => 'completed'),
            new Promise<'pending'>(resolve => setTimeout(() => resolve('pending'), 25)),
        ]);

        assert.strictEqual(retryOutcome, 'completed');
        assert.strictEqual(stopDebugging.callCount, 2);
        assert.strictEqual(send.calledOnceWithExactly('run-1', 'dcp-1'), true);
        assert.strictEqual(cleanup.calledOnce, true);
    });

    test('does not let a stale browser stop rejection clear the active retry', async () => {
        const firstStop = deferred<void>();
        const secondStop = deferred<void>();
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: () => { } });
        const stopDebugging = sinon.stub(vscode.debug, 'stopDebugging');
        stopDebugging.onFirstCall().returns(firstStop.promise);
        stopDebugging.onSecondCall().returns(secondStop.promise);
        const termination = new BrowserDebugSessionTermination(
            createDebugSession('browser-root'),
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
        await retry;
    });

    test('keeps natural termination armed after a disposal stop fails', async () => {
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

        termination.stopAndDisposeOnFailure();
        await Promise.resolve();
        await Promise.resolve();

        assert.ok(terminateListener);
        terminateListener(session);

        assert.strictEqual(send.calledOnceWithExactly('run-1', 'dcp-1'), true);
        assert.strictEqual(cleanup.calledOnce, true);
        assert.strictEqual(terminateListener, undefined);
    });

    test('returns the late browser session when its stop succeeds after Aspire disposal', async () => {
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const { result, browserSession, sendNotification } = await startBrowserAfterAspireDisposal();
        const startedSession = await result;
        await Promise.resolve();

        assert.strictEqual(startedSession?.session, browserSession);
        sinon.assert.calledOnceWithExactly(sendNotification, {
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1',
        });
    });

    test('keeps the late browser session reachable while its stop remains pending after Aspire disposal', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(debugSession =>
            debugSession?.id === 'browser-root' ? new Promise<void>(() => { }) : Promise.resolve());
        const { result, browserSession, sendNotification } = await startBrowserAfterAspireDisposal();
        const startedSession = await result;

        await clock.tickAsync(10_000);

        assert.strictEqual(startedSession?.session, browserSession);
        assert.strictEqual(sendNotification.notCalled, true);
    });
});

async function createBrowserConfiguration(
    launchConfiguration: BrowserLaunchConfiguration,
    workspaceSettings: Record<string, unknown>): Promise<AspireResourceExtendedDebugConfiguration> {
    const configuration = await prepareDebugSession(
        {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
            debuggers: { browser: workspaceSettings as never }
        },
        launchConfiguration,
        [],
        [],
        {
            debug: true,
            runId: 'run-1',
            debugSessionId: 'dcp-1',
            isApphost: false,
            debugSession: {} as AspireDebugSession
        },
        browserDebuggerExtension);

    return configuration.debugConfiguration;
}

function createDebugSession(id: string, parentSession?: vscode.DebugSession, configuration?: vscode.DebugConfiguration): vscode.DebugSession {
    return {
        id,
        type: configuration?.type ?? 'pwa-msedge',
        name: configuration?.name ?? 'Browser',
        parentSession,
        workspaceFolder: undefined,
        configuration: configuration ?? {
            type: 'pwa-msedge',
            name: 'Browser',
            request: 'launch',
            runId: 'run-1',
            debugSessionId: 'dcp-1',
            resourceType: 'browser'
        },
        customRequest: sinon.stub(),
        getDebugProtocolBreakpoint: sinon.stub()
    };
}

async function startBrowserAfterAspireDisposal(): Promise<{
    result: Promise<AspireResourceDebugSession | undefined>;
    browserSession: vscode.DebugSession;
    sendNotification: sinon.SinonStub;
}> {
    let startListener: ((session: vscode.DebugSession) => void) | undefined;
    sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
        startListener = listener;
        return { dispose: () => { startListener = undefined; } };
    });
    sinon.stub(vscode.debug, 'onDidTerminateDebugSession').returns({ dispose: () => { } });
    sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
    const start = deferred<boolean>();
    sinon.stub(vscode.debug, 'startDebugging').returns(start.promise);
    const parent = createDebugSession('aspire-parent', undefined, {
        type: 'aspire',
        name: 'Aspire',
        request: 'launch',
        program: '/workspace/apphost.cs'
    });
    const sendNotification = sinon.stub();
    const aspireSession = new AspireDebugSession(
        parent,
        {} as never,
        {
            sendNotification,
            takeDebugSessionAggregateStats: sinon.stub().returns(undefined)
        } as never,
        { isDebugConfigEnvironmentLoggingEnabled: () => false } as never,
        () => { });
    const configuration = await createBrowserConfiguration(
        { type: 'browser', url: 'https://localhost:5001' },
        {});
    const browserSession = createDebugSession('browser-root', undefined, configuration);

    const result = aspireSession.startAndGetDebugSession(configuration);
    await Promise.resolve();
    aspireSession.dispose();
    startListener!(browserSession);
    start.resolve(true);
    await Promise.resolve();

    return { result, browserSession, sendNotification };
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

    test('rejects a browser that has no supported debug adapter', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'browser', url: 'http://localhost:5173', browser: 'safari' }),
            new RegExp(escapeForRegExp(unsupportedBrowserDebugTarget('safari', BROWSER_RESOURCE_URL, 'msedge, chrome'))));
    });

    // The failure surfaces as a toast carrying only this message. An AppHost can declare several
    // browser resources, so a message naming just the offending value leaves the user with no way
    // to tell which resource to go and fix.
    test('names the resource that could not be debugged', async () => {
        await assert.rejects(
            () => createConfiguration({ type: 'browser', url: 'http://localhost:7654/admin', browser: 'safari' }),
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
            () => createConfiguration({ type: 'browser', browser: 'safari' }),
            (err: Error) => {
                assert.strictEqual(err.message, unsupportedBrowserDebugTargetWithoutUrl('safari', 'msedge, chrome'));
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
