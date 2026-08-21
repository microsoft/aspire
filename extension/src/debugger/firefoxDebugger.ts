import * as vscode from 'vscode';
import { firefoxDebuggerExtensionId, isFirefoxDebuggerInstalled } from '../capabilities';
import { firefoxDebuggerNotInstalled, installLabel } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

export const firefoxDebugAdapterType = 'firefox';

export function promptToInstallFirefoxDebugger(): void {
    extensionLogOutputChannel.info(`Firefox debug adapter requested but the ${firefoxDebuggerExtensionId} extension is not installed.`);
    void Promise.resolve(vscode.window.showErrorMessage(firefoxDebuggerNotInstalled, installLabel)).then(async selection => {
        if (selection === installLabel) {
            await vscode.commands.executeCommand('workbench.extensions.installExtension', firefoxDebuggerExtensionId);
        }
    }).catch((error: unknown) => {
        extensionLogOutputChannel.warn(`Failed to install Firefox Debugger extension '${firefoxDebuggerExtensionId}': ${error instanceof Error ? error.message : String(error)}`);
    });
}

export { isFirefoxDebuggerInstalled };
