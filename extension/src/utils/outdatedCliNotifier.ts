import * as path from 'path';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import { CliVersionStatusOptions, ConfigInfoProvider } from './configInfoProvider';
import { CliPathResolutionTarget, windowCliPathTarget, workspaceFolderCliPathTarget } from './cliPathVariables';
import { getComparisonKey } from './paths/comparison';

export const minimumSupportedAspireCliVersion = '13.5.0';
const updateAspireCliCommand = 'aspire-vscode.updateSelf';

type CliVersionProvider = Pick<ConfigInfoProvider, 'getCliVersionStatus'>;

export interface OutdatedCliNotificationSurface {
    showWarning(message: string, ...actions: string[]): Thenable<string | undefined>;
    executeCommand(command: string, ...args: unknown[]): Thenable<unknown>;
}

const defaultSurface: OutdatedCliNotificationSurface = {
    showWarning: (message, ...actions) => vscode.window.showWarningMessage(message, ...actions),
    executeCommand: (command, ...args) => vscode.commands.executeCommand(command, ...args),
};

/**
 * Warns when an Aspire feature actively selects a CLI older than the release that fixed AppHost
 * startup issue #17354. Checks are session-scoped and best effort: an unavailable version probe
 * remains silent, while replacing a CLI in place with a different version is observed.
 */
export class OutdatedCliNotifier implements vscode.Disposable {
    private readonly _notifiedCliVersions = new Set<string>();

    constructor(
        private readonly _versionProvider: CliVersionProvider,
        private readonly _surface: OutdatedCliNotificationSurface = defaultSurface,
    ) {
    }

    async notifyIfOutdated(target: CliPathResolutionTarget, cliPath?: string): Promise<void> {
        const options: CliVersionStatusOptions = { target, cliPath };
        const result = await this._versionProvider.getCliVersionStatus(minimumSupportedAspireCliVersion, options);
        if (result?.status !== 'unsupported') {
            return;
        }

        const notificationKey = `${getComparisonKey(path.normalize(result.cliPath))}\u0000${result.version}`;
        if (this._notifiedCliVersions.has(notificationKey)) {
            return;
        }

        // Mark the executable/version before showing UI so concurrent folder probes cannot create
        // duplicate notifications while the first warning is still awaiting user input.
        this._notifiedCliVersions.add(notificationKey);
        const selection = await this._surface.showWarning(
            strings.outdatedAspireCliWarning(result.version, minimumSupportedAspireCliVersion),
            strings.updateAspireCliAction);
        if (selection === strings.updateAspireCliAction) {
            await this._surface.executeCommand(updateAspireCliCommand, target, result.cliPath);
        }
    }

    async notifyForActiveCliTargets(): Promise<void> {
        const targets = [
            windowCliPathTarget,
            ...(vscode.workspace.workspaceFolders ?? []).map(workspaceFolderCliPathTarget),
        ];
        await Promise.all(targets.map(target => this.notifyIfOutdated(target)));
    }

    dispose(): void {
        this._notifiedCliVersions.clear();
    }
}
