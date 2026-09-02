import type * as vscode from 'vscode';
import type { DebugLaunchSettings, EnvVar, ExecutableLaunchConfiguration } from '../dcp/types';
import type { ViewMode } from '../data/AppHostDataRepository';
import type { CommandInvocationEvent } from '../utils/telemetry';
import type { AspireTerminalCommandEvent } from '../utils/AspireTerminalProvider';
import type { AppHostLaunchRequestedEvent } from '../services/AppHostLaunchService';
import type { AcquiredTestRunSession, TestRunSessionAcquireOptions } from '../dcp/TestRunSessionManager';

export interface AspireExtensionStateSnapshot {
    viewMode: ViewMode;
    isRepositoryLoading: boolean;
    isWorkspaceAppHostDiscoveryComplete: boolean;
    hasError: boolean;
    errorMessage: string | undefined;
    workspaceAppHost: AspireAppHostState | undefined;
    workspaceAppHostName: string | undefined;
    workspaceAppHostPath: string | undefined;
    workspaceAppHostCandidatePaths: readonly string[];
    workspaceAppHostDescription: string | undefined;
    workspaceResources: readonly AspireResourceState[];
    appHosts: readonly AspireAppHostState[];
    launchingPaths: readonly string[];
    stoppingPaths: readonly string[];
    debugSessions: readonly AspireDebugSessionState[];
}

export interface AspireAppHostState {
    appHostPath: string;
    appHostPid: number;
    dashboardUrl: string | null;
    resources: readonly AspireResourceState[] | null | undefined;
}

export interface AspireResourceState {
    name: string;
    displayName: string | null;
    resourceType: string;
    state: string | null;
    projectPath: string | null;
    dashboardUrl: string | null;
    urls: readonly AspireResourceUrlState[] | null;
    commands: Record<string, AspireResourceCommandState> | null;
}

export interface AspireResourceUrlState {
    name: string | null;
    displayName: string | null;
    url: string;
    isInternal: boolean;
}

export interface AspireResourceCommandState {
    displayName?: string | null;
    description: string | null;
    state?: string | null;
    visibility?: string | null;
}

export interface AspireDebugSessionState {
    appHostPath: string | undefined;
    dashboardUrl: string | undefined;
    startupCompleted: boolean;
}

export interface AspireServerInfo {
    address: string;
}

export interface WaitForStateOptions {
    timeoutMs?: number;
}

export interface AspireExtensionApiBase {
    readonly rpcServerInfo: AspireServerInfo;
    readonly dcpServerInfo: AspireServerInfo;
    readonly logDirectory: string;
    readonly state: AspireExtensionStateSnapshot;
    readonly onDidChangeState: vscode.Event<AspireExtensionStateSnapshot>;
    waitForState(predicate: (state: AspireExtensionStateSnapshot) => boolean, options?: WaitForStateOptions): Promise<AspireExtensionStateSnapshot>;
    waitForRepositoryIdle(options?: WaitForStateOptions): Promise<AspireExtensionStateSnapshot>;
    getDashboardUrl(appHostPath?: string): string | undefined;
}

export interface AspireExtensionApiV1 extends AspireExtensionApiBase {
    readonly apiVersion: 1;
}

export interface AspireExtensionApiV2 extends AspireExtensionApiBase {
    readonly apiVersion: 2;
    getRunningAppHosts(): Promise<readonly AspireAppHostState[]>;
    stopResource(resourceName: string, appHostPath: string): Promise<void>;
    startResource(resourceName: string, appHostPath: string): Promise<void>;
    acquireTestRunSession(options: TestRunSessionAcquireOptions): AcquiredTestRunSession;
    releaseTestRunSession(id: string): Promise<void>;
}

export type AspireExtensionApi = AspireExtensionApiV2;

export interface AspireExtensionE2EStateFile {
    extensionHostSessionId: string;
    updatedAt: string;
    /**
     * Identifies the E2E run whose extension host produced this file. The state and control files
     * live at a stable per-shard path, so an extension host left behind by an earlier run can still
     * be polling them. Readers compare this against their own run id to ignore a foreign writer.
     */
    runId?: string;
    state: AspireExtensionStateSnapshot;
    dashboardUrl?: string;
    commandInvocations: readonly AspireExtensionE2ECommandInvocation[];
    terminalCommands: readonly AspireExtensionE2ETerminalCommand[];
    debugLaunches: readonly AspireExtensionE2EDebugLaunch[];
    debugConsoleOutputs: readonly AspireExtensionE2EDebugConsoleOutput[];
    stoppingPathEvents: readonly AspireExtensionE2EStoppingPathEvent[];
    taskProcessEvents: readonly AspireExtensionE2ETaskProcessEvent[];
    browserDebugSessions: readonly AspireExtensionE2EBrowserDebugSession[];
    control?: AspireExtensionE2EControlStatus;
}

/**
 * A browser debug session (`pwa-chrome`, `pwa-msedge`, or `firefox`) that VS Code currently
 * reports as active. Browser sessions are not part of the extension's own state snapshot, so
 * E2E tests use this to observe whether a launched dashboard browser actually terminated.
 */
export interface AspireExtensionE2EBrowserDebugSession {
    id: string;
    type: string;
    name: string;
    parentSessionId?: string;
    parentSessionType?: string;
}

export interface AspireExtensionE2ESequence {
    sequence: number;
}

export type AspireExtensionE2ECommandInvocation = CommandInvocationEvent & AspireExtensionE2ESequence;

export type AspireExtensionE2ETerminalCommand = AspireTerminalCommandEvent & AspireExtensionE2ESequence;

export type AspireExtensionE2EDebugLaunch = AppHostLaunchRequestedEvent & AspireExtensionE2ESequence;

export type AspireExtensionE2EDebugConsoleOutput = AspireDebugConsoleOutputEvent & AspireExtensionE2ESequence;

export interface AspireExtensionE2EStoppingPathEvent extends AspireExtensionE2ESequence {
    appHostPath: string;
    state: 'entered' | 'left';
}

export interface AspireExtensionE2ETaskProcessEvent extends AspireExtensionE2ESequence {
    executionId: number;
    state: 'started' | 'ended';
    taskName: string;
    taskSource: string;
    taskDefinitionType: string;
    processId?: number;
    exitCode?: number;
}

export interface AspireExtensionE2ECodeLensProbeResult {
    filePath: string;
    languageId: string;
    commandTitles: readonly string[];
}

export interface AspireDebugConsoleOutputEvent {
    debugSessionId: string;
    appHostPath: string | undefined;
    category: 'stdout' | 'stderr';
    output: string;
}

export interface AspireExtensionE2EControlStatus {
    revision: number;
    status: 'started' | 'applied' | 'error';
    startedObserved?: boolean;
    errorMessage?: string;
    result?: unknown;
}

export interface AspireExtensionE2EControlPayload {
    revision: number;
    /**
     * Addresses this payload to a single E2E run. `revision` alone cannot do that: it restarts at 0
     * in every test process while a freshly launched extension host starts at -1, so a host left
     * behind by an earlier run would accept another run's commands.
     */
    runId?: string;
    aspireCliExecutablePath?: string;
    e2eCliExecutablePath?: string | null;
    forceCliUnavailable?: boolean;
    suppressTerminalCommandExecution?: boolean;
    suppressDebugLaunch?: boolean;
    showStatusDelayMs?: number | null;
    resetDashboardDefaultChangedNotification?: boolean;
    command?: AspireExtensionE2EControlCommand;
}

export type AspireExtensionE2EControlCommand =
    | { name: 'refreshAppHosts' }
    | { name: 'globalRefreshAppHosts' }
    | { name: 'switchToGlobalView' }
    | { name: 'switchToWorkspaceView' }
    | { name: 'runAppHost'; appHostPath?: string }
    | { name: 'stopAppHost'; appHostPath?: string }
    | { name: 'openDashboard'; appHostPath?: string }
    | { name: 'debugAppHost'; appHostPath?: string }
    | { name: 'publishAppHost'; appHostPath?: string }
    | { name: 'deployAppHostAction'; appHostPath: string }
    | { name: 'publishAppHostAction'; appHostPath: string }
    | { name: 'runPipelineStepAppHostAction'; appHostPath: string }
    | { name: 'debugPipelineStepAppHostAction'; appHostPath: string }
    | { name: 'openAppHostSource'; appHostPath?: string }
    | { name: 'viewAppHostSource'; appHostPath?: string }
    | { name: 'copyAppHostPath'; appHostPath?: string }
    | { name: 'viewAppHostLogFile'; appHostPath?: string }
    | { name: 'copyLogFilePath'; appHostPath?: string }
    | { name: 'viewResourceLogs'; appHostPath?: string; resourceName: string }
    | { name: 'openResourceTerminal'; appHostPath?: string; resourceName: string }
    | { name: 'copyResourceName'; appHostPath?: string; resourceName: string }
    | { name: 'copyEndpointUrl'; appHostPath?: string; resourceName?: string; url?: string }
    | { name: 'openInIntegratedBrowser'; appHostPath?: string; resourceName?: string; url?: string }
    | { name: 'stopResource'; appHostPath?: string; resourceName: string }
    | { name: 'startResource'; appHostPath?: string; resourceName: string }
    | { name: 'restartResource'; appHostPath?: string; resourceName: string }
    | { name: 'executeResourceCommand'; appHostPath?: string; resourceName: string }
    | { name: 'executeResourceCommandItem'; appHostPath?: string; resourceName: string; commandName: string }
    | { name: 'executeCodeLensResourceAction'; appHostPath?: string; resourceName: string; commandName: string }
    | { name: 'executeAspireCommand'; commandId: string; args?: readonly unknown[] }
    | { name: 'setSourceBreakpoint'; filePath: string; line: number; clearExisting?: boolean }
    | { name: 'clearBreakpoints' }
    | { name: 'getBreakpoints' }
    | { name: 'startDebugging'; configurationName: string }
    | { name: 'stopDebugging' }
    | { name: 'closeAllEditors' }
    | { name: 'getRegisteredAspireCommands' }
    | { name: 'getRegisteredLanguageModelTools' }
    | { name: 'prepareLanguageModelToolInvocation'; toolName: string; input: Record<string, unknown> }
    | { name: 'invokeLanguageModelTool'; toolName: string; input: Record<string, unknown>; times?: number; cancelAfterMs?: number }
    | { name: 'getDebugSessionProcessInfo'; appHostPath?: string }
    | { name: 'getExtensionPackageJson' }
    | { name: 'getExtensionFileStatus'; relativePaths: readonly string[] }
    | { name: 'getDiagnostics'; filePath: string }
    | { name: 'getDefinitions'; filePath: string; line: number; character: number }
    | { name: 'getJavaProjects' }
    | { name: 'getCodeLenses'; filePath: string }
    | { name: 'snapshotClipboard' }
    | { name: 'restoreClipboardSnapshot' }
    | { name: 'captureWorkspaceAppHostPathClipboardExpectation' }
    | { name: 'assertClipboardMatchesLastExpectation' }
    | { name: 'openFile'; filePath: string }
    | { name: 'openWorkspaceFolder'; folderPath: string }
    | { name: 'setWorkspaceFolders'; folders: readonly { folderPath: string; name?: string }[] }
    | { name: 'setWorkspaceFolderCliPath'; folderPath: string; cliPath: string }
    | { name: 'clearWorkspaceFolderCliPaths' }
    | { name: 'stopOwnedDebugSessionProcesses'; appHostPath?: string }
    | { name: 'getWorkspaceFolders' }
    | { name: 'addWorkspaceFolder'; folderPath: string }
    | { name: 'getActiveEditor' }
    | { name: 'getOpenEditors' }
    | { name: 'runAspireCli'; args: readonly string[]; workingDirectory: string; timeoutMs?: number; noExtensionVariables?: boolean; allowNonZeroExit?: boolean }
    | { name: 'getResourceDebuggerExtensions' }
    | { name: 'getSupportedCapabilities' }
    | { name: 'getVisibleExtensionIds' }
    | { name: 'waitForJavaLanguageServer'; timeoutMs?: number }
    | {
        name: 'createResourceDebugConfiguration';
        launchConfig: ExecutableLaunchConfiguration;
        args?: readonly string[];
        env?: readonly EnvVar[];
        debug?: boolean;
        isApphost?: boolean;
        debuggers?: Readonly<Record<string, DebugLaunchSettings>>;
        environmentKeys?: readonly string[];
    }
    | {
        name: 'proveAttachedResourceDebugging';
        appHostPath: string;
        resourceName: string;
        sourcePath: string;
        breakpointLine: number;
        expectedDebugType: 'coreclr' | 'go';
        expectedResponse: string;
        resourceRequestPath?: string;
        timeoutMs?: number;
    }
    | { name: 'proveAppHostAndResourceDebugging'; appHostPath: string; resourceName: string; appHostSourcePath: string; appHostBreakpointLine: number; resourceSourcePath: string; resourceBreakpointLine: number; resourceRequestPath?: string; timeoutMs?: number }
    | { name: 'proveMauiResourceDebugging'; appHostPath: string; resourceName: string; sourcePath: string; breakpointLine: number; timeoutMs?: number; pauseOnBreakpointMs?: number };
