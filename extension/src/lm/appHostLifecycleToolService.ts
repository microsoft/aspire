import * as vscode from 'vscode';

import { appHostLifecycleUnresolvedPath } from '../loc/strings';
import { canonicalizeAppHostPath } from '../utils/appHostIdentity';
import { isLinkedGitWorktree } from '../utils/gitWorktree';
import { extensionLogOutputChannel } from '../utils/logging';
import { isCommandCancellation } from '../utils/telemetry';
import { AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, AppHostStopError, type AppHostStopResult } from '../services/AppHostLaunchService';
import type {
    AppHostTarget,
    AppHostTargetResolution,
    AppHostTargetResolver,
} from './appHostTargetResolverContracts';
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

type PreflightResult =
    | { rejected: true; result: AppHostLifecycleToolResult }
    | { rejected: false; target: AppHostTarget };

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

    async describeStartTarget(input: AppHostStartToolInput | undefined, token: vscode.CancellationToken): Promise<{ displayPath: string; isolated: boolean }> {
        if (!vscode.workspace.isTrusted) {
            return { displayPath: appHostLifecycleUnresolvedPath, isolated: false };
        }

        const resolution = await this.resolveTarget(input?.appHostPath, token);
        if (!resolution.resolved) {
            return { displayPath: appHostLifecycleUnresolvedPath, isolated: false };
        }

        // Confirmation reports the isolation the launch will actually request. Lifecycle-tool
        // launches go through `launchFromLifecycleOwner`, which applies the
        // `linked-worktree-default` policy, so an omitted `isolated` becomes true in a linked
        // worktree. That half of the inference is a synchronous filesystem check, so it can be
        // shown here and must be: this dialog gates "Always allow", and consenting to a
        // non-isolated launch that then runs isolated is a consent mismatch. Only the CLI
        // *capability* half stays deferred - it spawns the CLI, and degradation for older CLIs
        // is negotiated at launch.
        //
        // `prepareInvocation` receives unvalidated model input, so only a real boolean counts as
        // an explicit choice; anything else is rejected by `isValidStartInput` at invoke time.
        const explicitIsolation = typeof input?.isolated === 'boolean' ? input.isolated : undefined;
        const isolated = explicitIsolation ?? isLinkedGitWorktree(resolution.target.absolutePath);
        return { displayPath: resolution.target.displayPath, isolated };
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
                // These outcomes observe an existing launch rather than creating one. The
                // tool therefore knows only that *some* process owns the AppHost, not
                // which effective isolation its launcher negotiated, so `isolated` stays
                // absent on each of them.
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
                    const launchedIsolation = await this._dependencies.launchService.launchFromLifecycleOwner(
                        current.absolutePath,
                        'run',
                        requestedMode === 'run',
                        input.isolated,
                        lockToken);
                    return createResult(aspireAppHostStartToolName, 'started', current.relativePath, 'editor', requestedMode, requestedMode, undefined, launchedIsolation?.effective);
                }
                catch (error) {
                    // The launch path clears its own reservation once it owns it, but a
                    // failure before that point (a disposed service, for example) would
                    // otherwise leave this AppHost reported as launching forever.
                    this._dependencies.launchService.clearLaunching(current.absolutePath);
                    return this.createErrorResult(aspireAppHostStartToolName, error, current.relativePath, 'editor', requestedMode, undefined);
                }
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

    private async resolveTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<AppHostTargetResolution> {
        return await this._dependencies.targetResolver.resolveTarget(rawAppHost, token);
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
