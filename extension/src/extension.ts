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
import { initializeTelemetry, isCommandCancellation, withCommandTelemetry } from './utils/telemetry';
import { MeaningfulEngagementReporter } from './utils/meaningfulEngagement';
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
import { AspirePackageRestoreProvider } from './utils/AspirePackageRestoreProvider';
import { AspireAppHostTreeProvider } from './views/AspireAppHostTreeProvider';
import { AppHostDataRepository } from './views/AppHostDataRepository';
import { installCliStableCommand, installCliDailyCommand, verifyCliInstalledCommand } from './commands/walkthroughCommands';
import { AspireMcpServerDefinitionProvider } from './mcp/AspireMcpServerDefinitionProvider';
import { AspireCodeLensProvider } from './editor/AspireCodeLensProvider';
import { AspireGutterDecorationProvider } from './editor/AspireGutterDecorationProvider';
import { AppHostFilePresenceWatcher } from './editor/AppHostFilePresenceWatcher';
import { getSupportedLanguageIds } from './editor/parsers/AppHostResourceParser';
import { readGitCommitSha } from './utils/versionInfo';
import { collectResourceCommandArguments } from './views/ResourceCommandArguments';
import { createResourceCommandArgumentLoader } from './views/ResourceCommandArgumentsLoader';
import { ResourceCommandJson } from './views/AppHostDataRepository';
import { AppHostDiscoveryService } from './utils/appHostDiscovery';
import { AppHostLaunchService } from './services/AppHostLaunchService';
import { AppHostsViewTelemetry } from './views/AppHostsViewTelemetry';

let aspireExtensionContext = new AspireExtensionContext();

export async function activate(context: vscode.ExtensionContext) {
  const gitCommitSha = readGitCommitSha(context);
  extensionLogOutputChannel.info(`Activating Aspire extension (commit: ${gitCommitSha})`);
  initializeTelemetry(context);

  const terminalProvider = new AspireTerminalProvider(context.subscriptions);

  const rpcServer = await AspireRpcServer.create(
    (rpcServerConnectionInfo: RpcServerConnectionInfo, connection: MessageConnection, token: string, debugSessionId: string | null) => {
      const client: RpcClient = new RpcClient(terminalProvider, connection, debugSessionId, () => aspireExtensionContext.getAspireDebugSession(client.debugSessionId));
      return client;
    }
  );

  // Declared up front so DCP-server hooks can reference it through a closure;
  // the actual instance is created after discovery service is available.
  let engagement: MeaningfulEngagementReporter | undefined;

  const dcpServer = await AspireDcpServer.create(
    aspireExtensionContext.getAspireDebugSession.bind(aspireExtensionContext),
    {
      onRunSessionAccepted: () => engagement?.recordDebugSession(),
    },
  );

  terminalProvider.rpcServerConnectionInfo = rpcServer.connectionInfo;
  terminalProvider.dcpServerConnectionInfo = dcpServer.connectionInfo;
  terminalProvider.closeAllOpenAspireTerminals();

  const appHostDiscoveryService = new AppHostDiscoveryService(terminalProvider);
  context.subscriptions.push(appHostDiscoveryService);

  // Meaningful-engagement reporter must outlive every command callback so it
  // can observe the first invocation. Wire it before any command is
  // registered so even synchronous early invocations (rare but possible) are
  // observed via the telemetry pipeline.
  engagement = new MeaningfulEngagementReporter(appHostDiscoveryService);
  context.subscriptions.push(engagement);

  const appHostLaunchService = new AppHostLaunchService();
  context.subscriptions.push(appHostLaunchService);

  const editorCommandProvider = new AspireEditorCommandProvider(appHostDiscoveryService, appHostLaunchService);

  /**
   * Adapter around vscode.commands.registerCommand that routes the callback
   * through {@link withCommandTelemetry} so every command invocation gets
   * outcome / duration / error_kind telemetry without changing call sites
   * across the file. Use this for command implementations that bypass
   * tryExecuteCommand (e.g., tree-view commands, code lens commands,
   * walkthrough commands) — tryExecuteCommand already wraps its callers.
   *
   * `source` distinguishes invocation sites we can statically classify
   * (`tree`, `codelens`, `walkthrough`); palette is the default and is
   * already used by tryExecuteCommand-wrapped commands.
   */
  function registerInstrumentedCommand(
    commandName: string,
    source: 'tree' | 'codelens' | 'walkthrough' | 'editor',
    // The signature mirrors vscode.commands.registerCommand which accepts
    // `(...args: any[]) => any`. Using `any` here preserves the inline
    // lambda parameter inference at the call sites (otherwise a generic
    // would default to `unknown[]` and force callers to annotate every
    // parameter just to satisfy the wrapper).
    fn: (...args: any[]) => any,
  ): vscode.Disposable {
    return vscode.commands.registerCommand(commandName, async (...args) => {
      try {
        return await withCommandTelemetry(commandName, () => fn(...args), { source });
      }
      catch (error) {
        if (isCommandCancellation(error)) {
          return undefined;
        }

        throw error;
      }
    });
  }

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

  // Walkthrough commands (no CLI check - CLI may not be installed yet)
  const installCliStableRegistration = registerInstrumentedCommand('aspire-vscode.installCliStable', 'walkthrough', installCliStableCommand);
  const installCliDailyRegistration = registerInstrumentedCommand('aspire-vscode.installCliDaily', 'walkthrough', installCliDailyCommand);
  const verifyCliInstalledRegistration = registerInstrumentedCommand('aspire-vscode.verifyCliInstalled', 'walkthrough', verifyCliInstalledCommand);

  // Aspire panel - running app hosts tree view
  const dataRepository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService);
  const appHostTreeProvider = new AspireAppHostTreeProvider(dataRepository, terminalProvider, appHostLaunchService, context.globalState);
  const appHostTreeView = vscode.window.createTreeView('aspire-vscode.appHosts', {
    treeDataProvider: appHostTreeProvider,
    showCollapseAll: true,
  });
  appHostTreeProvider.setTreeView(appHostTreeView);

  // Running AppHosts data sources are tied to panel visibility.
  dataRepository.setPanelVisible(appHostTreeView.visible);
  appHostTreeView.onDidChangeVisibility(e => {
    dataRepository.setPanelVisible(e.visible);
  });

  // Also drive data sources based on whether an AppHost file is currently visible in any editor.
  // This makes resource code-lens decorations on a fresh AppHost file work without first opening the panel.
  const appHostFilePresenceWatcher = new AppHostFilePresenceWatcher(dataRepository);
  context.subscriptions.push(appHostFilePresenceWatcher);

  // View-shown telemetry. Subscribes to visibility changes on the same tree
  // view; debounced internally so rapid VS Code panel toggles do not spam.
  const appHostsViewTelemetry = new AppHostsViewTelemetry(appHostTreeView, dataRepository);
  context.subscriptions.push(appHostsViewTelemetry);

  const globalRefreshAppHostsRegistration = registerInstrumentedCommand('aspire-vscode.globalRefreshAppHosts', 'tree', () => dataRepository.refresh());
  const refreshAppHostsRegistration = registerInstrumentedCommand('aspire-vscode.refreshAppHosts', 'tree', () => dataRepository.refresh());
  const switchToGlobalViewRegistration = registerInstrumentedCommand('aspire-vscode.switchToGlobalView', 'tree', () => dataRepository.setViewMode('global'));
  const switchToWorkspaceViewRegistration = registerInstrumentedCommand('aspire-vscode.switchToWorkspaceView', 'tree', () => dataRepository.setViewMode('workspace'));
  const openDashboardRegistration = registerInstrumentedCommand('aspire-vscode.openDashboard', 'tree', (element) => appHostTreeProvider.openDashboard(element));
  const openAppHostSourceRegistration = registerInstrumentedCommand('aspire-vscode.openAppHostSource', 'tree', (element) => appHostTreeProvider.openAppHostSource(element));
  const stopAppHostRegistration = registerInstrumentedCommand('aspire-vscode.stopAppHost', 'tree', (element) => appHostTreeProvider.stopAppHost(element));
  const runAppHostRegistration = registerInstrumentedCommand('aspire-vscode.runAppHost', 'tree', (element) => appHostTreeProvider.runAppHost(element, true));
  const debugAppHostRegistration = registerInstrumentedCommand('aspire-vscode.debugAppHost', 'tree', (element) => appHostTreeProvider.runAppHost(element, false));
  const stopResourceRegistration = registerInstrumentedCommand('aspire-vscode.stopResource', 'tree', (element) => appHostTreeProvider.stopResource(element));
  const startResourceRegistration = registerInstrumentedCommand('aspire-vscode.startResource', 'tree', (element) => appHostTreeProvider.startResource(element));
  const restartResourceRegistration = registerInstrumentedCommand('aspire-vscode.restartResource', 'tree', (element) => appHostTreeProvider.restartResource(element));
  const viewResourceLogsRegistration = registerInstrumentedCommand('aspire-vscode.viewResourceLogs', 'tree', (element) => appHostTreeProvider.viewResourceLogs(element));
  const executeResourceCommandRegistration = registerInstrumentedCommand('aspire-vscode.executeResourceCommand', 'tree', (element) => appHostTreeProvider.executeResourceCommand(element));
  const copyEndpointUrlRegistration = registerInstrumentedCommand('aspire-vscode.copyEndpointUrl', 'tree', (element) => appHostTreeProvider.copyEndpointUrl(element));
  const openInExternalBrowserRegistration = registerInstrumentedCommand('aspire-vscode.openInExternalBrowser', 'tree', (element) => appHostTreeProvider.openInExternalBrowser(element));
  const openInIntegratedBrowserRegistration = registerInstrumentedCommand('aspire-vscode.openInIntegratedBrowser', 'tree', (element) => appHostTreeProvider.openInIntegratedBrowser(element));
  const copyResourceNameRegistration = registerInstrumentedCommand('aspire-vscode.copyResourceName', 'tree', (element) => appHostTreeProvider.copyResourceName(element));
  const copyAppHostPathRegistration = registerInstrumentedCommand('aspire-vscode.copyAppHostPath', 'tree', (element) => appHostTreeProvider.copyAppHostPath(element));
  const viewAppHostSourceRegistration = registerInstrumentedCommand('aspire-vscode.viewAppHostSource', 'tree', (element) => appHostTreeProvider.viewAppHostSource(element));
  const viewAppHostLogFileRegistration = registerInstrumentedCommand('aspire-vscode.viewAppHostLogFile', 'tree', (element) => appHostTreeProvider.viewAppHostLogFile(element));
  const copyLogFilePathRegistration = registerInstrumentedCommand('aspire-vscode.copyLogFilePath', 'tree', (element) => appHostTreeProvider.copyLogFilePath(element));
  const expandAllRegistration = registerInstrumentedCommand('aspire-vscode.expandAll', 'tree', (element) => appHostTreeProvider.expandAll(element));

  // Set initial context for welcome view
  vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', true);
  vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', true);
  vscode.commands.executeCommand('setContext', 'aspire.loading', true);

  // Activate the data repository. Workspace describe watching and global polling begin when the panel is visible.
  dataRepository.activate();

  context.subscriptions.push(appHostTreeView, globalRefreshAppHostsRegistration, refreshAppHostsRegistration, switchToGlobalViewRegistration, switchToWorkspaceViewRegistration, openDashboardRegistration, openAppHostSourceRegistration, stopAppHostRegistration, runAppHostRegistration, debugAppHostRegistration, stopResourceRegistration, startResourceRegistration, restartResourceRegistration, viewResourceLogsRegistration, executeResourceCommandRegistration, copyEndpointUrlRegistration, openInExternalBrowserRegistration, openInIntegratedBrowserRegistration, copyResourceNameRegistration, copyAppHostPathRegistration, viewAppHostSourceRegistration, viewAppHostLogFileRegistration, copyLogFilePathRegistration, expandAllRegistration, { dispose: () => { appHostTreeProvider.dispose(); dataRepository.dispose(); } });

  // CodeLens provider — shows Debug on pipeline steps, resource state on resources
  const codeLensProvider = new AspireCodeLensProvider(appHostTreeProvider, dataRepository);
  const languageFilters = getSupportedLanguageIds().map(lang => ({ language: lang, scheme: 'file' }));
  const codeLensRegistration = vscode.languages.registerCodeLensProvider(languageFilters, codeLensProvider);
  const codeLensDebugPipelineStepRegistration = registerInstrumentedCommand('aspire-vscode.codeLensDebugPipelineStep', 'codelens', (stepName: string) => editorCommandProvider.tryExecuteDoAppHost(false, stepName));
  const codeLensResourceActionRegistration = registerInstrumentedCommand('aspire-vscode.codeLensResourceAction', 'codelens', async (resourceName: string, action: string, appHostPath: string, resourceCommand?: ResourceCommandJson) => {
    const commandArguments = await collectResourceCommandArguments(action, resourceCommand, {
      secretWarningState: context.globalState,
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

    let command = `resource "${resourceName}" "${action}"`;
    if (appHostPath) {
      command += ` --apphost "${appHostPath}"`;
    }
    terminalProvider.sendAspireCommandToAspireTerminal(command, true, commandArguments.args, { redactAdditionalArgs: commandArguments.containsSecret });
  });
  const codeLensViewLogsRegistration = registerInstrumentedCommand('aspire-vscode.codeLensViewLogs', 'codelens', (resourceName: string, appHostPath: string) => {
    let command = `logs "${resourceName}"`;
    if (appHostPath) {
      command += ` --apphost "${appHostPath}"`;
    }
    command += ' --follow';
    terminalProvider.sendAspireCommandToAspireTerminal(command);
  });
  const codeLensRevealResourceRegistration = registerInstrumentedCommand('aspire-vscode.codeLensRevealResource', 'codelens', (resourceName: string, appHostPath?: string) => {
    const element = appHostTreeProvider.findResourceElement(resourceName, appHostPath);
    if (element) {
      appHostTreeView.reveal(element, { select: true, focus: true });
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
    terminalProvider.sendAspireCommandToAspireTerminal('logs', true, additionalArgs);
  });
  context.subscriptions.push(codeLensRegistration, codeLensDebugPipelineStepRegistration, codeLensResourceActionRegistration, codeLensViewLogsRegistration, codeLensRevealResourceRegistration, codeLensOpenDashboardRegistration, codeLensViewAppHostLogsRegistration, codeLensProvider);

  // Gutter decorations — colored dots next to resources showing runtime state
  const gutterDecorationProvider = new AspireGutterDecorationProvider(appHostTreeProvider);
  context.subscriptions.push(gutterDecorationProvider);

  context.subscriptions.push(cliAddCommandRegistration, cliNewCommandRegistration, cliInitCommandRegistration, cliDeployCommandRegistration, cliPublishCommandRegistration, cliDoCommandRegistration, openTerminalCommandRegistration, configureLaunchJsonCommandRegistration);
  context.subscriptions.push(cliUpdateCommandRegistration, cliUpdateSelfCommandRegistration, settingsCommandRegistration, openLocalSettingsCommandRegistration, openGlobalSettingsCommandRegistration, runAppHostCommandRegistration, debugAppHostCommandRegistration);
  context.subscriptions.push(installCliStableRegistration, installCliDailyRegistration, verifyCliInstalledRegistration);

  const debugConfigProvider = new AspireDebugConfigurationProvider(appHostDiscoveryService);
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
    appHostDiscoveryService,
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

  // Auto-restore: run `aspire restore` on workspace open and when aspire.config.json changes
  const packageRestoreProvider = new AspirePackageRestoreProvider(terminalProvider);
  context.subscriptions.push(packageRestoreProvider);
  void packageRestoreProvider.activate().catch(err => {
    extensionLogOutputChannel.warn(`Auto-restore activation failed: ${String(err)}`);
  });

  const restoreCommandRegistration = registerInstrumentedCommand('aspire-vscode.restore', 'editor', () => {
    void packageRestoreProvider.retryRestore().catch(err => {
      extensionLogOutputChannel.warn(`Manual restore failed: ${String(err)}`);
    });
  });
  context.subscriptions.push(restoreCommandRegistration);

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
    await withCommandTelemetry(commandName, async () => {
      const cliCheckExcludedCommands: string[] = ["aspire-vscode.settings", "aspire-vscode.configureLaunchJson"];

      if (!cliCheckExcludedCommands.includes(commandName)) {
        const result = await checkCliAvailableOrRedirect();
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
