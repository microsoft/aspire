import * as vscode from 'vscode';

import { appHostLifecycleUnresolvedPath } from '../loc/strings';
import { isLinkedGitWorktree } from '../utils/gitWorktree';
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
import { type AppHostTargetIdentity, type ResolvedAppHostTarget, SafeAppHostTargetResolver, toAppHostLaunchTarget } from './safeAppHostTargetResolver';

type AppHostTargetResolution =
    | { resolved: true; target: ResolvedAppHostTarget }
    | { resolved: false; outcome: AppHostLifecycleOutcome; knownAppHosts?: readonly string[] };

type PreflightResult =
    | { rejected: true; result: AppHostLifecycleToolResult }
    | { rejected: false; target: ResolvedAppHostTarget };

interface PreparedLifecycleAction {
    readonly tool: typeof aspireAppHostStartToolName | typeof aspireAppHostStopToolName;
    readonly inputKey: string;
    readonly identity: AppHostTargetIdentity;
    readonly isolated?: boolean;
    readonly expiresAt: number;
}

const preparedActionLimit = 64;
const preparedActionLifetimeMs = 5 * 60 * 1000;

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
    private readonly _targetResolver: SafeAppHostTargetResolver;
    private readonly _preparedActions: PreparedLifecycleAction[] = [];
    private readonly _activePreparedActionCounts = new Map<string, number>();
    private _disposed = false;

    constructor(
        dependencies: AppHostLifecycleToolDependencies,
        targetResolver: SafeAppHostTargetResolver = new SafeAppHostTargetResolver(dependencies.discoveryService)) {
        this._dependencies = dependencies;
        this._targetResolver = targetResolver;
    }

    dispose(): void {
        this._disposed = true;
        this._preparedActions.length = 0;
        this._activePreparedActionCounts.clear();
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
        return await this.prepareStopTarget(
            typeof rawAppHost === 'string' ? { appHostPath: rawAppHost } : undefined,
            token);
    }

    async prepareStopTarget(input: AppHostStopToolInput | undefined, token: vscode.CancellationToken): Promise<string> {
        // VS Code can keep the implementation reachable in Restricted Mode and call
        // `prepareInvocation` before `invoke` gets a chance to reject the tool call. Do
        // not run AppHost discovery there: it shells out to `aspire ls`, which crosses
        // the same trust boundary as the eventual start/stop operation.
        if (!vscode.workspace.isTrusted) {
            return appHostLifecycleUnresolvedPath;
        }

        if (!isValidStopInput(input)) {
            return appHostLifecycleUnresolvedPath;
        }

        const resolution = await this._targetResolver.resolveTarget(input.appHostPath, token);
        if (!resolution.resolved) {
            return appHostLifecycleUnresolvedPath;
        }

        this.rememberPreparedAction({
            tool: aspireAppHostStopToolName,
            inputKey: getStopInputKey(input),
            identity: resolution.target.identity,
            expiresAt: Date.now() + preparedActionLifetimeMs,
        });
        return resolution.target.displayPath;
    }

    async describeStartTarget(input: AppHostStartToolInput | undefined, token: vscode.CancellationToken): Promise<{ displayPath: string; isolated: boolean }> {
        if (!vscode.workspace.isTrusted) {
            return { displayPath: appHostLifecycleUnresolvedPath, isolated: false };
        }

        if (!isValidStartInput(input)) {
            return { displayPath: appHostLifecycleUnresolvedPath, isolated: false };
        }

        const resolution = await this.resolveTarget(input.appHostPath, token);
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
        const isolated = explicitIsolation ?? isLinkedGitWorktree(resolution.target.canonicalPath);
        this.rememberPreparedAction({
            tool: aspireAppHostStartToolName,
            inputKey: getStartInputKey(input),
            identity: resolution.target.identity,
            isolated,
            expiresAt: Date.now() + preparedActionLifetimeMs,
        });
        return { displayPath: resolution.target.displayPath, isolated };
    }

    async startConfirmed(input: AppHostStartToolInput, token: vscode.CancellationToken): Promise<AppHostLifecycleToolResult> {
        if (!isValidStartInput(input) || this._disposed || token.isCancellationRequested || !vscode.workspace.isTrusted) {
            return await this.start(input, token);
        }

        const preparedAction = this.consumePreparedAction(aspireAppHostStartToolName, getStartInputKey(input));
        if (preparedAction === undefined) {
            return this.createUnconfirmedInvocationResult(aspireAppHostStartToolName, input.mode);
        }

        return await this.runPreparedAction(
            aspireAppHostStartToolName,
            getStartInputKey(input),
            () => this.startCore(input, token, preparedAction));
    }

    async start(input: AppHostStartToolInput, token: vscode.CancellationToken): Promise<AppHostLifecycleToolResult> {
        return await this.startCore(input, token);
    }

    private async startCore(
        input: AppHostStartToolInput,
        token: vscode.CancellationToken,
        preparedAction?: PreparedLifecycleAction): Promise<AppHostLifecycleToolResult> {
        if (!isValidStartInput(input)) {
            return createResult(aspireAppHostStartToolName, 'invalidInput', '', 'none', undefined, undefined);
        }

        const requestedMode = input.mode;
        const preflight = await this.preflight(aspireAppHostStartToolName, input?.appHostPath, token, requestedMode);
        if (preflight.rejected) {
            return preflight.result;
        }
        if (preparedAction !== undefined &&
            (preparedAction.identity !== preflight.target.identity ||
                preparedAction.isolated !== (input.isolated ?? isLinkedGitWorktree(preflight.target.canonicalPath)))) {
            return this.createUnconfirmedInvocationResult(aspireAppHostStartToolName, requestedMode);
        }

        // Every decision below is addressed to `canonicalPath`, the physical AppHost the selector
        // resolved to, rather than to the selector itself. Ownership probes, the lifecycle lock,
        // the launching claim, and the launch all cross awaits, and a selector is only a name: an
        // alias repointed while one of those steps runs would move the operation onto a different
        // AppHost while the result still reports the relative path that was confirmed. The
        // selector remains what the result displays and what re-resolution validates.
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
            if (!this.hasEditorSession(preflight.target.canonicalPath) &&
                await this.isRunningOutsideEditor(preflight.target.canonicalPath, token)) {
                // Launching again would start a second AppHost against the same project.
                // Report it instead so the agent can decide, and never adopt or kill a
                // process this extension does not own.
                return createResult(aspireAppHostStartToolName, 'alreadyRunning', preflight.target.relativePath, 'external', requestedMode, undefined);
            }

            return await this._dependencies.launchService.runWithAppHostLifecycleLock(preflight.target.canonicalPath, token, async lockToken => {
                // Re-resolve after the confirmation and after waiting on the shared lock:
                // the file can be deleted or replaced, and an editor command may already
                // have launched this AppHost while this call was queued.
                const recheck = await this.preflight(aspireAppHostStartToolName, input.appHostPath, lockToken, requestedMode);
                if (recheck.rejected) {
                    return recheck.result;
                }

                if (!this.isSameConfirmedAppHost(aspireAppHostStartToolName, preflight.target, recheck.target)) {
                    return createResult(aspireAppHostStartToolName, 'failed', preflight.target.relativePath, 'none', requestedMode, undefined);
                }

                const current = recheck.target;
                const owned = this.findEditorSessions(current.canonicalPath);
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

                if (this._dependencies.launchService.isLaunching(current.canonicalPath) || owned.sessions.length > 0) {
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
                if (await this.isRunningOutsideEditor(current.canonicalPath, lockToken)) {
                    return createResult(aspireAppHostStartToolName, 'alreadyRunning', current.relativePath, 'external', requestedMode, undefined);
                }

                // An omitted isolation value follows linked-worktree state. That filesystem state
                // can change while the external-owner probes or lifecycle lock are awaited, so
                // compare it again at the final launch boundary rather than relying only on the
                // preflight comparison made before those awaits.
                if (preparedAction !== undefined &&
                    preparedAction.isolated !== (input.isolated ?? isLinkedGitWorktree(current.canonicalPath))) {
                    return this.createUnconfirmedInvocationResult(aspireAppHostStartToolName, requestedMode);
                }

                // Claim the launching slot in one synchronous step. The lifecycle lock only
                // serializes callers that take it, and `launch.json`/F5 reaches
                // `startDebugging` without it, so this claim - not the checks above - is
                // what makes "no second AppHost" hold against a concurrent editor launch.
                if (!this._dependencies.launchService.tryReserveLaunch(current.canonicalPath)) {
                    return createResult(aspireAppHostStartToolName, 'alreadyStarting', current.relativePath, 'editor', requestedMode, undefined);
                }

                try {
                    // `noDebug` is the only lever the tool exposes; the Aspire command is pinned
                    // to `run` so an agent can never reach deploy/publish/do through this surface.
                    //
                    // The whole bound target travels into the launch: the physical AppHost the
                    // launch runs, the selector it is validated against and scoped by, and the
                    // identity that ties the two together. Passing only the physical path would
                    // drop the selector, and the launch's own pre-start freshness check would
                    // then compare that path with itself.
                    const launchedIsolation = await this._dependencies.launchService.launchFromLifecycleOwner(
                        toAppHostLaunchTarget(current),
                        'run',
                        requestedMode === 'run',
                        input.isolated,
                        lockToken,
                        preparedAction !== undefined && input.isolated === undefined
                            ? preparedAction.isolated
                            : undefined);
                    return createResult(aspireAppHostStartToolName, 'started', current.relativePath, 'editor', requestedMode, requestedMode, undefined, launchedIsolation?.effective);
                }
                catch (error) {
                    // The launch path clears its own reservation once it owns it, but a
                    // failure before that point (a disposed service, for example) would
                    // otherwise leave this AppHost reported as launching forever.
                    this._dependencies.launchService.clearLaunching(current.canonicalPath);
                    return this.createErrorResult(aspireAppHostStartToolName, error, current.relativePath, 'editor', requestedMode, undefined);
                }
            });
        }
        catch (error) {
            return this.createErrorResult(aspireAppHostStartToolName, error, preflight.target.relativePath, 'editor', requestedMode, undefined);
        }
    }

    async stopConfirmed(input: AppHostStopToolInput, token: vscode.CancellationToken): Promise<AppHostLifecycleToolResult> {
        if (!isValidStopInput(input) || this._disposed || token.isCancellationRequested || !vscode.workspace.isTrusted) {
            return await this.stop(input, token);
        }

        const preparedAction = this.consumePreparedAction(aspireAppHostStopToolName, getStopInputKey(input));
        if (preparedAction === undefined) {
            return this.createUnconfirmedInvocationResult(aspireAppHostStopToolName, undefined);
        }

        return await this.runPreparedAction(
            aspireAppHostStopToolName,
            getStopInputKey(input),
            () => this.stopCore(input, token, preparedAction));
    }

    async stop(input: AppHostStopToolInput, token: vscode.CancellationToken): Promise<AppHostLifecycleToolResult> {
        return await this.stopCore(input, token);
    }

    private async stopCore(
        input: AppHostStopToolInput,
        token: vscode.CancellationToken,
        preparedAction?: PreparedLifecycleAction): Promise<AppHostLifecycleToolResult> {
        if (!isValidStopInput(input)) {
            return createResult(aspireAppHostStopToolName, 'invalidInput', '', 'none', undefined, undefined);
        }

        const preflight = await this.preflight(aspireAppHostStopToolName, input?.appHostPath, token, undefined);
        if (preflight.rejected) {
            return preflight.result;
        }
        if (preparedAction !== undefined && preparedAction.identity !== preflight.target.identity) {
            return this.createUnconfirmedInvocationResult(aspireAppHostStopToolName, undefined);
        }

        try {
            return await this._dependencies.launchService.runWithAppHostLifecycleLock(preflight.target.canonicalPath, token, async lockToken => {
                const recheck = await this.preflight(aspireAppHostStopToolName, input.appHostPath, lockToken, undefined);
                if (recheck.rejected) {
                    return recheck.result;
                }

                if (!this.isSameConfirmedAppHost(aspireAppHostStopToolName, preflight.target, recheck.target)) {
                    return createResult(aspireAppHostStopToolName, 'failed', preflight.target.relativePath, 'none', undefined, undefined);
                }

                const result = await this._dependencies.launchService.stopAppHostFromLifecycleOwner(
                    toAppHostLaunchTarget(recheck.target),
                    lockToken);
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

    private rememberPreparedAction(action: PreparedLifecycleAction): void {
        this.prunePreparedActions();
        if (this._preparedActions.length >= preparedActionLimit) {
            this._preparedActions.splice(0, this._preparedActions.length - preparedActionLimit + 1);
        }

        this._preparedActions.push(action);
    }

    private consumePreparedAction(
        tool: PreparedLifecycleAction['tool'],
        inputKey: string): PreparedLifecycleAction | undefined {
        this.prunePreparedActions();
        for (let index = this._preparedActions.length - 1; index >= 0; index--) {
            const action = this._preparedActions[index];
            if (action.tool === tool && action.inputKey === inputKey) {
                this._preparedActions.splice(index, 1);
                return action;
            }
        }

        return undefined;
    }

    private async runPreparedAction<T>(
        tool: PreparedLifecycleAction['tool'],
        inputKey: string,
        action: () => Promise<T>): Promise<T> {
        const key = `${tool}\0${inputKey}`;
        this._activePreparedActionCounts.set(key, (this._activePreparedActionCounts.get(key) ?? 0) + 1);
        try {
            return await action();
        }
        finally {
            const activeCount = this._activePreparedActionCounts.get(key) ?? 0;
            if (activeCount > 1) {
                this._activePreparedActionCounts.set(key, activeCount - 1);
            }
            else {
                this._activePreparedActionCounts.delete(key);
                this.removePreparedActions(tool, inputKey);
            }
        }
    }

    private removePreparedActions(tool: PreparedLifecycleAction['tool'], inputKey: string): void {
        // The VS Code API does not provide a preparation token to correlate with invocation.
        // Concurrent identical confirmations may each consume one record while the first action is
        // still active. Once the last one settles, remove every abandoned duplicate so a later
        // unconfirmed invocation cannot replay an earlier preparation.
        for (let index = this._preparedActions.length - 1; index >= 0; index--) {
            const action = this._preparedActions[index];
            if (action.tool === tool && action.inputKey === inputKey) {
                this._preparedActions.splice(index, 1);
            }
        }
    }

    private prunePreparedActions(): void {
        const now = Date.now();
        for (let index = this._preparedActions.length - 1; index >= 0; index--) {
            if (this._preparedActions[index].expiresAt <= now) {
                this._preparedActions.splice(index, 1);
            }
        }
    }

    private createUnconfirmedInvocationResult(
        tool: PreparedLifecycleAction['tool'],
        requestedMode: AppHostLifecycleMode | undefined): AppHostLifecycleToolResult {
        extensionLogOutputChannel.warn(`Aspire language model tool ${tool} refused an invocation that did not match a current prepared action.`);
        return createResult(tool, 'failed', '', 'none', requestedMode, undefined);
    }

    /**
     * Reports whether the AppHost this operation now owns is the one the call resolved - and, for
     * a confirmed tool call, the one the user approved.
     *
     * Resolution happens before the lifecycle lock and again after it, and the selector between
     * them is mutable. If it now names a different AppHost, continuing would act on something
     * nobody chose while the result still reported the confirmed workspace-relative path, so the
     * operation fails closed instead. Re-resolving is what makes this detectable at all; the
     * identity is the only value that survives a name being repointed and back.
     */
    private isSameConfirmedAppHost(tool: string, resolved: ResolvedAppHostTarget, owned: ResolvedAppHostTarget): boolean {
        if (resolved.identity === owned.identity) {
            return true;
        }

        // Bounded on purpose: the paths involved are exactly what must not reach a model-visible
        // channel, and the tool name is enough to locate this in the output log.
        extensionLogOutputChannel.warn(`Aspire language model tool ${tool} refused an AppHost that changed between confirmation and ownership.`);
        return false;
    }

    private async resolveTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<AppHostTargetResolution> {
        const resolution = await this._targetResolver.resolveTarget(rawAppHost, token);
        if (resolution.resolved) {
            return resolution;
        }

        return {
            resolved: false,
            outcome:
                resolution.outcome === 'appHostNotFound' ? 'unknownAppHost'
                    : resolution.outcome === 'canceled' ? 'cancelled'
                        : resolution.outcome === 'error' ? 'discoveryFailed'
                            : resolution.outcome,
            knownAppHosts: resolution.knownAppHosts,
        };
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

        // Model-triggered failures routinely contain absolute paths, CLI output, and
        // connection details. Keep this diagnostic bounded to the registered tool name.
        extensionLogOutputChannel.error(`Aspire language model tool ${tool} failed.`);
        return createResult(tool, 'failed', relativePath, controller, requestedMode, effectiveMode);
    }

}

function getSessionMode(session: AppHostLifecycleEditorSession): AppHostLifecycleMode {
    return session.configuration?.noDebug === true ? 'run' : 'debug';
}

function getStartInputKey(input: AppHostStartToolInput): string {
    return JSON.stringify([input.appHostPath, input.mode, input.isolated ?? null]);
}

function getStopInputKey(input: AppHostStopToolInput): string {
    return JSON.stringify([input.appHostPath]);
}
