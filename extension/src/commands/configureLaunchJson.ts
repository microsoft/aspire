import * as vscode from 'vscode';
import * as path from 'path';
import { parse as parseJsonc, type ParseError } from 'jsonc-parser';
import { aspireConfigExists, failedToConfigureLaunchJson, defaultConfigurationName, selectAppHostToLaunch, selectDashboardLaunchBehavior, dashboardLaunchNoneLabel, dashboardLaunchNoneDescription, dashboardLaunchNotificationLabel, dashboardLaunchNotificationDescription, dashboardLaunchExternalBrowserLabel, dashboardLaunchExternalBrowserDescription, dashboardLaunchIntegratedBrowserLabel, dashboardLaunchIntegratedBrowserDescription, dashboardLaunchChromeLabel, dashboardLaunchChromeDescription, dashboardLaunchEdgeLabel, dashboardLaunchEdgeDescription, dashboardLaunchFirefoxLabel, dashboardLaunchFirefoxDescription } from '../loc/strings';
import type { DashboardLaunchBehavior } from '../debugger/AspireDebugSession';
import { type AppHostDiscoveryService, type CandidateAppHostDisplayInfo, formatAppHostLanguage, isBuildableAppHostCandidate, selectWorkspaceAppHostPath } from '../utils/appHostDiscovery';
import { sortCandidatesByPath } from '../utils/appHostCandidateSelection';
import { extensionLogOutputChannel } from '../utils/logging';

type DashboardLaunchBehaviorQuickPickItem = vscode.QuickPickItem & {
    value: DashboardLaunchBehavior;
};

type LaunchJson = {
    configurations?: unknown[];
    [key: string]: unknown;
};

type LaunchTarget = {
    workspaceFolder: vscode.WorkspaceFolder;
    appHostPath?: string;
};

export async function configureLaunchJsonCommand(appHostDiscoveryService: AppHostDiscoveryService) {
    try {
        const { workspaceFolder, appHostPath } = await selectLaunchTarget(appHostDiscoveryService);
        const launchJsonPath = path.join(workspaceFolder.uri.fsPath, '.vscode', 'launch.json');
        const vscodeDir = path.join(workspaceFolder.uri.fsPath, '.vscode');
        const vscodeUri = vscode.Uri.file(vscodeDir);
        const launchUri = vscode.Uri.file(launchJsonPath);
        let launchConfig: LaunchJson = {
            version: '0.2.0',
            configurations: []
        };

        // Check if launch.json already exists
        try {
            const existingContent = await vscode.workspace.fs.readFile(launchUri);
            const existingText = Buffer.from(existingContent).toString('utf8');
            const parseErrors: ParseError[] = [];
            const parsedLaunchConfig = parseJsonc(existingText, parseErrors) as unknown;
            if (parseErrors.length > 0) {
                throw new Error('launch.json contains invalid JSON.');
            }

            if (!isLaunchJson(parsedLaunchConfig)) {
                throw new Error('launch.json root must be an object.');
            }

            launchConfig = parsedLaunchConfig;

            // Check if Aspire configuration already exists
            const hasAspireConfig = launchConfig.configurations?.some(isAspireLaunchConfiguration);

            if (hasAspireConfig) {
                vscode.window.showInformationMessage(aspireConfigExists);
                vscode.window.showTextDocument(await vscode.workspace.openTextDocument(launchUri));
                return;
            }
        } catch {
            // File doesn't exist or is invalid JSON, we'll create/overwrite it
        }

        const dashboardBrowser = await promptForDashboardLaunchBehavior();
        if (!dashboardBrowser) {
            throw new vscode.CancellationError();
        }

        const defaultConfig = {
            type: 'aspire',
            request: 'launch',
            name: defaultConfigurationName,
            program: getProgram(workspaceFolder, appHostPath),
            dashboardBrowser
        };

        // Check if .vscode directory exists, create if not
        try {
            await vscode.workspace.fs.stat(vscodeUri);
        } catch {
            // Directory doesn't exist, create it
            await vscode.workspace.fs.createDirectory(vscodeUri);
        }

        // Ensure configurations array exists
        if (!launchConfig.configurations) {
            launchConfig.configurations = [];
        }

        // Add the Aspire configuration
        launchConfig.configurations.push(defaultConfig);

        // Write the updated launch.json
        const updatedContent = JSON.stringify(launchConfig, null, 2);
        await vscode.workspace.fs.writeFile(launchUri, Buffer.from(updatedContent, 'utf8'));

        const document = await vscode.workspace.openTextDocument(launchUri);
        await vscode.window.showTextDocument(document);


    } catch (error) {
        if (error instanceof vscode.CancellationError) {
            throw error;
        }

        vscode.window.showErrorMessage(failedToConfigureLaunchJson(error));
    }
}

/**
 * Resolves the AppHost to launch: the active editor's AppHost, then the single or default
 * AppHost of the active (or only AppHost-containing) folder, otherwise a prompt. Without any
 * discovered AppHost, falls back to the first workspace folder.
 */
async function selectLaunchTarget(appHostDiscoveryService: AppHostDiscoveryService): Promise<LaunchTarget> {
    const folders = vscode.workspace.workspaceFolders ?? [];
    const activeUri = vscode.window.activeTextEditor?.document.uri;
    const activeFolder = activeUri ? vscode.workspace.getWorkspaceFolder(activeUri) : undefined;
    if (activeUri && activeFolder) {
        const candidate = await appHostDiscoveryService.tryFindCandidateForEditorFile(activeUri.fsPath, activeFolder).catch(() => undefined);
        if (candidate) {
            return toLaunchTarget(activeFolder, candidate.path);
        }
    }

    const discovered = await Promise.all(folders.map(async workspaceFolder => {
        let candidates: CandidateAppHostDisplayInfo[] = [];
        try {
            candidates = await appHostDiscoveryService.discover(workspaceFolder);
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to discover AppHost candidates for workspace ${workspaceFolder.uri.fsPath}: ${error}`);
        }
        return { workspaceFolder, candidates, buildable: sortCandidatesByPath(candidates.filter(isBuildableAppHostCandidate)) };
    }));
    const withAppHosts = discovered.filter(entry => entry.buildable.length > 0);
    const activeEntry = withAppHosts.find(entry => entry.workspaceFolder.uri.toString() === activeFolder?.uri.toString());
    const pool = activeEntry ? [activeEntry] : withAppHosts;
    if (pool.length === 0) {
        return { workspaceFolder: folders[0] };
    }

    if (pool.length === 1) {
        const selectedPath = await selectWorkspaceAppHostPath(pool[0].workspaceFolder, pool[0].candidates);
        if (selectedPath) {
            return toLaunchTarget(pool[0].workspaceFolder, selectedPath);
        }
    }

    const options = pool.flatMap(entry => entry.buildable.map(candidate => ({ workspaceFolder: entry.workspaceFolder, candidate })));
    if (options.length === 1) {
        return toLaunchTarget(options[0].workspaceFolder, options[0].candidate.path);
    }

    const selected = await vscode.window.showQuickPick(options.map(option => ({
        label: path.relative(option.workspaceFolder.uri.fsPath, option.candidate.path),
        description: [folders.length > 1 ? option.workspaceFolder.name : undefined, option.candidate.language ? formatAppHostLanguage(option.candidate.language) : undefined].filter(Boolean).join(' · ') || undefined,
        detail: option.candidate.path,
        option,
    })), {
        placeHolder: selectAppHostToLaunch,
        ignoreFocusOut: true,
    });
    if (!selected) {
        throw new vscode.CancellationError();
    }

    return toLaunchTarget(selected.option.workspaceFolder, selected.option.candidate.path);
}

function toLaunchTarget(discoveredIn: vscode.WorkspaceFolder, appHostPath: string): LaunchTarget {
    // Nested workspace folders: write launch.json into the innermost folder containing the AppHost.
    return { workspaceFolder: vscode.workspace.getWorkspaceFolder(vscode.Uri.file(appHostPath)) ?? discoveredIn, appHostPath };
}

function getProgram(workspaceFolder: vscode.WorkspaceFolder, appHostPath: string | undefined): string {
    if (!appHostPath) {
        return '${workspaceFolder}';
    }

    const relativePath = path.relative(workspaceFolder.uri.fsPath, appHostPath);
    return relativePath.startsWith('..') || path.isAbsolute(relativePath)
        ? appHostPath
        : `\${workspaceFolder}/${relativePath.split(path.sep).join('/')}`;
}

async function promptForDashboardLaunchBehavior(): Promise<DashboardLaunchBehavior | undefined> {
    const items: DashboardLaunchBehaviorQuickPickItem[] = [
        {
            label: dashboardLaunchNoneLabel,
            description: dashboardLaunchNoneDescription,
            value: 'none',
        },
        {
            label: dashboardLaunchNotificationLabel,
            description: dashboardLaunchNotificationDescription,
            value: 'notification',
        },
        {
            label: dashboardLaunchExternalBrowserLabel,
            description: dashboardLaunchExternalBrowserDescription,
            value: 'openExternalBrowser',
        },
        {
            label: dashboardLaunchIntegratedBrowserLabel,
            description: dashboardLaunchIntegratedBrowserDescription,
            value: 'integratedBrowser',
        },
        {
            label: dashboardLaunchChromeLabel,
            description: dashboardLaunchChromeDescription,
            value: 'debugChrome',
        },
        {
            label: dashboardLaunchEdgeLabel,
            description: dashboardLaunchEdgeDescription,
            value: 'debugEdge',
        },
        {
            label: dashboardLaunchFirefoxLabel,
            description: dashboardLaunchFirefoxDescription,
            value: 'debugFirefox',
        },
    ];

    const selected = await vscode.window.showQuickPick(items, {
        placeHolder: selectDashboardLaunchBehavior,
    });

    return selected?.value;
}

function isLaunchJson(value: unknown): value is LaunchJson {
    return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isAspireLaunchConfiguration(value: unknown): boolean {
    return typeof value === 'object'
        && value !== null
        && 'type' in value
        && value.type === 'aspire';
}
