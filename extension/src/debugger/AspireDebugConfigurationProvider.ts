import * as vscode from 'vscode';
import { defaultConfigurationName } from '../loc/strings';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';
import { AppHostDiscoveryService, getDebugTargetForCandidate, isSamePath } from '../utils/appHostDiscovery';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey } from './AspireDebugConfigurationMetadata';

export class AspireDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
    constructor(
        private readonly _appHostDiscoveryService: AppHostDiscoveryService,
        // VS Code writes the configurations returned by an `Initial`-kind provider verbatim into a
        // newly created launch.json, while `Dynamic`-kind configurations stay ephemeral. Only the
        // ephemeral ones may carry the internal selection-origin marker: persisting it would bake a
        // stale provenance into a user-owned file and permanently defeat the launch-configuration
        // scoping this marker exists to enable. See https://github.com/microsoft/aspire/issues/19080.
        private readonly _triggerKind: vscode.DebugConfigurationProviderTriggerKind = vscode.DebugConfigurationProviderTriggerKind.Dynamic) {
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

        return [this.withProvidedSelectionOrigin({
            type: 'aspire',
            request: 'launch',
            name: defaultConfigurationName,
            program: getDebugTargetForCandidate(candidate),
        })];
    }

    async resolveDebugConfiguration(folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration | null | undefined> {
        const aspireConfig = config as AspireExtendedDebugConfiguration;
        this.ensureAppHostSelectionOrigin(aspireConfig);
        if (!aspireConfig.skipCliAvailabilityCheck) {
            const result = await checkCliAvailableOrRedirect('debug_gate');
            if (!result.available) {
                return undefined; // Cancel the debug session
            }
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
        const aspireConfig = config as AspireExtendedDebugConfiguration;
        this.ensureAppHostSelectionOrigin(aspireConfig);
        delete aspireConfig.skipCliAvailabilityCheck;

        if (typeof config.program === 'string') {
            const program = config.program;
            if (aspireConfig[appHostSelectionOriginConfigKey] === 'explicit-launch-configuration' && this.isWorkspaceFolderRoot(program, folder)) {
                // Only a program pointing at the workspace folder root delegates the choice back to
                // normal discovery, which is what the extension's own default configuration does. A
                // configuration naming a specific AppHost file *or* subdirectory is scoped to that
                // target and must not become the workspace default.
                aspireConfig[appHostSelectionOriginConfigKey] = 'default-discovery';
            }

            config.program = await this.resolveDebugTarget(program, folder);

            const telemetryTarget = await this.tryFindWorkspaceDefaultCandidate(program, folder);
            if (telemetryTarget) {
                config[appHostTelemetryTargetPathConfigKey] = telemetryTarget.path;
            }
            else {
                delete config[appHostTelemetryTargetPathConfigKey];
            }
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

    private async tryFindWorkspaceDefaultCandidate(filePath: string, folder: vscode.WorkspaceFolder | undefined): Promise<CandidateAppHostDisplayInfo | undefined> {
        try {
            return await this._appHostDiscoveryService.tryFindWorkspaceDefaultCandidate(filePath, folder);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to discover workspace AppHost telemetry target ${filePath}: ${error}`);
            return undefined;
        }
    }

    private createDefaultConfiguration(folder: vscode.WorkspaceFolder): vscode.DebugConfiguration {
        return this.withProvidedSelectionOrigin({
            type: 'aspire',
            request: 'launch',
            name: defaultConfigurationName,
            program: folder.uri.fsPath,
        });
    }

    private withProvidedSelectionOrigin(config: vscode.DebugConfiguration): vscode.DebugConfiguration {
        if (this._triggerKind !== vscode.DebugConfigurationProviderTriggerKind.Dynamic) {
            // Leave the marker off so resolve-time classification runs against whatever the user
            // ends up with in launch.json rather than against provenance frozen at creation time.
            return config;
        }

        return { ...config, [appHostSelectionOriginConfigKey]: 'default-discovery' };
    }

    private isWorkspaceFolderRoot(program: string, folder: vscode.WorkspaceFolder | undefined): boolean {
        const owningFolder = folder ?? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(program));
        return owningFolder !== undefined && isSamePath(program, owningFolder.uri.fsPath);
    }

    private ensureAppHostSelectionOrigin(config: AspireExtendedDebugConfiguration): void {
        if (config[appHostSelectionOriginConfigKey]) {
            return;
        }

        config[appHostSelectionOriginConfigKey] = config.program
            ? 'explicit-launch-configuration'
            : 'default-discovery';
    }
}
