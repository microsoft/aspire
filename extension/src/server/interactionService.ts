import { MessageConnection } from 'vscode-jsonrpc';
import * as vscode from 'vscode';
import * as fs from 'fs/promises';
import { getRelativePathToWorkspace, isFolderOpenInWorkspace } from '../utils/workspace';
import { yesLabel, noLabel, directLink, codespacesLink, openAspireDashboard, settingsLabel, failedToShowPromptEmpty, incompatibleAppHostError, aspireHostingSdkVersion, aspireCliVersion, requiredCapability, fieldRequired, aspireDebugSessionNotInitialized, errorMessage, failedToStartDebugSession, dashboard, codespaces, selectDirectoryTitle, selectFileTitle } from '../loc/strings';
import { ICliRpcClient } from './rpcClient';
import { ProgressNotifier } from './progressNotifier';
import { applyTextStyle, formatText } from '../utils/strings';
import { extensionLogOutputChannel } from '../utils/logging';
import { AspireExtendedDebugConfiguration, EnvVar } from '../dcp/types';
import { AnsiColors, AspireTerminal } from '../utils/AspireTerminalProvider';
import { AspireDebugSession, DashboardBrowserType } from '../debugger/AspireDebugSession';
import { isDirectory } from '../utils/io';

export interface IInteractionService {
    showStatus: (statusText: string | null) => void;
    promptForString: (promptText: string, defaultValue: string | null, required: boolean, rpcClient: ICliRpcClient) => Promise<string | null>;
    promptForSecretString: (promptText: string, required: boolean, rpcClient: ICliRpcClient) => Promise<string | null>;
    promptForFilePath: (promptText: string, defaultValue: string | null, directory: boolean) => Promise<string | null>;
    confirm: (promptText: string, defaultValue: boolean) => Promise<boolean | null>;
    promptForSelection: (promptText: string, choices: string[]) => Promise<string | null>;
    promptForSelections: (promptText: string, choices: string[]) => Promise<string[] | null>;
    displayIncompatibleVersionError: (requiredCapability: string, appHostHostingSdkVersion: string, rpcClient: ICliRpcClient) => Promise<void>;
    displayError: (errorMessage: string) => void;
    displayMessage: (emoji: string, message: string) => void;
    displaySuccess: (message: string) => void;
    displaySubtleMessage: (message: string) => void;
    displayEmptyLine: () => void;
    displayDashboardUrls: (dashboardUrls: DashboardUrls) => Promise<void>;
    displayLines: (lines: ConsoleLine[]) => void;
    displayPlainText: (message: string) => void;
    displayCancellationMessage: () => void;
    openEditor: (path: string) => Promise<void>;
    logMessage: (logLevel: CSLogLevel, message: string) => void;
    launchAppHost(projectFile: string, args: string[], environment: EnvVar[], debug: boolean): Promise<void>;
    stopDebugging: () => void;
    closeDashboard: () => void;
    notifyAppHostStartupCompleted: () => void;
    startDebugSession: (workingDirectory: string, projectFile: string | null, debug: boolean, options?: DebugSessionOptions) => Promise<void>;
    writeDebugSessionMessage: (message: string, stdout: boolean, textStyle?: string) => void;
}

type CSLogLevel = 'Trace' | 'Debug' | 'Information' | 'Warn' | 'Error' | 'Critical';

// Support both PascalCase (old) and camelCase (new) for backwards compatibility
// with different versions of the CLI/AppHost.
//
// BREAKING CHANGE: ModelContextProtocol package was updated from an older version to 0.2.0-preview.1
// (and later to 0.4.0-preview.3), which changed the default JSON serialization to use camelCase
// (via JsonNamingPolicy.CamelCase) to comply with the MCP specification.
//
// This type definition supports both formats to maintain compatibility with:
// - Older CLI/AppHost versions that serialize with PascalCase
// - Newer CLI/AppHost versions (with ModelContextProtocol 0.2.0+) that serialize with camelCase
type DashboardUrls = {
    // New camelCase format (ModelContextProtocol 0.2.0+)
    dashboardHealthy?: boolean;
    baseUrlWithLoginToken?: string;
    codespacesUrlWithLoginToken?: string | null;
    // Old PascalCase format (pre-ModelContextProtocol 0.2.0)
    DashboardHealthy?: boolean;
    BaseUrlWithLoginToken?: string;
    CodespacesUrlWithLoginToken?: string | null;
};

// Helper to access DashboardUrls properties in a case-insensitive way.
// Prefers the new camelCase format but falls back to PascalCase for backwards compatibility.
function getDashboardUrlProperty(urls: DashboardUrls, property: 'baseUrl' | 'codespacesUrl'): string {
    if (property === 'baseUrl') {
        // Try camelCase first (new format), then PascalCase (old format)
        return urls.baseUrlWithLoginToken ?? urls.BaseUrlWithLoginToken ?? '';
    } else {
        // Try camelCase first (new format), then PascalCase (old format)
        return urls.codespacesUrlWithLoginToken ?? urls.CodespacesUrlWithLoginToken ?? '';
    }
}

// Support both PascalCase (old) and camelCase (new) for backwards compatibility.
// DisplayLineState is serialized with ModelContextProtocol.McpJsonUtilities.DefaultOptions
// which changed to camelCase in version 0.2.0+
type ConsoleLine = {
    // New camelCase format (ModelContextProtocol 0.2.0+)
    stream?: 'stdout' | 'stderr';
    line?: string;
    // Old PascalCase format (pre-ModelContextProtocol 0.2.0)
    Stream?: 'stdout' | 'stderr';
    Line?: string;
};

// Helper to access ConsoleLine properties in a case-insensitive way.
// Prefers the new camelCase format but falls back to PascalCase for backwards compatibility.
function getConsoleLineText(line: ConsoleLine): string {
    return line.line ?? line.Line ?? '';
}

type DebugSessionOptions = {
    command?: string;
    args?: string[];
};

export class InteractionService implements IInteractionService {
    private _getAspireDebugSession: () => AspireDebugSession | null;
    private _getAspireTerminal?: () => AspireTerminal;

    private _rpcClient?: ICliRpcClient;
    private _progressNotifier: ProgressNotifier;

    constructor(getAspireDebugSession: () => AspireDebugSession | null, rpcClient: ICliRpcClient, getAspireTerminal?: () => AspireTerminal) {
        this._getAspireDebugSession = getAspireDebugSession;
        this._getAspireTerminal = getAspireTerminal;
        this._rpcClient = rpcClient;
        this._progressNotifier = new ProgressNotifier(this._rpcClient);
    }

    showStatus(statusText: string | null) {
        this._progressNotifier.show(statusText);
    }

    async promptForString(promptText: string, defaultValue: string | null, required: boolean, rpcClient: ICliRpcClient): Promise<string | null> {
        if (!promptText) {
            vscode.window.showErrorMessage(failedToShowPromptEmpty);
            extensionLogOutputChannel.error(failedToShowPromptEmpty);
            return null;
        }

        extensionLogOutputChannel.info(`Prompting for string: ${promptText} with default value: ${defaultValue ?? 'null'}`);
        const input = await vscode.window.showInputBox({
            prompt: formatText(promptText),
            value: formatText(defaultValue ?? ''),
            validateInput: async (value: string) => {
                // Check required field validation first
                if (required && (!value || value.trim() === '')) {
                    return fieldRequired;
                }

                // Then check RPC validation
                const validationResult = await rpcClient.validatePromptInputString(value);
                if (validationResult) {
                    return validationResult.Successful ? null : validationResult.Message;
                }

                return null;
            },
            ignoreFocusOut: true
        });

        return input ?? null;
    }

    async promptForSecretString(promptText: string, required: boolean, rpcClient: ICliRpcClient): Promise<string | null> {
        if (!promptText) {
            vscode.window.showErrorMessage(failedToShowPromptEmpty);
            extensionLogOutputChannel.error(failedToShowPromptEmpty);
            return null;
        }

        extensionLogOutputChannel.info(`Prompting for secret string: ${promptText}`);
        const input = await vscode.window.showInputBox({
            prompt: formatText(promptText),
            password: true, // This is the key difference - render as password field
            validateInput: async (value: string) => {
                // Check required field validation first
                if (required && (!value || value.trim() === '')) {
                    return fieldRequired;
                }

                // Then check RPC validation
                const validationResult = await rpcClient.validatePromptInputString(value);
                if (validationResult) {
                    return validationResult.Successful ? null : validationResult.Message;
                }

                return null;
            },
            ignoreFocusOut: true
        });

        return input ?? null;
    }

    async promptForFilePath(promptText: string, defaultValue: string | null, directory: boolean): Promise<string | null> {
        extensionLogOutputChannel.info(`Prompting for file path: ${promptText}, directory: ${directory}, default: ${defaultValue ?? 'null'}`);

        const defaultUri = defaultValue ? vscode.Uri.file(defaultValue) : undefined;
        const openLabel = directory ? selectDirectoryTitle : selectFileTitle;

        const result = await vscode.window.showOpenDialog({
            canSelectFiles: !directory,
            canSelectFolders: directory,
            canSelectMany: false,
            defaultUri,
            openLabel,
            title: formatText(promptText),
        });

        if (!result || result.length === 0) {
            return null;
        }

        return result[0].fsPath;
    }

    async confirm(promptText: string, defaultValue: boolean): Promise<boolean | null> {
        extensionLogOutputChannel.info(`Confirming: ${promptText} with default value: ${defaultValue}`);
        const yes = yesLabel;
        const no = noLabel;

        const choices = [yes, no];

        const result = await vscode.window.showQuickPick(choices, {
            placeHolder: formatText(promptText),
            canPickMany: false,
            ignoreFocusOut: true
        });

        if (result === yes) {
            return true;
        }

        if (result === no) {
            return false;
        }

        return null;
    }

    async promptForSelection(promptText: string, choices: string[]): Promise<string | null> {
        extensionLogOutputChannel.info(`Prompting for selection: ${promptText}`);

        const selected = await vscode.window.showQuickPick(choices, {
            placeHolder: formatText(promptText),
            canPickMany: false,
            ignoreFocusOut: true
        });

        return selected ?? null;
    }

    async promptForSelections(promptText: string, choices: string[]): Promise<string[] | null> {
        extensionLogOutputChannel.info(`Prompting for multiple selections: ${promptText}`);

        const selected = await vscode.window.showQuickPick(choices, {
            placeHolder: formatText(promptText),
            canPickMany: true,
            ignoreFocusOut: true
        });

        return selected ?? null;
    }

    async displayIncompatibleVersionError(requiredCapabilityStr: string, appHostHostingSdkVersion: string, rpcClient: ICliRpcClient) {
        extensionLogOutputChannel.info(`Displaying incompatible version error`);

        const cliInformationalVersion = await rpcClient.getCliVersion();

        const errorLines = [
            incompatibleAppHostError,
            aspireHostingSdkVersion(appHostHostingSdkVersion),
            aspireCliVersion(cliInformationalVersion),
            requiredCapability(requiredCapabilityStr),
        ];

        vscode.window.showErrorMessage(formatText(errorLines.join('. ')));

        errorLines.forEach(line => {
            extensionLogOutputChannel.error(formatText(line));
        });
    }

    displayError(errorMessage: string) {
        if (errorMessage.length === 0) {
            extensionLogOutputChannel.warn('Attempted to display an empty error message.');
            return;
        }

        extensionLogOutputChannel.error(`Displaying error: ${errorMessage}`);
        vscode.window.showErrorMessage(formatText(errorMessage));
        this.clearProgressNotification();
    }

    displayMessage(emoji: string, message: string) {
        if (message.length === 0) {
            extensionLogOutputChannel.warn('Attempted to display an empty message.');
            return;
        }

        extensionLogOutputChannel.info(`Displaying message: ${emoji} ${message}`);
        vscode.window.showInformationMessage(formatText(message));
    }

    // There is no need for a different success message handler, as a general informative message ~= success
    // in extension design philosophy.
    displaySuccess(message: string) {
        if (message.length === 0) {
            extensionLogOutputChannel.warn('Attempted to display an empty success message.');
            return;
        }

        extensionLogOutputChannel.info(`Displaying success message: ${message}`);
        vscode.window.showInformationMessage(formatText(message));
    }

    displaySubtleMessage(message: string) {
        if (message.length === 0) {
            extensionLogOutputChannel.warn('Attempted to display an empty subtle message.');
            return;
        }

        extensionLogOutputChannel.info(`Displaying subtle message: ${message}`);
        vscode.window.setStatusBarMessage(formatText(message), 5000);
    }

    displayPlainText(message: string) {
        extensionLogOutputChannel.info(`Displaying plain text: ${message}`);
        vscode.window.showInformationMessage(formatText(message));
    }

    // No direct equivalent in VS Code, so don't display anything visually, just log to output channel.
    displayEmptyLine() {
        extensionLogOutputChannel.append('\n');
    }

    async displayDashboardUrls(dashboardUrls: DashboardUrls) {
        extensionLogOutputChannel.info(`Displaying dashboard URLs: ${JSON.stringify(dashboardUrls)}`);

        const baseUrl = getDashboardUrlProperty(dashboardUrls, 'baseUrl');
        const codespacesUrl = getDashboardUrlProperty(dashboardUrls, 'codespacesUrl');

        this.writeDebugSessionMessage(`${dashboard}: `, true, AnsiColors.Green, false);
        this.writeDebugSessionMessage(baseUrl, true, AnsiColors.Blue);

        if (codespacesUrl) {
            this.writeDebugSessionMessage(`${codespaces}: `, true, AnsiColors.Green, false);
            this.writeDebugSessionMessage(codespacesUrl, true, AnsiColors.Blue);
        }

        // Refresh the Aspire panel so it picks up dashboard URLs for the running app host
        vscode.commands.executeCommand('aspire-vscode.refreshRunningAppHosts');

        //  If aspire.enableAspireDashboardAutoLaunch is 'launch', the dashboard will be launched automatically.
        //  If 'notification', a notification is shown with a link. If 'off', do nothing.
        const aspireConfig = vscode.workspace.getConfiguration('aspire');
        const rawDashboardAutoLaunch = aspireConfig.get<unknown>('enableAspireDashboardAutoLaunch', 'launch');

        // Handle legacy boolean values from before this setting was changed to an enum
        let dashboardAutoLaunch: 'launch' | 'notification' | 'off';
        if (rawDashboardAutoLaunch === true) {
            dashboardAutoLaunch = 'launch';
        } else if (rawDashboardAutoLaunch === false) {
            dashboardAutoLaunch = 'notification';
        } else if (rawDashboardAutoLaunch === 'launch' || rawDashboardAutoLaunch === 'notification' || rawDashboardAutoLaunch === 'off') {
            dashboardAutoLaunch = rawDashboardAutoLaunch;
        } else {
            dashboardAutoLaunch = 'launch';
        }

        if (dashboardAutoLaunch === 'launch') {
            // Open the dashboard URL in the configured browser. Prefer codespaces URL if available.
            const urlToOpen = codespacesUrl || baseUrl;
            const debugSession = this._getAspireDebugSession();
            if (debugSession) {
                const browserType = aspireConfig.get<DashboardBrowserType>('dashboardBrowser', 'openExternalBrowser');
                await debugSession.openDashboard(urlToOpen, browserType);
            }
            return;
        }

        if (dashboardAutoLaunch === 'off') {
            return;
        }

        const actions: vscode.MessageItem[] = [
            { title: directLink }
        ];

        if (codespacesUrl) {
            actions.push({ title: codespacesLink });
        }

        actions.push({ title: settingsLabel });

        // Delay 1 second to allow a slight pause between progress notification and message
        setTimeout(() => {
            // Don't await - fire and forget to avoid blocking
            vscode.window.showInformationMessage(
                openAspireDashboard,
                ...actions
            ).then(selected => {
                if (!selected) {
                    return;
                }

                extensionLogOutputChannel.info(`Selected action: ${selected.title}`);

                if (selected.title === directLink) {
                    vscode.env.openExternal(vscode.Uri.parse(baseUrl));
                }
                else if (selected.title === codespacesLink && codespacesUrl) {
                    vscode.env.openExternal(vscode.Uri.parse(codespacesUrl));
                }
                else if (selected.title === settingsLabel) {
                    vscode.commands.executeCommand('workbench.action.openSettings', 'aspire.enableAspireDashboardAutoLaunch');
                }
            });
        }, 1000);
    }

    async displayLines(lines: ConsoleLine[]) {
        const debugSession = this._getAspireDebugSession();
        const aspireTerminal = !debugSession ? this._getAspireTerminal?.() : undefined;
        for (const line of lines) {
            const text = getConsoleLineText(line);
            const stream = line.stream ?? line.Stream;
            extensionLogOutputChannel.info(formatText(text));
            if (debugSession) {
                debugSession.sendMessage(text, true, stream !== 'stderr' ? 'stdout' : 'stderr');
            } else if (aspireTerminal) {
                aspireTerminal.terminal.sendText(text, true);
            }
        }
    }

    displayCancellationMessage() {
        extensionLogOutputChannel.info(`Cancelled Aspire operation.`);
    }

    async openEditor(path: string) {
        extensionLogOutputChannel.info(`Opening path: ${path}`);

        // check if is folder
        if (await isDirectory(path)) {
            if (isFolderOpenInWorkspace(path)) {
                return;
            }

            const uri = vscode.Uri.file(path);
            vscode.commands.executeCommand('vscode.openFolder', uri, { forceNewWindow: false });
        }
        else {
            const fileUri = vscode.Uri.file(path);
            await vscode.window.showTextDocument(fileUri, { preview: false });
        }
    }

    logMessage(logLevel: CSLogLevel, message: string) {
        // Unable to log trace or debug messages, these levels are ignored by default
        // and we cannot set the log level programmatically. So for now, log as info
        // https://github.com/microsoft/vscode/issues/223536
        if (logLevel === 'Trace') {
            extensionLogOutputChannel.info(`[trace] ${formatText(message)}`);
        }
        else if (logLevel === 'Debug') {
            extensionLogOutputChannel.info(`[debug] ${formatText(message)}`);
        }
        else if (logLevel === 'Information') {
            extensionLogOutputChannel.info(formatText(message));
        }
        else if (logLevel === 'Warn') {
            extensionLogOutputChannel.warn(formatText(message));
        }
        else if (logLevel === 'Error' || logLevel === 'Critical') {
            extensionLogOutputChannel.error(formatText(message));
        }
    }

    writeDebugSessionMessage(message: string, stdout: boolean, textStyle: string | null | undefined, addNewLine: boolean = true) {
        const debugSession = this._getAspireDebugSession();
        if (!debugSession) {
            extensionLogOutputChannel.warn('Attempted to write to debug session, but no active debug session exists.');
            return;
        }

        debugSession.sendMessage(applyTextStyle(message, textStyle), addNewLine, stdout ? 'stdout' : 'stderr');
    }

    async launchAppHost(projectFile: string, args: string[], environment: EnvVar[], debug: boolean): Promise<void> {
        let debugSession = this._getAspireDebugSession();
        if (!debugSession) {
            throw new Error(aspireDebugSessionNotInitialized);
        }

        // Query CLI capabilities to determine if the CLI has already built the project
        const cliCapabilities = this._rpcClient ? await this._rpcClient.getCliCapabilities() : [];

        // If CLI has 'build-dotnet-using-cli' capability, it already built the project, so we don't need to force a build
        // For backwards compatibility with older CLIs that don't have this capability, we force a build
        const cliBuiltProject = cliCapabilities.includes('build-dotnet-using-cli');
        const forceBuild = !cliBuiltProject;

        return debugSession.startAppHost(projectFile, args, environment, debug, { forceBuild });
    }

    stopDebugging() {
        this.clearProgressNotification();
        this._getAspireDebugSession()?.dispose();
    }

    notifyAppHostStartupCompleted() {
        const debugSession = this._getAspireDebugSession();
        if (!debugSession) {
            throw new Error(aspireDebugSessionNotInitialized);
        }

        debugSession.notifyAppHostStartupCompleted();
    }

    async startDebugSession(workingDirectory: string, projectFile: string | null, debug: boolean, options?: DebugSessionOptions): Promise<void> {
        this.clearProgressNotification();

        const command = options?.command ?? 'run';

        const debugConfiguration: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            name: `Aspire ${command}: ${getRelativePathToWorkspace(projectFile ?? workingDirectory)}`,
            request: 'launch',
            program: projectFile ?? workingDirectory,
            command: command as AspireExtendedDebugConfiguration['command'],
            args: options?.args,
            noDebug: !debug,
        };

        const workspaceFolder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(workingDirectory));
        const didDebugStart = await vscode.debug.startDebugging(workspaceFolder, debugConfiguration);
        if (!didDebugStart) {
            throw new Error(failedToStartDebugSession);
        }
    }

    clearProgressNotification() {
        this._progressNotifier.clear();
    }

    /**
     * Closes the dashboard browser. Delegates to the current AspireDebugSession.
     */
    closeDashboard(): void {
        // No-op when called from InteractionService - the debug session handles closing in dispose()
    }
}

function tryExecuteEndpoint(interactionService: IInteractionService, withAuthentication: (callback: (...params: any[]) => any) => (...params: any[]) => any) {
    return (name: string, handler: (...args: any[]) => any) => withAuthentication(async (...args: any[]) => {
        try {
            return await Promise.resolve(handler(...args));
        }
        catch (err) {
            const message = (err && (((err as any).message) ?? String(err))) || 'An unknown error occurred';
            extensionLogOutputChannel.error(`Interaction service endpoint '${name}' failed: ${message}`);
            vscode.window.showErrorMessage(errorMessage(message));

            interactionService.showStatus(null);
            throw err;
        }
    });
}

export function addInteractionServiceEndpoints(connection: MessageConnection, interactionService: IInteractionService, rpcClient: ICliRpcClient, withAuthentication: (callback: (...params: any[]) => any) => (...params: any[]) => any) {
    const middleware = tryExecuteEndpoint(interactionService, withAuthentication);

    connection.onRequest("showStatus", middleware('showStatus', interactionService.showStatus.bind(interactionService)));
    connection.onRequest("promptForString", middleware('promptForString', async (promptText: string, defaultValue: string | null, required: boolean) => interactionService.promptForString(promptText, defaultValue, required, rpcClient)));
    connection.onRequest("promptForSecretString", middleware('promptForSecretString', async (promptText: string, required: boolean) => interactionService.promptForSecretString(promptText, required, rpcClient)));
    connection.onRequest("promptForFilePath", middleware('promptForFilePath', async (promptText: string, defaultValue: string | null, directory: boolean) => interactionService.promptForFilePath(promptText, defaultValue, directory)));
    connection.onRequest("confirm", middleware('confirm', interactionService.confirm.bind(interactionService)));
    connection.onRequest("promptForSelection", middleware('promptForSelection', interactionService.promptForSelection.bind(interactionService)));
    connection.onRequest("promptForSelections", middleware('promptForSelections', interactionService.promptForSelections.bind(interactionService)));
    connection.onRequest("displayIncompatibleVersionError", middleware('displayIncompatibleVersionError', (requiredCapability: string, appHostHostingSdkVersion: string) => interactionService.displayIncompatibleVersionError(requiredCapability, appHostHostingSdkVersion, rpcClient)));
    connection.onRequest("displayError", middleware('displayError', interactionService.displayError.bind(interactionService)));
    connection.onRequest("displayMessage", middleware('displayMessage', interactionService.displayMessage.bind(interactionService)));
    connection.onRequest("displaySuccess", middleware('displaySuccess', interactionService.displaySuccess.bind(interactionService)));
    connection.onRequest("displaySubtleMessage", middleware('displaySubtleMessage', interactionService.displaySubtleMessage.bind(interactionService)));
    connection.onRequest("displayEmptyLine", middleware('displayEmptyLine', interactionService.displayEmptyLine.bind(interactionService)));
    connection.onRequest("displayDashboardUrls", middleware('displayDashboardUrls', interactionService.displayDashboardUrls.bind(interactionService)));
    connection.onRequest("displayLines", middleware('displayLines', interactionService.displayLines.bind(interactionService)));
    connection.onRequest("displayPlainText", middleware('displayPlainText', interactionService.displayPlainText.bind(interactionService)));
    connection.onRequest("displayCancellationMessage", middleware('displayCancellationMessage', interactionService.displayCancellationMessage.bind(interactionService)));
    connection.onRequest("openEditor", middleware('openEditor', interactionService.openEditor.bind(interactionService)));
    connection.onRequest("logMessage", middleware('logMessage', interactionService.logMessage.bind(interactionService)));
    connection.onRequest("launchAppHost", middleware('launchAppHost', async (projectFile: string, args: string[], environment: EnvVar[], debug: boolean) => interactionService.launchAppHost(projectFile, args, environment, debug)));
    connection.onRequest("stopDebugging", middleware('stopDebugging', interactionService.stopDebugging.bind(interactionService)));
    connection.onRequest("notifyAppHostStartupCompleted", middleware('notifyAppHostStartupCompleted', interactionService.notifyAppHostStartupCompleted.bind(interactionService)));
    connection.onRequest("startDebugSession", middleware('startDebugSession', async (workingDirectory: string, projectFile: string | null, debug: boolean, options?: DebugSessionOptions) => interactionService.startDebugSession(workingDirectory, projectFile, debug, options)));
    connection.onRequest("writeDebugSessionMessage", middleware('writeDebugSessionMessage', interactionService.writeDebugSessionMessage.bind(interactionService)));
}
