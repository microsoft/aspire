import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration, type AspireResourceDebugSession } from '../dcp/types';
import { startDebuggingDeclined } from '../loc/strings';
import { compareAppHostIdentity, getAppHostIdentityKeyInfo, isAppHostPathWithinDirectory, type AppHostIdentityKeyInfo, type AppHostIdentityRelation } from '../utils/appHostIdentity';
import { classifyError, isCommandCancellation, sendTelemetryEvent, type EventProperties } from '../utils/telemetry';
import { extensionLogOutputChannel } from '../utils/logging';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { CliPathResolutionTarget, getCliPathTargetForUri } from '../utils/cliPathVariables';
import { appHostLaunchReservationIdConfigKey, appHostLaunchTokenConfigKey, appHostRestartSourceSessionIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey, type AppHostSelectionOrigin } from '../debugger/AspireDebugConfigurationMetadata';
import { markAspireDebugConfigurationAsExtensionOwned } from '../debugger/AspireDebugConfigurationProviderInternal';
import { AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, type AppHostDebugSessionTerminatedEvent, type AppHostEditorSessions, type AppHostLaunchRequestedEvent, type AppHostLaunchSession, type AppHostStopResult, type RunningAppHost } from './appHostLaunchContracts';
import { AppHostLaunchReservations } from './appHostLaunchReservations';
import { getLaunchTelemetryProperties, isE2eDebugLaunchSuppressed } from './appHostLaunchTelemetry';

export { AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, externalLaunchReservationTimeoutMs } from './appHostLaunchContracts';
export type { AppHostDebugSessionTerminatedEvent, AppHostEditorSessions, AppHostLaunchRequestedEvent, AppHostLaunchSession, AppHostStopResult, RunningAppHost } from './appHostLaunchContracts';

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
    private readonly _lifecycleCancellationSource = new vscode.CancellationTokenSource();
    private _getEditorSessions: () => readonly AppHostLaunchSession[] = () => [];
    private _getRunningAppHosts: (token: vscode.CancellationToken) => Promise<readonly RunningAppHost[]> = async () => [];
    private _stopExternalAppHost: ((appHostPath: string, token: vscode.CancellationToken) => Promise<void>) | undefined;
    private _disposed = false;
    private readonly _activeRunDebugSessionPaths = new Map<string, string>();
    private readonly _pendingRunPathByToken = new Map<number, string>();
    private _nextLaunchToken = 0;

    readonly onDidChangeLaunchingState = this._reservations.onDidChangeLaunchingState;

    private readonly _onDidTerminateAppHostDebugSession = new vscode.EventEmitter<AppHostDebugSessionTerminatedEvent>();
    readonly onDidTerminateAppHostDebugSession = this._onDidTerminateAppHostDebugSession.event;

    private readonly _onDidRequestLaunch = new vscode.EventEmitter<AppHostLaunchRequestedEvent>();
    readonly onDidRequestLaunch = this._onDidRequestLaunch.event;

    private readonly _debugSessionSubscription: vscode.Disposable;

    constructor() {
        const startSubscription = vscode.debug.onDidStartDebugSession(session => {
            const launchToken = session.configuration?.[appHostLaunchTokenConfigKey];
            if (typeof launchToken === 'number') {
                this._pendingRunPathByToken.delete(launchToken);
            }

            const appHostPath = getDebugConfigurationAppHostPath(session.configuration);
            const reservationId = session.configuration?.[appHostLaunchReservationIdConfigKey];
            if (appHostPath && typeof reservationId === 'string') {
                this._reservations.preserveStartedExternalLaunchReservation(appHostPath, reservationId);
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
            this._activeRunDebugSessionPaths.delete(session.id);
            const launchToken = session.configuration?.[appHostLaunchTokenConfigKey];
            if (typeof launchToken === 'number') {
                this._pendingRunPathByToken.delete(launchToken);
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
                const restartSourceSessionId = session.configuration[appHostRestartSourceSessionIdConfigKey];
                const isToolbarRestart = typeof restartSourceSessionId === 'string' &&
                    restartSourceSessionId === session.id;
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
        this._onDidTerminateAppHostDebugSession.dispose();
        this._onDidRequestLaunch.dispose();
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

        void tail.then(() => {
            for (const lockKey of keys) {
                if (this._lifecycleLocks.get(lockKey) === tail) {
                    this._lifecycleLocks.delete(lockKey);
                    this._lifecycleLockPathKeys.delete(lockKey);
                }
            }
        });

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
            if (acquired) {
                release();
            }
            else {
                // Preserve queue ordering even though this caller no longer waits.
                void previous.then(release, release);
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

    tryReserveLaunch(appHostPath: string): boolean {
        return this._reservations.tryReserveLaunch(appHostPath);
    }

    hasLifecycleLaunchClaim(appHostPath: string): boolean {
        return this._reservations.hasLifecycleLaunchClaim(appHostPath);
    }

    reserveLaunch(appHostPath: string): string {
        return this._reservations.reserveLaunch(appHostPath);
    }

    tryReserveExternalLaunch(appHostPath: string, isDirectoryScope = false): string | false {
        return this._reservations.tryReserveExternalLaunch(appHostPath, isDirectoryScope);
    }

    replaceExternalLaunchReservation(previousAppHostPath: string, previousReservationId: string, appHostPath: string, isDirectoryScope = false): string | false {
        return this._reservations.replaceExternalLaunchReservation(previousAppHostPath, previousReservationId, appHostPath, isDirectoryScope);
    }

    private hasActiveLifecycleOperationWithinDirectory(directoryPath: string): boolean {
        return Array.from(this._lifecycleLockPathKeys.values())
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
        for (const activePathKeys of this._lifecycleLockPathKeys.values()) {
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
        const launchToken = this.trackPendingRun(appHostPath, command);
        try {
            return await this.runWithAppHostLifecycleLock(appHostPath, this._lifecycleCancellationSource.token, async lockToken => {
                if (this._disposed) {
                    throw new vscode.CancellationError();
                }

                if (!this.tryReserveLaunch(appHostPath)) {
                    throw new vscode.CancellationError();
                }

                await this.launchCore(appHostPath, command, noDebug, doStep, 'user-selection', launchToken, lockToken, target, cliPath);
            });
        }
        catch (error) {
            this._pendingRunPathByToken.delete(launchToken);
            throw error;
        }
    }

    async launchFromLifecycleOwner(appHostPath: string, command: 'run', noDebug: boolean, token: vscode.CancellationToken): Promise<void> {
        if (this._disposed) {
            throw new vscode.CancellationError();
        }

        // The CLI treats this origin as invocation-scoped: an agent-selected target may
        // establish a missing default, but must not replace an existing workspace choice.
        const launchToken = this.trackPendingRun(appHostPath, command);
        try {
            await this.launchCore(appHostPath, command, noDebug, undefined, 'explicit-launch-configuration', launchToken, token);
        }
        catch (error) {
            this._pendingRunPathByToken.delete(launchToken);
            throw error;
        }
    }

    private async launchCore(
        appHostPath: string,
        command: AspireCommandType,
        noDebug: boolean,
        doStep: string | undefined,
        selectionOrigin: AppHostSelectionOrigin,
        launchToken: number,
        token: vscode.CancellationToken,
        target?: CliPathResolutionTarget,
        cliPath?: string,
    ): Promise<void> {
        // Reserve before the first await. The awaits below (telemetry, the CLI gate) run
        // before `startDebugging`, so reserving later would leave a window in which a
        // concurrent F5 or tool-driven start sees no launch in flight for this AppHost.
        // The tree also shows "Starting..." from here, and every pre-start failure path
        // clears it because VS Code emits no terminate event for a launch that never
        // started. See https://code.visualstudio.com/api/references/vscode-api#debug.startDebugging
        const reservationId = this.reserveLaunch(appHostPath);
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
            executionSuppressed,
        });
        abortIfCancelled();
        if (executionSuppressed) {
            this.clearMatchingLaunching(appHostPath, reservationId);
            sendTelemetryEvent('aspire/vscode/apphost/launch/result', {
                ...telemetryProperties,
                outcome: 'suppressed',
            }, {
                duration_ms: Date.now() - startTime,
            });
            return;
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
