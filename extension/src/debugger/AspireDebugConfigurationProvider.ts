import * as vscode from 'vscode';
import { defaultConfigurationName } from '../loc/strings';
import { AppHostDiscoveryService, getDebugTargetForCandidate } from '../utils/appHostDiscovery';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { extensionLogOutputChannel } from '../utils/logging';

export class AspireDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
    constructor(private readonly _appHostDiscoveryService: AppHostDiscoveryService) {
    }

    async provideDebugConfigurations(folder: vscode.WorkspaceFolder | undefined, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration[]> {
        if (folder === undefined) {
            return [];
        }

        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor) {
            return [this.createDefaultConfiguration(folder)];
        }

        const activeEditorFolder = vscode.workspace.getWorkspaceFolder(activeEditor.document.uri);
        if (activeEditorFolder?.uri.toString() !== folder.uri.toString()) {
            return [this.createDefaultConfiguration(folder)];
        }

        const candidate = await this.tryFindCandidateForEditorFile(activeEditor.document.uri.fsPath, folder);
        if (!candidate) {
            return [this.createDefaultConfiguration(folder)];
        }

        return [{
            type: 'aspire',
            request: 'launch',
            name: defaultConfigurationName,
            program: getDebugTargetForCandidate(candidate)
        }];
    }

    async resolveDebugConfiguration(folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration | null | undefined> {
         // Check if CLI is available before starting debug session
        const result = await checkCliAvailableOrRedirect();
        if (!result.available) {
            return undefined; // Cancel the debug session
        }

        if (!config.type) {
            config.type = 'aspire';
        }

        if (!config.request) {
            config.request = 'launch';
        }

        if (!config.name) {
            config.name = defaultConfigurationName;
        }

        if (!config.program) {
            config.program = folder?.uri.fsPath || '${workspaceFolder}';
        }

        return config;
    }

    async resolveDebugConfigurationWithSubstitutedVariables(folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration | null | undefined> {
        if (typeof config.program === 'string') {
            config.program = await this.resolveDebugTarget(config.program, folder);
        }

        return config;
    }

    private async tryFindCandidateForEditorFile(filePath: string, folder: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo | undefined> {
        try {
            return await this._appHostDiscoveryService.tryFindCandidateForEditorFile(filePath, folder);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to discover AppHost for debug configuration file ${filePath}: ${error}`);
            return undefined;
        }
    }

    private async resolveDebugTarget(filePath: string, folder: vscode.WorkspaceFolder | undefined): Promise<string> {
        try {
            return await this._appHostDiscoveryService.resolveDebugTarget(filePath, folder);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to resolve AppHost debug target ${filePath}: ${error}`);
            return filePath;
        }
    }

    private createDefaultConfiguration(folder: vscode.WorkspaceFolder): vscode.DebugConfiguration {
        return {
            type: 'aspire',
            request: 'launch',
            name: defaultConfigurationName,
            program: folder.uri.fsPath
        };
    }
}
