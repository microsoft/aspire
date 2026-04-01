import * as vscode from 'vscode';

import { addCommand } from './commands/add';
import { RpcClient } from './server/rpcClient';
import { InteractionService } from './server/interactionService';
import { newCommand } from './commands/new';
import { initCommand } from './commands/init';
import { deployCommand } from './commands/deploy';
import { publishCommand } from './commands/publish';
import { doCommand } from './commands/do';
import { errorMessage } from './loc/strings';
import { extensionLogOutputChannel } from './utils/logging';
import { initializeTelemetry, sendTelemetryEvent } from './utils/telemetry';
import { AspireDebugAdapterDescriptorFactory } from './debugger/AspireDebugAdapterDescriptorFactory';
import { AspireDebugConfigurationProvider } from './debugger/AspireDebugConfigurationProvider';
import { AspireExtensionContext } from './AspireExtensionContext';
import AspireRpcServer, { RpcServerConnectionInfo } from './server/AspireRpcServer';
import AspireDcpServer from './dcp/AspireDcpServer';
import { configureLaunchJsonCommand } from './commands/configureLaunchJson';
import { AspireTerminalProvider } from './utils/AspireTerminalProvider';
import { MessageConnection } from 'vscode-jsonrpc';
import { openTerminalCommand } from './commands/openTerminal';
import { updateCommand, updateSelfCommand } from './commands/update';
import { settingsCommand } from './commands/settings';
import { openLocalSettingsCommand, openGlobalSettingsCommand } from './commands/openSettings';
import { checkCliAvailableOrRedirect, checkForExistingAppHostPathInWorkspace } from './utils/workspace';
import { AspireEditorCommandProvider } from './editor/AspireEditorCommandProvider';
import { AspireAppHostTreeProvider } from './views/AspireAppHostTreeProvider';
import { AppHostDataRepository } from './views/AppHostDataRepository';
import { installCliStableCommand, installCliDailyCommand, verifyCliInstalledCommand } from './commands/walkthroughCommands';
import { AspireMcpServerDefinitionProvider } from './mcp/AspireMcpServerDefinitionProvider';
import { AspireCodeLensProvider } from './editor/AspireCodeLensProvider';
import { AspireGutterDecorationProvider } from './editor/AspireGutterDecorationProvider';
import { getSupportedLanguageIds } from './editor/parsers/AppHostResourceParser';

let aspireExtensionContext = new AspireExtensionContext();

export async function activate(context: vscode.ExtensionContext) {
  extensionLogOutputChannel.info("Activating Aspire extension");
  initializeTelemetry(context);

  const terminalProvider = new AspireTerminalProvider(context.subscriptions);

  const rpcServer = await AspireRpcServer.create(
    (rpcServerConnectionInfo: RpcServerConnectionInfo, connection: MessageConnection, token: string, debugSessionId: string | null) => {
      const client: RpcClient = new RpcClient(terminalProvider, connection, debugSessionId, () => aspireExtensionContext.getAspireDebugSession(client.debugSessionId));
      return client;
    }
  );

  const dcpServer = await AspireDcpServer.create(aspireExtensionContext.getAspireDebugSession.bind(aspireExtensionContext));

  terminalProvider.rpcServerConnectionInfo = rpcServer.connectionInfo;
  terminalProvider.dcpServerConnectionInfo = dcpServer.connectionInfo;
  terminalProvider.closeAllOpenAspireTerminals();

  const editorCommandProvider = new AspireEditorCommandProvider();

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
  const runAppHostCommandRegistration = vscode.commands.registerCommand('aspire-vscode.runAppHost', () => editorCommandProvider.tryExecuteRunAppHost(true));
  const debugAppHostCommandRegistration = vscode.commands.registerCommand('aspire-vscode.debugAppHost', () => editorCommandProvider.tryExecuteRunAppHost(false));

  // Walkthrough commands (no CLI check - CLI may not be installed yet)
  const installCliStableRegistration = vscode.commands.registerCommand('aspire-vscode.installCliStable', installCliStableCommand);
  const installCliDailyRegistration = vscode.commands.registerCommand('aspire-vscode.installCliDaily', installCliDailyCommand);
  const verifyCliInstalledRegistration = vscode.commands.registerCommand('aspire-vscode.verifyCliInstalled', verifyCliInstalledCommand);

  // Aspire panel - running app hosts tree view
  const dataRepository = new AppHostDataRepository(terminalProvider);
  const appHostTreeProvider = new AspireAppHostTreeProvider(dataRepository, terminalProvider);
  const appHostTreeView = vscode.window.createTreeView('aspire-vscode.runningAppHosts', {
    treeDataProvider: appHostTreeProvider,
    showCollapseAll: true,
  });
  appHostTreeProvider.setTreeView(appHostTreeView);

  // Global-mode polling is tied to panel visibility
  dataRepository.setPanelVisible(appHostTreeView.visible);
  appHostTreeView.onDidChangeVisibility(e => {
    dataRepository.setPanelVisible(e.visible);
  });

  const refreshRunningAppHostsRegistration = vscode.commands.registerCommand('aspire-vscode.refreshRunningAppHosts', () => dataRepository.refresh());
  const switchToGlobalViewRegistration = vscode.commands.registerCommand('aspire-vscode.switchToGlobalView', () => dataRepository.setViewMode('global'));
  const switchToWorkspaceViewRegistration = vscode.commands.registerCommand('aspire-vscode.switchToWorkspaceView', () => dataRepository.setViewMode('workspace'));
  const openDashboardRegistration = vscode.commands.registerCommand('aspire-vscode.openDashboard', (element) => appHostTreeProvider.openDashboard(element));
  const openAppHostSourceRegistration = vscode.commands.registerCommand('aspire-vscode.openAppHostSource', (element) => appHostTreeProvider.openAppHostSource(element));
  const stopAppHostRegistration = vscode.commands.registerCommand('aspire-vscode.stopAppHost', (element) => appHostTreeProvider.stopAppHost(element));
  const stopResourceRegistration = vscode.commands.registerCommand('aspire-vscode.stopResource', (element) => appHostTreeProvider.stopResource(element));
  const startResourceRegistration = vscode.commands.registerCommand('aspire-vscode.startResource', (element) => appHostTreeProvider.startResource(element));
  const restartResourceRegistration = vscode.commands.registerCommand('aspire-vscode.restartResource', (element) => appHostTreeProvider.restartResource(element));
  const viewResourceLogsRegistration = vscode.commands.registerCommand('aspire-vscode.viewResourceLogs', (element) => appHostTreeProvider.viewResourceLogs(element));
  const executeResourceCommandRegistration = vscode.commands.registerCommand('aspire-vscode.executeResourceCommand', (element) => appHostTreeProvider.executeResourceCommand(element));
  const copyEndpointUrlRegistration = vscode.commands.registerCommand('aspire-vscode.copyEndpointUrl', (element) => appHostTreeProvider.copyEndpointUrl(element));
  const openInExternalBrowserRegistration = vscode.commands.registerCommand('aspire-vscode.openInExternalBrowser', (element) => appHostTreeProvider.openInExternalBrowser(element));
  const openInSimpleBrowserRegistration = vscode.commands.registerCommand('aspire-vscode.openInSimpleBrowser', (element) => appHostTreeProvider.openInSimpleBrowser(element));
  const copyResourceNameRegistration = vscode.commands.registerCommand('aspire-vscode.copyResourceName', (element) => appHostTreeProvider.copyResourceName(element));
  const copyPidRegistration = vscode.commands.registerCommand('aspire-vscode.copyPid', (element) => appHostTreeProvider.copyPid(element));
  const copyAppHostPathRegistration = vscode.commands.registerCommand('aspire-vscode.copyAppHostPath', (element) => appHostTreeProvider.copyAppHostPath(element));
  const expandAllRegistration = vscode.commands.registerCommand('aspire-vscode.expandAll', (element) => appHostTreeProvider.expandAll(element));

  // Set initial context for welcome view
  vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', true);
  vscode.commands.executeCommand('setContext', 'aspire.loading', true);

  // Activate the data repository (starts workspace describe --follow; global polling begins when the panel is visible)
  dataRepository.activate();

  context.subscriptions.push(appHostTreeView, refreshRunningAppHostsRegistration, switchToGlobalViewRegistration, switchToWorkspaceViewRegistration, openDashboardRegistration, openAppHostSourceRegistration, stopAppHostRegistration, stopResourceRegistration, startResourceRegistration, restartResourceRegistration, viewResourceLogsRegistration, executeResourceCommandRegistration, copyEndpointUrlRegistration, openInExternalBrowserRegistration, openInSimpleBrowserRegistration, copyResourceNameRegistration, copyPidRegistration, copyAppHostPathRegistration, expandAllRegistration, { dispose: () => { appHostTreeProvider.dispose(); dataRepository.dispose(); } });

  // CodeLens provider — shows Debug on pipeline steps, resource state on resources
  const codeLensProvider = new AspireCodeLensProvider(appHostTreeProvider);
  const languageFilters = getSupportedLanguageIds().map(lang => ({ language: lang, scheme: 'file' }));
  const codeLensRegistration = vscode.languages.registerCodeLensProvider(languageFilters, codeLensProvider);
  const codeLensDebugPipelineStepRegistration = vscode.commands.registerCommand('aspire-vscode.codeLensDebugPipelineStep', (stepName: string) => editorCommandProvider.tryExecuteDoAppHost(false, stepName));
  const codeLensResourceActionRegistration = vscode.commands.registerCommand('aspire-vscode.codeLensResourceAction', (resourceName: string, action: string, appHostPath: string) => {
    let command = `resource "${resourceName}" ${action}`;
    if (appHostPath) {
      command += ` --apphost "${appHostPath}"`;
    }
    terminalProvider.sendAspireCommandToAspireTerminal(command);
  });
  const codeLensViewLogsRegistration = vscode.commands.registerCommand('aspire-vscode.codeLensViewLogs', (resourceName: string, appHostPath: string) => {
    let command = `logs "${resourceName}"`;
    if (appHostPath) {
      command += ` --apphost "${appHostPath}"`;
    }
    command += ' --follow';
    terminalProvider.sendAspireCommandToAspireTerminal(command);
  });
  const codeLensRevealResourceRegistration = vscode.commands.registerCommand('aspire-vscode.codeLensRevealResource', (resourceName: string) => {
    const element = appHostTreeProvider.findResourceElement(resourceName);
    if (element) {
      appHostTreeView.reveal(element, { select: true, focus: true });
    }
  });
  context.subscriptions.push(codeLensRegistration, codeLensDebugPipelineStepRegistration, codeLensResourceActionRegistration, codeLensViewLogsRegistration, codeLensRevealResourceRegistration, codeLensProvider);

  // Gutter decorations — colored dots next to resources showing runtime state
  const gutterDecorationProvider = new AspireGutterDecorationProvider(appHostTreeProvider);
  context.subscriptions.push(gutterDecorationProvider);

  context.subscriptions.push(cliAddCommandRegistration, cliNewCommandRegistration, cliInitCommandRegistration, cliDeployCommandRegistration, cliPublishCommandRegistration, cliDoCommandRegistration, openTerminalCommandRegistration, configureLaunchJsonCommandRegistration);
  context.subscriptions.push(cliUpdateCommandRegistration, cliUpdateSelfCommandRegistration, settingsCommandRegistration, openLocalSettingsCommandRegistration, openGlobalSettingsCommandRegistration, runAppHostCommandRegistration, debugAppHostCommandRegistration);
  context.subscriptions.push(installCliStableRegistration, installCliDailyRegistration, verifyCliInstalledRegistration);

  const debugConfigProvider = new AspireDebugConfigurationProvider();
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider('aspire', debugConfigProvider, vscode.DebugConfigurationProviderTriggerKind.Dynamic)
  );
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider('aspire', debugConfigProvider, vscode.DebugConfigurationProviderTriggerKind.Initial)
  );

  context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory('aspire', new AspireDebugAdapterDescriptorFactory(rpcServer, dcpServer, terminalProvider, aspireExtensionContext.addAspireDebugSession.bind(aspireExtensionContext), aspireExtensionContext.removeAspireDebugSession.bind(aspireExtensionContext))));

  aspireExtensionContext.initialize(rpcServer, context, debugConfigProvider, dcpServer, terminalProvider, editorCommandProvider);

  // Register Aspire MCP server definition provider so the Aspire MCP server
  // appears automatically in VS Code's MCP tools list for Aspire workspaces.
  const mcpProvider = new AspireMcpServerDefinitionProvider();
  if (typeof vscode.lm?.registerMcpServerDefinitionProvider === 'function') {
    context.subscriptions.push(vscode.lm.registerMcpServerDefinitionProvider('aspire-mcp-server', mcpProvider));
    context.subscriptions.push(mcpProvider);
    mcpProvider.refresh();
  }

  const getEnableSettingsFileCreationPromptOnStartup = () => vscode.workspace.getConfiguration('aspire').get<boolean>('enableSettingsFileCreationPromptOnStartup', true);
  const setEnableSettingsFileCreationPromptOnStartup = async (value: boolean) => await vscode.workspace.getConfiguration('aspire').update('enableSettingsFileCreationPromptOnStartup', value, vscode.ConfigurationTarget.Workspace);
  const appHostDisposablePromise = checkForExistingAppHostPathInWorkspace(
    terminalProvider,
    getEnableSettingsFileCreationPromptOnStartup,
    setEnableSettingsFileCreationPromptOnStartup
  );

  if (appHostDisposablePromise) {
    appHostDisposablePromise.then(disposable => {
      if (disposable) {
        context.subscriptions.push(disposable);
      }
    }, () => {
      // Intentionally ignore errors here to avoid impacting activation;
      // any user-visible errors should be handled within checkForExistingAppHostPathInWorkspace.
    });
  }
  // Return exported API for tests or other extensions
  return {
    rpcServerInfo: rpcServer.connectionInfo,
  };
}

export function deactivate() {
  aspireExtensionContext.dispose();
}

async function tryExecuteCommand(commandName: string, terminalProvider: AspireTerminalProvider, command: (terminalProvider: AspireTerminalProvider) => Promise<void>): Promise<void> {
  try {
    sendTelemetryEvent(`${commandName}.invoked`);

    const cliCheckExcludedCommands: string[] = ["aspire-vscode.settings", "aspire-vscode.configureLaunchJson"];

    if (!cliCheckExcludedCommands.includes(commandName)) {
      const result = await checkCliAvailableOrRedirect();
      if (!result.available) {
        return;
      }
    }

    await command(terminalProvider);
  }
  catch (error) {
    vscode.window.showErrorMessage(errorMessage(error));
  }
}
