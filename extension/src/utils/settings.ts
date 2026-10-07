import * as vscode from 'vscode';

const aspireConfigSection = 'aspire';

const registerMcpServerInWorkspaceSettingName = 'registerMcpServerInWorkspace';
export const registerMcpServerInWorkspaceSetting = `${aspireConfigSection}.${registerMcpServerInWorkspaceSettingName}`;

/**
 * Returns the Aspire extension configuration object.
 */
function getAspireConfig(scope?: vscode.ConfigurationScope): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration(aspireConfigSection, scope);
}

/**
 * Reads the explicit tri-state value of `aspire.registerMcpServerInWorkspace` for a scope.
 *
 * `get()` cannot answer this question: it folds the contributed default into its result, so an
 * untouched setting is indistinguishable from a deliberate opt-out. Only `inspect()` reports the
 * values a user actually wrote, and the contributed default is deliberately ignored here so that
 * "never configured" can mean automatic registration while `false` stays an explicit opt-out.
 *
 * The narrowest written value wins, matching how VS Code resolves settings: folder, then
 * workspace, then user.
 *
 * @param scope The resource whose folder/workspace settings apply; omit for the window scope.
 * @returns `true`/`false` when the user configured the setting, otherwise `undefined`.
 */
export function getRegisterMcpServerInWorkspaceOverride(scope?: vscode.ConfigurationScope): boolean | undefined {
    const inspection = getAspireConfig(scope).inspect<boolean>(registerMcpServerInWorkspaceSettingName);
    return inspection?.workspaceFolderValue ?? inspection?.workspaceValue ?? inspection?.globalValue;
}

export function getEnableAutoRestore(): boolean {
    return getAspireConfig().get<boolean>('enableAutoRestore', false);
}

export function getAppHostDiscoveryTimeoutMs(): number {
    const timeoutMs = getAspireConfig().get<number>('appHostDiscoveryTimeoutMs', 30000);
    return Number.isFinite(timeoutMs) ? Math.max(timeoutMs, 1000) : 30000;
}
