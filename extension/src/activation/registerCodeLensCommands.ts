import * as vscode from 'vscode';

import { extensionLogOutputChannel } from '../utils/logging';
import { AspireTerminalProvider, shellArg } from '../utils/AspireTerminalProvider';
import { AspireCodeLensProvider } from '../editor/AspireCodeLensProvider';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { getSupportedLanguageIds } from '../editor/parsers/AppHostResourceParser';
import { getPlainTextScannableLanguageIds } from '../editor/parsers/plainTextInactiveOffsets';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { isEnabledCommand } from '../views/treePresentation';
import { collectResourceCommandArguments } from '../views/ResourceCommandArguments';
import { createResourceCommandArgumentLoader } from '../views/ResourceCommandArgumentsLoader';
import { executeResourceCommand } from '../views/resourceCommandExecution';
import { AppHostDataRepository, isMatchingAppHostPath, ResourceCommandJson } from '../data/AppHostDataRepository';
import { DebuggerInstallHint, DebuggerInstallHintService } from '../debugger/debuggerInstallHints';
import { registerInstrumentedCommand } from './instrumentedCommand';
import { getCliPathTargetForUri, windowCliPathTarget } from '../utils/cliPathVariables';

export function registerCodeLensCommands(
  appHostTreeProvider: AspireAppHostTreeProvider,
  appHostTreeView: vscode.TreeView<unknown>,
  dataRepository: AppHostDataRepository,
  terminalProvider: AspireTerminalProvider,
  editorCommandProvider: AspireEditorCommandProvider,
  secretWarningState: vscode.Memento,
): vscode.Disposable[] {
  const codeLensProvider = new AspireCodeLensProvider(appHostTreeProvider, dataRepository);
  // Languages without a resource parser are registered too. They produce no state or action lenses,
  // but they can declare a Java resource that launches through Spring Boot, and that warning is the
  // one lens that does not need a parsed resource model.
  const lensLanguageIds = new Set([...getSupportedLanguageIds(), ...getPlainTextScannableLanguageIds()]);
  const languageFilters = [...lensLanguageIds].map(lang => ({ language: lang, scheme: 'file' }));
  const codeLensRegistration = vscode.languages.registerCodeLensProvider(languageFilters, codeLensProvider);
  const debuggerInstallHintService = new DebuggerInstallHintService(secretWarningState);
  const debuggerInstallHintObservation = debuggerInstallHintService.watchForMissingDebuggers(dataRepository);
  const installDebuggerExtensionRegistration = registerInstrumentedCommand(
    'aspire-vscode.installDebuggerExtension',
    'codelens',
    (hint: DebuggerInstallHint) => debuggerInstallHintService.installDebuggerExtension(hint));
  const codeLensDebugPipelineStepRegistration = registerInstrumentedCommand('aspire-vscode.codeLensDebugPipelineStep', 'codelens', (stepName: string) => editorCommandProvider.tryExecuteDoAppHost(false, stepName));
  const codeLensResourceActionRegistration = registerInstrumentedCommand('aspire-vscode.codeLensResourceAction', 'codelens', async (resourceName: string, action: string, appHostPath: string, resourceCommand?: ResourceCommandJson) => {
    const effectiveResourceCommand = getCurrentResourceCommand(dataRepository, resourceName, action, appHostPath) ?? resourceCommand;
    if (effectiveResourceCommand !== undefined && !isEnabledCommand(effectiveResourceCommand)) {
      extensionLogOutputChannel.warn(`Ignoring disabled CodeLens resource command '${action}' for resource '${resourceName}'.`);
      return;
    }

    const commandArguments = await collectResourceCommandArguments(action, effectiveResourceCommand, {
      secretWarningState,
      loadDynamicArguments: createResourceCommandArgumentLoader({
        cliExecutionProvider: terminalProvider,
        resourceName,
        commandName: action,
        appHostPath: appHostPath || undefined,
      }),
    });
    if (commandArguments === undefined) {
      return;
    }

    // Execute over the hidden CLI backchannel and surface the result inside VS Code, rather than
    // typing `aspire resource ...` into the visible terminal. Returned values are rendered through
    // the tree provider's read-only output document.
    return await executeResourceCommand(
      dataRepository,
      (resource, command, content, outputAppHostPath) =>
        appHostTreeProvider.showResourceCommandOutput(resource, command, content, outputAppHostPath),
      {
        resourceName,
        commandName: action,
        appHostPath: appHostPath || undefined,
        additionalArgs: commandArguments.args,
      });
  });
  const codeLensViewLogsRegistration = registerInstrumentedCommand('aspire-vscode.codeLensViewLogs', 'codelens', (resourceName: string, appHostPath: string) => {
    const command = appHostPath
      ? ['logs', shellArg(resourceName), '--apphost', shellArg(appHostPath), '--follow']
      : ['logs', shellArg(resourceName), '--follow'];
    const target = appHostPath
      ? getCliPathTargetForUri(vscode.Uri.file(appHostPath))
      : windowCliPathTarget;
    terminalProvider.sendAspireCommandToAspireTerminal(command, true, undefined, { target });
  });
  const codeLensRevealResourceRegistration = registerInstrumentedCommand('aspire-vscode.codeLensRevealResource', 'codelens', (resourceName: string, appHostPath?: string) => {
    const element = appHostTreeProvider.findResourceElement(resourceName, appHostPath);
    if (element) {
      appHostTreeView.reveal(element, { select: true, focus: true });
    }
  });
  const codeLensRevealAppHostRegistration = registerInstrumentedCommand('aspire-vscode.codeLensRevealAppHost', 'codelens', (appHostPath: string) => {
    const element = appHostTreeProvider.findAppHostElement(appHostPath);
    if (element) {
      return appHostTreeView.reveal(element, { select: true, focus: true, expand: true });
    }
  });
  const codeLensOpenDashboardRegistration = registerInstrumentedCommand('aspire-vscode.codeLensOpenDashboard', 'codelens', (appHostPath?: string) => {
    const element = appHostPath ? appHostTreeProvider.findAppHostElement(appHostPath) : undefined;
    return appHostTreeProvider.openDashboard(element);
  });
  const codeLensViewAppHostLogsRegistration = registerInstrumentedCommand('aspire-vscode.codeLensViewAppHostLogs', 'codelens', (appHostPath?: string) => {
    const additionalArgs: string[] = [];
    if (appHostPath) {
      additionalArgs.push('--apphost', appHostPath);
    }
    additionalArgs.push('--follow');
    const target = appHostPath
      ? getCliPathTargetForUri(vscode.Uri.file(appHostPath))
      : windowCliPathTarget;
    terminalProvider.sendAspireCommandToAspireTerminal('logs', true, additionalArgs, { target });
  });

  return [
    codeLensRegistration,
    installDebuggerExtensionRegistration,
    codeLensDebugPipelineStepRegistration,
    codeLensResourceActionRegistration,
    codeLensViewLogsRegistration,
    codeLensRevealResourceRegistration,
    codeLensRevealAppHostRegistration,
    codeLensOpenDashboardRegistration,
    codeLensViewAppHostLogsRegistration,
    codeLensProvider,
    debuggerInstallHintObservation,
  ];
}

function getCurrentResourceCommand(dataRepository: AppHostDataRepository, resourceName: string, commandName: string, appHostPath: string | undefined): ResourceCommandJson | undefined {
  const resources = dataRepository.viewMode === 'workspace'
    && (!appHostPath || isMatchingAppHostPath(dataRepository.workspaceAppHostPath, appHostPath))
    ? dataRepository.workspaceResources
    : dataRepository.appHosts.find(appHost => isMatchingAppHostPath(appHost.appHostPath, appHostPath))?.resources ?? [];
  const resource = resources.find(candidate => candidate.name === resourceName || candidate.displayName === resourceName);

  return resource?.commands?.[commandName] ?? undefined;
}
