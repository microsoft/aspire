import * as vscode from 'vscode';

import { type CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { type AppHostIdentityRelation } from '../utils/appHostIdentity';
import { type AppHostLaunchIsolation, type AppHostStopResult } from '../services/AppHostLaunchService';
import { isValidLaunchProfile } from '../utils/launchProfile';

/**
 * Names of the contributed language model tools. These must match the `name`
 * entries under `contributes.languageModelTools` in package.json and the
 * `onLanguageModelTool:` activation events, because VS Code resolves the
 * registration and the manifest entry by name.
 * See https://code.visualstudio.com/api/extension-guides/ai/tools
 */
export const aspireAppHostStartToolName = 'aspire_apphost_start';
export const aspireAppHostStopToolName = 'aspire_apphost_stop';

export type AppHostLifecycleMode = 'run' | 'debug';

/**
 * Who controls the AppHost process the tool acted on. `editor` means an Aspire debug
 * session created by this extension, `external` means a process the extension can
 * observe but did not start (a terminal, another window, or the CLI directly), and
 * `unknown` means the probe itself failed, which is deliberately not collapsed
 * into `none`.
 *
 * Named "controller" rather than "ownership" because the repository already uses
 * "ownership" for build ownership, process ownership, and termination ownership;
 * a fourth meaning of the same word would be actively misleading.
 */
export type AppHostLifecycleController = 'editor' | 'external' | 'none' | 'unknown';

export type AppHostLifecycleOutcome =
    | 'started'
    | 'alreadyStarting'
    | 'alreadyRunning'
    | 'stopped'
    | 'notRunning'
    | 'ambiguousSession'
    | 'invalidInput'
    | 'unknownAppHost'
    | 'ambiguousAppHost'
    | 'discoveryFailed'
    | 'workspaceNotTrusted'
    | 'busy'
    | 'cancelled'
    | 'failed';

export interface AppHostStartToolInput {
    appHostPath: string;
    mode: AppHostLifecycleMode;
    /** When omitted, linked git worktrees start isolated. Explicit true/false overrides that. */
    isolated?: boolean;
    /** Optional profile name from the AppHost launchSettings.json. */
    launchProfile?: string;
}

export interface AppHostStopToolInput {
    appHostPath: string;
}

/**
 * The complete result contract returned to the model. Every field is derived from the
 * extension's own lifecycle state or from the AppHost registry the editor already
 * displays — never from CLI stderr, environment, dashboard URLs, or DCP/RPC
 * credentials — so a tool result cannot become an exfiltration channel for a
 * prompt-injected agent.
 */
export interface AppHostLifecycleToolResult {
    tool: string;
    outcome: AppHostLifecycleOutcome;
    /** Path relative to the containing workspace folder, or empty when the input could not be resolved. */
    appHostPath: string;
    requestedMode?: AppHostLifecycleMode;
    effectiveMode?: AppHostLifecycleMode;
    /** Present on start results only when a known effective isolation value exists. */
    isolated?: boolean;
    controller: AppHostLifecycleController;
    /**
     * The selectors the tool accepts, returned only when the requested one did not
     * resolve. Without it a model that guesses wrong has no way to recover except by
     * guessing again, and these are the same paths the AppHost view already shows.
     */
    knownAppHosts?: readonly string[];
}

/**
 * Narrow view of `AppHostLaunchService` used by the tools. Launches are pinned to
 * editor-owned `run` sessions; stops use the same shared lifecycle operation as the
 * Aspire tree so editor and CLI-started AppHosts follow one policy.
 */
export interface AppHostLifecycleLaunchService {
    isLaunching(appHostPath: string): boolean;
    /**
     * Synchronously claims the launching slot, or reports that another launch already
     * holds it. See `AppHostLaunchService.tryReserveLaunch`.
     */
    tryReserveLaunch(appHostPath: string): boolean;
    clearLaunching(appHostPath: string): void;
    getEditorRunSessions(appHostPath: string): AppHostLifecycleEditorSessions;
    getRunningAppHosts(token: vscode.CancellationToken): Promise<readonly AppHostLifecycleRunningAppHost[]>;
    compareAppHostIdentity(left: string | undefined, right: string | undefined): AppHostIdentityRelation;
    runWithAppHostLifecycleLock<T>(appHostPath: string, token: vscode.CancellationToken, action: (token: vscode.CancellationToken) => Promise<T>): Promise<T>;
    resolveLaunchIsolation(appHostPath: string, isolated: boolean | undefined, token: vscode.CancellationToken): Promise<AppHostLaunchIsolation>;
    launchFromLifecycleOwner(appHostPath: string, command: 'run', noDebug: boolean, isolated: boolean | undefined, token: vscode.CancellationToken, launchProfile?: string): Promise<AppHostLaunchIsolation | undefined>;
    stopAppHost(appHostPath: string, token: vscode.CancellationToken): Promise<AppHostStopResult>;
    stopAppHostFromLifecycleOwner(appHostPath: string, token: vscode.CancellationToken): Promise<AppHostStopResult>;
}

/**
 * Narrow view of `AppHostDiscoveryService`. This is the registry the AppHost view, the
 * status bar, and the Run/Debug commands already resolve against, and it is populated by
 * the CLI's own `aspire ls --format json` output.
 */
export interface AppHostLifecycleDiscoveryService {
    discover(workspaceFolder: vscode.WorkspaceFolder, forceRefresh?: boolean, cancellationToken?: vscode.CancellationToken): Promise<readonly CandidateAppHostDisplayInfo[]>;
}

/**
 * Editor-created sessions for a requested AppHost, plus whether any session's relationship
 * to it could not be proven. See {@link AppHostIdentityRelation}.
 */
export interface AppHostLifecycleEditorSessions {
    readonly sessions: readonly AppHostLifecycleEditorSession[];
    readonly ambiguous: boolean;
}

export interface AppHostLifecycleRunningAppHost {
    readonly appHostPath: string;
}

/**
 * Narrow view of `AspireDebugSession`. `stopDebugging` is the coordinated stop that
 * terminates the AppHost child session before the Aspire parent session, which is
 * why the stop tool never touches processes directly.
 */
export interface AppHostLifecycleEditorSession {
    readonly appHostPath: string | undefined;
    /** True once the AppHost reported that startup finished and the dashboard is up. */
    readonly startupCompleted: boolean;
    // Mirrors the subset of AspireExtendedDebugConfiguration this surface reads. The
    // index signature keeps the real debug configuration structurally assignable
    // without importing the debugger types into the tool layer.
    readonly configuration: { readonly noDebug?: boolean; readonly command?: string;[key: string]: unknown };
    stopDebugging(): Promise<void>;
}

export interface AppHostLifecycleToolDependencies {
    readonly launchService: AppHostLifecycleLaunchService;
    readonly discoveryService: AppHostLifecycleDiscoveryService;
}

export interface AppHostLifecycleToolRegistration extends vscode.Disposable {
    readonly registered: boolean;
    /**
     * The registered tool instances by tool name. VS Code does not surface
     * `prepareInvocation` through `vscode.lm`, so E2E automation needs a way to ask the
     * extension's own instance for the confirmation it would present.
     */
    readonly tools: ReadonlyMap<string, PreparableAppHostLifecycleTool>;
}

export interface PreparableAppHostLifecycleTool {
    prepareInvocation(options: { readonly input: Record<string, unknown> }, token: vscode.CancellationToken): Promise<vscode.PreparedToolInvocation>;
}

export function createResult(
    tool: string,
    outcome: AppHostLifecycleOutcome,
    appHostPath: string,
    controller: AppHostLifecycleController,
    requestedMode: AppHostLifecycleMode | undefined,
    effectiveMode: AppHostLifecycleMode | undefined,
    knownAppHosts?: readonly string[],
    isolated?: boolean,
): AppHostLifecycleToolResult {
    const result: AppHostLifecycleToolResult = { tool, outcome, appHostPath, controller };
    if (requestedMode) {
        result.requestedMode = requestedMode;
    }

    if (effectiveMode) {
        result.effectiveMode = effectiveMode;
    }

    if (knownAppHosts) {
        result.knownAppHosts = knownAppHosts;
    }

    if (tool === aspireAppHostStartToolName && isolated !== undefined) {
        result.isolated = isolated;
    }

    return result;
}

export function parseMode(value: unknown): AppHostLifecycleMode | undefined {
    return value === 'run' || value === 'debug' ? value : undefined;
}

export function isValidStartInput(value: unknown): value is AppHostStartToolInput {
    if (!hasOnlyProperties(value, ['appHostPath', 'mode'], ['isolated', 'launchProfile']) ||
        typeof value.appHostPath !== 'string' ||
        parseMode(value.mode) === undefined) {
        return false;
    }

    return (!('isolated' in value) || typeof value.isolated === 'boolean') &&
        (!('launchProfile' in value) || isValidLaunchProfile(value.launchProfile));
}

export function isValidStopInput(value: unknown): value is AppHostStopToolInput {
    return hasOnlyProperties(value, ['appHostPath']) &&
        typeof value.appHostPath === 'string';
}

function hasOnlyProperties<T extends string>(value: unknown, properties: readonly T[], optional: readonly string[] = []): value is Record<T, unknown> {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
        return false;
    }

    const allowed = new Set([...properties, ...optional]);
    const actualProperties = Object.keys(value);
    return properties.every(property => Object.prototype.hasOwnProperty.call(value, property)) &&
        actualProperties.every(property => allowed.has(property));
}
