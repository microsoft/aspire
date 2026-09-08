import * as vscode from 'vscode';

import { type AppHostEditorSessionSnapshot } from '../services/AppHostLaunchService';
import { type AppHostEditorStateLaunchService, type AppHostLifecycleRunningAppHost } from './appHostLifecycleToolContracts';
import { type AppHostTargetIdentity, type ResolvedAppHostTarget, SafeAppHostTargetResolver } from './safeAppHostTargetResolver';

const maxSummaries = 20;

export type EditorAppHostState = 'running' | 'starting' | 'stopping' | 'notDebugging' | 'multipleSessions';
export type EditorAppHostMode = 'run' | 'debug' | 'other';

export interface EditorAppHostSummary {
    readonly appHost: string;
    readonly state: EditorAppHostState;
    readonly mode: EditorAppHostMode;
    readonly controller: 'editor' | 'external';
}

export interface EditorStateSnapshot {
    readonly appHosts: readonly EditorAppHostSummary[];
}

/**
 * One active AppHost, paired with the target it was summarized from.
 *
 * Callers read each AppHost asynchronously after the snapshot is taken, and re-resolving a
 * summary by its display path would let a registry change in between turn a known AppHost
 * into an unknown one. Carrying the resolved target keeps every AppHost the snapshot
 * observed in scope for the whole lookup, so nothing can be silently dropped from an
 * absence or uniqueness decision.
 */
export interface ActiveEditorAppHost {
    readonly target: ResolvedAppHostTarget;
    readonly summary: EditorAppHostSummary;
}

export interface ActiveEditorStateSnapshot {
    readonly appHosts: readonly ActiveEditorAppHost[];
    /**
     * Every target this snapshot enumerated, including the idle ones no summary is published for.
     *
     * Callers keep reading asynchronously after the snapshot returns, and an idle AppHost can be
     * repointed during one of those reads. It is carried here rather than dropped because it is
     * part of what makes the published answer complete: which AppHosts existed decides whether a
     * resource name is unique and whether the listed sessions are all of them. Revalidating the
     * whole captured set at the final barrier keeps a change to the enumerated scope from passing
     * unnoticed just because that AppHost had nothing active to report.
     */
    readonly observedTargets: readonly ResolvedAppHostTarget[];
    readonly truncated?: true;
}

export interface EditorStateSnapshotServiceDependencies {
    readonly launchService: AppHostEditorStateLaunchService;
    readonly targetResolver: SafeAppHostTargetResolver;
}

/**
 * Raised when the relationship between a known AppHost and a running one could not be
 * decided, so who runs it was never established.
 *
 * An undecidable relationship is not the absence of one: reporting it as idle would deny a
 * run that may exist, and reporting it as externally running would claim one that may not.
 * `EditorUiHandoffService` already refuses the same relationship, so every editor-assistance
 * surface answers `ambiguousAppHost` rather than disagreeing about the same AppHost.
 */
export class AmbiguousAppHostOwnershipError extends Error {
    constructor() {
        super('The relationship between an Aspire AppHost and a running AppHost could not be decided.');
        this.name = 'AmbiguousAppHostOwnershipError';
    }
}

/**
 * Produces a bounded, model-safe summary of the AppHosts this editor window knows about.
 *
 * The snapshot intentionally stops at AppHost-level state. Resource details, debug
 * session ids, launch configurations, and process identifiers are all omitted so the
 * `list_debug_sessions` surface can answer "what is the editor doing?" without
 * handing the model ambient handles into unrelated APIs.
 */
export class EditorStateSnapshotService {
    private readonly _dependencies: EditorStateSnapshotServiceDependencies;

    constructor(dependencies: EditorStateSnapshotServiceDependencies) {
        this._dependencies = dependencies;
    }

    async createSnapshot(token: vscode.CancellationToken): Promise<EditorStateSnapshot> {
        const representativeTargets = await this.enumerateRepresentativeTargets(token, maxSummaries);

        return {
            appHosts: await this.projectSummaries(representativeTargets, token),
        };
    }

    async createActiveSessionSnapshot(token: vscode.CancellationToken): Promise<ActiveEditorStateSnapshot> {
        const representativeTargets = await this.enumerateRepresentativeTargets(token);
        const sessionsByIdentity = this.groupEditorRunSessions(representativeTargets);
        const activeAppHosts = representativeTargets.flatMap(target => {
            const summary = this.createEditorStateSummary(
                target,
                sessionsByIdentity.get(target.identity) ?? []);
            return summary === undefined ? [] : [{ target, summary }];
        });
        const appHosts = activeAppHosts.slice(0, maxSummaries);
        throwIfCanceled(token);
        this._dependencies.targetResolver.assertTargetsCurrent(representativeTargets);

        // Only active AppHosts are summarized, but every enumerated target is carried out so the
        // caller's own freshness barrier covers the scope this snapshot was taken over rather than
        // just the subset that had something to say.
        return activeAppHosts.length > maxSummaries
            ? { appHosts, observedTargets: representativeTargets, truncated: true }
            : { appHosts, observedTargets: representativeTargets };
    }

    private async enumerateRepresentativeTargets(
        token: vscode.CancellationToken,
        limit?: number): Promise<readonly ResolvedAppHostTarget[]> {
        throwIfCanceled(token);
        const representativeTargets = selectRepresentativeTargets(
            await this._dependencies.targetResolver.enumerateKnownAppHosts(token),
            limit);
        throwIfCanceled(token);

        return representativeTargets;
    }

    /**
     * Summarizes every target, reading the running registry only when it can change an answer.
     *
     * `aspire ps` is a live CLI call that can fail or time out, and it only decides whether
     * something outside this window runs an AppHost. Reading it for every target would let
     * that failure erase states this window already knows for certain, so it is read once, and
     * only when at least one target has no editor session and no pending editor launch.
     */
    private async projectSummaries(
        targets: readonly ResolvedAppHostTarget[],
        token: vscode.CancellationToken): Promise<readonly EditorAppHostSummary[]> {
        const sessionsByIdentity = this.groupEditorRunSessions(targets);
        throwIfCanceled(token);

        const editorStateSummaries = targets.map(target =>
            this.createEditorStateSummary(target, sessionsByIdentity.get(target.identity) ?? []));
        const runningAppHosts = editorStateSummaries.some(summary => summary === undefined)
            ? await this._dependencies.launchService.getRunningAppHosts(token)
            : [];
        const summaries = targets.map((target, index) =>
            editorStateSummaries[index] ?? this.createExternalSummary(target, runningAppHosts));
        throwIfCanceled(token);
        // The running-AppHost read is the only asynchronous step between capturing these
        // targets and publishing their states, so revalidating here covers both that boundary
        // and publication itself. A target that changed can neither be described nor dropped:
        // the answer would be about a different file than the one that was resolved.
        this._dependencies.targetResolver.assertTargetsCurrent(targets);

        return summaries;
    }

    private groupEditorRunSessions(
        targets: readonly ResolvedAppHostTarget[]): ReadonlyMap<AppHostTargetIdentity, readonly AppHostEditorSessionSnapshot[]> {
        const knownIdentities = new Set(targets.map(target => target.identity));
        const sessionsByIdentity = new Map<AppHostTargetIdentity, AppHostEditorSessionSnapshot[]>();

        for (const session of this._dependencies.launchService.getEditorSessions()) {
            if (session.operationKind !== 'run') {
                continue;
            }

            const appHostPath = session.resolvedAppHostPath ?? session.appHostPath;
            if (!appHostPath) {
                continue;
            }

            const identity = session.appHostIdentity
                ?? this._dependencies.targetResolver.getIdentityForAppHostPath(appHostPath);
            if (!knownIdentities.has(identity)) {
                continue;
            }

            const grouped = sessionsByIdentity.get(identity);
            if (grouped) {
                grouped.push(session);
            }
            else {
                sessionsByIdentity.set(identity, [session]);
            }
        }

        return sessionsByIdentity;
    }

    /**
     * Summarizes one already-resolved AppHost without applying the bounded list cap.
     *
     * The status tool resolves its exact target first, so looking it up through
     * {@link createSnapshot} would make AppHosts beyond the first 20 appear to be
     * unknown. Direct summarization keeps the list bound specific to the future list
     * tool while preserving the same safe session projection and path identity rules.
     */
    async getAppHostSummary(target: ResolvedAppHostTarget, token: vscode.CancellationToken): Promise<EditorAppHostSummary> {
        throwIfCanceled(token);

        return (await this.projectSummaries([target], token))[0];
    }

    /**
     * Summarizes what this window knows on its own, or reports that it knows nothing.
     *
     * `undefined` means the editor neither runs nor is starting the AppHost, which is the only
     * case where who else might be running it still has to be established.
     *
     * The pending-launch question is asked about the AppHost this target was bound to rather
     * than about its selector, so a selector repointed after resolution cannot inherit the
     * launch state of the AppHost it used to name.
     */
    private createEditorStateSummary(
        target: ResolvedAppHostTarget,
        sessions: readonly AppHostEditorSessionSnapshot[]): EditorAppHostSummary | undefined {
        if (sessions.length > 1) {
            // Once more than one editor session could claim the AppHost there is no honest
            // single-session summary to return. Report that multiplicity instead of
            // inventing a run/debug answer from whichever session we happened to inspect
            // first.
            return createSummary(target.displayPath, 'multipleSessions', 'other');
        }

        const resolvedSession = sessions[0];
        if (resolvedSession) {
            return describeTrackedSession(target.displayPath, resolvedSession);
        }

        if (this._dependencies.launchService.hasPendingOrActiveRunLaunch(target.canonicalPath)) {
            return createSummary(target.displayPath, 'starting', 'other');
        }

        return undefined;
    }

    private createExternalSummary(
        target: ResolvedAppHostTarget,
        runningAppHosts: readonly AppHostLifecycleRunningAppHost[]): EditorAppHostSummary {
        // Every relation is classified explicitly, and all of them are inspected before any
        // answer is formed. Folding `ambiguous` in with `same` would turn "this may or may
        // not be the AppHost that is running" into a definite external run, and stopping at
        // the first `same` would make the answer depend on the order the CLI listed rows in.
        let isRunningExternally = false;
        for (const runningAppHost of runningAppHosts) {
            switch (this._dependencies.targetResolver.compareTargetToAppHostPath(target, runningAppHost.appHostPath)) {
                case 'same':
                    isRunningExternally = true;
                    break;
                case 'ambiguous':
                    throw new AmbiguousAppHostOwnershipError();
                case 'different':
                    break;
            }
        }

        return isRunningExternally
            ? createSummary(target.displayPath, 'running', 'other', 'external')
            : createSummary(target.displayPath, 'notDebugging', 'other');
    }
}

function describeTrackedSession(displayPath: string, session: AppHostEditorSessionSnapshot): EditorAppHostSummary {
    if (session.isStopping) {
        return createSummary(displayPath, 'stopping', getSessionMode(session));
    }

    return createSummary(
        displayPath,
        session.startupCompleted ? 'running' : 'starting',
        getSessionMode(session));
}

function getSessionMode(session: AppHostEditorSessionSnapshot): EditorAppHostMode {
    return getNoDebugMode(session.noDebug);
}

function getNoDebugMode(noDebug: unknown): EditorAppHostMode {
    return noDebug === true
        ? 'run'
        : noDebug === false
            ? 'debug'
            : 'other';
}

function createSummary(
    appHost: string,
    state: EditorAppHostState,
    mode: EditorAppHostMode,
    controller: EditorAppHostSummary['controller'] = 'editor'): EditorAppHostSummary {
    return {
        appHost,
        state,
        mode,
        controller,
    };
}

function selectRepresentativeTargets(
    targets: readonly ResolvedAppHostTarget[],
    limit?: number): readonly ResolvedAppHostTarget[] {
    const sorted = [...targets].sort((left, right) => compareDisplayPath(left.displayPath, right.displayPath));
    const representatives: ResolvedAppHostTarget[] = [];
    const seen = new Set<AppHostTargetIdentity>();
    for (const target of sorted) {
        if (seen.has(target.identity)) {
            continue;
        }

        seen.add(target.identity);
        representatives.push(target);
        if (representatives.length === limit) {
            break;
        }
    }

    return representatives;
}

function compareDisplayPath(left: string, right: string): number {
    if (left < right) {
        return -1;
    }

    if (left > right) {
        return 1;
    }

    return 0;
}

function throwIfCanceled(token: vscode.CancellationToken): void {
    if (token.isCancellationRequested) {
        throw new vscode.CancellationError();
    }
}
