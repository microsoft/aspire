import * as assert from 'assert';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { browserDebuggerExtension } from '../debugger/languages/browser';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { AspireResourceExtendedDebugConfiguration, BrowserLaunchConfiguration } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';

suite('Browser Debugger Tests', () => {
    teardown(() => {
        cleanupRun('run-1');
        sinon.restore();
    });

    test('configures js-debug browser launch with isolated profile and clean-exit flags', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        const launchConfig: BrowserLaunchConfiguration = {
            type: 'browser',
            mode: 'Debug',
            url: 'https://localhost:5001',
            web_root: '/workspace/app',
            browser: 'chrome'
        };
        const debugConfig = createDebugConfig();

        await configure(launchConfig, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-chrome');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.url, 'https://localhost:5001');
        assert.strictEqual(debugConfig.webRoot, '/workspace/app');
        assert.strictEqual(debugConfig.sourceMaps, true);
        assert.deepStrictEqual(debugConfig.resolveSourceMapLocations, ['**', '!**/node_modules/**']);
        assert.deepStrictEqual(debugConfig.runtimeArgs, [
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode'
        ]);
        assert.strictEqual(debugConfig.userDataDir, path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'run-1'));
        assert.strictEqual(debugConfig.debugSessionId, 'dcp-1');
        assert.strictEqual(debugConfig.sessionTerminatedDcpId, 'dcp-1');
        assert.strictEqual(debugConfig.sendSessionTerminatedOnDebugSessionEnd, true);
        assert.strictEqual(debugConfig.program, undefined);
        assert.strictEqual(debugConfig.args, undefined);
        assert.strictEqual(debugConfig.cwd, undefined);

        cleanupRun('run-1');
        assert.strictEqual(rmStub.calledOnceWithExactly(path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'run-1'), { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }), true);
    });

    test('defaults to Edge and preserves user runtime args', async () => {
        const launchConfig: BrowserLaunchConfiguration = {
            type: 'browser',
            url: 'https://localhost:5001'
        };
        const debugConfig = createDebugConfig();
        debugConfig.runtimeArgs = ['--custom-flag', '--no-first-run'];

        await configure(launchConfig, debugConfig);

        assert.strictEqual(debugConfig.type, 'pwa-msedge');
        assert.deepStrictEqual(debugConfig.runtimeArgs, [
            '--custom-flag',
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-mode'
        ]);
    });

    test('uses the registered cleanup run id for the browser profile directory', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        const debugConfig = createDebugConfig();
        debugConfig.runId = 'custom-run-id';

        await configure({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        assert.strictEqual(debugConfig.userDataDir, path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'custom-run-id'));

        cleanupRun('run-1');
        assert.strictEqual(rmStub.called, false);

        cleanupRun('custom-run-id');
        assert.strictEqual(rmStub.calledOnceWithExactly(path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'custom-run-id'), { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }), true);
    });

    test('maps Firefox to the VS Code Firefox debug adapter', async () => {
        const debugConfig = createDebugConfig();

        await configure({ type: 'browser', url: 'https://localhost:5001', browser: 'firefox' }, debugConfig);

        assert.strictEqual(debugConfig.type, 'firefox');
    });

    test('logs the missing URL reason when browser launch configuration is incomplete', async () => {
        const infoStub = sinon.stub(extensionLogOutputChannel, 'info');
        const launchConfig: BrowserLaunchConfiguration = {
            type: 'browser'
        };

        await assert.rejects(configure(launchConfig, createDebugConfig()));

        assert.strictEqual(infoStub.calledOnce, true);
        assert.match(infoStub.firstCall.args[0], /Browser launch configuration did not include a URL/);
    });

    test('sends sessionTerminated and cleans up when the root browser debug session terminates', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        let startDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        let terminateDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            assert.ok(startDebugSession);
            startDebugSession(createDebugSession('browser-session-id', configuration as vscode.DebugConfiguration));
            return true;
        });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        const dcpServer = {
            sendNotification: sinon.stub()
        };
        const parentDebugSession = createDebugSession('aspire-session-id', {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
        });
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        const debugConfig = createDebugConfig();
        await configure({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSession = await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        assert.ok(terminateDebugSession);
        terminateDebugSession(resourceDebugSession.session);

        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        assert.deepStrictEqual(dcpServer.sendNotification.firstCall.args[0], {
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1',
            exit_code: 0
        });
        assert.strictEqual(rmStub.calledOnceWithExactly(path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'run-1'), { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }), true);

        aspireDebugSession.dispose();
    });

    test('waits for stopped browser debug session before cleaning profile directory', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        let startDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(() => {
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            assert.ok(startDebugSession);
            startDebugSession(createDebugSession('browser-session-id', configuration as vscode.DebugConfiguration));
            return true;
        });
        let finishStopDebugging: (() => void) | undefined;
        sinon.stub(vscode.debug, 'stopDebugging').callsFake(() => new Promise<void>(resolve => {
            finishStopDebugging = resolve;
        }));

        const dcpServer = {
            sendNotification: sinon.stub(),
            takeDebugSessionAggregateStats: sinon.stub(),
        };
        const parentDebugSession = createDebugSession('aspire-session-id', {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
        });
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        const debugConfig = createDebugConfig();
        await configure({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSession = await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        resourceDebugSession.stopSession();
        await Promise.resolve();

        assert.strictEqual(rmStub.called, false);

        assert.ok(finishStopDebugging);
        finishStopDebugging();
        await Promise.resolve();

        assert.strictEqual(rmStub.calledOnceWithExactly(path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'run-1'), { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }), true);

        aspireDebugSession.dispose();
    });

    test('sends sessionTerminated when browser debug session starts after Aspire session disposal', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        let startDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(() => {
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();

        const dcpServer = {
            sendNotification: sinon.stub(),
            takeDebugSessionAggregateStats: sinon.stub(),
        };
        const parentDebugSession = createDebugSession('aspire-session-id', {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
        });
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        const debugConfig = createDebugConfig();
        await configure({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        const resourceDebugSessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        aspireDebugSession.dispose();

        assert.ok(startDebugSession);
        startDebugSession(createDebugSession('browser-session-id', debugConfig));

        const resourceDebugSession = await resourceDebugSessionPromise;

        assert.strictEqual(resourceDebugSession, undefined);
        assert.strictEqual(stopDebuggingStub.calledWith(sinon.match.has('id', 'browser-session-id')), true);
        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        assert.deepStrictEqual(dcpServer.sendNotification.firstCall.args[0], {
            notification_type: 'sessionTerminated',
            session_id: 'run-1',
            dcp_id: 'dcp-1',
            exit_code: 0
        });
        assert.strictEqual(rmStub.calledOnceWithExactly(path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'run-1'), { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }), true);
    });

    test('does not send sessionTerminated for a browser child session from another parent', async () => {
        let startDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        let terminateDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            assert.ok(startDebugSession);
            startDebugSession(createDebugSession('browser-session-id', configuration as vscode.DebugConfiguration));
            return true;
        });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        const dcpServer = {
            sendNotification: sinon.stub()
        };
        const parentDebugSession = createDebugSession('aspire-session-id', {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
        });
        const otherParentDebugSession = createDebugSession('other-browser-session-id', {
            type: 'pwa-msedge',
            request: 'launch',
            name: 'Browser: https://localhost:5001',
        });
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        const debugConfig = createDebugConfig();
        debugConfig.sendSessionTerminatedOnDebugSessionEnd = true;
        debugConfig.sessionTerminatedDcpId = 'dcp-1';
        debugConfig.debugSessionId = null;

        const resourceDebugSession = await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        assert.ok(terminateDebugSession);
        terminateDebugSession(createDebugSession('same-name-different-parent-session-id', {
            type: 'pwa-msedge',
            request: 'launch',
            name: 'Browser: https://localhost:5001',
        }, otherParentDebugSession));

        assert.strictEqual(dcpServer.sendNotification.called, false);

        aspireDebugSession.dispose();
    });

    test('does not send sessionTerminated for a transient browser child target', async () => {
        let startDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        let terminateDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            assert.ok(startDebugSession);
            startDebugSession(createDebugSession('browser-session-id', configuration as vscode.DebugConfiguration));
            return true;
        });
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        const dcpServer = {
            sendNotification: sinon.stub()
        };
        const parentDebugSession = createDebugSession('aspire-session-id', {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
        });
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        const debugConfig = createDebugConfig();
        debugConfig.sendSessionTerminatedOnDebugSessionEnd = true;
        debugConfig.sessionTerminatedDcpId = 'dcp-1';
        debugConfig.debugSessionId = null;

        const resourceDebugSession = await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.ok(resourceDebugSession);
        assert.ok(terminateDebugSession);
        terminateDebugSession(createDebugSession('js-debug-child-session-id', {
            type: 'pwa-msedge',
            request: 'launch',
            name: 'Page title from browser target',
        }, resourceDebugSession.session));

        assert.strictEqual(dcpServer.sendNotification.called, false);

        terminateDebugSession(resourceDebugSession.session);

        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);

        aspireDebugSession.dispose();
    });

    test('waits for browser debug shutdown before cleaning up a session that starts after disposal', async () => {
        const rmStub = sinon.stub(fs.promises, 'rm').resolves();
        const stopDebugging = createDeferred<void>();
        let startDebugSession: ((session: vscode.DebugSession) => void) | undefined;
        let aspireDebugSession: AspireDebugSession | undefined;
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startDebugSession = listener;
            return { dispose: () => { } };
        });
        sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            assert.ok(startDebugSession);
            aspireDebugSession?.dispose();
            startDebugSession(createDebugSession('browser-session-id', configuration as vscode.DebugConfiguration));
            return true;
        });
        sinon.stub(vscode.debug, 'stopDebugging').returns(stopDebugging.promise);

        const dcpServer = {
            sendNotification: sinon.stub(),
            takeDebugSessionAggregateStats: sinon.stub().returns(undefined)
        };
        const parentDebugSession = createDebugSession('aspire-session-id', {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
        });
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
        const debugConfig = createDebugConfig();
        await configure({ type: 'browser', url: 'https://localhost:5001' }, debugConfig);

        let resolved = false;
        const resourceDebugSessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig).then(result => {
            resolved = true;
            return result;
        });
        await Promise.resolve();
        await Promise.resolve();

        assert.strictEqual(resolved, false);
        assert.strictEqual(dcpServer.sendNotification.called, false);
        assert.strictEqual(rmStub.called, false);

        stopDebugging.resolve();
        const resourceDebugSession = await resourceDebugSessionPromise;

        assert.strictEqual(resourceDebugSession, undefined);
        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        assert.strictEqual(rmStub.calledOnceWithExactly(path.join(os.tmpdir(), 'aspire-vscode-browser-debug', 'run-1'), { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }), true);
    });
});

async function configure(launchConfig: BrowserLaunchConfiguration, debugConfig: AspireResourceExtendedDebugConfiguration): Promise<void> {
    const fakeAspireDebugSession = {} as AspireDebugSession;
    await browserDebuggerExtension.createDebugSessionConfigurationCallback!(
        launchConfig,
        [],
        [],
        { debug: true, runId: 'run-1', debugSessionId: 'dcp-1', isApphost: false, debugSession: fakeAspireDebugSession },
        debugConfig);
}

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: 'run-1',
        debugSessionId: 'dcp-1',
        type: 'browser',
        name: 'Browser: https://localhost:5001',
        request: 'launch',
        program: '/workspace/app',
        args: []
    };
}

function createDebugSession(id: string, configuration: vscode.DebugConfiguration, parentSession?: vscode.DebugSession): vscode.DebugSession {
    return {
        id,
        type: configuration.type,
        name: configuration.name,
        parentSession,
        workspaceFolder: undefined,
        configuration,
        customRequest: sinon.stub(),
        getDebugProtocolBreakpoint: sinon.stub(),
    };
}

function createDeferred<T>(): { promise: Promise<T>; resolve: (value: T | PromiseLike<T>) => void; reject: (reason?: unknown) => void } {
    let resolve!: (value: T | PromiseLike<T>) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((promiseResolve, promiseReject) => {
        resolve = promiseResolve;
        reject = promiseReject;
    });

    return { promise, resolve, reject };
}
