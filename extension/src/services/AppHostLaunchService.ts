import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import { AspireCommandType, AspireExtendedDebugConfiguration, AspireOperationKind } from '../dcp/types';
import { appHostLifecycleBusy, startDebuggingDeclined } from '../loc/strings';
import { classifyAppHostDirectory, classifyAppHostPath } from '../utils/appHostLanguage';
import { compareAppHostIdentity, getAppHostIdentityKeyInfo, getAppHostPathComparisonKey, type AppHostIdentityKeyInfo, type AppHostIdentityRelation } from '../utils/appHostIdentity';
import { classifyError, isCommandCancellation, sendTelemetryEvent, type EventProperties } from '../utils/telemetry';
import { bucketAspireCommand } from '../utils/telemetryBuckets';
import { extensionLogOutputChannel } from '../utils/logging';
import { checkCliAvailableOrRedirect } from '../utils/workspace';

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
    private readonly _lifecycleLocks = new Map<string, Promise<unknown>>();
    private readonly _lifecycleLockPathKeys = new Map<string, Set<string>>();
    private readonly _lifecycleCancellationSource = new vscode.CancellationTokenSource();
    private _getEditorSessions: () => readonly AppHostLaunchSession[] = () => [];
    private _getRunningAppHosts: (token: vscode.CancellationToken) => Promise<readonly RunningAppHost[]> = async () => [];
    private _disposed = false;

    private readonly _onDidChangeLaunchingState = new vscode.EventEmitter<void>();
    readonly onDidChangeLaunchingState = this._onDidChangeLaunchingState.event;

    private readonly _onDidTerminateAppHostDebugSession = new vscode.EventEmitter<AppHostDebugSessionTerminatedEvent>();
    readonly onDidTerminateAppHostDebugSession = this._onDidTerminateAppHostDebugSession.event;

    private readonly _onDidRequestLaunch = new vscode.EventEmitter<AppHostLaunchRequestedEvent>();
    readonly onDidRequestLaunch = this._onDidRequestLaunch.event;

    private readonly _debugSessionSubscription: vscode.Disposable;

    constructor() {
        // When a debug session terminates, clear launching state for that AppHost
        // so the tree reverts from "Starting..." if the launch failed or was cancelled.
        this._debugSessionSubscription = vscode.debug.onDidTerminateDebugSession(session => {
            const appHostPath = session.configuration?.program;
            if (appHostPath && session.configuration?.type === 'aspire') {
                const key = getAppHostPathComparisonKey(appHostPath);
                this._lifecycleLaunchClaims.delete(key);
                this.cancelExternalReservationExpiry(key);
                if (this._launchingPaths.delete(key)) {
                    this._onDidChangeLaunchingState.fire();
                }
                const command = getAspireDebugConfigurationCommand(session.configuration);
                this._onDidTerminateAppHostDebugSession.fire({
                    appHostPath,
                    command,
                    shouldRequestStopRefresh: command === 'run',
                });
            }
        });
    }

    dispose(): void {
        this._disposed = true;
        this._lifecycleCancellationSource.cancel();
        this._lifecycleCancellationSource.dispose();
        this._debugSessionSubscription.dispose();
        this._lifecycleLocks.clear();
        this._lifecycleLockPathKeys.clear();
        for (const expiry of this._externalReservationExpiries.values()) {
            clearTimeout(expiry);
        }
        this._externalReservationExpiries.clear();
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
        for (const session of this._getEditorSessions()) {
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
        const identity = getAppHostIdentityKeyInfo(appHostPath);
        const key = this.getLifecycleLockKey(identity);
        this.trackLifecycleLockPathKeys(key, identity);
        const previous = this._lifecycleLocks.get(key) ?? Promise.resolve();
        let release!: () => void;
        const gate = new Promise<void>(resolve => { release = resolve; });
        // The queue tail follows the prior owner and this operation's gate. A cancelled
        // waiter releases its gate only after the prior owner settles, so later callers
        // cannot overtake a still-running editor launch.
        const tail = previous.then(() => gate, () => gate);
        this._lifecycleLocks.set(key, tail);
        void tail.then(() => {
            if (this._lifecycleLocks.get(key) === tail) {
                this._lifecycleLocks.delete(key);
                this._lifecycleLockPathKeys.delete(key);
            }
        });

        let acquired = false;
        let holdTimeout: NodeJS.Timeout | undefined;
        const holdCancellation = new vscode.CancellationTokenSource();
        const callerCancellation = token.onCancellationRequested(() => holdCancellation.cancel());
        try {
            await waitForPromise(previous, token, appHostLifecycleLockWaitTimeoutMs);
            acquired = true;
            // An operation that outruns the bound is cancelled rather than abandoned. The
            // lock stays with it until it settles: forcing the gate open would let the next
            // start/stop run alongside an operation that is still tearing down containers
            // or still driving `startDebugging`, producing the duplicate lifecycle this
            // lock exists to prevent. Waiters give up on their own budget with `busy`,
            // which is a truthful answer while the AppHost really is mid-operation.
            holdTimeout = setTimeout(() => {
                extensionLogOutputChannel.warn(`AppHost lifecycle operation for ${appHostPath} exceeded ${appHostLifecycleLockMaxHoldMs}ms; cancelling it. The lifecycle lock is held until it settles.`);
                holdCancellation.cancel();
            }, appHostLifecycleLockMaxHoldMs);
            // The backstop must never be a reason for the host process to stay alive.
            holdTimeout.unref?.();
            throwIfCancelled(token);
            return await action(holdCancellation.token);
        }
        finally {
            if (holdTimeout) {
                clearTimeout(holdTimeout);
            }
            callerCancellation.dispose();
            holdCancellation.dispose();
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
     * Maps every path that {@link compareAppHostIdentity} reports as the same AppHost onto
     * one lifecycle lock key.
     *
     * New lock owners use the identity model from {@link getAppHostIdentityKeyInfo}, but
     * active owners keep the exact project/source paths that were proven equivalent when
     * they entered. That snapshot is necessary because the directory can change while the
     * operation is still running: adding a second project should not let the original
     * project bypass the lock it already shares with `Program.cs`, and removing that
     * second project should not move a queued `Program.cs` caller onto a fresh key.
     */
    private getLifecycleLockKey(identity: AppHostIdentityKeyInfo): string {
        for (const pathKey of identity.pathKeys) {
            for (const [activeKey, activePathKeys] of this._lifecycleLockPathKeys) {
                if (activePathKeys.has(pathKey)) {
                    return activeKey;
                }
            }
        }

        return identity.key;
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
    reserveLaunch(appHostPath: string): void {
        const key = getAppHostPathComparisonKey(appHostPath);
        // Any pending expiry belongs to a reservation this one supersedes.
        this.cancelExternalReservationExpiry(key);
        if (this._launchingPaths.has(key)) {
            return;
        }

        this._launchingPaths.add(key);
        this._onDidChangeLaunchingState.fire();
    }

    /**
     * Claims the launching slot for a launch this service did not initiate -
     * `launch.json`/F5 goes straight to `vscode.debug.startDebugging` and never reaches
     * {@link launch}.
     *
     * Returns `false` when a lifecycle-owned launch already holds the claim. Recording the
     * launch without refusing it would leave both callers running: the lifecycle caller has
     * already passed its own check and is on its way to `startDebugging`, so nothing later
     * can stop it, and two AppHosts would start against the same project. Whoever claimed
     * first wins, which is the only rule that produces one process from a race.
     *
     * The reservation is self-expiring, because this path has no completion signal of its
     * own: when VS Code declines a configuration after resolving it, no session is created
     * and no terminate event ever fires. Once the session does appear it is visible as an
     * editor session, so the reservation has nothing left to cover.
     */
    tryReserveExternalLaunch(appHostPath: string): boolean {
        if (this.hasLifecycleLaunchClaim(appHostPath)) {
            return false;
        }

        const key = getAppHostPathComparisonKey(appHostPath);
        this.reserveLaunch(appHostPath);
        const expiry = setTimeout(() => {
            // Only expire while this timer is still the registered one for the key. Another
            // reservation arriving in the meantime cancels this timer, so reaching here means
            // nothing has superseded it.
            if (this._externalReservationExpiries.get(key) !== expiry) {
                return;
            }

            this._externalReservationExpiries.delete(key);
            if (this._launchingPaths.delete(key)) {
                this._onDidChangeLaunchingState.fire();
            }
        }, externalLaunchReservationTimeoutMs);
        // A reservation must never be a reason for the host process to stay alive.
        expiry.unref?.();
        this._externalReservationExpiries.set(key, expiry);
        return true;
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
        this._lifecycleLaunchClaims.delete(key);
        this.cancelExternalReservationExpiry(key);
        if (this._launchingPaths.delete(key)) {
            this._onDidChangeLaunchingState.fire();
        }
    }

    clearMatchingLaunching(appHostPath: string): void {
        const exactKey = getAppHostPathComparisonKey(appHostPath);
        this._lifecycleLaunchClaims.delete(exactKey);
        this.cancelExternalReservationExpiry(exactKey);
        if (this._launchingPaths.delete(exactKey)) {
            this._onDidChangeLaunchingState.fire();
            return;
        }

        // Only a proven identity clears another path's launching flag. An ambiguous
        // association would otherwise hide a launch that is still in flight.
        const matchingPaths = Array.from(this._launchingPaths).filter(launchingPath =>
            compareAppHostIdentity(launchingPath, appHostPath) === 'same');
        if (matchingPaths.length !== 1) {
            return;
        }

        this._launchingPaths.delete(matchingPaths[0]);
        this._lifecycleLaunchClaims.delete(matchingPaths[0]);
        this.cancelExternalReservationExpiry(matchingPaths[0]);
        this._onDidChangeLaunchingState.fire();
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
        return await this.runWithAppHostLifecycleLock(appHostPath, this._lifecycleCancellationSource.token, async lockToken => {
            if (this._disposed) {
                throw new vscode.CancellationError();
            }

            await this.launchCore(appHostPath, command, noDebug, doStep, lockToken);
        });
    }

    async launchFromLifecycleOwner(appHostPath: string, command: 'run', noDebug: boolean, token: vscode.CancellationToken): Promise<void> {
        if (this._disposed) {
            throw new vscode.CancellationError();
        }

        await this.launchCore(appHostPath, command, noDebug, undefined, token);
    }

    private async launchCore(
        appHostPath: string,
        command: AspireCommandType,
        noDebug: boolean,
        doStep: string | undefined,
        token: vscode.CancellationToken,
    ): Promise<void> {
        // Reserve before the first await. The awaits below (telemetry, the CLI gate) run
        // before `startDebugging`, so reserving later would leave a window in which a
        // concurrent F5 or tool-driven start sees no launch in flight for this AppHost.
        // The tree also shows "Starting..." from here, and every pre-start failure path
        // clears it because VS Code emits no terminate event for a launch that never
        // started. See https://code.visualstudio.com/api/references/vscode-api#debug.startDebugging
        this.reserveLaunch(appHostPath);
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
        const telemetryProperties = await releaseReservationOnFailure(
            () => getLaunchTelemetryProperties(appHostPath, command, noDebug, executionSuppressed));
        abortIfCancelled();

        const config: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            name: `Aspire ${command}: ${vscode.workspace.asRelativePath(appHostPath)}`,
            request: 'launch',
            program: appHostPath,
            command,
            noDebug,
            launchedByExtension: true
        };

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
            this.clearLaunching(appHostPath);
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
            this.clearLaunching(appHostPath);
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
