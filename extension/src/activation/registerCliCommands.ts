import * as vscode from 'vscode';

import { addCommand } from '../commands/add';
import { newCommand } from '../commands/new';
import { initCommand } from '../commands/init';
import { deployCommand } from '../commands/deploy';
import { publishCommand } from '../commands/publish';
import { doCommand } from '../commands/do';
import { configureLaunchJsonCommand } from '../commands/configureLaunchJson';
import { openTerminalCommand } from '../commands/openTerminal';
import { updateCommand, updateSelfCommand } from '../commands/update';
import { settingsCommand } from '../commands/settings';
import { openLocalSettingsCommand, openGlobalSettingsCommand } from '../commands/openSettings';
import { installCliCommand, verifyCliInstalledCommand } from '../commands/walkthroughCommands';
import { cliNotAvailable, dismissLabel, errorMessage, openCliInstallInstructions } from '../loc/strings';
import { isCommandCancellation, withCommandTelemetry } from '../utils/telemetry';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { isE2eBridgeEnabled } from '../testing/e2eStateFileBridge';
import { registerInstrumentedCommand } from './instrumentedCommand';

export function registerCliCommands(
  terminalProvider: AspireTerminalProvider,
  editorCommandProvider: AspireEditorCommandProvider,
): vscode.Disposable[] {
  const cliAddCommandRegistration = vscode.commands.registerCommand('aspire-vscode.add', () => tryExecuteCommand('aspire-vscode.add', terminalProvider, (tp) => addCommand(tp, editorCommandProvider)));
  const cliNewCommandRegistration = vscode.commands.registerCommand('aspire-vscode.new', () => tryExecuteCommand('aspire-vscode.new', terminalProvider, newCommand));
  const cliInitCommandRegistration = vscode.commands.registerCommand('aspire-vscode.init', () => tryExecuteCommand('aspire-vscode.init', terminalProvider, initCommand));
  const cliDeployCommandRegistration = vscode.commands.registerCommand('aspire-vscode.deploy', () => tryExecuteCommand('aspire-vscode.deploy', terminalProvider, () => deployCommand(editorCommandProvider)));
  const cliPublishCommandRegistration = vscode.commands.registerCommand('aspire-vscode.publish', () => tryExecuteCommand('aspire-vscode.publish', terminalProvider, () => publishCommand(editorCommandProvider)));
  const cliDoCommandRegistration = vscode.commands.registerCommand('aspire-vscode.do', () => tryExecuteCommand('aspire-vscode.do', terminalProvider, (tp) => doCommand(tp, editorCommandProvider)));
  const cliUpdateCommandRegistration = vscode.commands.registerCommand('aspire-vscode.update', () => tryExecuteCommand('aspire-vscode.update', terminalProvider, (tp) => updateCommand(tp, editorCommandProvider)));
  const cliUpdateSelfCommandRegistration = vscode.commands.registerCommand('aspire-vscode.updateSelf', () => tryExecuteCommand('aspire-vscode.updateSelf', terminalProvider, updateSelfCommand));
  const openTerminalCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openTerminal', () => tryExecuteCommand('aspire-vscode.openTerminal', terminalProvider, openTerminalCommand));
  const configureLaunchJsonCommandRegistration = vscode.commands.registerCommand('aspire-vscode.configureLaunchJson', () => tryExecuteCommand('aspire-vscode.configureLaunchJson', terminalProvider, configureLaunchJsonCommand));
  const settingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.settings', () => tryExecuteCommand('aspire-vscode.settings', terminalProvider, settingsCommand));
  const openLocalSettingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openLocalSettings', () => tryExecuteCommand('aspire-vscode.openLocalSettings', terminalProvider, openLocalSettingsCommand));
  const openGlobalSettingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openGlobalSettings', () => tryExecuteCommand('aspire-vscode.openGlobalSettings', terminalProvider, openGlobalSettingsCommand));
  const runAppHostCommandRegistration = registerInstrumentedCommand('aspire-vscode.runAppHostCommand', 'editor', () => editorCommandProvider.tryExecuteRunAppHost(true));
  const debugAppHostCommandRegistration = registerInstrumentedCommand('aspire-vscode.debugAppHostCommand', 'editor', () => editorCommandProvider.tryExecuteRunAppHost(false));

  // Walkthrough commands (no CLI check - the CLI may not be installed yet).
  const installCliRegistration = registerInstrumentedCommand('aspire-vscode.installCli', 'walkthrough', installCliCommand);
  const verifyCliInstalledRegistration = registerInstrumentedCommand('aspire-vscode.verifyCliInstalled', 'walkthrough', verifyCliInstalledCommand);

  return [
    cliAddCommandRegistration,
    cliNewCommandRegistration,
    cliInitCommandRegistration,
    cliDeployCommandRegistration,
    cliPublishCommandRegistration,
    cliDoCommandRegistration,
    openTerminalCommandRegistration,
    configureLaunchJsonCommandRegistration,
    cliUpdateCommandRegistration,
    cliUpdateSelfCommandRegistration,
    settingsCommandRegistration,
    openLocalSettingsCommandRegistration,
    openGlobalSettingsCommandRegistration,
    runAppHostCommandRegistration,
    debugAppHostCommandRegistration,
    installCliRegistration,
    verifyCliInstalledRegistration,
  ];
}

async function tryExecuteCommand(commandName: string, terminalProvider: AspireTerminalProvider, command: (terminalProvider: AspireTerminalProvider) => Promise<void>): Promise<void> {
  try {
    await withCommandTelemetry(commandName, async () => {
      const cliCheckExcludedCommands: string[] = ["aspire-vscode.settings", "aspire-vscode.configureLaunchJson", "aspire-vscode.updateSelf"];
      if (!cliCheckExcludedCommands.includes(commandName)) {
        if (isE2eBridgeEnabled() && process.env.ASPIRE_EXTENSION_E2E_FORCE_CLI_UNAVAILABLE === 'true') {
          vscode.window.showErrorMessage(
            cliNotAvailable,
            openCliInstallInstructions,
            dismissLabel
          );
          throw new vscode.CancellationError();
        }

        const result = await checkCliAvailableOrRedirect('command_gate');
        if (!result.available) {
          // The command body never ran — the user was redirected to install the
          // CLI. Throwing a cancellation makes withCommandTelemetry record this
          // as `canceled` rather than a false `success`, and the catch below
          // suppresses the error toast (the redirect already informed the user).
          throw new vscode.CancellationError();
        }

      }

      await command(terminalProvider);
    }, { source: 'command_palette' });
  }
  catch (error) {
    // Cancellations should not surface as user-visible errors — but they still
    // bubble through the wrapper so it can classify outcome correctly.
    if (!isCommandCancellation(error)) {
      vscode.window.showErrorMessage(errorMessage(error));
    }
  }
}
