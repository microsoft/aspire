import * as vscode from 'vscode';
import { isCsDevKitInstalled } from '../capabilities';
import { extensionLogOutputChannel } from '../utils/logging';
import { hotReloadActiveNotice, hotReloadActiveNoticeSaveDisabled, hotReloadDisabledNotice, showHotReloadOutputLabel } from '../loc/strings';
import { isNotificationSuppressed, showInformationMessageWithDontShowAgain } from '../utils/notificationSuppression';

const hotReloadConfigurationSection = 'csharp.experimental.debug';
const hotReloadConfigurationName = 'hotReload';
const hotReloadOnSaveConfigurationSection = 'csharp.debug';
const hotReloadOnSaveConfigurationName = 'hotReloadOnSave';

const hotReloadDisabledNoticeName = 'hotReload.disabledNoticeV1';
const hotReloadActiveNoticeName = 'hotReload.activeNoticeV1';
const showHotReloadPanelCommand = 'csdevkit.debug.showHotReloadPanel';

export interface HotReloadDiagnostics {
    devKitInstalled: boolean;
    workspaceTrusted: boolean;
    settingContributed: boolean;
    settingEnabled: boolean;
    reloadOnSaveEnabled: boolean;
}

export function isHotReloadSettingEnabled(): boolean {
    return vscode.workspace.getConfiguration(hotReloadConfigurationSection).get<boolean>(hotReloadConfigurationName) === true;
}

function isHotReloadSettingContributed(): boolean {
    return vscode.workspace
        .getConfiguration(hotReloadConfigurationSection)
        .inspect<boolean>(hotReloadConfigurationName)?.defaultValue !== undefined;
}

export function isHotReloadOnSaveEnabled(): boolean {
    return vscode.workspace.getConfiguration(hotReloadOnSaveConfigurationSection).get<boolean>(hotReloadOnSaveConfigurationName) !== false;
}

export function getHotReloadDiagnostics(): HotReloadDiagnostics {
    return {
        devKitInstalled: isCsDevKitInstalled(),
        workspaceTrusted: vscode.workspace.isTrusted,
        settingContributed: isHotReloadSettingContributed(),
        settingEnabled: isHotReloadSettingEnabled(),
        reloadOnSaveEnabled: isHotReloadOnSaveEnabled()
    };
}

function isHotReloadExpected(diagnostics: HotReloadDiagnostics): boolean {
    return diagnostics.devKitInstalled
        && diagnostics.workspaceTrusted
        && diagnostics.settingContributed
        && diagnostics.settingEnabled;
}

export function logHotReloadDiagnostics(resourceName: string, diagnostics: HotReloadDiagnostics, isDebugSession: boolean): void {
    if (!diagnostics.devKitInstalled) {
        return;
    }

    extensionLogOutputChannel.info(
        `Hot Reload state for ${resourceName}: workspaceTrusted=${diagnostics.workspaceTrusted}, ` +
        `settingContributed=${diagnostics.settingContributed}, ` +
        `csharp.experimental.debug.hotReload=${diagnostics.settingEnabled}, ` +
        `csharp.debug.hotReloadOnSave=${diagnostics.reloadOnSaveEnabled}`);

    if (!diagnostics.workspaceTrusted) {
        extensionLogOutputChannel.info(
            'The workspace is not trusted, so C# Dev Kit activates in limited mode and Hot Reload is unavailable.');
    }

    if (!diagnostics.settingContributed) {
        extensionLogOutputChannel.info(
            `'${hotReloadConfigurationSection}.${hotReloadConfigurationName}' is not contributed by any installed extension, so Hot Reload cannot be reported.`);
    }

    if (diagnostics.workspaceTrusted && diagnostics.settingContributed && !diagnostics.settingEnabled) {
        extensionLogOutputChannel.info(
            "Hot Reload is disabled because 'csharp.experimental.debug.hotReload' is not enabled in user settings.");
    }

    if (!isHotReloadExpected(diagnostics)) {
        return;
    }

    if (!isDebugSession) {
        extensionLogOutputChannel.info(
            `${resourceName} is running without a debugger, so Hot Reload does not apply to it.`);
        return;
    }

    const gesture = diagnostics.reloadOnSaveEnabled
        ? "Saving a file asks Dev Kit to apply the edit ('csharp.debug.hotReloadOnSave'); the toolbar button applies pending edits"
        : "'csharp.debug.hotReloadOnSave' is off, so saving does not apply edits; the toolbar button applies pending edits";

    extensionLogOutputChannel.info(
        `Hot Reload covers ${resourceName}. ${gesture} across .NET resources at once. ` +
        "Dev Kit reports what it actually applied in the '.NET Hot Reload' output channel.");
}

let hotReloadNotificationState: vscode.Memento | undefined;
let hotReloadNotificationStateMissingLogged = false;
const hotReloadNotificationsShownThisWindow = new Set<string>();

export function initializeHotReloadNotificationState(context: { globalState: vscode.Memento } | undefined): void {
    hotReloadNotificationState = context?.globalState;
    hotReloadNotificationStateMissingLogged = false;
    hotReloadNotificationsShownThisWindow.clear();
}

export function showHotReloadNotificationIfNeeded(diagnostics: HotReloadDiagnostics, isDebugSession: boolean): void {
    if (!isDebugSession) {
        return;
    }

    const notice = getHotReloadNotice(diagnostics);
    if (!notice || hotReloadNotificationsShownThisWindow.has(notice.name) || isNotificationSuppressed(hotReloadNotificationState, notice.name)) {
        return;
    }

    hotReloadNotificationsShownThisWindow.add(notice.name);

    if (hotReloadNotificationState === undefined && !hotReloadNotificationStateMissingLogged) {
        hotReloadNotificationStateMissingLogged = true;
        extensionLogOutputChannel.warn('Hot Reload notification state was never initialized; a dismissal will not persist across windows.');
    }

    void (async () => {
        try {
            const selection = await showInformationMessageWithDontShowAgain({
                memento: hotReloadNotificationState,
                notificationName: notice.name,
                message: notice.message,
                items: notice.actions
            });

            if (selection === showHotReloadOutputLabel) {
                await vscode.commands.executeCommand(showHotReloadPanelCommand);
            }
        }
        catch (err) {
            extensionLogOutputChannel.warn(`Hot Reload notification failed: ${err instanceof Error ? err.message : String(err)}`);
        }
    })();
}

function getHotReloadNotice(diagnostics: HotReloadDiagnostics): { name: string; message: string; actions: string[] } | undefined {
    if (!diagnostics.devKitInstalled || !diagnostics.workspaceTrusted || !diagnostics.settingContributed) {
        return undefined;
    }

    if (!diagnostics.settingEnabled) {
        return { name: hotReloadDisabledNoticeName, message: hotReloadDisabledNotice, actions: [] };
    }

    const message = diagnostics.reloadOnSaveEnabled ? hotReloadActiveNotice : hotReloadActiveNoticeSaveDisabled;
    return { name: hotReloadActiveNoticeName, message, actions: [showHotReloadOutputLabel] };
}
