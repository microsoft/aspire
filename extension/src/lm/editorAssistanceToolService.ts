import * as vscode from 'vscode';

import { resolveResourceNameMatches, type ResourceJson } from '../data/appHostCliContracts';
import { type HotReloadDiagnostics } from '../debugger/hotReload';
import { ResourceState, ResourceType } from '../editor/resourceConstants';
import { appHostLifecycleUnresolvedPath } from '../loc/strings';
import { type EditorResourceSessionSnapshot } from '../services/appHostLaunchContracts';
import { createAppHostOperationTarget } from '../utils/appHostOperationTarget';
import { extensionLogOutputChannel } from '../utils/logging';
import { isSamePath } from '../utils/paths/comparison';
import { isCommandCancellation } from '../utils/telemetry';
import {
    aspireDebugSessionStatusToolName,
    aspireExplainLaunchFailureToolName,
    aspireHotReloadStatusToolName,
    aspireListDebugSessionsToolName,
    aspireOpenDashboardToolName,
    aspireOpenOutputToolName,
    isValidAppHostPathOnlyInput,
    isValidEmptyObjectInput,
    isValidDebugSessionStatusInput,
    isValidHotReloadStatusInput,
    type DebugSessionStatusFailureResult,
    type DebugSessionStatusResourceFailureResult,
    type DebugSessionStatusResult,
    type DebugSessionStatusToolResult,
    type EditorAssistanceController,
    type EditorAssistanceMode,
    type EditorAssistanceRecommendedAction,
    type EditorAssistanceResource,
    type EditorAssistanceResourceState,
    type EditorAssistanceToolDependencies,
    type ExplainLaunchFailureFailureResult,
    type ExplainLaunchFailureFoundResult,
    type ExplainLaunchFailureToolResult,
    type HotReloadEvidence,
    type HotReloadStatusToolResult,
    type HotReloadStatusUnavailableResult,
    type ListDebugSessionsToolResult,
    type OpenDashboardFailureResult,
    type OpenDashboardToolResult,
    type OpenOutputFailureResult,
    type OpenOutputToolResult,
} from './editorAssistanceToolContracts';
import {
    AmbiguousAppHostOwnershipError,
    type EditorAppHostSummary,
} from './editorStateSnapshotService';
import {
    StaleAppHostTargetError,
    type AppHostTargetIdentity,
    type ResolvedAppHostTarget,
    type SafeAppHostTargetResolution,
} from './safeAppHostTargetResolver';

type ResolvedPreflight<T> =
    | { readonly resolved: true; readonly target: ResolvedAppHostTarget; readonly input: T }
    | { readonly resolved: false; readonly outcome: 'appHostNotFound' | 'ambiguousAppHost' | 'workspaceNotTrusted' | 'invalidInput' | 'canceled' | 'error' };

interface ResourceSessionMatch {
    readonly session: EditorResourceSessionSnapshot;
    readonly matchingResources: readonly ResourceJson[];
}

/** One resource a Hot Reload question could be about, with the sessions that claim it. */
interface HotReloadCandidate {
    readonly appHost: string;
    /**
     * Who controls the AppHost this resource belongs to. Carried from the AppHost summary so
     * this tool cannot disagree with `aspire_debug_session_status` about the same AppHost.
     */
    readonly controller: EditorAssistanceController;
    readonly resource: ResourceJson;
    readonly matches: readonly ResourceSessionMatch[];
}

type HotReloadUnavailable = {
    readonly resolved: false;
    readonly outcome: HotReloadStatusUnavailableResult['outcome'];
};

type HotReloadCandidateCollection =
    | { readonly resolved: true; readonly candidates: readonly HotReloadCandidate[] }
    | HotReloadUnavailable;

/** One AppHost a Hot Reload question may be answered from, with who controls it. */
interface HotReloadAppHostScope {
    readonly target: ResolvedAppHostTarget;
    readonly controller: EditorAssistanceController;
}

type HotReloadScopeResolution =
    | {
        readonly resolved: true;
        readonly scopes: readonly HotReloadAppHostScope[];
        /**
         * Every target the scope decision was made over, including AppHosts that are in scope
         * for freshness but publish nothing. Answers are about all of them, so all of them are
         * revalidated before an answer is published.
         */
        readonly observedTargets: readonly ResolvedAppHostTarget[];
    }
    | HotReloadUnavailable;

/**
 * Provides model-safe editor assistance for AppHost state, diagnostics, and editor UI handoffs.
 *
 * The service resolves every selector through {@link SafeAppHostTargetResolver}.
 * Resource data and editor session snapshots are used only for exact internal
 * correlation; results are rebuilt from finite fields so paths, debug
 * configurations, resource properties, process identifiers, and raw errors
 * cannot cross into the model transcript.
 */
export class EditorAssistanceToolService {
    constructor(private readonly _dependencies: EditorAssistanceToolDependencies) {
    }

    /**
     * Resolves the display path used in the Dashboard tool's progress message.
     *
     * This is presentation only. The Dashboard handoff does not confirm, so nothing is bound to
     * the target resolved here; `openDashboard` resolves the target again through `preflight`.
     */
    async prepareDashboardTargetDisplayPath(
        rawAppHost: unknown,
        token: vscode.CancellationToken): Promise<string> {
        if (!vscode.workspace.isTrusted) {
            return appHostLifecycleUnresolvedPath;
        }

        const resolution = await this._dependencies.targetResolver.resolveTarget(rawAppHost, token);
        return resolution.resolved ? resolution.target.displayPath : appHostLifecycleUnresolvedPath;
    }

    async openDashboard(
        input: unknown,
        token: vscode.CancellationToken): Promise<OpenDashboardToolResult> {
        const preflight = await this.preflight(
            input,
            token,
            isValidAppHostPathOnlyInput,
            aspireOpenDashboardToolName);
        if (!preflight.resolved) {
            return createOpenDashboardFailure(preflight.outcome);
        }

        try {
            const result = await this._dependencies.uiHandoffService.openDashboard(preflight.target, token);
            if (result.outcome === 'opened') {
                return {
                    success: true,
                    tool: aspireOpenDashboardToolName,
                    outcome: 'opened',
                    presentation: result.presentation,
                };
            }

            return createOpenDashboardFailure(result.outcome);
        }
        catch (error) {
            if (isCommandCancellation(error) || token.isCancellationRequested) {
                return createOpenDashboardFailure('canceled');
            }

            // The handoff layer deliberately withholds its URL and raw browser error.
            extensionLogOutputChannel.error(`Aspire language model tool ${aspireOpenDashboardToolName} failed.`);
            return createOpenDashboardFailure('error');
        }
    }

    async openOutput(input: unknown, token: vscode.CancellationToken): Promise<OpenOutputToolResult> {
        const rejected = validateEmptyObjectInvocation(input, token);
        if (rejected) {
            return createOpenOutputFailure(rejected);
        }

        try {
            const outcome = await this._dependencies.uiHandoffService.openOutput(token);
            return outcome === 'opened'
                ? {
                    success: true,
                    tool: aspireOpenOutputToolName,
                    outcome: 'opened',
                }
                : createOpenOutputFailure('error');
        }
        catch (error) {
            return createOpenOutputFailure(
                isCommandCancellation(error) || token.isCancellationRequested
                    ? 'canceled'
                    : 'error');
        }
    }

    async listDebugSessions(input: unknown, token: vscode.CancellationToken): Promise<ListDebugSessionsToolResult> {
        const rejected = validateEmptyObjectInvocation(input, token);
        if (rejected) {
            return createListDebugSessionsFailure(rejected);
        }

        try {
            const snapshot = await this._dependencies.snapshotService.createActiveSessionSnapshot(token);
            throwIfCanceled(token);
            const sessions = snapshot.appHosts.map(entry => entry.summary);
            // Idle AppHosts are included in the final freshness barrier even though they publish
            // no summary: they are why this list is "every active session in this window", so one
            // of them changing underneath the snapshot leaves that claim unestablished.
            this.throwIfTargetsStale(...snapshot.observedTargets);
            return {
                success: true,
                tool: aspireListDebugSessionsToolName,
                outcome: snapshot.appHosts.length > 0 ? 'sessionsFound' : 'noSessions',
                sessions,
                ...(snapshot.truncated ? { truncated: true } : {}),
            };
        }
        catch (error) {
            if (isCommandCancellation(error) || token.isCancellationRequested) {
                return createListDebugSessionsFailure('canceled');
            }
            if (error instanceof AmbiguousAppHostOwnershipError) {
                // One undecidable AppHost makes the whole list undecidable: it can neither be
                // listed as running nor left out as idle, and silently dropping it would make
                // the remaining entries look like the complete set of active sessions.
                return createListDebugSessionsFailure('ambiguousAppHost');
            }

            extensionLogOutputChannel.error(`Aspire language model tool ${aspireListDebugSessionsToolName} failed.`);
            return createListDebugSessionsFailure('error');
        }
    }

    async getDebugSessionStatus(input: unknown, token: vscode.CancellationToken): Promise<DebugSessionStatusToolResult> {
        const preflight = await this.preflight(
            input,
            token,
            isValidDebugSessionStatusInput,
            aspireDebugSessionStatusToolName);
        if (!preflight.resolved) {
            return createStatusFailure(preflight.outcome);
        }

        try {
            const result = await this.createDebugSessionStatusResult(
                preflight.target,
                preflight.input.resourceName,
                token);
            // Every read above is asynchronous, so the entry the selector named can be replaced
            // while they run. Revalidating once here keeps a retargeted AppHost from having its
            // replacement's state published under the original identity.
            this.throwIfTargetsStale(preflight.target);
            return result;
        }
        catch (error) {
            return this.createStatusError(error);
        }
    }

    private async createDebugSessionStatusResult(
        target: ResolvedAppHostTarget,
        requestedResourceName: string | undefined,
        token: vscode.CancellationToken): Promise<DebugSessionStatusToolResult> {
        const appHostSummary = await this._dependencies.snapshotService.getAppHostSummary(target, token);
        if (requestedResourceName === undefined) {
            return createAppHostStatusResult(appHostSummary);
        }

        const resourceName = requestedResourceName;
        if (appHostSummary.state === 'notDebugging') {
            // Nothing is running, so there is no resource model to describe and no runtime
            // state to report. Reading the streamed cache here could only answer from
            // whatever a previous run happened to leave behind.
            return createResourceFailure(
                'resourceNotFound',
                target.displayPath,
                resourceName,
                appHostSummary.controller);
        }

        // Status reports the AppHost's current runtime state, so it reads authoritatively and
        // returns with whatever exists now. Following the describe stream instead would make a
        // window with no open stream wait out a fixed window before falling back to this same
        // read, which is a slower path to the same answer rather than a more accurate one.
        //
        // The read is addressed to the AppHost the selector resolved to, not to the selector:
        // `aspire describe` follows the path it is handed when it runs, so an alias could be
        // repointed for the duration of the read and repointed back before the freshness check,
        // which would publish another AppHost's resources under this one's identity.
        const resources: readonly ResourceJson[] = await this._dependencies.resourceRepository.fetchAppHostResourcesOnce(
            createAppHostOperationTarget(target.canonicalPath, target.absolutePath),
            token);
        throwIfCanceled(token);

        const matches = resolveResourceNameMatches(resources, resourceName);
        if (matches.length === 0) {
            return createResourceFailure(
                'resourceNotFound',
                target.displayPath,
                resourceName,
                appHostSummary.controller);
        }
        if (matches.length > 1) {
            return createResourceFailure(
                'resourceAmbiguous',
                target.displayPath,
                resourceName,
                appHostSummary.controller);
        }

        const resource = matches[0];
        const boundedResource = createBoundedResource(resource);
        const resourceTarget = getResourceTarget(resource);
        if (resourceTarget === undefined) {
            return createResourceStatusResult(
                'notDebugging',
                target.displayPath,
                resourceName,
                appHostSummary.controller,
                boundedResource);
        }

        const matchingSessions = this.getResourceSessionMatches(
            target,
            resources,
            this._dependencies.getEditorResourceSessions())
            .filter(match => match.matchingResources.includes(resource));
        if (matchingSessions.length === 0) {
            return createResourceStatusResult(
                'notDebugging',
                target.displayPath,
                resourceName,
                appHostSummary.controller,
                boundedResource);
        }

        if (matchingSessions.some(match => match.matchingResources.length > 1)) {
            return createResourceFailure(
                'resourceAmbiguous',
                target.displayPath,
                resourceName,
                appHostSummary.controller);
        }
        if (matchingSessions.length > 1) {
            return createResourceStatusResult(
                'multipleSessions',
                target.displayPath,
                resourceName,
                'editor',
                boundedResource);
        }

        const session = matchingSessions[0].session;
        return createResourceStatusResult(
            session.state,
            target.displayPath,
            resourceName,
            'editor',
            boundedResource,
            session.mode);
    }

    async explainLaunchFailure(input: unknown, token: vscode.CancellationToken): Promise<ExplainLaunchFailureToolResult> {
        const preflight = await this.preflight(
            input,
            token,
            isValidAppHostPathOnlyInput,
            aspireExplainLaunchFailureToolName);
        if (!preflight.resolved) {
            return createExplainFailure(preflight.outcome);
        }

        try {
            // The journal answers about whichever AppHost the path it is given resolves to, so it
            // is given the physical AppHost this target was bound to rather than the selector. The
            // journal's own path-to-identity resolution and the freshness check below are separate
            // filesystem calls, and another process can repoint an alias between them and repoint
            // it back, which would publish one AppHost's recorded failure - or its recorded
            // silence - under an identity that never named it.
            const [failure] = this._dependencies.readLatestLaunchFailures(preflight.target.canonicalPath);
            throwIfCanceled(token);
            // The target was resolved before that read across an asynchronous step. Revalidating
            // here - after the read and before either answer is assembled, with nothing awaited in
            // between - keeps an answer about one AppHost from being published under a selector
            // that has since stopped naming it.
            this.throwIfTargetsStale(preflight.target);
            if (!failure) {
                return {
                    success: true,
                    tool: aspireExplainLaunchFailureToolName,
                    outcome: 'noRecordedFailure',
                    appHost: preflight.target.displayPath,
                };
            }

            const result: ExplainLaunchFailureFoundResult = {
                success: true,
                tool: aspireExplainLaunchFailureToolName,
                outcome: 'failureFound',
                appHost: preflight.target.displayPath,
                stage: failure.stage,
                category: failure.category,
                controller: failure.controller,
                mode: failure.mode,
                providerKind: failure.providerKind,
                exitCodeBucket: failure.exitCodeBucket,
                recommendedActions: getRecommendedActions(failure.category),
            };
            return result;
        }
        catch (error) {
            return this.createExplainError(error);
        }
    }

    /**
     * Reports whether C# Dev Kit Hot Reload is enabled and whether it could reach one
     * selected Aspire resource, with the fallback to use when it cannot.
     *
     * Nothing here starts, triggers, or verifies a Hot Reload: the answer is assembled from
     * the debugger's own diagnostics probe and the sessions and resources this window already
     * tracks. Selection fails closed, because reporting Hot Reload for the wrong resource is
     * worse than reporting that the target could not be identified.
     */
    async getHotReloadStatus(input: unknown, token: vscode.CancellationToken): Promise<HotReloadStatusToolResult> {
        if (token.isCancellationRequested) {
            return createHotReloadFailure('canceled');
        }
        if (!vscode.workspace.isTrusted) {
            return createHotReloadFailure('workspaceNotTrusted');
        }
        if (!isValidHotReloadStatusInput(input)) {
            return createHotReloadFailure('invalidInput');
        }

        try {
            const collection = await this.collectHotReloadCandidates(input.resourceName, input.appHostPath, token);
            throwIfCanceled(token);
            if (!collection.resolved) {
                return createHotReloadFailure(collection.outcome);
            }

            const candidates = collection.candidates;
            if (candidates.length === 0) {
                return createHotReloadFailure(
                    input.resourceName === undefined ? 'noEditorControlledResource' : 'resourceNotFound');
            }

            if (candidates.length > 1) {
                return createHotReloadFailure('resourceAmbiguous');
            }

            const candidate = candidates[0];
            // More than one session claiming the resource, or one session that could equally be
            // any of several resources, leaves no single session to describe.
            if (candidate.matches.length > 1 || candidate.matches.some(match => match.matchingResources.length > 1)) {
                return createHotReloadFailure('resourceAmbiguous');
            }

            if (candidate.controller === 'external') {
                return createHotReloadFailure('noEditorControlledResource');
            }

            return createHotReloadReport(candidate, this._dependencies.readHotReloadDiagnostics());
        }
        catch (error) {
            if (isCommandCancellation(error) || token.isCancellationRequested) {
                return createHotReloadFailure('canceled');
            }
            if (error instanceof StaleAppHostTargetError) {
                return createHotReloadFailure('appHostNotFound');
            }
            if (error instanceof AmbiguousAppHostOwnershipError) {
                return createHotReloadFailure('ambiguousAppHost');
            }

            extensionLogOutputChannel.error(`Aspire language model tool ${aspireHotReloadStatusToolName} failed.`);
            return createHotReloadFailure('error');
        }
    }

    /**
     * Enumerates the resources a Hot Reload question could be about.
     *
     * A named resource is looked for across every AppHost in scope, so a resource this window
     * does not debug can still be answered with a definitive "never applicable". An omitted
     * name only considers resources an editor session claims, because those are the only ones
     * the window could have been asked about implicitly.
     */
    private async collectHotReloadCandidates(
        requestedResourceName: string | undefined,
        requestedAppHostPath: string | undefined,
        token: vscode.CancellationToken): Promise<HotReloadCandidateCollection> {
        const scopes = await this.resolveHotReloadScopes(requestedAppHostPath, token);
        if (!scopes.resolved) {
            return scopes;
        }

        const editorResourceSessions = this._dependencies.getEditorResourceSessions();
        const candidates: HotReloadCandidate[] = [];

        for (const scope of scopes.scopes) {
            // `aspire describe` evaluates the AppHost it is pointed at, so the target is checked
            // again right before the read rather than only before the answer is published: a
            // retargeted path must not be handed to the CLI at all.
            this.throwIfTargetsStale(scope.target);
            // Reporting must not wait for a resource to appear, but it must also not mistake a
            // window with no open describe stream for an AppHost with no resources, so this is
            // the authoritative one-shot read rather than whatever the cache happens to hold.
            // It is addressed to the AppHost this scope was bound to, because the CLI follows the
            // path it is handed when it runs and an alias moved during the read would answer for
            // a different AppHost.
            const resources = await this._dependencies.resourceRepository.fetchAppHostResourcesOnce(
                createAppHostOperationTarget(scope.target.canonicalPath, scope.target.absolutePath),
                token);
            throwIfCanceled(token);

            const sessionMatches = this.getResourceSessionMatches(scope.target, resources, editorResourceSessions);
            const selectedResources = requestedResourceName === undefined
                ? resources
                : resolveResourceNameMatches(resources, requestedResourceName);
            for (const resource of selectedResources) {
                const matches = sessionMatches.filter(match => match.matchingResources.includes(resource));
                if (requestedResourceName === undefined && matches.length === 0) {
                    continue;
                }

                candidates.push({
                    appHost: scope.target.displayPath,
                    controller: scope.controller,
                    resource,
                    matches,
                });
            }
        }

        // Whether a resource name is unique, or missing, is a statement about every AppHost in
        // scope, so all of them are revalidated after the last read rather than each one right
        // after its own. A scope that stopped being the file it was resolved from invalidates
        // the aggregate: dropping it instead would turn a shared name into a unique one. Idle
        // AppHosts are revalidated too, because they were part of the enumeration that decided
        // which AppHosts could publish the name at all.
        this.throwIfTargetsStale(...scopes.observedTargets);

        return { resolved: true, candidates };
    }

    /**
     * Decides which AppHosts a Hot Reload question may be answered from.
     *
     * A supplied selector resolves one AppHost through the same shared resolver every other
     * editor tool uses, and is summarized directly so the bounded active-session list cannot
     * hide it. Without a selector the question is global, and then the bounded snapshot decides:
     * once it is truncated neither "no such resource" nor "exactly one such resource" can be
     * established, because both are statements about AppHosts that were never looked at.
     */
    private async resolveHotReloadScopes(
        requestedAppHostPath: string | undefined,
        token: vscode.CancellationToken): Promise<HotReloadScopeResolution> {
        if (requestedAppHostPath !== undefined) {
            const resolution = await this._dependencies.targetResolver.resolveTarget(requestedAppHostPath, token);
            throwIfCanceled(token);
            if (!resolution.resolved) {
                return { resolved: false, outcome: resolution.outcome };
            }

            const summary = await this._dependencies.snapshotService.getAppHostSummary(resolution.target, token);
            if (summary.controller === 'external') {
                // Hot Reload diagnostics belong to this editor's debugger. Once exact resolution
                // establishes external ownership, reading that AppHost's resources cannot turn it
                // into an editor-controlled target and would only expose unrelated failure modes.
                return { resolved: false, outcome: 'noEditorControlledResource' };
            }
            if (summary.state === 'notDebugging') {
                // A stopped AppHost publishes no resources, so there is nothing to read and no
                // Hot Reload question to answer about it. Reading anyway would turn state this
                // window already knows into whatever `aspire describe` fails with, and an empty
                // read would report a named resource as missing rather than as not running.
                // A stopping AppHost is deliberately not short-circuited: it can still publish
                // resources, so its answer keeps coming from the authoritative read.
                return { resolved: false, outcome: 'appHostNotRunning' };
            }

            return {
                resolved: true,
                scopes: [{ target: resolution.target, controller: summary.controller }],
                observedTargets: [resolution.target],
            };
        }

        const snapshot = await this._dependencies.snapshotService.createActiveSessionSnapshot(token);
        throwIfCanceled(token);
        if (snapshot.truncated) {
            return { resolved: false, outcome: 'tooManyActiveAppHosts' };
        }

        // Every AppHost the snapshot observed stays in scope. Re-resolving each one by display
        // path would let a registry change between the snapshot and the lookup drop an AppHost
        // that publishes the requested name, which would report a shared name as unique.
        return {
            resolved: true,
            scopes: snapshot.appHosts.map(entry => ({
                target: entry.target,
                controller: entry.summary.controller,
            })),
            // Idle AppHosts answer nothing, but they were part of the enumeration that made this
            // lookup global, so they stay in scope for the freshness barrier.
            observedTargets: snapshot.observedTargets,
        };
    }

    private getResourceSessionMatches(
        target: ResolvedAppHostTarget,
        resources: readonly ResourceJson[],
        editorResourceSessions: readonly EditorResourceSessionSnapshot[]): readonly ResourceSessionMatch[] {
        // A Python module/executable launch can carry both the interpreter and console-script
        // paths because the typed launch shape cannot distinguish those entrypoint kinds. Keep
        // every candidate match so status can report ambiguity while list can omit it.
        return this.getEditorResourceSessionsForAppHost(target, editorResourceSessions)
            .map(session => ({
                session,
                matchingResources: resources.filter(resource => {
                    const resourceTarget = getResourceTarget(resource);
                    return resourceTarget !== undefined && isSessionTargetMatch(session, resourceTarget);
                }),
            }));
    }

    private getEditorResourceSessionsForAppHost(
        target: ResolvedAppHostTarget,
        editorResourceSessions: readonly EditorResourceSessionSnapshot[]): readonly EditorResourceSessionSnapshot[] {
        return editorResourceSessions.filter(session =>
            (session.appHostIdentity
                ?? this._dependencies.targetResolver.getIdentityForAppHostPath(session.appHostPath)) === target.identity);
    }

    /**
     * Rejects targets that no longer name the filesystem entries they were resolved from.
     *
     * The resolver binds an identity to the entry a path currently selects, so a symlink
     * retarget or a registry entry replaced mid-read produces a different identity for the
     * same display path. Every tool here reads asynchronously after resolving, so the whole
     * set a result covers is revalidated before publication rather than trusted from
     * resolution time - a target read first can still be repointed while a later one is read.
     */
    private throwIfTargetsStale(...targets: readonly ResolvedAppHostTarget[]): void {
        this._dependencies.targetResolver.assertTargetsCurrent(targets);
    }

    private async preflight<T>(
        input: unknown,
        token: vscode.CancellationToken,
        validate: (value: unknown) => value is T,
        tool: string): Promise<ResolvedPreflight<T>> {
        if (token.isCancellationRequested) {
            return { resolved: false, outcome: 'canceled' };
        }

        if (!vscode.workspace.isTrusted) {
            return { resolved: false, outcome: 'workspaceNotTrusted' };
        }

        if (!validate(input)) {
            return { resolved: false, outcome: 'invalidInput' };
        }

        let resolution: SafeAppHostTargetResolution;
        try {
            resolution = await this._dependencies.targetResolver.resolveTarget(
                (input as { readonly appHostPath: string }).appHostPath,
                token);
        }
        catch (error) {
            if (isCommandCancellation(error)) {
                return { resolved: false, outcome: 'canceled' };
            }

            extensionLogOutputChannel.error(`Aspire language model tool ${tool} failed while resolving an AppHost.`);
            return { resolved: false, outcome: 'error' };
        }

        if (resolution.resolved) {
            return { ...resolution, input };
        }

        return {
            resolved: false,
            outcome: resolution.outcome,
        };
    }

    private createStatusError(error: unknown): DebugSessionStatusFailureResult {
        if (isCommandCancellation(error)) {
            return createStatusFailure('canceled');
        }
        if (error instanceof StaleAppHostTargetError) {
            return createStatusFailure('appHostNotFound');
        }
        if (error instanceof AmbiguousAppHostOwnershipError) {
            return createStatusFailure('ambiguousAppHost');
        }

        extensionLogOutputChannel.error(`Aspire language model tool ${aspireDebugSessionStatusToolName} failed.`);
        return createStatusFailure('error');
    }

    private createExplainError(error: unknown): ExplainLaunchFailureFailureResult {
        if (isCommandCancellation(error)) {
            return createExplainFailure('canceled');
        }
        // A target that stopped naming the entry it was resolved from is reported the same way
        // every other editor-assistance surface reports it: the AppHost the caller named is no
        // longer there to answer for, which is `appHostNotFound` rather than a failure to run.
        if (error instanceof StaleAppHostTargetError) {
            return createExplainFailure('appHostNotFound');
        }

        extensionLogOutputChannel.error(`Aspire language model tool ${aspireExplainLaunchFailureToolName} failed.`);
        return createExplainFailure('error');
    }
}

function createAppHostStatusResult(summary: EditorAppHostSummary): DebugSessionStatusResult {
    const result: DebugSessionStatusResult = {
        success: true,
        tool: aspireDebugSessionStatusToolName,
        outcome: summary.state,
        scope: 'appHost',
        controller: summary.controller,
        appHost: summary.appHost,
    };
    if (isModeMeaningful(summary.state)) {
        return { ...result, mode: summary.mode };
    }

    return result;
}

function createResourceStatusResult(
    outcome: DebugSessionStatusResult['outcome'],
    appHost: string,
    resourceName: string,
    controller: EditorAssistanceController,
    resource: EditorAssistanceResource,
    mode?: EditorAssistanceMode): DebugSessionStatusResult {
    const result: DebugSessionStatusResult = {
        success: true,
        tool: aspireDebugSessionStatusToolName,
        outcome,
        scope: 'resource',
        controller,
        appHost,
        resourceName,
        resource,
    };
    if (mode !== undefined && isModeMeaningful(outcome)) {
        return { ...result, mode };
    }

    return result;
}

function createResourceFailure(
    outcome: DebugSessionStatusResourceFailureResult['outcome'],
    appHost: string,
    resourceName: string,
    controller: EditorAssistanceController): DebugSessionStatusResourceFailureResult {
    return {
        success: false,
        tool: aspireDebugSessionStatusToolName,
        outcome,
        scope: 'resource',
        controller,
        appHost,
        resourceName,
    };
}

function createStatusFailure(outcome: DebugSessionStatusFailureResult['outcome']): DebugSessionStatusFailureResult {
    return {
        success: false,
        tool: aspireDebugSessionStatusToolName,
        outcome,
    };
}

function createExplainFailure(outcome: ExplainLaunchFailureFailureResult['outcome']): ExplainLaunchFailureFailureResult {
    return {
        success: false,
        tool: aspireExplainLaunchFailureToolName,
        outcome,
    };
}

function createOpenDashboardFailure(outcome: OpenDashboardFailureResult['outcome']): OpenDashboardFailureResult {
    return {
        success: false,
        tool: aspireOpenDashboardToolName,
        outcome,
    };
}

function createOpenOutputFailure(outcome: OpenOutputFailureResult['outcome']): OpenOutputFailureResult {
    return {
        success: false,
        tool: aspireOpenOutputToolName,
        outcome,
    };
}

function createHotReloadFailure(
    outcome: HotReloadStatusUnavailableResult['outcome']): HotReloadStatusUnavailableResult {
    return {
        success: false,
        tool: aspireHotReloadStatusToolName,
        outcome,
    };
}

/**
 * Turns one selected resource and the current diagnostics into the reported answer.
 *
 * `csharp.debug.hotReloadOnSave` only decides whether saving applies an edit automatically,
 * so it is reported as evidence but never gates applicability. Everything else is a real
 * gate: C# Dev Kit provides Hot Reload, only for a debugger-attached session it owns, and
 * only for the .NET project launch path (see `createProjectDebuggerExtension`).
 */
function createHotReloadReport(
    candidate: HotReloadCandidate,
    diagnostics: HotReloadDiagnostics): HotReloadStatusToolResult {
    const session = candidate.matches[0]?.session;
    const sessionEvidence: HotReloadEvidence = session === undefined
        ? 'notEditorDebuggedResource'
        // `other` means the launch never recorded whether a debugger was attached, so it is
        // reported as unknown instead of being folded into "no debugger": Hot Reload stays
        // unavailable either way, but only one of those two statements is known to be true.
        : session.mode === 'other'
            ? 'editorSessionModeUnknown'
            : session.mode === 'run'
                ? 'editorSessionWithoutDebugger'
                : session.state === 'stopping'
                    ? 'editorDebugSessionStopping'
                    // `starting` is recorded when the resource launch is tracked, before VS Code
                    // reports the debug session as started, so no debugger is attached yet.
                    // Hot Reload is applied through that attached debugger, so this is a real
                    // gate rather than a slower path to the same answer.
                    : session.state === 'starting'
                        ? 'editorDebugSessionStarting'
                        : 'editorDebugSession';
    const resourceEvidence: HotReloadEvidence = getResourceTarget(candidate.resource)?.kind === 'project'
        ? 'dotnetProjectResource'
        : 'nonDotnetResource';
    // `workspaceTrusted` is deliberately not re-applied here. Trust is an earlier fail-closed
    // gate in `getHotReloadStatus`, and this result carries no trust evidence identifier, so
    // folding it back in could only produce a `hotReloadEnabled: false` that the evidence
    // cannot explain.
    const hotReloadEnabled = diagnostics.devKitInstalled &&
        diagnostics.settingContributed &&
        diagnostics.settingEnabled;
    const applicable = hotReloadEnabled &&
        sessionEvidence === 'editorDebugSession' &&
        resourceEvidence === 'dotnetProjectResource';

    return {
        success: true,
        tool: aspireHotReloadStatusToolName,
        outcome: applicable ? 'applicable' : 'notApplicable',
        appHost: candidate.appHost,
        // The registry's own name, never the caller's selector, so a display-name or
        // differently cased request cannot echo back as if it were the resource.
        resourceName: candidate.resource.name,
        controller: candidate.controller,
        hotReloadEnabled,
        evidence: [
            diagnostics.devKitInstalled ? 'devKitInstalled' : 'devKitNotInstalled',
            !diagnostics.settingContributed
                ? 'hotReloadSettingUnavailable'
                : diagnostics.settingEnabled ? 'hotReloadSettingEnabled' : 'hotReloadSettingDisabled',
            diagnostics.reloadOnSaveEnabled ? 'hotReloadOnSaveEnabled' : 'hotReloadOnSaveDisabled',
            sessionEvidence,
            resourceEvidence,
        ],
        // The fallback is deliberately the same two steps for every answer. It is an ordered
        // escalation ladder - try the smallest recovery, then the larger one - and not a
        // prediction that either step will carry the change: nothing here observes a rebuild,
        // so claiming that restarting the resource is sufficient (or that it is not) would be a
        // guess. Ordering is the whole content: the resource restart is always safe to try
        // first, and rebuilding and restarting the AppHost is only correct once it is not enough.
        fallback: ['restartResource', 'rebuildAndRestartAppHost'],
    };
}

function createListDebugSessionsFailure(
    outcome: Extract<ListDebugSessionsToolResult['outcome'], 'ambiguousAppHost' | 'workspaceNotTrusted' | 'invalidInput' | 'canceled' | 'error'>): ListDebugSessionsToolResult {
    return {
        success: false,
        tool: aspireListDebugSessionsToolName,
        outcome,
        sessions: [],
    };
}

function validateEmptyObjectInvocation(
    input: unknown,
    token: vscode.CancellationToken): 'workspaceNotTrusted' | 'invalidInput' | 'canceled' | undefined {
    if (token.isCancellationRequested) {
        return 'canceled';
    }
    if (!vscode.workspace.isTrusted) {
        return 'workspaceNotTrusted';
    }
    if (!isValidEmptyObjectInput(input)) {
        return 'invalidInput';
    }

    return undefined;
}

function isModeMeaningful(outcome: DebugSessionStatusResult['outcome']): boolean {
    return outcome === 'running' || outcome === 'starting' || outcome === 'stopping';
}

const maxModelSafeResourceSourceLength = 256;

// Model-facing results need a smaller privacy boundary than the tree view. Rebuild source values
// from properties tied to known resource kinds so a custom resource cannot place arbitrary text in
// the canonical source field and have it copied into a tool result.
function getModelSafeResourceSource(resource: ResourceJson): string | null {
    let source: string | null | undefined;
    let useFileName = false;
    switch (resource.resourceType) {
        case ResourceType.Project:
            source = resource.properties?.['project.path'];
            useFileName = true;
            break;
        case ResourceType.Executable:
            source = resource.properties?.['executable.path'];
            useFileName = true;
            break;
        case ResourceType.Container:
            source = resource.properties?.['container.image'];
            break;
        default:
            return null;
    }

    if (typeof source !== 'string') {
        return null;
    }

    const boundedSource = useFileName ? getPortableFileName(source) : source.trim();
    return boundedSource !== undefined &&
        boundedSource.length > 0 &&
        [...boundedSource].length <= maxModelSafeResourceSourceLength
        ? boundedSource
        : null;
}

function getPortableFileName(value: string): string | undefined {
    const separatorIndex = Math.max(value.lastIndexOf('/'), value.lastIndexOf('\\'));
    const fileName = value.slice(separatorIndex + 1);
    return fileName.trim().length > 0 ? fileName : undefined;
}

function createBoundedResource(resource: ResourceJson): EditorAssistanceResource {
    return {
        resourceType: resource.resourceType,
        state: getModelSafeResourceState(resource.state),
        healthStatus: resource.healthStatus,
        exitCode: resource.exitCode,
        source: getModelSafeResourceSource(resource),
    };
}

function getModelSafeResourceState(state: string | null): EditorAssistanceResourceState {
    switch (state) {
        case ResourceState.Running:
        case ResourceState.Active:
        case ResourceState.Starting:
        case ResourceState.Building:
        case ResourceState.Stopping:
        case ResourceState.Stopped:
        case ResourceState.Waiting:
        case ResourceState.NotStarted:
        case ResourceState.Finished:
        case ResourceState.Exited:
        case ResourceState.FailedToStart:
        case ResourceState.RuntimeUnhealthy:
        case ResourceState.ValueMissing:
            return state;
        default:
            return 'unknown';
    }
}

function getRecommendedActions(category: ExplainLaunchFailureFoundResult['category']): readonly EditorAssistanceRecommendedAction[] {
    switch (category) {
        case 'invalidConfiguration':
        case 'processExited':
        case 'unknown':
            return ['checkAspireOutput'];
        case 'missingDependency':
        case 'unsupported':
            return ['checkDependencies'];
        case 'cliUnavailable':
            return ['installAspireCli'];
        case 'buildFailed':
            return ['fixBuildErrors'];
        case 'timeout':
        case 'canceled':
            return ['retryLaunch'];
        case 'portConflict':
            return ['freeRequiredPort'];
        case 'permissionDenied':
            return ['checkPermissions'];
        default:
            return ['checkAspireOutput'];
    }
}

function throwIfCanceled(token: vscode.CancellationToken): void {
    if (token.isCancellationRequested) {
        throw new vscode.CancellationError();
    }
}

type ResourceTarget = {
    readonly kind: 'project' | 'executable';
    readonly path: string;
    // Only carried for executables. See `isSessionTargetMatch` for why the command alone is not
    // always enough to identify one executable resource.
    readonly workDir?: string;
};

function getResourceTarget(resource: ResourceJson): ResourceTarget | undefined {
    const projectPath = resource.properties?.['project.path'];
    if (typeof projectPath === 'string' && projectPath.trim().length > 0) {
        return { kind: 'project', path: projectPath };
    }

    const executablePath = resource.properties?.['executable.path'];
    if (typeof executablePath !== 'string' || executablePath.trim().length === 0) {
        return undefined;
    }

    const workDir = resource.properties?.['executable.workDir'];
    return {
        kind: 'executable',
        path: executablePath,
        ...(typeof workDir === 'string' && workDir.trim().length > 0 ? { workDir } : {}),
    };
}

function isSessionTargetMatch(
    session: EditorResourceSessionSnapshot,
    resourceTarget: ResourceTarget): boolean {
    if (resourceTarget.kind === 'project') {
        return isSamePath(session.targetPath, resourceTarget.path);
    }

    const executablePaths = session.resourceExecutablePaths ?? [session.targetPath];
    if (executablePaths.some(executablePath => isSamePath(executablePath, resourceTarget.path))) {
        return true;
    }

    // Some resources cannot be identified by their command at all. A Java resource launched
    // through WithMavenGoal/WithGradleTask runs the wrapper, so DCP reports its command as `sh`
    // on POSIX or `cmd` on Windows, which no launch configuration can meaningfully claim. For
    // those the working directory is the only stable link back to the session's target, and this
    // stays an additional way to match rather than a replacement so source-target languages —
    // where the target is a script or program path rather than a directory — are unaffected.
    return resourceTarget.workDir !== undefined && isSamePath(session.targetPath, resourceTarget.workDir);
}
