import * as vscode from 'vscode';
import { appHostCandidateDescription, cliNotAvailable, cliFoundAtDefaultPath, dismissLabel, dontShowAgainLabel, doYouWantToSetDefaultApphost, noLabel, noWorkspaceOpen, openCliInstallInstructions, selectDefaultLaunchApphost, yesLabel } from '../loc/strings';
import path from 'path';
import { AspireConfigFile, aspireConfigFileName, getAppHostPathFromConfig, readJsonFile } from './cliTypes';
import { extensionLogOutputChannel } from './logging';
import { resolveCliPath } from './cliPath';
import { AppHostDiscoveryService, AppHostProjectSearchResult, formatAppHostLanguage, getWorkspaceAppHostProjectSearchResult } from './appHostDiscovery';
import type { AppHostCandidate } from './appHostDiscovery';
import { getCommonExcludeGlob } from './workspaceFileSearch';

export { getCommonExcludeGlob } from './workspaceFileSearch';

/**
 * Searches for Aspire configuration files in the workspace, excluding common build output
 * and dependency directories. Searches for both the new aspire.config.json and the legacy
 * .aspire/settings.json formats. Prefers aspire.config.json when both exist in the same project.
 * @returns An array of URIs pointing to found configuration files
 */
export async function findAspireSettingsFiles(): Promise<vscode.Uri[]> {
    const excludePattern = getCommonExcludeGlob();

    // Search for both new and legacy config files in parallel
    const [newConfigFiles, legacySettingsFiles] = await Promise.all([
        vscode.workspace.findFiles(`**/${aspireConfigFileName}`, excludePattern),
        vscode.workspace.findFiles('**/.aspire/settings.json', excludePattern),
    ]);

    // Build a set of directories that already have an aspire.config.json
    // so we can filter out legacy files that are superseded by the new format
    const newConfigDirs = new Set(newConfigFiles.map(uri => path.dirname(uri.fsPath)));

    const filteredLegacyFiles = legacySettingsFiles.filter(legacyUri => {
        // Legacy file is at <project>/.aspire/settings.json; the project root is <project>
        const projectRoot = path.dirname(path.dirname(legacyUri.fsPath));
        return !newConfigDirs.has(projectRoot);
    });

    return [...newConfigFiles, ...filteredLegacyFiles];
}

export function isWorkspaceOpen(showErrorMessage: boolean = true): boolean {
    const isOpen = !!vscode.workspace.workspaceFolders && vscode.workspace.workspaceFolders.length > 0;
    if (!isOpen && showErrorMessage) {
        vscode.window.showErrorMessage(noWorkspaceOpen);
    }

    return isOpen;
}

export function isFolderOpenInWorkspace(folderPath: string): boolean {
    const uri = vscode.Uri.file(folderPath);
    return !!vscode.workspace.getWorkspaceFolder(uri);
}

export function getRelativePathToWorkspace(filePath: string): string {
    if (!isWorkspaceOpen(false)) {
        return filePath;
    }

    const uri = vscode.Uri.file(filePath);
    const workspaceFolder = vscode.workspace.getWorkspaceFolder(uri);

    if (workspaceFolder) {
        const relativePath = vscode.workspace.asRelativePath(uri);
        return relativePath;
    }

    return filePath;
}

interface AppHostQuickPickItem extends vscode.QuickPickItem {
    appHostPath: string;
}

export function isBuildableAppHostCandidate(candidate: AppHostCandidate): boolean {
    return candidate.status === 'buildable';
}

function createAppHostQuickPickItems(result: AppHostProjectSearchResult, rootFolder: vscode.WorkspaceFolder): AppHostQuickPickItem[] {
    const candidates = result.app_host_candidates.length > 0
        ? result.app_host_candidates
        : result.all_project_file_candidates.map(appHostPath => ({
            relativePath: path.relative(rootFolder.uri.fsPath, appHostPath),
            path: appHostPath,
            language: '',
            status: 'buildable',
        }));

    return candidates.map(candidate => {
        const language = candidate.language ? formatAppHostLanguage(candidate.language) : undefined;
        const status = candidate.status || undefined;
        return {
            label: candidate.relativePath || path.relative(rootFolder.uri.fsPath, candidate.path),
            description: language && status ? appHostCandidateDescription(language, status) : status,
            detail: candidate.path,
            appHostPath: candidate.path,
        };
    });
}

export async function checkForExistingAppHostPathInWorkspace(appHostDiscoveryService: AppHostDiscoveryService, getEnableSettingsFileCreationPromptOnStartup: () => boolean, setEnableSettingsFileCreationPromptOnStartup: (value: boolean) => Promise<void>): Promise<vscode.Disposable | null> {
    extensionLogOutputChannel.info('Checking for existing AppHost path in workspace');

    const enabled = getEnableSettingsFileCreationPromptOnStartup();
    if (!enabled) {
        extensionLogOutputChannel.info('AppHost path prompt is disabled in settings, skipping check');
        return null;
    }

    if (!isWorkspaceOpen(false)) {
        extensionLogOutputChannel.info('No workspace open, skipping AppHost path check');
        return null;
    }

    const activeUri = vscode.window.activeTextEditor?.document.uri;
    const folder = activeUri && vscode.workspace.getWorkspaceFolder(activeUri);
    const rootFolder = folder ?? vscode.workspace.workspaceFolders?.[0];

    if (!rootFolder) {
        extensionLogOutputChannel.warn('No workspace folder found, skipping AppHost path check');
        return null;
    }

    extensionLogOutputChannel.info(`Checking AppHost settings in workspace: ${rootFolder.name}`);

    // Search for config files (both new and legacy formats)
    const settingsFiles = await findAspireSettingsFiles();
    const settingsFileExists = settingsFiles.length > 0;

    if (settingsFileExists) {
        extensionLogOutputChannel.info(`Found existing Aspire settings file at: ${settingsFiles.map(f => f.fsPath).join(', ')}`);
        for (const file of settingsFiles) {
            const settings = await readJsonFile(file);
            const appHostPath = getAppHostPathFromConfig(settings);
            if (appHostPath) {
                extensionLogOutputChannel.info(`AppHost path already configured in file ${file.fsPath}: ${appHostPath}`);
                return null;
            }
        }

        extensionLogOutputChannel.info('Settings file(s) exist but no AppHost path is set');
        if (settingsFiles.length > 1) {
            // Multiple settings files exist, so don't prompt
            extensionLogOutputChannel.warn(`Multiple Aspire settings files found (${settingsFiles.length}). Not prompting to choose between them.`);
            return null;
        }
    }
    else {
        extensionLogOutputChannel.info('No Aspire settings file found, will create if AppHost is selected');
        // Default to creating new-format aspire.config.json in the workspace root
        settingsFiles.push(vscode.Uri.file(path.join(rootFolder.uri.fsPath, aspireConfigFileName)));
    }

    const settingsFile = settingsFiles[0];
    extensionLogOutputChannel.info('Searching for AppHost projects using shared AppHost discovery');

    appHostDiscoveryService.discover(rootFolder, true)
        .then(appHosts => getWorkspaceAppHostProjectSearchResult(rootFolder, appHosts))
        .then(result => promptToAddAppHostPathToSettingsFile(result, settingsFileExists, settingsFile, rootFolder, setEnableSettingsFileCreationPromptOnStartup))
        .catch(error => {
            extensionLogOutputChannel.error(`Failed to retrieve AppHost projects: ${error}`);
        });

    return null;
}

async function promptToAddAppHostPathToSettingsFile(result: AppHostProjectSearchResult, settingsFileExists: boolean, settingsFileLocation: vscode.Uri, rootFolder: vscode.WorkspaceFolder, setEnableSettingsFileCreationPromptOnStartup: (value: boolean) => Promise<void>): Promise<void> {
    if (!result.selected_project_file && result.all_project_file_candidates.length === 0 && result.app_host_candidates.length === 0) {
        extensionLogOutputChannel.info('No AppHost projects found in workspace');
        return;
    }

    extensionLogOutputChannel.info('Prompting user to set default AppHost path');
    const shouldSetApphostResponse = await vscode.window.showInformationMessage(!result.selected_project_file ? selectDefaultLaunchApphost : doYouWantToSetDefaultApphost(vscode.workspace.asRelativePath(result.selected_project_file)), yesLabel, noLabel, dontShowAgainLabel);

    if (shouldSetApphostResponse !== yesLabel) {
        if (shouldSetApphostResponse === dontShowAgainLabel) {
            extensionLogOutputChannel.info('User selected "Don\'t show again", disabling startup prompt');
            await setEnableSettingsFileCreationPromptOnStartup(false);
        } else {
            extensionLogOutputChannel.info('User declined to set AppHost path');
        }

        return;
    }

    extensionLogOutputChannel.info('User accepted to set AppHost path');

    let appHostToUse: string | null = result.selected_project_file;
    if (!appHostToUse) {
        const appHostItems = createAppHostQuickPickItems(result, rootFolder);
        extensionLogOutputChannel.info(`Showing quick pick with ${appHostItems.length} AppHost candidates`);
        const selected = await vscode.window.showQuickPick(appHostItems, {
            placeHolder: selectDefaultLaunchApphost,
            canPickMany: false,
            ignoreFocusOut: true
        }) ?? null;

        appHostToUse = selected?.appHostPath ?? null;

        if (selected) {
            extensionLogOutputChannel.info(`User selected AppHost: ${selected.appHostPath}`);
        } else {
            extensionLogOutputChannel.info('User cancelled AppHost selection');
        }
    }

    if (!appHostToUse) {
        return;
    }

    // make appHostToUse relative to the settings file location directory
    const settingsDir = path.dirname(settingsFileLocation.fsPath);
    appHostToUse = path.relative(settingsDir, appHostToUse);

    const isNewFormat = settingsFileLocation.fsPath.endsWith(aspireConfigFileName);

    if (isNewFormat) {
        // Write in new aspire.config.json format
        let configFile: AspireConfigFile = {};
        if (settingsFileExists) {
            extensionLogOutputChannel.info('Updating existing aspire.config.json');
            configFile = await readJsonFile(settingsFileLocation);
        } else {
            extensionLogOutputChannel.info('Creating new aspire.config.json');
        }

        configFile.appHost = { ...configFile.appHost, path: appHostToUse };

        const updatedContent = Buffer.from(JSON.stringify(configFile, null, 4), 'utf8');
        await vscode.workspace.fs.writeFile(settingsFileLocation, updatedContent);
    } else {
        // Write in legacy .aspire/settings.json format
        let legacySettings: any = {};
        if (settingsFileExists) {
            extensionLogOutputChannel.info('Updating existing Aspire settings file');
            legacySettings = await readJsonFile(settingsFileLocation);
        } else {
            extensionLogOutputChannel.info('Creating new Aspire settings file');
        }

        legacySettings.appHostPath = appHostToUse;

        const updatedContent = Buffer.from(JSON.stringify(legacySettings, null, 4), 'utf8');
        await vscode.workspace.fs.writeFile(settingsFileLocation, updatedContent);
    }

    extensionLogOutputChannel.info(`Successfully set AppHost path to: ${appHostToUse} in ${settingsFileLocation.fsPath}`);
}

/**
 * Checks if the Aspire CLI is available. If not found on PATH, it checks the default
 * installation directory and updates the VS Code setting accordingly.
 *
 * If not available, shows a message prompting to open Aspire CLI installation steps.
 * @returns An object containing the CLI path to use and whether CLI is available
 */
export async function checkCliAvailableOrRedirect(): Promise<{ cliPath: string; available: boolean }> {
    // Resolve CLI path fresh each time — settings or PATH may have changed
    const result = await resolveCliPath();

    if (result.available) {
        // Show informational message if CLI was found at default path (not on PATH)
        if (result.source === 'default-install') {
            extensionLogOutputChannel.info(`Using Aspire CLI from default install location: ${result.cliPath}`);
            vscode.window.showInformationMessage(cliFoundAtDefaultPath(result.cliPath));
        }

        return { cliPath: result.cliPath, available: true };
    }

    // CLI not found - show error message with install instructions
    vscode.window.showErrorMessage(
        cliNotAvailable,
        openCliInstallInstructions,
        dismissLabel
    ).then(selection => {
        if (selection === openCliInstallInstructions) {
            // Go to Aspire CLI installation instruction page in external browser
            vscode.env.openExternal(vscode.Uri.parse('https://aspire.dev/get-started/install-cli/'));
        }
    });

    return { cliPath: result.cliPath, available: false };
}
