import * as vscode from 'vscode';
import { appHostLifecycleLaunchAlreadyClaimed, defaultConfigurationName } from '../loc/strings';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';
import { AppHostDiscoveryService, getDebugTargetForCandidate, isSamePath } from '../utils/appHostDiscovery';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { compareAppHostIdentity } from '../utils/appHostIdentity';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostLaunchReservationIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey } from './AspireDebugConfigurationMetadata';
import { getAspireDebugConfigurationCommand } from '../services/AppHostLaunchService';
import { getAspireDebugConfigurationExternalLaunchReservation, isAspireDebugConfigurationExtensionOwned, markAspireDebugConfigurationAsExtensionOwned, markAspireDebugConfigurationWithExternalLaunchReservation } from './AspireDebugConfigurationProviderInternal';

export { stripAspireDebugConfigurationProviderInternalProperties } from './AspireDebugConfigurationProviderInternal';

/**
 * The part of `AppHostLaunchService` this provider needs to make a `launch.json`/F5
 * launch visible to the shared launching reservation.
 */
export interface ExternalLaunchReservation {
    /** Returns the reservation ID, or `false` when another launch or run session already owns this AppHost. */
    tryReserveExternalLaunch(appHostPath: string, isDirectoryScope?: boolean): string | false;
    /** Replaces this resolver's previous reservation, or returns `false` when the new AppHost is already owned. */
    replaceExternalLaunchReservation(previousAppHostPath: string, previousReservationId: string, appHostPath: string, isDirectoryScope?: boolean): string | false;
}

export class AspireDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
    constructor(
        private readonly _appHostDiscoveryService: AppHostDiscoveryService,
        private readonly _launchReservation: ExternalLaunchReservation,
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
            program: getDebugTargetForCandidate(candidate)
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
        const configRecord = config as Record<string, unknown>;
        // Read before the marker is stripped: an `AppHostLaunchService` launch reaches this
        // resolver through `startDebugging` and has already reserved its own slot, so
        // claiming it here as an external launch would make it refuse itself.
        //
        // VS Code registers this provider for both initial and dynamic configurations, and
        // it can pass cloned configuration objects through the substituted resolver before the
        // adapter starts. The launch-service marker is therefore a per-activation value, and
        // this resolver refreshes the private marker before stripping the public transport
        // property. A launch.json can spell `launchedByExtension`, but it cannot know the
        // per-activation value that makes the property authoritative.
        const launchedByExtension = isAspireDebugConfigurationExtensionOwned(config);
        const existingExternalReservation = getAspireDebugConfigurationExternalLaunchReservation(config);
        if (launchedByExtension) {
            markAspireDebugConfigurationAsExtensionOwned(config);
        }
        else if (existingExternalReservation) {
            markAspireDebugConfigurationWithExternalLaunchReservation(
                config,
                existingExternalReservation.reservationId,
                existingExternalReservation.appHostPath,
                existingExternalReservation.isDirectoryScope);
            configRecord[appHostLaunchReservationIdConfigKey] = existingExternalReservation.reservationId;
        }
        else {
            delete configRecord[appHostLaunchReservationIdConfigKey];
        }
        delete aspireConfig.skipCliAvailabilityCheck;
        delete configRecord.launchedByExtension;

        if (typeof config.program === 'string') {
            const program = config.program;
            const isWorkspaceFolderLaunch = this.isWorkspaceFolderRoot(program, folder);
            if (aspireConfig[appHostSelectionOriginConfigKey] === 'explicit-launch-configuration' && isWorkspaceFolderLaunch) {
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
                if (!claimedPath) {
                    return config;
                }

                const isDirectoryScope = telemetryTarget === undefined && isWorkspaceFolderLaunch;
                let reservationPath = claimedPath;
                let reservationId: string | false;
                if (!existingExternalReservation) {
                    reservationId = this._launchReservation.tryReserveExternalLaunch(claimedPath, isDirectoryScope);
                }
                else if (existingExternalReservation.isDirectoryScope === isDirectoryScope &&
                    compareAppHostIdentity(existingExternalReservation.appHostPath, claimedPath) === 'same') {
                    reservationId = existingExternalReservation.reservationId;
                    // Keep the path where the reservation was actually stored. The identity
                    // can become ambiguous on a later resolver pass if sibling files appear.
                    reservationPath = existingExternalReservation.appHostPath;
                }
                else {
                    reservationId = this._launchReservation.replaceExternalLaunchReservation(
                        existingExternalReservation.appHostPath,
                        existingExternalReservation.reservationId,
                        claimedPath,
                        isDirectoryScope);
                }

                if (!reservationId) {
                    // Another launch or run session already owns this AppHost, so proceeding
                    // would produce two AppHosts for one project.
                    // Abort this session and tell the user why rather than starting a second.
                    void vscode.window.showInformationMessage(appHostLifecycleLaunchAlreadyClaimed);
                    return undefined;
                }

                config[appHostLaunchReservationIdConfigKey] = reservationId;
                markAspireDebugConfigurationWithExternalLaunchReservation(config, reservationId, reservationPath, isDirectoryScope);
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
            program: folder.uri.fsPath
        });
    }

    private withProvidedSelectionOrigin(config: vscode.DebugConfiguration): vscode.DebugConfiguration {
        return this._triggerKind === vscode.DebugConfigurationProviderTriggerKind.Dynamic
            ? { ...config, [appHostSelectionOriginConfigKey]: 'default-discovery' }
            : config;
    }

    private isWorkspaceFolderRoot(program: string, folder: vscode.WorkspaceFolder | undefined): boolean {
        const owningFolder = folder ?? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(program));
        return owningFolder !== undefined && isSamePath(program, owningFolder.uri.fsPath);
    }

    private ensureAppHostSelectionOrigin(config: AspireExtendedDebugConfiguration): void {
        if (!config[appHostSelectionOriginConfigKey]) {
            config[appHostSelectionOriginConfigKey] = config.program
                ? 'explicit-launch-configuration'
                : 'default-discovery';
        }
    }
}
