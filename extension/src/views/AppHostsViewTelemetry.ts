import * as vscode from 'vscode';
import { AppHostDataRepository, isMatchingAppHostPath } from '../views/AppHostDataRepository';
import { sendTelemetryEvent } from '../utils/telemetry';

/**
 * Emits a telemetry event whenever the "AppHosts" tree view transitions from
 * hidden to visible. Reports the current view mode plus the count of running
 * AppHosts and the total resource count at the moment of the event, so we can
 * understand how the panel is actually being used.
 *
 * The visibility event fires on every transition; we debounce by a short
 * interval so that programmatic toggles (e.g. context-key flips that briefly
 * re-render) don't spam the pipeline.
 */
export class AppHostsViewTelemetry implements vscode.Disposable {
    private static readonly _debounceMs = 1000;

    private readonly _disposables: vscode.Disposable[] = [];
    private _timer: ReturnType<typeof setTimeout> | undefined;
    private _lastVisibility: boolean;

    constructor(
        private readonly _treeView: vscode.TreeView<unknown>,
        private readonly _dataRepository: AppHostDataRepository,
    ) {
        this._lastVisibility = this._treeView.visible;
        this._disposables.push(
            this._treeView.onDidChangeVisibility(e => this._onVisibilityChanged(e.visible))
        );

        // If the view is already visible at construction time (e.g. the user
        // had the Aspire panel pinned across sessions), fire on the next tick
        // so the data repository has had a chance to populate from the
        // initial polling cycle.
        if (this._treeView.visible) {
            this._schedule(true);
        }
    }

    dispose(): void {
        if (this._timer) {
            clearTimeout(this._timer);
            this._timer = undefined;
        }
        this._disposables.forEach(d => d.dispose());
    }

    private _onVisibilityChanged(visible: boolean): void {
        if (visible === this._lastVisibility) {
            return;
        }
        this._lastVisibility = visible;
        if (!visible) {
            if (this._timer) {
                clearTimeout(this._timer);
                this._timer = undefined;
            }
            return;
        }
        this._schedule(false);
    }

    private _schedule(initial: boolean): void {
        if (this._timer) {
            clearTimeout(this._timer);
        }
        this._timer = setTimeout(() => {
            this._timer = undefined;
            this._fire(initial);
        }, AppHostsViewTelemetry._debounceMs);
    }

    private _fire(initial: boolean): void {
        const appHosts = this._dataRepository.appHosts;
        const runningAppHosts = appHosts.length;
        const totalResources = this._getDisplayedResourceCount();
        sendTelemetryEvent('runningAppHostsView/shown', {
            view_mode: this._dataRepository.viewMode,
            initial_visibility: initial ? 'true' : 'false',
        }, {
            running_apphosts: runningAppHosts,
            total_resources: totalResources,
        });
    }

    private _getDisplayedResourceCount(): number {
        const appHosts = this._dataRepository.appHosts;
        const workspaceResources = this._dataRepository.workspaceResources;

        if (this._dataRepository.viewMode !== 'workspace' || workspaceResources.length === 0) {
            return appHosts.reduce((acc, host) => acc + (host.resources?.length ?? 0), 0);
        }

        const workspaceAppHostPath = this._dataRepository.workspaceAppHostPath;
        let countedWorkspaceResources = false;
        const totalResources = appHosts.reduce((acc, host) => {
            if (isMatchingAppHostPath(host.appHostPath, workspaceAppHostPath)) {
                countedWorkspaceResources = true;
                return acc + workspaceResources.length;
            }

            return acc + (host.resources?.length ?? 0);
        }, 0);

        return countedWorkspaceResources ? totalResources : totalResources + workspaceResources.length;
    }
}
