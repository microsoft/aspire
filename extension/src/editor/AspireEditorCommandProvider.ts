import * as vscode from 'vscode';
import * as path from 'path';
import { noAppHostInWorkspace } from '../loc/strings';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { AspireCommandType } from '../dcp/types';
import { aspireConfigFileName, getAppHostPathFromConfig, readJsonFile } from '../utils/cliTypes';

export class AspireEditorCommandProvider implements vscode.Disposable {
    private _workspaceAppHostPath: string | null = null;
    private _workspaceSettingsJsonWatchers: Map<vscode.WorkspaceFolder, vscode.Disposable> = new Map();
    private _disposables: vscode.Disposable[] = [];

    constructor() {
        // Watch for both aspire.config.json and .aspire/settings.json changes
        const workspaceFolder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file('/'));
        if (workspaceFolder) {
            this._workspaceSettingsJsonWatchers.set(workspaceFolder, this.watchWorkspaceForAppHostPathChanges(workspaceFolder, this.onChangeAppHostPath.bind(this)));
        }
        else {
            vscode.workspace.workspaceFolders?.forEach(folder => {
                this._workspaceSettingsJsonWatchers.set(folder, this.watchWorkspaceForAppHostPathChanges(folder, this.onChangeAppHostPath.bind(this)));
            });
        }

        // As additional workspace folders are added/removed, we need to watch/unwatch them too
        this._disposables.push(vscode.workspace.onDidChangeWorkspaceFolders(event => {
            event.added.forEach(folder => {
                this._workspaceSettingsJsonWatchers.set(folder, this.watchWorkspaceForAppHostPathChanges(folder, this.onChangeAppHostPath.bind(this)));
            });
            event.removed.forEach(folder => {
                const disposable = this._workspaceSettingsJsonWatchers.get(folder);
                if (disposable) {
                    disposable.dispose();
                    this._workspaceSettingsJsonWatchers.delete(folder);
                }
            });
        }));

        this._disposables.push(vscode.window.onDidChangeActiveTextEditor(async (editor) => {
            if (editor) {
                await this.processDocument(editor.document);
            }
        }));


        // Initialize context for the currently active document
        this.initializeActiveDocument();
    }

    private async initializeActiveDocument(): Promise<void> {
        const activeDocument = vscode.window.activeTextEditor?.document;
        if (activeDocument) {
            await this.processDocument(activeDocument);
        }
    }

    public async processDocument(document: vscode.TextDocument): Promise<void> {
        const fileExtension = path.extname(document.uri.fsPath).toLowerCase();
        const isSupportedFile = getResourceDebuggerExtensions().some(extension => extension.getSupportedFileTypes().includes(fileExtension));

        vscode.commands.executeCommand('setContext', 'aspire.editorSupportsRunDebug', isSupportedFile);

        if (await this.isAppHostFile(document.uri.fsPath)) {
            vscode.commands.executeCommand('setContext', 'aspire.fileIsAppHost', true);
        }
        else {
            vscode.commands.executeCommand('setContext', 'aspire.fileIsAppHost', false);
        }
    }

    private async isAppHostFile(filePath: string): Promise<boolean> {
        const fileText = await vscode.workspace.fs.readFile(vscode.Uri.file(filePath)).then(buffer => buffer.toString());
        const lines = fileText.split(/\r?\n/);

        // C# apphost detection
        if (lines.some(line => line.startsWith('#:sdk Aspire.AppHost.Sdk'))) {
            return true;
        }

        if (lines.some(line => line === 'var builder = DistributedApplication.CreateBuilder(args);')) {
            return true;
        }

        // TypeScript/JavaScript apphost detection
        const ext = path.extname(filePath).toLowerCase();
        if (['.ts', '.js', '.mts', '.mjs'].includes(ext)) {
            if (lines.some(line => /import\s+.*createBuilder.*from\s+['"].*\.modules\/aspire/.test(line))) {
                return true;
            }

            if (lines.some(line => /require\s*\(['"].*\.modules\/aspire/.test(line))) {
                return true;
            }
        }

        return false;
    }

    private onChangeAppHostPath(newPath: string | null) {
        vscode.commands.executeCommand('setContext', 'aspire.workspaceHasAppHost', !!newPath);
        this._workspaceAppHostPath = newPath;
    }

    private watchWorkspaceForAppHostPathChanges(workspaceFolder: vscode.WorkspaceFolder, onChangeAppHostPath: (newPath: string | null) => void): vscode.Disposable {
        const disposables: vscode.Disposable[] = [];

        // Watch new format: aspire.config.json in workspace root
        const newFormatWatcher = vscode.workspace.createFileSystemWatcher(
            new vscode.RelativePattern(workspaceFolder, aspireConfigFileName)
        );
        newFormatWatcher.onDidCreate(async uri => readConfigFileAndInvokeCallback(uri));
        newFormatWatcher.onDidChange(uri => readConfigFileAndInvokeCallback(uri));
        newFormatWatcher.onDidDelete(() => {
            // When new format is deleted, try to fall back to legacy format
            const legacyUri = vscode.Uri.joinPath(workspaceFolder.uri, '.aspire', 'settings.json');
            vscode.workspace.fs.stat(legacyUri).then(
                () => readConfigFileAndInvokeCallback(legacyUri),
                () => onChangeAppHostPath(null)
            );
        });
        disposables.push(newFormatWatcher);

        // Watch legacy format: .aspire/settings.json
        const legacyWatcher = vscode.workspace.createFileSystemWatcher(
            new vscode.RelativePattern(workspaceFolder, '.aspire/settings.json')
        );
        legacyWatcher.onDidCreate(async uri => {
            // Only use legacy if new format doesn't exist
            const newFormatUri = vscode.Uri.joinPath(workspaceFolder.uri, aspireConfigFileName);
            try {
                await vscode.workspace.fs.stat(newFormatUri);
                // New format exists, ignore legacy change
            } catch {
                readConfigFileAndInvokeCallback(uri);
            }
        });
        legacyWatcher.onDidChange(async uri => {
            const newFormatUri = vscode.Uri.joinPath(workspaceFolder.uri, aspireConfigFileName);
            try {
                await vscode.workspace.fs.stat(newFormatUri);
                // New format exists, ignore legacy change
            } catch {
                readConfigFileAndInvokeCallback(uri);
            }
        });
        legacyWatcher.onDidDelete(() => {
            // Legacy deleted; check if new format exists
            const newFormatUri = vscode.Uri.joinPath(workspaceFolder.uri, aspireConfigFileName);
            vscode.workspace.fs.stat(newFormatUri).then(
                () => readConfigFileAndInvokeCallback(newFormatUri),
                () => onChangeAppHostPath(null)
            );
        });
        disposables.push(legacyWatcher);

        // Read the initial value, preferring new format over legacy
        const newFormatUri = vscode.Uri.joinPath(workspaceFolder.uri, aspireConfigFileName);
        const legacyUri = vscode.Uri.joinPath(workspaceFolder.uri, '.aspire', 'settings.json');
        vscode.workspace.fs.stat(newFormatUri).then(
            () => readConfigFileAndInvokeCallback(newFormatUri),
            () => {
                // New format doesn't exist, try legacy
                vscode.workspace.fs.stat(legacyUri).then(
                    () => readConfigFileAndInvokeCallback(legacyUri),
                    () => onChangeAppHostPath(null)
                );
            }
        );

        return {
            dispose() {
                disposables.forEach(d => d.dispose());
            }
        };

        async function readConfigFileAndInvokeCallback(uri: vscode.Uri) {
            try {
                const json = await readJsonFile(uri);
                const appHostRelativePath = getAppHostPathFromConfig(json);
                if (!appHostRelativePath) {
                    onChangeAppHostPath(null);
                    return;
                }

                // Resolve relative path based on the config file's directory
                const configDir = path.dirname(uri.fsPath);
                const appHostPath = path.isAbsolute(appHostRelativePath)
                    ? appHostRelativePath
                    : path.join(configDir, appHostRelativePath);
                onChangeAppHostPath(appHostPath);
            }
            catch {
                onChangeAppHostPath(null);
            }
        }
    }

    /**
     * Returns the resolved AppHost path from the active editor or workspace settings, or null if none is available.
     */
    public async getAppHostPath(): Promise<string | null> {
        if (vscode.window.activeTextEditor && await this.isAppHostFile(vscode.window.activeTextEditor.document.uri.fsPath)) {
            return vscode.window.activeTextEditor.document.uri.fsPath;
        }

        return this._workspaceAppHostPath;
    }

    public async tryExecuteRunAppHost(noDebug: boolean): Promise<void> {
        await this.launchAspireDebugSession('run', noDebug);
    }

    public async tryExecuteDeployAppHost(noDebug: boolean): Promise<void> {
        await this.launchAspireDebugSession('deploy', noDebug);
    }

    public async tryExecutePublishAppHost(noDebug: boolean): Promise<void> {
        await this.launchAspireDebugSession('publish', noDebug);
    }

    public async tryExecuteDoAppHost(noDebug: boolean, doStep?: string): Promise<void> {
        await this.launchAspireDebugSession('do', noDebug, doStep);
    }

    private async launchAspireDebugSession(aspireCommand: AspireCommandType, noDebug: boolean, doStep?: string): Promise<void> {
        const appHostToRun = await this.getAppHostPath();
        if (!appHostToRun) {
            vscode.window.showErrorMessage(noAppHostInWorkspace);
            return;
        }

        const config: vscode.DebugConfiguration = {
            type: 'aspire',
            name: `Aspire ${aspireCommand}: ${vscode.workspace.asRelativePath(appHostToRun)}`,
            request: 'launch',
            program: appHostToRun,
            command: aspireCommand,
            noDebug: noDebug
        };

        if (doStep) {
            config.step = doStep;
        }

        await vscode.debug.startDebugging(undefined, config);
    }

    dispose() {
        this._disposables.forEach(disposable => disposable.dispose());
        this._workspaceSettingsJsonWatchers.forEach(disposable => disposable.dispose());
    }
}
