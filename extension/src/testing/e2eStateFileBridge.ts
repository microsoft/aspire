import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { randomUUID } from 'crypto';

import { AspireExtensionContext } from '../AspireExtensionContext';
import { getLoggableDebugConfiguration, type AspireDebugSession } from '../debugger/AspireDebugSession';
import { createDebugSessionConfiguration, getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { projectDebuggerExtension } from '../debugger/languages/dotnet';
import { redactCliArgsForLogging, spawnCliProcess, terminateCliProcess } from '../utils/process/cliProcess';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import type { AspireResourceExtendedDebugConfiguration, EnvVar, ExecutableLaunchConfiguration } from '../dcp/types';
import { createStateSnapshot, getSensitiveDashboardUrl, isSamePath } from '../extensionState';
import type { PreparableLanguageModelTool } from '../lm/languageModelToolContracts';
import { AppHostLaunchRequestedEvent, AppHostLaunchService } from '../services/AppHostLaunchService';
import type { AspireDebugConsoleOutputEvent, AspireExtensionE2EBrowserDebugSession, AspireExtensionE2ECodeLensProbeResult, AspireExtensionE2ECommandInvocation, AspireExtensionE2EControlCommand, AspireExtensionE2EControlPayload, AspireExtensionE2EControlStatus, AspireExtensionE2EDebugConsoleOutput, AspireExtensionE2EDebugLaunch, AspireExtensionE2EStoppingPathEvent, AspireExtensionE2ETaskProcessEvent, AspireExtensionE2ETerminalCommand, AspireExtensionStateSnapshot } from '../types/extensionApi';
import { AspireTerminalCommandEvent, AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { delay } from '../utils/async';
import { dashboardDefaultChangedNotificationKey } from '../utils/dashboardNotificationState';
import { extensionLogOutputChannel } from '../utils/logging';
import { isCommandCancellation, onDidInvokeCommand } from '../utils/telemetry';
import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { ResourceItem } from '../views/treeItems/resourceItems';
import { ResourceJson } from '../data/appHostCliContracts';
import { AppHostDataRepository } from '../data/AppHostDataRepository';
import { getSupportedCapabilities, javaLanguageExtensionId } from '../capabilities';
import { getCliPathTargetKey, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

let atomicWriteSequence = 0;

export function createE2eStateFileBridge(
  context: vscode.ExtensionContext,
  aspireContext: AspireExtensionContext,
  dataRepository: AppHostDataRepository,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider,
  terminalProvider: AspireTerminalProvider,
  onDidChangeState: vscode.Event<AspireExtensionStateSnapshot>,
  preparableLanguageModelTools: ReadonlyMap<string, PreparableLanguageModelTool>,
): vscode.Disposable {
  const stateFile = process.env.ASPIRE_EXTENSION_E2E_STATE_FILE;
  const controlFile = process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE;
  // Identifies this run so a host left behind by an earlier run cannot service this run's control
  // commands or overwrite its state file — both files live at a stable per-shard path.
  const runId = process.env.ASPIRE_EXTENSION_E2E_RUN_ID;
  if (!isE2eBridgeEnabled() || !stateFile || !controlFile) {
    return new vscode.Disposable(() => undefined);
  }

  const extensionHostSessionId = randomUUID();
  const commandInvocations: AspireExtensionE2ECommandInvocation[] = [];
  const terminalCommands: AspireExtensionE2ETerminalCommand[] = [];
  const debugLaunches: AspireExtensionE2EDebugLaunch[] = [];
  const debugConsoleOutputs: AspireExtensionE2EDebugConsoleOutput[] = [];
  const stoppingPathEvents: AspireExtensionE2EStoppingPathEvent[] = [];
  const taskProcessEvents: AspireExtensionE2ETaskProcessEvent[] = [];
  // VS Code's browser debug sessions are not part of the extension's state snapshot, so they are
  // tracked here. Tests need this to tell "the extension thinks it stopped" apart from "the
  // browser session actually terminated" — the two diverged in
  // https://github.com/microsoft/aspire/issues/19289.
  const browserDebugSessions: AspireExtensionE2EBrowserDebugSession[] = [];
  const clipboardSnapshot: E2eClipboardSnapshot = { hasSnapshot: false };
  const clipboardExpectation: E2eClipboardExpectation = {};
  let commandInvocationSequence = 0;
  let terminalCommandSequence = 0;
  let debugLaunchSequence = 0;
  let debugConsoleOutputSequence = 0;
  let stoppingPathSequence = 0;
  let taskProcessSequence = 0;
  let taskExecutionSequence = 0;
  let previousStoppingPaths: readonly string[] | undefined;
  const taskExecutionIds = new WeakMap<vscode.TaskExecution, number>();
  let controlStatus: AspireExtensionE2EControlStatus | undefined;
  let lastControlRevision = -1;
  const writeStateFile = () => {
    const state = createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireContext, true);
    recordStoppingPathEvents(state.stoppingPaths);

    writeJsonFileAtomic(stateFile, {
      extensionHostSessionId,
      updatedAt: new Date().toISOString(),
      runId,
      state,
      dashboardUrl: getSensitiveDashboardUrl(dataRepository),
      commandInvocations,
      terminalCommands,
      debugLaunches,
      debugConsoleOutputs,
      stoppingPathEvents,
      taskProcessEvents,
      browserDebugSessions,
      control: controlStatus,
    });
  };

  const recordStoppingPathEvents = (currentStoppingPaths: readonly string[]) => {
    if (previousStoppingPaths === undefined) {
      previousStoppingPaths = [...currentStoppingPaths];
      return;
    }

    for (const appHostPath of currentStoppingPaths) {
      if (!previousStoppingPaths.some(previousPath => isSamePath(previousPath, appHostPath))) {
        stoppingPathEvents.push({ sequence: ++stoppingPathSequence, appHostPath, state: 'entered' });
      }
    }

    for (const appHostPath of previousStoppingPaths) {
      if (!currentStoppingPaths.some(currentPath => isSamePath(currentPath, appHostPath))) {
        stoppingPathEvents.push({ sequence: ++stoppingPathSequence, appHostPath, state: 'left' });
      }
    }

    if (stoppingPathEvents.length > 100) {
      stoppingPathEvents.splice(0, stoppingPathEvents.length - 100);
    }

    previousStoppingPaths = [...currentStoppingPaths];
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
  const taskStartSubscription = vscode.tasks.onDidStartTaskProcess(event => {
    const executionId = ++taskExecutionSequence;
    taskExecutionIds.set(event.execution, executionId);
    taskProcessEvents.push({
      sequence: ++taskProcessSequence,
      executionId,
      state: 'started',
      taskName: event.execution.task.name,
      taskSource: event.execution.task.source,
      taskDefinitionType: event.execution.task.definition.type,
      processId: event.processId,
    });
    trimTaskProcessEvents(taskProcessEvents);
    writeStateFile();
  });
  const taskEndSubscription = vscode.tasks.onDidEndTaskProcess(event => {
    const executionId = taskExecutionIds.get(event.execution) ?? ++taskExecutionSequence;
    taskProcessEvents.push({
      sequence: ++taskProcessSequence,
      executionId,
      state: 'ended',
      taskName: event.execution.task.name,
      taskSource: event.execution.task.source,
      taskDefinitionType: event.execution.task.definition.type,
      exitCode: event.exitCode,
    });
    trimTaskProcessEvents(taskProcessEvents);
    writeStateFile();
  });

  let controlProcessing: Promise<void> | undefined;
  const browserDebugSessionStartSubscription = vscode.debug.onDidStartDebugSession(session => {
    if (!isBrowserDebugSessionType(session.type)) {
      return;
    }

    browserDebugSessions.push({
      id: session.id,
      type: session.type,
      name: session.name,
      parentSessionId: session.parentSession?.id,
      parentSessionType: session.parentSession?.type,
    });
    writeStateFile();
  });
  const browserDebugSessionEndSubscription = vscode.debug.onDidTerminateDebugSession(session => {
    const index = browserDebugSessions.findIndex(tracked => tracked.id === session.id);
    if (index < 0) {
      return;
    }

    browserDebugSessions.splice(index, 1);
    writeStateFile();
  });
  const controlInterval = controlFile
    ? setInterval(() => {
      if (controlProcessing) {
        return;
      }

      controlProcessing = processE2eControlFile(controlFile, lastControlRevision, runId, async (payload) => {
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
          if (payload.resetDashboardDefaultChangedNotification) {
            await context.globalState.update(dashboardDefaultChangedNotificationKey, undefined);
          }
          if (payload.command) {
            let commandStarted = false;
            const markCommandStarted = () => {
              if (!commandStarted) {
                commandStarted = true;
                controlStatus = { revision, status: 'started', startedObserved: true };
                writeStateFile();
              }
            };

            const result = await executeE2eControlCommand(context, aspireContext, dataRepository, appHostLaunchService, appHostTreeProvider, terminalProvider, clipboardSnapshot, clipboardExpectation, preparableLanguageModelTools, payload.command, markCommandStarted);
            controlStatus = { revision, status: 'applied', startedObserved: commandStarted, result };
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

  return vscode.Disposable.from(stateSubscription, commandSubscription, terminalCommandSubscription, debugLaunchSubscription, debugConsoleOutputSubscription, taskStartSubscription, taskEndSubscription, browserDebugSessionStartSubscription, browserDebugSessionEndSubscription, controlSubscription);
}

function isBrowserDebugSessionType(type: string): boolean {
  return type === 'pwa-chrome' || type === 'pwa-msedge' || type === 'firefox';
}

function trimTaskProcessEvents(events: AspireExtensionE2ETaskProcessEvent[]): void {
  if (events.length > 100) {
    events.splice(0, events.length - 100);
  }
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
  runId: string | undefined,
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

  // Ignore commands addressed to a different run. Revisions restart at 0 in every test process, so
  // without this an extension host from an earlier run would answer — and race the intended host.
  if (runId !== undefined && payload.runId !== undefined && payload.runId !== runId) {
    return;
  }

  await applyControl(payload);
}

function getE2eErrorMessage(error: unknown): string {
  if (isCommandCancellation(error)) {
    return 'E2E control command cancelled.';
  }

  return error instanceof Error && error.message.startsWith('Aspire extension E2E ')
    ? error.message
    : 'E2E control command failed.';
}

export async function executeE2eControlCommand(
  context: vscode.ExtensionContext,
  aspireContext: AspireExtensionContext,
  dataRepository: AppHostDataRepository,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider,
  terminalProvider: AspireTerminalProvider,
  clipboardSnapshot: E2eClipboardSnapshot,
  clipboardExpectation: E2eClipboardExpectation,
  preparableLanguageModelTools: ReadonlyMap<string, PreparableLanguageModelTool>,
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
    case 'deployAppHostAction': {
      const element = getRequiredAppHostActionElement(appHostTreeProvider, command.name, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.deployAppHost', element);
      markStarted();
      return await commandPromise;
    }
    case 'publishAppHostAction': {
      const element = getRequiredAppHostActionElement(appHostTreeProvider, command.name, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.publishAppHost', element);
      markStarted();
      return await commandPromise;
    }
    case 'runPipelineStepAppHostAction': {
      const element = getRequiredAppHostActionElement(appHostTreeProvider, command.name, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.runPipelineStepAppHost', element);
      markStarted();
      return await commandPromise;
    }
    case 'debugPipelineStepAppHostAction': {
      const element = getRequiredAppHostActionElement(appHostTreeProvider, command.name, command.appHostPath);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.debugPipelineStepAppHost', element);
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
      const expectedClipboardText = getAppHostPathForClipboard(element);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyAppHostPath', element);
      markStarted();
      await commandPromise;
      setClipboardExpectation(clipboardExpectation, expectedClipboardText, 'path');
      return undefined;
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
      const expectedClipboardText = getLogFilePathForClipboard(element);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyLogFilePath', element);
      markStarted();
      await commandPromise;
      setClipboardExpectation(clipboardExpectation, expectedClipboardText, 'path');
      return undefined;
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
      const expectedClipboardText = getResourceNameForClipboard(element);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyResourceName', element);
      markStarted();
      await commandPromise;
      setClipboardExpectation(clipboardExpectation, expectedClipboardText);
      return undefined;
    }
    case 'copyEndpointUrl': {
      const endpoint = getEndpointElement(appHostTreeProvider, command);
      const commandPromise = vscode.commands.executeCommand('aspire-vscode.copyEndpointUrl', endpoint.element);
      markStarted();
      await commandPromise;
      setClipboardExpectation(clipboardExpectation, endpoint.url);
      return undefined;
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
    case 'executeCodeLensResourceAction': {
      const element = getResourceCommandElement(appHostTreeProvider, command);
      const commandPromise = vscode.commands.executeCommand(
        'aspire-vscode.codeLensResourceAction',
        element.resourceItem.resource.name,
        element.commandName,
        command.appHostPath ?? element.resourceItem.appHostPath ?? '',
        element.commandJson);
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
    case 'startDebugging': {
      const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
      if (!workspaceFolder) {
        throw new Error('Aspire extension E2E startDebugging requires an open workspace folder.');
      }

      // Passing a name only searches launch.json. Resolve a matching dynamic configuration first so
      // the control command can await the same provider pipeline that VS Code's debug picker starts.
      const dynamicConfigurations = await aspireContext.debugConfigProvider?.provideDebugConfigurations(workspaceFolder);
      const configuration = dynamicConfigurations?.find(configuration =>
        configuration.name === command.configurationName ||
        configuration.name.startsWith(`${command.configurationName} (`))
        ?? command.configurationName;
      const commandPromise = vscode.debug.startDebugging(workspaceFolder, configuration);
      markStarted();
      return await commandPromise;
    }
    case 'stopDebugging': {
      markStarted();
      await stopDebuggingForE2E(aspireContext, dataRepository, appHostLaunchService, appHostTreeProvider);
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
    case 'getRegisteredLanguageModelTools': {
      markStarted();
      return vscode.lm.tools
        .filter(tool => tool.name.startsWith('aspire_'))
        .map(tool => ({ name: tool.name, tags: [...tool.tags], description: tool.description }))
        .sort((left, right) => left.name.localeCompare(right.name));
    }
    case 'prepareLanguageModelToolInvocation': {
      markStarted();
      const tool = preparableLanguageModelTools.get(command.toolName);
      if (!tool) {
        throw new Error(`Language model tool '${command.toolName}' is not registered.`);
      }

      const prepared = await tool.prepareInvocation({ input: command.input }, new vscode.CancellationTokenSource().token);
      return {
        invocationMessage: prepared.invocationMessage,
        confirmationTitle: prepared.confirmationMessages?.title,
        confirmationMessage: prepared.confirmationMessages?.message,
      };
    }
    case 'invokeLanguageModelTool': {
      markStarted();
      const invocationCount = Math.max(1, command.times ?? 1);
      const cancellationDelayMs = getE2eCancellationDelay(command.cancelAfterMs);
      const cancellationSource = cancellationDelayMs === undefined
        ? undefined
        : new vscode.CancellationTokenSource();
      const cancellationTimer = cancellationSource
        ? setTimeout(() => cancellationSource.cancel(), cancellationDelayMs)
        : undefined;
      try {
        const invocationResults = await Promise.all(Array.from({ length: invocationCount }, () => vscode.lm.invokeTool(command.toolName, {
          input: command.input,
          toolInvocationToken: undefined,
        }, cancellationSource?.token)));

        return {
          results: invocationResults.map(invocationResult => invocationResult.content
            .filter((part): part is vscode.LanguageModelTextPart => part instanceof vscode.LanguageModelTextPart)
            .map(part => part.value)
            .join('')),
        };
      }
      catch (error) {
        if (isCommandCancellation(error)) {
          return { results: [], cancelled: true };
        }

        throw error;
      }
      finally {
        if (cancellationTimer !== undefined) {
          clearTimeout(cancellationTimer);
        }
        cancellationSource?.dispose();
      }
    }
    case 'getDebugSessionProcessInfo': {
      markStarted();
      const state = createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireContext, true);
      const appHostPath = command.appHostPath;
      const debugSession = aspireContext.aspireDebugSessions.find(session =>
        appHostPath === undefined ||
        (typeof session.appHostPath === 'string' && isSamePath(session.appHostPath, appHostPath)));
      const appHost = state.appHosts.find(candidate =>
        appHostPath === undefined || isSamePath(candidate.appHostPath, appHostPath)) ??
        (state.workspaceAppHost && (appHostPath === undefined || isSamePath(state.workspaceAppHost.appHostPath, appHostPath))
          ? state.workspaceAppHost
          : undefined);

      return {
        appHostPath: debugSession?.appHostPath ?? appHost?.appHostPath,
        cliPid: debugSession?.cliProcessId,
        appHostPid: appHost?.appHostPid,
      };
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
    case 'getSupportedCapabilities': {
      markStarted();
      // The capability list is what the CLI asks for before it hands an AppHost to the extension to
      // launch, so a spec that needs the extension to debug an AppHost has to be able to see it.
      return getSupportedCapabilities();
    }
    case 'getVisibleExtensionIds': {
      markStarted();
      // Capabilities are derived from vscode.extensions.getExtension, so the only list that explains a
      // missing capability is the one the extension host itself can see. The runner already checks the
      // extensions directory and extensions.json, but both can be correct while the host still loads
      // nothing - a copied extension directory is only scanned while extensions.json is absent.
      return vscode.extensions.all.map(extension => extension.id);
    }
    case 'waitForJavaLanguageServer': {
      markStarted();
      return await waitForJavaLanguageServer(command.timeoutMs ?? 900000);
    }
    case 'createResourceDebugConfiguration': {
      markStarted();
      const launchConfig = getE2eLaunchConfiguration(command.launchConfig);
      const isApphost = command.isApphost ?? false;
      const debuggerExtension = isApphost && launchConfig.type === 'project'
        ? projectDebuggerExtension
        : getResourceDebuggerExtensions().find(extension => extension.resourceType === launchConfig.type);
      if (!debuggerExtension) {
        throw new Error(`No resource debugger extension is registered for launch configuration type '${launchConfig.type}'.`);
      }

      const runId = 'e2e-resource-debug-configuration';
      try {
        const debugSessionConfiguration = {
          type: 'aspire',
          request: 'launch',
          name: 'E2E resource debug configuration',
          program: '',
          debuggers: command.debuggers ? { ...command.debuggers } : undefined,
        };
        const debugConfiguration = await createDebugSessionConfiguration(
          debugSessionConfiguration,
          launchConfig,
          getE2eStringArray(command.args, 'args'),
          getE2eEnvVars(command.env),
          {
            debug: command.debug ?? true,
            forceBuild: false,
            runId,
            debugSessionId: 'e2e-debug-session',
            isApphost,
            debugSession: { configuration: debugSessionConfiguration } as AspireDebugSession
          },
          debuggerExtension);

        const loggableConfiguration = getLoggableDebugConfiguration(debugConfiguration, false);
        const environmentKeys = getE2eStringArray(command.environmentKeys, 'environmentKeys');
        return environmentKeys
          ? {
            ...loggableConfiguration,
            environment: Object.fromEntries(environmentKeys.map(key => [key, debugConfiguration.env?.[key]])),
          }
          : loggableConfiguration;
      } finally {
        cleanupRun(runId);
      }
    }
    case 'proveAttachedResourceDebugging': {
      markStarted();
      return await proveAttachedResourceDebugging(command, appHostTreeProvider, preparableLanguageModelTools);
    }
    case 'proveAppHostAndResourceDebugging': {
      markStarted();
      return await proveAppHostAndResourceDebugging(command, aspireContext, appHostTreeProvider);
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
    case 'getDefinitions': {
      const filePath = getE2eRunPath(command.filePath);
      markStarted();
      const definitions = await vscode.commands.executeCommand<Array<vscode.Location | vscode.LocationLink>>(
        'vscode.executeDefinitionProvider',
        vscode.Uri.file(filePath),
        new vscode.Position(command.line, command.character));

      return (definitions ?? []).map(definition => ({
        filePath: 'targetUri' in definition ? definition.targetUri.fsPath : definition.uri.fsPath,
        line: 'targetUri' in definition
          ? (definition.targetSelectionRange ?? definition.targetRange).start.line
          : definition.range.start.line,
      }));
    }
    case 'getJavaProjects': {
      markStarted();
      const projects = await vscode.commands.executeCommand<Array<vscode.Uri | string>>('java.project.getAll');
      return (projects ?? []).map(project => typeof project === 'string' ? project : project.toString());
    }
    case 'getCodeLenses': {
      const filePath = getE2eRunPath(command.filePath, command.name);
      markStarted();
      return await getCodeLensesForFile(filePath);
    }
    case 'snapshotClipboard': {
      markStarted();
      // The state and control files are uploaded as E2E diagnostics, so arbitrary user
      // clipboard text must stay in extension-host memory instead of crossing the JSON bridge.
      clipboardSnapshot.text = await vscode.env.clipboard.readText();
      clipboardSnapshot.hasSnapshot = true;
      return undefined;
    }
    case 'restoreClipboardSnapshot': {
      markStarted();
      if (clipboardSnapshot.hasSnapshot) {
        await vscode.env.clipboard.writeText(clipboardSnapshot.text ?? '');
        clipboardSnapshot.text = undefined;
        clipboardSnapshot.hasSnapshot = false;
      }

      return undefined;
    }
    case 'captureWorkspaceAppHostPathClipboardExpectation': {
      markStarted();
      const state = createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireContext, true);
      if (!state.workspaceAppHostPath) {
        throw new Error('E2E clipboard assertion could not determine the workspace AppHost path.');
      }

      setClipboardExpectation(clipboardExpectation, state.workspaceAppHostPath, 'path');
      return undefined;
    }
    case 'assertClipboardMatchesLastExpectation': {
      markStarted();
      await assertExpectedClipboardText(clipboardExpectation);
      return undefined;
    }
    case 'openFile': {
      const filePath = getE2eRunPath(command.filePath);
      markStarted();
      const document = await vscode.workspace.openTextDocument(vscode.Uri.file(filePath));
      await vscode.window.showTextDocument(document, { preview: false });
      return getActiveEditorInfo();
    }
    case 'openWorkspaceFolder': {
      const folderPath = getE2eWorkspaceFolderPath(command.folderPath);
      markStarted();
      clearPendingE2eControlFile();
      await vscode.commands.executeCommand('vscode.openFolder', vscode.Uri.file(folderPath), false);
      return undefined;
    }
    case 'setWorkspaceFolders': {
      const folders = getE2eWorkspaceFolderEntries(command.folders);
      markStarted();
      return await setE2eWorkspaceFolders(folders);
    }
    case 'setWorkspaceFolderCliPath': {
      const folderPath = getE2eWorkspacePath(command.folderPath);
      const cliPath = getE2eRunPath(command.cliPath);
      const workspaceFolder = vscode.workspace.workspaceFolders?.find(folder => isSamePath(folder.uri.fsPath, folderPath));
      if (!workspaceFolder) {
        throw new Error(`Aspire extension E2E setWorkspaceFolderCliPath requires an exact open workspace folder: ${folderPath}`);
      }

      markStarted();
      const targetKey = getCliPathTargetKey(workspaceFolderCliPathTarget(workspaceFolder));
      const cliPaths = getE2eWorkspaceFolderCliPaths();
      cliPaths[targetKey] = cliPath;
      process.env.ASPIRE_EXTENSION_E2E_CLI_PATHS = JSON.stringify(cliPaths);
      return { targetKey, cliPath };
    }
    case 'clearWorkspaceFolderCliPaths': {
      markStarted();
      delete process.env.ASPIRE_EXTENSION_E2E_CLI_PATHS;
      return undefined;
    }
    case 'stopOwnedDebugSessionProcesses': {
      markStarted();
      const appHostPath = command.appHostPath;
      const debugSessions = aspireContext.aspireDebugSessions.filter(session =>
        appHostPath === undefined ||
        (typeof session.appHostPath === 'string' && isSamePath(session.appHostPath, appHostPath)));
      await Promise.race([
        Promise.allSettled(debugSessions.map(session => session.requestCliStopForExtensionShutdown())),
        delay(5000),
      ]);
      await Promise.allSettled(debugSessions.map(session => session.terminateCliProcessTree({ force: true })));

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
    case 'addWorkspaceFolder': {
      markStarted();
      return await addWorkspaceFolderForE2E(getE2eAddableWorkspaceFolderPath(command.folderPath));
    }
    case 'getActiveEditor': {
      markStarted();
      return getActiveEditorInfo();
    }
    case 'getOpenEditors': {
      markStarted();
      return getOpenEditorInfo();
    }
    case 'runAspireCli': {
      if (!Array.isArray(command.args) || !command.args.every(argument => typeof argument === 'string')) {
        throw new Error('Aspire extension E2E runAspireCli args must be an array of strings.');
      }

      const workingDirectory = getE2eRunAspireCliWorkingDirectory(command.workingDirectory);
      const timeoutMs = getE2ePositiveInteger(command.timeoutMs, 300000, 'timeoutMs');
      const commandPromise = runAspireCliForE2E(
        terminalProvider,
        [...command.args],
        workingDirectory,
        timeoutMs,
        terminalProvider.createEnvironment(undefined, undefined, command.noExtensionVariables),
        {
          noExtensionVariables: command.noExtensionVariables === true,
          rejectOnNonZero: command.allowNonZeroExit !== true,
        });
      markStarted();
      return await commandPromise;
    }
    default:
      throw new Error(`Unsupported Aspire extension E2E control command: ${getUnknownCommandName(command)}`);
  }
}

interface E2eClipboardSnapshot {
  text?: string;
  hasSnapshot: boolean;
}

interface E2eClipboardExpectation {
  text?: string;
  comparison?: 'exact' | 'path';
}

function setClipboardExpectation(expectation: E2eClipboardExpectation, text: string, comparison: 'exact' | 'path' = 'exact'): void {
  expectation.text = text;
  expectation.comparison = comparison;
}

async function assertExpectedClipboardText(expectation: E2eClipboardExpectation): Promise<void> {
  if (expectation.text === undefined) {
    throw new Error('E2E clipboard assertion did not have an expected value captured in memory.');
  }

  const expectedText = expectation.text;
  const comparison = expectation.comparison ?? 'exact';

  // Keep the expected value in memory until the assertion succeeds so transient clipboard
  // mismatches can be retried. The E2E state file serializes thrown errors, so mismatch
  // diagnostics must avoid echoing arbitrary clipboard contents.
  const clipboardText = await vscode.env.clipboard.readText();
  const matches = comparison === 'path'
    ? isSamePath(clipboardText, expectedText)
    : clipboardText === expectedText;
  if (!matches) {
    throw new Error(formatClipboardMismatchError(comparison, expectedText.length, clipboardText.length));
  }

  // Only clear once the assertion has succeeded so a failing assertion can be retried.
  expectation.text = undefined;
  expectation.comparison = undefined;
}

function formatClipboardMismatchError(comparison: 'exact' | 'path', expectedLength: number, actualLength: number): string {
  return comparison === 'path'
    ? `E2E clipboard path did not match the expected path. Expected length: ${expectedLength}; actual length: ${actualLength}.`
    : `E2E clipboard text did not match the expected text. Expected length: ${expectedLength}; actual length: ${actualLength}.`;
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

type AppHostAndResourceDebugProofCommand = Extract<AspireExtensionE2EControlCommand, { name: 'proveAppHostAndResourceDebugging' }>;
type AttachedResourceDebugProofCommand = Extract<AspireExtensionE2EControlCommand, { name: 'proveAttachedResourceDebugging' }>;
type MauiResourceDebugProofCommand = Extract<AspireExtensionE2EControlCommand, { name: 'proveMauiResourceDebugging' }>;

interface E2eInvocableLanguageModelTool extends PreparableLanguageModelTool {
  invoke(
    options: { readonly input: Record<string, unknown>; readonly toolInvocationToken: undefined },
    token: vscode.CancellationToken,
  ): Promise<vscode.LanguageModelToolResult>;
}

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

async function proveAttachedResourceDebugging(
  command: AttachedResourceDebugProofCommand,
  appHostTreeProvider: AspireAppHostTreeProvider,
  languageModelTools: ReadonlyMap<string, PreparableLanguageModelTool>,
): Promise<unknown> {
  const appHostPath = getE2eWorkspacePath(command.appHostPath);
  const sourcePath = getE2eWorkspacePath(command.sourcePath);
  const resourceName = getE2eRequiredString(command.resourceName, 'Aspire extension E2E attach proof requires resourceName.');
  const expectedResponse = getE2eRequiredString(command.expectedResponse, 'Aspire extension E2E attach proof requires expectedResponse.');
  const breakpointLine = getE2eBreakpointLine(command.breakpointLine);
  const resourceRequestPath = command.resourceRequestPath ?? '/';
  const timeoutMs = getE2ePositiveInteger(command.timeoutMs, 300000, 'timeoutMs');
  const expectedDebugType = command.expectedDebugType;
  if (expectedDebugType !== 'coreclr' && expectedDebugType !== 'go') {
    throw new Error(`Aspire extension E2E attach proof expected coreclr or go, got '${String(expectedDebugType)}'.`);
  }

  const debugSessions: DebugSessionSnapshot[] = [];
  const sessionById = new Map<string, vscode.DebugSession>();
  const terminatedSessionIds = new Set<string>();
  const attachRequests: DebugAdapterMessageSummary[] = [];
  const debugAdapterResponses: DebugAdapterMessageSummary[] = [];
  const breakpointResponses: DebugAdapterMessageSummary[] = [];
  const stoppedEvents: DebugAdapterStoppedEvent[] = [];

  const sessionSubscription = vscode.debug.onDidStartDebugSession(session => {
    sessionById.set(session.id, session);
    debugSessions.push(toDebugSessionSnapshot(session));
  });
  const terminationSubscription = vscode.debug.onDidTerminateDebugSession(session => {
    terminatedSessionIds.add(session.id);
  });
  const trackerRegistration = vscode.debug.registerDebugAdapterTrackerFactory('*', {
    createDebugAdapterTracker(session) {
      return {
        onWillReceiveMessage(message) {
          if (message?.type === 'request' && message.command === 'attach') {
            attachRequests.push({
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
              success: false,
              body: redactDebugAdapterArguments(message),
            });
          }
          if (message?.type === 'response' && message.command === 'setBreakpoints') {
            const breakpointResponse = {
              sessionId: session.id,
              sessionType: session.type,
              sessionName: session.name,
              command: message.command,
              success: message.success,
              body: redactDebugAdapterArguments(message.body),
            };
            breakpointResponses.push(breakpointResponse);
            extensionLogOutputChannel.info(`Resource debug E2E ${session.type} setBreakpoints response: ${JSON.stringify(breakpointResponse)}`);
          }
          if (message?.type === 'event' && message.event === 'stopped') {
            const stoppedEvent = {
              sessionId: session.id,
              sessionType: session.type,
              sessionName: session.name,
              reason: message.body?.reason,
              threadId: message.body?.threadId,
            };
            stoppedEvents.push(stoppedEvent);
            extensionLogOutputChannel.info(`Resource debug E2E ${session.type} stopped event: ${JSON.stringify(stoppedEvent)}`);
          }
        },
      };
    },
  });

  const breakpoint = new vscode.SourceBreakpoint(
    new vscode.Location(vscode.Uri.file(sourcePath), new vscode.Position(breakpointLine, 0)),
    true);
  vscode.debug.addBreakpoints([breakpoint]);
  let attachedSession: vscode.DebugSession | undefined;
  let toolPayload: Record<string, unknown> | undefined;

  try {
    const resourceDebugTool = languageModelTools.get('aspire_resource_debug') as E2eInvocableLanguageModelTool | undefined;
    if (!resourceDebugTool?.invoke) {
      throw new Error('Aspire extension E2E attach proof could not find the registered aspire_resource_debug tool.');
    }

    const workspaceRoot = getE2eWorkspacePath(process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT);
    const relativeAppHostPath = path.relative(workspaceRoot, appHostPath).split(path.sep).join('/');
    const toolCancellation = new vscode.CancellationTokenSource();
    let languageModelResult: vscode.LanguageModelToolResult;
    try {
      languageModelResult = await resourceDebugTool.invoke({
        input: {
          appHostPath: relativeAppHostPath,
          resourceName,
          strategy: 'attach',
        },
        toolInvocationToken: undefined,
      }, toolCancellation.token);
    }
    finally {
      toolCancellation.dispose();
    }
    const resultPart = languageModelResult.content[0];
    if (!(resultPart instanceof vscode.LanguageModelTextPart)) {
      throw new Error('aspire_resource_debug returned a non-text result.');
    }

    toolPayload = JSON.parse(resultPart.value) as Record<string, unknown>;
    const expectedProvider = expectedDebugType === 'coreclr' ? 'dotnet' : 'go';
    if (toolPayload.outcome !== 'started' || toolPayload.provider !== expectedProvider) {
      throw new Error(`aspire_resource_debug returned ${JSON.stringify(toolPayload)} instead of starting ${expectedProvider}.`);
    }

    attachedSession = await waitForE2eValue(
      `${expectedDebugType} attach session for resource '${resourceName}'`,
      timeoutMs,
      () => [...sessionById.values()].find(session =>
        session.type === expectedDebugType &&
        session.configuration.request === 'attach'));

    const breakpointHit = await withResourceTraffic(
      appHostTreeProvider,
      appHostPath,
      resourceName,
      resourceRequestPath,
      timeoutMs,
      async () => await waitForE2eValue(
        `breakpoint in ${sourcePath}:${breakpointLine + 1}`,
        timeoutMs,
        async () => {
          for (const stoppedEvent of stoppedEvents) {
            if (stoppedEvent.sessionId !== attachedSession?.id || stoppedEvent.threadId === undefined) {
              continue;
            }

            let stackTrace: { stackFrames?: Array<{ source?: { path?: string }; line?: number }> } | undefined;
            try {
              stackTrace = await attachedSession.customRequest('stackTrace', {
                threadId: stoppedEvent.threadId,
                startFrame: 0,
                levels: 20,
              });
            }
            catch {
              continue;
            }

            const matchingFrame = stackTrace?.stackFrames?.find(frame =>
              typeof frame.source?.path === 'string' &&
              isSamePath(frame.source.path, sourcePath) &&
              frame.line === breakpointLine + 1);
            if (matchingFrame) {
              return { stoppedEvent, matchingFrame };
            }
          }

          return undefined;
        }));

    // Remove the breakpoint before continuing so requests queued by the traffic driver cannot
    // immediately stop the process again and race debugger detach.
    vscode.debug.removeBreakpoints([breakpoint]);
    await attachedSession.customRequest('continue', { threadId: breakpointHit.stoppedEvent.threadId });
    await vscode.debug.stopDebugging(attachedSession);
    await waitForE2eValue(
      `${expectedDebugType} attach session termination`,
      timeoutMs,
      () => terminatedSessionIds.has(attachedSession!.id) ? true : undefined);

    const responseBody = await waitForE2eValue(
      `resource '${resourceName}' response after debugger detach`,
      timeoutMs,
      async () => {
        const resourceAfterDetach = appHostTreeProvider.findResourceElement(resourceName, appHostPath);
        if (!(resourceAfterDetach instanceof ResourceItem) || resourceAfterDetach.resource.state !== 'Running') {
          return undefined;
        }

        const requestUrl = await resolveResourceRequestUrl(
          appHostTreeProvider,
          appHostPath,
          resourceName,
          resourceRequestPath,
          timeoutMs);
        try {
          const response = await fetch(requestUrl, { signal: AbortSignal.timeout(5000) });
          const body = await response.text();
          return response.ok && body === expectedResponse ? body : undefined;
        }
        catch {
          return undefined;
        }
      });

    if (attachRequests.length === 0 || breakpointResponses.every(response => response.success !== true)) {
      throw new Error(`The ${expectedDebugType} adapter did not report both attach and bound-breakpoint protocol traffic.`);
    }

    return {
      proof: 'aspire-resource-attach-breakpoint-detach',
      toolPayload,
      resourceName,
      debugType: expectedDebugType,
      debugSessionId: attachedSession.id,
      breakpoint: {
        sourcePath,
        line: breakpointLine + 1,
        text: fs.readFileSync(sourcePath, 'utf8').split(/\r?\n/)[breakpointLine]?.trim(),
        stoppedEvent: breakpointHit.stoppedEvent,
        matchingStackFrame: breakpointHit.matchingFrame,
      },
      attachRequests,
      breakpointResponses,
      debugAdapterResponses,
      resourceResponseAfterDetach: responseBody,
      sessionTerminated: true,
    };
  }
  catch (error) {
    const diagnostics = {
      debugSessions,
      attachRequests,
      breakpointResponses,
      debugAdapterResponses,
      stoppedEvents,
      terminatedSessionIds: [...terminatedSessionIds],
      toolPayload,
    };
    extensionLogOutputChannel.error(`Resource debug E2E attach proof failed: ${error instanceof Error ? error.message : String(error)}
Diagnostics:
${JSON.stringify(diagnostics, undefined, 2)}`);
    throw new Error(`${error instanceof Error ? error.message : String(error)}
Diagnostics:
${JSON.stringify(diagnostics, undefined, 2)}`);
  }
  finally {
    vscode.debug.removeBreakpoints([breakpoint]);
    sessionSubscription.dispose();
    terminationSubscription.dispose();
    trackerRegistration.dispose();
    if (attachedSession && !terminatedSessionIds.has(attachedSession.id)) {
      await vscode.debug.stopDebugging(attachedSession);
    }
  }
}

async function proveAppHostAndResourceDebugging(command: AppHostAndResourceDebugProofCommand, aspireContext: AspireExtensionContext, appHostTreeProvider: AspireAppHostTreeProvider): Promise<unknown> {
  const appHostPath = getE2eWorkspacePath(command.appHostPath);
  const appHostSourcePath = getE2eWorkspacePath(command.appHostSourcePath);
  const resourceSourcePath = getE2eWorkspacePath(command.resourceSourcePath);
  const resourceName = getE2eRequiredString(command.resourceName, 'Aspire extension E2E debug proof requires resourceName.');
  const appHostBreakpointLine = getE2eBreakpointLine(command.appHostBreakpointLine);
  const resourceBreakpointLine = getE2eBreakpointLine(command.resourceBreakpointLine);
  const resourceRequestPath = command.resourceRequestPath ?? '/';
  const timeoutMs = getE2ePositiveInteger(command.timeoutMs, 300000, 'timeoutMs');

  const debugSessions: DebugSessionSnapshot[] = [];
  const sessionById = new Map<string, vscode.DebugSession>();
  const launchRequests: DebugAdapterLaunchRequest[] = [];
  const debugAdapterResponses: DebugAdapterMessageSummary[] = [];
  const stoppedEvents: DebugAdapterStoppedEvent[] = [];
  const breakpointRequests: DebugAdapterMessageSummary[] = [];
  const breakpointResponses: DebugAdapterMessageSummary[] = [];

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
        }
      };
    }
  });

  const appHostBreakpoint = new vscode.SourceBreakpoint(
    new vscode.Location(vscode.Uri.file(appHostSourcePath), new vscode.Position(appHostBreakpointLine, 0)),
    true);
  const resourceBreakpoint = new vscode.SourceBreakpoint(
    new vscode.Location(vscode.Uri.file(resourceSourcePath), new vscode.Position(resourceBreakpointLine, 0)),
    true);
  vscode.debug.addBreakpoints([appHostBreakpoint, resourceBreakpoint]);

  const waitForBreakpoint = async (sourcePath: string, breakpointLine: number) => await waitForE2eValue(
    `breakpoint in ${sourcePath}:${breakpointLine + 1}`,
    timeoutMs,
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

        const matchingFrame = stackTrace?.stackFrames?.find(frame =>
          typeof frame.source?.path === 'string' && isSamePath(frame.source.path, sourcePath));
        if (matchingFrame) {
          return { session, stoppedEvent, stackTrace, matchingFrame };
        }
      }

      return undefined;
    });

  try {
    const appHostElement = getAppHostElement(appHostTreeProvider, appHostPath);
    await vscode.commands.executeCommand('aspire-vscode.debugAppHost', appHostElement);

    const appHostHit = await waitForBreakpoint(appHostSourcePath, appHostBreakpointLine);
    if (appHostHit.matchingFrame.line !== appHostBreakpointLine + 1) {
      throw new Error(`Expected AppHost breakpoint line ${appHostBreakpointLine + 1}, got ${appHostHit.matchingFrame.line}.`);
    }
    await appHostHit.session.customRequest('continue', { threadId: appHostHit.stoppedEvent.threadId });

    // A breakpoint inside a request handler only runs when a request arrives, and nothing else in the
    // run issues one: the health check probes /actuator/health rather than the controller. Without
    // driving the traffic here the wait below can only ever time out, which is what it did - the
    // resource launched under the debugger and sat idle for the full 15 minutes.
    //
    // The endpoint wait gets the caller's whole budget rather than a shorter cap of its own. The
    // resource is a Spring Boot app that the run still has to compile and start under a debugger, on
    // a runner that is already hosting the Java language server, so capping this at five minutes
    // reported a timeout while the resource was legitimately still coming up - and reported it as
    // "300000ms" even though the spec had asked for fifteen minutes.
    const resourceHit = await withResourceTraffic(
      appHostTreeProvider,
      appHostPath,
      resourceName,
      resourceRequestPath,
      timeoutMs,
      () => waitForBreakpoint(resourceSourcePath, resourceBreakpointLine));
    if (resourceHit.matchingFrame.line !== resourceBreakpointLine + 1) {
      throw new Error(`Expected resource breakpoint line ${resourceBreakpointLine + 1}, got ${resourceHit.matchingFrame.line}.`);
    }
    await resourceHit.session.customRequest('continue', { threadId: resourceHit.stoppedEvent.threadId });

    const aspireDebugSession = await waitForE2eValue(
      'Aspire AppHost debug startup completion',
      timeoutMs,
      () => aspireContext.aspireDebugSessions.find(session =>
        session.startupCompleted &&
        typeof session.appHostPath === 'string' &&
        isSamePath(session.appHostPath, appHostPath)));

    return {
      proof: 'aspire-apphost-and-resource-debug-breakpoints-hit',
      appHostPath,
      resourceName,
      aspireDebugSessionId: aspireDebugSession.debugSessionId,
      appHostBreakpoint: {
        sourcePath: appHostSourcePath,
        line: appHostBreakpointLine + 1,
        text: fs.readFileSync(appHostSourcePath, 'utf8').split(/\r?\n/)[appHostBreakpointLine]?.trim(),
        stoppedEvent: appHostHit.stoppedEvent,
        matchingStackFrame: appHostHit.matchingFrame,
        topStackFrame: appHostHit.stackTrace?.stackFrames?.[0],
      },
      resourceBreakpoint: {
        sourcePath: resourceSourcePath,
        line: resourceBreakpointLine + 1,
        text: fs.readFileSync(resourceSourcePath, 'utf8').split(/\r?\n/)[resourceBreakpointLine]?.trim(),
        stoppedEvent: resourceHit.stoppedEvent,
        matchingStackFrame: resourceHit.matchingFrame,
        topStackFrame: resourceHit.stackTrace?.stackFrames?.[0],
      },
      debugSessions,
      launchRequests,
      debugAdapterResponses,
      breakpointRequests,
      breakpointResponses,
      stoppedEvents,
    };
  }
  catch (error) {
    throw new Error(`${error instanceof Error ? error.message : String(error)}
Diagnostics:
${JSON.stringify({
      // The CLI only delegates the AppHost launch to the extension when it advertises the language's
      // capability, so a missing entry here is the difference between "the debugger failed" and "the
      // debugger was never asked", which the session list alone cannot distinguish.
      supportedCapabilities: getSupportedCapabilities(),
      debugSessions,
      launchRequests,
      debugAdapterResponses,
      breakpointRequests,
      breakpointResponses,
      stoppedEvents,
    }, undefined, 2)}`);
  }
  finally {
    vscode.debug.removeBreakpoints([appHostBreakpoint, resourceBreakpoint]);
    sessionSubscription.dispose();
    trackerRegistration.dispose();
    await vscode.debug.stopDebugging();
  }
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
      terminalProvider.createDcpRunSessionEnvironment(aspireDebugSession.debugSessionId, false),
      { noExtensionVariables: true, rejectOnNonZero: true });

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
  if ('environment' in copy) {
    copy.environment = '<redacted>';
  }
  if ('environmentVariables' in copy) {
    copy.environmentVariables = '<redacted>';
  }

  return copy;
}

async function runAspireCliForE2E(
  terminalProvider: AspireTerminalProvider,
  args: string[],
  workingDirectory: string,
  timeoutMs: number,
  environment: Record<string, string | undefined>,
  options: { noExtensionVariables: boolean; rejectOnNonZero: boolean }
): Promise<{ exitCode: number | null; stdout: string; stderr: string }> {
  const cliPath = await terminalProvider.getAspireCliExecutablePath();
  const diagnosticCommand = [cliPath, ...redactCliArgsForLogging(args)].join(' ');
  return await new Promise((resolve, reject) => {
    const stdout: string[] = [];
    const stderr: string[] = [];
    let completed = false;
    const timeout = setTimeout(() => {
      if (completed) {
        return;
      }

      completed = true;
      // `getE2eErrorMessage` only forwards messages that carry the "Aspire extension E2E " marker;
      // everything else is collapsed to a generic string so failures cannot leak user-supplied
      // values. These two diagnostics are safe to forward because `diagnosticCommand` has already
      // been redacted by `redactCliArgsForLogging` and neither embeds captured stdout/stderr.
      void terminateCliProcess(child, 'Aspire extension E2E CLI command', { force: true, suppressTimeoutWarning: true })
        .then(
          () => reject(new Error(`Aspire extension E2E ${diagnosticCommand} timed out after ${timeoutMs}ms.`)),
          reject);
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
        if (code === 0 || !options.rejectOnNonZero) {
          resolve(result);
        } else {
          reject(new Error(`Aspire extension E2E ${diagnosticCommand} exited with code ${code}.`));
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
      noExtensionVariables: options.noExtensionVariables,
      createProcessGroup: true,
      env: Object.entries(environment)
        .map(([name, value]) => ({ name, value: String(value) }))
    });
  });
}

/**
 * Sends requests to a resource's HTTP endpoint for as long as <paramref name="waitForHit"/> runs.
 *
 * A breakpoint in a request handler is only reachable while a request is in flight, so a proof that
 * merely waits for one is waiting on a line that nothing will execute. Aspire's own health check is
 * not enough: it probes /actuator/health, which is Spring's endpoint rather than the application's.
 *
 * Requests are issued rather than awaited. The first one that reaches the handler parks on the
 * breakpoint and never gets a response, so awaiting it would deadlock against the wait it is meant
 * to satisfy; each attempt is abandoned after a short timeout and another is sent behind it.
 */
async function withResourceTraffic<T>(
  appHostTreeProvider: AspireAppHostTreeProvider,
  appHostPath: string,
  resourceName: string,
  requestPath: string,
  endpointTimeoutMs: number,
  waitForHit: () => Promise<T>
): Promise<T> {
  const requestUrl = await resolveResourceRequestUrl(
    appHostTreeProvider,
    appHostPath,
    resourceName,
    requestPath,
    endpointTimeoutMs);
  extensionLogOutputChannel.info(`Resource debug E2E traffic target resolved for resource '${resourceName}'.`);

  let driving = true;
  let firstAttempt = true;
  const driver = (async () => {
    while (driving) {
      try {
        const response = await fetch(requestUrl, { signal: AbortSignal.timeout(2000) });
        if (firstAttempt) {
          firstAttempt = false;
          extensionLogOutputChannel.info(`Resource debug E2E first traffic response for resource '${resourceName}': HTTP ${response.status}.`);
        }
      }
      catch (error) {
        if (firstAttempt) {
          firstAttempt = false;
          extensionLogOutputChannel.info(`Resource debug E2E first traffic attempt for resource '${resourceName}' failed: ${error instanceof Error ? error.name : typeof error}.`);
        }
        // Connection refused until the server is listening, and aborted once a request parks on the
        // breakpoint. Neither says anything about whether the breakpoint bound, so both are ignored
        // and the wait below is left to decide.
      }

      await delay(500);
    }
  })();

  try {
    return await waitForHit();
  }
  finally {
    driving = false;
    await driver;
  }
}

async function resolveResourceRequestUrl(
  appHostTreeProvider: AspireAppHostTreeProvider,
  appHostPath: string,
  resourceName: string,
  requestPath: string,
  endpointTimeoutMs: number,
): Promise<string> {
  const baseUrl = await waitForE2eValue(
    `an HTTP endpoint for resource '${resourceName}'`,
    endpointTimeoutMs,
    () => {
      const element = appHostTreeProvider.findEndpointElement({ appHostPath, resourceName });
      return element && hasEndpointUrl(element) ? element.url : undefined;
    },
    () => describeResourcesForE2E(appHostTreeProvider, appHostPath, resourceName));

  // A relative path resolves against the endpoint only when the base ends in '/'; without it the
  // last segment of the endpoint would be replaced instead.
  return new URL(requestPath.replace(/^\//, ''), baseUrl.endsWith('/') ? baseUrl : `${baseUrl}/`).toString();
}

async function waitForE2eValue<T>(description: string, timeoutMs: number, getValue: () => T | undefined | Promise<T | undefined>, describeState?: () => string): Promise<T> {  const started = Date.now();
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

  // A poll that returns undefined never sets lastError, so waits that are simply never satisfied
  // report "Last error: <none>" and say nothing about why. `describeState` lets those callers attach
  // what they were looking at, which is the difference between an actionable failure and a rerun.
  const state = describeState ? ` State: ${describeState()}` : '';
  throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}. Last error: ${lastError ?? '<none>'}.${state}`);
}

async function stopDebuggingForE2E(
  aspireContext: AspireExtensionContext,
  dataRepository: AppHostDataRepository,
  appHostLaunchService: AppHostLaunchService,
  appHostTreeProvider: AspireAppHostTreeProvider
): Promise<void> {
  const trackedSessions = aspireContext.aspireDebugSessions;
  if (trackedSessions.length > 0) {
    const stoppedDebugSessionIds = new Set(trackedSessions.map(debugSession => debugSession.debugSessionId));
    const stoppedAppHostPaths = trackedSessions
      .map(debugSession => debugSession.appHostPath)
      .filter(path => path !== undefined);
    await Promise.all(trackedSessions.map(debugSession => debugSession.stopDebugging()));
    for (const appHostPath of stoppedAppHostPaths) {
      dataRepository.requestAppHostStopRefresh(appHostPath);
    }

    await waitForE2eValue('Aspire debug sessions to stop', 120000, () => {
      const state = createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireContext, true);
      const stoppedSessionsAreGone = aspireContext.aspireDebugSessions.every(debugSession => !stoppedDebugSessionIds.has(debugSession.debugSessionId));
      const stoppedAppHostsAreGone = stoppedAppHostPaths.every(appHostPath => !hasRunningAppHost(state, appHostPath));
      return stoppedSessionsAreGone && stoppedAppHostsAreGone && state.launchingPaths.length === 0 && state.stoppingPaths.length === 0
        ? true
        : undefined;
    });

    return;
  }

  await vscode.debug.stopDebugging();

  await waitForE2eValue('VS Code debug sessions to stop', 120000, () => {
    const state = createStateSnapshot(dataRepository, appHostLaunchService, appHostTreeProvider, aspireContext, true);
    return state.debugSessions.length === 0
      && state.launchingPaths.length === 0
      && state.stoppingPaths.length === 0
      ? true
      : undefined;
  });
}

function hasRunningAppHost(state: AspireExtensionStateSnapshot, appHostPath: string): boolean {
  return (state.workspaceAppHost !== undefined && isSamePath(state.workspaceAppHost.appHostPath, appHostPath))
    || state.appHosts.some(appHost => isSamePath(appHost.appHostPath, appHostPath));
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

function getE2eCancellationDelay(value: unknown): number | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0 || value > 10000) {
    throw new Error('Aspire extension E2E language-model cancellation delay must be an integer between 0 and 10000.');
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

function getE2eWorkspaceFolderCliPaths(): Record<string, string> {
  const value = process.env.ASPIRE_EXTENSION_E2E_CLI_PATHS;
  if (!value) {
    return {};
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      return Object.fromEntries(
        Object.entries(parsed)
          .filter((entry): entry is [string, string] => typeof entry[1] === 'string'));
    }
  }
  catch {
    return {};
  }

  return {};
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

function getE2eWorkspaceFolderEntries(folders: unknown): Array<{ uri: vscode.Uri; name?: string }> {
  if (!Array.isArray(folders) || folders.length === 0) {
    throw new Error('Aspire extension E2E setWorkspaceFolders requires at least one workspace folder.');
  }

  const expectedWorkspaceRoot = process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT;
  if (typeof expectedWorkspaceRoot !== 'string' || expectedWorkspaceRoot.length === 0) {
    throw new Error('Aspire extension E2E setWorkspaceFolders requires ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT.');
  }

  return folders.map((folder, index) => {
    if (!folder || typeof folder !== 'object') {
      throw new Error(`Aspire extension E2E workspace folder ${index} must be an object.`);
    }

    const { folderPath, name } = folder as { folderPath?: unknown; name?: unknown };
    if (typeof folderPath !== 'string' || folderPath.length === 0 || !path.isAbsolute(folderPath)) {
      throw new Error(`Aspire extension E2E workspace folder ${index} requires an absolute folderPath.`);
    }
    if (!fs.existsSync(folderPath) || !fs.statSync(folderPath).isDirectory()) {
      throw new Error(`Aspire extension E2E workspace folder ${index} requires an existing directory: ${folderPath}`);
    }
    if (!isPathWithinDirectory(folderPath, expectedWorkspaceRoot)) {
      throw new Error(`Aspire extension E2E workspace folder ${index} must stay inside the configured E2E workspace root.`);
    }
    if (name !== undefined && (typeof name !== 'string' || name.length === 0)) {
      throw new Error(`Aspire extension E2E workspace folder ${index} name must be a non-empty string when provided.`);
    }

    return {
      uri: vscode.Uri.file(folderPath),
      ...(typeof name === 'string' ? { name } : {}),
    };
  });
}

async function setE2eWorkspaceFolders(folders: Array<{ uri: vscode.Uri; name?: string }>): Promise<Array<{ name: string; uri: string; fileName: string }>> {
  const currentFolders = vscode.workspace.workspaceFolders ?? [];
  const matchesCurrentFolders = currentFolders.length === folders.length && currentFolders.every((folder, index) =>
    folder.uri.toString() === folders[index].uri.toString()
    && folder.name === (folders[index].name ?? path.basename(folders[index].uri.fsPath)));
  if (matchesCurrentFolders) {
    return getWorkspaceFolderInfo();
  }

  let workspaceFoldersChanged: (() => void) | undefined;
  const changed = new Promise<void>(resolve => workspaceFoldersChanged = resolve);
  const subscription = vscode.workspace.onDidChangeWorkspaceFolders(() => workspaceFoldersChanged?.());
  try {
    if (!vscode.workspace.updateWorkspaceFolders(0, currentFolders.length, ...folders)) {
      throw new Error('VS Code declined the E2E workspace folder update.');
    }

    await Promise.race([
      changed,
      delay(10_000).then(() => {
        throw new Error('Timed out waiting for VS Code to apply the E2E workspace folder update.');
      }),
    ]);
  }
  finally {
    subscription.dispose();
  }

  return getWorkspaceFolderInfo();
}

function getWorkspaceFolderInfo(): Array<{ name: string; uri: string; fileName: string }> {
  return vscode.workspace.workspaceFolders?.map(folder => ({
    name: folder.name,
    uri: folder.uri.toString(),
    fileName: folder.uri.fsPath,
  })) ?? [];
}

function getE2eRunPath(filePath: unknown, commandName = 'openFile'): string {
  if (typeof filePath !== 'string' || filePath.length === 0 || !path.isAbsolute(filePath)) {
    throw new Error(`Aspire extension E2E ${commandName} requires an absolute file path.`);
  }

  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    throw new Error(`Aspire extension E2E ${commandName} requires an existing file: ${filePath}`);
  }

  // The workspace root is normally inside the run root, but a run whose workspace has to live
  // elsewhere (the Java run keeps it in the repository so the CLI resolves packages correctly)
  // still needs to open its own sources. Both roots are harness-configured, so accept either.
  const allowedRoots = [
    process.env.ASPIRE_EXTENSION_E2E_RUN_ROOT,
    process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT,
  ].filter((root): root is string => typeof root === 'string' && root.length > 0);

  if (!allowedRoots.some(root => isPathWithinDirectory(filePath, root))) {
    throw new Error(`Aspire extension E2E ${commandName} can only open files inside the configured E2E run root or workspace root.`);
  }

  return filePath;
}

function getE2eRunAspireCliWorkingDirectory(directoryPath: unknown): string {
  if (typeof directoryPath !== 'string' || directoryPath.length === 0 || path.isAbsolute(directoryPath)) {
    throw new Error('Aspire extension E2E runAspireCli workingDirectory must be workspace-relative.');
  }

  const workspaceRoot = process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT;
  if (typeof workspaceRoot !== 'string' || workspaceRoot.length === 0) {
    throw new Error('Aspire extension E2E runAspireCli requires the configured E2E workspace root.');
  }

  const resolvedDirectory = path.resolve(workspaceRoot, directoryPath);
  if (!isPathWithinDirectory(resolvedDirectory, workspaceRoot)) {
    throw new Error('Aspire extension E2E runAspireCli workingDirectory must stay inside the configured E2E workspace root.');
  }

  if (!fs.existsSync(resolvedDirectory) || !fs.statSync(resolvedDirectory).isDirectory()) {
    throw new Error(`Aspire extension E2E runAspireCli requires an existing workingDirectory: ${directoryPath}`);
  }

  return resolvedDirectory;
}

// `addWorkspaceFolder` targets a folder that is not part of the workspace yet, so validate it
// against the harness roots rather than requiring containment in an already-open workspace folder.
export function getE2eAddableWorkspaceFolderPath(folderPath: unknown): string {
  if (typeof folderPath !== 'string' || folderPath.length === 0 || !path.isAbsolute(folderPath)) {
    throw new Error('Aspire extension E2E addWorkspaceFolder requires an absolute folder path.');
  }

  if (!fs.existsSync(folderPath) || !fs.statSync(folderPath).isDirectory()) {
    throw new Error(`Aspire extension E2E addWorkspaceFolder requires an existing folder: ${folderPath}`);
  }

  const allowedRoots = [
    process.env.ASPIRE_EXTENSION_E2E_RUN_ROOT,
    process.env.ASPIRE_EXTENSION_E2E_WORKSPACE_ROOT,
  ].filter((root): root is string => typeof root === 'string' && root.length > 0);

  if (!allowedRoots.some(root => isPathWithinDirectory(folderPath, root))) {
    throw new Error('Aspire extension E2E addWorkspaceFolder can only add folders inside the configured E2E run root or workspace root.');
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
  return relativePath === '' ||
      (relativePath !== '..' &&
          !relativePath.startsWith(`..${path.sep}`) &&
          !path.isAbsolute(relativePath));
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

export async function getDiagnosticsForFile(filePath: string): Promise<{ message: string; severity: vscode.DiagnosticSeverity; code?: string | number }[]> {
  if (typeof filePath !== 'string' || filePath.length === 0) {
    throw new Error('Aspire extension E2E getDiagnostics requires filePath.');
  }

  const uri = vscode.Uri.file(filePath);
  const wasAlreadyOpen = isFileOpenInAnyTab(uri);
  const document = await vscode.workspace.openTextDocument(uri);

  // The document has to be shown for a language server to publish diagnostics for it, but the Java
  // AppHost spec probes every generated SDK source - more than a hundred files. `preview` alone is
  // not enough to keep that to one tab because VS Code ignores it when the user has
  // `workbench.editor.enablePreview` off, so any tab this opened is closed again below. Otherwise
  // whichever suite tears down next closes them one at a time over WebDriver and exceeds its
  // timeout.
  await vscode.window.showTextDocument(document, { preview: true, preserveFocus: true });

  const diagnostics = vscode.languages.getDiagnostics(uri).map(diagnostic => ({
    message: diagnostic.message,
    severity: diagnostic.severity,
    code: typeof diagnostic.code === 'string' || typeof diagnostic.code === 'number' ? diagnostic.code : undefined,
  }));

  if (!wasAlreadyOpen) {
    const openedTabs = vscode.window.tabGroups.all
      .flatMap(group => group.tabs)
      .filter(tab => tab.input instanceof vscode.TabInputText && tab.input.uri.fsPath === uri.fsPath);

    if (openedTabs.length > 0) {
      await vscode.window.tabGroups.close(openedTabs, true);
    }
  }

  return diagnostics;
}

export async function getCodeLensesForFile(filePath: string): Promise<AspireExtensionE2ECodeLensProbeResult> {
  const uri = vscode.Uri.file(filePath);
  const document = await vscode.workspace.openTextDocument(uri);
  const codeLenses = await vscode.commands.executeCommand<vscode.CodeLens[] | undefined>('vscode.executeCodeLensProvider', uri);

  return {
    filePath: document.uri.fsPath,
    languageId: document.languageId,
    commandTitles: (codeLenses ?? [])
      .map(codeLens => codeLens.command?.title)
      .filter((title): title is string => typeof title === 'string' && title.length > 0),
  };
}

function isFileOpenInAnyTab(uri: vscode.Uri): boolean {
  return vscode.window.tabGroups.all.some(group => group.tabs.some(tab =>
    tab.input instanceof vscode.TabInputText && tab.input.uri.fsPath === uri.fsPath));
}

function getAppHostElement(appHostTreeProvider: AspireAppHostTreeProvider, appHostPath: string | undefined): unknown {
  return appHostPath ? appHostTreeProvider.findAppHostElement(appHostPath) ?? { appHostPath } : undefined;
}

function getRequiredAppHostActionElement(
  appHostTreeProvider: AspireAppHostTreeProvider,
  commandName: string,
  appHostPath: string,
): unknown {
  const element = appHostTreeProvider.findAppHostElement(appHostPath);
  if (!element) {
    throw new Error(`Aspire extension E2E ${commandName} could not find AppHost '${appHostPath}'.`);
  }

  return element;
}

function getAppHostPathForClipboard(element: unknown): string {
  if (hasAppHostPath(element)) {
    return element.appHostPath;
  }

  if (hasNestedAppHostPath(element)) {
    return element.appHost.appHostPath;
  }

  throw new Error('Aspire extension E2E AppHost clipboard assertion found an AppHost tree item with an unexpected shape.');
}

function hasAppHostPath(element: unknown): element is { appHostPath: string } {
  return typeof element === 'object'
    && element !== null
    && 'appHostPath' in element
    && typeof element.appHostPath === 'string'
    && element.appHostPath.length > 0;
}

function hasNestedAppHostPath(element: unknown): element is { appHost: { appHostPath: string } } {
  if (typeof element !== 'object' || element === null || !('appHost' in element)) {
    return false;
  }

  const appHost = element.appHost;
  return typeof appHost === 'object'
    && appHost !== null
    && 'appHostPath' in appHost
    && typeof appHost.appHostPath === 'string'
    && appHost.appHostPath.length > 0;
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

function getResourceNameForClipboard(element: unknown): string {
  if (!hasResourceForClipboard(element)) {
    throw new Error('Aspire extension E2E resource clipboard assertion found a resource tree item with an unexpected shape.');
  }

  return element.resource.displayName ?? element.resource.name;
}

function hasResourceForClipboard(element: unknown): element is { resource: { displayName?: string | null; name: string } } {
  if (typeof element !== 'object' || element === null || !('resource' in element)) {
    return false;
  }

  const resource = element.resource;
  return typeof resource === 'object'
    && resource !== null
    && 'name' in resource
    && typeof resource.name === 'string'
    && (!('displayName' in resource) || resource.displayName === undefined || resource.displayName === null || typeof resource.displayName === 'string');
}

function getResourceCommandElement(
  appHostTreeProvider: AspireAppHostTreeProvider,
  command: Extract<AspireExtensionE2EControlCommand, { name: 'executeResourceCommandItem' | 'executeCodeLensResourceAction' }>
): {
  commandName: string;
  commandJson: unknown;
  resourceItem: { resource: { name: string }; appHostPath?: string };
} {
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

  if (!hasResourceCommandShape(element)) {
    throw new Error(`Aspire extension E2E resource command item '${command.commandName}' on resource '${command.resourceName}' has an unexpected shape.`);
  }

  return element;
}

function hasResourceCommandShape(element: unknown): element is {
  commandName: string;
  commandJson: unknown;
  resourceItem: { resource: { name: string }; appHostPath?: string };
} {
  return typeof element === 'object'
    && element !== null
    && 'commandName' in element
    && typeof element.commandName === 'string'
    && 'commandJson' in element
    && 'resourceItem' in element
    && typeof element.resourceItem === 'object'
    && element.resourceItem !== null
    && 'resource' in element.resourceItem
    && typeof element.resourceItem.resource === 'object'
    && element.resourceItem.resource !== null
    && 'name' in element.resourceItem.resource
    && typeof element.resourceItem.resource.name === 'string';
}

function getLogFileElement(appHostTreeProvider: AspireAppHostTreeProvider, appHostPath?: string): unknown {
  const element = appHostTreeProvider.findLogFileElement(appHostPath);
  if (!element) {
    throw new Error('Aspire extension E2E log file command could not find an AppHost log file.');
  }

  return element;
}

function getLogFilePathForClipboard(element: unknown): string {
  if (!hasLogFilePath(element)) {
    throw new Error('Aspire extension E2E log file clipboard assertion found a log file tree item with an unexpected shape.');
  }

  return element.logFilePath;
}

function hasLogFilePath(element: unknown): element is { logFilePath: string } {
  return typeof element === 'object'
    && element !== null
    && 'logFilePath' in element
    && typeof element.logFilePath === 'string'
    && element.logFilePath.length > 0;
}

function getActiveEditorInfo(): { uri?: string; fileName?: string; text?: string } {
  const document = vscode.window.activeTextEditor?.document;
  return {
    uri: document?.uri.toString(),
    fileName: document?.fileName,
    text: document?.getText(),
  };
}

function getOpenEditorInfo(): Array<{ label: string; uri?: string; isPreview: boolean }> {
  return vscode.window.tabGroups.all.flatMap(group =>
    group.tabs.map(tab => {
      const uri = tab.input instanceof vscode.TabInputText ? tab.input.uri : undefined;
      return {
        label: tab.label,
        uri: uri?.toString(),
        isPreview: tab.isPreview,
      };
    }));
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
    cliPath: event.cliPath,
    cliTargetKey: event.cliTargetKey,
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

/**
 * Java language server API surface the E2E bridge depends on.
 *
 * redhat.java's own typings are not a dependency of this extension, so only the two members that
 * describe readiness are declared here.
 * https://github.com/redhat-developer/vscode-java/blob/master/src/extension.api.ts
 */
interface JavaLanguageServerApi {
  serverMode?: string;
  serverReady?: () => Promise<boolean>;
}

/**
 * Waits until the Java language server has finished importing the workspace.
 *
 * redhat.java reports no diagnostics both before it has looked at a file and after it has declared
 * that file clean, so a spec that reads diagnostics without waiting cannot tell a healthy workspace
 * from a language server that was never installed. That is precisely how the Java specs reported
 * green while no Java extension was present in the run at all.
 */
async function waitForJavaLanguageServer(timeoutMs: number): Promise<{ serverMode?: string }> {
  const extension = vscode.extensions.getExtension<JavaLanguageServerApi>(javaLanguageExtensionId);
  if (!extension) {
    throw new Error(`${javaLanguageExtensionId} is not installed, so nothing will import the Java workspace. Installed extensions: ${vscode.extensions.all.map(candidate => candidate.id).join(', ')}`);
  }

  const api = await extension.activate();
  if (typeof api?.serverReady !== 'function') {
    throw new Error(`${javaLanguageExtensionId} did not export serverReady(), so language server readiness cannot be observed.`);
  }

  let timer: NodeJS.Timeout | undefined;
  try {
    await Promise.race([
      api.serverReady(),
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(`The Java language server was not ready within ${timeoutMs}ms. Server mode: ${api.serverMode ?? '<unknown>'}.`)), timeoutMs);
      }),
    ]);
  }
  finally {
    if (timer) {
      clearTimeout(timer);
    }
  }

  // serverReady() resolves in LightWeight mode, which only serves syntax and answers no
  // project-aware request. That is not enough for the callers here: VS Code merges the CodeLens sets
  // of every registered provider, so while redhat.java is still importing, a .java file renders
  // `CodeLenses: (none)` - including the Aspire lens, which was ready the whole time. Waiting for
  // Standard mode is what makes "the workspace is imported" true rather than "the extension started".
  //
  // Server modes are LightWeight, Hybrid and Standard; only Standard means the project model exists.
  // See https://github.com/redhat-developer/vscode-java/blob/master/src/settings.ts (ServerMode).
  const deadline = Date.now() + timeoutMs;
  while (api.serverMode !== 'Standard' && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 500));
  }

  if (api.serverMode !== 'Standard') {
    throw new Error(`The Java language server did not reach Standard mode within ${timeoutMs}ms, so the workspace was never imported. Server mode: ${api.serverMode ?? '<unknown>'}.`);
  }

  return { serverMode: api.serverMode };
}

/**
 * Adds a folder to the running window's workspace and resolves once the extension host observes it.
 *
 * The spec used to drive `Workspaces: Add Folder to Workspace...` and its quick-open input. Adding the
 * first folder converts a single-folder window into an untitled multi-root workspace, which reloads
 * the window and restarts the extension host, and after that reload the second add never took - the
 * command ran, the input was confirmed, and `workspaceFolders` still never listed the folder, so the
 * spec burned its whole retry budget and failed on the confirmation poll.
 *
 * What the spec proves is that CLI commands target the right workspace folder. How the folder gets
 * added is incidental, so it goes through the API that VS Code itself calls rather than through the
 * UI, which removes the reload race without weakening the proof.
 *
 * `updateWorkspaceFolders` returns false when the edit could not be applied at all, and returning true
 * only means it was accepted - the folder appears asynchronously, so the caller still has to observe
 * `onDidChangeWorkspaceFolders`. Both are handled here so callers get one settled answer.
 */
async function addWorkspaceFolderForE2E(folderPath: string): Promise<{ added: boolean; folders: string[] }> {
    const uri = vscode.Uri.file(folderPath);
    const alreadyPresent = vscode.workspace.workspaceFolders?.some(folder => folder.uri.fsPath === uri.fsPath) ?? false;
    if (!alreadyPresent) {
        const accepted = vscode.workspace.updateWorkspaceFolders(vscode.workspace.workspaceFolders?.length ?? 0, null, { uri });
        if (!accepted) {
            throw new Error(`VS Code rejected adding '${folderPath}' to the workspace.`);
        }

        await new Promise<void>((resolve, reject) => {
            const timer = setTimeout(() => {
                subscription.dispose();
                reject(new Error(`'${folderPath}' was accepted but never appeared in workspaceFolders.`));
            }, 30000);
            const subscription = vscode.workspace.onDidChangeWorkspaceFolders(() => {
                if (vscode.workspace.workspaceFolders?.some(folder => folder.uri.fsPath === uri.fsPath)) {
                    clearTimeout(timer);
                    subscription.dispose();
                    resolve();
                }
            });
        });
    }

    return {
        added: !alreadyPresent,
        folders: vscode.workspace.workspaceFolders?.map(folder => folder.uri.fsPath) ?? [],
    };
}

/**
 * Renders every resource the tree knows about, so an endpoint that never appears says why.
 *
 * The endpoint wait polls for a URL and returns undefined until one exists, so it never records an
 * error and its timeout reported only "Last error: <none>" - which cannot distinguish a resource that
 * failed to start from one still building from one that was never in the model at all.
 */
function describeResourcesForE2E(appHostTreeProvider: AspireAppHostTreeProvider, appHostPath: string, resourceName: string): string {
  const element = appHostTreeProvider.findResourceElement(resourceName, appHostPath);
  if (!(element instanceof ResourceItem)) {
    return `resource '${resourceName}' is not in the tree for '${appHostPath}'.`;
  }

  const describe = (resource: ResourceJson) =>
    `${resource.name} [type=${resource.resourceType}, state=${resource.state ?? '<none>'}, health=${resource.healthStatus ?? '<none>'}, exitCode=${resource.exitCode ?? '<none>'}, urls=${(resource.urls ?? []).map(url => url.url).join(',') || '<none>'}]`;

  const siblings = element.allResources ?? [element.resource];
  return `${describe(element.resource)}; all resources: ${siblings.map(describe).join(' | ')}`;
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
