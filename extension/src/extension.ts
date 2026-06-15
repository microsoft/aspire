import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';

import { addCommand } from './commands/add';
import { RpcClient } from './server/rpcClient';
import { InteractionService } from './server/interactionService';
import { newCommand } from './commands/new';
import { initCommand } from './commands/init';
import { deployCommand } from './commands/deploy';
import { publishCommand } from './commands/publish';
import { doCommand } from './commands/do';
import { cliNotAvailable, dismissLabel, errorMessage, openCliInstallInstructions } from './loc/strings';
import { extensionLogOutputChannel } from './utils/logging';
import { CommandInvocationEvent, initializeTelemetry, isCommandCancellation, onDidInvokeCommand, withCommandTelemetry } from './utils/telemetry';
import { MeaningfulEngagementReporter } from './utils/meaningfulEngagement';
import { AspireDebugAdapterDescriptorFactory } from './debugger/AspireDebugAdapterDescriptorFactory';
import { AspireDebugConfigurationProvider } from './debugger/AspireDebugConfigurationProvider';
import { AspireExtensionContext } from './AspireExtensionContext';
import AspireRpcServer, { RpcServerConnectionInfo } from './server/AspireRpcServer';
import AspireDcpServer from './dcp/AspireDcpServer';
import { configureLaunchJsonCommand } from './commands/configureLaunchJson';
import { AspireTerminalProvider, AspireTerminalCommandEvent, shellArg } from './utils/AspireTerminalProvider';
import { MessageConnection } from 'vscode-jsonrpc';
import { openTerminalCommand } from './commands/openTerminal';
import { updateCommand, updateSelfCommand } from './commands/update';
import { settingsCommand } from './commands/settings';
import { openLocalSettingsCommand, openGlobalSettingsCommand } from './commands/openSettings';
import { checkCliAvailableOrRedirect, checkForExistingAppHostPathInWorkspace } from './utils/workspace';
import { AspireEditorCommandProvider } from './editor/AspireEditorCommandProvider';
import { AspirePackageRestoreProvider } from './utils/AspirePackageRestoreProvider';
import { AspireAppHostTreeProvider, isEnabledCommand } from './views/AspireAppHostTreeProvider';
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
import { AppHostDisplayInfo, ResourceCommandJson, ResourceJson, isMatchingAppHostPath } from './views/AppHostDataRepository';
import { AppHostDiscoveryService } from './utils/appHostDiscovery';
import { AppHostLaunchRequestedEvent, AppHostLaunchService } from './services/AppHostLaunchService';
import type { AspireAppHostState, AspireDebugConsoleOutputEvent, AspireExtensionApi, AspireExtensionE2ECommandInvocation, AspireExtensionE2EControlCommand, AspireExtensionE2EControlPayload, AspireExtensionE2EControlStatus, AspireExtensionE2EDebugConsoleOutput, AspireExtensionE2EDebugLaunch, AspireExtensionE2ETerminalCommand, AspireExtensionStateSnapshot, AspireResourceCommandState, AspireResourceState, AspireResourceUrlState, WaitForStateOptions } from './types/extensionApi';
import { AppHostsViewTelemetry } from './views/AppHostsViewTelemetry';

let aspireExtensionContext = new AspireExtensionContext();
let atomicWriteSequence = 0;

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
  const debugSessionRefreshRegistration = appHostLaunchService.onDidTerminateAppHostDebugSession(() => dataRepository.refresh());

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
  const openDashboardToSideRegistration = registerInstrumentedCommand('aspire-vscode.openDashboardToSide', 'tree', (element) => appHostTreeProvider.openDashboardToSide(element));
  const openAppHostSourceRegistration = registerInstrumentedCommand('aspire-vscode.openAppHostSource', 'tree', (element) => appHostTreeProvider.openAppHostSource(element));
  const stopAppHostRegistration = registerInstrumentedCommand('aspire-vscode.stopAppHost', 'tree', (element) => appHostTreeProvider.stopAppHost(element));
  const runAppHostRegistration = registerInstrumentedCommand('aspire-vscode.runAppHost', 'tree', (element) => appHostTreeProvider.runAppHost(element, true));
  const debugAppHostRegistration = registerInstrumentedCommand('aspire-vscode.debugAppHost', 'tree', (element) => appHostTreeProvider.runAppHost(element, false));
  const stopResourceRegistration = registerInstrumentedCommand('aspire-vscode.stopResource', 'tree', (element) => appHostTreeProvider.stopResource(element));
  const startResourceRegistration = registerInstrumentedCommand('aspire-vscode.startResource', 'tree', (element) => appHostTreeProvider.startResource(element));
  const restartResourceRegistration = registerInstrumentedCommand('aspire-vscode.restartResource', 'tree', (element) => appHostTreeProvider.restartResource(element));
  const viewResourceLogsRegistration = registerInstrumentedCommand('aspire-vscode.viewResourceLogs', 'tree', (element) => appHostTreeProvider.viewResourceLogs(element));
  const executeResourceCommandRegistration = registerInstrumentedCommand('aspire-vscode.executeResourceCommand', 'tree', (element) => appHostTreeProvider.executeResourceCommand(element));
  const executeResourceCommandItemRegistration = registerInstrumentedCommand('aspire-vscode.executeResourceCommandItem', 'tree', (element) => appHostTreeProvider.executeResourceCommandItem(element));
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

  context.subscriptions.push(
    appHostTreeView,
    globalRefreshAppHostsRegistration,
    refreshAppHostsRegistration,
    switchToGlobalViewRegistration,
    switchToWorkspaceViewRegistration,
    openDashboardRegistration,
    openDashboardToSideRegistration,
    openAppHostSourceRegistration,
    stopAppHostRegistration,
    runAppHostRegistration,
    debugAppHostRegistration,
    stopResourceRegistration,
    startResourceRegistration,
    restartResourceRegistration,
    viewResourceLogsRegistration,
    executeResourceCommandRegistration,
    executeResourceCommandItemRegistration,
    copyEndpointUrlRegistration,
    openInExternalBrowserRegistration,
    openInIntegratedBrowserRegistration,
    copyResourceNameRegistration,
    copyAppHostPathRegistration,
    viewAppHostSourceRegistration,
    viewAppHostLogFileRegistration,
    copyLogFilePathRegistration,
    expandAllRegistration,
    debugSessionRefreshRegistration,
    { dispose: () => { appHostTreeProvider.dispose(); dataRepository.dispose(); } });

  // CodeLens provider — shows Debug on pipeline steps, resource state on resources
  const codeLensProvider = new AspireCodeLensProvider(appHostTreeProvider, dataRepository);
  const languageFilters = getSupportedLanguageIds().map(lang => ({ language: lang, scheme: 'file' }));
  const codeLensRegistration = vscode.languages.registerCodeLensProvider(languageFilters, codeLensProvider);
  const codeLensDebugPipelineStepRegistration = registerInstrumentedCommand('aspire-vscode.codeLensDebugPipelineStep', 'codelens', (stepName: string) => editorCommandProvider.tryExecuteDoAppHost(false, stepName));
  const codeLensResourceActionRegistration = registerInstrumentedCommand('aspire-vscode.codeLensResourceAction', 'codelens', async (resourceName: string, action: string, appHostPath: string, resourceCommand?: ResourceCommandJson) => {
    if (resourceCommand !== undefined && !isEnabledCommand(resourceCommand)) {
      extensionLogOutputChannel.warn(`Ignoring disabled CodeLens resource command '${action}' for resource '${resourceName}'.`);
      return;
    }

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

    const command = appHostPath
      ? ['resource', shellArg(resourceName), shellArg(action), '--apphost', shellArg(appHostPath)]
      : ['resource', shellArg(resourceName), shellArg(action)];
    terminalProvider.sendAspireCommandToAspireTerminal(command, true, commandArguments.args, { redactAdditionalArgs: commandArguments.containsSecret });
  });
  const codeLensViewLogsRegistration = registerInstrumentedCommand('aspire-vscode.codeLensViewLogs', 'codelens', (resourceName: string, appHostPath: string) => {
    const command = appHostPath
      ? ['logs', shellArg(resourceName), '--apphost', shellArg(appHostPath), '--follow']
      : ['logs', shellArg(resourceName), '--follow'];
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

  const onDidChangeStateEmitter = new vscode.EventEmitter<AspireExtensionStateSnapshot>();
  const fireStateChanged = () => onDidChangeStateEmitter.fire(createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireExtensionContext));
  context.subscriptions.push(onDidChangeStateEmitter);
  context.subscriptions.push(dataRepository.onDidChangeData(fireStateChanged));
  context.subscriptions.push(appHostLaunchService.onDidChangeLaunchingState(fireStateChanged));
  context.subscriptions.push(appHostTreeProvider.onDidChangeStoppingState(fireStateChanged));
  context.subscriptions.push(aspireExtensionContext.onDidChangeDebugSessions(fireStateChanged));
  const e2eStateFileBridge = createE2eStateFileBridge(context, dataRepository, appHostLaunchService, appHostTreeProvider, terminalProvider, onDidChangeStateEmitter.event);
  context.subscriptions.push(e2eStateFileBridge);

  const api = createExtensionApi(context, rpcServer, dcpServer, dataRepository, appHostLaunchService, appHostTreeProvider, onDidChangeStateEmitter.event);

  return Object.freeze(api);
}

export function deactivate() {
  aspireExtensionContext.dispose();
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

function createExtensionApi(
  context: vscode.ExtensionContext,
  rpcServer: AspireRpcServer,
  dcpServer: AspireDcpServer,
  dataRepository: AppHostDataRepository,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider,
  onDidChangeState: vscode.Event<AspireExtensionStateSnapshot>,
): AspireExtensionApi {
  const waitForState = (
    predicate: (state: AspireExtensionStateSnapshot) => boolean,
    options?: WaitForStateOptions
  ): Promise<AspireExtensionStateSnapshot> => {
    const currentState = createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireExtensionContext);
    if (predicate(currentState)) {
      return Promise.resolve(currentState);
    }

    const timeoutMs = options?.timeoutMs ?? 30000;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        subscription.dispose();
        reject(new Error(`Timed out after ${timeoutMs}ms waiting for Aspire extension state. Last state: ${JSON.stringify(createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireExtensionContext))}`));
      }, timeoutMs);

      const subscription = onDidChangeState(state => {
        if (predicate(state)) {
          clearTimeout(timeout);
          subscription.dispose();
          resolve(state);
        }
      });
    });
  };

  const api: AspireExtensionApi & { __testOnlyRpcServerInfo?: RpcServerConnectionInfo } = {
    apiVersion: 1,
    rpcServerInfo: { address: rpcServer.connectionInfo.address },
    dcpServerInfo: { address: dcpServer.connectionInfo.address },
    logDirectory: context.logUri.fsPath,
    get state() {
      return createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireExtensionContext);
    },
    onDidChangeState,
    waitForState,
    waitForRepositoryIdle: options => waitForState(state => !state.isRepositoryLoading && state.isWorkspaceAppHostDiscoveryComplete, options),
    getDashboardUrl: appHostPath => getDashboardUrl(dataRepository, appHostPath),
  };
  if (context.extensionMode === vscode.ExtensionMode.Test) {
    api.__testOnlyRpcServerInfo = rpcServer.connectionInfo;
  }

  return api;
}

function createStateSnapshot(
  dataRepository: AppHostDataRepository,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider,
  extensionContext: AspireExtensionContext,
  includeSensitiveDashboardUrls = false,
): AspireExtensionStateSnapshot {
  return {
    viewMode: dataRepository.viewMode,
    isRepositoryLoading: dataRepository.isLoading,
    isWorkspaceAppHostDiscoveryComplete: dataRepository.isWorkspaceAppHostDiscoveryComplete,
    hasError: dataRepository.hasError,
    errorMessage: dataRepository.errorMessage,
    workspaceAppHost: dataRepository.workspaceAppHost ? cloneAppHostState(dataRepository.workspaceAppHost, includeSensitiveDashboardUrls) : undefined,
    workspaceAppHostName: dataRepository.workspaceAppHostName,
    workspaceAppHostPath: dataRepository.workspaceAppHostPath,
    workspaceAppHostCandidatePaths: [...dataRepository.workspaceAppHostCandidatePaths],
    workspaceAppHostDescription: dataRepository.workspaceAppHostDescription,
    workspaceResources: dataRepository.workspaceResources.map(resource => cloneResourceState(resource, includeSensitiveDashboardUrls)),
    appHosts: dataRepository.appHosts.map(appHost => cloneAppHostState(appHost, includeSensitiveDashboardUrls)),
    launchingPaths: [...appHostLaunchService.launchingPaths],
    stoppingPaths: [...appHostTreeProvider.stoppingPaths],
    debugSessions: extensionContext.aspireDebugSessions.map(session => ({
      appHostPath: session.appHostPath,
      dashboardUrl: session.dashboardUrl && includeSensitiveDashboardUrls ? stripResourceSuffix(session.dashboardUrl) : sanitizeDashboardUrl(session.dashboardUrl),
      startupCompleted: session.startupCompleted,
    })),
  };
}

function createE2eStateFileBridge(
  context: vscode.ExtensionContext,
  dataRepository: AppHostDataRepository,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider,
  terminalProvider: AspireTerminalProvider,
  onDidChangeState: vscode.Event<AspireExtensionStateSnapshot>,
): vscode.Disposable {
  const stateFile = process.env.ASPIRE_EXTENSION_E2E_STATE_FILE;
  const controlFile = process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE;
  if (!isE2eBridgeEnabled() || !stateFile || !controlFile) {
    return new vscode.Disposable(() => undefined);
  }

  const commandInvocations: AspireExtensionE2ECommandInvocation[] = [];
  const terminalCommands: AspireExtensionE2ETerminalCommand[] = [];
  const debugLaunches: AspireExtensionE2EDebugLaunch[] = [];
  const debugConsoleOutputs: AspireExtensionE2EDebugConsoleOutput[] = [];
  let commandInvocationSequence = 0;
  let terminalCommandSequence = 0;
  let debugLaunchSequence = 0;
  let debugConsoleOutputSequence = 0;
  let controlStatus: AspireExtensionE2EControlStatus | undefined;
  let lastControlRevision = -1;
  const writeStateFile = () => {
    writeJsonFileAtomic(stateFile, {
      updatedAt: new Date().toISOString(),
      state: createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireExtensionContext, true),
      dashboardUrl: getSensitiveDashboardUrl(dataRepository),
      commandInvocations,
      terminalCommands,
      debugLaunches,
      debugConsoleOutputs,
      control: controlStatus,
    });
  };

  fs.mkdirSync(path.dirname(stateFile), { recursive: true });
  writeStateFile();

  const stateSubscription = onDidChangeState(writeStateFile);
  const commandSubscription = onDidInvokeCommand(event => {
    commandInvocations.push({
      ...event,
      sequence: ++commandInvocationSequence,
    });
    if (commandInvocations.length > 50) {
      commandInvocations.shift();
    }
    writeStateFile();
  });
  const debugConsoleOutputSubscription = aspireExtensionContext.onDidReceiveDebugConsoleOutput(event => {
    debugConsoleOutputs.push(cloneDebugConsoleOutputEvent(event, ++debugConsoleOutputSequence));
    if (debugConsoleOutputs.length > 500) {
      debugConsoleOutputs.shift();
    }
    writeStateFile();
  });
  const terminalCommandSubscription = terminalProvider.onDidSendAspireCommand(event => {
    terminalCommands.push(cloneTerminalCommandEvent(event, ++terminalCommandSequence));
    if (terminalCommands.length > 100) {
      terminalCommands.shift();
    }
    writeStateFile();
  });
  const debugLaunchSubscription = appHostLaunchService.onDidRequestLaunch(event => {
    debugLaunches.push(cloneDebugLaunchEvent(event, ++debugLaunchSequence));
    if (debugLaunches.length > 100) {
      debugLaunches.shift();
    }
    writeStateFile();
  });

  let controlProcessing: Promise<void> | undefined;
  const controlInterval = controlFile
    ? setInterval(() => {
      if (controlProcessing) {
        return;
      }

      controlProcessing = processE2eControlFile(controlFile, lastControlRevision, async (payload) => {
        const revision = payload.revision;
        lastControlRevision = revision;
        try {
          if (typeof payload.aspireCliExecutablePath === 'string') {
            const target = vscode.workspace.workspaceFolders?.length
              ? vscode.ConfigurationTarget.Workspace
              : vscode.ConfigurationTarget.Global;
            await vscode.workspace.getConfiguration('aspire').update('aspireCliExecutablePath', payload.aspireCliExecutablePath, target);
          }
          if (payload.e2eCliExecutablePath === null) {
            delete process.env.ASPIRE_EXTENSION_E2E_CLI_PATH;
          }
          else if (typeof payload.e2eCliExecutablePath === 'string') {
            process.env.ASPIRE_EXTENSION_E2E_CLI_PATH = payload.e2eCliExecutablePath;
          }
          if (typeof payload.forceCliUnavailable === 'boolean') {
            process.env.ASPIRE_EXTENSION_E2E_FORCE_CLI_UNAVAILABLE = payload.forceCliUnavailable ? 'true' : 'false';
          }
          if (typeof payload.suppressTerminalCommandExecution === 'boolean') {
            process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_TERMINAL_COMMAND_EXECUTION = payload.suppressTerminalCommandExecution ? 'true' : 'false';
          }
          if (typeof payload.suppressDebugLaunch === 'boolean') {
            process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH = payload.suppressDebugLaunch ? 'true' : 'false';
          }
          if (payload.showStatusDelayMs === null) {
            delete process.env.ASPIRE_EXTENSION_E2E_SHOW_STATUS_DELAY_MS;
          }
          else if (typeof payload.showStatusDelayMs === 'number') {
            process.env.ASPIRE_EXTENSION_E2E_SHOW_STATUS_DELAY_MS = String(payload.showStatusDelayMs);
          }
          if (payload.command) {
            let commandStarted = false;
            const markCommandStarted = () => {
              if (!commandStarted) {
                commandStarted = true;
                controlStatus = { revision, status: 'started' };
                writeStateFile();
              }
            };

            const result = await executeE2eControlCommand(context, appHostTreeProvider, payload.command, markCommandStarted);
            controlStatus = { revision, status: 'applied', result };
          }
          else {
            controlStatus = { revision, status: 'applied' };
          }
        }
        catch (error) {
          controlStatus = { revision, status: 'error', errorMessage: getE2eErrorMessage(error) };
        }
        writeStateFile();
      }).finally(() => {
        controlProcessing = undefined;
      });

      void controlProcessing;
    }, 200)
    : undefined;
  const controlSubscription = new vscode.Disposable(() => {
    if (controlInterval) {
      clearInterval(controlInterval);
    }
  });

  return vscode.Disposable.from(stateSubscription, commandSubscription, terminalCommandSubscription, debugLaunchSubscription, debugConsoleOutputSubscription, controlSubscription);
}

function writeJsonFileAtomic(filePath: string, value: unknown): void {
  const temporaryPath = `${filePath}.${process.pid}.${atomicWriteSequence++}.tmp`;
  fs.writeFileSync(temporaryPath, JSON.stringify(value, undefined, 2));
  try {
    renameFileWithRetry(temporaryPath, filePath);
  }
  finally {
    fs.rmSync(temporaryPath, { force: true });
  }
}

function renameFileWithRetry(sourcePath: string, destinationPath: string): void {
  const maxAttempts = process.platform === 'win32' ? 10 : 1;
  for (let attempt = 1; ; attempt++) {
    try {
      fs.renameSync(sourcePath, destinationPath);
      return;
    }
    catch (error) {
      if (attempt >= maxAttempts || !isRetryableRenameError(error)) {
        throw error;
      }

      sleepSynchronously(25);
    }
  }
}

function isRetryableRenameError(error: unknown): boolean {
  if (process.platform !== 'win32' || !error || typeof error !== 'object' || !('code' in error)) {
    return false;
  }

  return error.code === 'EPERM' || error.code === 'EACCES' || error.code === 'EEXIST';
}

function sleepSynchronously(milliseconds: number): void {
  const buffer = new SharedArrayBuffer(4);
  Atomics.wait(new Int32Array(buffer), 0, 0, milliseconds);
}

async function processE2eControlFile(
  controlFile: string,
  lastControlRevision: number,
  applyControl: (payload: AspireExtensionE2EControlPayload) => Promise<void>,
): Promise<void> {
  let payload: AspireExtensionE2EControlPayload;
  try {
    payload = JSON.parse(fs.readFileSync(controlFile, 'utf8')) as AspireExtensionE2EControlPayload;
  }
  catch (error) {
    if (error && typeof error === 'object' && 'code' in error && error.code === 'ENOENT') {
      return;
    }

    extensionLogOutputChannel.warn(`Failed to read Aspire extension E2E control file: ${getE2eErrorMessage(error)}`);
    return;
  }

  if (typeof payload.revision !== 'number' || payload.revision <= lastControlRevision) {
    return;
  }

  await applyControl(payload);
}

function getE2eErrorMessage(error: unknown): string {
  return error instanceof Error ? (error.stack ?? error.message) : String(error);
}

async function executeE2eControlCommand(
  context: vscode.ExtensionContext,
  appHostTreeProvider: AspireAppHostTreeProvider,
  command: AspireExtensionE2EControlCommand,
  markStarted: () => void
): Promise<unknown> {
  switch (command.name) {
    case 'refreshAppHosts': {
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.refreshAppHosts');
      markStarted();
      return await commandPromise;
    }
    case 'globalRefreshAppHosts': {
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.globalRefreshAppHosts');
      markStarted();
      return await commandPromise;
    }
    case 'switchToGlobalView': {
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.switchToGlobalView');
      markStarted();
      return await commandPromise;
    }
    case 'switchToWorkspaceView': {
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.switchToWorkspaceView');
      markStarted();
      return await commandPromise;
    }
    case 'runAppHost': {
      const element = getAppHostElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.runAppHost', element);
      markStarted();
      return await commandPromise;
    }
    case 'stopAppHost': {
      const element = getAppHostElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.stopAppHost', element);
      markStarted();
      return await commandPromise;
    }
    case 'openDashboard': {
      const element = getAppHostElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.openDashboard', element);
      markStarted();
      return await commandPromise;
    }
    case 'debugAppHost': {
      const element = getAppHostElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.debugAppHost', element);
      markStarted();
      return await commandPromise;
    }
    case 'openAppHostSource': {
      const element = getAppHostElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.openAppHostSource', element);
      markStarted();
      await commandPromise;
      return getActiveEditorInfo();
    }
    case 'viewAppHostSource': {
      const element = getAppHostElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.viewAppHostSource', element);
      markStarted();
      await commandPromise;
      return getActiveEditorInfo();
    }
    case 'copyAppHostPath': {
      const element = getAppHostElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyAppHostPath', element);
      markStarted();
      await commandPromise;
      return await vscode.env.clipboard.readText();
    }
    case 'viewAppHostLogFile': {
      const element = getLogFileElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.viewAppHostLogFile', element);
      markStarted();
      await commandPromise;
      return getActiveEditorInfo();
    }
    case 'copyLogFilePath': {
      const element = getLogFileElement(appHostTreeProvider, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyLogFilePath', element);
      markStarted();
      await commandPromise;
      return await vscode.env.clipboard.readText();
    }
    case 'viewResourceLogs': {
      const element = getResourceElement(appHostTreeProvider, command.resourceName, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.viewResourceLogs', element);
      markStarted();
      return await commandPromise;
    }
    case 'copyResourceName': {
      const element = getResourceElement(appHostTreeProvider, command.resourceName, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyResourceName', element);
      markStarted();
      await commandPromise;
      return await vscode.env.clipboard.readText();
    }
    case 'copyEndpointUrl': {
      const element = getEndpointElement(appHostTreeProvider, command);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyEndpointUrl', element);
      markStarted();
      await commandPromise;
      return await vscode.env.clipboard.readText();
    }
    case 'openInIntegratedBrowser': {
      const element = getEndpointElement(appHostTreeProvider, command);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.openInIntegratedBrowser', element);
      markStarted();
      return await commandPromise;
    }
    case 'stopResource': {
      const element = getResourceElement(appHostTreeProvider, command.resourceName, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.stopResource', element);
      markStarted();
      return await commandPromise;
    }
    case 'startResource': {
      const element = getResourceElement(appHostTreeProvider, command.resourceName, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.startResource', element);
      markStarted();
      return await commandPromise;
    }
    case 'restartResource': {
      const element = getResourceElement(appHostTreeProvider, command.resourceName, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.restartResource', element);
      markStarted();
      return await commandPromise;
    }
    case 'executeResourceCommand': {
      const element = getResourceElement(appHostTreeProvider, command.resourceName, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.executeResourceCommand', element);
      markStarted();
      return await commandPromise;
    }
    case 'executeResourceCommandItem': {
      const element = getResourceCommandElement(appHostTreeProvider, command);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.executeResourceCommandItem', element);
      markStarted();
      return await commandPromise;
    }
    case 'executeAspireCommand': {
      const commandId = getE2eAspireCommandId(command.commandId);
      const args = getE2eCommandArguments(command.args);
      const commandPromise = vscode.commands.executeCommand(commandId, ...args);
      markStarted();
      await commandPromise;
      return undefined;
    }
    case 'setSourceBreakpoint': {
      markStarted();
      const filePath = getE2eWorkspacePath(command.filePath);
      const line = getE2eBreakpointLine(command.line);
      if (command.clearExisting) {
        vscode.debug.removeBreakpoints(vscode.debug.breakpoints);
      }

      const breakpoint = new vscode.SourceBreakpoint(new vscode.Location(vscode.Uri.file(filePath), new vscode.Position(line, 0)));
      vscode.debug.addBreakpoints([breakpoint]);
      return getE2eBreakpoints();
    }
    case 'clearBreakpoints': {
      markStarted();
      vscode.debug.removeBreakpoints(vscode.debug.breakpoints);
      return getE2eBreakpoints();
    }
    case 'getBreakpoints': {
      markStarted();
      return getE2eBreakpoints();
    }
    case 'stopDebugging': {
      markStarted();
      await vscode.debug.stopDebugging();
      return undefined;
    }
    case 'closeAllEditors': {
      markStarted();
      await vscode.commands.executeCommand('workbench.action.closeAllEditors');
      return getActiveEditorInfo();
    }
    case 'getRegisteredAspireCommands': {
      markStarted();
      const commands = await vscode.commands.getCommands(true);
      return commands.filter(commandId => commandId.startsWith('aspire-vscode.')).sort();
    }
    case 'getExtensionPackageJson': {
      markStarted();
      return context.extension.packageJSON;
    }
    case 'getExtensionFileStatus': {
      markStarted();
      return getExtensionFileStatus(context, command.relativePaths);
    }
    case 'getDiagnostics': {
      markStarted();
      return await getDiagnosticsForFile(command.filePath);
    }
    case 'readClipboard': {
      markStarted();
      return await vscode.env.clipboard.readText();
    }
    case 'openWorkspaceFolder': {
      const folderPath = getE2eWorkspaceFolderPath(command.folderPath);
      markStarted();
      clearPendingE2eControlFile();
      await vscode.commands.executeCommand('vscode.openFolder', vscode.Uri.file(folderPath), false);
      return undefined;
    }
    case 'getWorkspaceFolders': {
      markStarted();
      return vscode.workspace.workspaceFolders?.map(folder => ({
        name: folder.name,
        uri: folder.uri.toString(),
        fileName: folder.uri.fsPath,
      })) ?? [];
    }
    case 'getActiveEditor': {
      markStarted();
      return getActiveEditorInfo();
    }
    default:
      throw new Error(`Unsupported Aspire extension E2E control command: ${getUnknownCommandName(command)}`);
  }
}

function getE2eAspireCommandId(commandId: unknown): string {
  if (typeof commandId !== 'string' || !commandId.startsWith('aspire-vscode.')) {
    throw new Error('Aspire extension E2E executeAspireCommand requires an aspire-vscode command id.');
  }

  return commandId;
}

function getE2eCommandArguments(args: unknown): readonly unknown[] {
  if (args === undefined) {
    return [];
  }

  if (!Array.isArray(args)) {
    throw new Error('Aspire extension E2E executeAspireCommand args must be an array when provided.');
  }

  return args;
}

function getE2eWorkspacePath(filePath: unknown): string {
  if (typeof filePath !== 'string' || filePath.length === 0 || !path.isAbsolute(filePath)) {
    throw new Error('Aspire extension E2E workspace path arguments must be absolute paths.');
  }

  const workspaceFolders = vscode.workspace.workspaceFolders;
  if (!workspaceFolders || !workspaceFolders.some(folder => isPathWithinDirectory(filePath, folder.uri.fsPath))) {
    throw new Error('Aspire extension E2E workspace path arguments must stay inside the opened workspace.');
  }

  return filePath;
}

function getE2eWorkspaceFolderPath(folderPath: unknown): string {
  if (typeof folderPath !== 'string' || folderPath.length === 0 || !path.isAbsolute(folderPath)) {
    throw new Error('Aspire extension E2E openWorkspaceFolder requires an absolute folder path.');
  }

  if (!fs.existsSync(folderPath) || !fs.statSync(folderPath).isDirectory()) {
    throw new Error(`Aspire extension E2E openWorkspaceFolder requires an existing folder: ${folderPath}`);
  }

  const expectedWorkspaceRoot = process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT;
  if (typeof expectedWorkspaceRoot !== 'string' || expectedWorkspaceRoot.length === 0 || !isSamePath(folderPath, expectedWorkspaceRoot)) {
    throw new Error('Aspire extension E2E openWorkspaceFolder can only open the configured E2E workspace root.');
  }

  return folderPath;
}

function getE2eBreakpointLine(line: unknown): number {
  if (typeof line !== 'number' || !Number.isInteger(line) || line < 0) {
    throw new Error('Aspire extension E2E setSourceBreakpoint requires a zero-based non-negative integer line.');
  }

  return line;
}

function clearPendingE2eControlFile(): void {
  const controlFile = process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE;
  if (controlFile) {
    fs.rmSync(controlFile, { force: true });
  }
}

function isPathWithinDirectory(candidatePath: string, directoryPath: string): boolean {
  const resolvedCandidate = path.resolve(candidatePath);
  const resolvedDirectory = path.resolve(directoryPath);
  const relativePath = path.relative(resolvedDirectory, resolvedCandidate);
  return relativePath === '' || (!relativePath.startsWith('..') && !path.isAbsolute(relativePath));
}

function getE2eBreakpoints(): Array<{ filePath: string; line: number; enabled: boolean }> {
  return vscode.debug.breakpoints
    .filter((breakpoint): breakpoint is vscode.SourceBreakpoint => breakpoint instanceof vscode.SourceBreakpoint)
    .map(breakpoint => ({
      filePath: breakpoint.location.uri.fsPath,
      line: breakpoint.location.range.start.line,
      enabled: breakpoint.enabled,
    }));
}

function getExtensionFileStatus(context: vscode.ExtensionContext, relativePaths: readonly string[]): Record<string, boolean> {
  if (!Array.isArray(relativePaths) || relativePaths.some(relativePath => typeof relativePath !== 'string' || path.isAbsolute(relativePath) || relativePath.split(/[\\/]/).includes('..'))) {
    throw new Error('Aspire extension E2E getExtensionFileStatus requires relative paths inside the installed extension.');
  }

  return Object.fromEntries(relativePaths.map(relativePath => [
    relativePath,
    fs.existsSync(path.join(context.extension.extensionPath, relativePath)),
  ]));
}

async function getDiagnosticsForFile(filePath: string): Promise<{ message: string; severity: vscode.DiagnosticSeverity; code?: string | number }[]> {
  if (typeof filePath !== 'string' || filePath.length === 0) {
    throw new Error('Aspire extension E2E getDiagnostics requires filePath.');
  }

  const uri = vscode.Uri.file(filePath);
  const document = await vscode.workspace.openTextDocument(uri);
  await vscode.window.showTextDocument(document);
  return vscode.languages.getDiagnostics(uri).map(diagnostic => ({
    message: diagnostic.message,
    severity: diagnostic.severity,
    code: typeof diagnostic.code === 'string' || typeof diagnostic.code === 'number' ? diagnostic.code : undefined,
  }));
}

function getAppHostElement(appHostTreeProvider: AspireAppHostTreeProvider, appHostPath: string | undefined): unknown {
  return appHostPath ? appHostTreeProvider.findAppHostElement(appHostPath) ?? { appHostPath } : undefined;
}

function getResourceElement(appHostTreeProvider: AspireAppHostTreeProvider, resourceName: string, appHostPath?: string): unknown {
  if (typeof resourceName !== 'string' || resourceName.length === 0) {
    throw new Error('Aspire extension E2E resource command requires resourceName.');
  }

  const element = appHostTreeProvider.findResourceElement(resourceName, appHostPath);
  if (!element) {
    throw new Error(`Aspire extension E2E resource command could not find resource '${resourceName}'.`);
  }

  return element;
}

function getEndpointElement(
  appHostTreeProvider: AspireAppHostTreeProvider,
  command: Extract<AspireExtensionE2EControlCommand, { name: 'copyEndpointUrl' | 'openInIntegratedBrowser' }>
): unknown {
  const element = appHostTreeProvider.findEndpointElement({
    appHostPath: command.appHostPath,
    resourceName: command.resourceName,
    url: command.url,
  });
  if (!element) {
    throw new Error('Aspire extension E2E endpoint command could not find a matching endpoint.');
  }

  return element;
}

function getResourceCommandElement(
  appHostTreeProvider: AspireAppHostTreeProvider,
  command: Extract<AspireExtensionE2EControlCommand, { name: 'executeResourceCommandItem' }>
): unknown {
  if (typeof command.resourceName !== 'string' || command.resourceName.length === 0) {
    throw new Error('Aspire extension E2E resource command item requires resourceName.');
  }

  if (typeof command.commandName !== 'string' || command.commandName.length === 0) {
    throw new Error('Aspire extension E2E resource command item requires commandName.');
  }

  const element = appHostTreeProvider.findResourceCommandElement({
    appHostPath: command.appHostPath,
    resourceName: command.resourceName,
    commandName: command.commandName,
  });
  if (!element) {
    throw new Error(`Aspire extension E2E resource command item could not find command '${command.commandName}' on resource '${command.resourceName}'.`);
  }

  return element;
}

function getLogFileElement(appHostTreeProvider: AspireAppHostTreeProvider, appHostPath?: string): unknown {
  const element = appHostTreeProvider.findLogFileElement(appHostPath);
  if (!element) {
    throw new Error('Aspire extension E2E log file command could not find an AppHost log file.');
  }

  return element;
}

function getActiveEditorInfo(): { uri?: string; fileName?: string } {
  const document = vscode.window.activeTextEditor?.document;
  return {
    uri: document?.uri.toString(),
    fileName: document?.fileName,
  };
}

function cloneTerminalCommandEvent(event: AspireTerminalCommandEvent, sequence: number): AspireExtensionE2ETerminalCommand {
  return {
    sequence,
    subcommand: event.subcommand,
    commandLine: event.commandLine,
    showTerminal: event.showTerminal,
    additionalArgs: event.additionalArgs ? [...event.additionalArgs] : undefined,
    containsRedactedArgs: event.containsRedactedArgs,
    executionSuppressed: event.executionSuppressed,
    executionMode: event.executionMode,
  };
}

function cloneDebugLaunchEvent(event: AppHostLaunchRequestedEvent, sequence: number): AspireExtensionE2EDebugLaunch {
  return {
    sequence,
    appHostPath: event.appHostPath,
    command: event.command,
    noDebug: event.noDebug,
    doStep: event.doStep,
    executionSuppressed: event.executionSuppressed,
  };
}

function cloneDebugConsoleOutputEvent(event: AspireDebugConsoleOutputEvent, sequence: number): AspireExtensionE2EDebugConsoleOutput {
  return {
    sequence,
    debugSessionId: event.debugSessionId,
    appHostPath: event.appHostPath,
    category: event.category,
    output: event.output,
  };
}

function getDashboardUrl(dataRepository: AppHostDataRepository, appHostPath?: string): string | undefined {
  return sanitizeDashboardUrl(getSensitiveDashboardUrl(dataRepository, appHostPath));
}

function getSensitiveDashboardUrl(dataRepository: AppHostDataRepository, appHostPath?: string): string | undefined {
  if (appHostPath) {
    const matchingAppHost = dataRepository.appHosts.find(appHost => isMatchingAppHostPath(appHost.appHostPath, appHostPath));
    return matchingAppHost?.dashboardUrl ? stripResourceSuffix(matchingAppHost.dashboardUrl) : undefined;
  }

  if (dataRepository.workspaceAppHost?.dashboardUrl) {
    return stripResourceSuffix(dataRepository.workspaceAppHost.dashboardUrl);
  }

  const appHostsWithDashboard = dataRepository.appHosts.filter(appHost => appHost.dashboardUrl);
  if (appHostsWithDashboard.length > 1) {
    return undefined;
  }

  const dashboardUrl = appHostsWithDashboard[0]?.dashboardUrl ?? dataRepository.workspaceResources.find(resource => resource.dashboardUrl)?.dashboardUrl;

  return dashboardUrl ? stripResourceSuffix(dashboardUrl) : undefined;
}

function cloneAppHostState(appHost: AppHostDisplayInfo, includeSensitiveDashboardUrls: boolean): AspireAppHostState {
  return {
    appHostPath: appHost.appHostPath,
    appHostPid: appHost.appHostPid,
    dashboardUrl: appHost.dashboardUrl && includeSensitiveDashboardUrls ? stripResourceSuffix(appHost.dashboardUrl) : (sanitizeDashboardUrl(appHost.dashboardUrl) ?? null),
    resources: appHost.resources?.map(resource => cloneResourceState(resource, includeSensitiveDashboardUrls)) ?? appHost.resources,
  };
}

function cloneResourceState(resource: ResourceJson, includeSensitiveDashboardUrls: boolean): AspireResourceState {
  return {
    name: resource.name,
    displayName: resource.displayName,
    resourceType: resource.resourceType,
    state: resource.state,
    dashboardUrl: resource.dashboardUrl && includeSensitiveDashboardUrls ? stripResourceSuffix(resource.dashboardUrl) : (sanitizeDashboardUrl(resource.dashboardUrl) ?? null),
    urls: resource.urls?.map(cloneResourceUrlState) ?? null,
    commands: resource.commands ? cloneResourceCommands(resource.commands) : null,
  };
}

function cloneResourceUrlState(url: AspireResourceUrlState): AspireResourceUrlState {
  return {
    name: url.name,
    displayName: url.displayName,
    url: url.url,
    isInternal: url.isInternal,
  };
}

function cloneResourceCommands(commands: ResourceJson['commands']): Record<string, AspireResourceCommandState> | null {
  if (!commands) {
    return null;
  }

  return Object.fromEntries(Object.entries(commands).map(([name, command]) => [name, {
    displayName: command.displayName,
    description: command.description,
    state: command.state,
    visibility: command.visibility,
  }]));
}

function getUnknownCommandName(command: unknown): string {
  if (command && typeof command === 'object' && 'name' in command) {
    return String(command.name);
  }

  return '<missing>';
}

function isE2eBridgeEnabled(): boolean {
  return process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE === 'true' &&
    Boolean(process.env.ASPIRE_EXTENSION_E2E_STATE_FILE && process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE);
}

function stripResourceSuffix(url: string): string {
  const idx = url.indexOf('/?resource=');
  return idx !== -1 ? url.substring(0, idx) : url;
}

function sanitizeDashboardUrl(url: string | null | undefined): string | undefined {
  if (!url) {
    return undefined;
  }

  try {
    return new URL(stripResourceSuffix(url)).origin;
  }
  catch {
    return undefined;
  }
}

function isSamePath(left: string, right: string): boolean {
  const normalizedLeft = path.resolve(left);
  const normalizedRight = path.resolve(right);
  return process.platform === 'win32'
    ? normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
    : normalizedLeft === normalizedRight;
}
