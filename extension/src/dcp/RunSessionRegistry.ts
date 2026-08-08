import { AspireResourceDebugSession, RunSessionNotification, SessionTerminatedNotification } from './types';

/**
 * Declares which late signal, if any, is authoritative for a run's termination.
 *
 * DCP only learns that a run session ended from a `sessionTerminated` notification
 * (`DcpExecutor.StopResourceAsync` polls the executable for `Finished` and the DELETE
 * 200 alone does not advance that state), so every run must terminate exactly once.
 * What differs between resource kinds is *which* signal carries the truth:
 *
 * - `adapterExit`: a debug adapter is attached and its exit reports the real exit code.
 *   A requested stop terminates the stream immediately and the registry keeps the record
 *   for a bounded window so the adapter's later exit can still refine the recorded exit
 *   code. This is the default for `PUT /run_session` resources.
 * - `debugSessionEnd`: no adapter exit ever arrives; the run ends when VS Code reports the
 *   debug session terminated. That signal carries no exit code, so nothing is gained by
 *   waiting after a requested stop.
 * - `requestOnly`: no external termination signal exists at all. `DELETE /run_session` (or
 *   server disposal) is the only terminator and is final the moment it arrives.
 *
 * This replaces per-call-site booleans: a caller declares the run's termination model once
 * at registration instead of each termination path re-deciding whether to send and dedupe.
 */
export type TerminationTrigger =
    | { kind: 'adapterExit' }
    | { kind: 'debugSessionEnd' }
    | { kind: 'requestOnly' };

export type RunSessionLifecycle = 'starting' | 'running' | 'stopRequested' | 'completed';

export interface RunSessionRecord {
    readonly debugSessions: AspireResourceDebugSession[];
    readonly runId: string;
    /**
     * Stable debug-session identity (`aspire-extension-run-<hex>`) that owns this run.
     *
     * This is deliberately the prefix and not the full DCP instance ID. DCP can start a new
     * instance within the same debug session, and the full ID changes when it does. Keying
     * routing and authorization on the immutable prefix means a reconnecting DCP instance
     * simply continues to receive this run's notifications, with no ownership handoff.
     */
    readonly sessionPrefix: string;
    readonly terminationTrigger: TerminationTrigger;
    lifecycle: RunSessionLifecycle;
    retentionTimer?: NodeJS.Timeout;
    teardownStarted: boolean;
    terminated: boolean;
}

export interface RunSessionRegistration {
    debugSessions: AspireResourceDebugSession[];
    runId: string;
    sessionPrefix: string;
    terminationTrigger: TerminationTrigger;
}

export interface RunSessionRegistryOptions {
    /**
     * Closes the telemetry pair for a run. Implementations must be idempotent: a requested
     * stop and a later adapter exit can both report completion for the same run.
     */
    recordCompletion(runId: string, exitCode: number | undefined): void;
    /**
     * How long a terminated `adapterExit` run stays registered so a late adapter exit can
     * still supply the true exit code.
     */
    retentionMs: number;
    send(sessionPrefix: string, notification: RunSessionNotification): void;
}

/**
 * Tracks in-flight run sessions by `runId`, mirroring the runId-keyed
 * `../debugger/runCleanupRegistry`.
 *
 * The registry is the single owner of run termination: it guarantees one `sessionTerminated`
 * per run, drops post-terminal traffic, and resolves notification routing from the run's
 * immutable `sessionPrefix` rather than from whatever DCP instance ID a caller happened to
 * observe. Callers never route notifications themselves.
 */
export class RunSessionRegistry {
    private readonly _options: RunSessionRegistryOptions;
    private readonly _records = new Map<string, RunSessionRecord>();
    private readonly _retentionTimers = new Set<NodeJS.Timeout>();
    private _disposed = false;

    constructor(options: RunSessionRegistryOptions) {
        this._options = options;
    }

    get size(): number {
        return this._records.size;
    }

    values(): IterableIterator<RunSessionRecord> {
        return this._records.values();
    }

    register(registration: RunSessionRegistration): RunSessionRecord {
        const record: RunSessionRecord = {
            debugSessions: registration.debugSessions,
            lifecycle: 'starting',
            runId: registration.runId,
            sessionPrefix: registration.sessionPrefix,
            teardownStarted: false,
            terminated: false,
            terminationTrigger: registration.terminationTrigger,
        };
        this._records.set(registration.runId, record);

        return record;
    }

    get(runId: string): RunSessionRecord | undefined {
        return this._records.get(runId);
    }

    /**
     * Removes a run that never reached a live state, so a failed start leaves nothing behind.
     */
    remove(runId: string): void {
        const record = this._records.get(runId);
        if (record) {
            this._clearRetention(record);
            this._records.delete(runId);
        }
    }

    /**
     * Routes a notification produced for `runId`.
     *
     * Notifications for an unknown run are dropped. That is the deduplication mechanism for
     * late adapter callbacks: once a run is terminated and evicted there is nothing left to
     * publish under, so a stale callback cannot resurrect it or reach the raw transport.
     */
    notify(runId: string, notification: RunSessionNotification): void {
        const record = this._records.get(runId);
        if (this._disposed || !record || notification.session_id !== runId) {
            return;
        }

        if (notification.notification_type === 'sessionTerminated') {
            this.terminate(runId, (notification as SessionTerminatedNotification).exit_code);
            return;
        }

        // `sessionTerminated` is final on the DCP notification stream even when debugger
        // teardown is still running, so nothing may follow it.
        if (record.terminated) {
            return;
        }

        this._options.send(record.sessionPrefix, { ...notification, session_id: runId });
    }

    /**
     * Handles `DELETE /run_session`. Terminates the stream immediately so DCP observes the
     * stop even when debugger teardown is slow or never reports.
     *
     * Returns false when the run was already stopping or completed, which the caller answers
     * with 200 (the stop the client asked for has already happened).
     */
    requestStop(runId: string): boolean {
        const record = this._records.get(runId);
        if (!record || record.lifecycle === 'stopRequested' || record.lifecycle === 'completed') {
            return false;
        }

        record.lifecycle = 'stopRequested';
        this.terminate(runId, undefined);

        return true;
    }

    /**
     * Terminates a run exactly once. `exitCode` is omitted for a requested stop, which the
     * DCP contract permits (see `SessionTerminatedNotification.exit_code`).
     */
    terminate(runId: string, exitCode: number | undefined): void {
        const record = this._records.get(runId);
        if (this._disposed || !record) {
            return;
        }

        if (!record.terminated) {
            record.terminated = true;
            const notification: SessionTerminatedNotification = {
                notification_type: 'sessionTerminated',
                session_id: runId,
                dcp_id: record.sessionPrefix,
                ...(exitCode === undefined ? {} : { exit_code: exitCode }),
            };
            this._options.send(record.sessionPrefix, notification);
        }

        if (exitCode === undefined && record.terminationTrigger.kind === 'adapterExit') {
            // A requested stop on an adapter-backed run. The notification stream is terminal
            // immediately, but telemetry is deferred: the adapter's later exit reports the
            // process's real exit code, and the retention deadline records a cancellation if
            // no exit ever arrives.
            record.lifecycle = 'stopRequested';
            this._scheduleRetention(record);
            return;
        }

        // Reported even for a repeat call so a late adapter exit can replace a requested
        // stop's canceled bucket with the process's real exit code. The recorder is
        // idempotent, so only the first report for a run is emitted.
        this._options.recordCompletion(runId, exitCode ?? -1);

        record.lifecycle = 'completed';
        if (record.terminationTrigger.kind === 'adapterExit') {
            // DCP usually follows a natural exit with its own DELETE. Retention keeps the
            // record addressable so that DELETE is still answered 200 rather than 204 for
            // the most common shutdown ordering.
            this._scheduleRetention(record);
            return;
        }

        // No late signal is possible for this trigger, so release the run now. Later
        // notifications find no record and are dropped, and a repeated DELETE answers 204
        // per `docs/specs/IDE-execution.md`.
        this._evict(record);
    }

    private _scheduleRetention(record: RunSessionRecord): void {
        if (record.retentionTimer) {
            return;
        }

        const timer = setTimeout(() => {
            this._retentionTimers.delete(timer);
            record.retentionTimer = undefined;
            // Retention is the final lifecycle bound. Close telemetry as canceled if no
            // adapter exit arrived, then drop the last reference to this run.
            this._options.recordCompletion(record.runId, -1);
            record.lifecycle = 'completed';
            this._evict(record);
        }, this._options.retentionMs);
        record.retentionTimer = timer;
        this._retentionTimers.add(timer);
    }

    private _clearRetention(record: RunSessionRecord): void {
        if (record.retentionTimer) {
            clearTimeout(record.retentionTimer);
            this._retentionTimers.delete(record.retentionTimer);
            record.retentionTimer = undefined;
        }
    }

    private _evict(record: RunSessionRecord): void {
        this._clearRetention(record);
        if (this._records.get(record.runId) === record) {
            this._records.delete(record.runId);
        }
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        // Every run-session start must have one matching end event, and disposal can happen
        // before either an adapter exit or the retention deadline.
        for (const record of this._records.values()) {
            this._options.recordCompletion(record.runId, -1);
        }

        this._disposed = true;
        for (const timer of this._retentionTimers) {
            clearTimeout(timer);
        }
        this._retentionTimers.clear();
        this._records.clear();
    }
}
