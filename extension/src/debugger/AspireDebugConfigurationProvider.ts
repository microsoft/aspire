import * as vscode from 'vscode';
import { appHostLifecycleLaunchAlreadyClaimed, defaultConfigurationName } from '../loc/strings';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';
import { AppHostDiscoveryService, getDebugTargetForCandidate } from '../utils/appHostDiscovery';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostTelemetryTargetPathConfigKey } from './AspireDebugConfigurationMetadata';
import { getAspireDebugConfigurationCommand } from '../services/AppHostLaunchService';

/**
 * The part of `AppHostLaunchService` this provider needs to make a `launch.json`/F5
 * launch visible to the shared launching reservation.
 */
export interface ExternalLaunchReservation {
    /** Returns `false` when a lifecycle-owned launch already claimed this AppHost. */
    tryReserveExternalLaunch(appHostPath: string): boolean;
}

export class AspireDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
    constructor(
        private readonly _appHostDiscoveryService: AppHostDiscoveryService,
        private readonly _launchReservation: ExternalLaunchReservation) {
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
        const aspireConfig = config as AspireExtendedDebugConfiguration;
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
        // Read before the markers are stripped: an `AppHostLaunchService` launch reaches
        // this resolver through `startDebugging` and has already reserved its own slot, so
        // claiming it here as an external launch would make it refuse itself.
        const launchedByExtension = aspireConfig.launchedByExtension === true;
        delete aspireConfig.skipCliAvailabilityCheck;
        delete aspireConfig.launchedByExtension;

        if (typeof config.program === 'string') {
            const program = config.program;
            config.program = await this.resolveDebugTarget(program, folder);

            const telemetryTarget = await this.tryFindWorkspaceDefaultCandidate(program, folder);
            if (telemetryTarget) {
                config[appHostTelemetryTargetPathConfigKey] = telemetryTarget.path;
            }
            else {
                delete config[appHostTelemetryTargetPathConfigKey];
            }

            // This is the last hook before VS Code creates the session, and it is the only
            // point a `launch.json`/F5 launch shares with the tool-driven path, which goes
            // through `AppHostLaunchService`. Claiming here is what stops an agent from
            // starting a second AppHost in the window before the session exists. Only
            // `run` claims: publish/deploy/do sessions are not AppHost lifetimes.
            //
            // The concrete candidate is claimed in preference to `config.program`: the
            // default `${workspaceFolder}` configuration deliberately leaves `program` as
            // the directory, and a directory is not the same identity as the AppHost inside
            // it, so claiming the directory would leave the tool free to start a duplicate.
            if (!launchedByExtension && getAspireDebugConfigurationCommand(aspireConfig) === 'run') {
                const claimedPath = telemetryTarget?.path ?? (typeof config.program === 'string' ? config.program : undefined);
                if (claimedPath && !this._launchReservation.tryReserveExternalLaunch(claimedPath)) {
                    // A lifecycle-owned launch already claimed this AppHost and cannot be
                    // called back, so proceeding would produce two AppHosts for one project.
                    // Abort this session and tell the user why rather than starting a second.
                    void vscode.window.showInformationMessage(appHostLifecycleLaunchAlreadyClaimed);
                    return undefined;
                }
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
        return {
            type: 'aspire',
            request: 'launch',
            name: defaultConfigurationName,
            program: folder.uri.fsPath
        };
    }
}
