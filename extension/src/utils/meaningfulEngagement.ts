import * as vscode from 'vscode';
import type { AppHostDataRepository, WorkspaceAppHostDiscoverySnapshot } from '../data/AppHostDataRepository';
import { type AppHostLanguageSummary, summarizeAppHostLanguages } from './appHostLanguage';
import { type AppHostTargetVersionSummary, summarizeAppHostTargetVersions } from './appHostTargetVersion';
import { extensionLogOutputChannel } from './logging';
import { sendTelemetryEvent, setCommandInvocationListener, setCommonTelemetryProperties } from './telemetry';

interface AppHostTelemetryContext {
    apphost_present: 'true' | 'false' | undefined;
    apphost_languages: AppHostLanguageSummary;
    apphost_target_versions: AppHostTargetVersionSummary;
}

/**
 * Trigger that caused the meaningful-engagement event to fire.
 */
export type EngagementTrigger = 'apphost_detected' | 'command' | 'debug_session';

/**
 * Fires the `engagement/active` telemetry event at most once per extension
 * activation, once we observe a signal that suggests the user is *engaging*
 * with Aspire rather than just having the extension loaded.
 *
 * The extension's `activationEvents` are intentionally broad (file watcher
 * patterns, MCP provider, debug provider, …), so plain activation is a poor
 * engagement signal — it would fire for any workspace where the extension is
 * installed, regardless of whether the user touches Aspire. We instead wait
 * for one of:
 *
 *   - an AppHost is discovered in the workspace (via {@link AppHostDataRepository}),
 *   - any extension command is invoked (signalled via {@link recordCommandInvoked}),
 *   - the DCP server accepts a `PUT /run_session` (signalled via {@link recordDebugSession}).
 *
 * Whichever triggers first wins; subsequent triggers are dropped. Workspace
 * context is refreshed independently from the repository's discovery snapshots,
 * so a failed first scan cannot permanently misclassify subsequent events.
 */
export class MeaningfulEngagementReporter implements vscode.Disposable {
    private _fired = false;
    private _disposed = false;
    private _contextGeneration = 0;
    private _contextTask: Promise<void> = Promise.resolve();
    private readonly _contextChanged = new vscode.EventEmitter<void>();
    private _context: AppHostTelemetryContext = {
        apphost_present: undefined,
        apphost_languages: 'unknown',
        apphost_target_versions: 'unknown',
    };
    private readonly _disposables: vscode.Disposable[] = [];

    constructor(
        repository: Pick<AppHostDataRepository, 'workspaceAppHostDiscovery' | 'onDidChangeWorkspaceAppHostDiscovery'>,
    ) {
        this._disposables.push(
            repository.onDidChangeWorkspaceAppHostDiscovery(snapshot => {
                this._updateWorkspaceContext(snapshot);
            })
        );

        // Hook into the command-telemetry pipeline so the first wrapped
        // command invocation in the session triggers engagement.
        setCommandInvocationListener(() => this.recordCommandInvoked());
        this._disposables.push({ dispose: () => setCommandInvocationListener(undefined) });

        // Discovery may already have completed before this subscriber was created.
        this._updateWorkspaceContext(repository.workspaceAppHostDiscovery);
    }

    /**
     * Should be called whenever an extension command is invoked. Causes the
     * engagement event to fire if it hasn't already.
     */
    recordCommandInvoked(): void {
        void this._tryFire('command');
    }

    /**
     * Should be called when the DCP server receives a `PUT /run_session`
     * request — i.e. an external Aspire CLI process has begun a debug session
     * against the extension. Causes the engagement event to fire if it
     * hasn't already.
     */
    recordDebugSession(): void {
        void this._tryFire('debug_session');
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }
        this._disposed = true;
        this._contextGeneration++;
        this._contextChanged.fire();
        this._contextChanged.dispose();
        this._disposables.forEach(d => d.dispose());
        setCommonTelemetryProperties({
            apphost_present: undefined,
            apphost_languages: undefined,
            apphost_target_versions: undefined,
        });
    }

    private _updateWorkspaceContext(snapshot: WorkspaceAppHostDiscoverySnapshot): void {
        if (this._disposed) {
            return;
        }

        const generation = ++this._contextGeneration;
        const complete = snapshot.status === 'success';
        const hasCandidates = snapshot.candidates.length > 0;
        this._context = {
            // An incomplete scan can prove presence, but cannot prove absence.
            apphost_present: hasCandidates ? 'true' : complete ? 'false' : undefined,
            apphost_languages: complete ? summarizeAppHostLanguages(snapshot.candidates) : 'unknown',
            apphost_target_versions: complete && !hasCandidates ? 'none' : 'unknown',
        };
        setCommonTelemetryProperties(this._context);

        this._contextTask = complete && hasCandidates
            ? this._updateTargetVersions(snapshot.candidates, generation)
            : Promise.resolve();
        this._contextChanged.fire();
        if (hasCandidates) {
            void this._tryFire('apphost_detected');
        }
    }

    private async _updateTargetVersions(
        candidates: WorkspaceAppHostDiscoverySnapshot['candidates'],
        generation: number): Promise<void> {
        try {
            const targetVersions = await summarizeAppHostTargetVersions(candidates);
            if (this._disposed || generation !== this._contextGeneration) {
                return;
            }

            this._context.apphost_target_versions = targetVersions;
            setCommonTelemetryProperties({ apphost_target_versions: targetVersions });
        }
        catch (error) {
            if (!this._disposed && generation === this._contextGeneration) {
                extensionLogOutputChannel.warn(`Failed to resolve AppHost target versions for telemetry: ${String(error)}`);
            }
        }
    }

    private async _waitForCurrentContext(): Promise<void> {
        while (!this._disposed) {
            const generation = this._contextGeneration;
            let subscription: vscode.Disposable | undefined;
            const contextChanged = new Promise<void>(resolve => {
                subscription = this._contextChanged.event(resolve);
            });

            try {
                // A superseded filesystem lookup may never finish. Wake on context changes
                // or disposal instead of keeping engagement attached to that obsolete work.
                await Promise.race([this._contextTask, contextChanged]);
            }
            finally {
                subscription?.dispose();
            }

            if (generation === this._contextGeneration) {
                return;
            }
        }
    }

    private async _tryFire(trigger: EngagementTrigger): Promise<void> {
        if (this._fired || this._disposed) {
            return;
        }
        this._fired = true;

        // Wait only for metadata already being read, never for another CLI discovery.
        // If discovery changes in the meantime, report the latest conservative context.
        await this._waitForCurrentContext();
        if (this._disposed) {
            return;
        }
        const workspaceFolderCount = vscode.workspace.workspaceFolders?.length ?? 0;
        const hasCSharpDevKit = vscode.extensions.getExtension('ms-dotnettools.csdevkit') !== undefined;

        sendTelemetryEvent('aspire/vscode/engagement/active', {
            trigger,
            ...this._context,
            has_csharp_devkit: hasCSharpDevKit ? 'true' : 'false',
        }, {
            workspace_folders: workspaceFolderCount,
        });
    }
}
