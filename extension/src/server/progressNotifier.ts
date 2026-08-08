import * as vscode from 'vscode';
import { collapseWhitespace, escapeCodicons, formatText } from '../utils/strings';
import { extensionLogOutputChannel } from '../utils/logging';

export class ProgressNotifier {
    private _currentProgress?: {
        resolve: () => void;
        updateMessage: (msg: string) => void;
    };

    // If a new non-null status arrives within the delay, the clear is cancelled and the
    // current progress is updated.
    private _pendingClearTimeout?: ReturnType<typeof setTimeout>;

    public get isActive() {
        return !!this._currentProgress || !!this._pendingClearTimeout;
    }

    show(statusText: string | null) {
        extensionLogOutputChannel.info(`Setting status/progress: ${statusText ?? 'null'}`);

        if (!statusText) {
            // If there is an active progress, wait a short period before
            // actually clearing it. This allows callers to quickly call
            // show(null) followed by show(non-null) within 250ms and have the
            // existing progress updated instead of being torn down and recreated.
            if (this._currentProgress) {
                if (this._pendingClearTimeout) {
                    clearTimeout(this._pendingClearTimeout);
                }
                this._pendingClearTimeout = setTimeout(() => {
                    this.clear();
                    this._pendingClearTimeout = undefined;
                }, 250);
            }
            return;
        }

        // A new non-null status arrived; if there was a pending clear scheduled
        // from a recent show(null), cancel it so we can update the existing
        // progress in-place.
        if (this._pendingClearTimeout) {
            clearTimeout(this._pendingClearTimeout);
            this._pendingClearTimeout = undefined;
        }

        // If progress is already active, update its message
        if (this._currentProgress) {
            try {
                this._currentProgress.updateMessage(renderStatusMessage(statusText));
            }
            catch (err) {
                extensionLogOutputChannel.error(`Failed to update progress message: ${err}`);
            }
            return;
        }

        let resolveFn: () => void;
        const waitPromise = new Promise<void>(resolve => { resolveFn = resolve; });

        this._currentProgress = {
            resolve: () => { resolveFn(); },
            updateMessage: (_m: string) => {}
        };

        // `Window` rather than `Notification`: CLI status is reported for as long as the operation
        // runs, and a progress notification cannot be dismissed while it is active, so it sits on
        // top of the editor for the whole run (https://github.com/microsoft/aspire/issues/19036).
        // Window progress renders in the status bar, which the user can ignore or hide.
        vscode.window.withProgress({
            location: vscode.ProgressLocation.Window
        }, async progress => {
            this._currentProgress!.updateMessage = (m: string) => progress.report({ message: m });

            // Report the initial message as the progress message (no title)
            progress.report({ message: renderStatusMessage(statusText) });

            // Keep the progress alive until show(null) calls resolve
            return await waitPromise;
        }).then(undefined, (err: any) => {
            extensionLogOutputChannel.error(`Progress failed: ${err}`);
        });
    }

    clear() {
        // Cancel any scheduled deferred clear so we don't race with an
        // incoming show call.
        if (this._pendingClearTimeout) {
            clearTimeout(this._pendingClearTimeout);
            this._pendingClearTimeout = undefined;
        }

        if (this._currentProgress) {
            this._currentProgress.resolve();
            this._currentProgress = undefined;
        }
    }
}

/**
 * Makes CLI controlled status text safe to render as window progress: a single line, and without
 * arbitrary codicons injected into the status bar.
 */
function renderStatusMessage(statusText: string): string {
    return escapeCodicons(collapseWhitespace(formatText(statusText)));
}
