import * as vscode from 'vscode';
import { ErrorCodes, ResponseError } from 'vscode-jsonrpc';
import { AspireDebugSession } from './debugger/AspireDebugSession';
import { AspireDebugConfigurationProvider } from './debugger/AspireDebugConfigurationProvider';
import { debugSessionAlreadyExists, extensionContextNotInitialized } from './loc/strings';
import AspireRpcServer from './server/AspireRpcServer';
import AspireDcpServer from './dcp/AspireDcpServer';
import { AspireTerminalProvider } from './utils/AspireTerminalProvider';
import { AspireEditorCommandProvider } from './editor/AspireEditorCommandProvider';
import type { AspireDebugConsoleOutputEvent } from './types/extensionApi';
import { extensionLogOutputChannel } from './utils/logging';
import { resetEditorAssistanceWindowState } from './services/editorAssistanceWindowState';
import { type EditorResourceSessionSnapshot } from './services/appHostLaunchContracts';
import { getOrCreateIdentityForCurrentAppHostTarget, type OpaqueAppHostIdentity } from './utils/appHostIdentity';

export class AspireExtensionContext implements vscode.Disposable {
    private static readonly _cliStopTimeoutMs = 5_000;

    private _rpcServer?: AspireRpcServer;
    private _dcpServer?: AspireDcpServer;
    private _extensionContext?: vscode.ExtensionContext;
    private _debugConfigProvider?: AspireDebugConfigurationProvider;
    private _terminalProvider?: AspireTerminalProvider;
    private _editorCommandProvider?: AspireEditorCommandProvider;

    private _aspireDebugSessions: AspireDebugSession[] = [];
    private readonly _debugSessionStateSubscriptions = new Map<string, vscode.Disposable>();
    private readonly _debugSessionOutputSubscriptions = new Map<string, vscode.Disposable>();
    private readonly _onDidChangeDebugSessions = new vscode.EventEmitter<void>();
    private readonly _onDidReceiveDebugConsoleOutput = new vscode.EventEmitter<AspireDebugConsoleOutputEvent>();
    private readonly _lateDebugSessionStops: Promise<void>[] = [];
    private _shutdownPromise?: Promise<void>;
    private _isShuttingDown = false;
    private _hasOrderedDebugSessionStopSnapshot = false;
    private _isFinalizingShutdown = false;
    private _isShutdownRegistrationClosed = false;
    private _isDisposed = false;
    readonly onDidChangeDebugSessions = this._onDidChangeDebugSessions.event;
    readonly onDidReceiveDebugConsoleOutput = this._onDidReceiveDebugConsoleOutput.event;

    initialize(rpcServer: AspireRpcServer, extensionContext: vscode.ExtensionContext, debugConfigProvider: AspireDebugConfigurationProvider, dcpServer: AspireDcpServer, terminalProvider: AspireTerminalProvider, editorCommandProvider: AspireEditorCommandProvider): void {
        this._rpcServer = rpcServer;
        this._extensionContext = extensionContext;
        this._debugConfigProvider = debugConfigProvider;
        this._dcpServer = dcpServer;
        this._terminalProvider = terminalProvider;
        this._editorCommandProvider = editorCommandProvider;
    }

    get rpcServer(): AspireRpcServer {
        if (!this._rpcServer) {
            throw new Error(extensionContextNotInitialized);
        }
        return this._rpcServer;
    }

    get dcpServer(): AspireDcpServer {
        if (!this._dcpServer) {
            throw new Error(extensionContextNotInitialized);
        }
        return this._dcpServer;
    }

    get extensionContext(): vscode.ExtensionContext {
        if (!this._extensionContext) {
            throw new Error(extensionContextNotInitialized);
        }
        return this._extensionContext;
    }

    getAspireDebugSession(debugSessionId: string | null): AspireDebugSession | null {
        if (!debugSessionId) {
            return null;
        }

        return this._aspireDebugSessions.find(session => session.debugSessionId === debugSessionId && !session.isDisposed) || null;
    }

    get aspireDebugSessions(): readonly AspireDebugSession[] {
        // Disposed sessions can remain tracked only as CLI process owners. They must still be
        // visible to deactivation, but not to RPC lookups or extension-state snapshots.
        return this._aspireDebugSessions.filter(session => !session.isDisposed);
    }

    get editorResourceSessions(): readonly EditorResourceSessionSnapshot[] {
        return this.aspireDebugSessions.flatMap(session =>
            session.editorResourceSessions.map(resourceSession => ({
                appHostPath: resourceSession.appHostPath,
                ...(resourceSession.appHostIdentity === undefined
                    ? {}
                    : { appHostIdentity: resourceSession.appHostIdentity }),
                targetPath: resourceSession.targetPath,
                ...(resourceSession.resourceExecutablePaths === undefined
                    ? {}
                    : { resourceExecutablePaths: [...resourceSession.resourceExecutablePaths] }),
                state: resourceSession.state,
                mode: resourceSession.mode,
            })));
    }

    getAspireDebugSessionsForAppHostIdentity(identity: OpaqueAppHostIdentity): readonly AspireDebugSession[] {
        return this.aspireDebugSessions.filter(session => {
            const appHostPath = session.resolvedAppHostPath ?? session.appHostPath;
            return session.operationKind === 'run' &&
                appHostPath !== undefined &&
                (session.appHostIdentity ?? getOrCreateIdentityForCurrentAppHostTarget(appHostPath)) === identity;
        });
    }

    getAspireDebugSessionDashboardOwners(): readonly {
        readonly appHostIdentity: OpaqueAppHostIdentity;
        readonly session: AspireDebugSession;
    }[] {
        return this.aspireDebugSessions.flatMap(session => {
            const appHostPath = session.resolvedAppHostPath ?? session.appHostPath;
            if (session.operationKind !== 'run' || appHostPath === undefined) {
                return [];
            }

            return [{
                appHostIdentity: session.appHostIdentity ?? getOrCreateIdentityForCurrentAppHostTarget(appHostPath),
                session,
            }];
        });
    }

    addAspireDebugSession(debugSession: AspireDebugSession) {
        if (this._isDisposed) {
            // `_disposeCore` disposes exactly the sessions present when it takes its snapshot, and
            // it never runs twice. Tracking a session that arrives afterwards would therefore mean
            // never disposing it at all: its CLI would keep running with nothing left alive to stop
            // it. Sessions arriving *before* teardown are still accepted — `_waitForCliStopRequests`
            // re-scans for them, and `_disposeCore` then disposes them on the normal path.
            extensionLogOutputChannel.warn(`Refusing Aspire debug session ${debugSession.debugSessionId} because the extension has already been torn down; disposing it immediately.`);
            this._forceFinalizeUntrackedDebugSession(debugSession);
            return;
        }

        if (this._isShutdownRegistrationClosed) {
            // The final drain is still responsible for sessions that arrive after registration
            // closes. Keep their complete ordered/CLI/process finalization in that drain so shared
            // RPC and DCP infrastructure cannot be disposed while a resource adapter is stopping.
            const stop = this._finalizeLateDebugSession(debugSession);
            void stop.catch(() => { });
            this._lateDebugSessionStops.push(stop);
            return;
        }

        if (this._isFinalizingShutdown) {
            // The initial ordered drain has closed. Do not register a new owner that the CLI-stop
            // scan and final process sweep could observe before its resource debug sessions settle.
            const stop = this._finalizeLateDebugSession(debugSession);
            void stop.catch(() => { });
            this._lateDebugSessionStops.push(stop);
            return;
        }

        if (this._aspireDebugSessions.find(session => session.debugSessionId === debugSession.debugSessionId)) {
            throw new Error(debugSessionAlreadyExists(debugSession.debugSessionId));
        }

        this._aspireDebugSessions.push(debugSession);
        this._debugSessionStateSubscriptions.set(debugSession.debugSessionId, debugSession.onDidChangeState(() => this._onDidChangeDebugSessions.fire()));
        this._debugSessionOutputSubscriptions.set(debugSession.debugSessionId, debugSession.onDidSendDebugConsoleOutput(event => this._onDidReceiveDebugConsoleOutput.fire(event)));
        this._onDidChangeDebugSessions.fire();

        if (this._isShuttingDown && this._hasOrderedDebugSessionStopSnapshot) {
            const orderedStop = (async () => debugSession.stopDebugging())();
            // The drain below owns the failure, but observe it immediately so a rejection that
            // arrives before the next drain turn is never reported as unhandled.
            void orderedStop.catch(() => { });
            this._lateDebugSessionStops.push(orderedStop);
        }
    }

    removeAspireDebugSession(debugSession: AspireDebugSession) {
        this._aspireDebugSessions = this._aspireDebugSessions.filter(session => session.debugSessionId !== debugSession.debugSessionId);
        this._debugSessionStateSubscriptions.get(debugSession.debugSessionId)?.dispose();
        this._debugSessionStateSubscriptions.delete(debugSession.debugSessionId);
        this._debugSessionOutputSubscriptions.get(debugSession.debugSessionId)?.dispose();
        this._debugSessionOutputSubscriptions.delete(debugSession.debugSessionId);
        this._onDidChangeDebugSessions.fire();
    }

    get debugConfigProvider(): AspireDebugConfigurationProvider | undefined {
        if (!this._debugConfigProvider) {
            throw new Error(extensionContextNotInitialized);
        }

        return this._debugConfigProvider;
    }

    deactivate(): Promise<void> {
        if (this._shutdownPromise) {
            return this._shutdownPromise;
        }

        if (this._isDisposed) {
            return Promise.resolve();
        }

        this._isShuttingDown = true;
        // Schedule the async work after storing the shared promise so a reentrant dispose/deactivate
        // call cannot begin synchronous teardown between the stop request and the first await.
        this._shutdownPromise = Promise.resolve().then(() => this._deactivateCore());
        return this._shutdownPromise;
    }

    dispose(): void {
        if (this._isDisposed || this._isShuttingDown) {
            return;
        }

        this._disposeCore();
    }

    private async _deactivateCore(): Promise<void> {
        let stopFailures: unknown[] = [];
        try {
            // Finish debugger shutdown before asking the CLI to exit. A cooperative CLI stop can
            // tear down the AppHost process immediately, so running these phases concurrently would
            // violate the resource -> AppHost -> synthetic parent ordering.
            stopFailures = await this._waitForOrderedDebugSessionStops();
            await this._waitForCliStopRequests();

            // A session can be registered while the CLI requests are settling, after the initial
            // ordered-stop drain has closed. Those sessions are refused as context owners and run
            // their complete ordered/CLI/process finalization through this tracked drain.
            this._isShutdownRegistrationClosed = true;
            stopFailures.push(...await this._drainLateDebugSessionStops(true));
        }
        finally {
            await this._forceTerminateCliProcesses();
            this._disposeCore();
        }

        if (stopFailures.length === 1) {
            throw stopFailures[0];
        }
        if (stopFailures.length > 1) {
            throw new AggregateError(stopFailures);
        }
    }

    private async _waitForOrderedDebugSessionStops(): Promise<unknown[]> {
        // Sessions registered after deactivate() but before this synchronous snapshot are already
        // included below. Queue only later registrations separately so one failed stop is not
        // reported once from the snapshot and again from the late-stop drain.
        this._hasOrderedDebugSessionStopSnapshot = true;
        const sessions = [...this._aspireDebugSessions];
        const results: PromiseSettledResult<void>[] = await Promise.allSettled(
            sessions.map(async session => session.stopDebugging()));
        const stopFailures = results
            .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
            .map(result => result.reason);

        // Close tracked registration before the late drain. Any session arriving from this point
        // gets its own complete finalizer, so it cannot enter the main session array after the
        // ordered snapshot and race the CLI-stop phase.
        this._isFinalizingShutdown = true;
        stopFailures.push(...await this._drainLateDebugSessionStops());

        return stopFailures;
    }

    private async _drainLateDebugSessionStops(finalizeShutdown = false): Promise<unknown[]> {
        const stopFailures: unknown[] = [];
        while (this._lateDebugSessionStops.length > 0) {
            const lateStops = this._lateDebugSessionStops.splice(0);
            const results = await Promise.allSettled(lateStops);
            stopFailures.push(...results
                .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
                .map(result => result.reason));
        }

        if (finalizeShutdown) {
            await this._forceTerminateCliProcesses();
            // A session can arrive while process termination is awaited. Drain it before taking
            // the final synchronous empty-check-and-dispose step.
            while (this._lateDebugSessionStops.length > 0) {
                stopFailures.push(...await this._drainLateDebugSessionStops());
            }
            this._disposeCore();
        }

        return stopFailures;
    }

    private async _finalizeLateDebugSession(debugSession: AspireDebugSession): Promise<void> {
        const stopFailures: unknown[] = [];
        try {
            await debugSession.stopDebugging();
        }
        catch (error) {
            stopFailures.push(error);
        }

        try {
            const cliStop = debugSession.requestCliStopForExtensionShutdown();
            await this._settleStopRequests(
                [cliStop],
                Date.now() + AspireExtensionContext._cliStopTimeoutMs);
        }
        finally {
            try {
                await this._terminateCliProcesses([debugSession]);
            }
            finally {
                debugSession.finalizeForExtensionShutdown();
            }
        }

        if (stopFailures.length > 0) {
            throw stopFailures[0];
        }
    }

    private _forceFinalizeUntrackedDebugSession(debugSession: AspireDebugSession): void {
        void debugSession.stopDebugging().catch(error => {
            extensionLogOutputChannel.error(`Failed to stop Aspire debug session '${debugSession.debugSessionId}' during final extension teardown: ${error}`);
        });
        void debugSession.requestCliStopForExtensionShutdown().catch(error => {
            extensionLogOutputChannel.warn(`Failed to stop Aspire CLI during final extension teardown: ${error}`);
        });
        void debugSession.terminateCliProcessTree({ force: true }).then(
            () => debugSession.finalizeForExtensionShutdown(),
            () => debugSession.finalizeForExtensionShutdown());
    }

    private async _waitForCliStopRequests(): Promise<void> {
        const requested = new Map<string, Promise<void>>();
        const deadline = Date.now() + AspireExtensionContext._cliStopTimeoutMs;

        // Re-snapshot after every await. `_isShuttingDown` does not stop `addAspireDebugSession`
        // from registering a session, so a debug-adapter descriptor or an RPC-triggered
        // `startDebugSession` that lands mid-await would never be asked to stop if the array were
        // captured only once. Requesting a stop is idempotent per session, so re-scanning is safe.
        while (Date.now() < deadline && this._collectStopRequests(requested)) {
            const timedOut = await this._settleStopRequests([...requested.values()], deadline);
            if (timedOut) {
                extensionLogOutputChannel.warn(`Timed out after ${AspireExtensionContext._cliStopTimeoutMs}ms waiting for Aspire CLI stop requests; continuing extension teardown.`);
                break;
            }
        }
    }

    private async _forceTerminateCliProcesses(): Promise<void> {
        await this._terminateCliProcesses([...this._aspireDebugSessions]);
    }

    private async _terminateCliProcesses(sessions: AspireDebugSession[]): Promise<void> {
        // A cooperative stop that resolved, rejected or timed out proves only what happened to the
        // RPC request; the CLI process can still be running. Signal any that are, so deactivation
        // cannot leave an AppHost and its resource processes orphaned.
        const terminations = Promise.all(sessions.map(async session => {
            try {
                // Force rather than signal-and-schedule: `terminateCliProcess` escalates to a hard
                // kill on an `unref`'d timer, and `_deactivateCore` resolves immediately after this
                // sweep, so the extension host can exit before that timer fires. The cooperative
                // deadline above was this CLI's grace period; there is no second one.
                await session.terminateCliProcessTree({ force: true });
            }
            catch (error) {
                extensionLogOutputChannel.warn(`Failed to terminate the Aspire CLI process during extension deactivation: ${error}`);
            }
        }));
        let timeout: ReturnType<typeof setTimeout> | undefined;
        const completed = await Promise.race([
            terminations.then(() => true),
            new Promise<false>(resolve => {
                timeout = setTimeout(() => resolve(false), AspireExtensionContext._cliStopTimeoutMs);
            }),
        ]);
        if (timeout) {
            clearTimeout(timeout);
        }
        if (!completed) {
            extensionLogOutputChannel.warn(
                `Timed out after ${AspireExtensionContext._cliStopTimeoutMs}ms waiting for Aspire CLI process-tree termination; continuing extension teardown.`);
        }
    }

    /**
     * Requests a CLI stop for every registered session that has not been asked yet, returning
     * whether any new session was found.
     */
    private _collectStopRequests(requested: Map<string, Promise<void>>): boolean {
        let addedRequest = false;
        for (const session of this._aspireDebugSessions) {
            if (requested.has(session.debugSessionId)) {
                continue;
            }

            addedRequest = true;
            try {
                requested.set(session.debugSessionId, session.requestCliStopForExtensionShutdown());
            }
            catch (error) {
                requested.set(session.debugSessionId, Promise.reject(error));
            }
        }

        return addedRequest;
    }

    private async _settleStopRequests(stopRequests: Promise<void>[], deadline: number): Promise<boolean> {
        const allStops = Promise.allSettled(stopRequests);
        let timeout: ReturnType<typeof setTimeout> | undefined;
        const outcome = await Promise.race([
            allStops.then(results => ({ timedOut: false as const, results })),
            new Promise<{ timedOut: true }>(resolve => {
                timeout = setTimeout(() => {
                    timeout = undefined;
                    resolve({ timedOut: true });
                }, Math.max(0, deadline - Date.now()));
            }),
        ]);

        if (timeout) {
            clearTimeout(timeout);
        }

        if (outcome.timedOut) {
            return true;
        }

        const failures = outcome.results
            .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
            .map(result => result.reason);
        for (const failure of failures) {
            // Closing the RPC transport rejects its outstanding stop request even though the
            // synchronous debug-session and terminal teardown below has completed successfully.
            if (failure instanceof ResponseError && failure.code === ErrorCodes.PendingResponseRejected) {
                extensionLogOutputChannel.info(`Aspire CLI stop request ended after the RPC transport closed: ${failure}`);
            }
            else {
                extensionLogOutputChannel.warn(`Failed to stop Aspire CLI during extension deactivation: ${failure}`);
            }
        }

        return false;
    }

    private _disposeCore(): void {
        if (this._isDisposed) {
            return;
        }

        this._isDisposed = true;
        resetEditorAssistanceWindowState();
        this._debugSessionStateSubscriptions.forEach(disposable => disposable.dispose());
        this._debugSessionStateSubscriptions.clear();
        this._debugSessionOutputSubscriptions.forEach(disposable => disposable.dispose());
        this._debugSessionOutputSubscriptions.clear();
        const sessions = this._aspireDebugSessions.splice(0);
        sessions.forEach(session => session.finalizeForExtensionShutdown());
        this._rpcServer?.dispose();
        this._dcpServer?.dispose();
        this._terminalProvider?.dispose();
        this._editorCommandProvider?.dispose();
        this._onDidChangeDebugSessions.dispose();
        this._onDidReceiveDebugConsoleOutput.dispose();
    }
}
