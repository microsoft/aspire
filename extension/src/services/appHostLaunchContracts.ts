import * as vscode from 'vscode';
import { AspireCommandType, AspireOperationKind } from '../dcp/types';
import { appHostLaunchTargetChanged, appHostLifecycleBusy } from '../loc/strings';
import { type OpaqueAppHostIdentity } from '../utils/appHostIdentity';

export interface AppHostLaunchRequestedEvent {
    appHostPath: string;
    command: AspireCommandType;
    noDebug: boolean;
    doStep?: string;
    cliPath?: string;
    cliTargetKey?: string;
    executionSuppressed: boolean;
}

export interface AppHostDebugSessionTerminatedEvent {
    appHostPath: string;
    command?: AspireCommandType;
    shouldRequestStopRefresh: boolean;
    shouldMarkAppHostStopping: boolean;
}

/**
 * A durable non-Run AppHost operation (`deploy`, `publish`, or `do`) that is currently
 * pending or driving an active debug session.
 *
 * Run launches are deliberately excluded: a Run is represented by its long-lived running
 * AppHost plus the launching and stop-refresh state. Deploy/publish/do have no running
 * AppHost of their own, so the extension records them here to reflect that an operation is
 * in flight for the AppHost even though nothing appears in the running list.
 */
export interface AppHostOperationState {
    readonly appHostPath: string;
    readonly command: AspireCommandType;
    readonly noDebug: boolean;
    readonly doStep?: string;
}

export interface AppHostLaunchSession {
    readonly appHostPath: string | undefined;
    readonly appHostIdentity?: OpaqueAppHostIdentity;
    /**
     * The concrete AppHost the extension resolved for this session, when the session's
     * own `program` is a workspace folder rather than a file.
     *
     * `Aspire: Configure launch.json` writes `program: '${workspaceFolder}'`, and
     * `AspireDebugConfigurationProvider` also falls back to the folder when `program` is
     * absent, so for the standard "configure launch.json then F5" flow `appHostPath` is a
     * directory and can never match a requested AppHost file. The configuration provider
     * has already resolved the unambiguous candidate for that folder, so carry it here
     * instead of guessing which AppHost under the folder is running.
     */
    readonly resolvedAppHostPath: string | undefined;
    readonly operationKind: AspireOperationKind;
    readonly startupCompleted: boolean;
    readonly configuration: { readonly noDebug?: boolean;[key: string]: unknown };
    stopDebugging(): Promise<void>;
}

/**
 * Safe session details the editor-assistance surfaces are allowed to inspect.
 *
 * The snapshot deliberately excludes VS Code debug session ids, process ids, and the
 * full debug configuration. Consumers only need enough state to summarize the AppHost
 * the editor is already managing.
 */
export interface AppHostEditorSessionSnapshot {
    readonly appHostPath: string | undefined;
    readonly resolvedAppHostPath: string | undefined;
    readonly appHostIdentity?: OpaqueAppHostIdentity;
    readonly operationKind: AspireOperationKind;
    readonly startupCompleted: boolean;
    readonly noDebug: boolean | undefined;
    readonly isStopping: boolean;
}

export type EditorResourceSessionState = 'starting' | 'running' | 'stopping';
export type EditorResourceSessionMode = 'run' | 'debug' | 'other';

/**
 * Safe child-session details exposed to editor-assistance services.
 *
 * AppHost, source-target, and resource-executable paths are used only for exact
 * internal correlation. The snapshot deliberately omits the VS Code session id,
 * process id, full debug configuration, and resource metadata so callers cannot
 * turn it into an ambient debug-session handle.
 */
export interface EditorResourceSessionSnapshot {
    readonly appHostPath: string;
    readonly appHostIdentity?: OpaqueAppHostIdentity;
    readonly targetPath: string;
    readonly resourceExecutablePaths?: readonly string[];
    readonly state: EditorResourceSessionState;
    readonly mode: EditorResourceSessionMode;
}

export interface RunningAppHost {
    readonly appHostPath: string;
}

/**
 * One lifecycle operation's AppHost, resolved once into the name the caller chose and the
 * physical AppHost that name selected.
 *
 * The two are kept apart on purpose. `selectorPath` is provenance: it is what the caller chose,
 * what a workspace-relative display renders, which workspace folder's CLI and settings apply,
 * and what is checked again before the operation commits. `canonicalPath` is what the operation
 * is actually performed against - reservations, CLI probes, and the debug configuration - because
 * a name can be repointed while any of those steps is in flight, and an operation that followed
 * the name would then act on an AppHost the caller never selected while `identity` still
 * attributes it to the one they did.
 *
 * Callers resolve this once, before taking the lifecycle lock, and carry the whole value through.
 * Passing only the canonical path forward loses the selector, which silently turns the freshness
 * check into a comparison of the physical path against itself - a check that can no longer fail.
 */
export interface AppHostLaunchTarget {
    readonly selectorPath: string;
    readonly canonicalPath: string;
    readonly identity: OpaqueAppHostIdentity;
    /** Stable workspace-relative identity used for user-facing launch presentation. */
    readonly displayPath: string;
}

export type AppHostStopResult =
    | { readonly outcome: 'stopped'; readonly controller: 'editor'; readonly noDebug: boolean }
    | { readonly outcome: 'stopped'; readonly controller: 'external' }
    | { readonly outcome: 'notRunning'; readonly controller: 'none' }
    | { readonly outcome: 'alreadyStarting'; readonly controller: 'editor' }
    | { readonly outcome: 'ambiguousSession'; readonly controller: 'editor' }
    | { readonly outcome: 'ambiguousAppHost'; readonly controller: 'external' };

/**
 * Sessions proven to belong to a requested AppHost, plus whether any session could not be
 * proven either way.
 *
 * `ambiguous` exists because a project file and a sibling `Program.cs` only describe one
 * AppHost when the directory forces that pairing. When it does not, answering "no
 * sessions" would be a guess that lets a caller start a duplicate AppHost, and answering
 * "this session" would let a caller stop the wrong one.
 */
export interface AppHostEditorSessions {
    readonly sessions: readonly AppHostLaunchSession[];
    readonly ambiguous: boolean;
}

export const appHostLifecycleLockWaitTimeoutMs = 10_000;

/**
 * How long one lifecycle operation may run before the lock cancels it.
 *
 * Generous on purpose: a real AppHost shutdown tears down containers and other
 * resources, so this is a stuck-operation backstop rather than an operation timeout.
 */
export const appHostLifecycleLockMaxHoldMs = 120_000;

/**
 * How long a `launch.json`/F5 launch stays reserved before the reservation expires.
 *
 * It only has to cover the gap between VS Code resolving the debug configuration and the
 * debug session becoming observable; after that the session itself is the evidence.
 */
export const externalLaunchReservationTimeoutMs = 60_000;

export class AppHostLifecycleLockTimeoutError extends Error {
    constructor() {
        // `AppHostLaunchService.launch` is the editor's own run/debug path, so this
        // message can reach a notification via showErrorMessage. It must therefore be
        // localized, unlike the tool path where the timeout only maps to a `busy` outcome.
        super(appHostLifecycleBusy);
        this.name = 'AppHostLifecycleLockTimeoutError';
    }
}

export class AppHostLaunchTargetChangedError extends Error {
    constructor() {
        // `AppHostLaunchService.launch` is the editor's own run/debug path, so this message can
        // reach a notification via showErrorMessage and has to be localized. The tool path maps
        // it to a generic failure outcome, which keeps the AppHost identity out of tool output.
        super(appHostLaunchTargetChanged);
        this.name = 'AppHostLaunchTargetChangedError';
    }
}

export class AppHostStopError extends Error {
    constructor(
        readonly controller: 'editor' | 'external',
        readonly noDebug: boolean | undefined,
        error: unknown) {
        super(error instanceof Error ? error.message : String(error));
        this.name = 'AppHostStopError';
    }
}

export class AppHostStopCancellationError extends vscode.CancellationError {
    constructor(
        readonly controller: 'editor' | 'external',
        readonly noDebug: boolean | undefined) {
        super();
    }
}
