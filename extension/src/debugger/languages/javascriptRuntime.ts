import * as vscode from 'vscode';
import { ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { extensionLogOutputChannel } from "../../utils/logging";

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
    const launchMethod = config.launch_method;

    // Undefined/empty launch_method is the expected legacy signal: an older AppHost (version skew vs
    // the extension) does not emit the field, so we silently fall back to positional/runtime inference.
    if (!launchMethod) {
        return inferLegacy();
    }

    if (launchMethod === launchMethodDirect || launchMethod === launchMethodPackageManager) {
        return launchMethod;
    }

    // A non-empty but unrecognized value indicates contract drift between the hosting side and the
    // extension. We must NOT silently treat it as "direct" (that could launch the wrong command), so we
    // log a warning and fall back to inference instead.
    extensionLogOutputChannel.warn(`Unrecognized launch_method '${launchMethod}'; falling back to inference.`);
    return inferLegacy();
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
