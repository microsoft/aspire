import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration, type AspireResourceDebugSession } from '../dcp/types';
import { appHostLifecycleIsolationCapabilityCouldNotBeVerified, appHostLifecycleIsolationModeNotSupported, startDebuggingDeclined } from '../loc/strings';
import { ensureIsolatedCliArg, getRootIsolatedCliArg, isLinkedGitWorktree } from '../utils/gitWorktree';
import { compareAppHostIdentity, getAppHostIdentityKeyInfo, isAppHostPathWithinDirectory, type AppHostIdentityKeyInfo, type AppHostIdentityRelation } from '../utils/appHostIdentity';
import { classifyError, isCommandCancellation, sendTelemetryEvent, type EventProperties } from '../utils/telemetry';
import { extensionLogOutputChannel } from '../utils/logging';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { CliPathResolutionTarget, getCliPathTargetForUri, getCliPathTargetKey } from '../utils/cliPathVariables';
import { appHostLaunchReservationIdConfigKey, appHostLaunchTokenConfigKey, appHostRestartSourceSessionIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey, type AppHostSelectionOrigin } from '../debugger/AspireDebugConfigurationMetadata';
import { markAspireDebugConfigurationAsExtensionOwned } from '../debugger/AspireDebugConfigurationProviderInternal';
import { AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, externalLaunchReservationTimeoutMs, type AppHostDebugSessionTerminatedEvent, type AppHostEditorSessions, type AppHostLaunchRequestedEvent, type AppHostLaunchSession, type AppHostOperationState, type AppHostStopResult, type RunningAppHost } from './appHostLaunchContracts';
import { AppHostLaunchReservations } from './appHostLaunchReservations';
import { getLaunchTelemetryProperties, isE2eDebugLaunchSuppressed } from './appHostLaunchTelemetry';
import { isolatedLaunchCapability, isolatedLaunchMinimumVersion, type CapabilityStatus } from '../types/configInfo';

export { AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, externalLaunchReservationTimeoutMs } from './appHostLaunchContracts';
export type { AppHostDebugSessionTerminatedEvent, AppHostEditorSessions, AppHostLaunchRequestedEvent, AppHostLaunchSession, AppHostOperationState, AppHostStopResult, RunningAppHost } from './appHostLaunchContracts';

export interface AppHostLaunchCapabilityProvider {
    getCapabilityStatus(capability: string, options?: {
        suppressErrors?: boolean;
        forceRefresh?: boolean;
        cliPath?: string;
        cancellationToken?: vscode.CancellationToken;
        minimumVersion?: string;
        target?: CliPathResolutionTarget;
    }): Promise<CapabilityStatus>;
}

export interface AppHostLaunchIsolation {
    readonly effective: boolean;
    readonly option: boolean | undefined;
}

type AppHostLaunchIsolationPolicy = 'explicit-only' | 'linked-worktree-default';

export interface PreparedAppHostLaunchArguments {
    readonly args: string[] | undefined;
    readonly isolation: AppHostLaunchIsolation;
}

function isAspireCommandType(value: unknown): value is AspireCommandType {
    return value === 'run' || value === 'deploy' || value === 'publish' || value === 'do';
}

/**
 * The Aspire command an `aspire` debug configuration will run, or `undefined` when the
 * configuration names something this extension does not recognize.
 */
export function getAspireDebugConfigurationCommand(configuration: vscode.DebugConfiguration): AspireCommandType | undefined {
    // Run is the default Aspire command when omitted from launch configuration.
    if (configuration.command === undefined || configuration.command === null) {
        return 'run';
    }

    return isAspireCommandType(configuration.command) ? configuration.command : undefined;
}

function getDebugConfigurationAppHostPath(configuration: vscode.DebugConfiguration): string | undefined {
    const telemetryTargetPath = configuration[appHostTelemetryTargetPathConfigKey];
    if (typeof telemetryTargetPath === 'string') {
        return telemetryTargetPath;
    }

    return typeof configuration.program === 'string' ? configuration.program : undefined;
}

interface TrackedAppHostDebugSession {
    readonly owner: AppHostLaunchSession;
    readonly session: AppHostLaunchSession;
}

interface TrackedAppHostOperationState extends AppHostOperationState {
    readonly isDirectoryScope?: boolean;
}

/**
 * Centralizes all Aspire AppHost launch operations that require a resolved
 * AppHost path. Both the editor command provider (which discovers the path)
 * and the tree provider (which extracts it from a tree item) delegate here.
 *
 * Also tracks which AppHost paths are currently in a "launching" state
 * (between the user clicking Run/Debug and the AppHost appearing in the
 * running list or the debug session terminating).
 */
export class AppHostLaunchService implements vscode.Disposable {
    private readonly _appHostDebugSessions = new Map<string, TrackedAppHostDebugSession>();
    private readonly _reservations = new AppHostLaunchReservations({
        getEditorRunSessions: appHostPath => this.getEditorRunSessions(appHostPath),
        hasEditorRunSessionWithinDirectory: directoryPath => this.hasEditorRunSessionWithinDirectory(directoryPath),
        hasActiveLifecycleOperation: appHostPath => this.hasActiveLifecycleOperation(appHostPath),
        hasActiveLifecycleOperationWithinDirectory: directoryPath => this.hasActiveLifecycleOperationWithinDirectory(directoryPath),
    });
    private readonly _lifecycleLocks = new Map<string, Promise<unknown>>();
    private readonly _lifecycleLockPathKeys = new Map<string, Set<string>>();
    private readonly _pendingOrActiveLifecycleOperationPathKeys = new Map<number, Set<string>>();
    private _nextLifecycleOperationId = 0;
    private readonly _lifecycleCancellationSource = new vscode.CancellationTokenSource();
    private _getEditorSessions: () => readonly AppHostLaunchSession[] = () => [];
    private _getRunningAppHosts: (token: vscode.CancellationToken) => Promise<readonly RunningAppHost[]> = async () => [];
    private _stopExternalAppHost: ((appHostPath: string, token: vscode.CancellationToken) => Promise<void>) | undefined;
    private _disposed = false;
    private readonly _activeRunDebugSessionPaths = new Map<string, string>();
    private readonly _pendingRunPathByToken = new Map<number, string>();
    /**
     * Durable non-Run operations (deploy/publish/do) that have begun launch preparation but
     * whose root debug session has not started yet, keyed by launch token. A pending entry
     * is recorded before the first `await` so a concurrent duplicate is rejected, and is
     * either transferred to {@link _activeOperationBySessionId} when the session starts or
     * cleared when the launch is cancelled, declined, suppressed, errors, or disposes.
     */
    private readonly _pendingOperationByToken = new Map<number, TrackedAppHostOperationState>();
    /**
     * Operations started from launch.json/F5 never pass through {@link launch}. Their short-lived
     * reservations cover the gap between debug configuration resolution and the root session start.
     */
    private readonly _pendingExternalOperationByReservationId = new Map<string, TrackedAppHostOperationState>();
    private readonly _pendingExternalOperationExpiryByReservationId = new Map<string, ReturnType<typeof setTimeout>>();
    private readonly _restartOperationExpiryByToken = new Map<number, ReturnType<typeof setTimeout>>();
    /**
     * Durable non-Run operations whose root debug session is running, keyed by that
     * session's ID. Cleared when the session terminates.
     */
    private readonly _activeOperationBySessionId = new Map<string, TrackedAppHostOperationState>();
    private _nextLaunchToken = 0;
    private _nextExternalOperationReservationId = 0;

    readonly onDidChangeLaunchingState = this._reservations.onDidChangeLaunchingState;

    private readonly _onDidChangeOperationState = new vscode.EventEmitter<void>();
    readonly onDidChangeOperationState = this._onDidChangeOperationState.event;

    private readonly _onDidTerminateAppHostDebugSession = new vscode.EventEmitter<AppHostDebugSessionTerminatedEvent>();
    readonly onDidTerminateAppHostDebugSession = this._onDidTerminateAppHostDebugSession.event;

    private readonly _onDidRequestLaunch = new vscode.EventEmitter<AppHostLaunchRequestedEvent>();
    readonly onDidRequestLaunch = this._onDidRequestLaunch.event;

    private readonly _debugSessionSubscription: vscode.Disposable;

    constructor(private readonly _capabilityProvider: AppHostLaunchCapabilityProvider) {
        const startSubscription = vscode.debug.onDidStartDebugSession(session => {
            const launchToken = session.configuration?.[appHostLaunchTokenConfigKey];
            let transferredOperation = false;
            if (typeof launchToken === 'number') {
                this._pendingRunPathByToken.delete(launchToken);
                // The launch token only rides on the root configuration this service creates,
                // so its presence proves this is the root session that now owns any pending
                // non-Run operation.
                transferredOperation = this.transferPendingOperationToActiveSession(launchToken, session.id);
            }

            const appHostPath = getDebugConfigurationAppHostPath(session.configuration);
            const reservationId = session.configuration?.[appHostLaunchReservationIdConfigKey];
            const command = getAspireDebugConfigurationCommand(session.configuration);
            if (!transferredOperation &&
                appHostPath &&
                typeof reservationId === 'string' &&
                command !== undefined &&
                command !== 'run') {
                transferredOperation = this.transferPendingExternalOperationToActiveSession(
                    reservationId,
                    appHostPath,
                    session.id);
            }
            if (appHostPath && typeof reservationId === 'string') {
                if (transferredOperation) {
                    // The active operation is now owned by this session. Its temporary launch
                    // reservation must not block an independent Run or F5 for the same AppHost.
                    this.clearMatchingLaunching(appHostPath, reservationId);
                }
                else {
                    this._reservations.preserveStartedExternalLaunchReservation(appHostPath, reservationId);
                }
            }
            if (appHostPath &&
                session.configuration?.type === 'aspire' &&
                getAspireDebugConfigurationCommand(session.configuration) === 'run') {
                this._activeRunDebugSessionPaths.set(session.id, appHostPath);
            }
        });

        // When a debug session terminates, clear launching state for that AppHost
        // so the tree reverts from "Starting..." if the launch failed or was cancelled.
        const terminateSubscription = vscode.debug.onDidTerminateDebugSession(session => {
            const launchToken = session.configuration?.[appHostLaunchTokenConfigKey];
            const restartSourceSessionId = session.configuration?.[appHostRestartSourceSessionIdConfigKey];
            const isToolbarRestart = typeof restartSourceSessionId === 'string' &&
                restartSourceSessionId === session.id;
            this._activeRunDebugSessionPaths.delete(session.id);
            if (isToolbarRestart && typeof launchToken === 'number') {
                this.preserveActiveOperationForRestart(session.id, launchToken);
            }
            else {
                this.clearActiveOperation(session.id);
            }
            if (typeof launchToken === 'number') {
                this._pendingRunPathByToken.delete(launchToken);
                if (!isToolbarRestart) {
                    this.clearPendingOperation(launchToken);
                }
            }

            this._appHostDebugSessions.delete(session.id);
            const appHostPath = getDebugConfigurationAppHostPath(session.configuration);
            if (appHostPath && session.configuration?.type === 'aspire') {
                const reservationId = session.configuration?.[appHostLaunchReservationIdConfigKey];
                const isCurrentGeneration = typeof reservationId !== 'string' ||
                    this._reservations.isLatestLaunchReservation(appHostPath, reservationId);
                if (typeof reservationId === 'string') {
                    this.clearMatchingLaunching(appHostPath, reservationId);
                }
                const command = getAspireDebugConfigurationCommand(session.configuration);
                const shouldRequestStopRefresh = command === 'run' && isCurrentGeneration;
                this._onDidTerminateAppHostDebugSession.fire({
                    appHostPath,
                    command,
                    shouldRequestStopRefresh,
                    shouldMarkAppHostStopping: shouldRequestStopRefresh &&
                        !isToolbarRestart &&
                        !this.hasPendingOrActiveRunDebugSession(appHostPath),
                });
            }
        });
        this._debugSessionSubscription = vscode.Disposable.from(startSubscription, terminateSubscription);
    }

    dispose(): void {
        this._disposed = true;
        this._lifecycleCancellationSource.cancel();
        this._lifecycleCancellationSource.dispose();
        this._debugSessionSubscription.dispose();
        this._lifecycleLocks.clear();
        this._lifecycleLockPathKeys.clear();
        this._appHostDebugSessions.clear();
        this._reservations.dispose();
        this._activeRunDebugSessionPaths.clear();
        this._pendingRunPathByToken.clear();
        this._pendingOperationByToken.clear();
        this._pendingExternalOperationByReservationId.clear();
        for (const expiry of this._pendingExternalOperationExpiryByReservationId.values()) {
            clearTimeout(expiry);
        }
        this._pendingExternalOperationExpiryByReservationId.clear();
        for (const expiry of this._restartOperationExpiryByToken.values()) {
            clearTimeout(expiry);
        }
        this._restartOperationExpiryByToken.clear();
        this._activeOperationBySessionId.clear();
        this._pendingOrActiveLifecycleOperationPathKeys.clear();
        this._onDidTerminateAppHostDebugSession.dispose();
        this._onDidRequestLaunch.dispose();
        this._onDidChangeOperationState.dispose();
    }

    get launchingPaths(): readonly string[] {
        return this._reservations.launchingPaths;
    }

    get pendingLifecycleOperationCount(): number {
        return this._lifecycleLocks.size;
    }

    setEditorSessionProvider(provider: () => readonly AppHostLaunchSession[]): void {
        this._getEditorSessions = provider;
    }

    setRunningAppHostProvider(provider: (token: vscode.CancellationToken) => Promise<readonly RunningAppHost[]>): void {
        this._getRunningAppHosts = provider;
    }

    setExternalAppHostStopper(stopper: (appHostPath: string, token: vscode.CancellationToken) => Promise<void>): void {
        this._stopExternalAppHost = stopper;
    }

    trackAppHostDebugSession(owner: AppHostLaunchSession, appHostPath: string, debugSession: AspireResourceDebugSession): void {
        const session: AppHostLaunchSession = {
            appHostPath,
            resolvedAppHostPath: appHostPath,
            operationKind: owner.operationKind,
            get startupCompleted() { return owner.startupCompleted; },
            configuration: owner.configuration,
            stopDebugging: async () => { await owner.stopDebugging(); },
        };
        this._appHostDebugSessions.set(debugSession.id, { owner, session });
    }

    /**
     * Returns the editor-created `run` sessions for an AppHost, and whether any session's
     * relationship to it could not be proven.
     *
     * A session's own {@link AppHostLaunchSession.resolvedAppHostPath} is authoritative
     * when present: the debug configuration provider only sets it after resolving a
     * folder to a single unambiguous candidate, whereas `appHostPath` is then just the
     * folder. Falling back to `appHostPath` for those sessions would compare a directory
     * against a file and quietly report "no session".
     */
    getEditorRunSessions(appHostPath: string): AppHostEditorSessions {
        const sessions: AppHostLaunchSession[] = [];
        let ambiguous = false;
        const editorSessions = this._getEditorSessions();
        const fallbackSessions = [...this._appHostDebugSessions.values()]
            .filter(tracked => {
                if (!editorSessions.includes(tracked.owner)) {
                    return true;
                }

                const ownerPath = tracked.owner.resolvedAppHostPath ?? tracked.owner.appHostPath;
                return compareAppHostIdentity(ownerPath, tracked.session.appHostPath) !== 'same';
            })
            .map(tracked => tracked.session);
        for (const session of [...editorSessions, ...fallbackSessions]) {
            if (session.operationKind !== 'run') {
                continue;
            }

            const sessionPath = session.resolvedAppHostPath ?? session.appHostPath;
            switch (compareAppHostIdentity(sessionPath, appHostPath)) {
                case 'same':
                    sessions.push(session);
                    break;
                case 'ambiguous':
                    ambiguous = true;
                    break;
            }
        }

        return { sessions, ambiguous };
    }

    async getRunningAppHosts(token: vscode.CancellationToken): Promise<readonly RunningAppHost[]> {
        throwIfCancelled(token);
        const appHosts = await this._getRunningAppHosts(token);
        throwIfCancelled(token);
        return appHosts;
    }

    async stopAppHost(appHostPath: string, token: vscode.CancellationToken = this._lifecycleCancellationSource.token): Promise<AppHostStopResult> {
        throwIfCancelled(token);

        return await this.runWithAppHostLifecycleLock(appHostPath, token, lockToken =>
            this.stopAppHostFromLifecycleOwner(appHostPath, lockToken));
    }

    async stopAppHostFromLifecycleOwner(appHostPath: string, token: vscode.CancellationToken): Promise<AppHostStopResult> {
        throwIfCancelled(token);
        const initialEditorResult = await this.stopEditorAppHostIfControlled(appHostPath, token);
        if (initialEditorResult) {
            return initialEditorResult;
        }

        const externalRelation = await this.getRunningAppHostRelation(appHostPath, token);
        const currentEditorResult = await this.stopEditorAppHostIfControlled(appHostPath, token);
        if (currentEditorResult) {
            return currentEditorResult;
        }

        if (externalRelation === 'different') {
            return { outcome: 'notRunning', controller: 'none' };
        }
        if (externalRelation === 'ambiguous') {
            return { outcome: 'ambiguousAppHost', controller: 'external' };
        }

        if (!this._stopExternalAppHost) {
            throw new AppHostStopError('external', undefined, new Error('No external AppHost stopper is configured.'));
        }

        throwIfCancelled(token);
        try {
            await this._stopExternalAppHost(appHostPath, token);
        }
        catch (error) {
            if (isCommandCancellation(error)) {
                throw new AppHostStopCancellationError('external', undefined);
            }
            throw new AppHostStopError('external', undefined, error);
        }
        return { outcome: 'stopped', controller: 'external' };
    }

    private async stopEditorAppHostIfControlled(appHostPath: string, token: vscode.CancellationToken): Promise<AppHostStopResult | undefined> {
        const editorSessions = this.getEditorRunSessions(appHostPath);
        if (editorSessions.sessions.length > 1 ||
            (editorSessions.sessions.length === 0 && editorSessions.ambiguous)) {
            return { outcome: 'ambiguousSession', controller: 'editor' };
        }

        if (editorSessions.sessions.length === 1) {
            const session = editorSessions.sessions[0];
            const noDebug = session.configuration.noDebug === true;
            throwIfCancelled(token);
            try {
                await session.stopDebugging();
            }
            catch (error) {
                if (isCommandCancellation(error)) {
                    throw new AppHostStopCancellationError('editor', noDebug);
                }
                throw new AppHostStopError('editor', noDebug, error);
            }
            return {
                outcome: 'stopped',
                controller: 'editor',
                noDebug,
            };
        }

        return this.isLaunching(appHostPath)
            ? { outcome: 'alreadyStarting', controller: 'editor' }
            : undefined;
    }

    private async getRunningAppHostRelation(appHostPath: string, token: vscode.CancellationToken): Promise<AppHostIdentityRelation> {
        const runningAppHosts = await this.getRunningAppHosts(token);
        let relation: AppHostIdentityRelation = 'different';
        for (const runningAppHost of runningAppHosts) {
            const current = compareAppHostIdentity(runningAppHost.appHostPath, appHostPath);
            if (current === 'same') {
                return 'same';
            }
            if (current === 'ambiguous') {
                relation = 'ambiguous';
            }
        }

        return relation;
    }

    compareAppHostIdentity(left: string | undefined, right: string | undefined): AppHostIdentityRelation {
        return compareAppHostIdentity(left, right);
    }

    /**
     * Runs `action` as the only lifecycle operation for this AppHost.
     *
     * `action` receives a token that is cancelled when the caller cancels *or* when the
     * operation outruns {@link appHostLifecycleLockMaxHoldMs}. The lock is held until
     * `action` settles either way: releasing it while the operation is still in flight
     * would admit a second start/stop alongside the first, which is the exact duplicate
     * this lock exists to prevent.
     */
    async runWithAppHostLifecycleLock<T>(appHostPath: string, token: vscode.CancellationToken, action: (token: vscode.CancellationToken) => Promise<T>): Promise<T> {
        throwIfCancelled(token);
        throwIfCancelled(this._lifecycleCancellationSource.token);
        const identity = getAppHostIdentityKeyInfo(appHostPath);
        const lifecycleOperationId = ++this._nextLifecycleOperationId;
        this._pendingOrActiveLifecycleOperationPathKeys.set(
            lifecycleOperationId,
            new Set(identity.pathKeys));
        const keys = this.getLifecycleLockKeys(identity);
        this.trackLifecycleLockPathKeys(keys[0], identity);
        // Waiting on every overlapping queue, not just the first, is what keeps exclusivity
        // across a directory mutation that merges two independently active identities. While a
        // second project file makes `First.csproj` and `Program.cs` ambiguous they hold separate
        // locks; once it is removed a caller's identity spans both, and queueing behind only one
        // of them would run this operation beside the other.
        const active = keys.map(lockKey => this._lifecycleLocks.get(lockKey)).filter(queue => queue !== undefined);
        const previous = active.length <= 1
            ? active[0] ?? Promise.resolve()
            : Promise.all(active).then(() => undefined, () => undefined);
        let release!: () => void;
        const gate = new Promise<void>(resolve => { release = resolve; });
        // The queue tail follows the prior owners and this operation's gate. A cancelled
        // waiter releases its gate only after the prior owners settle, so later callers
        // cannot overtake a still-running editor launch.
        const tail = previous.then(() => gate, () => gate);
        // Every merged key points at the same tail, so a later caller that only knows one of
        // them still queues behind this operation.
        for (const lockKey of keys) {
            this._lifecycleLocks.set(lockKey, tail);
        }

        const clearLifecycleLockIfOwned = () => {
            for (const lockKey of keys) {
                if (this._lifecycleLocks.get(lockKey) === tail) {
                    this._lifecycleLocks.delete(lockKey);
                    this._lifecycleLockPathKeys.delete(lockKey);
                }
            }
        };
        void tail.then(clearLifecycleLockIfOwned);

        let acquired = false;
        let holdTimeout: NodeJS.Timeout | undefined;
        const operationCancellation = new vscode.CancellationTokenSource();
        const callerCancellation = token.onCancellationRequested(() => operationCancellation.cancel());
        const serviceCancellation = this._lifecycleCancellationSource.token.onCancellationRequested(() => operationCancellation.cancel());
        try {
            await waitForPromise(previous, operationCancellation.token, appHostLifecycleLockWaitTimeoutMs);
            acquired = true;
            // An operation that outruns the bound is cancelled rather than abandoned. The
            // lock stays with it until it settles: forcing the gate open would let the next
            // start/stop run alongside an operation that is still tearing down containers
            // or still driving `startDebugging`, producing the duplicate lifecycle this
            // lock exists to prevent. Waiters give up on their own budget with `busy`,
            // which is a truthful answer while the AppHost really is mid-operation.
            holdTimeout = setTimeout(() => {
                extensionLogOutputChannel.warn(`AppHost lifecycle operation for ${appHostPath} exceeded ${appHostLifecycleLockMaxHoldMs}ms; cancelling it. The lifecycle lock is held until it settles.`);
                operationCancellation.cancel();
            }, appHostLifecycleLockMaxHoldMs);
            // The backstop must never be a reason for the host process to stay alive.
            holdTimeout.unref?.();
            throwIfCancelled(operationCancellation.token);
            return await action(operationCancellation.token);
        }
        finally {
            if (holdTimeout) {
                clearTimeout(holdTimeout);
            }
            callerCancellation.dispose();
            serviceCancellation.dispose();
            operationCancellation.dispose();
            this._pendingOrActiveLifecycleOperationPathKeys.delete(lifecycleOperationId);
            if (acquired) {
                release();
                // Clearing the final owner synchronously keeps the lock's observable lifetime
                // aligned with this promise. A queued owner has already replaced `tail`, so the
                // identity metadata remains intact when another operation is waiting.
                clearLifecycleLockIfOwned();
            }
            else {
                // Preserve queue ordering even though this caller no longer waits.
                const releaseCancelledWaiter = () => {
                    release();
                    clearLifecycleLockIfOwned();
                };
                void previous.then(releaseCancelledWaiter, releaseCancelledWaiter);
            }
        }
    }

    /**
     * Maps every path that {@link compareAppHostIdentity} reports as the same AppHost onto the
     * lifecycle lock keys an operation for it must queue behind.
     *
     * New lock owners use the identity model from {@link getAppHostIdentityKeyInfo}, but
     * active owners keep the exact project/source paths that were proven equivalent when
     * they entered. That snapshot is necessary because the directory can change while the
     * operation is still running: adding a second project should not let the original
     * project bypass the lock it already shares with `Program.cs`, and removing that
     * second project should not move a queued `Program.cs` caller onto a fresh key.
     *
     * More than one active key can overlap, because a directory mutation can merge identities
     * that were distinct - and therefore separately locked - when their operations started. All
     * of them are returned so the caller waits for each, rather than picking one and running
     * beside the rest.
     */
    private getLifecycleLockKeys(identity: AppHostIdentityKeyInfo): readonly string[] {
        const keys: string[] = [];
        for (const [activeKey, activePathKeys] of this._lifecycleLockPathKeys) {
            if (identity.pathKeys.some(pathKey => activePathKeys.has(pathKey))) {
                keys.push(activeKey);
            }
        }

        if (keys.length === 0) {
            return [identity.key];
        }

        // The identity's own key joins the wait when it is not already one of the merged keys, so
        // a caller addressing this AppHost by the merged identity queues behind this operation.
        if (!keys.includes(identity.key)) {
            keys.push(identity.key);
        }

        return keys;
    }

    private trackLifecycleLockPathKeys(key: string, identity: AppHostIdentityKeyInfo): void {
        let pathKeys = this._lifecycleLockPathKeys.get(key);
        if (!pathKeys) {
            pathKeys = new Set<string>();
            this._lifecycleLockPathKeys.set(key, pathKeys);
        }

        for (const pathKey of identity.pathKeys) {
            pathKeys.add(pathKey);
        }
    }

    isLaunching(appHostPath: string): boolean {
        return this._reservations.isLaunching(appHostPath);
    }

    tryReserveLaunch(appHostPath: string, trackRunGeneration = true): boolean {
        return this._reservations.tryReserveLaunch(appHostPath, trackRunGeneration);
    }

    hasLifecycleLaunchClaim(appHostPath: string): boolean {
        return this._reservations.hasLifecycleLaunchClaim(appHostPath);
    }

    reserveLaunch(appHostPath: string, trackRunGeneration = true): string {
        return this._reservations.reserveLaunch(appHostPath, trackRunGeneration);
    }

    tryReserveExternalLaunch(appHostPath: string, isDirectoryScope = false): string | false {
        return this._reservations.tryReserveExternalLaunch(appHostPath, isDirectoryScope);
    }

    validateOrReacquireExternalLaunchReservation(appHostPath: string, reservationId: string, isDirectoryScope = false): string | false {
        return this._reservations.validateOrReacquireExternalLaunchReservation(appHostPath, reservationId, isDirectoryScope);
    }

    replaceExternalLaunchReservation(previousAppHostPath: string, previousReservationId: string, appHostPath: string, isDirectoryScope = false): string | false {
        return this._reservations.replaceExternalLaunchReservation(previousAppHostPath, previousReservationId, appHostPath, isDirectoryScope);
    }

    releaseExternalLaunchReservation(appHostPath: string, reservationId: string): void {
        this._reservations.clearMatchingLaunching(appHostPath, reservationId);
    }

    private hasActiveLifecycleOperationWithinDirectory(directoryPath: string): boolean {
        return Array.from(this._pendingOrActiveLifecycleOperationPathKeys.values())
            .some(activePathKeys => Array.from(activePathKeys)
                .some(activePathKey => isAppHostPathWithinDirectory(activePathKey, directoryPath)));
    }

    private hasEditorRunSessionWithinDirectory(directoryPath: string): boolean {
        const sessions = [
            ...this._getEditorSessions(),
            ...Array.from(this._appHostDebugSessions.values(), tracked => tracked.session),
        ];
        return sessions.some(session => {
            const sessionPath = session.resolvedAppHostPath ?? session.appHostPath;
            return session.operationKind === 'run' &&
                sessionPath !== undefined &&
                isAppHostPathWithinDirectory(sessionPath, directoryPath);
        });
    }

    private hasActiveLifecycleOperation(appHostPath: string): boolean {
        for (const activePathKeys of this._pendingOrActiveLifecycleOperationPathKeys.values()) {
            if (Array.from(activePathKeys).some(activePathKey =>
                compareAppHostIdentity(activePathKey, appHostPath) !== 'different')) {
                return true;
            }
        }

        return false;
    }

    clearLaunching(appHostPath: string): void {
        this._reservations.clearLaunching(appHostPath);
    }

    clearMatchingLaunching(appHostPath: string, reservationId?: string): void {
        this._reservations.clearMatchingLaunching(appHostPath, reservationId);
    }

    clearLaunchingForRunningAppHost(appHostPath: string): void {
        this._reservations.clearLaunchingForRunningAppHost(appHostPath);
    }

    tryReserveExternalOperation(
        appHostPath: string,
        command: Exclude<AspireCommandType, 'run'>,
        noDebug: boolean,
        doStep?: string,
        isDirectoryScope = false,
    ): string | false {
        if (this._reservations.hasPendingLaunchOrLifecycleConflict(appHostPath, isDirectoryScope)) {
            return false;
        }

        if (isDirectoryScope
            ? this.hasPendingOrActiveOperationWithinDirectory(appHostPath)
            : this.hasPendingOrActiveOperationConflict(appHostPath)) {
            return false;
        }

        const reservationId = `operation-${++this._nextExternalOperationReservationId}`;
        this._pendingExternalOperationByReservationId.set(
            reservationId,
            { appHostPath, command, noDebug, doStep, isDirectoryScope: isDirectoryScope || undefined });
        this.scheduleExternalOperationExpiry(reservationId);
        this._onDidChangeOperationState.fire();
        return reservationId;
    }

    validateOrReacquireExternalOperationReservation(
        appHostPath: string,
        reservationId: string,
        command: Exclude<AspireCommandType, 'run'>,
        noDebug: boolean,
        doStep?: string,
        isDirectoryScope = false,
    ): string | false {
        const pending = this._pendingExternalOperationByReservationId.get(reservationId);
        const ownsCurrentReservation = pending &&
            compareAppHostIdentity(pending.appHostPath, appHostPath) === 'same' &&
            pending.isDirectoryScope === (isDirectoryScope || undefined);
        if (ownsCurrentReservation) {
            if (this._reservations.hasPendingLaunchOrLifecycleConflict(appHostPath, isDirectoryScope)) {
                this.clearExternalOperationReservation(reservationId);
                return false;
            }

            this._pendingExternalOperationByReservationId.set(
                reservationId,
                { appHostPath, command, noDebug, doStep, isDirectoryScope: isDirectoryScope || undefined });
            this.scheduleExternalOperationExpiry(reservationId);
            return reservationId;
        }

        return this.tryReserveExternalOperation(appHostPath, command, noDebug, doStep, isDirectoryScope);
    }

    replaceExternalOperationReservation(
        previousAppHostPath: string,
        previousReservationId: string,
        appHostPath: string,
        command: Exclude<AspireCommandType, 'run'>,
        noDebug: boolean,
        doStep?: string,
        isDirectoryScope = false,
    ): string | false {
        this.releaseExternalOperationReservation(previousAppHostPath, previousReservationId);
        return this.tryReserveExternalOperation(appHostPath, command, noDebug, doStep, isDirectoryScope);
    }

    releaseExternalOperationReservation(appHostPath: string, reservationId: string): void {
        const pending = this._pendingExternalOperationByReservationId.get(reservationId);
        if (!pending || compareAppHostIdentity(pending.appHostPath, appHostPath) !== 'same') {
            return;
        }

        this.clearExternalOperationReservation(reservationId);
    }

    /**
     * Launches an Aspire debug session for the given AppHost path.
     * Automatically marks the path as "launching" until it either appears
     * in the running list or the debug session terminates.
     * @param appHostPath Absolute path to the AppHost project.
     * @param command The Aspire CLI command to execute (run, deploy, publish, do).
     * @param noDebug When true, launches without the debugger attached.
     * @param doStep Optional step name for the 'do' command.
     */
    async launch(appHostPath: string, command: AspireCommandType, noDebug: boolean, doStep?: string, target?: CliPathResolutionTarget, cliPath?: string): Promise<void> {
        // A durable non-Run operation (deploy/publish/do) must be the only one in flight for
        // its AppHost. Rejecting here - before any pending state or the lifecycle lock -
        // stops a second deploy/publish/do from starting while one is pending or active,
        // while still allowing a Run to start alongside an active non-Run operation.
        if (command !== 'run' && this.hasPendingOrActiveOperationConflict(appHostPath)) {
            throw new vscode.CancellationError();
        }

        const launchToken = this.trackPendingRun(appHostPath, command);
        this.beginPendingOperation(launchToken, appHostPath, command, noDebug, doStep);
        try {
            return await this.runWithAppHostLifecycleLock(appHostPath, this._lifecycleCancellationSource.token, async lockToken => {
                if (this._disposed) {
                    throw new vscode.CancellationError();
                }

                if (!this.tryReserveLaunch(appHostPath, command === 'run')) {
                    throw new vscode.CancellationError();
                }

                await this.launchCore(appHostPath, command, noDebug, doStep, 'user-selection', launchToken, lockToken, undefined, target, cliPath);
            });
        }
        catch (error) {
            this._pendingRunPathByToken.delete(launchToken);
            this.clearPendingOperation(launchToken);
            throw error;
        }
    }

    async launchFromLifecycleOwner(appHostPath: string, command: 'run', noDebug: boolean, isolated: boolean | undefined, token: vscode.CancellationToken): Promise<AppHostLaunchIsolation | undefined> {
        if (this._disposed) {
            throw new vscode.CancellationError();
        }

        // The CLI treats this origin as invocation-scoped: an agent-selected target may
        // establish a missing default, but must not replace an existing workspace choice.
        const launchToken = this.trackPendingRun(appHostPath, command);
        try {
            return await this.launchCore(appHostPath, command, noDebug, undefined, 'explicit-launch-configuration', launchToken, token, isolated, undefined, undefined, 'linked-worktree-default');
        }
        catch (error) {
            this._pendingRunPathByToken.delete(launchToken);
            throw error;
        }
    }

    /**
     * Computes the root Aspire CLI args for a launch without reserving or starting anything.
     *
     * The launch.json/F5 resolver reuses this so it can negotiate isolation with the exact
     * CLI it already selected, rather than recursing back through `startDebugging`.
     */
    async prepareLaunchArguments(
        appHostPath: string,
        command: AspireCommandType,
        args: string[] | undefined,
        token: vscode.CancellationToken,
        cliPath?: string,
        target: CliPathResolutionTarget = getCliPathTargetForUri(vscode.Uri.file(appHostPath)),
        isolated: boolean | undefined = getRootIsolatedCliArg(args),
        isolationPolicy: AppHostLaunchIsolationPolicy = 'explicit-only',
    ): Promise<PreparedAppHostLaunchArguments> {
        if (command !== 'run') {
            return {
                args,
                isolation: { effective: false, option: undefined },
            };
        }

        const launchIsolation = await this.resolveLaunchIsolation(appHostPath, isolated, token, cliPath, isolationPolicy, target);
        return {
            args: ensureIsolatedCliArg(args, launchIsolation.option),
            isolation: launchIsolation,
        };
    }

    /**
     * Resolves requested or inferred isolation against the selected CLI's advertised
     * capabilities. Known older CLIs may omit inferred isolation or explicit false, but an
     * explicit choice is never changed when capability support could not be determined.
     */
    async resolveLaunchIsolation(
        appHostPath: string,
        isolated: boolean | undefined,
        token: vscode.CancellationToken,
        cliPath?: string,
        isolationPolicy: AppHostLaunchIsolationPolicy = 'explicit-only',
        target: CliPathResolutionTarget = getCliPathTargetForUri(vscode.Uri.file(appHostPath)),
    ): Promise<AppHostLaunchIsolation> {
        throwIfCancelled(token);
        const inferredIsolation = isolationPolicy === 'linked-worktree-default' && isLinkedGitWorktree(appHostPath);
        const effective = isolated ?? inferredIsolation;
        const needsCapability = effective || isolated === false;
        if (!needsCapability) {
            return { effective: false, option: undefined };
        }

        const supportStatus = await this._capabilityProvider.getCapabilityStatus(isolatedLaunchCapability, {
            suppressErrors: true,
            forceRefresh: cliPath !== undefined,
            cliPath,
            cancellationToken: token,
            minimumVersion: isolatedLaunchMinimumVersion,
            target,
        });
        throwIfCancelled(token);
        if (supportStatus === 'supported') {
            return { effective, option: isolated ?? true };
        }

        if (cliPath === undefined && isolated !== undefined) {
            // Preflight capability data may describe an earlier PATH or setting snapshot.
            // Preserve explicit user input for confirmation and let the exact-CLI refresh
            // immediately before launch decide whether that executable can honor it.
            return { effective: isolated, option: isolated };
        }

        const mustFailSafely = isolated === true ||
            (supportStatus === 'unavailable' && (effective || (isolated === false && inferredIsolation)));
        if (mustFailSafely) {
            const reason = supportStatus === 'unsupported'
                ? appHostLifecycleIsolationModeNotSupported
                : appHostLifecycleIsolationCapabilityCouldNotBeVerified;
            throw new Error(reason);
        }

        // An unconfirmed inferred preference may fall back for compatibility with CLIs that
        // predate isolation. A known older CLI can also honor explicit false by omission.
        return { effective: false, option: undefined };
    }

    private async launchCore(
        appHostPath: string,
        command: AspireCommandType,
        noDebug: boolean,
        doStep: string | undefined,
        selectionOrigin: AppHostSelectionOrigin,
        launchToken: number,
        token: vscode.CancellationToken,
        isolated: boolean | undefined,
        target?: CliPathResolutionTarget,
        cliPath?: string,
        isolationPolicy: AppHostLaunchIsolationPolicy = 'explicit-only',
    ): Promise<AppHostLaunchIsolation | undefined> {
        // Reserve before the first await. The awaits below (telemetry, the CLI gate) run
        // before `startDebugging`, so reserving later would leave a window in which a
        // concurrent F5 or tool-driven start sees no launch in flight for this AppHost.
        // The tree also shows "Starting..." from here, and every pre-start failure path
        // clears it because VS Code emits no terminate event for a launch that never
        // started. See https://code.visualstudio.com/api/references/vscode-api#debug.startDebugging
        const reservationId = this.reserveLaunch(appHostPath, command === 'run');
        // Everything between the reservation and the main try/catch below has to release
        // the reservation itself, otherwise a cancelled or failed launch would leave this
        // AppHost permanently reported as launching.
        const abortIfCancelled = (): void => {
            if (!token.isCancellationRequested) {
                return;
            }

            this.clearLaunching(appHostPath);
            throw new vscode.CancellationError();
        };
        const releaseReservationOnFailure = async <T>(work: () => Promise<T>): Promise<T> => {
            abortIfCancelled();
            try {
                return await work();
            }
            catch (error) {
                this.clearLaunching(appHostPath);
                throw error;
            }
        };
        const startTime = Date.now();
        const executionSuppressed = isE2eDebugLaunchSuppressed();
        if (executionSuppressed) {
            this._pendingRunPathByToken.delete(launchToken);
            // A suppressed launch never starts a session, so there is nothing to transfer
            // the pending operation to; clear it now rather than leaking it.
            this.clearPendingOperation(launchToken);
        }

        let telemetryProperties: Awaited<ReturnType<typeof getLaunchTelemetryProperties>>;
        try {
            telemetryProperties = await releaseReservationOnFailure(
                () => getLaunchTelemetryProperties(appHostPath, command, noDebug, executionSuppressed));
            abortIfCancelled();
        }
        catch (err) {
            this._pendingRunPathByToken.delete(launchToken);
            throw err;
        }

        const config: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            name: `Aspire ${command}: ${vscode.workspace.asRelativePath(appHostPath)}`,
            request: 'launch',
            program: appHostPath,
            command,
            noDebug,
            [appHostSelectionOriginConfigKey]: selectionOrigin,
            [appHostLaunchTokenConfigKey]: launchToken,
        };
        config[appHostLaunchReservationIdConfigKey] = reservationId;
        markAspireDebugConfigurationAsExtensionOwned(config);

        if (doStep) {
            config.step = doStep;
        }

        abortIfCancelled();
        this._onDidRequestLaunch.fire({
            appHostPath,
            command,
            noDebug,
            doStep,
            cliPath,
            cliTargetKey: target ? getCliPathTargetKey(target) : undefined,
            executionSuppressed,
        });
        abortIfCancelled();
        if (executionSuppressed) {
            await releaseReservationOnFailure(
                () => this.prepareLaunchArguments(appHostPath, command, config.args, token, undefined, target, isolated, isolationPolicy));
            this.clearMatchingLaunching(appHostPath, reservationId);
            sendTelemetryEvent('aspire/vscode/apphost/launch/result', {
                ...telemetryProperties,
                outcome: 'suppressed',
            }, {
                duration_ms: Date.now() - startTime,
            });
            // E2E suppression exercises launch routing without starting an AppHost, so no
            // effective isolation mode was established for the lifecycle result to report.
            return undefined;
        }

        try {
            let resolvedCliPath = cliPath;
            if (!resolvedCliPath) {
                const cliAvailability = await checkCliAvailableOrRedirect('debug_gate', target ?? getCliPathTargetForUri(vscode.Uri.file(appHostPath)));
                if (!cliAvailability.available) {
                    throw new vscode.CancellationError();
                }
                resolvedCliPath = cliAvailability.cliPath;
            }
            throwIfCancelled(token);
            config.skipCliAvailabilityCheck = true;
            const launchPreparation = await this.prepareLaunchArguments(
                appHostPath,
                command,
                config.args,
                token,
                resolvedCliPath,
                target,
                isolated,
                isolationPolicy);
            if (launchPreparation.args === undefined) {
                delete config.args;
            }
            else {
                config.args = launchPreparation.args;
            }
            config.resolvedCliPath = resolvedCliPath;
            const started = await vscode.debug.startDebugging(undefined, config);
            if (!started) {
                // A false result means VS Code declined the launch before the
                // debug session started (for example, no provider matched or
                // an adapter gate rejected it). Surface it as an error so the
                // tree command path does not silently swallow a real launch
                // failure while still clearing the temporary "Starting..." state.
                const error = new Error(startDebuggingDeclined(command, vscode.workspace.asRelativePath(appHostPath)));
                error.name = 'StartDebuggingDeclined';
                throw error;
            }
            sendTelemetryEvent('aspire/vscode/apphost/launch/result', {
                ...telemetryProperties,
                outcome: 'success',
            }, {
                duration_ms: Date.now() - startTime,
            });
            return launchPreparation.isolation;
        } catch (err) {
            this._pendingRunPathByToken.delete(launchToken);
            this.clearMatchingLaunching(appHostPath, reservationId);
            const canceled = isCommandCancellation(err);
            const properties: EventProperties<'aspire/vscode/apphost/launch/result'> = {
                ...telemetryProperties,
                outcome: canceled ? 'canceled' : 'error',
            };
            if (!canceled) {
                properties.error_kind = classifyError(err);
            }
            sendTelemetryEvent('aspire/vscode/apphost/launch/result', properties, {
                duration_ms: Date.now() - startTime,
            });
            throw err;
        }
    }

    private hasPendingOrActiveRunDebugSession(appHostPath: string): boolean {
        return [...this._pendingRunPathByToken.values(), ...this._activeRunDebugSessionPaths.values()]
            .some(runPath => compareAppHostIdentity(runPath, appHostPath) !== 'different');
    }

    private trackPendingRun(appHostPath: string, command: AspireCommandType): number {
        const launchToken = ++this._nextLaunchToken;
        if (command === 'run' && !isE2eDebugLaunchSuppressed()) {
            this._pendingRunPathByToken.set(launchToken, appHostPath);
        }

        return launchToken;
    }

    /**
     * The durable non-Run operation (deploy/publish/do) currently pending or active for an
     * AppHost, or `undefined` when none can be identified unambiguously. Matches only a
     * proven AppHost identity - not just the raw path - so a unique project file and its
     * sibling source file resolve to the same operation without assigning ownership when
     * multiple AppHosts could match.
     */
    getActiveOperation(appHostPath: string): AppHostOperationState | undefined {
        const matchingOperations = this.getPendingAndActiveOperations()
            .filter(operation => operation.isDirectoryScope
                ? isAppHostPathWithinDirectory(appHostPath, operation.appHostPath)
                : compareAppHostIdentity(operation.appHostPath, appHostPath) === 'same');

        if (matchingOperations.length !== 1) {
            return undefined;
        }

        const { isDirectoryScope: _, ...operation } = matchingOperations[0];
        return operation;
    }

    private hasPendingOrActiveOperationConflict(appHostPath: string): boolean {
        // Duplicate prevention is intentionally conservative: an ambiguous source/project
        // association cannot identify an owner, but starting another operation could still
        // overlap one that is already pending or active.
        return this.getPendingAndActiveOperations()
            .some(operation => operation.isDirectoryScope
                ? isAppHostPathWithinDirectory(appHostPath, operation.appHostPath)
                : compareAppHostIdentity(operation.appHostPath, appHostPath) !== 'different');
    }

    private hasPendingOrActiveOperationWithinDirectory(directoryPath: string): boolean {
        return this.getPendingAndActiveOperations()
            .some(operation => isAppHostPathWithinDirectory(operation.appHostPath, directoryPath) ||
                (operation.isDirectoryScope && isAppHostPathWithinDirectory(directoryPath, operation.appHostPath)));
    }

    private getPendingAndActiveOperations(): TrackedAppHostOperationState[] {
        return [
            ...this._pendingOperationByToken.values(),
            ...this._pendingExternalOperationByReservationId.values(),
            ...this._activeOperationBySessionId.values(),
        ];
    }

    private beginPendingOperation(launchToken: number, appHostPath: string, command: AspireCommandType, noDebug: boolean, doStep: string | undefined): void {
        // Only deploy/publish/do are durable operations; a Run is represented by its running
        // AppHost and needs no operation entry.
        if (command === 'run') {
            return;
        }

        this._pendingOperationByToken.set(launchToken, { appHostPath, command, noDebug, doStep });
        this._onDidChangeOperationState.fire();
    }

    private transferPendingOperationToActiveSession(launchToken: number, sessionId: string): boolean {
        const pending = this._pendingOperationByToken.get(launchToken);
        if (!pending) {
            return false;
        }

        this.clearRestartOperationExpiry(launchToken);
        this._pendingOperationByToken.delete(launchToken);
        this._activeOperationBySessionId.set(sessionId, pending);
        // No state event fires: {@link getActiveOperation} still reports the same operation,
        // so nothing observable changed - only the owner moved from the launch token to the
        // now-running session.
        return true;
    }

    private clearPendingOperation(launchToken: number): void {
        this.clearRestartOperationExpiry(launchToken);
        if (this._pendingOperationByToken.delete(launchToken)) {
            this._onDidChangeOperationState.fire();
        }
    }

    private transferPendingExternalOperationToActiveSession(
        reservationId: string,
        appHostPath: string,
        sessionId: string,
    ): boolean {
        const pending = this._pendingExternalOperationByReservationId.get(reservationId);
        if (!pending || compareAppHostIdentity(pending.appHostPath, appHostPath) !== 'same') {
            return false;
        }

        this.clearExternalOperationExpiry(reservationId);
        this._pendingExternalOperationByReservationId.delete(reservationId);
        this._activeOperationBySessionId.set(sessionId, pending);
        return true;
    }

    private preserveActiveOperationForRestart(sessionId: string, launchToken: number): void {
        const active = this._activeOperationBySessionId.get(sessionId);
        if (!active) {
            return;
        }

        this._activeOperationBySessionId.delete(sessionId);
        this._pendingOperationByToken.set(launchToken, active);
        this.clearRestartOperationExpiry(launchToken);
        const expiry = setTimeout(
            () => this.clearPendingOperation(launchToken),
            externalLaunchReservationTimeoutMs);
        expiry.unref?.();
        this._restartOperationExpiryByToken.set(launchToken, expiry);
    }

    private scheduleExternalOperationExpiry(reservationId: string): void {
        this.clearExternalOperationExpiry(reservationId);
        const expiry = setTimeout(
            () => this.clearExternalOperationReservation(reservationId),
            externalLaunchReservationTimeoutMs);
        expiry.unref?.();
        this._pendingExternalOperationExpiryByReservationId.set(reservationId, expiry);
    }

    private clearExternalOperationReservation(reservationId: string): void {
        this.clearExternalOperationExpiry(reservationId);
        if (this._pendingExternalOperationByReservationId.delete(reservationId)) {
            this._onDidChangeOperationState.fire();
        }
    }

    private clearExternalOperationExpiry(reservationId: string): void {
        const expiry = this._pendingExternalOperationExpiryByReservationId.get(reservationId);
        if (expiry) {
            clearTimeout(expiry);
            this._pendingExternalOperationExpiryByReservationId.delete(reservationId);
        }
    }

    private clearRestartOperationExpiry(launchToken: number): void {
        const expiry = this._restartOperationExpiryByToken.get(launchToken);
        if (expiry) {
            clearTimeout(expiry);
            this._restartOperationExpiryByToken.delete(launchToken);
        }
    }

    private clearActiveOperation(sessionId: string): void {
        if (this._activeOperationBySessionId.delete(sessionId)) {
            this._onDidChangeOperationState.fire();
        }
    }
}

function throwIfCancelled(token: vscode.CancellationToken): void {
    if (token.isCancellationRequested) {
        throw new vscode.CancellationError();
    }
}

function waitForPromise(promise: Promise<unknown>, token: vscode.CancellationToken, timeoutMs: number): Promise<void> {
    if (token.isCancellationRequested) {
        return Promise.reject(new vscode.CancellationError());
    }

    return new Promise<void>((resolve, reject) => {
        let cancellation: vscode.Disposable | undefined;
        let timeout: ReturnType<typeof setTimeout> | undefined;
        let settled = false;
        const finish = (action: () => void) => {
            if (settled) {
                return;
            }

            settled = true;
            if (timeout) {
                clearTimeout(timeout);
            }
            cancellation?.dispose();
            action();
        };
        timeout = setTimeout(() => {
            finish(() => reject(new AppHostLifecycleLockTimeoutError()));
        }, timeoutMs);
        (timeout as { unref?: () => void }).unref?.();
        cancellation = token.onCancellationRequested(() => {
            finish(() => reject(new vscode.CancellationError()));
        });
        promise.then(
            () => {
                finish(resolve);
            },
            () => {
                finish(resolve);
            });
    });
}
