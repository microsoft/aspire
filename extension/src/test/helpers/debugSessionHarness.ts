import * as fs from 'node:fs';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession } from '../../debugger/AspireDebugSession';
import { browserDebuggerExtension } from '../../debugger/languages/browser';
import { AspireResourceExtendedDebugConfiguration, BrowserLaunchConfiguration, SessionTerminatedNotification } from '../../dcp/types';

/** Stubs installed over the filesystem calls the browser profile directory setup makes. */
export interface BrowserProfileFsStubs {
    mkdir: sinon.SinonStub;
    lstat: sinon.SinonStub;
    mkdtemp: sinon.SinonStub;
    realpath: sinon.SinonStub;
}

/** Suffix the stubbed `mkdtemp` appends, standing in for Node's random six characters. */
export const stubbedMkdtempSuffix = 'A1b2C3';

function asStub(candidate: unknown): sinon.SinonStub | undefined {
    return typeof candidate === 'function' && typeof (candidate as { restore?: unknown }).restore === 'function'
        ? candidate as unknown as sinon.SinonStub
        : undefined;
}

/**
 * Stubs the filesystem call `createBrowserUserDataDir` makes, so tests exercise the real creation
 * and containment logic without touching the actual filesystem.
 *
 * `mkdtemp` mirrors Node's contract by appending characters to the prefix it is given, so
 * assertions can tell a generated leaf apart from a deterministic path. Individual tests override
 * it to describe a hostile or unexpected result.
 *
 * Calling this twice in one test returns the stub already installed rather than stacking a second
 * layer. Suites install it once in `setup` so no test can accidentally create real directories
 * under the shared temp directory, and tests that need the handle call it again to get it.
 */
export function stubBrowserProfileFs(): BrowserProfileFsStubs {
    const existingMkdir = asStub(fs.promises.mkdir);
    const mkdir = existingMkdir ?? sinon.stub(fs.promises, 'mkdir').resolves(undefined);

    const existingLstat = asStub(fs.promises.lstat);
    const lstat = existingLstat ?? sinon.stub(fs.promises, 'lstat').resolves({
        isDirectory: () => true,
        isSymbolicLink: () => false,
        mode: 0o700,
        uid: typeof process.getuid === 'function' ? process.getuid() : 0,
    } as fs.Stats);

    const existingMkdtemp = asStub(fs.promises.mkdtemp);
    const mkdtemp = existingMkdtemp ?? sinon.stub(fs.promises, 'mkdtemp').callsFake(async (prefix: fs.PathLike) => `${String(prefix)}${stubbedMkdtempSuffix}`);

    const existingRealpath = asStub(fs.promises.realpath);
    const realpath = existingRealpath ?? sinon.stub(fs.promises, 'realpath').callsFake(async (candidate: fs.PathLike) => String(candidate));

    return { mkdir, lstat, mkdtemp, realpath };
}

export interface Deferred<T> {
    promise: Promise<T>;
    resolve: (value: T | PromiseLike<T>) => void;
    reject: (reason?: unknown) => void;
}

export function createDeferred<T>(): Deferred<T> {
    let resolve!: (value: T | PromiseLike<T>) => void;
    let reject!: (reason?: unknown) => void;
    const promise = new Promise<T>((promiseResolve, promiseReject) => {
        resolve = promiseResolve;
        reject = promiseReject;
    });

    return { promise, resolve, reject };
}

export function createDebugSession(id: string, configuration: vscode.DebugConfiguration, parentSession?: vscode.DebugSession): vscode.DebugSession {
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

export interface DebugSessionHarnessOptions {
    /**
     * `'immediate'` resolves `vscode.debug.stopDebugging` right away. `'deferred'` hands the test
     * control over when (and whether) the stop settles, which is how the lifecycle-ordering tests
     * observe that `stopSession()` stays pending until VS Code has finished stopping.
     */
    stopDebugging?: 'immediate' | 'deferred';
    /** Id given to the debug session synthesized by `vscode.debug.startDebugging`. */
    startedSessionId?: string;
    /** When false, `vscode.debug.startDebugging` resolves without announcing a started session. */
    autoStartSession?: boolean;
}

/**
 * Shared setup for resource debug session lifecycle tests.
 *
 * These tests all need the same scaffolding — captured `onDidStartDebugSession` /
 * `onDidTerminateDebugSession` listeners, a `startDebugging` stub that synthesizes the session VS
 * Code would have created, a controllable `stopDebugging`, a fake DCP server, and a real
 * `AspireDebugSession`. Centralizing it here keeps each test to the state transition it is proving.
 *
 * The constructor installs sinon stubs, so it must run inside a test whose teardown calls
 * `sinon.restore()`.
 */
export class DebugSessionHarness {
    readonly aspireDebugSession: AspireDebugSession;
    readonly parentSession: vscode.DebugSession;
    readonly sendNotification: sinon.SinonStub;
    readonly startDebugging: sinon.SinonStub;
    readonly stopDebugging: sinon.SinonStub;
    readonly rm: sinon.SinonStub;
    readonly profileFs: BrowserProfileFsStubs;

    /**
     * Invoked immediately before the synthesized session is announced. Tests use it to interleave
     * Aspire session disposal with debug session startup.
     */
    onBeforeSessionStarted: (() => void) | undefined;

    private _startListener: ((session: vscode.DebugSession) => void) | undefined;
    private _terminateListener: ((session: vscode.DebugSession) => void) | undefined;
    private _pendingStop: Deferred<void> | undefined;

    constructor(options: DebugSessionHarnessOptions = {}) {
        const { stopDebugging = 'immediate', startedSessionId = 'browser-session-id', autoStartSession = true } = options;

        this.rm = sinon.stub(fs.promises, 'rm').resolves();
        this.profileFs = stubBrowserProfileFs();
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            this._startListener = listener;
            return { dispose: () => { } };
        });
        // Honors dispose() rather than returning a no-op Disposable. A fake that keeps invoking a
        // released listener cannot observe a listener leak, which is precisely what the
        // failed-stop lifecycle tests need to assert.
        sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            this._terminateListener = listener;
            return {
                dispose: () => {
                    if (this._terminateListener === listener) {
                        this._terminateListener = undefined;
                    }
                }
            };
        });
        this.startDebugging = sinon.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            if (autoStartSession) {
                this.onBeforeSessionStarted?.();
                this.startSession(createDebugSession(startedSessionId, configuration as vscode.DebugConfiguration));
            }

            return true;
        });

        if (stopDebugging === 'deferred') {
            this.stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').callsFake(() => {
                this._pendingStop = createDeferred<void>();
                return this._pendingStop.promise;
            });
        }
        else {
            this.stopDebugging = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        }

        this.sendNotification = sinon.stub();
        const dcpServer = {
            sendNotification: this.sendNotification,
            takeDebugSessionAggregateStats: sinon.stub().returns(undefined),
        };
        this.parentSession = createDebugSession('aspire-session-id', {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/apphost.cs',
        });
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };

        this.aspireDebugSession = new AspireDebugSession(this.parentSession, {} as any, dcpServer as any, terminalProvider as any, () => { });
    }

    /** Fires the captured `vscode.debug.onDidStartDebugSession` listener. */
    startSession(session: vscode.DebugSession): void {
        if (!this._startListener) {
            throw new Error('No onDidStartDebugSession listener was registered.');
        }

        this._startListener(session);
    }

    /**
     * Fires the captured `vscode.debug.onDidTerminateDebugSession` listener, if one is still
     * registered. Returns whether a listener was there to receive it, so tests can distinguish
     * "the event was ignored" from "nobody was listening any more".
     */
    terminateSession(session: vscode.DebugSession): boolean {
        if (!this._terminateListener) {
            return false;
        }

        this._terminateListener(session);

        return true;
    }

    /** Completes an in-flight `'deferred'` stop successfully. */
    finishStopDebugging(): void {
        if (!this._pendingStop) {
            throw new Error('No stopDebugging call is pending.');
        }

        this._pendingStop.resolve();
    }

    /** Fails an in-flight `'deferred'` stop, as VS Code does when it cannot stop a session. */
    failStopDebugging(error: Error): void {
        if (!this._pendingStop) {
            throw new Error('No stopDebugging call is pending.');
        }

        this._pendingStop.reject(error);
    }

    /** Every `sessionTerminated` notification the extension pushed to DCP, in order. */
    sessionTerminatedNotifications(): SessionTerminatedNotification[] {
        return this.sendNotification.getCalls()
            .map(call => call.args[0])
            .filter((notification): notification is SessionTerminatedNotification => notification?.notification_type === 'sessionTerminated');
    }

    dispose(): void {
        this.aspireDebugSession.dispose();
    }
}

export function createResourceDebugConfig(overrides: Partial<AspireResourceExtendedDebugConfiguration> = {}): AspireResourceExtendedDebugConfiguration {
    return {
        runId: 'run-1',
        debugSessionId: 'dcp-1',
        // Default to the process-backed signal so tests opt in to the browser lifecycle
        // explicitly, matching how every non-browser debugger integration declares it.
        terminationSignal: 'adapterExit',
        type: 'browser',
        name: 'Browser: https://localhost:5001',
        request: 'launch',
        program: '/workspace/app',
        args: [],
        ...overrides
    };
}

/**
 * Runs the browser debugger extension's configuration callback against a resource debug config,
 * reproducing the parts of `prepareDebugSession` that the callback depends on.
 *
 * The termination signal is stamped from `browserDebuggerExtension.terminationSignal` rather than
 * hardcoded, mirroring `applyAspireOwnedFields` in production. Taking it from the extension keeps
 * these lifecycle tests honest: if the browser integration ever re-declares itself as
 * `adapterExit`, the tests below start exercising that instead of silently passing against a
 * literal the tests supplied themselves.
 */
export async function configureBrowserDebugSession(launchConfig: BrowserLaunchConfiguration, debugConfig: AspireResourceExtendedDebugConfiguration): Promise<void> {
    const fakeAspireDebugSession = {} as AspireDebugSession;
    await browserDebuggerExtension.createDebugSessionConfigurationCallback!(
        launchConfig,
        [],
        [],
        { debug: true, runId: debugConfig.runId, debugSessionId: 'dcp-1', isApphost: false, debugSession: fakeAspireDebugSession },
        debugConfig);
    debugConfig.terminationSignal = browserDebuggerExtension.terminationSignal;
}
