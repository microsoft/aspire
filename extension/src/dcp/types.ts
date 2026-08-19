import * as vscode from 'vscode';
import type { AspireDebugSession, DashboardLaunchBehavior } from '../debugger/AspireDebugSession';
import { appHostLaunchTokenConfigKey, appHostRestartSourceSessionIdConfigKey, appHostSelectionOriginConfigKey, type AppHostSelectionOrigin } from '../debugger/AspireDebugConfigurationMetadata';

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

export interface RustCargoLaunchTarget {
    args?: string[];
    executable_path?: string;
}

export interface RustLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "rust";
    cargo?: RustCargoLaunchTarget;
    working_directory?: string;
}

export function isRustLaunchConfiguration(obj: any): obj is RustLaunchConfiguration {
    return obj && obj.type === 'rust';
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

export interface JavaLaunchConfiguration extends ExecutableLaunchConfiguration {
    type: "java";
    request?: "launch" | "attach";
    working_directory?: string;
    // A fully qualified class name, optionally prefixed with a Java module name
    // ("[module/]com.example.Api"), or the path of the .java source file declaring main. Absent when
    // the IDE should resolve the entry point itself. A JAR path is never valid here; an executable
    // JAR is sent on class_paths with its manifest Main-Class here.
    // See src/Aspire.Hosting.Java/JavaLaunchConfiguration.cs.
    main_class?: string;
    // The name the Java tooling imported this resource's project under. Only sent when main_class
    // could not be determined, to scope the adapter's entry point search to a single project.
    project_name?: string;
    // Classpath entries to launch the JVM with, used when the resource runs a prebuilt JAR. Absent
    // when the IDE should resolve the classpath from the project itself.
    class_paths?: string[];
    // JVM options (e.g. "-Xmx512m"). These are the JVM's own arguments, not the application's.
    vm_args?: string[];
    // "maven" or "gradle", or absent when the resource runs a prebuilt JAR and therefore has no
    // build files whose classpath the Java language server could refresh.
    build_tool?: string;
}

export function isJavaLaunchConfiguration(obj: any): obj is JavaLaunchConfiguration {
    return obj && obj.type === 'java';
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

export type DebugConfigurationArguments = string | string[];

export interface DebugLaunchSettings {
    [key: string]: unknown;
    env?: { [key: string]: string };
    args?: DebugConfigurationArguments;
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
    // The DCP contract permits omission when termination is not caused by a process exit.
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
    resetStopSessionAttempt?(): void;
}

export interface AspireResourceExtendedDebugConfiguration extends vscode.DebugConfiguration {
    runId: string;
    debugSessionId: string | null;
    projectFile?: string;
    isApphost?: boolean;
}

export type AspireCommandType = 'run' | 'deploy' | 'publish' | 'do';
export type AspireOperationKind = AspireCommandType | 'test' | 'unknown';

export interface AspireExtendedDebugConfiguration extends vscode.DebugConfiguration {
    program: string;
    debuggers?: AspireDebuggersConfiguration;
    command?: AspireCommandType;
    dashboardBrowser?: DashboardLaunchBehavior;
    args?: string[];
    step?: string;
    skipCliAvailabilityCheck?: boolean;
    resolvedCliPath?: string;
    env?: { [key: string]: string };
    [appHostLaunchTokenConfigKey]?: number;
    [appHostRestartSourceSessionIdConfigKey]?: string;
    [appHostSelectionOriginConfigKey]?: AppHostSelectionOrigin;
}

interface AspireDebuggersConfiguration {
    [key: string]: DebugLaunchSettings;
}

export interface RunSessionInfo {
    protocols_supported: string[];
    supported_launch_configurations: string[];
}
