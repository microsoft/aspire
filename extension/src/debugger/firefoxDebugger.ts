import * as vscode from 'vscode';
import { firefoxDebuggerExtensionId, isFirefoxDebuggerInstalled } from '../capabilities';
import { firefoxDebuggerNotInstalled, installLabel } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

// The Firefox debug adapter type emitted in debug configurations. Unlike 'pwa-chrome'
// and 'pwa-msedge' (built into VS Code via js-debug), 'firefox' is contributed by the
// firefox-devtools.vscode-firefox-debug extension, so it is only registered when that
// extension is installed.
export const firefoxDebugAdapterType = 'firefox';

/**
 * Shows an actionable error offering to install the Firefox Debugger extension.
 *
 * This lives in its own module (rather than in browser.ts) to avoid an import cycle:
 * browser.ts -> debuggerExtensions -> dotnet -> AspireDebugSession, and AspireDebugSession
 * also needs this helper for the dashboard Firefox launch path.
 */
export function promptToInstallFirefoxDebugger(): void {
    extensionLogOutputChannel.info(`Firefox debug adapter requested but the ${firefoxDebuggerExtensionId} extension is not installed.`);
    void vscode.window.showErrorMessage(firefoxDebuggerNotInstalled, installLabel).then(async selection => {
        if (selection === installLabel) {
            // Installs the extension by id and opens it in the Extensions view.
            await vscode.commands.executeCommand('workbench.extensions.installExtension', firefoxDebuggerExtensionId);
        }
    });
}

export { isFirefoxDebuggerInstalled };
