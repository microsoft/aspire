import type * as vscode from 'vscode';
import type { EnvVar, ExecutableLaunchConfiguration } from '../dcp/types';
import type { ViewMode } from '../views/AppHostDataRepository';
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
    updatedAt: string;
    state: AspireExtensionStateSnapshot;
    dashboardUrl?: string;
    commandInvocations: readonly AspireExtensionE2ECommandInvocation[];
    terminalCommands: readonly AspireExtensionE2ETerminalCommand[];
    debugLaunches: readonly AspireExtensionE2EDebugLaunch[];
    debugConsoleOutputs: readonly AspireExtensionE2EDebugConsoleOutput[];
    control?: AspireExtensionE2EControlStatus;
}

export interface AspireExtensionE2ESequence {
    sequence: number;
}

export type AspireExtensionE2ECommandInvocation = CommandInvocationEvent & AspireExtensionE2ESequence;

export type AspireExtensionE2ETerminalCommand = AspireTerminalCommandEvent & AspireExtensionE2ESequence;

export type AspireExtensionE2EDebugLaunch = AppHostLaunchRequestedEvent & AspireExtensionE2ESequence;

export type AspireExtensionE2EDebugConsoleOutput = AspireDebugConsoleOutputEvent & AspireExtensionE2ESequence;

export interface AspireDebugConsoleOutputEvent {
    debugSessionId: string;
    appHostPath: string | undefined;
    category: 'stdout' | 'stderr';
    output: string;
}

export interface AspireExtensionE2EControlStatus {
    revision: number;
    status: 'started' | 'applied' | 'error';
    errorMessage?: string;
    result?: unknown;
}

export interface AspireExtensionE2EControlPayload {
    revision: number;
    aspireCliExecutablePath?: string;
    e2eCliExecutablePath?: string | null;
    forceCliUnavailable?: boolean;
    suppressTerminalCommandExecution?: boolean;
    suppressDebugLaunch?: boolean;
    showStatusDelayMs?: number | null;
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
    | { name: 'executeAspireCommand'; commandId: string; args?: readonly unknown[] }
    | { name: 'setSourceBreakpoint'; filePath: string; line: number; clearExisting?: boolean }
    | { name: 'clearBreakpoints' }
    | { name: 'getBreakpoints' }
    | { name: 'stopDebugging' }
    | { name: 'closeAllEditors' }
    | { name: 'getRegisteredAspireCommands' }
    | { name: 'getExtensionPackageJson' }
    | { name: 'getExtensionFileStatus'; relativePaths: readonly string[] }
    | { name: 'getDiagnostics'; filePath: string }
    | { name: 'readClipboard' }
    | { name: 'openWorkspaceFolder'; folderPath: string }
    | { name: 'getWorkspaceFolders' }
    | { name: 'getActiveEditor' }
    | { name: 'getResourceDebuggerExtensions' }
    | { name: 'createResourceDebugConfiguration'; launchConfig: ExecutableLaunchConfiguration; args?: readonly string[]; env?: readonly EnvVar[]; debug?: boolean }
    | { name: 'proveMauiResourceDebugging'; appHostPath: string; resourceName: string; sourcePath: string; breakpointLine: number; timeoutMs?: number; pauseOnBreakpointMs?: number };
