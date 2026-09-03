import * as vscode from 'vscode';
import { extensionLogOutputChannel } from '../utils/logging';
import { cleanupRun } from './runCleanupRegistry';

export type SendBrowserSessionTerminated = (runId: string, dcpId: string) => void;

/**
 * Owns the terminal state for one Aspire-launched browser debug session.
 *
 * js-debug is server-hosted and does not have a per-run adapter exit. The root VS Code session
 * ending, or a successful `stopDebugging`, is therefore the only point where Aspire can report
 * termination.
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
        // A failed explicit stop can still be followed by a natural root-session termination.
        // Keep observing that event while handling the rejection so disposal cannot create an
        // unhandled promise or suppress the eventual DCP notification and cleanup.
        void this.stop().catch(() => { });
    }

    private async stopCore(): Promise<void> {
        try {
            // A timed-out attempt may still confirm the shared session's termination while a newer
            // VS Code stop request is pending. Every generation races the same completion signal so
            // that confirmation settles all of them without letting stale promises own the cache.
            await Promise.race([
                Promise.resolve(vscode.debug.stopDebugging(this._session)),
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

        this.finish();
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
