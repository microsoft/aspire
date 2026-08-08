import * as vscode from 'vscode';
import type { AspireDebugSession, DashboardLaunchBehavior } from '../debugger/AspireDebugSession';

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

export interface GoLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "go";
    program?: string;
    working_directory?: string;
    build_flags?: string;
}

export function isGoLaunchConfiguration(obj: any): obj is GoLaunchConfiguration {
    return obj && obj.type === 'go';
}

export interface JavaScriptRuntimeLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "node" | "bun";
    script_path?: string;
    runtime_executable?: string;
    working_directory?: string;
    // Optional on purpose: an older AppHost (version skew vs the extension) won't emit this field at
    // all, leaving it undefined. Undefined is the legitimate legacy signal that tells the extension to
    // fall back to positional/runtime inference. Do not make it required.
    launch_method?: "direct" | "package-manager";
}

export function isJavaScriptRuntimeLaunchConfiguration(obj: any): obj is JavaScriptRuntimeLaunchConfiguration {
    return obj && (obj.type === 'node' || obj.type === 'bun');
}

export type NodeLaunchConfiguration = JavaScriptRuntimeLaunchConfiguration & { type: "node" };

export function isNodeLaunchConfiguration(obj: any): obj is NodeLaunchConfiguration {
    return obj && obj.type === 'node';
}

export type BunLaunchConfiguration = JavaScriptRuntimeLaunchConfiguration & { type: "bun" };

export function isBunLaunchConfiguration(obj: any): obj is BunLaunchConfiguration {
    return obj && obj.type === 'bun';
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

export interface MauiLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "maui";
    project_path: string;
    target_framework?: string;
    platform?: string;
    target_kind?: string;
    device?: string;
    runtime_identifier?: string;
    msbuild_properties?: Record<string, string>;
}

export function isMauiLaunchConfiguration(obj: any): obj is MauiLaunchConfiguration {
    return obj && obj.type === 'maui';
}

export interface EnvVar {
    name: string;
    value: string;
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
    // The DCP IDE execution contract permits omission when an exit code is
    // unavailable or inapplicable. Requested stops therefore omit this wire
    // field even though canceled telemetry uses -1 internally.
    // See docs/specs/IDE-execution.md#session-change-notifications.
    exit_code?: number;
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
};

export interface StartAppHostOptions {
    forceBuild: boolean;
}

export interface AspireResourceDebugSession {
    id: string;
    session: vscode.DebugSession;
    stopSession(): Thenable<void>;
}

/**
 * Which observable event marks the end of a resource run, and therefore which component emits
 * the terminal `sessionTerminated` notification to DCP.
 *
 * - `adapterExit` — the debug adapter exiting is the lifetime signal. `adapterTracker` emits
 *   `sessionTerminated` from `onExit`, carrying the observed debuggee exit code. This is correct
 *   for every resource type whose debuggee is a process the adapter owns.
 * - `debugSessionEnd` — the VS Code debug session ending is the lifetime signal, and
 *   `AspireDebugSession` emits `sessionTerminated`. Used by browser (js-debug) runs: js-debug is
 *   a server-hosted adapter shared across sessions, so its `onExit` is not a per-run signal, and
 *   it tears down child target sessions independently of the root session.
 *
 * This is a property of the *debugger integration*, not of an individual run, so it is declared
 * once per {@link ResourceDebuggerExtension} rather than assigned per session. That placement is
 * deliberate and load-bearing for safety: the resource debug configuration is merged with the
 * workspace `debuggers` setting, so anything stored there is workspace-writable. A workspace that
 * could set this field would be choosing who reports run termination — for example silently
 * rewiring the DCP lifecycle of `node`/`dotnet` resources, whose callbacks never overwrite it.
 * Deciding it at authoring time keeps it out of reach of settings entirely.
 *
 * The member names deliberately match the `kind` values of the `TerminationTrigger` union in
 * #19125, which owns the run-scoped termination state machine (dedupe, retention, telemetry).
 * That PR currently registers every `PUT /run_session` run as `adapterExit` on the assumption
 * that such resources always launch through a debug adapter — browser runs are the counterexample
 * this signal exists to describe. Keeping the names identical means the two models join by
 * reading `{ kind: configuration.terminationSignal }` at the registration site rather than
 * needing a translation table.
 */
export type ResourceTerminationSignal = 'adapterExit' | 'debugSessionEnd';

export interface AspireResourceExtendedDebugConfiguration extends vscode.DebugConfiguration {
    runId: string;
    debugSessionId: string | null;
    /**
     * Which event ends this run. Stamped from the resource's {@link ResourceDebuggerExtension}
     * after workspace settings are merged, so it always reflects the integration's declaration.
     *
     * Read it through `getResourceTerminationSignal` in `debugger/resourceSessionTermination.ts`
     * rather than directly: `vscode.DebugSession.configuration` is untyped JSON rebuilt by
     * VS Code, so validation and the default belong in one place.
     */
    terminationSignal: ResourceTerminationSignal;
    projectFile?: string;
    isApphost?: boolean;
}

export type AspireCommandType = 'run' | 'deploy' | 'publish' | 'do';

export interface AspireExtendedDebugConfiguration extends vscode.DebugConfiguration {
    program: string;
    debuggers?: AspireDebuggersConfiguration;
    command?: AspireCommandType;
    dashboardBrowser?: DashboardLaunchBehavior;
    args?: string[];
    step?: string;
    skipCliAvailabilityCheck?: boolean;
    env?: { [key: string]: string };
}

interface AspireDebuggersConfiguration {
    [key: string]: DebugLaunchSettings;
}

export interface RunSessionInfo {
    protocols_supported: string[];
    supported_launch_configurations: string[];
}
