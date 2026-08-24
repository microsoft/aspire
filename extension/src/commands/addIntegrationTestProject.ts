import * as path from 'path';
import * as vscode from 'vscode';
import {
    addIntegrationTestProjectRequiresCSharpAppHost,
    addIntegrationTestProjectUnsupported,
} from '../loc/strings';
import { aspireTestAppHostCapability } from '../types/configInfo';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import {
    CliPathResolutionTarget,
    windowCliPathTarget,
    workspaceFolderCliPathTarget,
} from '../utils/cliPathVariables';
import { extensionLogOutputChannel } from '../utils/logging';

export const addIntegrationTestProjectSupportedContext = 'aspire.addIntegrationTestProjectSupported';

export class AddIntegrationTestProjectAvailability implements vscode.Disposable {
    private _refreshGeneration = 0;
    private _disposed = false;

    constructor(private readonly _configInfoProvider: ConfigInfoProvider) {
    }

    async refresh(forceRefresh = false): Promise<void> {
        const generation = ++this._refreshGeneration;
        await this._publish(false, generation);
        if (this._disposed || generation !== this._refreshGeneration) {
            return;
        }

        try {
            const supported = await this._configInfoProvider.hasCapability(
                aspireTestAppHostCapability,
                {
                    target: getAvailabilityTarget(),
                    forceRefresh,
                    suppressErrors: true,
                });
            await this._publish(supported, generation);
        }
        catch (error) {
            if (!this._disposed && generation === this._refreshGeneration) {
                extensionLogOutputChannel.warn(`Unable to determine integration test scaffolding availability: ${String(error)}`);
            }
        }
    }

    dispose(): void {
        this._disposed = true;
        this._refreshGeneration++;
        void vscode.commands.executeCommand('setContext', addIntegrationTestProjectSupportedContext, false);
    }

    private async _publish(supported: boolean, generation: number): Promise<void> {
        if (!this._disposed && generation === this._refreshGeneration) {
            await vscode.commands.executeCommand(
                'setContext',
                addIntegrationTestProjectSupportedContext,
                supported);
        }
    }
}

export async function addIntegrationTestProject(
    terminalProvider: AspireTerminalProvider,
    configInfoProvider: ConfigInfoProvider,
    appHostPath: string,
    target: CliPathResolutionTarget,
    cliPath: string,
): Promise<void> {
    if (path.extname(appHostPath).toLowerCase() !== '.csproj') {
        await vscode.window.showErrorMessage(addIntegrationTestProjectRequiresCSharpAppHost);
        return;
    }

    const supported = await configInfoProvider.hasCapability(
        aspireTestAppHostCapability,
        {
            cliPath,
            target,
            forceRefresh: true,
            suppressErrors: true,
        });
    if (!supported) {
        await vscode.window.showErrorMessage(addIntegrationTestProjectUnsupported);
        return;
    }

    await terminalProvider.sendAspireCommandToAspireTerminal(
        ['new', 'aspire-test'],
        true,
        ['--apphost', appHostPath],
        { cliPath, target });
}

function getAvailabilityTarget(): CliPathResolutionTarget {
    const activeUri = vscode.window.activeTextEditor?.document.uri;
    const workspaceFolder = activeUri
        ? vscode.workspace.getWorkspaceFolder(activeUri)
        : vscode.workspace.workspaceFolders?.[0];
    return workspaceFolder
        ? workspaceFolderCliPathTarget(workspaceFolder)
        : windowCliPathTarget;
}
