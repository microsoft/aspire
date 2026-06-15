import * as vscode from 'vscode';
import * as path from 'path';
import { noAppHostInWorkspace } from '../loc/strings';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { AspireCommandType } from '../dcp/types';
import { AppHostDiscoveryService, getDebugTargetForCandidate, selectWorkspaceAppHostPath } from '../utils/appHostDiscovery';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { extensionLogOutputChannel } from '../utils/logging';
import { AppHostLaunchService } from '../services/AppHostLaunchService';

export class AspireEditorCommandProvider implements vscode.Disposable {
    private _disposables: vscode.Disposable[] = [];

    constructor(
        private readonly _appHostDiscoveryService: AppHostDiscoveryService,
        private readonly _launchService: AppHostLaunchService,
    ) {
        this._disposables.push(vscode.workspace.onDidChangeWorkspaceFolders(event => {
            void this.updateWorkspaceAppHostContext();
        }));

        this._disposables.push(this._appHostDiscoveryService.onDidChangeCandidates(workspaceFolder => {
            void this.processActiveDocumentForWorkspace(workspaceFolder);
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

    private async processActiveDocumentForWorkspace(workspaceFolder: vscode.WorkspaceFolder): Promise<void> {
        const activeDocument = vscode.window.activeTextEditor?.document;
        if (!activeDocument) {
            await this.updateWorkspaceAppHostContext();
            return;
        }

        const activeWorkspaceFolder = vscode.workspace.getWorkspaceFolder(activeDocument.uri);
        if (activeWorkspaceFolder?.uri.toString() === workspaceFolder.uri.toString()) {
            await this.processDocument(activeDocument);
        }
    }

    public async processDocument(document: vscode.TextDocument): Promise<void> {
        const fileExtension = path.extname(document.uri.fsPath).toLowerCase();
        const isSupportedFile = getResourceDebuggerExtensions().some(extension => extension.getSupportedFileTypes().includes(fileExtension));

        vscode.commands.executeCommand('setContext', 'aspire.editorSupportsRunDebug', isSupportedFile);
        vscode.commands.executeCommand('setContext', 'aspire.fileIsAppHost', await this.tryFindCandidateForEditorFile(document.uri.fsPath) !== undefined);
        await this.updateWorkspaceAppHostContext();
    }

    private async updateWorkspaceAppHostContext(): Promise<void> {
        const workspaceFolder = vscode.window.activeTextEditor
            ? vscode.workspace.getWorkspaceFolder(vscode.window.activeTextEditor.document.uri)
            : vscode.workspace.workspaceFolders?.[0];
        if (!workspaceFolder) {
            vscode.commands.executeCommand('setContext', 'aspire.workspaceHasAppHost', false);
            return;
        }

        const appHostPath = await this.trySelectWorkspaceAppHostPath(workspaceFolder);
        vscode.commands.executeCommand('setContext', 'aspire.workspaceHasAppHost', appHostPath !== undefined);
    }

    /**
     * Returns the resolved AppHost path from the active editor or workspace settings, or null if none is available.
     */
    public async getAppHostPath(): Promise<string | null> {
        if (vscode.window.activeTextEditor) {
            const candidate = await this.tryFindCandidateForEditorFile(vscode.window.activeTextEditor.document.uri.fsPath);
            if (candidate) {
                return getDebugTargetForCandidate(candidate);
            }
        }

        const workspaceFolder = vscode.window.activeTextEditor
            ? vscode.workspace.getWorkspaceFolder(vscode.window.activeTextEditor.document.uri)
            : vscode.workspace.workspaceFolders?.[0];
        if (!workspaceFolder) {
            return null;
        }

        return await this.trySelectWorkspaceAppHostPath(workspaceFolder) ?? null;
    }

    private async tryFindCandidateForEditorFile(filePath: string): Promise<CandidateAppHostDisplayInfo | undefined> {
        try {
            return await this._appHostDiscoveryService.tryFindCandidateForEditorFile(filePath);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to discover AppHost for editor file ${filePath}: ${error}`);
            return undefined;
        }
    }

    private async trySelectWorkspaceAppHostPath(workspaceFolder: vscode.WorkspaceFolder): Promise<string | undefined> {
        try {
            const appHosts = await this._appHostDiscoveryService.discover(workspaceFolder);
            return await selectWorkspaceAppHostPath(workspaceFolder, appHosts);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to discover AppHost candidates for workspace ${workspaceFolder.uri.fsPath}: ${error}`);
            return undefined;
        }
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

        await this._launchService.launch(appHostToRun, aspireCommand, noDebug, doStep);
    }

    dispose() {
        this._disposables.forEach(disposable => disposable.dispose());
    }
}
