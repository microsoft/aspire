import * as vscode from 'vscode';
import { compareAppHostIdentity, getAppHostPathComparisonKey, isAppHostPathWithinDirectory } from '../utils/appHostIdentity';
import { appHostLaunchReservationIdConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { externalLaunchReservationTimeoutMs, type AppHostEditorSessions } from './appHostLaunchContracts';

/**
 * The parts of the launch service a reservation decision depends on but does not own:
 * editor debug sessions and the lifecycle locks.
 */
export interface AppHostLaunchReservationHost {
    getEditorRunSessions(appHostPath: string): AppHostEditorSessions;
    hasEditorRunSessionWithinDirectory(directoryPath: string): boolean;
    hasActiveLifecycleOperation(appHostPath: string): boolean;
    hasActiveLifecycleOperationWithinDirectory(directoryPath: string): boolean;
}

/**
 * Tracks which AppHost paths are currently in a "launching" state (between the user
 * clicking Run/Debug and the AppHost appearing in the running list or the debug session
 * terminating).
 */
export class AppHostLaunchReservations implements vscode.Disposable {
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
    private readonly _externalDirectoryLaunchReservations = new Set<string>();
    private readonly _launchReservationIds = new Map<string, string>();
    private readonly _latestLaunchReservationIds = new Map<string, string>();
    private _nextLaunchReservationId = 0;

    private readonly _onDidChangeLaunchingState = new vscode.EventEmitter<void>();
    readonly onDidChangeLaunchingState = this._onDidChangeLaunchingState.event;

    constructor(private readonly _host: AppHostLaunchReservationHost) {
    }

    dispose(): void {
        for (const expiry of this._externalReservationExpiries.values()) {
            clearTimeout(expiry);
        }
        this._externalReservationExpiries.clear();
        this._externalDirectoryLaunchReservations.clear();
        this._launchReservationIds.clear();
        this._latestLaunchReservationIds.clear();
        this._onDidChangeLaunchingState.dispose();
    }

    /**
     * Returns whether the given AppHost path is currently in a launching state.
     */
    get launchingPaths(): readonly string[] {
        return Array.from(this._launchingPaths);
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

    hasPendingLaunchOrLifecycleConflict(appHostPath: string, isDirectoryScope = false): boolean {
        return isDirectoryScope
            ? this.hasLaunchingPathWithinDirectory(appHostPath) ||
                this._host.hasActiveLifecycleOperationWithinDirectory(appHostPath)
            : this.isLaunching(appHostPath) ||
                this._host.hasActiveLifecycleOperation(appHostPath);
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
    tryReserveLaunch(appHostPath: string, trackRunGeneration = true): boolean {
        if (this.isLaunching(appHostPath)) {
            return false;
        }

        this._lifecycleLaunchClaims.add(getAppHostPathComparisonKey(appHostPath));
        this.reserveLaunch(appHostPath, trackRunGeneration);
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
     *
     * Only Run launches advance the latest-generation record that
     * {@link isLatestLaunchReservation} reads: pass `trackRunGeneration: false` for a
     * deploy/publish/do launch so it can still reserve and clean up its own launching slot
     * without overwriting the Run generation. Otherwise a publish started while a Run is
     * active would claim the latest generation, and the Run's own termination would then
     * look stale and skip its stop-state refresh.
     */
    reserveLaunch(appHostPath: string, trackRunGeneration = true): string {
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
        if (trackRunGeneration) {
            this.recordLatestLaunchReservation(appHostPath, reservationId);
        }
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
                this._host.hasActiveLifecycleOperationWithinDirectory(appHostPath) ||
                this._host.hasEditorRunSessionWithinDirectory(appHostPath)) {
                return false;
            }
        }
        else {
            const editorSessions = this._host.getEditorRunSessions(appHostPath);
            if (this.isLaunching(appHostPath) ||
                this.hasLifecycleLaunchClaim(appHostPath) ||
                this._host.hasActiveLifecycleOperation(appHostPath) ||
                editorSessions.sessions.length > 0 ||
                editorSessions.ambiguous) {
                return false;
            }
        }
        const key = getAppHostPathComparisonKey(appHostPath);
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
        this.scheduleExternalReservationExpiry(key, reservationId);
        return reservationId;
    }

    /**
     * Atomically validates a repeated resolver pass against the reservation generation it
     * received. A live pending reservation keeps its ID and gets a fresh expiry window. An
     * expired reservation may reacquire with a new ID, but an ID mismatch means a newer
     * owner won and must not be replaced or released by the stale resolver pass.
     */
    validateOrReacquireExternalLaunchReservation(appHostPath: string, reservationId: string, isDirectoryScope = false): string | false {
        const key = getAppHostPathComparisonKey(appHostPath);
        const ownsCurrentReservation = this._launchingPaths.has(key) &&
            this._launchReservationIds.get(key) === reservationId &&
            !this._lifecycleLaunchClaims.has(key) &&
            this._externalDirectoryLaunchReservations.has(key) === isDirectoryScope;
        if (ownsCurrentReservation) {
            if (this._externalReservationExpiries.has(key)) {
                this.scheduleExternalReservationExpiry(key, reservationId);
            }
            return reservationId;
        }

        return this.tryReserveExternalLaunch(appHostPath, isDirectoryScope);
    }

    private scheduleExternalReservationExpiry(key: string, reservationId: string): void {
        this.cancelExternalReservationExpiry(key);
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
        for (const session of this._host.getEditorRunSessions(appHostPath).sessions) {
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

    isLatestLaunchReservation(appHostPath: string, reservationId: string): boolean {
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

    preserveStartedExternalLaunchReservation(appHostPath: string, reservationId: string): void {
        const key = getAppHostPathComparisonKey(appHostPath);
        if (this._launchReservationIds.get(key) === reservationId) {
            this.cancelExternalReservationExpiry(key);
        }
    }
}
