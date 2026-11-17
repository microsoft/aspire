import * as vscode from 'vscode';
import { getAppHostIdentityKey } from '../utils/appHostIdentity';
import { extensionLogOutputChannel } from '../utils/logging';
import type { ResourceDebugAppHostTarget } from './resourceDebugContracts';
import {
    ExtensionResourceDebugTelemetry,
    type ResourceDebugAttachSessionMetadata,
    type ResourceDebugClock,
    type ResourceDebugTelemetry,
    monotonicResourceDebugClock,
} from './resourceDebugTelemetry';

const resourceDebugSessionMarkerConfigKey = '__aspireResourceDebugSessionMarker';

export interface ResourceDebugSessionEvents {
    readonly onDidStartDebugSession: vscode.Event<vscode.DebugSession>;
    readonly onDidTerminateDebugSession: vscode.Event<vscode.DebugSession>;
}

export interface ResourceDebugSessionAttempt {
    readonly configuration: vscode.DebugConfiguration;
    markStarted(): void;
    abandon(): void;
}

export interface ResourceDebugSessionRegistryOptions {
    readonly pendingStartTimeoutMs?: number;
    readonly telemetry?: ResourceDebugTelemetry;
    readonly clock?: ResourceDebugClock;
}

interface TrackedAttachAttempt {
    readonly marker: number;
    readonly resourceKey: string;
    readonly sessionIds: Set<string>;
    pendingStartTimeout: ReturnType<typeof setTimeout> | undefined;
    startAccepted: boolean;
    terminated: boolean;
    sessionStarted: boolean;
    sessionStartedAt: number | undefined;
    readonly telemetry: ResourceDebugAttachSessionMetadata;
}

/**
 * Tracks only attach sessions created by ResourceDebugService. The marker is intentionally
 * private to generated configurations so unrelated VS Code debug sessions cannot affect
 * resource attach serialization or lifecycle state.
 */
export class ResourceDebugSessionRegistry implements vscode.Disposable {
    private static readonly _defaultPendingStartTimeoutMs = 10_000;

    private readonly _attempts = new Map<number, TrackedAttachAttempt>();
    private readonly _attemptsByResource = new Map<string, Set<number>>();
    private readonly _resourceLocks = new Map<string, Promise<void>>();
    private readonly _onDidChangeSessions = new vscode.EventEmitter<void>();
    readonly onDidChangeSessions = this._onDidChangeSessions.event;
    private readonly _subscriptions: vscode.Disposable;
    private readonly _pendingStartTimeoutMs: number;
    private readonly _telemetry: ResourceDebugTelemetry;
    private readonly _clock: ResourceDebugClock;
    private _nextMarker = 0;

    constructor(events: ResourceDebugSessionEvents = vscode.debug, options: ResourceDebugSessionRegistryOptions = {}) {
        this._pendingStartTimeoutMs = options.pendingStartTimeoutMs ?? ResourceDebugSessionRegistry._defaultPendingStartTimeoutMs;
        this._telemetry = options.telemetry ?? new ExtensionResourceDebugTelemetry();
        this._clock = options.clock ?? monotonicResourceDebugClock;
        this._subscriptions = vscode.Disposable.from(
            events.onDidStartDebugSession(session => this._onDidStartDebugSession(session)),
            events.onDidTerminateDebugSession(session => this._onDidTerminateDebugSession(session)));
    }

    dispose(): void {
        this._subscriptions.dispose();
        for (const attempt of this._attempts.values()) {
            this._clearPendingStartExpiry(attempt);
        }
        this._attempts.clear();
        this._attemptsByResource.clear();
        this._resourceLocks.clear();
        this._onDidChangeSessions.dispose();
    }

    hasActiveSession(appHost: ResourceDebugAppHostTarget, resourceName: string): boolean {
        const attemptMarkers = this._attemptsByResource.get(this._getResourceKey(appHost, resourceName));
        if (!attemptMarkers) {
            return false;
        }

        return Array.from(attemptMarkers).some(marker => {
            const attempt = this._attempts.get(marker);
            return attempt !== undefined && !attempt.terminated && (attempt.startAccepted || attempt.sessionIds.size > 0);
        });
    }

    async runSerialized<T>(
        appHost: ResourceDebugAppHostTarget,
        resourceName: string,
        cancellationToken: vscode.CancellationToken | undefined,
        operation: () => Promise<T>,
        getCancelledResult: () => T,
    ): Promise<T> {
        const resourceKey = this._getResourceKey(appHost, resourceName);
        const precedingOperation = this._resourceLocks.get(resourceKey);
        let releaseCurrentOperation: (() => void) | undefined;
        const currentOperationGate = new Promise<void>(resolve => {
            releaseCurrentOperation = resolve;
        });
        // The map stores a canonical tail, not merely this caller's completion signal. A canceled
        // waiter releases its gate promptly, but its tail still waits for the predecessor so later
        // callers cannot overtake an active operation.
        const currentOperation = (precedingOperation?.catch(() => undefined) ?? Promise.resolve())
            .then(() => currentOperationGate);
        this._resourceLocks.set(resourceKey, currentOperation);

        try {
            if (!await this._waitForLock(precedingOperation, cancellationToken)) {
                return getCancelledResult();
            }

            return await operation();
        }
        finally {
            releaseCurrentOperation!();
            // A canceled waiter returns before its tail settles behind the active operation.
            // Defer deletion until that canonical tail settles so a later request cannot overtake it.
            void currentOperation.then(() => {
                if (this._resourceLocks.get(resourceKey) === currentOperation) {
                    this._resourceLocks.delete(resourceKey);
                }
            });
        }
    }

    createAttempt(
        appHost: ResourceDebugAppHostTarget,
        resourceName: string,
        configuration: vscode.DebugConfiguration,
        telemetry: ResourceDebugAttachSessionMetadata,
    ): ResourceDebugSessionAttempt {
        const resourceKey = this._getResourceKey(appHost, resourceName);
        const marker = ++this._nextMarker;
        const attempt: TrackedAttachAttempt = {
            marker,
            resourceKey,
            sessionIds: new Set<string>(),
            pendingStartTimeout: undefined,
            startAccepted: false,
            terminated: false,
            sessionStarted: false,
            sessionStartedAt: undefined,
            telemetry,
        };
        this._attempts.set(marker, attempt);
        const attemptMarkers = this._attemptsByResource.get(resourceKey) ?? new Set<number>();
        attemptMarkers.add(marker);
        this._attemptsByResource.set(resourceKey, attemptMarkers);

        return {
            configuration: {
                ...configuration,
                [resourceDebugSessionMarkerConfigKey]: marker,
            },
            markStarted: () => {
                if (this._attempts.get(attempt.marker) !== attempt || attempt.terminated) {
                    return;
                }

                attempt.startAccepted = true;
                if (attempt.sessionIds.size === 0) {
                    this._schedulePendingStartExpiry(attempt);
                }
                this._onDidChangeSessions.fire();
            },
            abandon: () => this._removeAttempt(attempt),
        };
    }

    private _onDidStartDebugSession(session: vscode.DebugSession): void {
        const attempt = this._getAttempt(session);
        if (!attempt || attempt.terminated) {
            return;
        }

        attempt.sessionIds.add(session.id);
        attempt.sessionStarted = true;
        attempt.sessionStartedAt ??= this._getTimestamp();
        this._clearPendingStartExpiry(attempt);
    }

    private _onDidTerminateDebugSession(session: vscode.DebugSession): void {
        const attempt = this._getAttempt(session);
        if (!attempt) {
            return;
        }

        attempt.sessionIds.delete(session.id);
        if (attempt.sessionIds.size > 0) {
            return;
        }

        attempt.terminated = true;
        if (attempt.sessionStarted) {
            this._recordTelemetry(() => this._telemetry.recordSessionEnd({
                ...attempt.telemetry,
                controller: 'editor',
                session_end_reason: 'terminated',
            }, this._getMeasurements(attempt.sessionStartedAt)));
        }
        this._removeAttempt(attempt);
    }

    private _getAttempt(session: vscode.DebugSession): TrackedAttachAttempt | undefined {
        const marker = session.configuration?.[resourceDebugSessionMarkerConfigKey];
        return typeof marker === 'number' ? this._attempts.get(marker) : undefined;
    }

    private _removeAttempt(attempt: TrackedAttachAttempt): void {
        this._clearPendingStartExpiry(attempt);
        this._attempts.delete(attempt.marker);
        const attemptMarkers = this._attemptsByResource.get(attempt.resourceKey);
        attemptMarkers?.delete(attempt.marker);
        if (attemptMarkers?.size === 0) {
            this._attemptsByResource.delete(attempt.resourceKey);
        }
        this._onDidChangeSessions.fire();
    }

    private _schedulePendingStartExpiry(attempt: TrackedAttachAttempt): void {
        this._clearPendingStartExpiry(attempt);
        attempt.pendingStartTimeout = setTimeout(() => {
            attempt.pendingStartTimeout = undefined;
            if (this._attempts.get(attempt.marker) === attempt && attempt.sessionIds.size === 0) {
                // Debug adapters can strip private configuration properties. Do not fall back to
                // matching sessions by process or configuration: that could claim an unrelated
                // debugger session. Expire this bounded entry and make the residual recovery risk
                // diagnosable instead.
                extensionLogOutputChannel.warn('Resource debugger session tracking expired before its debug session reported the private marker. A later attach may start another session.');
                this._removeAttempt(attempt);
            }
        }, this._pendingStartTimeoutMs);
    }

    private _clearPendingStartExpiry(attempt: TrackedAttachAttempt): void {
        if (attempt.pendingStartTimeout) {
            clearTimeout(attempt.pendingStartTimeout);
            attempt.pendingStartTimeout = undefined;
        }
    }

    private _getMeasurements(startedAt: number | undefined): { readonly session_duration_ms?: number } {
        const duration = this._getDuration(startedAt, this._getTimestamp());
        return duration === undefined ? {} : { session_duration_ms: duration };
    }

    private _getTimestamp(): number | undefined {
        try {
            const timestamp = this._clock.now();
            return Number.isFinite(timestamp) ? timestamp : undefined;
        }
        catch {
            return undefined;
        }
    }

    private _getDuration(start: number | undefined, end: number | undefined): number | undefined {
        if (start === undefined || end === undefined) {
            return undefined;
        }

        const duration = end - start;
        return Number.isFinite(duration) && duration >= 0 ? duration : undefined;
    }

    private _recordTelemetry(record: () => void): void {
        try {
            record();
        }
        catch {
            // Telemetry is observational. Debug session lifecycle tracking must continue if it fails.
        }
    }

    private async _waitForLock(
        precedingOperation: Promise<void> | undefined,
        cancellationToken: vscode.CancellationToken | undefined,
    ): Promise<boolean> {
        if (!precedingOperation) {
            return !cancellationToken?.isCancellationRequested;
        }

        return await new Promise<boolean>(resolve => {
            let settled = false;
            let cancellationRegistration: vscode.Disposable | undefined;
            const settle = (acquired: boolean) => {
                if (settled) {
                    return;
                }

                settled = true;
                cancellationRegistration?.dispose();
                resolve(acquired);
            };

            cancellationRegistration = cancellationToken?.onCancellationRequested(() => settle(false));
            if (cancellationToken?.isCancellationRequested) {
                settle(false);
                return;
            }

            void precedingOperation.catch(() => undefined).then(() => settle(true));
        });
    }

    private _getResourceKey(appHost: ResourceDebugAppHostTarget, resourceName: string): string {
        const appHostProcessIdentity = appHost.appHostPid?.toString() ?? '';
        return `${getAppHostIdentityKey(appHost.absolutePath)}\u0000${appHostProcessIdentity}\u0000${resourceName}`;
    }
}
