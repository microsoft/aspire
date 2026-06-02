import * as vscode from 'vscode';
import { AppHostDiscoveryService } from './appHostDiscovery';
import { summarizeAppHostLanguages } from './appHostLanguage';
import { sendTelemetryEvent, setCommandInvocationListener, setCommonTelemetryProperties } from './telemetry';

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
 *   - an AppHost is discovered in the workspace (via {@link AppHostDiscoveryService}),
 *   - any extension command is invoked (signalled via {@link recordCommandInvoked}),
 *   - the DCP server accepts a `PUT /run_session` (signalled via {@link recordDebugSession}).
 *
 * Whichever triggers first wins; subsequent triggers are dropped. We also
 * publish a small set of common telemetry properties (apphost language
 * summary, presence flag) at fire time so subsequent events from the same
 * session carry consistent context.
 */
export class MeaningfulEngagementReporter implements vscode.Disposable {
    private _fired = false;
    private readonly _disposables: vscode.Disposable[] = [];

    constructor(
        private readonly _appHostDiscoveryService: AppHostDiscoveryService,
    ) {
        // Subscribe to discovery changes; the first time a workspace folder
        // produces at least one buildable AppHost candidate, fire.
        this._disposables.push(
            this._appHostDiscoveryService.onDidChangeCandidates(folder => {
                void this._checkAppHostsForFolder(folder, 'change');
            })
        );

        // Hook into the command-telemetry pipeline so the first wrapped
        // command invocation in the session triggers engagement.
        setCommandInvocationListener(() => this.recordCommandInvoked());
        this._disposables.push({ dispose: () => setCommandInvocationListener(undefined) });

        // Probe the current set of workspace folders eagerly. An AppHost may
        // already be present at activation; we don't want to wait for a
        // discovery change event that may never come.
        this._probeInitialWorkspaceFolders();
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
        this._disposables.forEach(d => d.dispose());
    }

    private _probeInitialWorkspaceFolders(): void {
        const folders = vscode.workspace.workspaceFolders;
        if (!folders) {
            return;
        }
        for (const folder of folders) {
            void this._checkAppHostsForFolder(folder, 'initial');
        }
    }

    private async _checkAppHostsForFolder(folder: vscode.WorkspaceFolder, _origin: 'initial' | 'change'): Promise<void> {
        if (this._fired) {
            return;
        }
        try {
            const candidates = await this._appHostDiscoveryService.discover(folder);
            if (this._fired) {
                return;
            }
            if (candidates.length > 0) {
                await this._tryFire('apphost_detected');
            }
        }
        catch {
            // AppHost discovery can fail when the CLI is missing or the
            // workspace doesn't have an Aspire project — those failures are
            // not interesting for engagement telemetry.
        }
    }

    private async _tryFire(trigger: EngagementTrigger): Promise<void> {
        if (this._fired) {
            return;
        }
        this._fired = true;

        // Collect a coarse snapshot of the workspace state at fire time. We
        // intentionally only fetch from already-running discovery — never
        // forcing a new discovery — to keep the event side-effect-free.
        const languageSummary = await this._safeLanguageSummary();
        const workspaceFolderCount = vscode.workspace.workspaceFolders?.length ?? 0;
        const hasCSharpDevKit = vscode.extensions.getExtension('ms-dotnettools.csdevkit') !== undefined;

        // Publish a small set of properties to be merged into every
        // subsequent event in this session.
        setCommonTelemetryProperties({
            apphost_languages: languageSummary,
            apphost_present: languageSummary === 'none' ? 'false' : 'true',
        });

        sendTelemetryEvent('engagement/active', {
            trigger,
            apphost_present: languageSummary === 'none' ? 'false' : 'true',
            apphost_languages: languageSummary,
            has_csharp_devkit: hasCSharpDevKit ? 'true' : 'false',
        }, {
            workspace_folders: workspaceFolderCount,
        });
    }

    private async _safeLanguageSummary(): Promise<ReturnType<typeof summarizeAppHostLanguages>> {
        const folders = vscode.workspace.workspaceFolders;
        if (!folders || folders.length === 0) {
            return 'none';
        }
        const all: import('./appHostDiscovery').CandidateAppHostDisplayInfo[] = [];
        for (const folder of folders) {
            try {
                const candidates = await this._appHostDiscoveryService.discover(folder);
                all.push(...candidates);
            }
            catch {
                // ignored; see _checkAppHostsForFolder
            }
        }
        return summarizeAppHostLanguages(all);
    }
}
