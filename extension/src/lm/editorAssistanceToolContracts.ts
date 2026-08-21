import * as vscode from 'vscode';

import { type ResourceJson } from '../data/appHostCliContracts';
import { type AppHostOperationTarget } from '../utils/appHostOperationTarget';
import { type EditorResourceSessionSnapshot } from '../services/appHostLaunchContracts';
import {
    type LaunchFailureCategory,
    type LaunchFailureController,
    type LaunchFailureExitCodeBucket,
    type LaunchFailureMode,
    type LaunchFailureProviderKind,
    type LaunchFailureStage,
    type SanitizedLaunchFailure,
} from '../services/launchFailureJournal';
import { type EditorStateSnapshotService } from './editorStateSnapshotService';
import {
    type AppHostTargetIdentity,
    type ResolvedAppHostTarget,
    type SafeAppHostTargetResolver,
} from './safeAppHostTargetResolver';
import {
    type DashboardBrowserType,
    type DashboardPresentation,
} from '../debugger/session/dashboardLauncher';
import { type HotReloadDiagnostics } from '../debugger/hotReload';
import { type AppHostDisplayInfo } from '../data/appHostCliContracts';

export const aspireDebugSessionStatusToolName = 'aspire_debug_session_status';
export const aspireExplainLaunchFailureToolName = 'aspire_explain_launch_failure';
export const aspireOpenDashboardToolName = 'aspire_open_dashboard';
export const aspireOpenOutputToolName = 'aspire_open_output';
export const aspireListDebugSessionsToolName = 'aspire_list_debug_sessions';
export const aspireHotReloadStatusToolName = 'aspire_hot_reload_status';

export type EditorAssistanceToolName =
    | typeof aspireDebugSessionStatusToolName
    | typeof aspireExplainLaunchFailureToolName
    | typeof aspireOpenDashboardToolName
    | typeof aspireOpenOutputToolName
    | typeof aspireListDebugSessionsToolName
    | typeof aspireHotReloadStatusToolName;

const maxResourceNameLength = 256;
const identityChangingCharacters = /[\u0000-\u001F\u007F-\u009F]|\p{Cf}/u;

export type DebugSessionStatusOutcome =
    | 'running'
    | 'starting'
    | 'stopping'
    | 'notDebugging'
    | 'multipleSessions'
    | 'appHostNotFound'
    | 'ambiguousAppHost'
    | 'resourceNotFound'
    | 'resourceAmbiguous'
    | 'workspaceNotTrusted'
    | 'invalidInput'
    | 'canceled'
    | 'error';

export type ExplainLaunchFailureOutcome =
    | 'failureFound'
    | 'noRecordedFailure'
    | 'appHostNotFound'
    | 'ambiguousAppHost'
    | 'workspaceNotTrusted'
    | 'invalidInput'
    | 'canceled'
    | 'error';

export type OpenDashboardOutcome =
    | 'opened'
    | 'dashboardUnavailable'
    | 'appHostNotRunning'
    | 'appHostNotFound'
    | 'ambiguousAppHost'
    | 'workspaceNotTrusted'
    | 'invalidInput'
    | 'canceled'
    | 'error';

export type OpenOutputOutcome =
    | 'opened'
    | 'workspaceNotTrusted'
    | 'invalidInput'
    | 'canceled'
    | 'error';

export type ListDebugSessionsOutcome =
    | 'sessionsFound'
    | 'noSessions'
    /** At least one listed AppHost's relationship to a running one could not be decided. */
    | 'ambiguousAppHost'
    | 'workspaceNotTrusted'
    | 'invalidInput'
    | 'canceled'
    | 'error';

export type HotReloadStatusOutcome =
    | 'applicable'
    | 'notApplicable'
    | 'noEditorControlledResource'
    /**
     * The requested AppHost is known but is not running, so it publishes no resources.
     *
     * It is reported separately because neither `resourceNotFound` nor
     * `noEditorControlledResource` is true of a stopped AppHost: both are statements about
     * what a running AppHost publishes, and answering with either would deny the existence of
     * a resource that simply has nothing to run under.
     */
    | 'appHostNotRunning'
    | 'resourceNotFound'
    | 'resourceAmbiguous'
    | 'tooManyActiveAppHosts'
    | 'appHostNotFound'
    | 'ambiguousAppHost'
    | 'workspaceNotTrusted'
    | 'invalidInput'
    | 'canceled'
    | 'error';

/**
 * Finite reasons behind a Hot Reload answer.
 *
 * Each identifier maps to one already-known fact: a C# Dev Kit Hot Reload diagnostic from
 * {@link HotReloadDiagnostics}, how the selected resource is debugged, or whether it is the
 * .NET project kind vsdbg and C# Dev Kit provide Hot Reload for. The enum exists so the
 * result can justify itself without quoting setting values, paths, or resource data.
 *
 * Who controls the resource is reported by `controller`, not here: `notEditorDebuggedResource`
 * states only that no editor debug session claims this resource, which is equally true of a
 * container inside an editor-run AppHost and of anything inside an externally started one.
 */
export type HotReloadEvidence =
    | 'devKitInstalled'
    | 'devKitNotInstalled'
    | 'hotReloadSettingEnabled'
    | 'hotReloadSettingDisabled'
    | 'hotReloadSettingUnavailable'
    | 'hotReloadOnSaveEnabled'
    | 'hotReloadOnSaveDisabled'
    | 'editorDebugSession'
    | 'editorDebugSessionStarting'
    | 'editorDebugSessionStopping'
    | 'editorSessionWithoutDebugger'
    | 'editorSessionModeUnknown'
    | 'notEditorDebuggedResource'
    | 'dotnetProjectResource'
    | 'nonDotnetResource';

/**
 * What to do when Hot Reload cannot carry a change, ordered smallest first.
 *
 * This is an escalation ladder rather than a prediction. The tool never observes whether a
 * restart or a rebuild actually carries the change, so the value is fixed and its ordering is
 * the whole content: restarting the affected resource is always safe to try first, and
 * rebuilding and restarting the whole AppHost is only correct once that is not enough, so it
 * never appears before it.
 */
export type HotReloadFallbackAction = 'restartResource' | 'rebuildAndRestartAppHost';

export type EditorAssistanceScope = 'appHost' | 'resource';
export type EditorAssistanceMode = 'run' | 'debug' | 'other';
export type EditorAssistanceController = 'editor' | 'external';

export interface EditorAssistanceResource {
    readonly resourceType: string;
    readonly state: EditorAssistanceResourceState;
    readonly healthStatus: string | null;
    readonly exitCode: number | null;
    readonly source: string | null;
}

export type EditorAssistanceResourceState =
    | 'Running'
    | 'Active'
    | 'Starting'
    | 'Building'
    | 'Stopping'
    | 'Stopped'
    | 'Waiting'
    | 'NotStarted'
    | 'Finished'
    | 'Exited'
    | 'FailedToStart'
    | 'RuntimeUnhealthy'
    | 'ValueMissing'
    | 'unknown';

export type EditorAssistanceRecommendedAction =
    | 'checkAspireOutput'
    | 'fixBuildErrors'
    | 'installAspireCli'
    | 'checkDependencies'
    | 'freeRequiredPort'
    | 'checkPermissions'
    | 'retryLaunch';

export interface DebugSessionStatusToolInput {
    readonly appHostPath: string;
    readonly resourceName?: string;
}

interface AppHostPathOnlyInput {
    readonly appHostPath: string;
}

export type ExplainLaunchFailureToolInput = AppHostPathOnlyInput;
export type OpenDashboardToolInput = AppHostPathOnlyInput;

export type OpenOutputToolInput = Record<string, never>;
export type ListDebugSessionsToolInput = Record<string, never>;

export interface HotReloadStatusToolInput {
    /** When omitted, the tool answers only if one editor-controlled resource is unambiguous. */
    readonly resourceName?: string;
    /**
     * When omitted, every active AppHost controlled by this editor is considered. Supplying
     * the same workspace-relative selector the other tools take narrows the lookup to one
     * AppHost, which is how a resource name shared by several AppHosts is disambiguated.
     */
    readonly appHostPath?: string;
}

export interface DebugSessionStatusAppHostResult {
    readonly success: true;
    readonly tool: typeof aspireDebugSessionStatusToolName;
    readonly outcome: 'running' | 'starting' | 'stopping' | 'notDebugging' | 'multipleSessions';
    readonly scope: 'appHost';
    readonly controller: EditorAssistanceController;
    readonly mode?: EditorAssistanceMode;
    readonly appHost: string;
}

export interface DebugSessionStatusResourceResult {
    readonly success: true;
    readonly tool: typeof aspireDebugSessionStatusToolName;
    readonly outcome: 'running' | 'starting' | 'stopping' | 'notDebugging' | 'multipleSessions';
    readonly scope: 'resource';
    readonly controller: EditorAssistanceController;
    readonly mode?: EditorAssistanceMode;
    readonly appHost: string;
    readonly resourceName: string;
    readonly resource: EditorAssistanceResource;
}

export type DebugSessionStatusResult = DebugSessionStatusAppHostResult | DebugSessionStatusResourceResult;

export interface DebugSessionStatusResourceFailureResult {
    readonly success: false;
    readonly tool: typeof aspireDebugSessionStatusToolName;
    readonly outcome: 'resourceNotFound' | 'resourceAmbiguous';
    readonly scope: 'resource';
    readonly controller: EditorAssistanceController;
    readonly appHost: string;
    readonly resourceName: string;
}

export interface DebugSessionStatusFailureResult {
    readonly success: false;
    readonly tool: typeof aspireDebugSessionStatusToolName;
    readonly outcome:
        | 'appHostNotFound'
        | 'ambiguousAppHost'
        | 'workspaceNotTrusted'
        | 'invalidInput'
        | 'canceled'
        | 'error';
}

export type DebugSessionStatusToolResult =
    | DebugSessionStatusResult
    | DebugSessionStatusResourceFailureResult
    | DebugSessionStatusFailureResult;

export interface ExplainLaunchFailureFoundResult {
    readonly success: true;
    readonly tool: typeof aspireExplainLaunchFailureToolName;
    readonly outcome: 'failureFound';
    readonly appHost: string;
    readonly stage: LaunchFailureStage;
    readonly category: LaunchFailureCategory;
    readonly controller: LaunchFailureController;
    readonly mode: LaunchFailureMode;
    readonly providerKind: LaunchFailureProviderKind;
    readonly exitCodeBucket: LaunchFailureExitCodeBucket;
    readonly recommendedActions: readonly EditorAssistanceRecommendedAction[];
}

export interface ExplainLaunchFailureNotFoundResult {
    readonly success: true;
    readonly tool: typeof aspireExplainLaunchFailureToolName;
    readonly outcome: 'noRecordedFailure';
    readonly appHost: string;
}

export interface ExplainLaunchFailureFailureResult {
    readonly success: false;
    readonly tool: typeof aspireExplainLaunchFailureToolName;
    readonly outcome:
        | 'appHostNotFound'
        | 'ambiguousAppHost'
        | 'workspaceNotTrusted'
        | 'invalidInput'
        | 'canceled'
        | 'error';
}

export type ExplainLaunchFailureToolResult =
    | ExplainLaunchFailureFoundResult
    | ExplainLaunchFailureNotFoundResult
    | ExplainLaunchFailureFailureResult;

export interface OpenDashboardSuccessResult {
    readonly success: true;
    readonly tool: typeof aspireOpenDashboardToolName;
    readonly outcome: 'opened';
    readonly presentation: DashboardPresentation;
}

export interface OpenDashboardFailureResult {
    readonly success: false;
    readonly tool: typeof aspireOpenDashboardToolName;
    readonly outcome: Exclude<OpenDashboardOutcome, 'opened'>;
}

export type OpenDashboardToolResult =
    | OpenDashboardSuccessResult
    | OpenDashboardFailureResult;

export interface OpenOutputSuccessResult {
    readonly success: true;
    readonly tool: typeof aspireOpenOutputToolName;
    readonly outcome: 'opened';
}

export interface OpenOutputFailureResult {
    readonly success: false;
    readonly tool: typeof aspireOpenOutputToolName;
    readonly outcome: Exclude<OpenOutputOutcome, 'opened'>;
}

export type OpenOutputToolResult =
    | OpenOutputSuccessResult
    | OpenOutputFailureResult;

export interface ListDebugSessionAppHostSummary {
    readonly appHost: string;
    readonly state: 'running' | 'starting' | 'stopping' | 'notDebugging' | 'multipleSessions';
    readonly mode: EditorAssistanceMode;
    readonly controller: EditorAssistanceController;
}

export interface ListDebugSessionsToolResult {
    readonly success: boolean;
    readonly tool: typeof aspireListDebugSessionsToolName;
    readonly outcome: ListDebugSessionsOutcome;
    readonly sessions: readonly ListDebugSessionAppHostSummary[];
    readonly truncated?: true;
}

/**
 * A Hot Reload answer for exactly one selected resource.
 *
 * `hotReloadEnabled` reports the window-wide C# Dev Kit capability, `outcome` reports whether
 * that capability could reach this resource at all, `controller` reports who controls the
 * AppHost the resource belongs to, and `evidence` explains both. The tool only ever reports:
 * it never applies, triggers, or confirms a code edit, so no field claims that a change
 * reached the running process.
 */
export interface HotReloadStatusReportResult {
    readonly success: true;
    readonly tool: typeof aspireHotReloadStatusToolName;
    readonly outcome: 'applicable' | 'notApplicable';
    readonly appHost: string;
    readonly resourceName: string;
    readonly controller: EditorAssistanceController;
    readonly hotReloadEnabled: boolean;
    readonly evidence: readonly HotReloadEvidence[];
    readonly fallback: readonly HotReloadFallbackAction[];
}

export interface HotReloadStatusUnavailableResult {
    readonly success: false;
    readonly tool: typeof aspireHotReloadStatusToolName;
    readonly outcome: Exclude<HotReloadStatusOutcome, 'applicable' | 'notApplicable'>;
}

export type HotReloadStatusToolResult =
    | HotReloadStatusReportResult
    | HotReloadStatusUnavailableResult;

export type EditorAssistanceToolResult =
    | DebugSessionStatusToolResult
    | ExplainLaunchFailureToolResult
    | OpenDashboardToolResult
    | OpenOutputToolResult
    | ListDebugSessionsToolResult
    | HotReloadStatusToolResult;

export interface EditorAssistanceResourceRepository {
    /**
     * Reads an AppHost's resources without depending on a live `describe --follow` stream.
     *
     * The streamed cache only holds resources while the Aspire view is showing or another
     * consumer keeps a stream open, so a window that has never opened the view sees nothing
     * there. Model-facing answers about whether a resource exists, whether it is unique, or
     * what its runtime state is have to come from this read instead, which completes on its
     * own without waiting for a resource to appear or polling for one.
     *
     * This is the only resource read these surfaces have. All of them report current state
     * rather than waiting for one, so a read that blocks until a named resource appears has
     * no caller here and is deliberately not offered.
     */
    fetchAppHostResourcesOnce(
        appHost: AppHostOperationTarget,
        token: vscode.CancellationToken): Promise<readonly ResourceJson[]>;
}

export interface EditorUiHandoffAppHostRepository {
    fetchRunningAppHostsOnce(token: vscode.CancellationToken): Promise<readonly AppHostDisplayInfo[]>;
}

export interface EditorUiHandoffOutput {
    show(preserveFocus?: boolean): void;
}

export interface EditorUiHandoffDebugSession {
    readonly cliProcessId: number | undefined;
    readonly configuration: { readonly dashboardBrowser?: unknown };
    readonly isShuttingDown: boolean;
    openDashboard(url: string, browserType: DashboardBrowserType): Promise<DashboardPresentation | undefined>;
}

export type EditorUiHandoffDashboardResult =
    | { readonly outcome: 'opened'; readonly presentation: DashboardPresentation }
    | { readonly outcome: 'dashboardUnavailable' | 'appHostNotRunning' | 'ambiguousAppHost' | 'error' };

export interface EditorUiHandoffOperations {
    openDashboard(target: ResolvedAppHostTarget, token: vscode.CancellationToken): Promise<EditorUiHandoffDashboardResult>;
    openOutput(token: vscode.CancellationToken): Promise<'opened' | 'error'>;
}

export interface EditorUiHandoffServiceDependencies {
    readonly targetResolver: SafeAppHostTargetResolver;
    readonly appHostRepository: EditorUiHandoffAppHostRepository;
    readonly output: EditorUiHandoffOutput;
    readonly getAspireDebugSessionOwners: () => readonly {
        readonly appHostIdentity: AppHostTargetIdentity;
        readonly session: EditorUiHandoffDebugSession;
    }[];
}

export interface EditorAssistanceToolDependencies {
    readonly targetResolver: SafeAppHostTargetResolver;
    readonly snapshotService: EditorStateSnapshotService;
    readonly resourceRepository: EditorAssistanceResourceRepository;
    readonly getEditorResourceSessions: () => readonly EditorResourceSessionSnapshot[];
    readonly readLatestLaunchFailures: (appHostPath: string) => readonly SanitizedLaunchFailure[];
    /**
     * The debugger's own Hot Reload probe. It is injected rather than imported so the tool
     * reports exactly what the dotnet launch path reports instead of growing a second,
     * drifting copy of C# Dev Kit detection.
     */
    readonly readHotReloadDiagnostics: () => HotReloadDiagnostics;
    readonly uiHandoffService: EditorUiHandoffOperations;
}

export interface EditorAssistanceToolRegistration extends vscode.Disposable {
    readonly registered: boolean;
    readonly tools: ReadonlyMap<string, vscode.LanguageModelTool<unknown>>;
}

export function isValidDebugSessionStatusInput(value: unknown): value is DebugSessionStatusToolInput {
    if (!hasOnlyAllowedProperties(value, ['appHostPath', 'resourceName']) ||
        typeof value.appHostPath !== 'string') {
        return false;
    }

    if (!Object.prototype.hasOwnProperty.call(value, 'resourceName')) {
        return true;
    }

    return isValidResourceName(value.resourceName);
}

/**
 * Accepts `{}`, `{ resourceName }`, `{ appHostPath }`, or both, and nothing else.
 *
 * Every property is genuinely optional here: an omitted name is a real request ("the resource
 * this window is debugging") and an omitted AppHost is a real request ("wherever it is running"),
 * so the empty object is valid input rather than a malformed call. The service, not the
 * validator, decides whether those requests resolve to exactly one resource, and the shared
 * resolver - not this function - decides whether an `appHostPath` names a known AppHost.
 */
export function isValidHotReloadStatusInput(value: unknown): value is HotReloadStatusToolInput {
    if (!isPlainObject(value)) {
        return false;
    }

    const properties = Object.keys(value);
    if (properties.some(property => property !== 'resourceName' && property !== 'appHostPath')) {
        return false;
    }

    const input = value as { readonly resourceName?: unknown; readonly appHostPath?: unknown };
    return (!properties.includes('resourceName') || isValidResourceName(input.resourceName)) &&
        (!properties.includes('appHostPath') ||
            (typeof input.appHostPath === 'string' && input.appHostPath.trim().length > 0));
}

export function isValidAppHostPathOnlyInput(value: unknown): value is AppHostPathOnlyInput {
    return hasOnlyAllowedProperties(value, ['appHostPath']) &&
        typeof value.appHostPath === 'string';
}

export function isValidEmptyObjectInput(value: unknown): value is OpenOutputToolInput | ListDebugSessionsToolInput {
    return isPlainObject(value) && Object.keys(value).length === 0;
}

function isValidResourceName(value: unknown): value is string {
    return typeof value === 'string' &&
        value.trim().length > 0 &&
        value.length <= maxResourceNameLength &&
        !identityChangingCharacters.test(value);
}

function isPlainObject(value: unknown): value is object {
    return typeof value === 'object' &&
        value !== null &&
        !Array.isArray(value) &&
        (Object.getPrototypeOf(value) === Object.prototype || Object.getPrototypeOf(value) === null);
}

function hasOnlyAllowedProperties<T extends string>(
    value: unknown,
    properties: readonly T[]): value is Record<T, unknown> {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
        return false;
    }

    const actualProperties = Object.keys(value);
    return actualProperties.length > 0 &&
        actualProperties.every(property => properties.includes(property as T)) &&
        Object.prototype.hasOwnProperty.call(value, 'appHostPath');
}
