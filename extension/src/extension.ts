import * as vscode from 'vscode';

import { RpcClient } from './server/rpcClient';
import { extensionLogOutputChannel } from './utils/logging';
import { initializeTelemetry, sendTelemetryEvent } from './utils/telemetry';
import { MeaningfulEngagementReporter } from './utils/meaningfulEngagement';
import { AspireDebugAdapterDescriptorFactory } from './debugger/AspireDebugAdapterDescriptorFactory';
import { AspireDebugConfigurationProvider } from './debugger/AspireDebugConfigurationProvider';
import { AspireExtensionContext } from './AspireExtensionContext';
import AspireRpcServer, { RpcServerConnectionInfo } from './server/AspireRpcServer';
import AspireDcpServer from './dcp/AspireDcpServer';
import { TestRunSessionManager } from './dcp/TestRunSessionManager';
import { AspireTerminalProvider } from './utils/AspireTerminalProvider';
import { MessageConnection } from 'vscode-jsonrpc';
import { checkForExistingAppHostPathInWorkspace } from './utils/workspace';
import { AspireEditorCommandProvider } from './editor/AspireEditorCommandProvider';
import { AspirePackageRestoreProvider } from './utils/AspirePackageRestoreProvider';
import { AspireAppHostTreeProvider } from './views/AspireAppHostTreeProvider';
import { AppHostDataRepository } from './data/AppHostDataRepository';
import { AspireMcpServerDefinitionProvider } from './mcp/AspireMcpServerDefinitionProvider';
import { AspireGutterDecorationProvider } from './editor/AspireGutterDecorationProvider';
import { AppHostFilePresenceWatcher } from './editor/AppHostFilePresenceWatcher';
import { readGitCommitSha } from './utils/versionInfo';
import { AppHostDiscoveryService } from './utils/appHostDiscovery';
import { ConfigInfoProvider } from './utils/configInfoProvider';
import { AppHostLaunchService } from './services/AppHostLaunchService';
import { stopExternalAppHost } from './services/AppHostStopper';
import { cloneAppHostState, createStateSnapshot, getDashboardUrl } from './extensionState';
import { createE2eStateFileBridge } from './testing/e2eStateFileBridge';
import type { AspireAppHostState, AspireExtensionApi, AspireExtensionStateSnapshot, WaitForStateOptions } from './types/extensionApi';
import { AppHostsViewTelemetry } from './views/AppHostsViewTelemetry';
import { CliPathEnvironmentSynchronizer } from './utils/cliPathEnvironment';
import { CliPathRejectionNotifier } from './utils/cliPathRejectionNotifier';
import { cliPathResolver } from './utils/cliPath';
import { AppHostLifecycleToolService, registerAppHostLifecycleTools } from './lm/appHostLifecycleTools';
import { registerInstrumentedCommand } from './activation/instrumentedCommand';
import { registerCliCommands } from './activation/registerCliCommands';
import { registerTreeViewCommands } from './activation/registerTreeViewCommands';
import { registerCodeLensCommands } from './activation/registerCodeLensCommands';
import { resetEditorAssistanceWindowState } from './services/editorAssistanceWindowState';
import { SafeAppHostTargetResolver } from './lm/safeAppHostTargetResolver';
import { EditorStateSnapshotService } from './lm/editorStateSnapshotService';
import { EditorAssistanceToolService } from './lm/editorAssistanceToolService';
import { registerEditorAssistanceTools } from './lm/editorAssistanceToolAdapters';
import { EditorUiHandoffService } from './lm/editorUiHandoffService';
import { readLatestLaunchFailures } from './services/launchFailureJournal';
import { getHotReloadDiagnostics, initializeHotReloadAdvisory } from './debugger/hotReload';
import { OutdatedCliNotifier } from './utils/outdatedCliNotifier';
import { onDidResolveCliForOperation } from './utils/cliOperationResolution';
import { FileSystemOutdatedCliSuppressionStore } from './utils/outdatedCliSuppressionStore';

let aspireExtensionContext = new AspireExtensionContext();

export async function activate(context: vscode.ExtensionContext) {
  resetEditorAssistanceWindowState();
  aspireExtensionContext = new AspireExtensionContext();
  initializeHotReloadAdvisory(context.workspaceState);

  const gitCommitSha = readGitCommitSha(context);
  extensionLogOutputChannel.info(`Activating Aspire extension (commit: ${gitCommitSha})`);
  initializeTelemetry(context);
  sendTelemetryEvent('aspire/vscode/extension/activated', {
    workspace_open: vscode.workspace.workspaceFolders?.length ? 'true' : 'false',
    extension_mode: getExtensionModeForTelemetry(context.extensionMode),
  }, {
    workspace_folders: vscode.workspace.workspaceFolders?.length ?? 0,
  });

  const terminalProvider = new AspireTerminalProvider(context.subscriptions, undefined, cliPathResolver);
  const testRunSessionManager = new TestRunSessionManager();

  // Keep VS Code's contributed terminal/task environment in sync with the
  // configured or discovered CLI path so MSBuild's ResolveAspireCliBundle task
  // and tools spawned from integrated terminals use the same installation as
  // the extension (https://github.com/microsoft/aspire/issues/18073). Start
  // resolution before other activation work, then await it before returning so
  // the first user-initiated terminal already inherits AspireCliPath.
  const cliPathEnvironmentSynchronizer = new CliPathEnvironmentSynchronizer(
    context.environmentVariableCollection,
    cliPathResolver,
    context.subscriptions,
    target => terminalProvider.invalidateSharedAspireTerminal(target));
  context.subscriptions.push(cliPathEnvironmentSynchronizer);
  // A rejected configured CLI path otherwise only appears in the output channel, which hides the
  // fact that commands are running a different CLI than the one the user pinned.
  context.subscriptions.push(new CliPathRejectionNotifier());
  const cliPathEnvironmentInitialization = cliPathEnvironmentSynchronizer.initialize().catch(error => {
    extensionLogOutputChannel.warn(`Initial Aspire CLI path resolution failed: ${String(error)}`);
  });

  const rpcServer = await AspireRpcServer.create(
    (rpcServerConnectionInfo: RpcServerConnectionInfo, connection: MessageConnection, token: string, debugSessionId: string | null) => {
      const client: RpcClient = new RpcClient(connection, debugSessionId, () => aspireExtensionContext.getAspireDebugSession(client.debugSessionId), context.globalState);
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

  testRunSessionManager.initializeConnectionInfo(dcpServer.connectionInfo);

  terminalProvider.rpcServerConnectionInfo = rpcServer.connectionInfo;
  terminalProvider.dcpServerConnectionInfo = dcpServer.connectionInfo;
  terminalProvider.closeAllOpenAspireTerminals();

  const configInfoProvider = new ConfigInfoProvider(terminalProvider);
  const outdatedCliNotifier = new OutdatedCliNotifier(
    configInfoProvider,
    undefined,
    Date.now,
    new FileSystemOutdatedCliSuppressionStore(context.globalStorageUri.fsPath));
  context.subscriptions.push(outdatedCliNotifier);
  context.subscriptions.push(onDidResolveCliForOperation(({ target, cliPath }) => {
    void outdatedCliNotifier.notifyIfOutdated(target, cliPath).catch(error => {
      extensionLogOutputChannel.warn(`Unable to check Aspire CLI version: ${String(error)}`);
    });
  }));
  const appHostDiscoveryService = new AppHostDiscoveryService(terminalProvider, configInfoProvider);
  context.subscriptions.push(appHostDiscoveryService);

  // Meaningful-engagement reporter must outlive every command callback so it
  // can observe the first invocation. Wire it before any command is
  // registered so even synchronous early invocations (rare but possible) are
  // observed via the telemetry pipeline.
  engagement = new MeaningfulEngagementReporter(appHostDiscoveryService);
  context.subscriptions.push(engagement);

  const appHostLaunchService = new AppHostLaunchService(configInfoProvider);
  context.subscriptions.push(appHostLaunchService);

  const editorCommandProvider = new AspireEditorCommandProvider(appHostDiscoveryService, appHostLaunchService);

  const cliCommandRegistrations = registerCliCommands(terminalProvider, editorCommandProvider, configInfoProvider);

  // Aspire panel - running app hosts tree view
  const dataRepository = new AppHostDataRepository(terminalProvider, appHostDiscoveryService, configInfoProvider);
  appHostLaunchService.setEditorSessionProvider(() => aspireExtensionContext.aspireDebugSessions);
  appHostLaunchService.setRunningAppHostProvider(async token => {
    const appHosts = await dataRepository.fetchRunningAppHostsOnce(token);
    return appHosts.map(appHost => ({ appHostPath: appHost.appHostPath }));
  });
  appHostLaunchService.setExternalAppHostStopper((appHost, token) =>
    stopExternalAppHost(terminalProvider, appHost, token));
  const appHostTreeProvider = new AspireAppHostTreeProvider(dataRepository, terminalProvider, appHostLaunchService, context.globalState, vscode.env.clipboard, configInfoProvider);
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
  const debugSessionRefreshRegistration = appHostLaunchService.onDidTerminateAppHostDebugSession(event => {
    if (event.shouldRequestStopRefresh) {
      appHostTreeProvider.notifyAppHostStopping(event.appHostPath, event.shouldMarkAppHostStopping);
    }
  });

  // Also drive data sources based on whether an AppHost file is currently visible in any editor.
  // This makes resource code-lens decorations on a fresh AppHost file work without first opening the panel.
  const appHostFilePresenceWatcher = new AppHostFilePresenceWatcher(dataRepository);
  context.subscriptions.push(appHostFilePresenceWatcher);

  // View-shown telemetry. Subscribes to visibility changes on the same tree
  // view; debounced internally so rapid VS Code panel toggles do not spam.
  const appHostsViewTelemetry = new AppHostsViewTelemetry(appHostTreeView, dataRepository);
  context.subscriptions.push(appHostsViewTelemetry);

  const treeViewCommandRegistrations = registerTreeViewCommands(appHostTreeProvider, dataRepository);

  // Set initial context for welcome view
  vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', true);
  vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', true);
  vscode.commands.executeCommand('setContext', 'aspire.loading', true);

  // Activate the data repository. Workspace describe watching and global polling begin when the panel is visible.
  dataRepository.activate();

  context.subscriptions.push(
    appHostTreeView,
    ...treeViewCommandRegistrations,
    debugSessionRefreshRegistration,
    { dispose: () => { appHostTreeProvider.dispose(); dataRepository.dispose(); } });

  // CodeLens provider — shows Debug on pipeline steps, resource state on resources
  const codeLensRegistrations = registerCodeLensCommands(appHostTreeProvider, appHostTreeView, dataRepository, terminalProvider, editorCommandProvider, context.globalState);
  context.subscriptions.push(...codeLensRegistrations);

  // Gutter decorations — colored dots next to resources showing runtime state
  const gutterDecorationProvider = new AspireGutterDecorationProvider(appHostTreeProvider);
  context.subscriptions.push(gutterDecorationProvider);

  context.subscriptions.push(...cliCommandRegistrations);

  const dynamicDebugConfigProvider = new AspireDebugConfigurationProvider(appHostDiscoveryService, appHostLaunchService, context.workspaceState, vscode.DebugConfigurationProviderTriggerKind.Dynamic);
  const initialDebugConfigProvider = new AspireDebugConfigurationProvider(appHostDiscoveryService, appHostLaunchService, context.workspaceState, vscode.DebugConfigurationProviderTriggerKind.Initial);
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider('aspire', dynamicDebugConfigProvider, vscode.DebugConfigurationProviderTriggerKind.Dynamic)
  );
  context.subscriptions.push(
    vscode.debug.registerDebugConfigurationProvider('aspire', initialDebugConfigProvider, vscode.DebugConfigurationProviderTriggerKind.Initial)
  );

  context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory('aspire', new AspireDebugAdapterDescriptorFactory(rpcServer, dcpServer, terminalProvider, aspireExtensionContext.addAspireDebugSession.bind(aspireExtensionContext), aspireExtensionContext.removeAspireDebugSession.bind(aspireExtensionContext), appHostLaunchService.trackAppHostDebugSession.bind(appHostLaunchService))));
  context.subscriptions.push(testRunSessionManager.listenForLeasedDebugSessions({
    rpcServer,
    dcpServer,
    terminalProvider,
    addAspireDebugSession: aspireExtensionContext.addAspireDebugSession.bind(aspireExtensionContext),
    removeAspireDebugSession: aspireExtensionContext.removeAspireDebugSession.bind(aspireExtensionContext),
    getAspireDebugSession: aspireExtensionContext.getAspireDebugSession.bind(aspireExtensionContext),
  }));

  aspireExtensionContext.initialize(rpcServer, context, dynamicDebugConfigProvider, dcpServer, terminalProvider, editorCommandProvider);

  // Register Aspire MCP server definition provider so one Aspire MCP server per discovered
  // AppHost appears automatically in VS Code's MCP tools list for Aspire workspaces.
  // The provider subscribes to discovery and configuration events in its constructor, so it is
  // only built when the MCP API exists to dispose it; otherwise those subscriptions would outlive
  // the extension on VS Code versions that cannot host the provider at all.
  if (typeof vscode.lm?.registerMcpServerDefinitionProvider === 'function') {
    const mcpProvider = new AspireMcpServerDefinitionProvider({
      appHostDiscovery: appHostDiscoveryService,
      capabilityProbe: configInfoProvider,
    }, cliPathResolver);
    context.subscriptions.push(mcpProvider);
    context.subscriptions.push(vscode.lm.registerMcpServerDefinitionProvider('aspire-mcp-server', mcpProvider));
    void mcpProvider.refresh();
  }

  // Language model tools that let an agent use the same AppHost lifecycle service as the
  // editor and Aspire tree instead of maintaining a separate start/stop policy.
  const appHostTargetResolver = new SafeAppHostTargetResolver(appHostDiscoveryService);
  const appHostLifecycleToolService = new AppHostLifecycleToolService({
    launchService: appHostLaunchService,
    discoveryService: appHostDiscoveryService,
  }, appHostTargetResolver);
  context.subscriptions.push(appHostLifecycleToolService);
  const appHostLifecycleToolRegistration = registerAppHostLifecycleTools(appHostLifecycleToolService);
  context.subscriptions.push(appHostLifecycleToolRegistration);

  // Editor-assistance tools use the same safe AppHost registry and editor-owned session
  // projections as lifecycle tools. UI side effects stay isolated behind the handoff service.
  const editorStateSnapshotService = new EditorStateSnapshotService({
    launchService: appHostLaunchService,
    targetResolver: appHostTargetResolver,
  });
  const editorUiHandoffService = new EditorUiHandoffService({
    targetResolver: appHostTargetResolver,
    appHostRepository: dataRepository,
    output: extensionLogOutputChannel,
    getAspireDebugSessionOwners: () =>
      aspireExtensionContext.getAspireDebugSessionDashboardOwners(),
  });
  const editorAssistanceToolService = new EditorAssistanceToolService({
    targetResolver: appHostTargetResolver,
    snapshotService: editorStateSnapshotService,
    resourceRepository: dataRepository,
    getEditorResourceSessions: () => aspireExtensionContext.editorResourceSessions,
    readLatestLaunchFailures,
    readHotReloadDiagnostics: getHotReloadDiagnostics,
    uiHandoffService: editorUiHandoffService,
  });
  const editorAssistanceToolRegistration = registerEditorAssistanceTools(editorAssistanceToolService);
  context.subscriptions.push(editorAssistanceToolRegistration);

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
  const e2eLanguageModelTools = new Map<string, {
    readonly tool: vscode.LanguageModelTool<unknown>;
    readonly registered: boolean;
  }>();
  for (const [name, tool] of appHostLifecycleToolRegistration.tools) {
    e2eLanguageModelTools.set(name, {
      tool,
      registered: appHostLifecycleToolRegistration.registered,
    });
  }
  for (const [name, tool] of editorAssistanceToolRegistration.tools) {
    e2eLanguageModelTools.set(name, {
      tool,
      registered: editorAssistanceToolRegistration.registered,
    });
  }
  const e2eStateFileBridge = createE2eStateFileBridge(context, aspireExtensionContext, dataRepository, appHostLaunchService, appHostTreeProvider, terminalProvider, onDidChangeStateEmitter.event, e2eLanguageModelTools);
  context.subscriptions.push(e2eStateFileBridge);

  await cliPathEnvironmentInitialization;
  const api = createExtensionApi(context, rpcServer, dcpServer, testRunSessionManager, dataRepository, appHostLaunchService, appHostTreeProvider, onDidChangeStateEmitter.event);

  return Object.freeze(api);
}

export function deactivate(): Promise<void> {
  return aspireExtensionContext.deactivate();
}

function getExtensionModeForTelemetry(mode: vscode.ExtensionMode): string {
  switch (mode) {
    case vscode.ExtensionMode.Production:
      return 'production';
    case vscode.ExtensionMode.Development:
      return 'development';
    case vscode.ExtensionMode.Test:
      return 'test';
    default:
      return 'unknown';
  }
}

function createExtensionApi(
  context: vscode.ExtensionContext,
  rpcServer: AspireRpcServer,
  dcpServer: AspireDcpServer,
  testRunSessionManager: TestRunSessionManager,
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
    apiVersion: 2,
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
    async getRunningAppHosts(): Promise<readonly AspireAppHostState[]> {
      const appHosts = await dataRepository.fetchAppHostsOnce();
      return appHosts.map(appHost => cloneAppHostState(appHost, false));
    },
    async stopResource(resourceName: string, appHostPath: string): Promise<void> {
      await dataRepository.runResourceCommand(resourceName, appHostPath, 'stop');
    },
    async startResource(resourceName: string, appHostPath: string): Promise<void> {
      await dataRepository.runResourceCommand(resourceName, appHostPath, 'start');
    },
    acquireTestRunSession: (options) => testRunSessionManager.acquireTestRunSession(options),
    releaseTestRunSession: (id) => testRunSessionManager.releaseTestRunSession(id),
  };
  if (context.extensionMode === vscode.ExtensionMode.Test) {
    api.__testOnlyRpcServerInfo = rpcServer.connectionInfo;
  }

  return api;
}
