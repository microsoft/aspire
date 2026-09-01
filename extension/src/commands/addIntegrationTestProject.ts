import * as path from 'path';
import * as vscode from 'vscode';
import {
    addIntegrationTestProjectCapabilityCouldNotBeVerified,
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
    private static readonly _unavailableCapabilityRetryDelayMs = 5_000;

    private _refreshGeneration = 0;
    private _probeCancellationSource: vscode.CancellationTokenSource | undefined;
    private _retryTimer: ReturnType<typeof setTimeout> | undefined;
    private _disposed = false;

    constructor(private readonly _configInfoProvider: ConfigInfoProvider) {
    }

    async refresh(forceRefresh = false): Promise<void> {
        if (this._disposed) {
            return;
        }

        const generation = ++this._refreshGeneration;
        this._cancelPendingWork();
        await this._publish(false, generation);
        if (this._disposed || generation !== this._refreshGeneration) {
            return;
        }

        const cancellationSource = new vscode.CancellationTokenSource();
        this._probeCancellationSource = cancellationSource;
        try {
            const status = await this._configInfoProvider.getCapabilityStatus(
                aspireTestAppHostCapability,
                {
                    target: getAvailabilityTarget(),
                    forceRefresh,
                    suppressErrors: true,
                    cancellationToken: cancellationSource.token,
                });
            if (this._disposed
                || generation !== this._refreshGeneration
                || cancellationSource.token.isCancellationRequested) {
                return;
            }

            if (status === 'unavailable') {
                this._scheduleRetry(generation);
                return;
            }

            await this._publish(status === 'supported', generation);
        }
        catch (error) {
            if (!cancellationSource.token.isCancellationRequested
                && !this._disposed
                && generation === this._refreshGeneration) {
                extensionLogOutputChannel.warn(`Unable to determine integration test scaffolding availability: ${String(error)}`);
            }
        }
        finally {
            if (this._probeCancellationSource === cancellationSource) {
                this._probeCancellationSource = undefined;
            }
            cancellationSource.dispose();
        }
    }

    dispose(): void {
        this._disposed = true;
        this._refreshGeneration++;
        this._cancelPendingWork();
        void vscode.commands.executeCommand('setContext', addIntegrationTestProjectSupportedContext, false);
    }

    private _scheduleRetry(generation: number): void {
        this._retryTimer = setTimeout(() => {
            this._retryTimer = undefined;
            if (this._disposed || generation !== this._refreshGeneration) {
                return;
            }

            // A shared probe can still be running after this caller times out, so retry with an
            // invocation-owned probe rather than waiting on the same work again.
            void this.refresh(true);
        }, AddIntegrationTestProjectAvailability._unavailableCapabilityRetryDelayMs);
    }

    private _cancelPendingWork(): void {
        if (this._retryTimer !== undefined) {
            clearTimeout(this._retryTimer);
            this._retryTimer = undefined;
        }

        this._probeCancellationSource?.cancel();
        this._probeCancellationSource = undefined;
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
        // The targeted message is already displayed. Cancellation records a non-success telemetry outcome
        // without causing tryExecuteCommand to show another error.
        throw new vscode.CancellationError();
    }

    const supportStatus = await configInfoProvider.getCapabilityStatus(
        aspireTestAppHostCapability,
        {
            cliPath,
            target,
            forceRefresh: true,
            suppressErrors: true,
        });
    if (supportStatus !== 'supported') {
        await vscode.window.showErrorMessage(supportStatus === 'unsupported'
            ? addIntegrationTestProjectUnsupported
            : addIntegrationTestProjectCapabilityCouldNotBeVerified);
        // The targeted message is already displayed. Cancellation records a non-success telemetry outcome
        // without causing tryExecuteCommand to show another error.
        throw new vscode.CancellationError();
    }

    await terminalProvider.sendAspireCommandToAspireTerminal(
        ['new', 'aspire-test'],
        true,
        ['--apphost', appHostPath],
        {
            cliPath,
            target,
        });
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
