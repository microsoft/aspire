import * as path from 'path';
import * as vscode from 'vscode';

import {
    appHostLifecycleStartConfirmationMessage,
    appHostLifecycleStartConfirmationTitle,
    appHostLifecycleStartInvocationMessage,
    appHostLifecycleStopConfirmationMessage,
    appHostLifecycleStopConfirmationTitle,
    appHostLifecycleStopInvocationMessage,
    appHostLifecycleUnresolvedPath,
    appHostLifecycleUnspecifiedMode,
} from '../loc/strings';
import { type CandidateAppHostDisplayInfo } from '../utils/appHostDiscovery';
import { canonicalizeAppHostPath, type AppHostIdentityRelation } from '../utils/appHostIdentity';
import { extensionLogOutputChannel } from '../utils/logging';
import { isCommandCancellation } from '../utils/telemetry';
import { AppHostLifecycleLockTimeoutError } from '../services/AppHostLaunchService';

/**
 * Names of the contributed language model tools. These must match the `name`
 * entries under `contributes.languageModelTools` in package.json and the
 * `onLanguageModelTool:` activation events, because VS Code resolves the
 * registration and the manifest entry by name.
 * See https://code.visualstudio.com/api/extension-guides/ai/tools
 */
export const aspireAppHostStartToolName = 'aspire_apphost_start';
export const aspireAppHostStopToolName = 'aspire_apphost_stop';

/**
 * Upper bound on the workspace-relative path a confirmation may show.
 *
 * A path longer than this is refused outright rather than elided, because an elided path
 * no longer identifies one file: two AppHosts sharing a long prefix would produce the same
 * prompt. The bound is far above any realistic repository path (Windows' own MAX_PATH is
 * 260 for a full path), so refusing beyond it costs nothing in practice.
 */
const maxConfirmationPathLength = 512;

/** Reject model-supplied selectors large enough to make normalization itself expensive. */
const maxAppHostSelectorLength = 4096;

/** Cap on how many AppHost paths an `unknownAppHost` result lists back to the model. */
const maxReportedKnownAppHosts = 32;

/**
 * Characters that change what a path *is* without changing, or while changing, how it
 * looks: C0/C1 controls and DEL, plus every Unicode format character (`\p{Cf}`).
 *
 * Bidi controls (U+202A-U+202E, U+2066-U+2069) reorder the run that follows them, so a
 * path can render as a completely different one. Zero-width characters (U+200B-U+200D)
 * are invisible, so two distinct files can produce identical-looking prompts. A registry
 * entry carrying one of these is dropped rather than shown with the characters deleted,
 * because deleting them would break the one-to-one relationship between the identity the
 * user confirms and the file that runs.
 * See https://unicode.org/reports/tr9/ and https://unicode.org/reports/tr36/#Bidirectional_Text_Spoofing
 */
const identityChangingCharacters = /[\u0000-\u001F\u007F-\u009F]|\p{Cf}/u;

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
    | 'notEditorOwned'
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
    controller: AppHostLifecycleController;
    /**
     * The selectors the tool accepts, returned only when the requested one did not
     * resolve. Without it a model that guesses wrong has no way to recover except by
     * guessing again, and these are the same paths the AppHost view already shows.
     */
    knownAppHosts?: readonly string[];
}

/**
 * Narrow view of `AppHostLaunchService` used by the tools. Only the editor-owned
 * launch path is reachable: there is deliberately no CLI fallback and no way to
 * request an Aspire command other than `run`.
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
    launchFromLifecycleOwner(appHostPath: string, command: 'run', noDebug: boolean, token: vscode.CancellationToken): Promise<void>;
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

/**
 * One entry of the AppHost registry, projected into the form the tool speaks.
 *
 * Every field comes from a candidate the discovery service enumerated, so the string the
 * confirmation renders and the path the launcher receives originate from the same object.
 * The model's input only ever selects one of these; it never contributes to one.
 */
interface ResolvedAppHostTarget {
    /** Absolute path exactly as the registry enumerated it, used for launching. */
    absolutePath: string;
    /** Path relative to the containing workspace folder, always with `/` separators. */
    relativePath: string;
    /**
     * The identity shown in the confirmation dialog. Identical to `relativePath` in a
     * single-root workspace, and prefixed with the workspace folder name otherwise, so a
     * selector that resolves under one root still names that root in the prompt.
     */
    displayPath: string;
}

type AppHostTargetResolution =
    | { resolved: true; target: ResolvedAppHostTarget }
    | { resolved: false; outcome: AppHostLifecycleOutcome; knownAppHosts?: readonly string[] };

type PreflightResult =
    | { rejected: true; result: AppHostLifecycleToolResult }
    | { rejected: false; target: ResolvedAppHostTarget };

/**
 * Backs the `aspire_apphost_start` / `aspire_apphost_stop` language model tools.
 *
 * The service is intentionally the only place that decides whether an agent request may
 * touch AppHost lifecycle state. It resolves the model's selector against the AppHost
 * registry the editor already maintains, enforces workspace trust, and refuses anything
 * it cannot prove is an editor-created Aspire debug session.
 *
 * Resolving against the registry rather than parsing a path is what makes the surface
 * safe: the model can only name something Aspire already enumerated, so a crafted string
 * cannot reach the filesystem, cannot become a launch target, and cannot make the
 * confirmation dialog show one identity while a different one runs.
 *
 * Lifecycle work is serialized per AppHost through {@link AppHostLifecycleLaunchService},
 * which the editor's own Run/Debug commands share, so a model call and a user action
 * cannot start two processes for the same AppHost. That guarantee covers callers routed
 * through those commands; starting a `launch.json` Aspire configuration with F5 goes
 * straight to the debug adapter and bypasses the lock, which is why every decision here
 * is re-validated against live session state rather than the lock alone.
 */
export class AppHostLifecycleToolService implements vscode.Disposable {
    private readonly _dependencies: AppHostLifecycleToolDependencies;
    private _disposed = false;

    constructor(dependencies: AppHostLifecycleToolDependencies) {
        this._dependencies = dependencies;
    }

    dispose(): void {
        this._disposed = true;
    }

    /**
     * Renders the identity the confirmation dialog must show for a requested selector.
     *
     * This runs the *same* registry resolution `invoke` runs and displays its result, so
     * the target the user approves is the target that gets executed. It performs discovery
     * to do so, which `prepareInvocation` allows: the API requires it to be free of side
     * effects, not free of work. Input that does not resolve is described with a fixed
     * placeholder rather than echoed, because such a call is always rejected anyway and
     * echoing it would hand the model free-form prose inside the trusted prompt that gates
     * "Always allow".
     */
    async describeTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<string> {
        const resolution = await this.resolveTarget(rawAppHost, token);
        return resolution.resolved ? resolution.target.displayPath : appHostLifecycleUnresolvedPath;
    }

    async start(input: AppHostStartToolInput, token: vscode.CancellationToken): Promise<AppHostLifecycleToolResult> {
        if (!isValidStartInput(input)) {
            return createResult(aspireAppHostStartToolName, 'invalidInput', '', 'none', undefined, undefined);
        }

        const requestedMode = input.mode;
        const preflight = await this.preflight(aspireAppHostStartToolName, input?.appHostPath, token, requestedMode);
        if (preflight.rejected) {
            return preflight.result;
        }

        try {
            // Probe for a process this extension does not own *before* taking the
            // lifecycle lock, and return early when the answer is "yes".
            //
            // `aspire ps` spawns the CLI and then queries each AppHost over its
            // backchannel, which can take tens of seconds when an AppHost is paused at a
            // breakpoint - the very situation this tool exists to protect. That slow case
            // is exactly the case this early exit covers, so the expensive probe never
            // runs while the lock is held. When the answer is "no" the probe result is
            // discarded: it is only a fast path, never the authority, because an AppHost
            // started from a terminal while this call waited up to 10s for the lock would
            // leave a stale `false` behind and allow a duplicate launch.
            if (!this.hasEditorSession(preflight.target.absolutePath) &&
                await this.isRunningOutsideEditor(preflight.target.absolutePath, token)) {
                // Launching again would start a second AppHost against the same project.
                // Report it instead so the agent can decide, and never adopt or kill a
                // process this extension does not own.
                return createResult(aspireAppHostStartToolName, 'alreadyRunning', preflight.target.relativePath, 'external', requestedMode, undefined);
            }

            return await this._dependencies.launchService.runWithAppHostLifecycleLock(preflight.target.absolutePath, token, async lockToken => {
                // Re-resolve after the confirmation and after waiting on the shared lock:
                // the file can be deleted or replaced, and an editor command may already
                // have launched this AppHost while this call was queued.
                const recheck = await this.preflight(aspireAppHostStartToolName, input.appHostPath, lockToken, requestedMode);
                if (recheck.rejected) {
                    return recheck.result;
                }

                const current = recheck.target;
                const owned = this.findEditorSessions(current.absolutePath);
                // A session that finished startup is checked before the launching flag on
                // purpose. That flag is only cleared once `aspire ps` reconciliation observes
                // the process, which can lag far behind the session itself.
                const runningSession = owned.sessions.find(session => session.startupCompleted);
                if (runningSession) {
                    return createResult(
                        aspireAppHostStartToolName,
                        'alreadyRunning',
                        current.relativePath,
                        'editor',
                        requestedMode,
                        getSessionMode(runningSession));
                }

                if (this._dependencies.launchService.isLaunching(current.absolutePath) || owned.sessions.length > 0) {
                    return createResult(aspireAppHostStartToolName, 'alreadyStarting', current.relativePath, 'editor', requestedMode, undefined);
                }

                if (owned.ambiguous) {
                    // A session exists whose AppHost cannot be told apart from this one -
                    // for example a sibling project file and a `Program.cs` in a directory
                    // holding several projects. Launching would risk a second process for
                    // an AppHost that is already running, so refuse instead of guessing.
                    return createResult(aspireAppHostStartToolName, 'ambiguousSession', current.relativePath, 'editor', requestedMode, undefined);
                }

                // Authoritative ownership check immediately before launching. This is the
                // one that matters: everything before it could be stale by now.
                if (await this.isRunningOutsideEditor(current.absolutePath, lockToken)) {
                    return createResult(aspireAppHostStartToolName, 'alreadyRunning', current.relativePath, 'external', requestedMode, undefined);
                }

                // Claim the launching slot in one synchronous step. The lifecycle lock only
                // serializes callers that take it, and `launch.json`/F5 reaches
                // `startDebugging` without it, so this claim - not the checks above - is
                // what makes "no second AppHost" hold against a concurrent editor launch.
                if (!this._dependencies.launchService.tryReserveLaunch(current.absolutePath)) {
                    return createResult(aspireAppHostStartToolName, 'alreadyStarting', current.relativePath, 'editor', requestedMode, undefined);
                }

                try {
                    // `noDebug` is the only lever the tool exposes; the Aspire command is pinned
                    // to `run` so an agent can never reach deploy/publish/do through this surface.
                    await this._dependencies.launchService.launchFromLifecycleOwner(
                        current.absolutePath,
                        'run',
                        requestedMode === 'run',
                        lockToken);
                }
                catch (error) {
                    // The launch path clears its own reservation once it owns it, but a
                    // failure before that point (a disposed service, for example) would
                    // otherwise leave this AppHost reported as launching forever.
                    this._dependencies.launchService.clearLaunching(current.absolutePath);
                    return this.createErrorResult(aspireAppHostStartToolName, error, current.relativePath, 'editor', requestedMode, undefined);
                }

                return createResult(aspireAppHostStartToolName, 'started', current.relativePath, 'editor', requestedMode, requestedMode);
            });
        }
        catch (error) {
            return this.createErrorResult(aspireAppHostStartToolName, error, preflight.target.relativePath, 'editor', requestedMode, undefined);
        }
    }

    async stop(input: AppHostStopToolInput, token: vscode.CancellationToken): Promise<AppHostLifecycleToolResult> {
        if (!isValidStopInput(input)) {
            return createResult(aspireAppHostStopToolName, 'invalidInput', '', 'none', undefined, undefined);
        }

        const preflight = await this.preflight(aspireAppHostStopToolName, input?.appHostPath, token, undefined);
        if (preflight.rejected) {
            return preflight.result;
        }

        try {
            // Same reasoning as `start`: the `aspire ps` probe stays outside the lock so a
            // slow or wedged AppHost cannot block the user's own Run/Debug. The probe only
            // labels the outcome here, so its result is safe to carry into the lock, and it
            // is skipped entirely when the editor already owns a session for this AppHost.
            const externalControllerBeforeLock = this.hasEditorSession(preflight.target.absolutePath)
                ? undefined
                : await this.probeExternalControllerForStop(preflight.target.absolutePath, token);

            return await this._dependencies.launchService.runWithAppHostLifecycleLock(preflight.target.absolutePath, token, async lockToken => {
                const recheck = await this.preflight(aspireAppHostStopToolName, input.appHostPath, lockToken, undefined);
                if (recheck.rejected) {
                    return recheck.result;
                }

                const current = recheck.target;
                const owned = this.findEditorSessions(current.absolutePath);
                if (owned.sessions.length > 1) {
                    // Two sessions claim the same AppHost. Stopping "one of them" would be an
                    // arbitrary choice, so refuse and let the user disambiguate in the UI.
                    return createResult(aspireAppHostStopToolName, 'ambiguousSession', current.relativePath, 'editor', undefined, undefined);
                }

                if (owned.sessions.length === 0 && owned.ambiguous) {
                    // A session exists that this AppHost cannot be told apart from - a
                    // sibling project file and a `Program.cs` in a directory holding several
                    // projects, for instance. Stopping it would terminate an AppHost the
                    // caller never named, so refuse.
                    return createResult(aspireAppHostStopToolName, 'ambiguousSession', current.relativePath, 'editor', undefined, undefined);
                }

                if (owned.sessions.length === 0) {
                    const externalController = externalControllerBeforeLock
                        ?? await this.probeExternalControllerForStop(current.absolutePath, lockToken);
                    if (externalController === 'unknown') {
                        // The probe failed, so "nothing is running" would be an assertion the
                        // extension cannot make. Report the failure and let the agent retry
                        // or fall back rather than telling it the AppHost is not running.
                        return createResult(aspireAppHostStopToolName, 'failed', current.relativePath, 'unknown', undefined, undefined);
                    }

                    return createResult(
                        aspireAppHostStopToolName,
                        externalController === 'external' ? 'notEditorOwned' : 'notRunning',
                        current.relativePath,
                        externalController,
                        undefined,
                        undefined);
                }

                const session = owned.sessions[0];
                const effectiveMode = getSessionMode(session);
                if (lockToken.isCancellationRequested) {
                    return createResult(aspireAppHostStopToolName, 'cancelled', current.relativePath, 'editor', undefined, effectiveMode);
                }
                try {
                    await session.stopDebugging();
                }
                catch (error) {
                    return this.createErrorResult(aspireAppHostStopToolName, error, current.relativePath, 'editor', undefined, effectiveMode);
                }

                return createResult(aspireAppHostStopToolName, 'stopped', current.relativePath, 'editor', undefined, effectiveMode);
            });
        }
        catch (error) {
            return this.createErrorResult(aspireAppHostStopToolName, error, preflight.target.relativePath, 'editor', undefined, undefined);
        }
    }

    /**
     * Resolves a model-supplied selector against the AppHost registry.
     *
     * The selector is only ever *compared* against entries the discovery service
     * enumerated; it is never joined onto a directory, never normalized into a path, and
     * never reaches the filesystem. That is what makes confirmation spoofing
     * unrepresentable rather than merely rejected: whatever the model sends, the target
     * carried forward is one of Aspire's own candidates, so the identity shown in the
     * prompt and the identity handed to the launcher come from the same object.
     *
     * Resolution never guesses. A selector that names nothing is `unknownAppHost`, a
     * selector matching several candidates is `ambiguousAppHost`, and a registry that
     * could not be read is `discoveryFailed` rather than an empty list.
     */
    async resolveTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<AppHostTargetResolution> {
        if (typeof rawAppHost !== 'string') {
            return { resolved: false, outcome: 'invalidInput' };
        }

        const selector = rawAppHost.trim();
        if (selector.length === 0 || selector.length > maxAppHostSelectorLength) {
            return { resolved: false, outcome: 'invalidInput' };
        }

        // The manifest, the README, and the tool description all say the selector is a
        // workspace-relative path. An absolute path would still have to match a registry
        // entry to do anything, but accepting one would make the implementation contradict
        // its own documented contract, so it is refused up front.
        if (path.isAbsolute(selector)) {
            return { resolved: false, outcome: 'invalidInput' };
        }

        let knownAppHosts: readonly ResolvedAppHostTarget[];
        try {
            knownAppHosts = await this.enumerateKnownAppHosts(token);
        }
        catch (error) {
            if (isCommandCancellation(error)) {
                return { resolved: false, outcome: 'cancelled' };
            }

            // "The registry could not be read" is not "there are no AppHosts". Reporting
            // the latter would tell the agent its target does not exist when the truth is
            // that the extension could not find out.
            extensionLogOutputChannel.warn(`Aspire language model tools could not enumerate AppHosts: ${String(error)}`);
            return { resolved: false, outcome: 'discoveryFailed' };
        }

        const requestedKey = toSelectorKey(selector);
        const matches = knownAppHosts.filter(candidate =>
            toSelectorKey(candidate.relativePath) === requestedKey ||
            toSelectorKey(candidate.displayPath) === requestedKey);
        if (matches.length === 0) {
            return { resolved: false, outcome: 'unknownAppHost', knownAppHosts: describeKnownAppHosts(knownAppHosts) };
        }

        // A bare relative path can name candidates under several roots of a multi-root
        // workspace. Picking one would launch an AppHost the caller did not identify, so
        // the folder-qualified form has to be used instead.
        if (matches.length > 1) {
            return { resolved: false, outcome: 'ambiguousAppHost', knownAppHosts: describeKnownAppHosts(matches) };
        }

        return { resolved: true, target: matches[0] };
    }

    /**
     * Projects the discovery service's candidates into tool targets.
     *
     * Candidates outside every workspace folder are dropped: the tool's contract is
     * expressed in workspace-relative paths, and a candidate with no containing folder
     * has no such path to offer or to display.
     */
    private async enumerateKnownAppHosts(token: vscode.CancellationToken): Promise<readonly ResolvedAppHostTarget[]> {
        const workspaceFolders = vscode.workspace.workspaceFolders ?? [];
        const candidatesByFolder = await Promise.all(workspaceFolders.map(async folder => ({
            folder,
            candidates: await this._dependencies.discoveryService.discover(folder, false, token),
        })));

        const targets = new Map<string, ResolvedAppHostTarget>();
        for (const { folder, candidates } of candidatesByFolder) {
            // Containment is decided on the real paths, because a link inside the workspace
            // can point at a file outside it. The confirmation would show the in-workspace
            // link while `startDebugging` executed the external target, so a lexical check
            // alone would let the workspace boundary be crossed under an in-workspace name.
            const canonicalFolderPath = canonicalizeAppHostPath(folder.uri.fsPath);
            for (const candidate of candidates) {
                const relativePath = toContainedPosixRelativePath(folder.uri.fsPath, candidate.path);
                if (relativePath === undefined) {
                    continue;
                }

                // The lexical relative path is still what gets displayed: it is the name the
                // caller sees in the explorer, and it is the one they can pass back.
                if (toContainedPosixRelativePath(canonicalFolderPath, canonicalizeAppHostPath(candidate.path)) === undefined) {
                    continue;
                }

                const displayPath = workspaceFolders.length > 1
                    ? `${folder.name}/${relativePath}`
                    : relativePath;
                // Nested workspace folders enumerate the same file twice. Keying by the
                // absolute path collapses those into one target so a selector matching both
                // is not reported as ambiguous against itself. The deepest folder wins, so
                // the displayed path matches the folder the user sees in the explorer.
                const key = toSelectorKey(candidate.path);
                const existing = targets.get(key);
                if (existing && existing.relativePath.length <= relativePath.length) {
                    continue;
                }

                targets.set(key, { absolutePath: candidate.path, relativePath, displayPath });
            }
        }

        // A real file or folder name can itself carry invisible or bidi characters, and the
        // confirmation must never show an identity it cannot render faithfully. Such an
        // entry is dropped from the registry rather than displayed altered, which would
        // break the one-to-one relationship between the prompt and the launch target.
        return [...targets.values()].filter(target =>
            !identityChangingCharacters.test(target.displayPath) &&
            target.displayPath.length <= maxConfirmationPathLength);
    }

    private async preflight(
        tool: string,
        rawAppHost: unknown,
        token: vscode.CancellationToken,
        requestedMode: AppHostLifecycleMode | undefined,
    ): Promise<PreflightResult> {
        const reject = (outcome: AppHostLifecycleOutcome, knownAppHosts?: readonly string[]): PreflightResult => ({
            rejected: true,
            result: createResult(tool, outcome, '', 'none', requestedMode, undefined, knownAppHosts),
        });

        // A disposed service means the extension is deactivating; treat queued work as
        // cancelled rather than starting processes that would outlive the host.
        if (this._disposed || token.isCancellationRequested) {
            return reject('cancelled');
        }

        // Untrusted workspaces can contain hostile project files, and starting an AppHost
        // executes them. Restricted Mode must therefore block the tool even if a
        // registration somehow survived a trust change.
        if (!vscode.workspace.isTrusted) {
            return reject('workspaceNotTrusted');
        }

        const resolution = await this.resolveTarget(rawAppHost, token);
        if (!resolution.resolved) {
            return reject(resolution.outcome, resolution.knownAppHosts);
        }

        return { rejected: false, target: resolution.target };
    }

    private findEditorSessions(appHostPath: string): AppHostLifecycleEditorSessions {
        return this._dependencies.launchService.getEditorRunSessions(appHostPath);
    }

    private hasEditorSession(appHostPath: string): boolean {
        const editorSessions = this._dependencies.launchService.getEditorRunSessions(appHostPath);
        return this._dependencies.launchService.isLaunching(appHostPath) ||
            editorSessions.sessions.length > 0 ||
            editorSessions.ambiguous;
    }

    private async isRunningOutsideEditor(appHostPath: string, token: vscode.CancellationToken): Promise<boolean> {
        const runningAppHosts = await this._dependencies.launchService.getRunningAppHosts(token);
        // An identity that cannot be proven distinct counts as running. Treating it as a
        // different AppHost would let `start` put a second process on the ports of the one
        // the CLI already reported.
        return runningAppHosts.some(runningAppHost =>
            this._dependencies.launchService.compareAppHostIdentity(runningAppHost.appHostPath, appHostPath) !== 'different');
    }

    /**
     * Controller probe for `stop`, which distinguishes "not running" from "could not tell".
     *
     * Collapsing a probe failure into `none` would make the tool report `notRunning` for an
     * AppHost that is running outside the editor, contradicting the contract the agent
     * relies on. The caller turns `unknown` into a `failed` outcome instead.
     */
    private async probeExternalControllerForStop(appHostPath: string, token: vscode.CancellationToken): Promise<AppHostLifecycleController> {
        try {
            return await this.isRunningOutsideEditor(appHostPath, token) ? 'external' : 'none';
        }
        catch (error) {
            if (isCommandCancellation(error)) {
                throw error;
            }

            extensionLogOutputChannel.warn(`Aspire language model tool ${aspireAppHostStopToolName} could not determine which process controls the AppHost: ${String(error)}`);
            return 'unknown';
        }
    }

    private createErrorResult(
        tool: string,
        error: unknown,
        relativePath: string,
        controller: AppHostLifecycleController,
        requestedMode: AppHostLifecycleMode | undefined,
        effectiveMode: AppHostLifecycleMode | undefined,
    ): AppHostLifecycleToolResult {
        if (isCommandCancellation(error)) {
            return createResult(tool, 'cancelled', relativePath, controller, requestedMode, effectiveMode);
        }

        if (error instanceof AppHostLifecycleLockTimeoutError) {
            return createResult(tool, 'busy', relativePath, controller, requestedMode, effectiveMode);
        }

        // Failure details stay in the extension log. They routinely contain absolute
        // paths, CLI stderr, and DCP/RPC connection details, none of which may cross
        // back into the model transcript.
        extensionLogOutputChannel.error(`Aspire language model tool ${tool} failed: ${String(error)}`);
        return createResult(tool, 'failed', relativePath, controller, requestedMode, effectiveMode);
    }

}

export class AppHostStartLanguageModelTool implements vscode.LanguageModelTool<AppHostStartToolInput> {
    constructor(private readonly _service: AppHostLifecycleToolService) {
    }

    // Preparation resolves the requested selector against the AppHost registry so the
    // confirmation shows the exact target `invoke` will act on. It performs discovery but
    // no lifecycle work, which is what the API requires of a preparation step.
    async prepareInvocation(options: vscode.LanguageModelToolInvocationPrepareOptions<AppHostStartToolInput>, token: vscode.CancellationToken): Promise<vscode.PreparedToolInvocation> {
        const displayPath = escapeMarkdown(await this._service.describeTarget(options.input?.appHostPath, token));
        const displayMode = describeRequestedMode(options.input?.mode);
        return {
            invocationMessage: appHostLifecycleStartInvocationMessage(displayPath),
            confirmationMessages: {
                title: appHostLifecycleStartConfirmationTitle,
                message: appHostLifecycleStartConfirmationMessage(displayPath, displayMode),
            },
        };
    }

    async invoke(options: vscode.LanguageModelToolInvocationOptions<AppHostStartToolInput>, token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._service.start(options.input, token));
    }
}

export class AppHostStopLanguageModelTool implements vscode.LanguageModelTool<AppHostStopToolInput> {
    constructor(private readonly _service: AppHostLifecycleToolService) {
    }

    async prepareInvocation(options: vscode.LanguageModelToolInvocationPrepareOptions<AppHostStopToolInput>, token: vscode.CancellationToken): Promise<vscode.PreparedToolInvocation> {
        const displayPath = escapeMarkdown(await this._service.describeTarget(options.input?.appHostPath, token));
        return {
            invocationMessage: appHostLifecycleStopInvocationMessage(displayPath),
            confirmationMessages: {
                title: appHostLifecycleStopConfirmationTitle,
                message: appHostLifecycleStopConfirmationMessage(displayPath),
            },
        };
    }

    async invoke(options: vscode.LanguageModelToolInvocationOptions<AppHostStopToolInput>, token: vscode.CancellationToken): Promise<vscode.LanguageModelToolResult> {
        return createToolResult(await this._service.stop(options.input, token));
    }
}

/**
 * Registers the AppHost lifecycle tools when the stable
 * {@link vscode.lm.registerTool} API exists.
 *
 * The API check keeps the extension loadable on VS Code builds that predate the
 * finalized language model tool API (`engines.vscode` allows older hosts). The
 * implementation is registered in Restricted Mode too because VS Code can retain the
 * contributed tool metadata there; invocation then returns `workspaceNotTrusted`
 * instead of failing with a missing implementation.
 */
export function registerAppHostLifecycleTools(service: AppHostLifecycleToolService): AppHostLifecycleToolRegistration {
    const registrations: vscode.Disposable[] = [];
    const startTool = new AppHostStartLanguageModelTool(service);
    const stopTool = new AppHostStopLanguageModelTool(service);
    // The preparable view exists for E2E automation, which only has raw JSON input. The
    // cast is safe because both tools validate every field of the input themselves and
    // treat anything unexpected as invalid rather than trusting the declared type.
    const tools = new Map<string, PreparableAppHostLifecycleTool>([
        [aspireAppHostStartToolName, { prepareInvocation: (options, token) => startTool.prepareInvocation({ input: options.input as unknown as AppHostStartToolInput }, token) }],
        [aspireAppHostStopToolName, { prepareInvocation: (options, token) => stopTool.prepareInvocation({ input: options.input as unknown as AppHostStopToolInput }, token) }],
    ]);
    const registerTools = () => {
        if (registrations.length > 0) {
            return;
        }

        registrations.push(
            vscode.lm.registerTool(aspireAppHostStartToolName, startTool),
            vscode.lm.registerTool(aspireAppHostStopToolName, stopTool));
        extensionLogOutputChannel.info('Registered Aspire AppHost lifecycle language model tools.');
    };

    if (typeof vscode.lm?.registerTool !== 'function') {
        extensionLogOutputChannel.info('Skipping Aspire AppHost lifecycle language model tools: the language model tool API is unavailable.');
    }
    else {
        registerTools();
    }

    return {
        get registered() {
            return registrations.length > 0;
        },
        tools,
        dispose() {
            registrations.forEach(registration => registration.dispose());
            registrations.length = 0;
        },
    };
}

function createToolResult(result: AppHostLifecycleToolResult): vscode.LanguageModelToolResult {
    return new vscode.LanguageModelToolResult([new vscode.LanguageModelTextPart(JSON.stringify(result))]);
}

function createResult(
    tool: string,
    outcome: AppHostLifecycleOutcome,
    appHostPath: string,
    controller: AppHostLifecycleController,
    requestedMode: AppHostLifecycleMode | undefined,
    effectiveMode: AppHostLifecycleMode | undefined,
    knownAppHosts?: readonly string[],
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

    return result;
}

function parseMode(value: unknown): AppHostLifecycleMode | undefined {
    return value === 'run' || value === 'debug' ? value : undefined;
}

function isValidStartInput(value: unknown): value is AppHostStartToolInput {
    return hasOnlyProperties(value, ['appHostPath', 'mode']) &&
        typeof value.appHostPath === 'string' &&
        parseMode(value.mode) !== undefined;
}

function isValidStopInput(value: unknown): value is AppHostStopToolInput {
    return hasOnlyProperties(value, ['appHostPath']) &&
        typeof value.appHostPath === 'string';
}

function hasOnlyProperties<T extends string>(value: unknown, properties: readonly T[]): value is Record<T, unknown> {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) {
        return false;
    }

    const actualProperties = Object.keys(value);
    return actualProperties.length === properties.length &&
        properties.every(property => Object.prototype.hasOwnProperty.call(value, property));
}

function describeRequestedMode(value: unknown): string {
    return parseMode(value) ?? appHostLifecycleUnspecifiedMode;
}

function getSessionMode(session: AppHostLifecycleEditorSession): AppHostLifecycleMode {
    return session.configuration?.noDebug === true ? 'run' : 'debug';
}

/**
 * Escapes the Markdown constructs that change how a path renders inline.
 *
 * The confirmation body renders as Markdown, so an unescaped `*`, `_`, `` ` ``, `[`, or
 * `<` in a real file name would show the user something other than the file the tool is
 * about to launch. Escaping keeps the rendered text one-to-one with the path instead of
 * deleting characters, which would break that relationship in the other direction.
 * Characters that are only meaningful at the start of a line (`.`, `-`, `{`, `}`) are
 * left alone: the path is always interpolated mid-sentence and they are extremely common
 * in real project paths.
 * See https://spec.commonmark.org/0.31.2/#backslash-escapes
 */
function escapeMarkdown(value: string): string {
    return value.replace(/[\\`*_[\]()<>#+~|!&]/g, character => `\\${character}`);
}

/**
 * Normalizes a selector or registry path into the key both sides are compared on.
 *
 * The comparison is deliberately narrow: separators are unified because the CLI reports
 * native paths while models routinely send POSIX ones, a leading `./` is dropped because
 * it is noise, and Windows comparisons are case-insensitive to match the filesystem.
 * Nothing else is normalized. `..` segments, for instance, are left alone precisely so
 * they can never match anything the registry enumerated.
 */
function toSelectorKey(value: string): string {
    const normalized = value.replace(/\\/g, '/').replace(/^\.\//, '');
    return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
}

/**
 * Renders the selectors a failed resolution can offer back to the model.
 *
 * The list is capped because a large monorepo can enumerate hundreds of AppHosts and the
 * result is spent from the model's context window.
 */
function describeKnownAppHosts(targets: readonly ResolvedAppHostTarget[]): readonly string[] {
    return targets.slice(0, maxReportedKnownAppHosts).map(target => target.displayPath);
}

/**
 * Path relative to `folderPath` with `/` separators, or `undefined` when `candidate`
 * is not inside the folder.
 */
function toContainedPosixRelativePath(folderPath: string, candidate: string): string | undefined {
    const relative = path.relative(folderPath, candidate);
    if (relative.length === 0 || relative.startsWith('..') || path.isAbsolute(relative)) {
        return undefined;
    }

    return relative.split(path.sep).join('/');
}
