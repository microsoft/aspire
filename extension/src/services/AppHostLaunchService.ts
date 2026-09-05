import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration, type AspireResourceDebugSession } from '../dcp/types';
import { appHostLifecycleInvalidLaunchProfile, appHostLifecycleIsolationCapabilityCouldNotBeVerified, appHostLifecycleIsolationModeNotSupported, appHostLifecycleLaunchProfileCapabilityCouldNotBeVerified, appHostLifecycleLaunchProfileNotSupported, appHostLifecycleLaunchProfileRequiresRun, startDebuggingDeclined } from '../loc/strings';
import { ensureIsolatedCliArg, getRootIsolatedCliArg, isLinkedGitWorktree } from '../utils/gitWorktree';
import { getLaunchFailureProviderKindForAppHostPath, recordLaunchFailureForAppHostIdentity, type LaunchFailureCategory, type LaunchFailureMode } from './launchFailureJournal';
import { bindCurrentAppHostTarget, compareAppHostIdentity, getAppHostIdentityKeyInfo, getOrCreateIdentityForCurrentAppHostTarget, isAppHostPathWithinDirectory, type AppHostIdentityKeyInfo, type AppHostIdentityRelation, type OpaqueAppHostIdentity } from '../utils/appHostIdentity';
import { isSameAppHostPath } from '../utils/paths/comparison';
import { classifyError, isCommandCancellation, sendTelemetryEvent, type EventProperties } from '../utils/telemetry';
import { extensionLogOutputChannel } from '../utils/logging';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { CliPathResolutionTarget, getCliPathTargetForUri, getCliPathTargetKey } from '../utils/cliPathVariables';
import { createAppHostOperationTarget, type AppHostOperationTarget } from '../utils/appHostOperationTarget';
import { appHostLaunchReservationIdConfigKey, appHostLaunchTokenConfigKey, appHostRestartSourceSessionIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey, type AppHostSelectionOrigin } from '../debugger/AspireDebugConfigurationMetadata';
import { isAspireDebugConfigurationExtensionOwned, markAspireDebugConfigurationAsExtensionOwned } from '../debugger/AspireDebugConfigurationProviderInternal';
import { AppHostLaunchTargetChangedError, AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, externalLaunchReservationTimeoutMs, type AppHostDebugSessionTerminatedEvent, type AppHostEditorSessionSnapshot, type AppHostEditorSessions, type AppHostLaunchRequestedEvent, type AppHostLaunchSession, type AppHostLaunchTarget, type AppHostOperationState, type AppHostStopResult, type RunningAppHost } from './appHostLaunchContracts';
import { AppHostLaunchReservations } from './appHostLaunchReservations';
import { getLaunchTelemetryProperties, isE2eDebugLaunchSuppressed } from './appHostLaunchTelemetry';
import { isolatedLaunchCapability, isolatedLaunchMinimumVersion, launchProfileCapability, type CapabilityStatus } from '../types/configInfo';
import { ensureLaunchProfileCliArg } from '../utils/launchProfile';

export { AppHostLaunchTargetChangedError, AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, externalLaunchReservationTimeoutMs } from './appHostLaunchContracts';
export type { AppHostDebugSessionTerminatedEvent, AppHostEditorSessionSnapshot, AppHostEditorSessions, AppHostLaunchRequestedEvent, AppHostLaunchSession, AppHostLaunchTarget, AppHostOperationState, AppHostStopResult, RunningAppHost } from './appHostLaunchContracts';

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
    readonly canonicalAppHostPath?: string;
    readonly appHostIdentity?: OpaqueAppHostIdentity;
}

/**
 * One editor-owned `run` launch that is pending or active, with the AppHost identity captured
 * when the launch was tracked.
 *
 * The identity is captured rather than re-derived on each query because a path is only a name:
 * an alias repointed after the launch started resolves to a different file, which would move the
 * launch onto an AppHost nothing was ever started against while the AppHost that is genuinely
 * starting stopped being reported. The captured identity is bound to the file that was launched
 * and cannot be changed afterwards; the physical path it was captured from is kept alongside it
 * so the debug session that starts can be matched back to the launch that requested it without
 * re-following a name that may have moved.
 */
interface TrackedRunLaunch {
    readonly appHostPath: string;
    readonly canonicalAppHostPath: string;
    readonly appHostIdentity: OpaqueAppHostIdentity;
}

type AppHostTrackedSession = AppHostLaunchSession & {
    readonly isStopAttemptInProgress?: boolean;
    readonly isShuttingDown?: boolean;
};

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
    private _stopExternalAppHost: ((target: AppHostOperationTarget, token: vscode.CancellationToken) => Promise<void>) | undefined;
    private _disposed = false;
    private readonly _activeRunLaunchBySessionId = new Map<string, TrackedRunLaunch>();
    private readonly _pendingRunLaunchByToken = new Map<number, TrackedRunLaunch>();
    private readonly _debugSessionByLaunchToken = new Map<number, vscode.DebugSession>();
    private readonly _canceledDebugStartTokens = new Set<number>();
    // Attempt correlation stays process-local. These tokens must never be projected into
    // the launch failure journal, telemetry, logs, tool results, or E2E state.
    private readonly _appHostPathAwaitingDebugStartByToken = new Map<number, string>();
    private readonly _launchTokensWithSpecificFailure = new Set<number>();
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
            // The pending entry is read before it is removed so the debug session inherits the
            // identity captured when the launch was requested, instead of resolving the path
            // again at a point an alias may already have been repointed.
            const pendingLaunch = typeof launchToken === 'number'
                ? this._pendingRunLaunchByToken.get(launchToken)
                : undefined;
            let transferredOperation = false;
            if (typeof launchToken === 'number') {
                this._pendingRunLaunchByToken.delete(launchToken);
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
                this._activeRunLaunchBySessionId.set(session.id, createTrackedRunLaunch(appHostPath, pendingLaunch));
            }
            if (typeof launchToken === 'number') {
                this._debugSessionByLaunchToken.set(launchToken, session);
                if (this._canceledDebugStartTokens.delete(launchToken)) {
                    this.stopCanceledDebugStart(session);
                }
            }
        });

        // When a debug session terminates, clear launching state for that AppHost
        // so the tree reverts from "Starting..." if the launch failed or was cancelled.
        const terminateSubscription = vscode.debug.onDidTerminateDebugSession(session => {
            this._activeRunLaunchBySessionId.delete(session.id);
            const launchToken = session.configuration?.[appHostLaunchTokenConfigKey];
            if (typeof launchToken === 'number') {
                this._debugSessionByLaunchToken.delete(launchToken);
                this._canceledDebugStartTokens.delete(launchToken);
            }
            const restartSourceSessionId = session.configuration?.[appHostRestartSourceSessionIdConfigKey];
            const isToolbarRestart = typeof restartSourceSessionId === 'string' &&
                restartSourceSessionId === session.id;
            if (isToolbarRestart && typeof launchToken === 'number') {
                this.preserveActiveOperationForRestart(session.id, launchToken);
            }
            else {
                this.clearActiveOperation(session.id);
            }
            if (typeof launchToken === 'number') {
                this._pendingRunLaunchByToken.delete(launchToken);
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
        this._activeRunLaunchBySessionId.clear();
        this._pendingRunLaunchByToken.clear();
        this._debugSessionByLaunchToken.clear();
        this._canceledDebugStartTokens.clear();
        this._appHostPathAwaitingDebugStartByToken.clear();
        this._launchTokensWithSpecificFailure.clear();
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

    setExternalAppHostStopper(stopper: (target: AppHostOperationTarget, token: vscode.CancellationToken) => Promise<void>): void {
        this._stopExternalAppHost = stopper;
    }

    markLaunchAttemptFailureRecorded(configuration: vscode.DebugConfiguration): void {
        const launchToken = configuration[appHostLaunchTokenConfigKey];
        if (!isAspireDebugConfigurationExtensionOwned(configuration) ||
            typeof launchToken !== 'number' ||
            !Number.isSafeInteger(launchToken) ||
            launchToken <= 0) {
            return;
        }

        const appHostPath = this._appHostPathAwaitingDebugStartByToken.get(launchToken);
        const configurationAppHostPath = getDebugConfigurationAppHostPath(configuration);
        if (!appHostPath ||
            !configurationAppHostPath ||
            compareAppHostIdentity(appHostPath, configurationAppHostPath) !== 'same') {
            return;
        }

        this._launchTokensWithSpecificFailure.add(launchToken);
    }

    trackAppHostDebugSession(owner: AppHostLaunchSession, appHostPath: string, debugSession: AspireResourceDebugSession): void {
        const session: AppHostTrackedSession = {
            appHostPath,
            appHostIdentity: owner.appHostIdentity ?? getOrCreateIdentityForCurrentAppHostTarget(appHostPath),
            resolvedAppHostPath: appHostPath,
            operationKind: owner.operationKind,
            get startupCompleted() { return owner.startupCompleted; },
            configuration: owner.configuration,
            get isStopAttemptInProgress() { return isTrackedSessionStopping(owner); },
            stopDebugging: async () => { await owner.stopDebugging(); },
        };
        this._appHostDebugSessions.set(debugSession.id, { owner, session });
    }

    /**
     * Returns the safe subset of editor session state that editor-assistance tools may
     * summarize.
     *
     * These summaries intentionally exclude VS Code's own session identifiers and the
     * full debug configuration. The caller only needs to answer "what AppHost is the
     * editor managing right now?" without gaining handles it could feed back into other
     * command surfaces.
     */
    getEditorSessions(): readonly AppHostEditorSessionSnapshot[] {
        return this.getTrackedEditorSessions().map(session => ({
            appHostPath: session.appHostPath,
            resolvedAppHostPath: session.resolvedAppHostPath,
            appHostIdentity: session.appHostIdentity,
            operationKind: session.operationKind,
            startupCompleted: session.startupCompleted,
            noDebug: typeof session.configuration.noDebug === 'boolean'
                ? session.configuration.noDebug
                : undefined,
            isStopping: isTrackedSessionStopping(session),
        }));
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
        const requestedIdentity = getOrCreateIdentityForCurrentAppHostTarget(appHostPath);
        for (const session of this.getTrackedEditorSessions()) {
            if (session.operationKind !== 'run') {
                continue;
            }

            if (session.appHostIdentity !== undefined) {
                if (session.appHostIdentity === requestedIdentity) {
                    sessions.push(session);
                }
                else {
                    const sessionPath = session.resolvedAppHostPath ?? session.appHostPath;
                    if (compareAppHostIdentity(sessionPath, appHostPath) === 'ambiguous') {
                        ambiguous = true;
                    }
                }
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

    private getTrackedEditorSessions(): readonly AppHostLaunchSession[] {
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
        return [...editorSessions, ...fallbackSessions];
    }

    async getRunningAppHosts(token: vscode.CancellationToken): Promise<readonly RunningAppHost[]> {
        throwIfCancelled(token);
        const appHosts = await this._getRunningAppHosts(token);
        throwIfCancelled(token);
        return appHosts;
    }

    async stopAppHost(appHostPath: string, token: vscode.CancellationToken = this._lifecycleCancellationSource.token): Promise<AppHostStopResult> {
        throwIfCancelled(token);

        // The stop is bound once, before the lock is waited on, so the AppHost that is stopped is
        // the one this call selected rather than whatever the name reaches after the wait.
        const stopTarget = bindAppHostLaunchTarget(appHostPath);
        return await this.runWithAppHostLifecycleLock(stopTarget.canonicalPath, token, lockToken =>
            this.stopAppHostFromLifecycleOwner(stopTarget, lockToken));
    }

    /**
     * Stops an AppHost on behalf of a caller that already owns the lifecycle lock for it.
     *
     * The whole bound target is required rather than a path: the editor-session and running-CLI
     * comparisons are made against the physical AppHost, while the external stop still has to run
     * under the workspace folder the caller named so it resolves that folder's CLI.
     */
    async stopAppHostFromLifecycleOwner(stopTarget: AppHostLaunchTarget, token: vscode.CancellationToken): Promise<AppHostStopResult> {
        throwIfCancelled(token);
        const appHostPath = stopTarget.canonicalPath;
        const initialEditorResult = await this.stopEditorAppHostIfControlled(stopTarget, token);
        if (initialEditorResult) {
            return initialEditorResult;
        }

        const externalRelation = await this.getRunningAppHostRelation(appHostPath, token);
        const currentEditorResult = await this.stopEditorAppHostIfControlled(stopTarget, token);
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
        this.assertAppHostLaunchTargetCurrent(stopTarget);
        try {
            await this._stopExternalAppHost(
                createAppHostOperationTarget(stopTarget.canonicalPath, stopTarget.selectorPath),
                token);
        }
        catch (error) {
            if (isCommandCancellation(error)) {
                throw new AppHostStopCancellationError('external', undefined);
            }
            throw new AppHostStopError('external', undefined, error);
        }
        return { outcome: 'stopped', controller: 'external' };
    }

    private async stopEditorAppHostIfControlled(stopTarget: AppHostLaunchTarget, token: vscode.CancellationToken): Promise<AppHostStopResult | undefined> {
        const appHostPath = stopTarget.canonicalPath;
        const editorSessions = this.getEditorRunSessions(appHostPath);
        if (editorSessions.sessions.length > 1 ||
            (editorSessions.sessions.length === 0 && editorSessions.ambiguous)) {
            return { outcome: 'ambiguousSession', controller: 'editor' };
        }

        if (editorSessions.sessions.length === 1) {
            const session = editorSessions.sessions[0];
            const noDebug = session.configuration.noDebug === true;
            throwIfCancelled(token);
            this.assertAppHostLaunchTargetCurrent(stopTarget);
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

    private assertAppHostLaunchTargetCurrent(target: AppHostLaunchTarget): void {
        if (getOrCreateIdentityForCurrentAppHostTarget(target.selectorPath) !== target.identity) {
            throw new AppHostLaunchTargetChangedError();
        }
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

    /**
     * Returns whether an editor-owned `run` launch is pending or active for this AppHost.
     *
     * The comparison is made only against the identity each launch captured, never against a
     * stored path. Matching paths as well would answer for the AppHost a name used to mean:
     * after an alias is repointed, a caller naming that alias is asking about the file it names
     * now, and nothing was started against that file. Equivalent spellings of the AppHost that
     * *was* launched - the same file named differently, or the sibling of a single
     * project/source pair - still resolve to the captured identity, so they keep their answer.
     *
     * This intentionally exposes only a boolean. Editor-assistance callers do not need
     * launch arguments, configurations, or debug session identifiers.
     */
    hasPendingOrActiveRunLaunch(appHostPath: string): boolean {
        const requestedIdentity = getOrCreateIdentityForCurrentAppHostTarget(appHostPath);
        return this.getTrackedRunLaunches().some(launch => launch.appHostIdentity === requestedIdentity) ||
            this._reservations.hasPendingExternalRunLaunch(appHostPath);
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
            this.createTrackedOperationState(appHostPath, command, noDebug, doStep, isDirectoryScope));
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
            (isDirectoryScope
                ? isSameAppHostPath(pending.appHostPath, appHostPath)
                : this.operationMatchesAppHost(pending, appHostPath)) &&
            pending.isDirectoryScope === (isDirectoryScope || undefined);
        if (ownsCurrentReservation) {
            if (this._reservations.hasPendingLaunchOrLifecycleConflict(appHostPath, isDirectoryScope)) {
                this.clearExternalOperationReservation(reservationId);
                return false;
            }

            this._pendingExternalOperationByReservationId.set(
                reservationId,
                this.createTrackedOperationState(appHostPath, command, noDebug, doStep, isDirectoryScope));
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
        if (!pending ||
            (!this.operationMatchesAppHost(pending, appHostPath) &&
                !isSameAppHostPath(pending.appHostPath, appHostPath))) {
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

        // Bind before entering the queue so the lock and the eventual launch always refer to the
        // same physical AppHost. If the selector moves while queued, the freshness check below
        // rejects the launch instead of running a different AppHost under the original lock.
        const launchTarget = bindAppHostLaunchTarget(appHostPath);
        const launchToken = this.trackPendingRun(appHostPath, command);
        this.beginPendingOperation(launchToken, appHostPath, command, noDebug, doStep);
        try {
            return await this.runWithAppHostLifecycleLock(launchTarget.canonicalPath, this._lifecycleCancellationSource.token, async lockToken => {
                if (this._disposed) {
                    throw new vscode.CancellationError();
                }

                this.assertAppHostLaunchTargetCurrent(launchTarget);
                if (!this.tryReserveLaunch(launchTarget.canonicalPath, command === 'run')) {
                    throw new vscode.CancellationError();
                }

                await this.launchCore(launchTarget, command, noDebug, doStep, 'user-selection', launchToken, lockToken, undefined, target, cliPath);
            });
        }
        catch (error) {
            this._pendingRunLaunchByToken.delete(launchToken);
            this.clearPendingOperation(launchToken);
            throw error;
        }
        finally {
            this.clearLaunchAttemptFailureCorrelation(launchToken);
        }
    }

    /**
     * Launches on behalf of a caller that resolved the AppHost and owns the lifecycle lock for it.
     *
     * The caller's own binding is used as-is. Re-resolving here would discard the selector the
     * caller confirmed - the only value a retarget can be detected against - and would re-follow
     * a name the caller already resolved, so a launch could commit against an AppHost that was
     * never confirmed while every later check compared the physical path with itself.
     */
    async launchFromLifecycleOwner(
        launchTarget: AppHostLaunchTarget,
        command: 'run',
        noDebug: boolean,
        isolated: boolean | undefined,
        token: vscode.CancellationToken,
        launchProfile?: string,
        inferredIsolationOverride?: boolean): Promise<AppHostLaunchIsolation | undefined> {
        if (this._disposed) {
            throw new vscode.CancellationError();
        }

        // The CLI treats this origin as invocation-scoped: an agent-selected target may
        // establish a missing default, but must not replace an existing workspace choice.
        const launchToken = this.trackPendingRun(launchTarget.selectorPath, command);
        try {
            return await this.launchCore(
                launchTarget,
                command,
                noDebug,
                undefined,
                'explicit-launch-configuration',
                launchToken,
                token,
                isolated,
                undefined,
                undefined,
                'linked-worktree-default',
                launchProfile,
                inferredIsolationOverride);
        }
        catch (error) {
            this._pendingRunLaunchByToken.delete(launchToken);
            throw error;
        }
        finally {
            this.clearLaunchAttemptFailureCorrelation(launchToken);
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
        launchProfile?: string,
        inferredIsolationOverride?: boolean,
    ): Promise<PreparedAppHostLaunchArguments> {
        if (command !== 'run') {
            if (launchProfile !== undefined) {
                throw new Error(appHostLifecycleLaunchProfileRequiresRun);
            }

            return {
                args,
                isolation: { effective: false, option: undefined },
            };
        }

        const launchIsolation = await this.resolveLaunchIsolation(
            appHostPath,
            isolated,
            token,
            cliPath,
            isolationPolicy,
            target,
            inferredIsolationOverride);
        const selectedLaunchProfile = await this.resolveLaunchProfile(launchProfile, token, cliPath, target);
        const isolatedArgs = ensureIsolatedCliArg(args, launchIsolation.option);
        return {
            args: selectedLaunchProfile === undefined
                ? isolatedArgs
                : ensureLaunchProfileCliArg(isolatedArgs, selectedLaunchProfile),
            isolation: launchIsolation,
        };
    }

    private async resolveLaunchProfile(
        launchProfile: string | undefined,
        token: vscode.CancellationToken,
        cliPath: string | undefined,
        target: CliPathResolutionTarget,
    ): Promise<string | undefined> {
        if (launchProfile === undefined) {
            return undefined;
        }
        if (launchProfile.trim().length === 0) {
            throw new Error(appHostLifecycleInvalidLaunchProfile);
        }

        throwIfCancelled(token);
        const supportStatus = await this._capabilityProvider.getCapabilityStatus(launchProfileCapability, {
            suppressErrors: true,
            forceRefresh: cliPath !== undefined,
            cliPath,
            cancellationToken: token,
            target,
        });
        throwIfCancelled(token);
        if (supportStatus === 'supported') {
            return launchProfile;
        }

        if (cliPath === undefined) {
            // Preflight data can describe a stale PATH or setting snapshot. The launch path
            // repeats this check against the resolved executable before starting VS Code.
            return launchProfile;
        }

        throw new Error(supportStatus === 'unsupported'
            ? appHostLifecycleLaunchProfileNotSupported
            : appHostLifecycleLaunchProfileCapabilityCouldNotBeVerified);
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
        inferredIsolationOverride?: boolean,
    ): Promise<AppHostLaunchIsolation> {
        throwIfCancelled(token);
        const inferredIsolation = inferredIsolationOverride ??
            (isolationPolicy === 'linked-worktree-default' && isLinkedGitWorktree(appHostPath));
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
        launchTarget: AppHostLaunchTarget,
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
        launchProfile?: string,
        inferredIsolationOverride?: boolean,
    ): Promise<AppHostLaunchIsolation | undefined> {
        // Reserve before the first await. The awaits below (telemetry, the CLI gate) run
        // before `startDebugging`, so reserving later would leave a window in which a
        // concurrent F5 or tool-driven start sees no launch in flight for this AppHost.
        // The tree also shows "Starting..." from here, and every pre-start failure path
        // clears it because VS Code emits no terminate event for a launch that never
        // started. See https://code.visualstudio.com/api/references/vscode-api#debug.startDebugging
        //
        // The caller resolved this AppHost once, before entering, into the selector it named and
        // the physical file that selector pointed at. Everything this launch does - the
        // reservation, the CLI gate, the isolation negotiation, and the configuration handed to
        // `startDebugging` - is addressed to that file rather than to the selector, which may be
        // an alias another process can repoint while those steps are in flight. The selector is
        // still what the display renders and what is checked again before the launch commits.
        const appHostPath = launchTarget.selectorPath;
        const canonicalAppHostPath = launchTarget.canonicalPath;
        const appHostIdentity = launchTarget.identity;
        // Which CLI runs, and whose settings apply, is a scope question about the path the caller
        // named: it decides which workspace folder's `aspire.cliPath` and capability data are
        // used, not which AppHost is started. It is computed once here and passed explicitly to
        // every step below, because the bound physical path can resolve outside every open folder
        // - a symlinked root or a linked worktree - and letting a step derive scope from it would
        // silently fall back to window settings, or to another root's settings in a multi-root
        // workspace, and run a different `aspire` than the folder configured.
        const scopeTarget = target ?? getCliPathTargetForUri(vscode.Uri.file(appHostPath));
        const reservationId = this.reserveLaunch(canonicalAppHostPath, command === 'run');
        // The pending entry was tracked before the lifecycle lock was taken, so it is rebound to
        // the AppHost this attempt actually launches. Reporting one AppHost as starting while
        // another one is started is the same misattribution this binding exists to prevent.
        this.rebindPendingRun(launchToken, launchTarget);
        this.rebindPendingOperation(launchToken, launchTarget);
        // Everything between the reservation and the main try/catch below has to release
        // the reservation itself, otherwise a cancelled or failed launch would leave this
        // AppHost permanently reported as launching.
        const abortIfCancelled = (): void => {
            if (!token.isCancellationRequested) {
                return;
            }

            this.clearLaunching(canonicalAppHostPath);
            throw new vscode.CancellationError();
        };
        const releaseReservationOnFailure = async <T>(work: () => Promise<T>): Promise<T> => {
            abortIfCancelled();
            try {
                return await work();
            }
            catch (error) {
                this.clearLaunching(canonicalAppHostPath);
                throw error;
            }
        };
        const startTime = Date.now();
        const executionSuppressed = isE2eDebugLaunchSuppressed();
        if (executionSuppressed) {
            this._pendingRunLaunchByToken.delete(launchToken);
            // A suppressed launch never starts a session, so there is nothing to transfer
            // the pending operation to; clear it now rather than leaking it.
            this.clearPendingOperation(launchToken);
        }

        let telemetryProperties: Awaited<ReturnType<typeof getLaunchTelemetryProperties>>;
        try {
            telemetryProperties = await releaseReservationOnFailure(
                () => getLaunchTelemetryProperties(canonicalAppHostPath, command, noDebug, executionSuppressed));
            abortIfCancelled();
        }
        catch (err) {
            this._pendingRunLaunchByToken.delete(launchToken);
            this.clearPendingOperation(launchToken);
            throw err;
        }

        const config: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            // The name is presentation, so it keeps the selector: it is workspace-relative for
            // the path the caller named, while the bound path can resolve outside the workspace.
            name: `Aspire ${command}: ${launchTarget.displayPath}`,
            request: 'launch',
            program: canonicalAppHostPath,
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
        if (launchProfile !== undefined) {
            config.launchProfile = launchProfile;
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
                () => this.prepareLaunchArguments(
                    canonicalAppHostPath,
                    command,
                    config.args,
                    token,
                    undefined,
                    scopeTarget,
                    isolated,
                    isolationPolicy,
                    launchProfile,
                    inferredIsolationOverride));
            this.clearMatchingLaunching(canonicalAppHostPath, reservationId);
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

        let failureCategory: LaunchFailureCategory | undefined;
        try {
            let resolvedCliPath = cliPath;
            if (!resolvedCliPath) {
                const cliAvailability = await checkCliAvailableOrRedirect('debug_gate', scopeTarget);
                if (!cliAvailability.available) {
                    failureCategory = 'cliUnavailable';
                    throw new vscode.CancellationError();
                }
                resolvedCliPath = cliAvailability.cliPath;
            }
            throwIfCancelled(token);
            config.skipCliAvailabilityCheck = true;
            const launchPreparation = await this.prepareLaunchArguments(
                canonicalAppHostPath,
                command,
                config.args,
                token,
                resolvedCliPath,
                scopeTarget,
                isolated,
                isolationPolicy,
                launchProfile,
                inferredIsolationOverride);
            if (launchPreparation.args === undefined) {
                delete config.args;
            }
            else {
                config.args = launchPreparation.args;
            }
            config.resolvedCliPath = resolvedCliPath;

            // Last check before the launch becomes irreversible: the selector must still name the
            // AppHost this attempt was bound to. Everything above was awaited, so the entry the
            // caller chose can have been replaced in the meantime, and starting anyway would run
            // an AppHost the caller never selected - or the one they did select while the tree,
            // the journal, and the tools all attribute it to something else. The configuration
            // already carries the bound physical path, so a retarget after this point can no
            // longer redirect the process VS Code starts.
            this.assertAppHostLaunchTargetCurrent(launchTarget);

            this._appHostPathAwaitingDebugStartByToken.set(launchToken, canonicalAppHostPath);
            throwIfCancelled(token);
            const start = Promise.resolve(vscode.debug.startDebugging(undefined, config));
            void start.then(
                started => {
                    if (!started) {
                        this._canceledDebugStartTokens.delete(launchToken);
                    }
                },
                () => this._canceledDebugStartTokens.delete(launchToken));
            let cancellationDisposable: vscode.Disposable | undefined;
            const cancellation = new Promise<never>((_, reject) => {
                cancellationDisposable = token.onCancellationRequested(() => {
                    this._canceledDebugStartTokens.add(launchToken);
                    const startedSession = this._debugSessionByLaunchToken.get(launchToken);
                    if (startedSession) {
                        this._canceledDebugStartTokens.delete(launchToken);
                        this.stopCanceledDebugStart(startedSession);
                    }
                    reject(new vscode.CancellationError());
                });
            });
            let started: boolean;
            try {
                started = await Promise.race([start, cancellation]);
            }
            finally {
                cancellationDisposable?.dispose();
            }
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
            this._pendingRunLaunchByToken.delete(launchToken);
            this.clearPendingOperation(launchToken);
            this.clearMatchingLaunching(canonicalAppHostPath, reservationId);
            const hasSpecificFailureForAttempt = this._launchTokensWithSpecificFailure.delete(launchToken);
            if (!hasSpecificFailureForAttempt) {
                recordLaunchFailureForAppHostIdentity(appHostIdentity, {
                    stage: 'cliLaunch',
                    category: failureCategory,
                    controller: 'editor',
                    mode: getLaunchFailureMode(command, noDebug),
                    providerKind: getLaunchFailureProviderKindForAppHostPath(canonicalAppHostPath),
                    error: err,
                });
            }
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

    private stopCanceledDebugStart(session: vscode.DebugSession): void {
        // This session carries the unique launch token of the canceled request. Stop that session
        // directly rather than using path-wide lifecycle cleanup, which could target a newer launch.
        void Promise.resolve(vscode.debug.stopDebugging(session)).catch(() => {
            extensionLogOutputChannel.warn('Failed to stop an Aspire debug session that started after its launch was canceled.');
        });
    }

    private clearLaunchAttemptFailureCorrelation(launchToken: number): void {
        this._appHostPathAwaitingDebugStartByToken.delete(launchToken);
        this._launchTokensWithSpecificFailure.delete(launchToken);
    }

    private getTrackedRunLaunches(): readonly TrackedRunLaunch[] {
        return [...this._pendingRunLaunchByToken.values(), ...this._activeRunLaunchBySessionId.values()];
    }

    /**
     * Reports whether anything might still be running this AppHost, so the tree is not told an
     * AppHost stopped while another attempt for it is in flight.
     *
     * This keeps the current-path relation - including `ambiguous` - because it decides whether to
     * *withhold* a "stopping" claim: an association that cannot be disproven has to count. The
     * captured identity is checked as well, so a launch whose alias was repointed still answers
     * for the file it was actually started against.
     */
    private hasPendingOrActiveRunDebugSession(appHostPath: string): boolean {
        const requestedIdentity = getOrCreateIdentityForCurrentAppHostTarget(appHostPath);
        return this.getTrackedRunLaunches().some(launch =>
            launch.appHostIdentity === requestedIdentity ||
            compareAppHostIdentity(launch.appHostPath, appHostPath) !== 'different');
    }

    private trackPendingRun(appHostPath: string, command: AspireCommandType): number {
        const launchToken = ++this._nextLaunchToken;
        if (command === 'run' && !isE2eDebugLaunchSuppressed()) {
            this._pendingRunLaunchByToken.set(launchToken, createTrackedRunLaunch(appHostPath));
        }

        return launchToken;
    }

    /**
     * Moves an already tracked pending `run` launch onto the AppHost the launch was bound to.
     *
     * The pending entry is created when the launch is requested, which is before the lifecycle
     * lock is waited on. An alias repointed during that wait would leave the tree and the
     * editor-assistance surfaces reporting the AppHost that was named at request time while the
     * launch that follows belongs to a different one.
     */
    private rebindPendingRun(launchToken: number, launchTarget: AppHostLaunchTarget): void {
        if (!this._pendingRunLaunchByToken.has(launchToken)) {
            return;
        }

        this._pendingRunLaunchByToken.set(launchToken, {
            appHostPath: launchTarget.selectorPath,
            canonicalAppHostPath: launchTarget.canonicalPath,
            appHostIdentity: launchTarget.identity,
        });
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
                : this.operationMatchesAppHost(operation, appHostPath));

        if (matchingOperations.length !== 1) {
            return undefined;
        }

        const operation = matchingOperations[0];
        return {
            appHostPath: operation.canonicalAppHostPath ?? operation.appHostPath,
            command: operation.command,
            noDebug: operation.noDebug,
            doStep: operation.doStep,
        };
    }

    private hasPendingOrActiveOperationConflict(appHostPath: string): boolean {
        // Duplicate prevention is intentionally conservative: an ambiguous source/project
        // association cannot identify an owner, but starting another operation could still
        // overlap one that is already pending or active.
        return this.getPendingAndActiveOperations()
            .some(operation => operation.isDirectoryScope
                ? isAppHostPathWithinDirectory(appHostPath, operation.appHostPath)
                : this.operationMatchesAppHost(operation, appHostPath) ||
                    compareAppHostIdentity(operation.canonicalAppHostPath ?? operation.appHostPath, appHostPath) !== 'different');
    }

    private hasPendingOrActiveOperationWithinDirectory(directoryPath: string): boolean {
        return this.getPendingAndActiveOperations()
            .some(operation => isAppHostPathWithinDirectory(operation.canonicalAppHostPath ?? operation.appHostPath, directoryPath) ||
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

        this._pendingOperationByToken.set(
            launchToken,
            this.createTrackedOperationState(appHostPath, command, noDebug, doStep));
        this._onDidChangeOperationState.fire();
    }

    private rebindPendingOperation(launchToken: number, launchTarget: AppHostLaunchTarget): void {
        const pending = this._pendingOperationByToken.get(launchToken);
        if (!pending) {
            return;
        }

        this._pendingOperationByToken.set(launchToken, {
            ...pending,
            canonicalAppHostPath: launchTarget.canonicalPath,
            appHostIdentity: launchTarget.identity,
        });
    }

    private createTrackedOperationState(
        appHostPath: string,
        command: AspireCommandType,
        noDebug: boolean,
        doStep: string | undefined,
        isDirectoryScope = false,
    ): TrackedAppHostOperationState {
        if (isDirectoryScope) {
            return { appHostPath, command, noDebug, doStep, isDirectoryScope: true };
        }

        const binding = bindCurrentAppHostTarget(appHostPath);
        return {
            appHostPath,
            command,
            noDebug,
            doStep,
            canonicalAppHostPath: binding.canonicalPath,
            appHostIdentity: binding.identity,
        };
    }

    private operationMatchesAppHost(operation: TrackedAppHostOperationState, appHostPath: string): boolean {
        return operation.appHostIdentity === getOrCreateIdentityForCurrentAppHostTarget(appHostPath);
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
        if (!pending ||
            (pending.isDirectoryScope
                ? !isAppHostPathWithinDirectory(appHostPath, pending.appHostPath) &&
                    !isSameAppHostPath(pending.appHostPath, appHostPath)
                : !this.operationMatchesAppHost(pending, appHostPath))) {
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

/**
 * Captures the AppHost a lifecycle operation will act on: the name the caller used, the physical
 * file that name currently selects, and the identity both were captured under.
 *
 * Callers bind once - before waiting on the lifecycle lock, or before confirming with a user -
 * and then carry the whole value. That is what lets every later step act on the AppHost that was
 * chosen while still detecting that the name stopped pointing at it.
 */
export function bindAppHostLaunchTarget(appHostPath: string): AppHostLaunchTarget {
    const binding = bindCurrentAppHostTarget(appHostPath);
    return {
        selectorPath: appHostPath,
        canonicalPath: binding.canonicalPath,
        identity: binding.identity,
        displayPath: vscode.workspace.asRelativePath(appHostPath),
    };
}

/**
 * Binds a `run` launch to the AppHost identity in effect when it was tracked.
 *
 * A launch that already captured an identity keeps it: the debug session that starts belongs to
 * the launch that requested it, so resolving the path a second time could only replace a correct
 * capture with whatever the path names by then. The configuration's path is matched against the
 * physical path the pending launch was bound to, because that comparison is lexical and is
 * therefore the one comparison a retarget cannot influence.
 */
function createTrackedRunLaunch(appHostPath: string, pendingLaunch?: TrackedRunLaunch): TrackedRunLaunch {
    if (pendingLaunch &&
        (isSameAppHostPath(pendingLaunch.canonicalAppHostPath, appHostPath) ||
            isSameAppHostPath(pendingLaunch.appHostPath, appHostPath))) {
        return pendingLaunch;
    }

    const binding = bindCurrentAppHostTarget(appHostPath);
    return {
        appHostPath,
        canonicalAppHostPath: binding.canonicalPath,
        appHostIdentity: binding.identity,
    };
}

function getLaunchFailureMode(command: AspireCommandType, noDebug: boolean): LaunchFailureMode {
    if (command === 'deploy' || command === 'publish') {
        return command;
    }
    if (command === 'run') {
        return noDebug ? 'run' : 'debug';
    }

    return 'other';
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

function isTrackedSessionStopping(session: AppHostLaunchSession): boolean {
    const tracked = session as AppHostTrackedSession;
    return tracked.isStopAttemptInProgress === true || tracked.isShuttingDown === true;
}
