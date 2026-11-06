import * as vscode from 'vscode';
import {
    azureFunctionsExtensionId,
    codeLldbExtensionId,
    csharpExtensionId,
    getRustExtensionId,
    javaDebugExtensionId,
    javaLanguageExtensionId,
    mauiExtensionId,
} from '../capabilities';
import { ResourceState } from '../editor/resourceConstants';
import {
    debuggerSetupAction,
    debuggerSetupNotification,
    debuggerExtensionDisabled,
    debuggerInstalledRestartAppHost,
    dontShowAgainLabel,
    errorMessage,
    openExtensionsLabel,
} from '../loc/strings';
import { bunDebuggerExtension } from './languages/bun';
import { goDebuggerExtension } from './languages/go';
import { pythonDebuggerExtension } from './languages/python';
import { isCommandCancellation } from '../utils/telemetry';

export const launchConfigurationTypePropertyName = 'resource.launchConfigurationType';

export interface DebuggerInstallHint {
    debuggerName: string;
    debuggerType: string;
    extensionIds: readonly string[];
}

interface DebuggerInstallFailure {
    success: false;
    errorKind: string;
}

interface DebuggableResourceSnapshot {
    state: string | null;
    properties: Record<string, string | null> | null;
}

interface DebuggerInstallHintDataSource {
    readonly workspaceAppHostCandidatePaths: readonly string[];
    readonly workspaceResources: readonly DebuggableResourceSnapshot[];
    readonly appHosts: readonly { resources?: readonly DebuggableResourceSnapshot[] | null }[];
    readonly onDidChangeData: vscode.Event<void>;
    keepDataActive(): vscode.Disposable;
}

const debuggerInstallHints = new Map<string, DebuggerInstallHint>([
    ['python', {
        debuggerName: 'Python',
        debuggerType: 'python',
        extensionIds: [pythonDebuggerExtension.extensionId!],
    }],
    ['go', {
        debuggerName: 'Go',
        debuggerType: 'go',
        extensionIds: [goDebuggerExtension.extensionId!],
    }],
    ['bun', {
        debuggerName: 'Bun',
        debuggerType: 'bun',
        extensionIds: [bunDebuggerExtension.extensionId!],
    }],
    ['java', {
        debuggerName: 'Java',
        debuggerType: 'java',
        extensionIds: [javaLanguageExtensionId, javaDebugExtensionId],
    }],
    ['maui', {
        debuggerName: '.NET MAUI',
        debuggerType: 'maui',
        extensionIds: [mauiExtensionId],
    }],
    ['azure-functions', {
        debuggerName: 'Azure Functions',
        debuggerType: 'azure-functions',
        extensionIds: [csharpExtensionId, azureFunctionsExtensionId],
    }],
]);

const notificationSuppressedKeyPrefix = 'aspire.debuggerInstallHint.suppressed.';

export function getDebuggerInstallHintForResource(
    resource: DebuggableResourceSnapshot,
    platform: NodeJS.Platform = process.platform
): DebuggerInstallHint | undefined {
    const launchConfigurationType = resource.properties?.[launchConfigurationTypePropertyName];
    let hint = launchConfigurationType ? debuggerInstallHints.get(launchConfigurationType) : undefined;
    if (launchConfigurationType === 'rust') {
        const selectedExtensionId = getRustExtensionId(
            platform,
            candidateExtensionId => !!vscode.extensions.getExtension(candidateExtensionId));
        // Preserve either installed Windows adapter, but recommend CodeLLDB when neither is installed
        // because GNU targets require it and it also supports MSVC targets.
        const extensionId = platform === 'win32' && !vscode.extensions.getExtension(selectedExtensionId)
            ? codeLldbExtensionId
            : selectedExtensionId;
        hint = {
            debuggerName: 'Rust',
            debuggerType: 'rust',
            extensionIds: [extensionId],
        };
    }

    return hint?.extensionIds.some(extensionId => !vscode.extensions.getExtension(extensionId))
        ? hint
        : undefined;
}

export class DebuggerInstallHintService {
    private static readonly _extensionRegistrationTimeoutMs = 5_000;
    private readonly _installsInProgress = new Map<string, Promise<void | DebuggerInstallFailure>>();
    private readonly _notificationsShown = new Set<string>();

    constructor(private readonly _globalState: vscode.Memento) {
    }

    watchForMissingDebuggers(dataSource: DebuggerInstallHintDataSource): vscode.Disposable {
        let dataLease: vscode.Disposable | undefined;
        const refresh = () => {
            const hasKnownAppHost = dataSource.workspaceAppHostCandidatePaths.length > 0
                || dataSource.appHosts.length > 0;
            if (hasKnownAppHost && !dataLease) {
                // AppHost discovery runs independently of the panel data lifecycle. Wait for a real
                // candidate before keeping ps/describe active so unrelated .NET workspaces do not
                // acquire a permanent Aspire CLI process just because the extension activated.
                dataLease = dataSource.keepDataActive();
            } else if (!hasKnownAppHost && dataLease) {
                dataLease.dispose();
                dataLease = undefined;
            }

            const resources = [
                ...dataSource.workspaceResources,
                ...dataSource.appHosts.flatMap(appHost => appHost.resources ?? []),
            ];
            void this.notifyMissingDebuggers(resources);
        };

        const dataSubscription = dataSource.onDidChangeData(refresh);
        // Enabling or disabling a debugger extension changes which hints apply without changing
        // AppHost data, so the resource stream alone would never re-evaluate them.
        const extensionSubscription = vscode.extensions.onDidChange(refresh);
        refresh();

        return vscode.Disposable.from(
            dataSubscription,
            extensionSubscription,
            new vscode.Disposable(() => dataLease?.dispose()));
    }

    async notifyMissingDebuggers(resources: Iterable<DebuggableResourceSnapshot>): Promise<void> {
        const notifications: Promise<void>[] = [];
        for (const resource of resources) {
            if (resource.state !== ResourceState.Running) {
                continue;
            }

            const hint = getDebuggerInstallHintForResource(resource);
            if (hint) {
                notifications.push(this._showNotification(hint));
            }
        }

        await Promise.all(notifications);
    }

    installDebuggerExtension(hint: DebuggerInstallHint): Promise<void | DebuggerInstallFailure> {
        const existingInstallation = this._installsInProgress.get(hint.debuggerType);
        if (existingInstallation) {
            return existingInstallation;
        }

        // Start the install after it is tracked because command execution can synchronously trigger
        // an extension change event that re-evaluates setup notifications.
        const installation = Promise.resolve().then(() => this._installDebuggerExtension(hint));
        this._installsInProgress.set(hint.debuggerType, installation);
        void installation.then(
            () => this._clearInstallation(hint.debuggerType, installation),
            () => this._clearInstallation(hint.debuggerType, installation));

        return installation;
    }

    private async _installDebuggerExtension(hint: DebuggerInstallHint): Promise<void | DebuggerInstallFailure> {
        try {
            const missingExtensionIds = hint.extensionIds.filter(
                extensionId => !vscode.extensions.getExtension(extensionId));
            for (const extensionId of missingExtensionIds) {
                await vscode.commands.executeCommand('workbench.extensions.installExtension', extensionId);
            }

            // Installing an already-installed but disabled extension is a no-op, and disabled
            // extensions remain absent from this registry. A fresh install can also appear after
            // the command resolves, so wait for the registry change before deciding it is disabled.
            // See https://github.com/microsoft/vscode/issues/71943.
            const registered = await this._waitForExtensionRegistrations(hint.extensionIds);
            if (registered) {
                await vscode.window.showInformationMessage(debuggerInstalledRestartAppHost(hint.debuggerName));
            } else {
                const selected = await vscode.window.showWarningMessage(
                    debuggerExtensionDisabled(hint.debuggerName),
                    openExtensionsLabel);
                if (selected === openExtensionsLabel) {
                    const unregisteredExtensionIds = hint.extensionIds.filter(
                        extensionId => !vscode.extensions.getExtension(extensionId));
                    if (unregisteredExtensionIds.length > 0) {
                        await vscode.commands.executeCommand(
                            'workbench.extensions.search',
                            unregisteredExtensionIds.map(extensionId => `@id:${extensionId}`).join(' '));
                    }
                }
            }
        } catch (error) {
            // The command wrapper turns a cancellation into a silent no-op; dismissing the install
            // prompt is not a failure worth a notification.
            if (isCommandCancellation(error)) {
                throw error;
            }

            await vscode.window.showErrorMessage(errorMessage(error));
            return {
                success: false,
                errorKind: error instanceof Error ? error.name : 'Error',
            };
        }
    }

    private _clearInstallation(
        debuggerType: string,
        installation: Promise<void | DebuggerInstallFailure>,
    ): void {
        if (this._installsInProgress.get(debuggerType) === installation) {
            this._installsInProgress.delete(debuggerType);
        }
    }

    private async _waitForExtensionRegistrations(extensionIds: readonly string[]): Promise<boolean> {
        const areAllExtensionsRegistered = () =>
            extensionIds.every(extensionId => !!vscode.extensions.getExtension(extensionId));
        if (areAllExtensionsRegistered()) {
            return true;
        }

        return new Promise(resolve => {
            let settled = false;
            let timeout: NodeJS.Timeout | undefined;
            let subscription: vscode.Disposable | undefined;
            const finish = (registered: boolean) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeout) {
                    clearTimeout(timeout);
                }
                subscription?.dispose();
                resolve(registered);
            };
            subscription = vscode.extensions.onDidChange(() => {
                if (areAllExtensionsRegistered()) {
                    finish(true);
                }
            });
            timeout = setTimeout(
                () => finish(false),
                DebuggerInstallHintService._extensionRegistrationTimeoutMs);

            // Close the gap between the initial check and registering the change listener.
            if (areAllExtensionsRegistered()) {
                finish(true);
            }
        });
    }

    private async _showNotification(hint: DebuggerInstallHint): Promise<void> {
        const suppressionKey = `${notificationSuppressedKeyPrefix}${hint.debuggerType}`;
        if (this._notificationsShown.has(hint.debuggerType)
            || this._installsInProgress.has(hint.debuggerType)
            || this._globalState.get<boolean>(suppressionKey, false)) {
            return;
        }

        // Mark the debugger before awaiting user input so overlapping repository updates cannot
        // open duplicate notifications for multiple resources using the same debugger.
        this._notificationsShown.add(hint.debuggerType);

        try {
            const selected = await vscode.window.showWarningMessage(
                debuggerSetupNotification(hint.debuggerName),
                debuggerSetupAction,
                dontShowAgainLabel);

            if (selected === debuggerSetupAction) {
                await this.installDebuggerExtension(hint);
            } else if (selected === dontShowAgainLabel) {
                await this._globalState.update(suppressionKey, true);
            }
        } catch (error) {
            if (!isCommandCancellation(error)) {
                this._notificationsShown.delete(hint.debuggerType);
                await vscode.window.showErrorMessage(errorMessage(error));
            }
        }
    }
}
