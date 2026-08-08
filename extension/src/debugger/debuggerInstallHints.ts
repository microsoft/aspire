import * as vscode from 'vscode';
import {
    debuggerInstallAction,
    debuggerInstallFailed,
    debuggerInstallNotification,
    debuggerInstalledRestartAppHost,
    dontShowAgainLabel,
} from '../loc/strings';
import { getAllResourceDebuggerExtensions } from './debuggerExtensions';
import { ResourceState } from '../editor/resourceConstants';
import { extensionLogOutputChannel } from '../utils/logging';

/**
 * Snapshot property published by Aspire.Hosting from `SupportsDebuggingAnnotation.LaunchConfigurationType`
 * (see `KnownProperties.Resource.LaunchConfigurationType`). Its value is the launch configuration type the
 * resource would be launched with — `python`, `go`, `project`, ... — and its absence means the resource has
 * no debug support at all.
 */
export const launchConfigurationTypePropertyName = 'resource.launchConfigurationType';

export interface DebuggerInstallHint {
    debuggerName: string;
    extensionId: string;
}

/**
 * The parts of a resource snapshot the install hints need. `ResourceJson` is structurally assignable to
 * this; declaring the shape locally keeps the debugger module independent of the views layer.
 */
export interface DebuggableResourceSnapshot {
    state: string | null;
    properties: Record<string, string | null> | null;
}

export interface DebuggerInstallHintServiceDependencies {
    getExtension(extensionId: string): vscode.Extension<unknown> | undefined;
    onDidChangeExtensions: vscode.Event<void>;
    showInformationMessage(message: string, ...items: string[]): Thenable<string | undefined>;
    showErrorMessage(message: string): Thenable<string | undefined>;
    installExtension(extensionId: string): Thenable<void>;
}

/**
 * Install hints keyed by launch configuration type, derived from the same table that drives actual
 * debugging (`languages/*.ts`). Deriving both from one table is what keeps a resource that Aspire can
 * debug from silently getting no hint: any integration that adds a launch configuration type, including a
 * third-party one, is covered as soon as it has a `ResourceDebuggerExtension`.
 */
const debuggerInstallHintsByLaunchConfigurationType = new Map<string, DebuggerInstallHint>();

for (const debuggerExtension of getAllResourceDebuggerExtensions()) {
    if (!debuggerExtension.extensionId) {
        // A null extensionId means the debug adapter ships with VS Code, so there is nothing to install.
        continue;
    }

    debuggerInstallHintsByLaunchConfigurationType.set(debuggerExtension.resourceType, {
        // The display name describes the extension, not the language, because several launch
        // configuration types can share one extension ('project' and 'azure-functions' both need the C#
        // extension) and the toast is coalesced per extension id.
        debuggerName: debuggerExtension.extensionDisplayName ?? debuggerExtension.extensionId,
        extensionId: debuggerExtension.extensionId,
    });
}

const notificationSuppressedKeyPrefix = 'aspire.debuggerInstallHint.suppressed.';

/**
 * Returns the install hint for a resource snapshot, or `undefined` when the resource has no debug
 * support, is debugged by an adapter built into VS Code, or uses a launch configuration type this
 * build of the extension does not know about.
 */
export function getDebuggerInstallHintForResource(resource: DebuggableResourceSnapshot): DebuggerInstallHint | undefined {
    const launchConfigurationType = resource.properties?.[launchConfigurationTypePropertyName];
    return launchConfigurationType ? debuggerInstallHintsByLaunchConfigurationType.get(launchConfigurationType) : undefined;
}

export async function installDebuggerExtension(extensionId: string): Promise<void> {
    // https://code.visualstudio.com/api/references/commands - `workbench.extensions.installExtension`
    // accepts an extension id and resolves once the gallery install completes.
    await vscode.commands.executeCommand('workbench.extensions.installExtension', extensionId);
}

export class DebuggerInstallHintService implements vscode.Disposable {
    private readonly _onDidChange = new vscode.EventEmitter<void>();
    readonly onDidChange = this._onDidChange.event;

    private readonly _notificationsShownThisSession = new Set<string>();
    private readonly _installsAwaitingActivation = new Map<string, DebuggerInstallHint>();
    private readonly _extensionChangeSubscription: vscode.Disposable;
    private _disposed = false;

    constructor(
        private readonly _globalState: vscode.Memento,
        private readonly _dependencies: DebuggerInstallHintServiceDependencies,
    ) {
        this._extensionChangeSubscription = _dependencies.onDidChangeExtensions(() => this.refresh());
    }

    /**
     * Returns the debugger extension `resource` needs and the user does not have installed, if any.
     */
    getMissingDebugger(resource: DebuggableResourceSnapshot): DebuggerInstallHint | undefined {
        const hint = getDebuggerInstallHintForResource(resource);
        return hint && !this._dependencies.getExtension(hint.extensionId) ? hint : undefined;
    }

    /**
     * Shows an install toast for every debugger extension the given resources need and the user does not
     * have. Resources are only considered once they are running, so a resource that never starts does not
     * produce a prompt.
     *
     * The toast is coalesced to one per extension id per session, so it deliberately says nothing about
     * how many resources are affected: that number is a snapshot that goes stale as soon as another
     * resource starts, and the actionable fact is that the debug adapter is missing.
     */
    notifyMissingDebuggers(resources: Iterable<DebuggableResourceSnapshot>): void {
        if (this._disposed) {
            return;
        }

        for (const resource of resources) {
            if (resource.state !== ResourceState.Running) {
                continue;
            }

            const hint = this.getMissingDebugger(resource);
            if (!hint) {
                continue;
            }

            // The notification promise stays pending while the toast is visible, so resource updates must
            // not block on user interaction. `showNotificationIfNeeded` coalesces concurrent prompts.
            void this.showNotificationIfNeeded(hint).catch(error => {
                extensionLogOutputChannel.warn(`Failed to show a debugger install hint: ${String(error)}`);
            });
        }
    }

    /**
     * Shows the install toast for `hint`, unless one has already been shown this session, the user
     * suppressed it, or the extension is now installed.
     */
    async showNotificationIfNeeded(hint: DebuggerInstallHint): Promise<void> {
        if (!this._canShowNotification(hint)) {
            return;
        }

        // Mark the extension before opening the notification. Repository refreshes can overlap
        // while the toast is awaiting user input, and each language can have several resources
        // across several AppHosts.
        this._notificationsShownThisSession.add(hint.extensionId);

        let selected: string | undefined;
        try {
            selected = await this._dependencies.showInformationMessage(
                debuggerInstallNotification(hint.debuggerName),
                debuggerInstallAction,
                dontShowAgainLabel);
        } catch (error) {
            // No notification was shown successfully, so a later resource update should retry.
            this._notificationsShownThisSession.delete(hint.extensionId);
            throw error;
        }

        if (this._disposed) {
            return;
        }

        if (selected === dontShowAgainLabel) {
            await this._globalState.update(`${notificationSuppressedKeyPrefix}${hint.extensionId}`, true);
        } else if (selected === debuggerInstallAction) {
            await this.installExtension(hint);
        }
    }

    async installExtension(hint: DebuggerInstallHint): Promise<void> {
        if (this._disposed) {
            return;
        }

        try {
            await this._dependencies.installExtension(hint.extensionId);
        } catch (error) {
            // Installing goes through the marketplace, so it fails when the user is offline, behind a
            // proxy, or running a build without gallery access. Surface that instead of leaving the
            // hint in place with no explanation.
            if (!this._disposed) {
                void this._dependencies.showErrorMessage(debuggerInstallFailed(hint.debuggerName, getErrorMessage(error)));
            }

            return;
        }

        if (this._disposed) {
            return;
        }

        // `workbench.extensions.installExtension` resolves when the install completes, but the
        // extension host publishes the new extension afterwards, so `getExtension` can still return
        // undefined here. Defer the follow-up guidance until the extension is actually visible;
        // `refresh` re-checks on every `extensions.onDidChange` event.
        this._installsAwaitingActivation.set(hint.extensionId, hint);
        this.refresh();
    }

    refresh(): void {
        if (this._disposed) {
            return;
        }

        this._notifyCompletedInstalls();
        this._onDidChange.fire();
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        this._installsAwaitingActivation.clear();
        this._extensionChangeSubscription.dispose();
        this._onDidChange.dispose();
    }

    private _canShowNotification(hint: DebuggerInstallHint): boolean {
        return !this._disposed
            && !this._dependencies.getExtension(hint.extensionId)
            && !this._notificationsShownThisSession.has(hint.extensionId)
            && !this._globalState.get<boolean>(`${notificationSuppressedKeyPrefix}${hint.extensionId}`, false);
    }

    private _notifyCompletedInstalls(): void {
        for (const [extensionId, hint] of [...this._installsAwaitingActivation]) {
            if (!this._dependencies.getExtension(extensionId)) {
                continue;
            }

            this._installsAwaitingActivation.delete(extensionId);

            // Debug capabilities are snapshotted into DEBUG_SESSION_INFO / ASPIRE_EXTENSION_CAPABILITIES
            // when the AppHost process starts (see utils/AspireTerminalProvider), so a debugger installed
            // while an AppHost is running only takes effect on the next run. Say so rather than implying
            // the already-running resource became debuggable.
            void this._dependencies.showInformationMessage(debuggerInstalledRestartAppHost(hint.debuggerName));
        }
    }
}

export function createDebuggerInstallHintService(globalState: vscode.Memento): DebuggerInstallHintService {
    return new DebuggerInstallHintService(globalState, {
        getExtension: extensionId => vscode.extensions.getExtension(extensionId),
        onDidChangeExtensions: vscode.extensions.onDidChange,
        showInformationMessage: (message, ...items) => vscode.window.showInformationMessage(message, ...items),
        showErrorMessage: message => vscode.window.showErrorMessage(message),
        installExtension: installDebuggerExtension,
    });
}

function getErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}
