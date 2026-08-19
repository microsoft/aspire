import * as vscode from 'vscode';
import { AspireCommandType, AspireOperationKind } from '../dcp/types';
import { appHostLifecycleBusy } from '../loc/strings';

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

export interface RunningAppHost {
    readonly appHostPath: string;
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
