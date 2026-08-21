import * as vscode from 'vscode';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess, terminateCliProcess } from '../utils/process/cliProcess';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { errorFetchingAppHosts } from '../loc/strings';
import { AppHostCliRunner, LimitedOutputBuffer, oneShotOutputBufferLimit } from './appHostCliRunner';
import { windowCliPathTarget } from '../utils/cliPathVariables';

export interface PsOutput {
    readonly stdout: string;
    readonly canCompleteGlobalLoading: boolean;
    readonly followOutputsToReplay?: readonly string[];
}

/**
 * Owns the `aspire ps` data source: the `--follow` stream, the interval fallback, the one-shot
 * authoritative snapshot, and the generation counters that discard stale async results. It produces
 * raw `ps` payloads and error/loading signals; interpreting them into the rendered AppHost list stays
 * with {@link AppHostDataRepository}.
 */
export class AppHostPsPoller implements vscode.Disposable {
    private static readonly _oneShotOutputBufferLimit = oneShotOutputBufferLimit;
    private static readonly _authoritativeSnapshotFollowOutputLimit = 256;

    private readonly _onDidReceivePsOutput = new vscode.EventEmitter<PsOutput>();
    readonly onDidReceivePsOutput = this._onDidReceivePsOutput.event;

    private readonly _onDidChangePsError = new vscode.EventEmitter<string | undefined>();
    readonly onDidChangePsError = this._onDidChangePsError.event;

    private readonly _onDidRequestClearLoading = new vscode.EventEmitter<void>();
    readonly onDidRequestClearLoading = this._onDidRequestClearLoading.event;

    private readonly _onDidStartPsFollow = new vscode.EventEmitter<void>();
    readonly onDidStartPsFollow = this._onDidStartPsFollow.event;

    private _pollingInterval: ReturnType<typeof setInterval> | undefined;
    private _psProcesses = new Set<ChildProcessWithoutNullStreams>();
    private _psPollingGeneration = 0;
    private _psFetchVersion = 0;
    private _supportsPsFollow = true;
    private _fetchInProgress = false;
    // Prevents a second `ps --follow` start while the first one is still resolving the CLI path.
    private _psFollowStartPending = false;
    private _authoritativeSnapshotInProgress = false;
    private _authoritativeSnapshotPending = false;
    private _authoritativeSnapshotPendingForce = false;
    private _authoritativeSnapshotRequestId = 0;
    private _activeAuthoritativeSnapshotRequestId: number | undefined;
    private readonly _authoritativeSnapshotFollowOutputs: string[] = [];
    private _authoritativeSnapshotFollowOutputsOverflowed = false;
    private _authoritativeSnapshotCaptured = false;
    private _authoritativeSnapshotContested = false;

    // Disposal, data-activity, and post-stop refresh scheduling stay owned by the repository; the
    // poller reads them through these accessors so it never holds a reference back to the repository.
    constructor(
        private readonly _terminalProvider: AspireTerminalProvider,
        private readonly _cliRunner: AppHostCliRunner,
        private readonly _isDisposed: () => boolean,
        private readonly _isDataActive: () => boolean,
        private readonly _clearPostStopRefreshTimers: () => void) {
    }

    get pollingActive(): boolean {
        return this._pollingInterval !== undefined
            || this._psProcesses.size > 0
            || this._fetchInProgress
            || this._psFollowStartPending;
    }

    get supportsPsFollow(): boolean {
        return this._supportsPsFollow;
    }

    startPsPolling(): void {
        // Restarting `ps` polling is routine while the workspace AppHost discovery result settles, the
        // polling interval changes, or the view resumes. Keep explicit post-stop refreshes alive across
        // those restarts; otherwise a debug-session stop can lose the authoritative `aspire ps` snapshot
        // that clears a stale global AppHost row.
        this.stopPolling({ clearPostStopRefreshTimers: false });
        if (this._supportsPsFollow) {
            this._startPsFollow();
            this._startPsFollowReconciliation();
            return;
        }

        this._startPsIntervalPolling();
    }

    private _startPsFollowReconciliation(): void {
        if (this._pollingInterval) {
            clearInterval(this._pollingInterval);
        }

        // The long-lived stream is the fast path, but an AppHost can start while the CLI is
        // transitioning from its initial scan to the follow subscription. Periodic authoritative
        // snapshots close that missed-delta window without restarting the stream.
        this._pollingInterval = setInterval(() => {
            if (!this._isDisposed()) {
                this.refreshAppHostsFromAuthoritativeSnapshot();
            }
        }, this.getPollingIntervalMs());
    }

    private _startPsIntervalPolling(fetchImmediately = true): void {
        if (this._pollingInterval) {
            clearInterval(this._pollingInterval);
            this._pollingInterval = undefined;
        }

        const intervalMs = this.getPollingIntervalMs();
        if (fetchImmediately) {
            this._fetchAppHosts();
        }
        this._pollingInterval = setInterval(() => {
            if (!this._isDisposed()) {
                this._fetchAppHosts();
            }
        }, intervalMs);
    }

    // Most callers are leaving the polling lifecycle and should cancel post-stop refreshes. Internal
    // restarts keep those timers so a pending AppHost-stop reconciliation is not lost.
    stopPolling(options?: { clearPostStopRefreshTimers?: boolean }): void {
        this._psPollingGeneration++;
        this._psFetchVersion++;
        this._fetchInProgress = false;
        this._psFollowStartPending = false;
        this._authoritativeSnapshotInProgress = false;
        this._authoritativeSnapshotPending = false;
        this._authoritativeSnapshotPendingForce = false;
        this._activeAuthoritativeSnapshotRequestId = undefined;
        this._authoritativeSnapshotFollowOutputs.length = 0;
        this._authoritativeSnapshotFollowOutputsOverflowed = false;
        this._authoritativeSnapshotCaptured = false;
        this._authoritativeSnapshotContested = false;
        if (options?.clearPostStopRefreshTimers ?? true) {
            this._clearPostStopRefreshTimers();
        }
        if (this._pollingInterval) {
            clearInterval(this._pollingInterval);
            this._pollingInterval = undefined;
            extensionLogOutputChannel.info(`aspire ps polling stopped`);
        }
        for (const psProcess of this._psProcesses) {
            void terminateCliProcess(psProcess, 'aspire ps').catch(error => {
                extensionLogOutputChannel.error(`Failed to terminate aspire ps: ${String(error)}`);
            });
        }
        this._psProcesses.clear();
    }

    clearPendingAuthoritativeSnapshot(): void {
        this._authoritativeSnapshotPending = false;
        this._authoritativeSnapshotPendingForce = false;
    }

    getPollingIntervalMs(): number {
        const config = vscode.workspace.getConfiguration('aspire');
        const interval = getConfiguredNumber(config, 'appHostsPollingInterval')
            ?? getConfiguredNumber(config, 'globalAppHostsPollingInterval')
            ?? config.get<number>('appHostsPollingInterval', 30000);
        return Math.max(interval, 1000);
    }

    private async _startPsFollow(): Promise<void> {
        const fetchVersion = ++this._psFetchVersion;
        this._psFollowStartPending = true;
        let cliPath: string;
        try {
            cliPath = await this._terminalProvider.getAspireCliExecutablePath(windowCliPathTarget);
        } catch (error) {
            if (this._isCurrentPsFetch(fetchVersion)) {
                this._psFollowStartPending = false;
                const errorMessage = errorFetchingAppHosts(String(error));
                extensionLogOutputChannel.warn(errorMessage);
                this._onDidChangePsError.fire(errorMessage);
                this._onDidRequestClearLoading.fire();
                this._supportsPsFollow = false;
                this._startPsIntervalPolling(false);
            }
            return;
        }
        if (!this._isCurrentPsFetch(fetchVersion)) {
            return;
        }

        let psProcess: ChildProcessWithoutNullStreams | undefined;
        let psProcessCompletedSynchronously = false;
        let callbackInvoked = false;
        const removePsProcess = () => {
            if (psProcess) {
                this._psProcesses.delete(psProcess);
            } else {
                psProcessCompletedSynchronously = true;
            }
        };

        const args = this._cliRunner.withNoLogo(['ps', '--follow', '--format', 'json'], cliPath);
        const psFollowStdout = new LimitedOutputBuffer(AppHostPsPoller._oneShotOutputBufferLimit);
        const psFollowStderr = new LimitedOutputBuffer(AppHostPsPoller._oneShotOutputBufferLimit);

        psProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
            createProcessGroup: true,
            noExtensionVariables: true,
            stdoutCallback: (data) => {
                psFollowStdout.append(data);
            },
            lineCallback: (line) => {
                if (!this._isCurrentPsFetch(fetchVersion) || line.trim().length === 0) {
                    return;
                }

                this._recordAuthoritativeSnapshotFollowOutput(line);
                this._onDidChangePsError.fire(undefined);
                this._onDidReceivePsOutput.fire({ stdout: line, canCompleteGlobalLoading: false });
            },
            stderrCallback: (data) => {
                psFollowStderr.append(data);
            },
            exitCallback: (code) => {
                removePsProcess();
                if (callbackInvoked) {
                    return;
                }
                callbackInvoked = true;
                if (!this._isCurrentPsFetch(fetchVersion)) {
                    return;
                }

                if (code !== 0) {
                    if (this._cliRunner.disableNoLogoForRetry(cliPath, args, psFollowStdout.value, psFollowStderr.value, 'aspire ps --follow')) {
                        this._startPsFollow();
                        return;
                    }

                    this._supportsPsFollow = false;
                    extensionLogOutputChannel.info('aspire ps --follow failed, falling back to aspire ps polling');
                    this._startPsIntervalPolling();
                    return;
                }

                this._startPsIntervalPolling();
            },
            errorCallback: (error) => {
                removePsProcess();
                if (callbackInvoked) {
                    return;
                }
                callbackInvoked = true;
                if (!this._isCurrentPsFetch(fetchVersion)) {
                    return;
                }

                extensionLogOutputChannel.warn(errorFetchingAppHosts(error.message));
                this._supportsPsFollow = false;
                this._startPsIntervalPolling();
            }
        });
        if (!psProcessCompletedSynchronously) {
            this._psProcesses.add(psProcess);
        }

        this._psFollowStartPending = false;
        this._onDidStartPsFollow.fire();
    }

    private _fetchAppHosts(): void {
        if (this._fetchInProgress || this._isDisposed() || !this._isDataActive()) {
            return;
        }
        this._fetchInProgress = true;
        const fetchVersion = ++this._psFetchVersion;

        const args = this._cliRunner.withNoLogo(['ps', '--format', 'json']);
        this._runPsCommand(args, (code, stdout, stderr) => {
            if (code === 0) {
                this._onDidChangePsError.fire(undefined);
                this._onDidReceivePsOutput.fire({ stdout, canCompleteGlobalLoading: true });
            } else {
                this._onDidRequestClearLoading.fire();
                this._onDidChangePsError.fire(errorFetchingAppHosts(stderr || `exit code ${code}`));
            }
            this._fetchInProgress = false;
        }, { fetchVersion });
    }

    refreshAppHostsFromAuthoritativeSnapshot(force = false): void {
        if (this._isDisposed() || (!force && !this._isDataActive())) {
            return;
        }

        if (this._authoritativeSnapshotInProgress) {
            this._authoritativeSnapshotPending = true;
            this._authoritativeSnapshotPendingForce ||= force;
            return;
        }

        this._authoritativeSnapshotInProgress = true;
        const snapshotRequestId = ++this._authoritativeSnapshotRequestId;
        this._activeAuthoritativeSnapshotRequestId = snapshotRequestId;
        this._authoritativeSnapshotFollowOutputs.length = 0;
        this._authoritativeSnapshotFollowOutputsOverflowed = false;
        this._authoritativeSnapshotCaptured = false;
        this._authoritativeSnapshotContested = false;
        const isCurrentSnapshot = () => this._activeAuthoritativeSnapshotRequestId === snapshotRequestId
            && !this._isDisposed()
            && (force || this._isDataActive());
        const pollingGeneration = this._psPollingGeneration;
        const args = this._cliRunner.withNoLogo(['ps', '--format', 'json']);
        this._runPsCommand(args, (code, stdout, stderr) => {
            if (this._activeAuthoritativeSnapshotRequestId !== snapshotRequestId) {
                return;
            }

            if (pollingGeneration !== this._psPollingGeneration) {
                this._activeAuthoritativeSnapshotRequestId = undefined;
                this._authoritativeSnapshotFollowOutputs.length = 0;
                this._authoritativeSnapshotFollowOutputsOverflowed = false;
                this._authoritativeSnapshotCaptured = false;
                this._authoritativeSnapshotContested = false;
                this._authoritativeSnapshotInProgress = false;
                return;
            }

            if (!this._isDisposed() && (force || this._isDataActive())) {
                if (code === 0) {
                    this._onDidChangePsError.fire(undefined);
                    if (this._authoritativeSnapshotFollowOutputsOverflowed || this._authoritativeSnapshotContested) {
                        // The replay window cannot be trusted, so applying the snapshot could
                        // overwrite newer follow state. Overflow queues a retry to reconcile once
                        // activity settles, without retaining an unbounded history. Contention is
                        // deliberately left to the polling timer instead: a delta landing inside the
                        // snapshot's startup window is common enough that retrying immediately could
                        // spawn `aspire ps` in a tight loop, and a contested window is itself proof
                        // that follow is delivering, which is when reconciliation matters least.
                        this._onDidRequestClearLoading.fire();
                    }
                    else {
                        // Apply the authoritative snapshot to recover AppHosts whose follow delta was
                        // missed, then replay deltas through the repository's canonical instance matcher.
                        this._onDidReceivePsOutput.fire({
                            stdout,
                            canCompleteGlobalLoading: true,
                            followOutputsToReplay: [...this._authoritativeSnapshotFollowOutputs],
                        });
                    }
                } else {
                    this._onDidRequestClearLoading.fire();
                    this._onDidChangePsError.fire(errorFetchingAppHosts(stderr || `exit code ${code}`));
                }
            }

            this._activeAuthoritativeSnapshotRequestId = undefined;
            this._authoritativeSnapshotFollowOutputs.length = 0;
            this._authoritativeSnapshotFollowOutputsOverflowed = false;
            this._authoritativeSnapshotCaptured = false;
            this._authoritativeSnapshotContested = false;
            this._authoritativeSnapshotInProgress = false;
            if (this._authoritativeSnapshotPending) {
                const pendingForce = this._authoritativeSnapshotPendingForce;
                this._authoritativeSnapshotPending = false;
                this._authoritativeSnapshotPendingForce = false;
                this.refreshAppHostsFromAuthoritativeSnapshot(pendingForce);
            }
        }, {
            force,
            isCurrent: isCurrentSnapshot,
            // `aspire ps` enumerates and then writes its JSON, so the first byte of output is a
            // safe lower bound for when the snapshot was captured. Deltas observed from that point
            // on are provably newer than the snapshot and are replayed over it; deltas observed
            // before it are unorderable and abandon the snapshot instead, which is handled in
            // `_recordAuthoritativeSnapshotFollowOutput`.
            onFirstStdout: () => {
                if (this._activeAuthoritativeSnapshotRequestId === snapshotRequestId) {
                    this._authoritativeSnapshotCaptured = true;
                }
            },
            onAttemptRestart: () => {
                if (this._activeAuthoritativeSnapshotRequestId === snapshotRequestId) {
                    this._authoritativeSnapshotCaptured = false;
                    this._authoritativeSnapshotContested = false;
                    this._authoritativeSnapshotFollowOutputs.length = 0;
                    this._authoritativeSnapshotFollowOutputsOverflowed = false;
                }
            },
        });
    }

    private _recordAuthoritativeSnapshotFollowOutput(line: string): void {
        if (this._activeAuthoritativeSnapshotRequestId === undefined) {
            return;
        }

        if (!this._authoritativeSnapshotCaptured) {
            // This delta cannot be ordered against the snapshot. It was observed before the first
            // byte of output, but the CLI had already enumerated at some unknown instant inside
            // that window, so the delta is either newer than the snapshot or older than it and
            // there is no evidence to tell which. Replaying it could resurrect an AppHost the
            // snapshot deliberately omits; dropping it lets the snapshot overwrite a stop that
            // really did happen after enumeration. The snapshot is abandoned instead, leaving the
            // live follow state in place until a later snapshot lands on an uncontested window.
            this._authoritativeSnapshotContested = true;
            return;
        }

        if (this._authoritativeSnapshotFollowOutputs.length < AppHostPsPoller._authoritativeSnapshotFollowOutputLimit) {
            this._authoritativeSnapshotFollowOutputs.push(line);
        }
        else {
            this._authoritativeSnapshotFollowOutputsOverflowed = true;
            this._authoritativeSnapshotPending = true;
        }
    }

    private _isCurrentPsFetch(fetchVersion: number): boolean {
        return !this._isDisposed() && this._isDataActive() && fetchVersion === this._psFetchVersion;
    }

    private async _runPsCommand(args: string[], callback: (code: number, stdout: string, stderr: string) => void, options?: { fetchVersion?: number; force?: boolean; isCurrent?: () => boolean; onFirstStdout?: () => void; onAttemptRestart?: () => void }): Promise<void> {
        const fetchVersion = options?.fetchVersion;
        const force = options?.force === true;
        const isCurrentPsCommand = () => {
            if (options?.isCurrent) {
                return options.isCurrent();
            }

            if (fetchVersion !== undefined) {
                return this._isCurrentPsFetch(fetchVersion);
            }

            return !this._isDisposed() && (force || this._isDataActive());
        };

        let cliPath: string;
        try {
            cliPath = await this._terminalProvider.getAspireCliExecutablePath(windowCliPathTarget);
        } catch (error) {
            if (isCurrentPsCommand()) {
                const rawErrorMessage = String(error);
                extensionLogOutputChannel.warn(errorFetchingAppHosts(rawErrorMessage));
                callback(1, '', rawErrorMessage);
            }
            return;
        }

        if (!isCurrentPsCommand()) {
            return;
        }
        const invocationArgs = this._cliRunner.normalizeNoLogoArgs(cliPath, args);

        let stdout = '';
        let stderr = '';
        let callbackInvoked = false;

        let psProcess: ChildProcessWithoutNullStreams | undefined;
        let psProcessCompletedSynchronously = false;
        const removePsProcess = () => {
            if (psProcess) {
                this._psProcesses.delete(psProcess);
            } else {
                psProcessCompletedSynchronously = true;
            }
        };

        psProcess = spawnCliProcess(this._terminalProvider, cliPath, invocationArgs, {
            createProcessGroup: true,
            noExtensionVariables: true,
            stdoutCallback: (data) => {
                if (stdout.length === 0 && data.length > 0) {
                    options?.onFirstStdout?.();
                }

                stdout += data;
            },
            stderrCallback: (data) => { stderr += data; },
            exitCallback: (code) => {
                removePsProcess();
                if (!callbackInvoked) {
                    if ((code ?? 1) !== 0) {
                        const retryArgs = this._cliRunner.tryGetNoLogoRetryArgs(cliPath, invocationArgs, stdout, stderr, 'aspire ps');
                        if (retryArgs) {
                            // A rejected --nologo can report itself on stdout, which already fired
                            // onFirstStdout for an attempt that produced no snapshot. Let the caller
                            // drop that false capture so the retry's window starts from its own output.
                            options?.onAttemptRestart?.();
                            this._runPsCommand(retryArgs, callback, options);
                            return;
                        }
                    }

                    callbackInvoked = true;
                    if (isCurrentPsCommand()) {
                        callback(code ?? 1, stdout, stderr);
                    }
                }
            },
            errorCallback: (error) => {
                removePsProcess();
                extensionLogOutputChannel.warn(errorFetchingAppHosts(error.message));
                if (!callbackInvoked) {
                    callbackInvoked = true;
                    if (isCurrentPsCommand()) {
                        callback(1, stdout, stderr || error.message);
                    }
                }
            }
        });
        if (!psProcessCompletedSynchronously) {
            this._psProcesses.add(psProcess);
        }
    }

    dispose(): void {
        // stopPolling owns the interval and child-process teardown, so dispose must route through it
        // rather than only releasing emitters. It is idempotent, and the repository already calls it first.
        this.stopPolling();
        this._onDidReceivePsOutput.dispose();
        this._onDidChangePsError.dispose();
        this._onDidRequestClearLoading.dispose();
        this._onDidStartPsFollow.dispose();
    }
}

function getConfiguredNumber(config: vscode.WorkspaceConfiguration, key: string): number | undefined {
    const inspection = config.inspect<number>(key);
    return inspection?.workspaceFolderValue
        ?? inspection?.workspaceValue
        ?? inspection?.globalValue;
}
