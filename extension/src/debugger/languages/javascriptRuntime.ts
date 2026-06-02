import * as vscode from 'vscode';
import { ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";

export const jsRuntimeBaseFileTypes = ['.js', '.ts', '.mjs', '.mts', '.cjs', '.cts'];

/**
 * The resource runs via a package-manager script (e.g., `npm run dev` or `bun run start`).
 */
export const launchMethodPackageManager = 'package-manager';

/**
 * The resource runs a script file directly (e.g., `bun index.ts` or `node app.js`).
 */
export const launchMethodDirect = 'direct';

export function getJavaScriptRuntimeTargetPath(launchConfig: JavaScriptRuntimeLaunchConfiguration): string {
    return launchConfig.script_path || launchConfig.working_directory || '';
}

export function resolveJavaScriptLaunchMethod(
    config: JavaScriptRuntimeLaunchConfiguration,
    inferLegacy: () => string): string {
    return config.launch_method || inferLegacy();
}

export function getJavaScriptRuntimeDisplayName(
    launchConfig: ExecutableLaunchConfiguration,
    runtimeType: string,
    formatDisplayName: (target: string) => string,
    fallbackLabel: string): string {
    if (isJavaScriptRuntimeLaunchConfiguration(launchConfig) && launchConfig.type === runtimeType) {
        const target = getJavaScriptRuntimeTargetPath(launchConfig);
        return formatDisplayName(target ? vscode.workspace.asRelativePath(target) : 'unknown');
    }

    return fallbackLabel;
}
