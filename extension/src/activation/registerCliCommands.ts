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
import { cliNotAvailable, dismissLabel, errorMessage, noAppHostInWorkspace, openCliInstallInstructions, selectWorkspaceFolderForAspireCommand } from '../loc/strings';
import { isCommandCancellation, withCommandTelemetry } from '../utils/telemetry';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { CliPathResolutionTarget, windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { isE2eBridgeEnabled } from '../testing/e2eStateFileBridge';
import { registerInstrumentedCommand } from './instrumentedCommand';
import { AppHostCommandTarget, getAppHostArgs } from '../utils/appHostArgs';
import { getCliPathTargetForUri } from '../utils/cliPathVariables';

interface CommandInvocation {
  readonly target: CliPathResolutionTarget;
  readonly appHost?: AppHostCommandTarget;
}

export function registerCliCommands(
  terminalProvider: AspireTerminalProvider,
  editorCommandProvider: AspireEditorCommandProvider,
): vscode.Disposable[] {
  const cliAddCommandRegistration = vscode.commands.registerCommand('aspire-vscode.add', () => tryExecuteCommand('aspire-vscode.add', terminalProvider, (tp, invocation, cliPath) => addCommand(tp, editorCommandProvider, invocation.appHost ?? {}, invocation.target, cliPath), () => selectAppHostCommandInvocation(editorCommandProvider)));
  const cliNewCommandRegistration = vscode.commands.registerCommand('aspire-vscode.new', () => tryExecuteCommand('aspire-vscode.new', terminalProvider, (tp, invocation, cliPath) => newCommand(tp, invocation.target, cliPath), selectCommandInvocation));
  const cliInitCommandRegistration = vscode.commands.registerCommand('aspire-vscode.init', () => tryExecuteCommand('aspire-vscode.init', terminalProvider, (tp, invocation, cliPath) => initCommand(tp, invocation.target, cliPath), selectCommandInvocation));
  const cliDeployCommandRegistration = vscode.commands.registerCommand('aspire-vscode.deploy', () => tryExecuteCommand('aspire-vscode.deploy', terminalProvider, () => deployCommand(editorCommandProvider)));
  const cliPublishCommandRegistration = vscode.commands.registerCommand('aspire-vscode.publish', () => tryExecuteCommand('aspire-vscode.publish', terminalProvider, () => publishCommand(editorCommandProvider)));
  const cliDoCommandRegistration = vscode.commands.registerCommand('aspire-vscode.do', () => tryExecuteCommand('aspire-vscode.do', terminalProvider, (tp, invocation, cliPath) => doCommand(tp, editorCommandProvider, invocation.appHost?.appHostPath, invocation.target, cliPath), () => selectAppHostCommandInvocation(editorCommandProvider, true)));
  const cliUpdateCommandRegistration = vscode.commands.registerCommand('aspire-vscode.update', () => tryExecuteCommand('aspire-vscode.update', terminalProvider, (tp, invocation, cliPath) => updateCommand(tp, editorCommandProvider, invocation.appHost ?? {}, invocation.target, cliPath), () => selectAppHostCommandInvocation(editorCommandProvider)));
  const cliUpdateSelfCommandRegistration = vscode.commands.registerCommand('aspire-vscode.updateSelf', () => tryExecuteCommand('aspire-vscode.updateSelf', terminalProvider, updateSelfCommand));
  const openTerminalCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openTerminal', () => tryExecuteCommand('aspire-vscode.openTerminal', terminalProvider, (tp, invocation, cliPath) => openTerminalCommand(tp, invocation.target, cliPath), selectCommandInvocation));
  const configureLaunchJsonCommandRegistration = vscode.commands.registerCommand('aspire-vscode.configureLaunchJson', () => tryExecuteCommand('aspire-vscode.configureLaunchJson', terminalProvider, configureLaunchJsonCommand));
  const settingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.settings', () => tryExecuteCommand('aspire-vscode.settings', terminalProvider, settingsCommand));
  const openLocalSettingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openLocalSettings', () => tryExecuteCommand('aspire-vscode.openLocalSettings', terminalProvider, (tp, invocation, cliPath) => openLocalSettingsCommand(tp, invocation.target, cliPath), selectCommandInvocation));
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

export async function selectCommandTarget(): Promise<CliPathResolutionTarget> {
  const activeUri = vscode.window.activeTextEditor?.document.uri;
  const activeFolder = activeUri ? vscode.workspace.getWorkspaceFolder(activeUri) : undefined;
  if (activeFolder) {
    return workspaceFolderCliPathTarget(activeFolder);
  }

  const folders = vscode.workspace.workspaceFolders ?? [];
  if (folders.length === 1) {
    return workspaceFolderCliPathTarget(folders[0]);
  }
  if (folders.length > 1) {
    const selected = await vscode.window.showWorkspaceFolderPick({
      placeHolder: selectWorkspaceFolderForAspireCommand,
    });
    if (!selected) {
      throw new vscode.CancellationError();
    }
    return workspaceFolderCliPathTarget(selected);
  }

  return windowCliPathTarget;
}

async function selectCommandInvocation(): Promise<CommandInvocation> {
  return { target: await selectCommandTarget() };
}

async function selectAppHostCommandInvocation(editorCommandProvider: AspireEditorCommandProvider, requireAppHost = false): Promise<CommandInvocation> {
  const appHost = await getAppHostArgs(editorCommandProvider);
  if (!appHost.appHostPath && requireAppHost) {
    vscode.window.showErrorMessage(noAppHostInWorkspace);
    throw new vscode.CancellationError();
  }

  const target = appHost.appHostPath
    ? getCliPathTargetForUri(vscode.Uri.file(appHost.appHostPath))
    : await selectCommandTarget();
  return { target, appHost };
}

async function tryExecuteCommand(
  commandName: string,
  terminalProvider: AspireTerminalProvider,
  command: (terminalProvider: AspireTerminalProvider, invocation: CommandInvocation, cliPath: string) => Promise<void>,
  prepareInvocation: () => Promise<CommandInvocation> = async () => ({ target: windowCliPathTarget }),
): Promise<void> {
  try {
    await withCommandTelemetry(commandName, async () => {
      const invocation = await prepareInvocation();
      let cliPath = '';
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

        const result = await checkCliAvailableOrRedirect('command_gate', invocation.target);
        if (!result.available) {
          // The command body never ran — the user was redirected to install the
          // CLI. Throwing a cancellation makes withCommandTelemetry record this
          // as `canceled` rather than a false `success`, and the catch below
          // suppresses the error toast (the redirect already informed the user).
          throw new vscode.CancellationError();
        }
        cliPath = result.cliPath;

      }

      await command(terminalProvider, invocation, cliPath);
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
