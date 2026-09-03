import * as vscode from 'vscode';
import path from 'path';
import { stripComments } from 'jsonc-parser';
import { AspireConfigFile, aspireConfigFileName } from './cliTypes';
import { findAspireSettingsFiles } from './workspace';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess } from './process/cliProcess';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { CliPathResolutionTarget, getCliPathTargetForUri } from './cliPathVariables';
import { reportCliResolvedForOperation } from './cliOperationResolution';
import { extensionLogOutputChannel } from './logging';
import { getEnableAutoRestore } from './settings';
import { runningAspireRestore, runningAspireRestoreProgress, aspireRestoreCompleted, aspireRestoreAllCompleted, aspireRestoreFailed, aspireRestoreFailedStatusBar } from '../loc/strings';
import { ConfigInfoProvider, type CliVersionInfo } from './configInfoProvider';
import { codeGenerationVersionMarkerCapability } from '../types/configInfo';
import { classifyAppHostPath } from './appHostLanguage';

const generatedModulesDirectory = path.join('.aspire', 'modules');
const legacyGeneratedModulesDirectory = '.modules';
const codeGenerationVersionFileName = '.codegen-version';

interface ResolvedRestoreCli {
    readonly target: CliPathResolutionTarget;
    readonly cliPath: string;
}

/**
 * Automatically restores existing generated modules for non-.NET AppHosts when they were produced
 * by a different Aspire CLI version. Explicit restore commands retain the broader workspace-wide
 * behavior.
 */
export class AspirePackageRestoreProvider implements vscode.Disposable {
    private static readonly _maxConcurrency = 4;
    private static readonly _statusBarHideDelayMs = 5000;
    private static readonly _restoreTimeoutMs = 120_000;

    private readonly _disposables: vscode.Disposable[] = [];
    private readonly _terminalProvider: AspireTerminalProvider;
    private readonly _configInfoProvider: ConfigInfoProvider;
    private readonly _statusBarItem: vscode.StatusBarItem;
    private readonly _active = new Map<string, string>(); // configDir → relativePath
    private readonly _childProcesses = new Set<ChildProcessWithoutNullStreams>();
    private readonly _timeouts = new Set<ReturnType<typeof setTimeout>>();
    private readonly _pendingRestore = new Map<string, boolean>(); // configDir → force restore
    private readonly _failedDirs = new Set<string>(); // configDirs that failed
    private readonly _cliVersionByPath = new Map<string, Promise<CliVersionInfo | null>>();
    private readonly _markerSupportByCliIdentity = new Map<string, Promise<boolean>>();
    private _total = 0;
    private _completed = 0;
    private _hideTimeout: ReturnType<typeof setTimeout> | undefined;
    private _disposed = false;

    constructor(
        terminalProvider: AspireTerminalProvider,
        configInfoProvider: ConfigInfoProvider = new ConfigInfoProvider(terminalProvider),
    ) {
        this._terminalProvider = terminalProvider;
        this._configInfoProvider = configInfoProvider;
        this._statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
        this._statusBarItem.command = 'aspire-vscode.restore';
        this._disposables.push(this._statusBarItem);
    }

    async activate(): Promise<void> {
        this._disposables.push(
            vscode.workspace.onDidChangeConfiguration(e => {
                const shouldRecheck = e.affectsConfiguration('aspire.enableAutoRestore')
                    || e.affectsConfiguration('aspire.aspireCliExecutablePath');
                if (shouldRecheck && getEnableAutoRestore()) {
                    void this._restoreAll().catch(err => {
                        extensionLogOutputChannel.warn(`Auto-restore failed: ${String(err)}`);
                    });
                }
            }),
            vscode.workspace.onDidChangeWorkspaceFolders(() => {
                if (getEnableAutoRestore()) {
                    void this._restoreAll().catch(err => {
                        extensionLogOutputChannel.warn(`Auto-restore failed after workspace folders changed: ${String(err)}`);
                    });
                }
            }),
            vscode.workspace.onDidGrantWorkspaceTrust(() => {
                if (getEnableAutoRestore()) {
                    void this._restoreAll().catch(err => {
                        extensionLogOutputChannel.warn(`Auto-restore failed after workspace trust was granted: ${String(err)}`);
                    });
                }
            }),
        );

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
        if (this._disposed || (!force && (!getEnableAutoRestore() || !vscode.workspace.isTrusted))) {
            extensionLogOutputChannel.info('Auto-restore is disabled or the workspace is not trusted; skipping restore');
            return;
        }
        const allConfigs = await findAspireSettingsFiles();
        const configs = allConfigs.filter(uri => uri.fsPath.endsWith(aspireConfigFileName));
        if (configs.length === 0) {
            return;
        }

        this._cliVersionByPath.clear();
        this._markerSupportByCliIdentity.clear();
        if (this._active.size === 0) {
            this._total = 0;
            this._completed = 0;
            this._failedDirs.clear();
        }

        const pending = new Set<Promise<void>>();
        for (const uri of configs) {
            const p = this._restoreIfNeeded(uri, force).finally(() => pending.delete(p));
            pending.add(p);
            if (pending.size >= AspirePackageRestoreProvider._maxConcurrency) {
                await Promise.race(pending);
            }
        }
        await Promise.all(pending);
    }

    private async _restoreIfNeeded(uri: vscode.Uri, force: boolean, continueBatch = false): Promise<void> {
        if (this._disposed || (!force && (!getEnableAutoRestore() || !vscode.workspace.isTrusted))) {
            return;
        }

        let resolvedCli: ResolvedRestoreCli | undefined;
        if (!force) {
            let content: string;
            try {
                content = (await vscode.workspace.fs.readFile(uri)).toString();
            } catch (error) {
                extensionLogOutputChannel.warn(`Failed to read ${uri.fsPath}: ${error}`);
                return;
            }
            resolvedCli = await this._getAutoRestoreCli(uri, content);
            if (!resolvedCli) {
                return;
            }
        }

        const configDir = path.dirname(uri.fsPath);
        const relativePath = vscode.workspace.asRelativePath(uri);
        extensionLogOutputChannel.info(`${force ? 'Manual' : 'Automatic'} restore for ${relativePath}`);

        // Queue re-restore if one is already active for this config directory
        if (this._active.has(configDir)) {
            this._pendingRestore.set(
                configDir,
                (this._pendingRestore.get(configDir) ?? false) || force);
            return;
        }

        if (!continueBatch && this._active.size === 0 && this._completed >= this._total) {
            this._total = 0;
            this._completed = 0;
            this._failedDirs.clear();
        }
        this._total++;

        try {
            await this._runRestore(uri, configDir, relativePath, force, resolvedCli);
            if (this._disposed) {
                return;
            }
            this._failedDirs.delete(configDir);
            this._showProgress();
            this._scheduleHide();
        } catch (error) {
            if (this._disposed) {
                return;
            }
            this._failedDirs.add(configDir);
            this._showProgress();
            extensionLogOutputChannel.warn(`Restore failed for ${relativePath}: ${error}`);
        }

        // Preserve an explicit manual retry that arrived while an automatic restore was active.
        while (!this._disposed && this._pendingRestore.has(configDir)) {
            const reportPendingCliUse = this._pendingRestore.get(configDir) ?? false;
            this._pendingRestore.delete(configDir);
            await this._restoreIfNeeded(uri, reportPendingCliUse, true);
        }
    }

    private async _getAutoRestoreCli(uri: vscode.Uri, content: string): Promise<ResolvedRestoreCli | undefined> {
        let config: AspireConfigFile;
        try {
            config = JSON.parse(stripComments(content)) as AspireConfigFile;
        } catch (error) {
            extensionLogOutputChannel.warn(`Skipping auto-restore for invalid config ${uri.fsPath}: ${String(error)}`);
            return undefined;
        }

        const appHost = config.appHost;
        const configuredPath = appHost?.path?.trim();
        const configuredLanguage = appHost?.language?.trim().toLowerCase();
        if (!appHost || (!configuredPath && !configuredLanguage)) {
            return undefined;
        }
        if (configuredLanguage === 'csharp'
            || (!configuredLanguage && classifyAppHostPath(configuredPath) === 'csharp')) {
            return undefined;
        }

        const configDirectory = path.dirname(uri.fsPath);
        const appHostDirectory = configuredPath
            ? path.dirname(path.resolve(configDirectory, configuredPath))
            : configDirectory;
        let usesLegacyTypeScriptLayout = false;
        if (path.basename(configuredPath ?? '').toLowerCase() === 'apphost.ts') {
            try {
                const modernAppHost = await vscode.workspace.fs.stat(vscode.Uri.file(path.join(appHostDirectory, 'apphost.mts')));
                usesLegacyTypeScriptLayout = (modernAppHost.type & vscode.FileType.File) === 0;
            } catch (error) {
                if (isFileNotFound(error)) {
                    usesLegacyTypeScriptLayout = true;
                } else {
                    extensionLogOutputChannel.warn(`Unable to inspect the TypeScript AppHost layout in ${appHostDirectory}: ${String(error)}`);
                    return undefined;
                }
            }
        }
        const generatedDirectories = usesLegacyTypeScriptLayout
            ? [legacyGeneratedModulesDirectory, generatedModulesDirectory]
            : [generatedModulesDirectory, legacyGeneratedModulesDirectory];
        let modulesDirectory: string | undefined;
        for (const generatedDirectory of generatedDirectories) {
            const candidate = path.join(appHostDirectory, generatedDirectory);
            try {
                const stat = await vscode.workspace.fs.stat(vscode.Uri.file(candidate));
                if ((stat.type & vscode.FileType.Directory) !== 0) {
                    modulesDirectory = candidate;
                    break;
                }
            } catch (error) {
                // Missing generated modules are restored explicitly from the AppHost tree. Avoid
                // generating every sample or test AppHost merely because its config is in a workspace.
                if (!isFileNotFound(error)) {
                    extensionLogOutputChannel.warn(`Unable to inspect generated modules at ${candidate}: ${String(error)}`);
                }
            }
        }
        if (!modulesDirectory) {
            return undefined;
        }

        const target = getCliPathTargetForUri(uri);
        const cliPath = await this._terminalProvider.getAspireCliExecutablePath(target);
        let cliVersionPromise = this._cliVersionByPath.get(cliPath);
        if (!cliVersionPromise) {
            cliVersionPromise = this._configInfoProvider.getCliVersion({ target, cliPath });
            this._cliVersionByPath.set(cliPath, cliVersionPromise);
        }
        const cliVersion = await cliVersionPromise;
        if (!cliVersion) {
            extensionLogOutputChannel.warn(`Skipping auto-restore for ${vscode.workspace.asRelativePath(uri)} because the Aspire CLI version could not be determined.`);
            return undefined;
        }

        const cliIdentityKey = `${cliPath}\0${cliVersion.executableIdentity}`;
        let markerSupportPromise = this._markerSupportByCliIdentity.get(cliIdentityKey);
        if (!markerSupportPromise) {
            markerSupportPromise = this._configInfoProvider.getCapabilityStatus(
                codeGenerationVersionMarkerCapability,
                { target, cliPath, suppressErrors: true, forceRefresh: true })
                .then(status => status === 'supported');
            this._markerSupportByCliIdentity.set(cliIdentityKey, markerSupportPromise);
        }
        if (!await markerSupportPromise) {
            extensionLogOutputChannel.info(
                `Skipping auto-restore for ${vscode.workspace.asRelativePath(uri)} because the selected Aspire CLI does not advertise ${codeGenerationVersionMarkerCapability}.`);
            return undefined;
        }

        const versionUri = vscode.Uri.file(path.join(modulesDirectory, codeGenerationVersionFileName));
        let generatedVersion: string | undefined;
        try {
            generatedVersion = (await vscode.workspace.fs.readFile(versionUri)).toString().trim();
        } catch (error) {
            if (isFileNotFound(error)) {
                extensionLogOutputChannel.info(`Generated modules for ${vscode.workspace.asRelativePath(uri)} have no Aspire CLI version marker; restoring once to create it.`);
                return { target, cliPath };
            }

            extensionLogOutputChannel.warn(`Unable to read generated module version marker ${versionUri.fsPath}: ${String(error)}`);
            return undefined;
        }

        if (generatedVersion === cliVersion.version) {
            extensionLogOutputChannel.info(`Generated modules for ${vscode.workspace.asRelativePath(uri)} already match Aspire CLI ${cliVersion.version}; skipping restore.`);
            return undefined;
        }

        extensionLogOutputChannel.info(
            `Generated modules for ${vscode.workspace.asRelativePath(uri)} were created by Aspire CLI ${generatedVersion || '<unknown>'}; restoring with ${cliVersion.version}.`);
        return { target, cliPath };
    }

    private async _runRestore(
        uri: vscode.Uri,
        configDir: string,
        relativePath: string,
        reportCliUse: boolean,
        resolvedCli?: ResolvedRestoreCli,
    ): Promise<void> {
        if (this._disposed) {
            return;
        }

        this._active.set(configDir, relativePath);
        this._showProgress();

        try {
            const target = resolvedCli?.target ?? getCliPathTargetForUri(uri);
            const cliPath = resolvedCli?.cliPath ?? await this._terminalProvider.getAspireCliExecutablePath(target);
            if (this._disposed) {
                return;
            }
            if (reportCliUse) {
                reportCliResolvedForOperation(target, cliPath);
            }
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
            if (!this._disposed) {
                this._showProgress();
            }
        }

    }

    private _scheduleHide(): void {
        if (this._disposed) {
            return;
        }

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
        if (this._disposed) {
            return;
        }

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
        if (this._disposed) {
            return;
        }
        this._disposed = true;

        for (const proc of this._childProcesses) {
            try { proc.kill(); } catch { /* ignore */ }
        }
        this._childProcesses.clear();
        this._cliVersionByPath.clear();
        this._markerSupportByCliIdentity.clear();
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

function isFileNotFound(error: unknown): boolean {
    return error instanceof vscode.FileSystemError && error.code === 'FileNotFound';
}
