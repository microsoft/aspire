import * as vscode from 'vscode';
import { AspireDebugSession } from '../debugger/AspireDebugSession';

export interface ErrorResponse {
    error: ErrorDetails;
};

export interface ErrorDetails {
    code: string;
    message: string;
    details: ErrorDetails[];
};

type LaunchConfigurationMode = "Debug" | "NoDebug";

export interface ExecutableLaunchConfiguration {
    type: string;
    mode?: LaunchConfigurationMode | undefined;
}

export interface ProjectLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "project";
    launch_profile?: string;
    disable_launch_profile?: boolean;
    project_path: string;
}

export function isProjectLaunchConfiguration(obj: any): obj is ProjectLaunchConfiguration {
    return obj && obj.type === 'project';
}

export interface PythonLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "python";

    // legacy fields
    project_path?: string;
    program_path?: string;

    module?: string;
    interpreter_path?: string;
    working_directory?: string;
}

export function isPythonLaunchConfiguration(obj: any): obj is PythonLaunchConfiguration {
    return obj && obj.type === 'python';
}

export interface NodeLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "node"; // Provided by VS Code's built-in js-debug, no extension needed
    script_path?: string;
    runtime_executable?: string;
    working_directory?: string;
}

export function isNodeLaunchConfiguration(obj: any): obj is NodeLaunchConfiguration {
    return obj && obj.type === 'node';
}

export interface BrowserLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "browser";
    url?: string;
    web_root?: string;
    browser?: string;
}

export function isBrowserLaunchConfiguration(obj: any): obj is BrowserLaunchConfiguration {
    return obj && obj.type === 'browser';
}

export interface AzureFunctionsLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "azure-functions";
    project_path: string;
}

export function isAzureFunctionsLaunchConfiguration(obj: any): obj is AzureFunctionsLaunchConfiguration {
    return obj && obj.type === 'azure-functions';
}

export interface EnvVar {
    name: string;
    value: string;
}

export type ServerReadyActionAction = 'openExternally' | 'debugWithChrome' | 'debugWithEdge' | 'startDebugging';

export interface ServerReadyAction {
    action?: ServerReadyActionAction;
    /**
     * Regex that matches a URL. Prefer a capture group so VS Code can substitute it into uriFormat.
     * Example match: "Now listening on: https://localhost:5001"
     */
    pattern: string;
    /**
     * URI format string used with the first capture group (commonly "%s").
     */
    uriFormat?: string;
    /**
     * Web root for browser debugging (used by VS Code debug-server-ready).
     */
    webRoot?: string;
    /**
     * Optional name for startDebugging.
     */
    name?: string;
    /**
     * Optional debug configuration to start (used with startDebugging).
     */
    config?: vscode.DebugConfiguration;
    /**
     * Whether to stop the browser debug session when the server stops.
     */
    killOnServerStop?: boolean;
}

export interface RunSessionPayload {
    launch_configurations: ExecutableLaunchConfiguration[];
    env?: EnvVar[];
    args?: string[];
}

export interface DebugLaunchSettings {
    env?: { [key: string]: string };
    args?: string[];
    launchProfile?: string;
    disableLaunchProfile?: boolean;
}

export interface DcpServerConnectionInfo {
    address: string;
    token: string;
    certificate: string;
}

export interface RunSessionNotification {
    notification_type: 'processRestarted' | 'sessionTerminated' | 'serviceLogs' | 'sessionMessage';
    session_id: string;
    dcp_id: string;
}

export interface ProcessRestartedNotification extends RunSessionNotification {
    notification_type: 'processRestarted';
    pid?: number;
}

export interface SessionTerminatedNotification extends RunSessionNotification {
    notification_type: 'sessionTerminated';
    exit_code: number;
}

export interface ServiceLogsNotification extends RunSessionNotification {
    notification_type: 'serviceLogs';
    is_std_err: boolean;
    log_message: string;
}

export interface SessionMessageNotification extends RunSessionNotification {
    notification_type: 'sessionMessage';
    message: string;
    code?: string;
    level: "error" | "info" | "debug";
    details: ErrorDetails[];
}

export interface LaunchOptions {
    debug: boolean;
    forceBuild?: boolean;
    runId: string;
    debugSessionId: string;
    isApphost: boolean;
    debugSession: AspireDebugSession;
    parentDebugConfiguration?: AspireExtendedDebugConfiguration;
};

export interface StartAppHostOptions {
    forceBuild: boolean;
}

export interface AspireResourceDebugSession {
    id: string;
    session: vscode.DebugSession;
    stopSession(): void;
}

export interface AspireResourceExtendedDebugConfiguration extends vscode.DebugConfiguration {
    runId: string;
    debugSessionId: string | null;
    projectFile?: string;
    isApphost?: boolean;
}

export type AspireCommandType = 'run' | 'deploy' | 'publish' | 'do';

export interface AspireExtendedDebugConfiguration extends vscode.DebugConfiguration {
    program: string;
    debuggers?: AspireDebuggersConfiguration;
    command?: AspireCommandType;
    args?: string[];
    step?: string;
    env?: { [key: string]: string };
    serverReadyAction?: ServerReadyAction;
}

interface AspireDebuggersConfiguration {
    [key: string]: DebugLaunchSettings;
}

export interface RunSessionInfo {
    protocols_supported: string[];
    supported_launch_configurations: string[];
}
