import * as vscode from 'vscode';

const aspireConfigSection = 'aspire';

const registerMcpServerInWorkspaceSettingName = 'registerMcpServerInWorkspace';
export const registerMcpServerInWorkspaceSetting = `${aspireConfigSection}.${registerMcpServerInWorkspaceSettingName}`;

/**
 * Returns the Aspire extension configuration object.
 */
function getAspireConfig(): vscode.WorkspaceConfiguration {
    return vscode.workspace.getConfiguration(aspireConfigSection);
}

export function getRegisterMcpServerInWorkspace(): boolean {
    return getAspireConfig().get<boolean>(registerMcpServerInWorkspaceSettingName, false);
}

export function getEnableAutoRestore(): boolean {
    return getAspireConfig().get<boolean>('enableAutoRestore', false);
}

export function getAppHostDiscoveryTimeoutMs(): number {
    const timeoutMs = getAspireConfig().get<number>('appHostDiscoveryTimeoutMs', 30000);
    return Number.isFinite(timeoutMs) ? Math.max(timeoutMs, 1000) : 30000;
}
