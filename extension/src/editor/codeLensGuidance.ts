import * as vscode from 'vscode';
import { codeLensGuidanceWorkspaceOverride, codeLensHideInWorkspaceSettings } from '../loc/strings';

/** Settings under `aspire` that each gate one family of advisory CodeLens. */
export const codeLensGuidanceSettings = [
    'enableCodeLensSpringBootGuidance',
    'enableCodeLensJavaLaunchGuidance',
    'enableCodeLensRustLaunchGuidance',
    'enableCodeLensDebuggerHints',
] as const;

export type CodeLensGuidanceSetting = typeof codeLensGuidanceSettings[number];

export function isCodeLensGuidanceEnabled(setting: CodeLensGuidanceSetting): boolean {
    return vscode.workspace.getConfiguration('aspire').get<boolean>(setting, true);
}

/**
 * Turns off one guidance family in User settings. A workspace value of `true` would keep it visible,
 * so in that case the user is asked before the workspace configuration is touched.
 */
export async function hideCodeLensGuidance(setting: string): Promise<void> {
    if (!(codeLensGuidanceSettings as readonly string[]).includes(setting)) {
        return;
    }

    const configuration = vscode.workspace.getConfiguration('aspire');
    const workspaceOverrides = configuration.inspect<boolean>(setting)?.workspaceValue === true;

    await configuration.update(setting, false, vscode.ConfigurationTarget.Global);

    if (!workspaceOverrides) {
        return;
    }

    const choice = await vscode.window.showWarningMessage(codeLensGuidanceWorkspaceOverride(`aspire.${setting}`), codeLensHideInWorkspaceSettings);
    if (choice === codeLensHideInWorkspaceSettings) {
        await configuration.update(setting, false, vscode.ConfigurationTarget.Workspace);
    }
}
