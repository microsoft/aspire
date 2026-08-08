import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration, ResourceTerminationSignal } from '../dcp/types';
import { extensionLogOutputChannel } from '../utils/logging';
import { cleanupRun } from './runCleanupRegistry';

/**
 * Reads the termination signal off a resource debug configuration.
 *
 * Always go through this instead of reading `configuration.terminationSignal` directly. The
 * configuration round-trips through VS Code as untyped JSON (`vscode.DebugSession.configuration`
 * is a plain object rebuilt by VS Code), so the field is not guaranteed to still match its
 * declared type by the time a consumer sees it. Anything unrecognized falls back to
 * `adapterExit`, the behavior every process-backed resource type has.
 */
export function getResourceTerminationSignal(configuration: AspireResourceExtendedDebugConfiguration): ResourceTerminationSignal {
    return configuration.terminationSignal === 'debugSessionEnd' ? 'debugSessionEnd' : 'adapterExit';
}

/** Emits the terminal DCP notification for a run. Implemented by `AspireDebugSession`. */
export type SendSessionTerminated = (runId: string, dcpId: string) => void;

/**
 * Owns the stop/finish state machine for one resource debug session.
 *
 * There are exactly two terminal transitions and both have to be idempotent, because a run can
 * be torn down concurrently by a DCP `DELETE /run_session`, by VS Code terminating the session,
 * and by Aspire session disposal:
 *
 * - `finish()` performs the terminal bookkeeping exactly once: it stops listening for the VS Code
 *   termination event, emits `sessionTerminated` when this run owns that notification, and runs
 *   the per-run cleanup handlers (browser profile directory, Azure Functions host, ...).
 * - `stop()` requests a VS Code stop and finishes only if that stop succeeded. It is memoized so
 *   concurrent stops issue a single `vscode.debug.stopDebugging`, and it resolves only after
 *   VS Code has finished stopping so callers can sequence teardown.
 *   `AspireDebugSession.stopDebugging()` depends on that: it stops the AppHost first and only then
 *   the Aspire parent, which keeps VS Code's parent session cascade from racing the AppHost
 *   registry refresh. A failed stop leaves the run unfinished on purpose - see `stopCore`.
 *
 * Collecting both transitions here is the point: the signal is read once, in the constructor, so
 * no caller can partially reconstruct termination ownership.
 */
export class ResourceSessionTermination {
    private readonly _session: vscode.DebugSession;
    private readonly _runId: string;
    private readonly _dcpId: string | null;
    private readonly _signal: ResourceTerminationSignal;
    private readonly _sendSessionTerminated: SendSessionTerminated;

    private _terminationListener: vscode.Disposable | undefined;
    private _finished = false;
    private _stopPromise: Promise<void> | undefined;

    constructor(session: vscode.DebugSession, configuration: AspireResourceExtendedDebugConfiguration, sendSessionTerminated: SendSessionTerminated) {
        this._session = session;
        this._runId = configuration.runId;
        this._dcpId = configuration.debugSessionId;
        this._signal = getResourceTerminationSignal(configuration);
        this._sendSessionTerminated = sendSessionTerminated;
    }

    /**
     * Starts listening for the end of this VS Code debug session, when this run's termination is
     * driven by the session ending rather than by a debug adapter exit. No-op otherwise.
     */
    watchForDebugSessionEnd(): void {
        if (this._signal !== 'debugSessionEnd') {
            return;
        }

        this._terminationListener = vscode.debug.onDidTerminateDebugSession(terminatedSession => {
            // js-debug terminates target/page child sessions (and sessions belonging to other
            // parents) while this browser session is still alive, so only the root session this
            // instance owns is the DCP lifetime signal.
            if (terminatedSession.id !== this._session.id) {
                return;
            }

            this.finish();
        });
    }

    /**
     * Runs the terminal bookkeeping for the run. Safe to call repeatedly; only the first call
     * has an effect.
     */
    finish(): void {
        if (this._finished) {
            return;
        }

        this._finished = true;
        this._terminationListener?.dispose();
        this._terminationListener = undefined;

        // A run whose lifetime is the debug session reports its own termination. `dcpId` is the
        // same `debugSessionId` the adapter tracker addresses its notifications to; there is no
        // separate id, so there is nothing to keep in sync.
        //
        // This is the seam with #19125, which owns the run-scoped termination registry. When that
        // lands, emission moves behind its `runSessions.terminate(runId)` and this call goes away;
        // dedupe and retention are its concerns, not this class's. What stays here is stop
        // orchestration and profile cleanup, which are per-debug-session and have no home in a
        // run-keyed registry.
        if (this._signal === 'debugSessionEnd' && this._dcpId) {
            this._sendSessionTerminated(this._runId, this._dcpId);
        }

        cleanupRun(this._runId);
    }

    /**
     * Stops the VS Code debug session and then finishes the run.
     *
     * The returned promise settles only after VS Code has finished stopping, and rejects if VS Code
     * failed to stop the session. Callers that can act on (or report) the failure should await it;
     * fire-and-forget callers should use {@link stopAndLogFailure} so the rejection is not unhandled.
     */
    stop(): Promise<void> {
        this._stopPromise ??= this.stopCore();

        return this._stopPromise;
    }

    /**
     * Fire-and-forget variant of {@link stop} for disposal paths that have no caller to report a
     * failure to. Shares the memoized stop, so it never issues a second `stopDebugging`.
     */
    stopAndLogFailure(): void {
        // stopCore() already logged the failure; swallow here only to keep the rejection handled.
        void this.stop().catch(() => { });
    }

    /**
     * Releases the termination listener without finishing the run.
     *
     * A failed stop deliberately leaves this instance waiting for a real termination, which means
     * a session that never ends would otherwise hold the listener for the life of the extension
     * host. This is the bounded exit from that state, and it is an explicit disposal path rather
     * than a timeout on purpose: a timeout would have to guess how long a browser may legitimately
     * stay open, and firing it would mean reporting `sessionTerminated` for a run that never
     * actually ended — the exact false claim the failed-stop handling exists to avoid. Tying
     * teardown to the owning AspireDebugSession instead bounds the listener by something real,
     * because the extension is no longer tracking the run once that session is gone.
     *
     * Deliberately does not report termination or clean up: at this point nothing has observed the
     * debuggee ending, so the profile directory is left for the OS to reclaim.
     */
    dispose(): void {
        this._terminationListener?.dispose();
        this._terminationListener = undefined;
    }

    private async stopCore(): Promise<void> {
        try {
            await vscode.debug.stopDebugging(this._session);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to stop debug session '${this._session.name}': ${error instanceof Error ? error.message : String(error)}`);
            // Deliberately do not finish here. A rejected stop means VS Code never confirmed the
            // session ended, so the debuggee may well still be running. Reporting sessionTerminated
            // would tell DCP the run is over while it is not, and running cleanup would delete the
            // browser profile directory out from under a live browser.
            //
            // Leaving the state pending is recoverable: the termination listener stays registered,
            // so a session that later ends for real still finishes the lifecycle normally. Clearing
            // the memoized promise lets a subsequent stop retry rather than replaying the failure.
            // The cost is a leaked profile directory if the session never ends, which is strictly
            // better than corrupting a running browser or lying to DCP about the run's state.
            this._stopPromise = undefined;
            throw error;
        }

        this.finish();
    }
}
