import * as fs from 'fs';
import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration, AspireOperationKind, type AspireResourceDebugSession } from '../dcp/types';
import { appHostLifecycleBusy, startDebuggingDeclined } from '../loc/strings';
import { classifyAppHostDirectory, classifyAppHostPath } from '../utils/appHostLanguage';
import { compareAppHostIdentity, getAppHostIdentityKeyInfo, getAppHostPathComparisonKey, isAppHostPathWithinDirectory, type AppHostIdentityKeyInfo, type AppHostIdentityRelation } from '../utils/appHostIdentity';
import { classifyError, isCommandCancellation, sendTelemetryEvent, type EventProperties } from '../utils/telemetry';
import { bucketAspireCommand } from '../utils/telemetryBuckets';
import { extensionLogOutputChannel } from '../utils/logging';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { appHostLaunchReservationIdConfigKey, appHostLaunchTokenConfigKey, appHostRestartSourceSessionIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey, type AppHostSelectionOrigin } from '../debugger/AspireDebugConfigurationMetadata';
import { markAspireDebugConfigurationAsExtensionOwned } from '../debugger/AspireDebugConfigurationProviderInternal';

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

export interface AppHostLaunchRequestedEvent {
    appHostPath: string;
    command: AspireCommandType;
    noDebug: boolean;
    doStep?: string;
    executionSuppressed: boolean;
}

export interface AppHostDebugSessionTerminatedEvent {
    appHostPath: string;
    command?: AspireCommandType;
    shouldRequestStopRefresh: boolean;
    shouldMarkAppHostStopping: boolean;
}

export interface AppHostLaunchSession {
    readonly appHostPath: string | undefined;
    /**
     * The concrete AppHost the extension resolved for this session, when the session's
     * own `program` is a workspace folder rather than a file.
     *
     * `Aspire: Configure launch.json` writes `program: '${workspaceFolder}'`, and
     * `AspireDebugConfigurationProvider` also falls back to the folder when `program` is
     * absent, so for the standard "configure launch.json then F5" flow `appHostPath` is a
     * directory and can never match a requested AppHost file. The configuration provider
     * has already resolved the unambiguous candidate for that folder, so carry it here
     * instead of guessing which AppHost under the folder is running.
     */
    readonly resolvedAppHostPath: string | undefined;
    readonly operationKind: AspireOperationKind;
    readonly startupCompleted: boolean;
    readonly configuration: { readonly noDebug?: boolean;[key: string]: unknown };
    stopDebugging(): Promise<void>;
}

export interface RunningAppHost {
    readonly appHostPath: string;
}

export type AppHostStopResult =
    | { readonly outcome: 'stopped'; readonly controller: 'editor'; readonly noDebug: boolean }
    | { readonly outcome: 'stopped'; readonly controller: 'external' }
    | { readonly outcome: 'notRunning'; readonly controller: 'none' }
    | { readonly outcome: 'alreadyStarting'; readonly controller: 'editor' }
    | { readonly outcome: 'ambiguousSession'; readonly controller: 'editor' }
    | { readonly outcome: 'ambiguousAppHost'; readonly controller: 'external' };

/**
 * Sessions proven to belong to a requested AppHost, plus whether any session could not be
 * proven either way.
 *
 * `ambiguous` exists because a project file and a sibling `Program.cs` only describe one
 * AppHost when the directory forces that pairing. When it does not, answering "no
 * sessions" would be a guess that lets a caller start a duplicate AppHost, and answering
 * "this session" would let a caller stop the wrong one.
 */
export interface AppHostEditorSessions {
    readonly sessions: readonly AppHostLaunchSession[];
    readonly ambiguous: boolean;
}

interface TrackedAppHostDebugSession {
    readonly owner: AppHostLaunchSession;
    readonly session: AppHostLaunchSession;
}

export const appHostLifecycleLockWaitTimeoutMs = 10_000;

/**
 * How long one lifecycle operation may run before the lock cancels it.
 *
 * Generous on purpose: a real AppHost shutdown tears down containers and other
 * resources, so this is a stuck-operation backstop rather than an operation timeout.
 */
export const appHostLifecycleLockMaxHoldMs = 120_000;

/**
 * How long a `launch.json`/F5 launch stays reserved before the reservation expires.
 *
 * It only has to cover the gap between VS Code resolving the debug configuration and the
 * debug session becoming observable; after that the session itself is the evidence.
 */
export const externalLaunchReservationTimeoutMs = 60_000;

export class AppHostLifecycleLockTimeoutError extends Error {
    constructor() {
        // `AppHostLaunchService.launch` is the editor's own run/debug path, so this
        // message can reach a notification via showErrorMessage. It must therefore be
        // localized, unlike the tool path where the timeout only maps to a `busy` outcome.
        super(appHostLifecycleBusy);
        this.name = 'AppHostLifecycleLockTimeoutError';
    }
}

export class AppHostStopError extends Error {
    constructor(
        readonly controller: 'editor' | 'external',
        readonly noDebug: boolean | undefined,
        error: unknown) {
        super(error instanceof Error ? error.message : String(error));
        this.name = 'AppHostStopError';
    }
}

export class AppHostStopCancellationError extends vscode.CancellationError {
    constructor(
        readonly controller: 'editor' | 'external',
        readonly noDebug: boolean | undefined) {
        super();
    }
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
    private readonly _launchingPaths = new Set<string>();
    private readonly _appHostDebugSessions = new Map<string, TrackedAppHostDebugSession>();
    /**
     * The subset of {@link _launchingPaths} claimed by a lifecycle-owned launch, meaning a
     * caller that went through {@link tryReserveLaunch}.
     *
     * Recorded separately because a claim has to be able to refuse a later arrival. An
     * ordinary launching flag only reports that something is in flight; it cannot tell a
     * `launch.json`/F5 launch that the AppHost is already spoken for.
     */
    private readonly _lifecycleLaunchClaims = new Set<string>();
    /**
     * The pending self-expiry timer for each externally reserved key.
     *
     * Kept per key so an older timer can never delete a newer reservation. Repeated
     * external reservations are allowed, and the same key can also be re-reserved by an
     * internal launch, so an unconditional delete scheduled by the first reservation would
     * clear a launch that is still in flight and reopen the duplicate-launch window.
     */
    private readonly _externalReservationExpiries = new Map<string, NodeJS.Timeout>();
    private readonly _externalDirectoryLaunchReservations = new Set<string>();
    private readonly _launchReservationIds = new Map<string, string>();
    private readonly _latestLaunchReservationIds = new Map<string, string>();
    private _nextLaunchReservationId = 0;
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

    private readonly _onDidChangeLaunchingState = new vscode.EventEmitter<void>();
    readonly onDidChangeLaunchingState = this._onDidChangeLaunchingState.event;

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
                this.preserveStartedExternalLaunchReservation(appHostPath, reservationId);
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
                    this.isLatestLaunchReservation(appHostPath, reservationId);
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
        for (const expiry of this._externalReservationExpiries.values()) {
            clearTimeout(expiry);
        }
        this._externalReservationExpiries.clear();
        this._externalDirectoryLaunchReservations.clear();
        this._launchReservationIds.clear();
        this._latestLaunchReservationIds.clear();
        this._activeRunDebugSessionPaths.clear();
        this._pendingRunPathByToken.clear();
        this._onDidChangeLaunchingState.dispose();
        this._onDidTerminateAppHostDebugSession.dispose();
        this._onDidRequestLaunch.dispose();
    }

    /**
     * Returns whether the given AppHost path is currently in a launching state.
     */
    get launchingPaths(): readonly string[] {
        return Array.from(this._launchingPaths);
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
        const exactKey = getAppHostPathComparisonKey(appHostPath);
        if (this._launchingPaths.has(exactKey)) {
            return true;
        }

        if (Array.from(this._externalDirectoryLaunchReservations)
            .some(directoryPath => isAppHostPathWithinDirectory(appHostPath, directoryPath))) {
            return true;
        }

        // The editor can discover a C# AppHost by its project while an agent addresses
        // the same AppHost by Program.cs/AppHost.cs (or vice versa). Keep the launching
        // guard active across that identity boundary after the shared launch lock releases.
        // An association that cannot be proven also counts as launching: reporting "not
        // launching" would let a second process start against the same AppHost.
        return Array.from(this._launchingPaths).some(launchingPath =>
            compareAppHostIdentity(launchingPath, appHostPath) !== 'different');
    }

    /**
     * Claims the launching slot for an AppHost, or reports that another launch already
     * holds it.
     *
     * Synchronous on purpose. {@link runWithAppHostLifecycleLock} only serializes the
     * launches that go through it, and a `launch.json`/F5 launch reaches
     * `vscode.debug.startDebugging` through the debug configuration provider without ever
     * taking that lock. Any check followed by an `await` therefore leaves a window in
     * which both paths see "nothing is launching" for the same AppHost. Claiming the slot
     * in a single synchronous step closes that window, because the JavaScript event loop
     * cannot interleave the two callers inside it.
     */
    tryReserveLaunch(appHostPath: string): boolean {
        if (this.isLaunching(appHostPath)) {
            return false;
        }

        this._lifecycleLaunchClaims.add(getAppHostPathComparisonKey(appHostPath));
        this.reserveLaunch(appHostPath);
        return true;
    }

    /**
     * Whether a lifecycle-owned launch currently holds the claim for this AppHost.
     *
     * Uses the same identity relation as {@link isLaunching}: an association that cannot be
     * proven counts as claimed, because letting a second launch proceed on an unproven
     * "different" would be the exact duplicate this claim exists to prevent.
     */
    hasLifecycleLaunchClaim(appHostPath: string): boolean {
        if (this._lifecycleLaunchClaims.has(getAppHostPathComparisonKey(appHostPath))) {
            return true;
        }

        return Array.from(this._lifecycleLaunchClaims).some(claimedPath =>
            compareAppHostIdentity(claimedPath, appHostPath) !== 'different');
    }

    /**
     * Records that a launch is in flight without refusing it.
     */
    reserveLaunch(appHostPath: string): string {
        const key = getAppHostPathComparisonKey(appHostPath);
        // Any pending expiry belongs to a reservation this one supersedes.
        this.cancelExternalReservationExpiry(key);
        if (this._launchingPaths.has(key)) {
            const existingReservationId = this._launchReservationIds.get(key);
            if (existingReservationId) {
                return existingReservationId;
            }
        }

        const reservationId = String(++this._nextLaunchReservationId);
        this._launchReservationIds.set(key, reservationId);
        this.recordLatestLaunchReservation(appHostPath, reservationId);
        if (this._launchingPaths.has(key)) {
            return reservationId;
        }
        this._launchingPaths.add(key);
        this._onDidChangeLaunchingState.fire();
        return reservationId;
    }

    /**
     * Claims the launching slot for a launch this service did not initiate -
     * `launch.json`/F5 goes straight to `vscode.debug.startDebugging` and never reaches
     * {@link launch}.
     *
        * Returns `false` when another launch or run session already owns the AppHost. Recording
        * the launch without refusing it would leave both callers running: a lifecycle caller
        * may have already passed its own check and be on its way to `startDebugging`, or an
        * editor session may already control the AppHost. Whoever claimed first wins, which is
        * the only rule that produces one process from a race.
     *
     * The reservation is self-expiring, because this path has no completion signal of its
     * own: when VS Code declines a configuration after resolving it, no session is created
     * and no terminate event ever fires. Once the session does appear it is visible as an
     * editor session, so the reservation has nothing left to cover.
     */
    tryReserveExternalLaunch(appHostPath: string, isDirectoryScope = false): string | false {
        if (isDirectoryScope) {
            if (this.hasLaunchingPathWithinDirectory(appHostPath) ||
                this.hasLifecycleLaunchWithinDirectory(appHostPath) ||
                this.hasActiveLifecycleOperationWithinDirectory(appHostPath) ||
                this.hasEditorRunSessionWithinDirectory(appHostPath)) {
                return false;
            }
        }
        else {
            const editorSessions = this.getEditorRunSessions(appHostPath);
            if (this.isLaunching(appHostPath) ||
                this.hasLifecycleLaunchClaim(appHostPath) ||
                this.hasActiveLifecycleOperation(appHostPath) ||
                editorSessions.sessions.length > 0 ||
                editorSessions.ambiguous) {
                return false;
            }
        }

        const key = getAppHostPathComparisonKey(appHostPath);
        this.cancelExternalReservationExpiry(key);
        const reservationId = String(++this._nextLaunchReservationId);
        this._launchReservationIds.set(key, reservationId);
        this.recordLatestLaunchReservation(appHostPath, reservationId);
        if (isDirectoryScope) {
            this._externalDirectoryLaunchReservations.add(key);
        }
        if (!this._launchingPaths.has(key)) {
            this._launchingPaths.add(key);
            this._onDidChangeLaunchingState.fire();
        }
        const expiry = setTimeout(() => {
            // Only expire while this timer is still the registered one for the key. Another
            // reservation arriving in the meantime cancels this timer, so reaching here means
            // nothing has superseded it.
            if (this._externalReservationExpiries.get(key) !== expiry ||
                this._launchReservationIds.get(key) !== reservationId) {
                return;
            }

            this._externalReservationExpiries.delete(key);
            this.clearLaunchingKey(key);
        }, externalLaunchReservationTimeoutMs);
        // A reservation must never be a reason for the host process to stay alive.
        expiry.unref?.();
        this._externalReservationExpiries.set(key, expiry);
        return reservationId;
    }

    /**
     * Moves a repeated debug-configuration resolver pass to its newly selected AppHost.
     *
     * This is synchronous so no other launch can interleave between releasing the old
     * reservation and claiming the new target. The old reservation is released even when
     * the replacement is refused, because VS Code will abandon this debug configuration.
     */
    replaceExternalLaunchReservation(previousAppHostPath: string, previousReservationId: string, appHostPath: string, isDirectoryScope = false): string | false {
        this.clearMatchingLaunching(previousAppHostPath, previousReservationId);
        return this.tryReserveExternalLaunch(appHostPath, isDirectoryScope);
    }

    private hasLaunchingPathWithinDirectory(directoryPath: string): boolean {
        return Array.from(this._launchingPaths).some(launchingPath =>
            isAppHostPathWithinDirectory(launchingPath, directoryPath) ||
            (this._externalDirectoryLaunchReservations.has(launchingPath) &&
                isAppHostPathWithinDirectory(directoryPath, launchingPath)));
    }

    private hasLifecycleLaunchWithinDirectory(directoryPath: string): boolean {
        return Array.from(this._lifecycleLaunchClaims)
            .some(claimedPath => isAppHostPathWithinDirectory(claimedPath, directoryPath));
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

    private cancelExternalReservationExpiry(key: string): void {
        const expiry = this._externalReservationExpiries.get(key);
        if (expiry) {
            clearTimeout(expiry);
            this._externalReservationExpiries.delete(key);
        }
    }

    /**
     * Clears launching state for the given AppHost path (e.g., when it
     * appears in the running AppHosts list).
     */
    clearLaunching(appHostPath: string): void {
        const key = getAppHostPathComparisonKey(appHostPath);
        this.clearLaunchingKey(key);
    }

    clearMatchingLaunching(appHostPath: string, reservationId?: string): void {
        const exactKey = getAppHostPathComparisonKey(appHostPath);
        if (this._launchingPaths.has(exactKey)) {
            if (reservationId === undefined || this._launchReservationIds.get(exactKey) === reservationId) {
                this.clearLaunchingKey(exactKey);
            }
            return;
        }

        // Only a proven identity clears another path's launching flag. An ambiguous
        // association would otherwise hide a launch that is still in flight.
        const matchingPaths = Array.from(this._launchingPaths).filter(launchingPath =>
            (compareAppHostIdentity(launchingPath, appHostPath) === 'same' ||
                (this._externalDirectoryLaunchReservations.has(launchingPath) &&
                    isAppHostPathWithinDirectory(appHostPath, launchingPath))) &&
            (reservationId === undefined || this._launchReservationIds.get(launchingPath) === reservationId));
        if (matchingPaths.length !== 1) {
            return;
        }

        this.clearLaunchingKey(matchingPaths[0]);
    }

    clearLaunchingForRunningAppHost(appHostPath: string): void {
        for (const session of this.getEditorRunSessions(appHostPath).sessions) {
            const reservationId = session.configuration[appHostLaunchReservationIdConfigKey];
            if (typeof reservationId === 'string') {
                this.clearMatchingLaunching(appHostPath, reservationId);
            }
        }
    }

    private clearLaunchingKey(key: string): void {
        this._lifecycleLaunchClaims.delete(key);
        this._launchReservationIds.delete(key);
        this._externalDirectoryLaunchReservations.delete(key);
        this.cancelExternalReservationExpiry(key);
        if (this._launchingPaths.delete(key)) {
            this._onDidChangeLaunchingState.fire();
        }
    }

    private isLatestLaunchReservation(appHostPath: string, reservationId: string): boolean {
        const exactReservationId = this._latestLaunchReservationIds.get(getAppHostPathComparisonKey(appHostPath));
        if (exactReservationId !== undefined) {
            return exactReservationId === reservationId;
        }

        const matchingReservationIds = Array.from(this._latestLaunchReservationIds)
            .filter(([launchingPath]) => compareAppHostIdentity(launchingPath, appHostPath) === 'same')
            .map(([, currentReservationId]) => currentReservationId);
        return matchingReservationIds.length !== 1 || matchingReservationIds[0] === reservationId;
    }

    private recordLatestLaunchReservation(appHostPath: string, reservationId: string): void {
        for (const knownPath of this._latestLaunchReservationIds.keys()) {
            if (compareAppHostIdentity(knownPath, appHostPath) === 'same') {
                this._latestLaunchReservationIds.set(knownPath, reservationId);
            }
        }

        this._latestLaunchReservationIds.set(getAppHostPathComparisonKey(appHostPath), reservationId);
    }

    private preserveStartedExternalLaunchReservation(appHostPath: string, reservationId: string): void {
        const key = getAppHostPathComparisonKey(appHostPath);
        if (this._launchReservationIds.get(key) === reservationId) {
            this.cancelExternalReservationExpiry(key);
        }
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
    async launch(appHostPath: string, command: AspireCommandType, noDebug: boolean, doStep?: string): Promise<void> {
        const launchToken = this.trackPendingRun(appHostPath, command);
        try {
            return await this.runWithAppHostLifecycleLock(appHostPath, this._lifecycleCancellationSource.token, async lockToken => {
                if (this._disposed) {
                    throw new vscode.CancellationError();
                }

                if (!this.tryReserveLaunch(appHostPath)) {
                    throw new vscode.CancellationError();
                }

                await this.launchCore(appHostPath, command, noDebug, doStep, 'user-selection', launchToken, lockToken);
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
            const cliAvailability = await checkCliAvailableOrRedirect('debug_gate');
            if (!cliAvailability.available) {
                throw new vscode.CancellationError();
            }
            throwIfCancelled(token);
            config.skipCliAvailabilityCheck = true;

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

async function getLaunchTelemetryProperties(appHostPath: string, command: AspireCommandType, noDebug: boolean, executionSuppressed: boolean) {
    const isDirectory = isDirectoryForTelemetry(appHostPath);
    return {
        mode: noDebug ? 'run' : 'debug',
        command: bucketAspireCommand(command),
        apphost_language: isDirectory ? await classifyAppHostDirectory(appHostPath) : classifyAppHostPath(appHostPath),
        execution_suppressed: executionSuppressed ? 'true' : 'false',
    };
}

function isDirectoryForTelemetry(appHostPath: string): boolean {
    try {
        return fs.statSync(appHostPath, { throwIfNoEntry: false })?.isDirectory() === true;
    }
    catch {
        return false;
    }
}

function isE2eDebugLaunchSuppressed(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE === 'true' &&
        !!process.env.ASPIRE_EXTENSION_E2E_STATE_FILE &&
        !!process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE &&
        process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH === 'true';
}
