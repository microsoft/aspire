import type { AspireResourceDebugSession, RunSessionNotification, SessionTerminatedNotification } from './types';

export type RunSessionKind = 'adapter' | 'confirmedStop';
export type RunSessionLifecycle = 'starting' | 'running' | 'stopRequested' | 'completed';

export interface RunSessionRecord {
    readonly debugSessions: AspireResourceDebugSession[];
    readonly kind: RunSessionKind;
    readonly ownerDcpId: string;
    readonly runId: string;
    readonly startupCompletion: Promise<void>;
    completionRecorded: boolean;
    lifecycle: RunSessionLifecycle;
    retentionTimer?: NodeJS.Timeout;
    teardownPromise?: Promise<void>;
    teardownStarted: boolean;
    terminalSent: boolean;
}

export interface RunSessionRegistration {
    debugSessions: AspireResourceDebugSession[];
    kind: RunSessionKind;
    ownerDcpId: string;
    runId: string;
    startupCompletion: Promise<void>;
}

export interface RunSessionRegistryOptions {
    recordCompletion(runId: string, exitCode: number): void;
    retentionMs: number;
    scheduleTeardown(record: RunSessionRecord): void;
    send(ownerDcpId: string, notification: RunSessionNotification): void;
}

/**
 * Owns the terminal boundary for DCP run sessions.
 *
 * A requested stop is immediately terminal on an adapter-backed run's wire stream, while
 * its record remains for a bounded interval so a late adapter exit can supply telemetry.
 * Browser runs use confirmed-stop semantics and do not cross the terminal boundary until
 * VS Code reports that their debug session stopped.
 */
export class RunSessionRegistry {
    private readonly _options: RunSessionRegistryOptions;
    private readonly _records = new Map<string, RunSessionRecord>();
    private _disposed = false;

    constructor(options: RunSessionRegistryOptions) {
        this._options = options;
    }

    get size(): number {
        return this._records.size;
    }

    register(registration: RunSessionRegistration): RunSessionRecord {
        const record: RunSessionRecord = {
            ...registration,
            completionRecorded: false,
            lifecycle: 'starting',
            teardownStarted: false,
            terminalSent: false,
        };
        this._records.set(record.runId, record);
        return record;
    }

    get(runId: string): RunSessionRecord | undefined {
        return this._records.get(runId);
    }

    markRunning(runId: string): boolean {
        const record = this._records.get(runId);
        if (!record || record.lifecycle !== 'starting') {
            return false;
        }

        record.lifecycle = 'running';
        return true;
    }

    remove(runId: string): void {
        const record = this._records.get(runId);
        if (record) {
            this._evict(record);
        }
    }

    notify(notification: RunSessionNotification): void {
        const record = this._records.get(notification.session_id);
        if (this._disposed || !record) {
            return;
        }

        if (notification.notification_type === 'sessionTerminated') {
            this.terminate(record.runId, (notification as SessionTerminatedNotification).exit_code);
            return;
        }

        // DCP treats sessionTerminated as final. Adapter shutdown logs and restart events can
        // arrive after DELETE, but forwarding them would violate the terminal wire ordering.
        if (record.terminalSent) {
            return;
        }

        this._options.send(record.ownerDcpId, {
            ...notification,
            dcp_id: record.ownerDcpId,
        });
    }

    requestStop(runId: string): boolean {
        const record = this._records.get(runId);
        if (this._disposed || !record || record.terminalSent) {
            return false;
        }

        record.lifecycle = 'stopRequested';
        this._sendTerminal(record, undefined);
        this._scheduleRetention(record);
        return true;
    }

    confirmStop(runId: string): boolean {
        const record = this._records.get(runId);
        if (this._disposed || !record || record.terminalSent) {
            return false;
        }

        this._sendTerminal(record, undefined);
        this._recordCompletion(record, -1);
        record.lifecycle = 'completed';
        this._evict(record);
        return true;
    }

    terminate(runId: string, exitCode: number | undefined): void {
        const record = this._records.get(runId);
        if (this._disposed || !record) {
            return;
        }

        const started = record.lifecycle !== 'starting';
        const naturalCompletion = record.lifecycle === 'running';
        this._sendTerminal(record, exitCode);
        // DCP omits exit_code when no process started, while telemetry uses -1 for cancellation.
        this._recordCompletion(record, exitCode ?? -1);
        record.lifecycle = 'completed';

        if (record.kind === 'adapter' && naturalCompletion) {
            // A follow-up DELETE can arrive after retention evicts this record. Schedule
            // teardown while the owned debug sessions are still reachable.
            this._options.scheduleTeardown(record);
        }

        if (record.kind === 'adapter' && started) {
            // Keep natural exits briefly so DCP's follow-up DELETE remains idempotent, and
            // keep requested stops so a late exit can refine telemetry before the deadline.
            this._scheduleRetention(record);
        }
        else {
            this._evict(record);
        }
    }

    private _sendTerminal(record: RunSessionRecord, exitCode: number | undefined): void {
        if (record.terminalSent) {
            return;
        }

        const notification: SessionTerminatedNotification = {
            notification_type: 'sessionTerminated',
            session_id: record.runId,
            dcp_id: record.ownerDcpId,
            ...(exitCode === undefined ? {} : { exit_code: exitCode }),
        };
        this._options.send(record.ownerDcpId, notification);
        // Delivery is synchronous, so commit the terminal boundary only after it returns. A throw
        // leaves the record available for DELETE to retry instead of suppressing its notification.
        record.terminalSent = true;
    }

    private _recordCompletion(record: RunSessionRecord, exitCode: number): void {
        if (record.completionRecorded) {
            return;
        }

        record.completionRecorded = true;
        this._options.recordCompletion(record.runId, exitCode);
    }

    private _scheduleRetention(record: RunSessionRecord): void {
        if (record.retentionTimer) {
            return;
        }

        record.retentionTimer = setTimeout(() => {
            record.retentionTimer = undefined;
            this._recordCompletion(record, -1);
            record.lifecycle = 'completed';
            this._evict(record);
        }, this._options.retentionMs);
    }

    private _evict(record: RunSessionRecord): void {
        if (record.retentionTimer) {
            clearTimeout(record.retentionTimer);
            record.retentionTimer = undefined;
        }
        if (this._records.get(record.runId) === record) {
            this._records.delete(record.runId);
        }
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        for (const record of this._records.values()) {
            this._recordCompletion(record, -1);
            if (record.retentionTimer) {
                clearTimeout(record.retentionTimer);
                record.retentionTimer = undefined;
            }
        }
        this._records.clear();
    }
}
