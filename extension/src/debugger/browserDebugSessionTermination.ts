import * as vscode from 'vscode';
import { extensionLogOutputChannel } from '../utils/logging';
import { cleanupRun } from './runCleanupRegistry';

export type SendBrowserSessionTerminated = (runId: string, dcpId: string) => void;

/**
 * Owns the terminal state for one Aspire-launched browser debug session.
 *
 * Browser adapters do not have a per-run adapter exit: js-debug is server-hosted, and Firefox can
 * disconnect independently from its browser process. The root VS Code session ending, or a
 * successful `stopDebugging`, is therefore the only point where Aspire can report termination.
 */
export class BrowserDebugSessionTermination {
    private readonly _session: vscode.DebugSession;
    private readonly _runId: string;
    private readonly _dcpId: string | null;
    private readonly _sendSessionTerminated: SendBrowserSessionTerminated;
    private readonly _terminationListener: vscode.Disposable;
    private _finished = false;
    private _stopPromise: Promise<void> | undefined;

    constructor(session: vscode.DebugSession, runId: string, dcpId: string | null, sendSessionTerminated: SendBrowserSessionTerminated) {
        this._session = session;
        this._runId = runId;
        this._dcpId = dcpId;
        this._sendSessionTerminated = sendSessionTerminated;
        this._terminationListener = vscode.debug.onDidTerminateDebugSession(terminatedSession => {
            // Chromium and Firefox create child target sessions. Only the root session that DCP
            // launched represents the resource lifetime.
            if (terminatedSession.id === session.id) {
                this.finish();
            }
        });
    }

    stop(): Promise<void> {
        if (this._finished) {
            return Promise.resolve();
        }

        this._stopPromise ??= this.stopCore();

        return this._stopPromise;
    }

    stopAndDisposeOnFailure(): void {
        // A failed explicit stop can still be followed by a natural root-session termination.
        // Keep observing that event while handling the rejection so disposal cannot create an
        // unhandled promise or suppress the eventual DCP notification and cleanup.
        void this.stop().catch(() => { });
    }

    private async stopCore(): Promise<void> {
        try {
            await vscode.debug.stopDebugging(this._session);
        }
        catch (error) {
            if (this._finished) {
                return;
            }

            this._stopPromise = undefined;
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
        this._terminationListener.dispose();

        if (this._dcpId) {
            this._sendSessionTerminated(this._runId, this._dcpId);
        }

        cleanupRun(this._runId);
    }
}
