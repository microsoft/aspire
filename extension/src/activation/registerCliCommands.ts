import * as vscode from 'vscode';

import { addCommand } from '../commands/add';
import { newCommand } from '../commands/new';
import { initCommand } from '../commands/init';
import { createWithAspireCommand } from '../commands/createWithAspire';
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
import { classifyError, type HandledCommandOutcome, isCommandCancellation, withCommandTelemetry } from '../utils/telemetry';
import { checkCliAvailableOrRedirect } from '../utils/workspace';
import { CliPathResolutionTarget, windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { isE2eBridgeEnabled } from '../testing/e2eStateFileBridge';
import { registerInstrumentedCommand } from './instrumentedCommand';
import { AppHostCommandTarget, getAppHostArgs } from '../utils/appHostArgs';
import { getCliPathTargetForUri } from '../utils/cliPathVariables';

interface CommandInvocation {
  readonly target: CliPathResolutionTarget;
  readonly appHost?: AppHostCommandTarget;
  readonly cliPath?: string;
}

type CommandSource = 'command_palette' | 'tree';
const cliCheckExcludedCommands = new Set([
  'aspire-vscode.settings',
  'aspire-vscode.configureLaunchJson',
  'aspire-vscode.updateSelf',
]);
const cliCheckDeferredCommands = new Set([
  'aspire-vscode.deploy',
  'aspire-vscode.publish',
]);

export function registerCliCommands(
  terminalProvider: AspireTerminalProvider,
  editorCommandProvider: AspireEditorCommandProvider,
  configInfoProvider: ConfigInfoProvider = new ConfigInfoProvider(terminalProvider),
): vscode.Disposable[] {
  const cliAddCommandRegistration = vscode.commands.registerCommand('aspire-vscode.add', () => tryExecuteCommand('aspire-vscode.add', terminalProvider, (tp, invocation, cliPath) => addCommand(tp, editorCommandProvider, invocation.appHost ?? {}, invocation.target, cliPath), () => selectAppHostCommandInvocation(editorCommandProvider)));
  const cliNewCommandRegistration = vscode.commands.registerCommand('aspire-vscode.new', (source: CommandSource = 'command_palette') => tryExecuteCommand('aspire-vscode.new', terminalProvider, (tp, invocation, cliPath) => newCommand(tp, invocation.target, cliPath), selectCommandInvocation, source));
  const cliInitCommandRegistration = vscode.commands.registerCommand('aspire-vscode.init', (target?: CliPathResolutionTarget, source: CommandSource = 'command_palette') => tryExecuteCommand('aspire-vscode.init', terminalProvider, (tp, invocation, cliPath) => initCommand(tp, invocation.target, cliPath), target ? async () => ({ target }) : selectCommandInvocation, source));
  // Delegates to aspire-vscode.new / aspire-vscode.init above, so it doesn't go
  // through tryExecuteCommand itself — the delegated-to command owns its own CLI
  // availability check and telemetry.
  const createWithAspireCommandRegistration = registerInstrumentedCommand('aspire-vscode.createWithAspire', 'tree', createWithAspireCommand);
  const cliDeployCommandRegistration = vscode.commands.registerCommand('aspire-vscode.deploy', () => tryExecuteCommand('aspire-vscode.deploy', terminalProvider, () => deployCommand(editorCommandProvider)));
  const cliPublishCommandRegistration = vscode.commands.registerCommand('aspire-vscode.publish', () => tryExecuteCommand('aspire-vscode.publish', terminalProvider, () => publishCommand(editorCommandProvider)));
  const cliDoCommandRegistration = vscode.commands.registerCommand('aspire-vscode.do', () => tryExecuteCommand('aspire-vscode.do', terminalProvider, (_tp, invocation, cliPath) => doCommand(configInfoProvider, editorCommandProvider, invocation.appHost?.appHostPath, invocation.target, cliPath), () => selectAppHostCommandInvocation(editorCommandProvider, true)));
  const cliUpdateCommandRegistration = vscode.commands.registerCommand('aspire-vscode.update', () => tryExecuteCommand('aspire-vscode.update', terminalProvider, (tp, invocation, cliPath) => updateCommand(tp, editorCommandProvider, invocation.appHost ?? {}, invocation.target, cliPath), () => selectAppHostCommandInvocation(editorCommandProvider)));
  const cliUpdateSelfCommandRegistration = vscode.commands.registerCommand('aspire-vscode.updateSelf', (target: CliPathResolutionTarget = windowCliPathTarget, cliPath?: string) =>
    tryExecuteCommand(
      'aspire-vscode.updateSelf',
      terminalProvider,
      (tp, invocation, resolvedCliPath) => updateSelfCommand(tp, invocation.target, resolvedCliPath || undefined),
      async () => ({ target, cliPath })));
  const openTerminalCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openTerminal', () => tryExecuteCommand('aspire-vscode.openTerminal', terminalProvider, (tp, invocation, cliPath) => openTerminalCommand(tp, invocation.target, cliPath), selectCommandInvocation));
  const configureLaunchJsonCommandRegistration = vscode.commands.registerCommand('aspire-vscode.configureLaunchJson', () => tryExecuteCommand('aspire-vscode.configureLaunchJson', terminalProvider, configureLaunchJsonCommand));
  const settingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.settings', () => tryExecuteCommand('aspire-vscode.settings', terminalProvider, settingsCommand));
  const openLocalSettingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openLocalSettings', () => tryExecuteCommand('aspire-vscode.openLocalSettings', terminalProvider, (tp, invocation, cliPath) => openLocalSettingsCommand(tp, invocation.target, cliPath), selectCommandInvocation));
  const openGlobalSettingsCommandRegistration = vscode.commands.registerCommand('aspire-vscode.openGlobalSettings', () => tryExecuteCommand('aspire-vscode.openGlobalSettings', terminalProvider, openGlobalSettingsCommand));
  const runAppHostCommandRegistration = registerInstrumentedCommand('aspire-vscode.runAppHostCommand', 'editor', (resource?: vscode.Uri) => editorCommandProvider.tryExecuteRunAppHost(true, resource));
  const debugAppHostCommandRegistration = registerInstrumentedCommand('aspire-vscode.debugAppHostCommand', 'editor', (resource?: vscode.Uri) => editorCommandProvider.tryExecuteRunAppHost(false, resource));
  const runAppHostFromExplorerRegistration = registerInstrumentedCommand('aspire-vscode.runAppHostFromExplorer', 'editor', (resource?: vscode.Uri) => editorCommandProvider.tryExecuteRunAppHost(true, resource, false));
  const debugAppHostFromExplorerRegistration = registerInstrumentedCommand('aspire-vscode.debugAppHostFromExplorer', 'editor', (resource?: vscode.Uri) => editorCommandProvider.tryExecuteRunAppHost(false, resource, false));
  const runAppHostFromEditorCommandRegistration = registerInstrumentedCommand('aspire-vscode.runAppHostFromEditorCommand', 'editor', () => editorCommandProvider.tryExecuteRunAppHost(true));
  const debugAppHostFromEditorCommandRegistration = registerInstrumentedCommand('aspire-vscode.debugAppHostFromEditorCommand', 'editor', () => editorCommandProvider.tryExecuteRunAppHost(false));

  // Walkthrough commands (no CLI check - the CLI may not be installed yet).
  const installCliRegistration = registerInstrumentedCommand('aspire-vscode.installCli', 'walkthrough', installCliCommand);
  const verifyCliInstalledRegistration = registerInstrumentedCommand('aspire-vscode.verifyCliInstalled', 'walkthrough', verifyCliInstalledCommand);

  return [
    cliAddCommandRegistration,
    cliNewCommandRegistration,
    cliInitCommandRegistration,
    createWithAspireCommandRegistration,
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
    runAppHostFromExplorerRegistration,
    debugAppHostFromExplorerRegistration,
    runAppHostFromEditorCommandRegistration,
    debugAppHostFromEditorCommandRegistration,
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
  source: CommandSource = 'command_palette',
): Promise<HandledCommandOutcome | undefined> {
  try {
    await withCommandTelemetry(commandName, async () => {
      const invocation = await prepareInvocation();
      let cliPath = invocation.cliPath ?? '';
      if (!cliCheckExcludedCommands.has(commandName)) {
        if (isE2eBridgeEnabled() && process.env.ASPIRE_EXTENSION_E2E_FORCE_CLI_UNAVAILABLE === 'true') {
          vscode.window.showErrorMessage(
            cliNotAvailable,
            openCliInstallInstructions,
            dismissLabel
          );
          throw new vscode.CancellationError();
        }

        if (!cliCheckDeferredCommands.has(commandName)) {
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
      }

      await command(terminalProvider, invocation, cliPath);
    }, { source });
  }
  catch (error) {
    if (isCommandCancellation(error)) {
      return { success: false, canceled: true };
    }

    vscode.window.showErrorMessage(errorMessage(error));
    return { success: false, errorKind: classifyError(error) };
  }
}
