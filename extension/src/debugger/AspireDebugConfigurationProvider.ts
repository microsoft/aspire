import * as vscode from 'vscode';
import * as path from 'path';
import { appHostLifecycleLaunchAlreadyClaimed, appHostOperationAlreadyInProgress, defaultConfigurationName, defaultConfigurationNameForWorkspaceFolder, selectAppHostToLaunch } from '../loc/strings';
import type { AspireCommandType, AspireExtendedDebugConfiguration } from '../dcp/types';
import { AppHostDiscoveryService, formatAppHostLanguage, getDebugTargetForCandidate, isSamePath } from '../utils/appHostDiscovery';
import type { CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { findWorkspaceDefaultCandidate, sortCandidatesByPath } from '../utils/appHostCandidateSelection';
import { compareAppHostIdentity, isAppHostProjectFile } from '../utils/appHostIdentity';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { getCliPathTargetForUri, getCliPathTargetKey, windowCliPathTarget, workspaceFolderCliPathTarget, type CliPathResolutionTarget } from '../utils/cliPathVariables';
import { extensionLogOutputChannel } from '../utils/logging';
import { getLaunchFailureProviderKindForAppHostPath, recordLaunchFailureForAppHostPath, type LaunchFailureMode } from '../services/launchFailureJournal';
import { isAppHostSourceFile } from '../utils/paths/comparison';
import { doesFileExist } from '../utils/io';
import { isCommandCancellation } from '../utils/telemetry';
import { classifyAppHostPath } from '../utils/appHostLanguage';
import { appHostLaunchReservationIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey } from './AspireDebugConfigurationMetadata';
import { getAspireDebugConfigurationCommand } from '../services/AppHostLaunchService';
import { getAspireDebugConfigurationExternalLaunchReservation, getAspireDebugConfigurationResolvedCliPath, getAspireDebugConfigurationResolvedCliPathScope, isAspireDebugConfigurationExtensionOwned, markAspireDebugConfigurationAsExtensionOwned, markAspireDebugConfigurationWithExternalLaunchReservation, markAspireDebugConfigurationWithResolvedCliPath, markAspireDebugConfigurationWithResolvedCliPathScope, tryMarkAspireDebugConfigurationDiscoveryFailureRecorded } from './AspireDebugConfigurationProviderInternal';

export { stripAspireDebugConfigurationProviderInternalProperties } from './AspireDebugConfigurationProviderInternal';

const legacyDynamicConfigurationOwnerWorkspaceStateKey = 'aspire.debugger.legacyDynamicConfigurationOwnerUri';

/**
 * The part of `AppHostLaunchService` this provider needs to make a `launch.json`/F5
 * launch visible to the shared launching reservation.
 */
export interface ExternalLaunchReservation {
    /** Returns the reservation ID, or `false` when another launch or run session already owns this AppHost. */
    tryReserveExternalLaunch(appHostPath: string, isDirectoryScope?: boolean): string | false;
    /**
     * Validates and refreshes this launch's reservation, reacquiring it with a new ID when
     * it expired, or returns `false` when another launch now owns the AppHost.
     */
    validateOrReacquireExternalLaunchReservation(appHostPath: string, reservationId: string, isDirectoryScope?: boolean): string | false;
    /** Replaces this resolver's previous reservation, or returns `false` when the new AppHost is already owned. */
    replaceExternalLaunchReservation(previousAppHostPath: string, previousReservationId: string, appHostPath: string, isDirectoryScope?: boolean): string | false;
    /** Marks a provider-recorded failure only when this configuration belongs to an active service-owned launch. */
    markLaunchAttemptFailureRecorded(configuration: vscode.DebugConfiguration): void;
    /** Releases the reservation only when the path and reservation ID still identify the same launch. */
    releaseExternalLaunchReservation(appHostPath: string, reservationId: string): void;
    /** Claims a durable non-Run operation started from launch.json/F5. */
    tryReserveExternalOperation(
        appHostPath: string,
        command: Exclude<AspireCommandType, 'run'>,
        noDebug: boolean,
        doStep?: string,
        isDirectoryScope?: boolean,
    ): string | false;
    /** Validates or reacquires a repeated resolver pass for a durable non-Run operation. */
    validateOrReacquireExternalOperationReservation(
        appHostPath: string,
        reservationId: string,
        command: Exclude<AspireCommandType, 'run'>,
        noDebug: boolean,
        doStep?: string,
        isDirectoryScope?: boolean,
    ): string | false;
    /** Moves a repeated resolver pass to a different AppHost. */
    replaceExternalOperationReservation(
        previousAppHostPath: string,
        previousReservationId: string,
        appHostPath: string,
        command: Exclude<AspireCommandType, 'run'>,
        noDebug: boolean,
        doStep?: string,
        isDirectoryScope?: boolean,
    ): string | false;
    /** Releases a pending external operation when debug configuration resolution fails. */
    releaseExternalOperationReservation(appHostPath: string, reservationId: string): void;
    /** Prepares root Aspire CLI args for the exact executable that will handle this launch. */
    prepareLaunchArguments(
        appHostPath: string,
        command: AspireCommandType,
        args: string[] | undefined,
        token: vscode.CancellationToken,
        cliPath?: string,
        target?: CliPathResolutionTarget,
    ): Promise<{ args: string[] | undefined }>;
}

export class AspireDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
    private _legacyDynamicConfigurationOwnerUri: string | undefined;

    constructor(
        private readonly _appHostDiscoveryService: AppHostDiscoveryService,
        private readonly _launchReservation: ExternalLaunchReservation,
        private readonly _workspaceState: vscode.Memento,
        private readonly _triggerKind: vscode.DebugConfigurationProviderTriggerKind = vscode.DebugConfigurationProviderTriggerKind.Dynamic) {
    }

    async provideDebugConfigurations(folder: vscode.WorkspaceFolder | undefined, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration[]> {
        if (folder === undefined) {
            return [];
        }

        const activeEditor = vscode.window.activeTextEditor;
        if (!activeEditor) {
            return this.createDefaultConfigurations(folder);
        }

        const activeEditorFolder = vscode.workspace.getWorkspaceFolder(activeEditor.document.uri);
        if (activeEditorFolder?.uri.toString() !== folder.uri.toString()) {
            return this.createDefaultConfigurations(folder);
        }

        const candidate = await this.tryFindCandidateForEditorFile(activeEditor.document.uri.fsPath, folder);
        if (!candidate) {
            return this.createDefaultConfigurations(folder);
        }

        return this.createProvidedConfigurations(folder, getDebugTargetForCandidate(candidate));
    }

    async resolveDebugConfiguration(folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration | null | undefined> {
        const aspireConfig = config as AspireExtendedDebugConfiguration;
        this.ensureAppHostSelectionOrigin(aspireConfig);
        if (!aspireConfig.skipCliAvailabilityCheck) {
            const program = typeof config.program === 'string' ? config.program : undefined;
            const programFolder = program && path.isAbsolute(program) && !program.includes('${')
                ? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(program))
                : undefined;
            const target = programFolder
                ? workspaceFolderCliPathTarget(programFolder)
                : folder ? workspaceFolderCliPathTarget(folder) : windowCliPathTarget;
            if (!(await this.validateAndTrustCliPath(config, target))) {
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
        const existingExternalReservation = getAspireDebugConfigurationExternalLaunchReservation(config);
        if (typeof config.program === 'string') {
            const program = config.program;
            if (aspireConfig[appHostSelectionOriginConfigKey] === 'explicit-launch-configuration' && this.isWorkspaceFolderRoot(program, folder)) {
                aspireConfig[appHostSelectionOriginConfigKey] = 'default-discovery';
            }

            const resolvedProgram = await this.resolveDefaultDiscoveryTarget(aspireConfig, program, folder, token);
            if (resolvedProgram === undefined) {
                if (existingExternalReservation) {
                    if (existingExternalReservation.kind === 'operation') {
                        this._launchReservation.releaseExternalOperationReservation(
                            existingExternalReservation.appHostPath,
                            existingExternalReservation.reservationId);
                    }
                    else {
                        this._launchReservation.releaseExternalLaunchReservation(
                            existingExternalReservation.appHostPath,
                            existingExternalReservation.reservationId);
                    }
                }
                return undefined;
            }

            config.program = resolvedProgram;
        }

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
        const resolvedCliPath = await this.reresolveCliPathForSubstitutedProgram(config);
        if (resolvedCliPath !== undefined) {
            aspireConfig.resolvedCliPath = resolvedCliPath;
        }
        else if (!launchedByExtension) {
            delete aspireConfig.resolvedCliPath;
        }
        if (launchedByExtension) {
            markAspireDebugConfigurationAsExtensionOwned(config);
        }
        else if (existingExternalReservation) {
            markAspireDebugConfigurationWithExternalLaunchReservation(
                config,
                existingExternalReservation.reservationId,
                existingExternalReservation.appHostPath,
                existingExternalReservation.isDirectoryScope,
                existingExternalReservation.kind);
        }
        if (existingExternalReservation) {
            configRecord[appHostLaunchReservationIdConfigKey] = existingExternalReservation.reservationId;
        }
        else if (!launchedByExtension) {
            delete configRecord[appHostLaunchReservationIdConfigKey];
        }
        delete aspireConfig.skipCliAvailabilityCheck;
        delete configRecord.launchedByExtension;

        if (typeof config.program === 'string') {
            const program = config.program;
            const isWorkspaceFolderLaunch = this.isWorkspaceFolderRoot(program, folder);
            const resolvedDebugTarget = await this.resolveDebugTarget(program, folder, aspireConfig);
            if (resolvedDebugTarget === undefined) {
                if (existingExternalReservation) {
                    if (existingExternalReservation.kind === 'operation') {
                        this._launchReservation.releaseExternalOperationReservation(
                            existingExternalReservation.appHostPath,
                            existingExternalReservation.reservationId);
                    }
                    else {
                        this._launchReservation.releaseExternalLaunchReservation(
                            existingExternalReservation.appHostPath,
                            existingExternalReservation.reservationId);
                    }
                }
                return undefined;
            }
            config.program = resolvedDebugTarget;

            const telemetryTarget = await this.tryFindWorkspaceDefaultCandidate(program, folder);
            if (telemetryTarget) {
                config[appHostTelemetryTargetPathConfigKey] = telemetryTarget.path;
            }
            else {
                delete config[appHostTelemetryTargetPathConfigKey];
            }

            const command = getAspireDebugConfigurationCommand(aspireConfig);
            const launchTargetPath = telemetryTarget?.path ?? (typeof config.program === 'string' ? config.program : undefined);
            if (!launchedByExtension && command === 'run' && launchTargetPath) {
                const cliPath = aspireConfig.resolvedCliPath ?? await this.validateAndTrustCliPath(
                    config,
                    getCliPathTargetForUri(vscode.Uri.file(launchTargetPath)));
                if (!cliPath) {
                    return undefined;
                }

                const cancellationToken = token ?? {
                    isCancellationRequested: false,
                    onCancellationRequested: () => ({ dispose: () => { } }),
                } as vscode.CancellationToken;
                let prepared: Awaited<ReturnType<ExternalLaunchReservation['prepareLaunchArguments']>>;
                try {
                    prepared = await this._launchReservation.prepareLaunchArguments(
                        launchTargetPath,
                        command,
                        Array.isArray(config.args) ? [...config.args] : undefined,
                        cancellationToken,
                        cliPath,
                        getCliPathTargetForUri(vscode.Uri.file(launchTargetPath)));
                }
                catch (error) {
                    if (existingExternalReservation) {
                        if (existingExternalReservation.kind === 'operation') {
                            this._launchReservation.releaseExternalOperationReservation(
                                existingExternalReservation.appHostPath,
                                existingExternalReservation.reservationId);
                        }
                        else {
                            this._launchReservation.releaseExternalLaunchReservation(
                                existingExternalReservation.appHostPath,
                                existingExternalReservation.reservationId);
                        }
                    }
                    throw error;
                }
                if (prepared.args === undefined) {
                    delete config.args;
                }
                else {
                    config.args = prepared.args;
                }
            }

            // This is the last hook before VS Code creates the session, and it is the only
            // point a `launch.json`/F5 launch shares with the tool-driven path, which goes
            // through `AppHostLaunchService`. Run uses the launching reservation, while
            // deploy/publish/do use separate durable-operation ownership so they remain
            // exclusive with each other without blocking an independent Run.
            //
            // The concrete candidate is claimed in preference to `config.program`: the
            // default `${workspaceFolder}` configuration deliberately leaves `program` as
            // the directory, and a directory is not the same identity as the AppHost inside
            // it, so claiming the directory would leave the tool free to start a duplicate.
            if (!launchedByExtension && command !== undefined) {
                const claimedPath = launchTargetPath;
                if (!claimedPath) {
                    return config;
                }

                const isDirectoryScope = telemetryTarget === undefined && isWorkspaceFolderLaunch;
                let reservationPath = claimedPath;
                let reservationId: string | false;
                if (command === 'run') {
                    if (!existingExternalReservation) {
                        reservationId = this._launchReservation.tryReserveExternalLaunch(claimedPath, isDirectoryScope);
                    }
                    else if (existingExternalReservation.kind === 'run' &&
                        existingExternalReservation.isDirectoryScope === isDirectoryScope &&
                        compareAppHostIdentity(existingExternalReservation.appHostPath, claimedPath) === 'same') {
                        reservationId = this._launchReservation.validateOrReacquireExternalLaunchReservation(
                            existingExternalReservation.appHostPath,
                            existingExternalReservation.reservationId,
                            isDirectoryScope);
                        // Keep the path where the reservation was actually stored. The identity
                        // can become ambiguous on a later resolver pass if sibling files appear.
                        reservationPath = existingExternalReservation.appHostPath;
                    }
                    else {
                        if (existingExternalReservation.kind === 'operation') {
                            this._launchReservation.releaseExternalOperationReservation(
                                existingExternalReservation.appHostPath,
                                existingExternalReservation.reservationId);
                            reservationId = this._launchReservation.tryReserveExternalLaunch(claimedPath, isDirectoryScope);
                        }
                        else {
                            reservationId = this._launchReservation.replaceExternalLaunchReservation(
                                existingExternalReservation.appHostPath,
                                existingExternalReservation.reservationId,
                                claimedPath,
                                isDirectoryScope);
                        }
                    }
                }
                else {
                    const noDebug = config.noDebug === true;
                    const doStep = typeof config.step === 'string' ? config.step : undefined;
                    if (!existingExternalReservation) {
                        reservationId = this._launchReservation.tryReserveExternalOperation(
                            claimedPath,
                            command,
                            noDebug,
                            doStep,
                            isDirectoryScope);
                    }
                    else if (existingExternalReservation.kind === 'operation' &&
                        existingExternalReservation.isDirectoryScope === isDirectoryScope &&
                        compareAppHostIdentity(existingExternalReservation.appHostPath, claimedPath) === 'same') {
                        reservationId = this._launchReservation.validateOrReacquireExternalOperationReservation(
                            existingExternalReservation.appHostPath,
                            existingExternalReservation.reservationId,
                            command,
                            noDebug,
                            doStep,
                            isDirectoryScope);
                        reservationPath = existingExternalReservation.appHostPath;
                    }
                    else {
                        if (existingExternalReservation.kind === 'run') {
                            this._launchReservation.releaseExternalLaunchReservation(
                                existingExternalReservation.appHostPath,
                                existingExternalReservation.reservationId);
                            reservationId = this._launchReservation.tryReserveExternalOperation(
                                claimedPath,
                                command,
                                noDebug,
                                doStep,
                                isDirectoryScope);
                        }
                        else {
                            reservationId = this._launchReservation.replaceExternalOperationReservation(
                                existingExternalReservation.appHostPath,
                                existingExternalReservation.reservationId,
                                claimedPath,
                                command,
                                noDebug,
                                doStep,
                                isDirectoryScope);
                        }
                    }
                }

                if (!reservationId) {
                    // Another launch or operation already owns this AppHost. Abort this session
                    // rather than starting overlapping work against the same project.
                    // A directory-scoped reservation does not identify which AppHost the user
                    // intended to launch, so it cannot safely be attributed in the journal.
                    if (isConcreteAppHostTarget(claimedPath)) {
                        recordLaunchFailureForAppHostPath(claimedPath, {
                            stage: 'validation',
                            category: 'invalidConfiguration',
                            controller: 'editor',
                            mode: getLaunchFailureMode(aspireConfig),
                            providerKind: getLaunchFailureProviderKindForAppHostPath(claimedPath),
                        });
                    }
                    void vscode.window.showInformationMessage(
                        command === 'run'
                            ? appHostLifecycleLaunchAlreadyClaimed
                            : appHostOperationAlreadyInProgress);
                    return undefined;
                }

                config[appHostLaunchReservationIdConfigKey] = reservationId;
                markAspireDebugConfigurationWithExternalLaunchReservation(
                    config,
                    reservationId,
                    reservationPath,
                    isDirectoryScope,
                    command === 'run' ? 'run' : 'operation');
            }
        }

        return config;
    }

    private async resolveDefaultDiscoveryTarget(
        config: AspireExtendedDebugConfiguration,
        program: string,
        folder: vscode.WorkspaceFolder | undefined,
        token?: vscode.CancellationToken): Promise<string | undefined> {
        if (config[appHostSelectionOriginConfigKey] !== 'default-discovery' || !this.isWorkspaceFolderRoot(program, folder)) {
            return program;
        }

        const workspaceFolder = folder ?? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(program));
        if (!workspaceFolder) {
            return program;
        }

        let candidates: CandidateAppHostDisplayInfo[];
        try {
            candidates = await this._appHostDiscoveryService.discover(workspaceFolder, false, token);
        }
        catch (error) {
            if (token?.isCancellationRequested) {
                return undefined;
            }

            extensionLogOutputChannel.warn(`Failed to discover AppHost candidates for directory launch ${program}: ${error}`);
            return program;
        }

        const buildableCandidates = candidates.filter(candidate => candidate.status === 'buildable');
        if (buildableCandidates.length <= 1 || findWorkspaceDefaultCandidate(candidates)) {
            return program;
        }

        const items = sortCandidatesByPath(buildableCandidates).map(candidate => ({
            label: path.relative(workspaceFolder.uri.fsPath, candidate.path),
            description: candidate.language ? formatAppHostLanguage(candidate.language) : undefined,
            detail: candidate.path,
            appHostPath: candidate.path,
        }));
        const selected = await vscode.window.showQuickPick(items, {
            placeHolder: selectAppHostToLaunch,
            canPickMany: false,
            ignoreFocusOut: true,
        }, token);
        if (!selected) {
            return undefined;
        }

        config[appHostSelectionOriginConfigKey] = 'user-selection';
        return selected.appHostPath;
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

    private async resolveDebugTarget(filePath: string, folder: vscode.WorkspaceFolder | undefined, config: AspireExtendedDebugConfiguration): Promise<string | undefined> {
        try {
            return await this._appHostDiscoveryService.resolveDebugTarget(filePath, folder);
        }
        catch (error) {
            const exactTarget = isConcreteAppHostTarget(filePath);
            const terminalFailure = isCommandCancellation(error) ||
                (exactTarget && !await doesFileExist(filePath));
            if (terminalFailure &&
                exactTarget &&
                tryMarkAspireDebugConfigurationDiscoveryFailureRecorded(config)) {
                recordLaunchFailureForAppHostPath(filePath, {
                    stage: 'discovery',
                    controller: 'editor',
                    mode: getLaunchFailureMode(config),
                    providerKind: getLaunchFailureProviderKindForAppHostPath(filePath),
                    error,
                });
                this._launchReservation.markLaunchAttemptFailureRecorded(config);
            }
            extensionLogOutputChannel.warn(`Failed to resolve AppHost debug target ${filePath}: ${error}`);
            return terminalFailure ? undefined : filePath;
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

    private createDefaultConfigurations(folder: vscode.WorkspaceFolder): Promise<vscode.DebugConfiguration[]> {
        return this.createProvidedConfigurations(folder, folder.uri.fsPath);
    }

    private async createProvidedConfigurations(folder: vscode.WorkspaceFolder, program: string): Promise<vscode.DebugConfiguration[]> {
        const isDynamic = this._triggerKind === vscode.DebugConfigurationProviderTriggerKind.Dynamic;
        const config: vscode.DebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: isDynamic
                ? await this.getDynamicConfigurationName(folder)
                : defaultConfigurationName,
            program
        };

        if (!isDynamic) {
            return [config];
        }

        return [{ ...config, [appHostSelectionOriginConfigKey]: 'default-discovery' }];
    }

    private async getDynamicConfigurationName(folder: vscode.WorkspaceFolder): Promise<string> {
        let ownerUri = this._legacyDynamicConfigurationOwnerUri
            ?? this._workspaceState.get<string>(legacyDynamicConfigurationOwnerWorkspaceStateKey);
        if (ownerUri === undefined) {
            // Keep the first folder's shipped configuration name across workspace changes. VS Code
            // does not hide dynamic configurations through `presentation.hidden`, so ownership must
            // be persisted instead of returning a compatibility alias that becomes a duplicate.
            ownerUri = vscode.workspace.workspaceFolders?.[0]?.uri.toString() ?? folder.uri.toString();
            this._legacyDynamicConfigurationOwnerUri = ownerUri;
            await this._workspaceState.update(legacyDynamicConfigurationOwnerWorkspaceStateKey, ownerUri);
        }
        else {
            this._legacyDynamicConfigurationOwnerUri = ownerUri;
        }

        return ownerUri === folder.uri.toString()
            ? defaultConfigurationName
            : defaultConfigurationNameForWorkspaceFolder(folder.name, folder.uri.toString());
    }

    /**
     * Returns the CLI path the session should launch with, re-resolving when variable substitution
     * revealed that the program belongs to a workspace folder other than the one the availability gate
     * used.
     *
     * `resolveDebugConfiguration` runs before VS Code substitutes variables, so a `program` such as
     * `${workspaceFolder:other}/AppHost.java`, or a relative one, is still opaque there and the gate
     * can only fall back to the initiating folder. Without this the target folder's AppHost would be
     * launched with the initiating folder's `aspire.cliPath` — in a multi-root workspace that is
     * frequently a different CLI build entirely, and the mismatch is silent.
     */
    private async reresolveCliPathForSubstitutedProgram(config: vscode.DebugConfiguration): Promise<string | undefined> {
        const gatedCliPath = getAspireDebugConfigurationResolvedCliPath(config);
        if (gatedCliPath === undefined) {
            // No gate ran (an extension-owned launch, or the check was skipped), so there is nothing
            // to correct.
            return undefined;
        }

        const program = typeof config.program === 'string' ? config.program : undefined;
        if (!program || !path.isAbsolute(program) || program.includes('${')) {
            return gatedCliPath;
        }

        const programFolder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(program));
        if (!programFolder) {
            return gatedCliPath;
        }

        const target = workspaceFolderCliPathTarget(programFolder);
        if (getCliPathTargetKey(target) === getAspireDebugConfigurationResolvedCliPathScope(config)) {
            // Same scope the gate already used, so re-resolving would only repeat work and risk a
            // second availability prompt.
            return gatedCliPath;
        }

        const result = await checkCliAvailableOrRedirect('debug_gate', target);
        if (!result.available) {
            // Keep the gated path rather than cancelling: the gate already proved a usable CLI, and the
            // session is better served by that one than by failing outright.
            return gatedCliPath;
        }

        markAspireDebugConfigurationWithResolvedCliPath(config, result.cliPath);
        markAspireDebugConfigurationWithResolvedCliPathScope(config, getCliPathTargetKey(target));
        return result.cliPath;
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

    private async validateAndTrustCliPath(config: vscode.DebugConfiguration, target: CliPathResolutionTarget): Promise<string | undefined> {
        const result = await checkCliAvailableOrRedirect(
            'debug_gate',
            target,
            { pinnedCliPath: getAspireDebugConfigurationResolvedCliPath(config) });
        if (!result.available) {
            return undefined;
        }

        (config as AspireExtendedDebugConfiguration).resolvedCliPath = result.cliPath;
        markAspireDebugConfigurationWithResolvedCliPath(config, result.cliPath);
        markAspireDebugConfigurationWithResolvedCliPathScope(config, getCliPathTargetKey(target));
        return result.cliPath;
    }
}

function getLaunchFailureMode(config: AspireExtendedDebugConfiguration): LaunchFailureMode {
    const command = getAspireDebugConfigurationCommand(config);
    if (command === 'deploy' || command === 'publish') {
        return command;
    }
    if (command === 'run') {
        return config.noDebug === true ? 'run' : 'debug';
    }

    return 'other';
}

function isConcreteAppHostTarget(appHostPath: string): boolean {
    if (isAppHostProjectFile(appHostPath) || isAppHostSourceFile(appHostPath)) {
        return true;
    }

    return classifyAppHostPath(appHostPath) !== 'unknown';
}
