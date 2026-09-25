import * as vscode from 'vscode';
import { extensionLogOutputChannel } from '../utils/logging';
import { cleanupRun } from './runCleanupRegistry';

export type SendBrowserSessionTerminated = (runId: string, dcpId: string) => void;

/**
 * Owns the terminal state for one Aspire-launched browser debug session.
 *
 * js-debug is server-hosted and does not have a per-run adapter exit. The root VS Code session
 * ending is therefore the only point where Aspire can report termination; a successful
 * `stopDebugging` only acknowledges the DAP request.
 */
export class BrowserDebugSessionTermination {
    private readonly _session: vscode.DebugSession;
    private readonly _runId: string;
    private readonly _dcpId: string | null;
    private readonly _sendSessionTerminated: SendBrowserSessionTerminated;
    private readonly _terminationListener: vscode.Disposable;
    private readonly _completion: Promise<void>;
    private _resolveCompletion!: () => void;
    private _finished = false;
    private _stopPromise: Promise<void> | undefined;

    constructor(session: vscode.DebugSession, runId: string, dcpId: string | null, sendSessionTerminated: SendBrowserSessionTerminated) {
        this._session = session;
        this._runId = runId;
        this._dcpId = dcpId;
        this._sendSessionTerminated = sendSessionTerminated;
        this._completion = new Promise(resolve => {
            this._resolveCompletion = resolve;
        });
        this._terminationListener = vscode.debug.onDidTerminateDebugSession(terminatedSession => {
            // Chromium creates child target sessions. Only the root session that DCP launched
            // represents the resource lifetime.
            if (terminatedSession.id === session.id) {
                this.finish();
            }
        });
    }

    stop(): Promise<void> {
        if (this._finished) {
            return Promise.resolve();
        }

        if (!this._stopPromise) {
            const stop = this.stopCore();
            this._stopPromise = stop;
            void stop.catch(() => {
                if (this._stopPromise === stop) {
                    this._stopPromise = undefined;
                }
            });
        }

        return this._stopPromise;
    }

    resetStopAttempt(attempt: Promise<void>): void {
        if (this._stopPromise === attempt) {
            this._stopPromise = undefined;
        }
    }

    stopAndDisposeOnFailure(): void {
        // AspireDebugSession calls this only from its irreversible disposeCore path. Explicit stop
        // failures do not dispose the owner and keep this listener armed for natural termination or
        // a retry; once ownership ends, a failed final stop must not retain the listener.
        void this.stop().catch(() => this._terminationListener.dispose());
    }

    private async stopCore(): Promise<void> {
        try {
            // A timed-out attempt may still confirm the shared session's termination while a newer
            // VS Code stop request is pending. Every generation races the same completion signal so
            // that confirmation settles all of them without letting stale promises own the cache.
            // A DAP stop response only acknowledges the request; root-session termination remains
            // the authoritative lifecycle boundary.
            await Promise.race([
                Promise.resolve(vscode.debug.stopDebugging(this._session)).then(() => this._completion),
                this._completion,
            ]);
        }
        catch (error) {
            if (this._finished) {
                return;
            }

            extensionLogOutputChannel.warn(`Failed to stop browser debug session '${this._session.name}': ${error instanceof Error ? error.message : String(error)}`);
            throw error;
        }
    }

    private finish(): void {
        if (this._finished) {
            return;
        }

        this._finished = true;
        this._resolveCompletion();
        this._terminationListener.dispose();

        try {
            if (this._dcpId) {
                this._sendSessionTerminated(this._runId, this._dcpId);
            }
            else {
                extensionLogOutputChannel.warn(`Unable to report termination for run ${this._runId} because the DCP session ID is missing.`);
            }
        }
        finally {
            cleanupRun(this._runId);
        }
    }
}
