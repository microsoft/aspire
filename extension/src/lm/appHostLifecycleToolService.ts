import * as path from 'path';
import * as vscode from 'vscode';

import { appHostLifecycleUnresolvedPath } from '../loc/strings';
import { canonicalizeAppHostPath } from '../utils/appHostIdentity';
import { extensionLogOutputChannel } from '../utils/logging';
import { isCommandCancellation } from '../utils/telemetry';
import { AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, type AppHostStopResult } from '../services/AppHostLaunchService';
import {
    aspireAppHostStartToolName,
    aspireAppHostStopToolName,
    createResult,
    isValidStartInput,
    isValidStopInput,
    type AppHostLifecycleController,
    type AppHostLifecycleEditorSession,
    type AppHostLifecycleEditorSessions,
    type AppHostLifecycleMode,
    type AppHostLifecycleOutcome,
    type AppHostLifecycleToolDependencies,
    type AppHostLifecycleToolResult,
    type AppHostStartToolInput,
    type AppHostStopToolInput,
} from './appHostLifecycleToolContracts';

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
 * registry the editor already maintains and enforces workspace trust. Stop requests then
 * delegate to the same lifecycle service used by the Aspire tree.
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
     * the target the user approves is the target that gets executed. Input that does not
     * resolve is described with a fixed placeholder rather than echoed, because such a
     * call is always rejected anyway and echoing it would hand the model free-form prose
     * inside the trusted prompt that gates "Always allow".
     */
    async describeTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<string> {
        // VS Code can keep the implementation reachable in Restricted Mode and call
        // `prepareInvocation` before `invoke` gets a chance to reject the tool call. Do
        // not run AppHost discovery there: it shells out to `aspire ls`, which crosses
        // the same trust boundary as the eventual start/stop operation.
        if (!vscode.workspace.isTrusted) {
            return appHostLifecycleUnresolvedPath;
        }

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
            return await this._dependencies.launchService.runWithAppHostLifecycleLock(preflight.target.absolutePath, token, async lockToken => {
                const recheck = await this.preflight(aspireAppHostStopToolName, input.appHostPath, lockToken, undefined);
                if (recheck.rejected) {
                    return recheck.result;
                }

                const result = await this._dependencies.launchService.stopAppHostFromLifecycleOwner(recheck.target.absolutePath, lockToken);
                return this.createStopResult(recheck.target.relativePath, result);
            });
        }
        catch (error) {
            const stopError = error instanceof AppHostStopError || error instanceof AppHostStopCancellationError
                ? error
                : undefined;
            const controller = stopError?.controller ?? 'unknown';
            const effectiveMode = stopError?.controller === 'editor'
                ? stopError.noDebug ? 'run' : 'debug'
                : undefined;
            return this.createErrorResult(aspireAppHostStopToolName, error, preflight.target.relativePath, controller, undefined, effectiveMode);
        }
    }

    private createStopResult(relativePath: string, result: AppHostStopResult): AppHostLifecycleToolResult {
        const effectiveMode = result.outcome === 'stopped' && result.controller === 'editor'
            ? result.noDebug ? 'run' : 'debug'
            : undefined;
        return createResult(
            aspireAppHostStopToolName,
            result.outcome,
            relativePath,
            result.controller,
            undefined,
            effectiveMode);
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
        const displayMatches = knownAppHosts.filter(candidate => toSelectorKey(candidate.displayPath) === requestedKey);
        if ((vscode.workspace.workspaceFolders?.length ?? 0) > 1) {
            // A bare relative selector is not stable in a multi-root workspace: a confirmation
            // could name the only current match under root A, then a later invocation could
            // re-resolve the same text under root B. Require the same folder-qualified identity
            // the confirmation displays so each invocation is independently bound to one root.
            if (displayMatches.length === 1) {
                return { resolved: true, target: displayMatches[0] };
            }

            if (displayMatches.length > 1) {
                return { resolved: false, outcome: 'ambiguousAppHost', knownAppHosts: describeKnownAppHosts(displayMatches) };
            }

            const relativeMatches = knownAppHosts.filter(candidate => toSelectorKey(candidate.relativePath) === requestedKey);
            if (relativeMatches.length > 0) {
                return { resolved: false, outcome: 'ambiguousAppHost', knownAppHosts: describeKnownAppHosts(relativeMatches) };
            }

            return { resolved: false, outcome: 'unknownAppHost', knownAppHosts: describeKnownAppHosts(knownAppHosts) };
        }

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

function getSessionMode(session: AppHostLifecycleEditorSession): AppHostLifecycleMode {
    return session.configuration?.noDebug === true ? 'run' : 'debug';
}

/**
 * Normalizes a selector or registry path into the key both sides are compared on.
 *
 * The comparison is deliberately narrow: a leading `./` is dropped because it is noise,
 * and Windows separators and casing are normalized to match that filesystem. On POSIX a
 * backslash is a valid filename character, so treating it as a separator would alias two
 * different registry entries. Nothing else is normalized. `..` segments, for instance,
 * are left alone precisely so they can never match anything the registry enumerated.
 */
function toSelectorKey(value: string): string {
    if (process.platform === 'win32') {
        return value.replace(/\\/g, '/').replace(/^\.\//, '').toLowerCase();
    }

    return value.replace(/^\.\//, '');
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
