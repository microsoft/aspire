import * as vscode from 'vscode';
import { CliPathResolutionTarget, getCliPathTargetKey } from './cliPathVariables';
import { cliPathResolver, getConfiguredCliPath, isConfiguredCliPathRejectedForForwarding } from './cliPath';
import { configuredCliPathRejected, configuredCliPathRejectedOpenSetting } from '../loc/strings';

/** The setting a rejected configured CLI path comes from. */
const cliPathSettingId = 'aspire.aspireCliExecutablePath';

/**
 * Test seam for the notification surface, so unit tests can observe what the user would see
 * without a real window.
 */
export interface CliPathRejectionNotificationSurface {
    showWarning(message: string, ...actions: string[]): Thenable<string | undefined>;
    openSetting(settingId: string): Thenable<unknown>;
}

const defaultSurface: CliPathRejectionNotificationSurface = {
    showWarning: (message, ...actions) => vscode.window.showWarningMessage(message, ...actions),
    openSetting: settingId => vscode.commands.executeCommand('workbench.action.openSettings', settingId),
};

/** Test seam for the CLI path resolution state the notifier reads. */
export interface CliPathRejectionState {
    getConfiguredPath(target: CliPathResolutionTarget): string;
    isRejected(configuredPath: string, target: CliPathResolutionTarget): boolean;
    onDidChangeRejection(listener: (target: CliPathResolutionTarget) => void): vscode.Disposable;
}

const defaultState: CliPathRejectionState = {
    getConfiguredPath: target => getConfiguredCliPath(target),
    isRejected: (configuredPath, target) => isConfiguredCliPathRejectedForForwarding(configuredPath, target),
    onDidChangeRejection: listener => cliPathResolver.onDidChangeConfiguredCliPathRejection(listener),
};

/**
 * Surfaces a user-visible warning when an explicitly configured Aspire CLI path is rejected and
 * resolution silently falls back to a different CLI.
 *
 * Without this, the only signal is a line in the extension output channel, so a mistyped path (or
 * one that names the build output directory rather than the executable) looks like it took effect
 * while every command actually runs an unrelated CLI version. That mismatch surfaces much later as
 * confusing package-downgrade or assembly-load errors.
 */
export class CliPathRejectionNotifier implements vscode.Disposable {
    private readonly _disposable: vscode.Disposable;

    // Keyed by resolution scope so a rejection in one workspace folder does not suppress the
    // warning for another. The stored value is the path already reported for that scope, so a
    // user who edits the setting to a different bad value is told again.
    private readonly _notifiedPathByScope = new Map<string, string>();

    constructor(
        private readonly _surface: CliPathRejectionNotificationSurface = defaultSurface,
        private readonly _state: CliPathRejectionState = defaultState,
    ) {
        this._disposable = this._state.onDidChangeRejection(target => {
            void this.notifyIfRejected(target);
        });
    }

    async notifyIfRejected(target: CliPathResolutionTarget): Promise<void> {
        const scopeKey = getCliPathTargetKey(target);
        const configuredPath = this._state.getConfiguredPath(target);

        if (!configuredPath || !this._state.isRejected(configuredPath, target)) {
            // Resolution recovered (or the setting was cleared), so allow the same path to be
            // reported again if it is rejected once more later in the session.
            this._notifiedPathByScope.delete(scopeKey);
            return;
        }

        if (this._notifiedPathByScope.get(scopeKey) === configuredPath) {
            return;
        }

        this._notifiedPathByScope.set(scopeKey, configuredPath);

        const selection = await this._surface.showWarning(
            configuredCliPathRejected(configuredPath),
            configuredCliPathRejectedOpenSetting);

        if (selection === configuredCliPathRejectedOpenSetting) {
            await this._surface.openSetting(cliPathSettingId);
        }
    }

    dispose(): void {
        this._disposable.dispose();
        this._notifiedPathByScope.clear();
    }
}
