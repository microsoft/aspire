import * as vscode from 'vscode';

import {
    isWebDashboardUrl,
    resolveExplicitDashboardLaunchBehavior,
    showDashboardLaunchNotification,
    type DashboardBrowserType,
} from '../debugger/session/dashboardLauncher';
import { extensionLogOutputChannel } from '../utils/logging';
import { isCommandCancellation } from '../utils/telemetry';
import {
    type EditorUiHandoffDashboardResult,
    type EditorUiHandoffOperations,
    type EditorUiHandoffServiceDependencies,
} from './editorAssistanceToolContracts';
import { type ResolvedAppHostTarget } from './safeAppHostTargetResolver';

/**
 * Performs the editor-only side effects behind the handoff tools.
 *
 * Dashboard URLs remain confined to this service and the shared browser helpers. The
 * model-facing service receives only a finite presentation result, so login tokens can
 * never enter tool output, telemetry, or error text.
 */
export class EditorUiHandoffService implements EditorUiHandoffOperations {
    constructor(private readonly _dependencies: EditorUiHandoffServiceDependencies) {
    }

    async openDashboard(
        target: ResolvedAppHostTarget,
        token: vscode.CancellationToken): Promise<EditorUiHandoffDashboardResult> {
        try {
            throwIfCanceled(token);
            const appHosts = await this._dependencies.appHostRepository.fetchRunningAppHostsOnce(token);
            throwIfCanceled(token);
            if (!this._dependencies.targetResolver.isTargetCurrent(target)) {
                return { outcome: 'appHostNotRunning' };
            }

            const runningMatches: Array<(typeof appHosts)[number]> = [];
            for (const appHost of appHosts) {
                if (appHost.status?.toLowerCase() === 'stopped') {
                    continue;
                }

                const relation = this._dependencies.targetResolver.compareTargetToAppHostPath(target, appHost.appHostPath);
                if (relation === 'ambiguous') {
                    return { outcome: 'ambiguousAppHost' };
                }
                if (relation === 'same') {
                    runningMatches.push(appHost);
                }
            }

            if (runningMatches.length === 0) {
                return { outcome: 'appHostNotRunning' };
            }
            if (runningMatches.length > 1) {
                return { outcome: 'ambiguousAppHost' };
            }

            const runningAppHost = runningMatches[0];
            if (typeof runningAppHost.appHostPid === 'number' && !isProcessRunning(runningAppHost.appHostPid)) {
                return { outcome: 'appHostNotRunning' };
            }

            const dashboardUrl = runningAppHost.dashboardUrl;
            if (!dashboardUrl || !isWebDashboardUrl(dashboardUrl)) {
                return { outcome: 'dashboardUnavailable' };
            }

            const sessionOwners = this._dependencies.getAspireDebugSessionOwners();
            const cliPid = runningAppHost.cliPid;
            if (typeof cliPid !== 'number') {
                return { outcome: 'error' };
            }

            // External CLI rows do not carry a launch-time target identity. Require one editor
            // session whose captured identity and CLI PID both match the fresh repository row.
            const cliOwners = sessionOwners.filter(owner => owner.session.cliProcessId === cliPid);
            const matchingOwners = cliOwners.filter(owner => owner.appHostIdentity === target.identity);
            if (matchingOwners.length !== 1 || cliOwners.length !== 1) {
                return { outcome: 'error' };
            }

            const editorSession = matchingOwners[0].session;
            throwIfCanceled(token);
            if (editorSession.isShuttingDown) {
                return { outcome: 'error' };
            }
            if (!this._dependencies.targetResolver.isTargetCurrent(target)) {
                return { outcome: 'appHostNotRunning' };
            }

            const resolvedBehavior = resolveExplicitDashboardLaunchBehavior(
                vscode.workspace.getConfiguration('aspire', vscode.Uri.file(target.absolutePath)),
                editorSession.configuration.dashboardBrowser);
            throwIfCanceled(token);

            if (resolvedBehavior.behavior === 'notification') {
                const presented = await showDashboardLaunchNotification({
                    baseUrl: dashboardUrl,
                    source: resolvedBehavior.source,
                });
                return presented
                    ? { outcome: 'opened', presentation: 'notification' }
                    : { outcome: 'error' };
            }

            const browserType: DashboardBrowserType = resolvedBehavior.behavior;
            const presentation = await editorSession.openDashboard(dashboardUrl, browserType, true, token);
            return presentation
                ? { outcome: 'opened', presentation }
                : { outcome: 'error' };
        }
        catch (error) {
            if (isCommandCancellation(error) || token.isCancellationRequested) {
                throw new vscode.CancellationError();
            }

            // Browser errors can quote the full login-token URL. Keep this diagnostic
            // intentionally generic so this explicit path never writes the URL to logs.
            extensionLogOutputChannel.error('Aspire open Dashboard language model tool failed.');
            return { outcome: 'error' };
        }
    }

    async openOutput(token: vscode.CancellationToken): Promise<'opened' | 'error'> {
        try {
            throwIfCanceled(token);
            this._dependencies.output.show(true);
            return 'opened';
        }
        catch (error) {
            if (isCommandCancellation(error) || token.isCancellationRequested) {
                throw new vscode.CancellationError();
            }

            extensionLogOutputChannel.error('Aspire open Output language model tool failed.');
            return 'error';
        }
    }
}

function throwIfCanceled(token: vscode.CancellationToken): void {
    if (token.isCancellationRequested) {
        throw new vscode.CancellationError();
    }
}

function isProcessRunning(pid: number): boolean {
    try {
        // Signal 0 performs an existence/permission check without sending a signal.
        // https://nodejs.org/api/process.html#processkillpid-signal
        process.kill(pid, 0);
        return true;
    }
    catch (error) {
        return error instanceof Error && 'code' in error && error.code === 'EPERM';
    }
}
