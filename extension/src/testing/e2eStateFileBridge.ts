import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

import { AspireExtensionContext } from '../AspireExtensionContext';
import { getLoggableDebugConfiguration, type AspireDebugSession } from '../debugger/AspireDebugSession';
import { createDebugSessionConfiguration, getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { spawnCliProcess } from '../debugger/languages/cli';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import type { AspireResourceExtendedDebugConfiguration, EnvVar, ExecutableLaunchConfiguration } from '../dcp/types';
import { createStateSnapshot, getSensitiveDashboardUrl, isSamePath } from '../extensionState';
import { AppHostLaunchRequestedEvent, AppHostLaunchService } from '../services/AppHostLaunchService';
import type { AspireDebugConsoleOutputEvent, AspireExtensionE2ECommandInvocation, AspireExtensionE2EControlCommand, AspireExtensionE2EControlPayload, AspireExtensionE2EControlStatus, AspireExtensionE2EDebugConsoleOutput, AspireExtensionE2EDebugLaunch, AspireExtensionE2ETerminalCommand, AspireExtensionStateSnapshot } from '../types/extensionApi';
import { AspireTerminalCommandEvent, AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { onDidInvokeCommand } from '../utils/telemetry';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { AppHostDataRepository } from '../views/AppHostDataRepository';

let atomicWriteSequence = 0;

export function createE2eStateFileBridge(
  context: vscode.ExtensionContext,
  aspireContext: AspireExtensionContext,
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
      state: createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireContext, true),
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
  const debugConsoleOutputSubscription = aspireContext.onDidReceiveDebugConsoleOutput(event => {
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

            const result = await executeE2eControlCommand(context, aspireContext, appHostLaunchService, appHostTreeProvider, terminalProvider, payload.command, markCommandStarted);
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
  aspireContext: AspireExtensionContext,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider,
  terminalProvider: AspireTerminalProvider,
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
    case 'publishAppHost': {
      if (!command.appHostPath) {
        throw new Error('Aspire extension E2E publishAppHost requires appHostPath.');
      }

      const commandPromise = appHostLaunchService.launch(command.appHostPath, 'publish', true);
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
    case 'openResourceTerminal': {
      const element = getResourceElement(appHostTreeProvider, command.resourceName, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.openResourceTerminal', element);
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
      const endpoint = getEndpointElement(appHostTreeProvider, command);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyEndpointUrl', endpoint.element);
      markStarted();
      await commandPromise;
      return await vscode.env.clipboard.readText();
    }
    case 'openInIntegratedBrowser': {
      const endpoint = getEndpointElement(appHostTreeProvider, command);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.openInIntegratedBrowser', endpoint.element);
      markStarted();
      await commandPromise;
      return { url: endpoint.url };
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
    case 'getResourceDebuggerExtensions': {
      markStarted();
      return getResourceDebuggerExtensions().map(extension => ({
        resourceType: extension.resourceType,
        debugAdapter: extension.debugAdapter,
        extensionId: extension.extensionId,
        supportedFileTypes: extension.getSupportedFileTypes(),
      }));
    }
    case 'createResourceDebugConfiguration': {
      markStarted();
      const launchConfig = getE2eLaunchConfiguration(command.launchConfig);
      const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === launchConfig.type);
      if (!debuggerExtension) {
        throw new Error(`No resource debugger extension is registered for launch configuration type '${launchConfig.type}'.`);
      }

      const runId = 'e2e-resource-debug-configuration';
      try {
        const debugConfiguration = await createDebugSessionConfiguration(
          { type: 'aspire', request: 'launch', name: 'E2E resource debug configuration', program: '' },
          launchConfig,
          getE2eStringArray(command.args, 'args'),
          getE2eEnvVars(command.env),
          {
            debug: command.debug ?? true,
            runId,
            debugSessionId: 'e2e-debug-session',
            isApphost: false,
            debugSession: {} as AspireDebugSession
          },
          debuggerExtension);

        return getLoggableDebugConfiguration(debugConfiguration, false);
      } finally {
        cleanupRun(runId);
      }
    }
    case 'proveMauiResourceDebugging': {
      markStarted();
      return await proveMauiResourceDebugging(command, aspireContext, appHostTreeProvider, terminalProvider);
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

function getE2eLaunchConfiguration(value: unknown): ExecutableLaunchConfiguration {
  if (!value || typeof value !== 'object' || !('type' in value) || typeof value.type !== 'string' || value.type.length === 0) {
    throw new Error('Aspire extension E2E createResourceDebugConfiguration requires a launchConfig object with a non-empty type.');
  }

  return value as ExecutableLaunchConfiguration;
}

function getE2eStringArray(value: unknown, propertyName: string): string[] | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (!Array.isArray(value) || !value.every(item => typeof item === 'string')) {
    throw new Error(`Aspire extension E2E createResourceDebugConfiguration ${propertyName} must be an array of strings when provided.`);
  }

  return [...value];
}

function getE2eEnvVars(value: unknown): EnvVar[] {
  if (value === undefined) {
    return [];
  }

  if (!Array.isArray(value) || !value.every(item =>
    item &&
    typeof item === 'object' &&
    'name' in item &&
    typeof item.name === 'string' &&
    'value' in item &&
    typeof item.value === 'string')) {
    throw new Error('Aspire extension E2E createResourceDebugConfiguration env must be an array of { name, value } strings when provided.');
  }

  return value.map(item => ({ name: item.name, value: item.value }));
}

type MauiResourceDebugProofCommand = Extract<AspireExtensionE2EControlCommand, { name: 'proveMauiResourceDebugging' }>;

interface DebugSessionSnapshot {
  id: string;
  type: string;
  name: string;
  parentSessionId?: string;
  parentSessionType?: string;
  configuration: Record<string, unknown>;
}

interface DebugAdapterLaunchRequest {
  sessionId: string;
  sessionType: string;
  sessionName: string;
  arguments?: unknown;
}

interface DebugAdapterStoppedEvent {
  sessionId: string;
  sessionType: string;
  sessionName: string;
  reason?: string;
  threadId?: number;
}

interface DebugAdapterOutputEvent {
  sessionId: string;
  sessionType: string;
  output: string;
}

interface DebugAdapterMessageSummary {
  sessionId: string;
  sessionType: string;
  sessionName: string;
  command?: string;
  success?: boolean;
  body?: unknown;
}

async function proveMauiResourceDebugging(command: MauiResourceDebugProofCommand, aspireContext: AspireExtensionContext, appHostTreeProvider: AspireAppHostTreeProvider, terminalProvider: AspireTerminalProvider): Promise<unknown> {
  const appHostPath = getE2eWorkspacePath(command.appHostPath);
  const sourcePath = getE2eWorkspacePath(command.sourcePath);
  const resourceName = getE2eRequiredString(command.resourceName, 'Aspire extension E2E MAUI proof requires resourceName.');
  const breakpointLine = getE2eBreakpointLine(command.breakpointLine);
  const timeoutMs = getE2ePositiveInteger(command.timeoutMs, 300000, 'timeoutMs');
  const pauseOnBreakpointMs = getE2ePositiveInteger(command.pauseOnBreakpointMs, 0, 'pauseOnBreakpointMs');
  const appHostStartupTimeoutMs = Math.min(timeoutMs, 180000);
  const resourceStartTimeoutMs = Math.min(timeoutMs, 180000);
  const breakpointTimeoutMs = Math.min(timeoutMs, 240000);
  const sourceText = fs.readFileSync(sourcePath, 'utf8');
  const breakpointText = sourceText.split(/\r?\n/)[breakpointLine]?.trim();

  const debugSessions: DebugSessionSnapshot[] = [];
  const sessionById = new Map<string, vscode.DebugSession>();
  const launchRequests: DebugAdapterLaunchRequest[] = [];
  const debugAdapterResponses: DebugAdapterMessageSummary[] = [];
  const stoppedEvents: DebugAdapterStoppedEvent[] = [];
  const outputEvents: DebugAdapterOutputEvent[] = [];
  const breakpointRequests: DebugAdapterMessageSummary[] = [];
  const breakpointResponses: DebugAdapterMessageSummary[] = [];
  let resourceCommandResult: Awaited<ReturnType<typeof runAspireCliForE2E>> | undefined;

  const sessionSubscription = vscode.debug.onDidStartDebugSession(session => {
    sessionById.set(session.id, session);
    debugSessions.push(toDebugSessionSnapshot(session));
  });
  const trackerRegistration = vscode.debug.registerDebugAdapterTrackerFactory('*', {
    createDebugAdapterTracker(session) {
      return {
        onWillReceiveMessage(message) {
          if (message?.type === 'request' && message.command === 'launch') {
            launchRequests.push({
              sessionId: session.id,
              sessionType: session.type,
              sessionName: session.name,
              arguments: redactDebugAdapterArguments(message.arguments),
            });
          }
          if (message?.type === 'request' && (message.command === 'setBreakpoints' || message.command === 'configurationDone')) {
            breakpointRequests.push({
              sessionId: session.id,
              sessionType: session.type,
              sessionName: session.name,
              command: message.command,
              body: redactDebugAdapterArguments(message.arguments),
            });
          }
        },
        onDidSendMessage(message) {
          if (message?.type === 'response' && message.success === false) {
            debugAdapterResponses.push({
              sessionId: session.id,
              sessionType: session.type,
              sessionName: session.name,
              command: message.command,
              success: message.success,
              body: redactDebugAdapterArguments(message),
            });
          }
          if (message?.type === 'response' && (message.command === 'setBreakpoints' || message.command === 'configurationDone')) {
            breakpointResponses.push({
              sessionId: session.id,
              sessionType: session.type,
              sessionName: session.name,
              command: message.command,
              success: message.success,
              body: redactDebugAdapterArguments(message.body),
            });
          }
          if (message?.type === 'event' && message.event === 'stopped') {
            stoppedEvents.push({
              sessionId: session.id,
              sessionType: session.type,
              sessionName: session.name,
              reason: message.body?.reason,
              threadId: message.body?.threadId,
            });
          }
          if (message?.type === 'event' && message.event === 'output') {
            outputEvents.push({
              sessionId: session.id,
              sessionType: session.type,
              output: String(message.body?.output ?? ''),
            });
            if (outputEvents.length > 200) {
              outputEvents.shift();
            }
          }
        }
      };
    }
  });

  const breakpoint = new vscode.SourceBreakpoint(
    new vscode.Location(vscode.Uri.file(sourcePath), new vscode.Position(breakpointLine, 0)),
    true);
  vscode.debug.addBreakpoints([breakpoint]);

  try {
    const appHostElement = getAppHostElement(appHostTreeProvider, appHostPath);
    await vscode.commands.executeCommand('aspire-vscode.debugAppHost', appHostElement);

    const aspireDebugSession = await waitForE2eValue(
      'Aspire AppHost debug startup completion',
      appHostStartupTimeoutMs,
      () => aspireContext.aspireDebugSessions.find(session =>
        session.startupCompleted &&
        typeof session.appHostPath === 'string' &&
        isSamePath(session.appHostPath, appHostPath)));

    resourceCommandResult = await runAspireCliForE2E(
      terminalProvider,
      ['resource', resourceName, 'start', '--apphost', appHostPath, '--non-interactive', '--nologo'],
      path.dirname(appHostPath),
      resourceStartTimeoutMs,
      aspireDebugSession.debugSessionId);

    let stoppedEvent: { stoppedEvent: DebugAdapterStoppedEvent; stackTrace: { stackFrames?: Array<{ source?: { path?: string }; line?: number }> }; matchingFrame: { source?: { path?: string }; line?: number } };
    try {
      stoppedEvent = await waitForE2eValue(
        `MAUI breakpoint in ${sourcePath}:${breakpointLine + 1}`,
        breakpointTimeoutMs,
        async () => {
          for (const stoppedEvent of stoppedEvents) {
            if (stoppedEvent.threadId === undefined) {
              continue;
            }

            const session = sessionById.get(stoppedEvent.sessionId);
            if (!session) {
              continue;
            }

            let stackTrace: { stackFrames?: Array<{ source?: { path?: string }; line?: number }> } | undefined;
            try {
              stackTrace = await session.customRequest('stackTrace', {
                threadId: stoppedEvent.threadId,
                startFrame: 0,
                levels: 20,
              });
            }
            catch {
              continue;
            }
            const matchingFrame = stackTrace?.stackFrames?.find((frame: { source?: { path?: string }; line?: number }) =>
              typeof frame.source?.path === 'string' && isSamePath(frame.source.path, sourcePath));
            if (matchingFrame) {
              return { stoppedEvent, stackTrace: stackTrace!, matchingFrame };
            }
          }

          return undefined;
        });
    }
    catch (error) {
      throw new Error(`${error instanceof Error ? error.message : String(error)}
Diagnostics:
${JSON.stringify({
        resourceCommandResult,
        debugSessions,
        launchRequests,
        debugAdapterResponses,
        breakpointRequests,
        breakpointResponses,
        stoppedEvents,
        outputSample: outputEvents.slice(-40),
      }, undefined, 2)}`);
    }

    if (stoppedEvent.matchingFrame.line !== breakpointLine + 1) {
      throw new Error(`Expected MAUI breakpoint line ${breakpointLine + 1}, got ${stoppedEvent.matchingFrame.line}.`);
    }

    if (pauseOnBreakpointMs > 0) {
      await delay(pauseOnBreakpointMs);
    }

    return {
      proof: 'aspire-maui-resource-debug-breakpoint-hit',
      appHostPath,
      resourceName,
      timeouts: {
        appHostStartupTimeoutMs,
        resourceStartTimeoutMs,
        breakpointTimeoutMs,
      },
      breakpoint: {
        sourcePath,
        line: breakpointLine + 1,
        text: breakpointText,
      },
      resourceCommandResult,
      debugSessions,
      launchRequests,
      debugAdapterResponses,
      breakpointRequests,
      breakpointResponses,
      stoppedEvents,
      matchingStackFrame: stoppedEvent.matchingFrame,
      topStackFrame: stoppedEvent.stackTrace?.stackFrames?.[0],
      outputSample: outputEvents.slice(-40),
    };
  } finally {
    vscode.debug.removeBreakpoints([breakpoint]);
    sessionSubscription.dispose();
    trackerRegistration.dispose();
    await vscode.debug.stopDebugging();
  }
}

function toDebugSessionSnapshot(session: vscode.DebugSession): DebugSessionSnapshot {
  return {
    id: session.id,
    type: session.type,
    name: session.name,
    parentSessionId: session.parentSession?.id,
    parentSessionType: session.parentSession?.type,
    configuration: getLoggableDebugConfiguration(session.configuration as AspireResourceExtendedDebugConfiguration, false) as Record<string, unknown>,
  };
}

function redactDebugAdapterArguments(value: unknown): unknown {
  if (!value || typeof value !== 'object') {
    return value;
  }

  const copy = { ...(value as Record<string, unknown>) };
  if ('env' in copy) {
    copy.env = '<redacted>';
  }
  if ('environmentVariables' in copy) {
    copy.environmentVariables = '<redacted>';
  }

  return copy;
}

async function runAspireCliForE2E(terminalProvider: AspireTerminalProvider, args: string[], workingDirectory: string, timeoutMs: number, debugSessionId: string): Promise<{ exitCode: number | null; stdout: string; stderr: string }> {
  const cliPath = await terminalProvider.getAspireCliExecutablePath();
  return await new Promise((resolve, reject) => {
    const stdout: string[] = [];
    const stderr: string[] = [];
    let completed = false;
    const timeout = setTimeout(() => {
      if (completed) {
        return;
      }

      completed = true;
      child.kill('SIGTERM');
      reject(new Error(`${cliPath} ${args.join(' ')} timed out after ${timeoutMs}ms.\nstdout:\n${stdout.join('')}\nstderr:\n${stderr.join('')}`));
    }, timeoutMs);

    const child = spawnCliProcess(terminalProvider, cliPath, args, {
      workingDirectory,
      stdoutCallback: data => stdout.push(data),
      stderrCallback: data => stderr.push(data),
      exitCallback: code => {
        if (completed) {
          return;
        }

        completed = true;
        clearTimeout(timeout);
        const result = { exitCode: code, stdout: stdout.join(''), stderr: stderr.join('') };
        if (code === 0) {
          resolve(result);
        } else {
          reject(new Error(`${cliPath} ${args.join(' ')} exited with code ${code}.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`));
        }
      },
      errorCallback: error => {
        if (completed) {
          return;
        }

        completed = true;
        clearTimeout(timeout);
        reject(error);
      },
      noExtensionVariables: true,
      env: Object.entries(terminalProvider.createDcpRunSessionEnvironment(debugSessionId, false))
        .map(([name, value]) => ({ name, value: String(value) }))
    });
  });
}

async function waitForE2eValue<T>(description: string, timeoutMs: number, getValue: () => T | undefined | Promise<T | undefined>): Promise<T> {
  const started = Date.now();
  let lastError: string | undefined;
  while (Date.now() - started < timeoutMs) {
    try {
      const value = await getValue();
      if (value !== undefined) {
        return value;
      }
    }
    catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }

    await delay(500);
  }

  throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}. Last error: ${lastError ?? '<none>'}`);
}

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function getE2eRequiredString(value: unknown, errorMessage: string): string {
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(errorMessage);
  }

  return value;
}

function getE2ePositiveInteger(value: unknown, defaultValue: number, propertyName: string): number {
  if (value === undefined) {
    return defaultValue;
  }

  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0) {
    throw new Error(`Aspire extension E2E MAUI proof ${propertyName} must be a non-negative integer when provided.`);
  }

  return value;
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
): { element: unknown; url: string } {
  const element = appHostTreeProvider.findEndpointElement({
    appHostPath: command.appHostPath,
    resourceName: command.resourceName,
    url: command.url,
  });
  if (!element) {
    throw new Error('Aspire extension E2E endpoint command could not find a matching endpoint.');
  }

  if (!hasEndpointUrl(element)) {
    throw new Error('Aspire extension E2E endpoint command found an endpoint tree item without a URL.');
  }

  return { element, url: element.url };
}

function hasEndpointUrl(element: unknown): element is { url: string } {
  return typeof element === 'object'
    && element !== null
    && 'url' in element
    && typeof element.url === 'string'
    && element.url.length > 0;
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

function getUnknownCommandName(command: unknown): string {
  if (command && typeof command === 'object' && 'name' in command) {
    return String(command.name);
  }

  return '<missing>';
}

export function isE2eBridgeEnabled(): boolean {
  return process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE === 'true' &&
    Boolean(process.env.ASPIRE_EXTENSION_E2E_STATE_FILE && process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE);
}
