import * as path from 'path';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import { CliVersionStatus, CliVersionStatusOptions, ConfigInfoProvider } from './configInfoProvider';
import { CliPathResolutionTarget } from './cliPathVariables';
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
    private static readonly _maxConcurrentProbes = 4;

    private readonly _notifiedCliVersions = new Set<string>();
    private readonly _cancellationSource = new vscode.CancellationTokenSource();
    private readonly _inFlightByCliPath = new Map<string, Promise<void>>();
    private readonly _probeWaiters: Array<() => void> = [];
    private _activeProbeCount = 0;
    private _disposed = false;

    constructor(
        private readonly _versionProvider: CliVersionProvider,
        private readonly _surface: OutdatedCliNotificationSurface = defaultSurface,
    ) {
    }

    async notifyIfOutdated(target: CliPathResolutionTarget, cliPath: string): Promise<void> {
        if (this._disposed) {
            return;
        }

        const cliPathKey = getComparisonKey(path.normalize(cliPath));
        const existingProbe = this._inFlightByCliPath.get(cliPathKey);
        if (existingProbe) {
            await existingProbe;
            return;
        }

        const probe = this._runProbe(target, cliPath).finally(() => {
            if (this._inFlightByCliPath.get(cliPathKey) === probe) {
                this._inFlightByCliPath.delete(cliPathKey);
            }
        });
        this._inFlightByCliPath.set(cliPathKey, probe);
        await probe;
    }

    private async _runProbe(target: CliPathResolutionTarget, cliPath: string): Promise<void> {
        if (!await this._acquireProbeSlot()) {
            return;
        }

        let result: CliVersionStatus | null;
        try {
            const options: CliVersionStatusOptions = {
                target,
                cliPath,
                cancellationToken: this._cancellationSource.token,
            };
            result = await this._versionProvider.getCliVersionStatus(minimumSupportedAspireCliVersion, options);
        }
        finally {
            this._releaseProbeSlot();
        }
        if (this._disposed || result?.status !== 'unsupported') {
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
            strings.outdatedAspireCliWarning(result.version, result.cliPath, minimumSupportedAspireCliVersion),
            strings.updateAspireCliAction);
        if (!this._disposed && selection === strings.updateAspireCliAction) {
            await this._surface.executeCommand(updateAspireCliCommand, target, result.cliPath);
        }
    }

    private async _acquireProbeSlot(): Promise<boolean> {
        if (this._disposed) {
            return false;
        }
        if (this._activeProbeCount < OutdatedCliNotifier._maxConcurrentProbes) {
            this._activeProbeCount++;
            return true;
        }

        await new Promise<void>(resolve => this._probeWaiters.push(resolve));
        if (this._disposed) {
            return false;
        }

        // The releasing probe transfers its slot directly to this waiter. Do not increment here:
        // keeping the count at the limit prevents a new arrival from taking the same slot before
        // this continuation resumes.
        return true;
    }

    private _releaseProbeSlot(): void {
        const waiter = this._probeWaiters.shift();
        if (waiter) {
            waiter();
            return;
        }

        this._activeProbeCount--;
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this._cancellationSource.cancel();
        this._cancellationSource.dispose();
        while (this._probeWaiters.length > 0) {
            this._probeWaiters.shift()?.();
        }
        this._notifiedCliVersions.clear();
    }
}
