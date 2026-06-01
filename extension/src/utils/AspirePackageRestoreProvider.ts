import * as vscode from 'vscode';
import path from 'path';
import { aspireConfigFileName } from './cliTypes';
import { findAspireSettingsFiles } from './workspace';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { extensionLogOutputChannel } from './logging';
import { getEnableAutoRestore } from './settings';
import { runningAspireRestore, runningAspireRestoreProgress, aspireRestoreCompleted, aspireRestoreAllCompleted, aspireRestoreFailed, aspireRestoreFailedStatusBar } from '../loc/strings';

/**
 * Runs `aspire restore` on workspace open and whenever aspire.config.json content changes
 * (e.g. after a git branch switch).
 */
export class AspirePackageRestoreProvider implements vscode.Disposable {
    private static readonly _maxConcurrency = 4;
    private static readonly _statusBarHideDelayMs = 5000;
    private static readonly _restoreTimeoutMs = 120_000;

    private readonly _disposables: vscode.Disposable[] = [];
    private readonly _terminalProvider: AspireTerminalProvider;
    private readonly _statusBarItem: vscode.StatusBarItem;
    private readonly _lastContent = new Map<string, string>(); // fsPath → content
    private readonly _active = new Map<string, string>(); // configDir → relativePath
    private readonly _childProcesses = new Set<ChildProcessWithoutNullStreams>();
    private readonly _timeouts = new Set<ReturnType<typeof setTimeout>>();
    private readonly _pendingRestore = new Set<string>(); // configDirs needing re-restore
    private readonly _failedDirs = new Set<string>(); // configDirs that failed
    private _total = 0;
    private _completed = 0;
    private _hideTimeout: ReturnType<typeof setTimeout> | undefined;

    constructor(terminalProvider: AspireTerminalProvider) {
        this._terminalProvider = terminalProvider;
        this._statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
        this._statusBarItem.command = 'aspire-vscode.restore';
        this._disposables.push(this._statusBarItem);
    }

    async activate(): Promise<void> {
        this._disposables.push(
            vscode.workspace.onDidChangeConfiguration(e => {
                if (e.affectsConfiguration('aspire.enableAutoRestore') && getEnableAutoRestore()) {
                    void this._restoreAll().catch(err => {
                        extensionLogOutputChannel.warn(`Auto-restore failed: ${String(err)}`);
                    });
                }
            })
        );

        // Always set up watchers so they're ready when the setting is toggled on.
        // _onChanged gates on the current setting value.
        this._watchConfigFiles();

        if (!getEnableAutoRestore()) {
            extensionLogOutputChannel.info('Auto-restore is disabled');
            return;
        }

        await this._restoreAll();
    }

    async retryRestore(): Promise<void> {
        this._failedDirs.clear();
        this._showProgress();
        await this._restoreAll(true);
    }

    private async _restoreAll(force = false): Promise<void> {
        if (!force && !getEnableAutoRestore()) {
            extensionLogOutputChannel.info('Auto-restore is disabled, skipping restore');
            return;
        }
        const allConfigs = await findAspireSettingsFiles();
        const configs = allConfigs.filter(uri => uri.fsPath.endsWith(aspireConfigFileName));
        if (configs.length === 0) {
            return;
        }

        this._total = configs.length;
        this._completed = 0;
        this._failedDirs.clear();

        const pending = new Set<Promise<void>>();
        for (const uri of configs) {
            const p = this._restoreIfChanged(uri, true).finally(() => pending.delete(p));
            pending.add(p);
            if (pending.size >= AspirePackageRestoreProvider._maxConcurrency) {
                await Promise.race(pending);
            }
        }
        await Promise.all(pending);
    }

    private _watchConfigFiles(): void {
        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            const watcher = vscode.workspace.createFileSystemWatcher(
                new vscode.RelativePattern(folder, `**/${aspireConfigFileName}`)
            );
            watcher.onDidChange(uri => void this._onChanged(uri).catch(err => extensionLogOutputChannel.warn(`Watcher handler failed: ${String(err)}`)));
            watcher.onDidCreate(uri => void this._onChanged(uri).catch(err => extensionLogOutputChannel.warn(`Watcher handler failed: ${String(err)}`)));
            watcher.onDidDelete(uri => {
                this._lastContent.delete(uri.fsPath);
            });
            this._disposables.push(watcher);
        }
    }

    private async _onChanged(uri: vscode.Uri): Promise<void> {
        if (!getEnableAutoRestore()) {
            return;
        }
        const configDir = path.dirname(uri.fsPath);
        // Don't inflate total if a re-restore is already queued for this directory
        if (!this._pendingRestore.has(configDir)) {
            if (this._active.size === 0 && this._completed >= this._total) {
                this._total = 1;
                this._completed = 0;
                this._failedDirs.clear();
            } else {
                this._total++;
            }
        }
        await this._restoreIfChanged(uri, false);
    }

    private async _restoreIfChanged(uri: vscode.Uri, isInitial: boolean): Promise<void> {
        if (!getEnableAutoRestore()) {
            return;
        }

        let content: string;
        try {
            content = (await vscode.workspace.fs.readFile(uri)).toString();
        } catch (error) {
            extensionLogOutputChannel.warn(`Failed to read ${uri.fsPath}: ${error}`);
            this._completed++;
            this._showProgress();
            this._scheduleHide();
            return;
        }

        const prev = this._lastContent.get(uri.fsPath);
        if (!isInitial && prev === content) {
            this._completed++;
            this._showProgress();
            this._scheduleHide();
            return;
        }

        const configDir = path.dirname(uri.fsPath);
        const relativePath = vscode.workspace.asRelativePath(uri);
        extensionLogOutputChannel.info(`${isInitial ? 'Initial' : 'Changed'} restore for ${relativePath}`);

        // Queue re-restore if one is already active for this config directory
        if (this._active.has(configDir)) {
            this._pendingRestore.add(configDir);
            return;
        }

        try {
            await this._runRestore(configDir, relativePath);
            // Only update baseline after successful restore so a retry is attempted on next change
            this._lastContent.set(uri.fsPath, content);
            this._failedDirs.delete(configDir);
            this._showProgress();
            this._scheduleHide();
        } catch (error) {
            this._failedDirs.add(configDir);
            this._showProgress();
            extensionLogOutputChannel.warn(`Restore failed for ${relativePath}: ${error}`);
        }

        // If a change arrived while we were restoring, re-read and restore again
        while (this._pendingRestore.delete(configDir)) {
            await this._restoreIfChanged(uri, false);
        }
    }

    private async _runRestore(configDir: string, relativePath: string): Promise<void> {
        this._active.set(configDir, relativePath);
        this._showProgress();

        try {
            const cliPath = await this._terminalProvider.getAspireCliExecutablePath();
            await new Promise<void>((resolve, reject) => {
                let settled = false;
                const proc = spawnCliProcess(this._terminalProvider, cliPath, ['restore'], {
                    workingDirectory: configDir,
                    noExtensionVariables: true,
                    exitCallback: code => {
                        if (settled) { return; }
                        settled = true;
                        if (code === 0) {
                            extensionLogOutputChannel.info(aspireRestoreCompleted(relativePath));
                            resolve();
                        } else {
                            extensionLogOutputChannel.warn(aspireRestoreFailed(relativePath, `exit code ${code}`));
                            reject(new Error(`exit code ${code}`));
                        }
                    },
                    errorCallback: error => {
                        if (settled) { return; }
                        settled = true;
                        extensionLogOutputChannel.warn(aspireRestoreFailed(relativePath, error.message));
                        reject(error);
                    },
                });
                this._childProcesses.add(proc);
                const timeout = setTimeout(() => {
                    if (settled) { return; }
                    settled = true;
                    try { proc.kill(); } catch { /* ignore */ }
                    reject(new Error('restore timed out'));
                }, AspirePackageRestoreProvider._restoreTimeoutMs);
                this._timeouts.add(timeout);
                proc.on('close', () => {
                    clearTimeout(timeout);
                    this._timeouts.delete(timeout);
                    this._childProcesses.delete(proc);
                });
            });
        } finally {
            this._active.delete(configDir);
            this._completed++;
            this._showProgress();
        }
    }

    private _scheduleHide(): void {
        if (this._hideTimeout) {
            clearTimeout(this._hideTimeout);
            this._timeouts.delete(this._hideTimeout);
            this._hideTimeout = undefined;
        }
        if (this._active.size === 0 && this._failedDirs.size === 0) {
            this._hideTimeout = setTimeout(() => {
                this._timeouts.delete(this._hideTimeout!);
                this._hideTimeout = undefined;
                if (this._active.size === 0 && this._failedDirs.size === 0) { this._statusBarItem.hide(); }
            }, AspirePackageRestoreProvider._statusBarHideDelayMs);
            this._timeouts.add(this._hideTimeout);
        }
    }

    private _showProgress(): void {
        if (this._active.size === 0 && this._failedDirs.size > 0) {
            this._statusBarItem.text = `$(error) ${aspireRestoreFailedStatusBar}`;
            this._statusBarItem.backgroundColor = new vscode.ThemeColor('statusBarItem.errorBackground');
        } else if (this._active.size === 0) {
            this._statusBarItem.text = `$(check) ${aspireRestoreAllCompleted}`;
            this._statusBarItem.backgroundColor = undefined;
        } else if (this._total <= 1) {
            this._statusBarItem.text = `$(sync~spin) ${runningAspireRestore([...this._active.values()][0])}`;
            this._statusBarItem.backgroundColor = undefined;
        } else {
            this._statusBarItem.text = `$(sync~spin) ${runningAspireRestoreProgress(this._completed, this._total)}`;
            this._statusBarItem.backgroundColor = undefined;
        }
        this._statusBarItem.show();
    }

    dispose(): void {
        for (const proc of this._childProcesses) {
            try { proc.kill(); } catch { /* ignore */ }
        }
        this._childProcesses.clear();
        for (const timeout of this._timeouts) {
            clearTimeout(timeout);
        }
        this._timeouts.clear();
        for (const d of this._disposables) {
            d.dispose();
        }
        this._disposables.length = 0;
    }
}
