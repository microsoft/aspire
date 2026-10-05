import * as vscode from 'vscode';
import { isCsDevKitInstalled } from '../capabilities';
import { hotReloadDisabledNotice, openSettingsLabel } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

const hotReloadConfigurationSection = 'csharp.experimental.debug';
const hotReloadConfigurationName = 'hotReload';
const hotReloadOnSaveConfigurationSection = 'csharp.debug';
const hotReloadOnSaveConfigurationName = 'hotReloadOnSave';
const aspireConfigurationSection = 'aspire';
const hotReloadNotificationConfigurationName = 'enableHotReloadNotification';
const openSettingsCommand = 'workbench.action.openSettings';
const hotReloadSetting = `${hotReloadConfigurationSection}.${hotReloadConfigurationName}`;
const hotReloadDisabledAdvisoryShownKey = 'aspire.hotReloadDisabledAdvisoryShown';

export interface HotReloadDiagnostics {
    devKitInstalled: boolean;
    workspaceTrusted: boolean;
    settingContributed: boolean;
    settingEnabled: boolean;
    reloadOnSaveEnabled: boolean;
}

export function getHotReloadDiagnostics(): HotReloadDiagnostics {
    const hotReloadConfiguration = vscode.workspace.getConfiguration(hotReloadConfigurationSection);
    // VS Code can keep returning a key-only inspection, or just a user-scoped value, after an extension
    // stops contributing a setting. The default value is the reliable signal that the setting still exists.
    const hotReloadSettingInspection = hotReloadConfiguration.inspect<boolean>(hotReloadConfigurationName);

    return {
        devKitInstalled: isCsDevKitInstalled(),
        workspaceTrusted: vscode.workspace.isTrusted,
        settingContributed: hotReloadSettingInspection?.defaultValue !== undefined,
        settingEnabled: hotReloadConfiguration.get<boolean>(hotReloadConfigurationName) === true,
        reloadOnSaveEnabled: vscode.workspace
            .getConfiguration(hotReloadOnSaveConfigurationSection)
            .get<boolean>(hotReloadOnSaveConfigurationName) !== false
    };
}

export function logHotReloadDiagnostics(resourceIdentifier: string, diagnostics: HotReloadDiagnostics): void {
    extensionLogOutputChannel.info(
        `Hot Reload state for ${resourceIdentifier}: devKitInstalled=${diagnostics.devKitInstalled}, ` +
        `workspaceTrusted=${diagnostics.workspaceTrusted}, ` +
        `${hotReloadSetting}.contributed=${diagnostics.settingContributed}, ` +
        `${hotReloadSetting}=${diagnostics.settingEnabled}, ` +
        `${hotReloadOnSaveConfigurationSection}.${hotReloadOnSaveConfigurationName}=${diagnostics.reloadOnSaveEnabled}`);
}

let hotReloadDisabledAdvisoryShown = false;
let hotReloadAdvisoryWorkspaceState: vscode.Memento | undefined;

export function initializeHotReloadAdvisory(workspaceState: vscode.Memento): void {
    hotReloadAdvisoryWorkspaceState = workspaceState;

    try {
        hotReloadDisabledAdvisoryShown = workspaceState.get<boolean>(hotReloadDisabledAdvisoryShownKey, false);
    }
    catch (err) {
        hotReloadDisabledAdvisoryShown = false;
        extensionLogOutputChannel.warn(`C# Dev Kit Hot Reload advisory persistence failed: ${err instanceof Error ? err.message : String(err)}`);
    }
}

export async function showHotReloadDisabledAdvisoryIfNeeded(diagnostics: HotReloadDiagnostics): Promise<void> {
    if (hotReloadDisabledAdvisoryShown
        || !diagnostics.devKitInstalled
        || !diagnostics.settingContributed
        || diagnostics.settingEnabled
        || !vscode.workspace
            .getConfiguration(aspireConfigurationSection)
            .get<boolean>(hotReloadNotificationConfigurationName, true)) {
        return;
    }

    // Set this before showing the message so concurrently launching resources cannot stack notices.
    hotReloadDisabledAdvisoryShown = true;

    try {
        await hotReloadAdvisoryWorkspaceState?.update(hotReloadDisabledAdvisoryShownKey, true);
    }
    catch (err) {
        // Still show the advisory when persistence fails so this activation retains the existing discoverability.
        // The in-memory guard continues to prevent concurrent launches from stacking notifications.
        extensionLogOutputChannel.warn(`C# Dev Kit Hot Reload advisory persistence failed: ${err instanceof Error ? err.message : String(err)}`);
    }

    try {
        const selection = await vscode.window.showInformationMessage(hotReloadDisabledNotice, openSettingsLabel);
        if (selection === openSettingsLabel) {
            await vscode.commands.executeCommand(openSettingsCommand, hotReloadSetting);
        }
    }
    catch (err) {
        extensionLogOutputChannel.warn(`C# Dev Kit Hot Reload advisory failed: ${err instanceof Error ? err.message : String(err)}`);
    }
}
