import * as vscode from "vscode";
import os from "os";
import { extensionLogOutputChannel } from "../../utils/logging";
import { aspireDashboard, debugSessionStartTimedOut } from "../../loc/strings";
import { describeStopFailure, startStop, stopSessionInBackground } from "./stopHelpers";

export type DashboardLaunchBehavior = 'none' | 'notification' | DashboardBrowserType;
export type DashboardBrowserType = 'openExternalBrowser' | 'integratedBrowser' | 'debugChrome' | 'debugEdge' | 'debugFirefox';

/**
 * The slice of the owning Aspire debug session the dashboard launcher needs: the parent session it
 * matches and parents browser sessions against, the shutdown flags its guards read, and the shared
 * shutdown-budget primitives, so the launcher never gets a whole AspireDebugSession.
 */
export interface DashboardLauncherHost {
  readonly parentSession: vscode.DebugSession;
  readonly isDisposed: boolean;
  readonly isShuttingDown: boolean;
  readonly isStopAttemptInProgress: boolean;
  readonly isExtensionShutdownRequested: boolean;
  notifyStateChanged(): void;
  stopWithinBudget(operation: () => Thenable<void>, sessionName: string, deadline: number, onTimeout?: () => void): Promise<void>;
  waitWithinBudget(stop: PromiseLike<void>, sessionName: string, deadline: number, onTimeout?: () => void, timeoutMessage?: (sessionName: string, seconds: number) => string): Promise<void>;
}

export class DashboardLauncher implements vscode.Disposable {
  /**
   * Dashboard browsers are optional UI children. Give their launch/stop a smaller share of the
   * shutdown budget so a wedged browser adapter cannot starve AppHost and parent teardown.
   */
  private static readonly _dashboardStopTimeoutMs = 2000;

  private readonly _host: DashboardLauncherHost;

  private _dashboardDebugSession: vscode.DebugSession | null = null;
  private _dashboardStopPromise: Promise<void> | undefined;
  private _dashboardTerminationDisposable: vscode.Disposable | undefined;
  private _dashboardTerminationPromise: Promise<void> | undefined;
  private _resolveDashboardTermination: (() => void) | undefined;
  private readonly _pendingDashboardDebugSessionStarts = new Set<Promise<void>>();
  private _dashboardUrl: string | undefined;

  constructor(host: DashboardLauncherHost) {
    this._host = host;
  }

  get dashboardUrl(): string | undefined {
    return this._dashboardUrl;
  }

  /**
   * Whether the dashboard browser has yet to be asked to stop, including a launch that has not
   * produced its session yet.
   */
  get hasSessionsToStop(): boolean {
    return this._dashboardDebugSession !== null
      || this._pendingDashboardDebugSessionStarts.size > 0;
  }

  /**
   * Opens the dashboard URL in the specified browser.
   * For debugChrome/debugEdge/debugFirefox, launches as a child debug session that is stopped by
   * the ordered shutdown or by the late-start handler when shutdown is already in progress.
   */
  async openDashboard(url: string, browserType: DashboardBrowserType): Promise<void> {
    extensionLogOutputChannel.info(`Opening dashboard in browser: ${browserType}.`);

    if (this._host.isDisposed || this._host.isStopAttemptInProgress || this._host.isExtensionShutdownRequested) {
      extensionLogOutputChannel.info('Skipping dashboard browser launch because the Aspire session is shutting down.');
      return;
    }

    this._dashboardUrl = url;
    this._host.notifyStateChanged();

    switch (browserType) {
      case 'debugChrome':
        await this.launchDebugBrowser(url, 'pwa-chrome');
        break;

      case 'debugEdge':
        await this.launchDebugBrowser(url, 'pwa-msedge');
        break;

      case 'debugFirefox':
        await this.launchDebugBrowser(url, 'firefox');
        break;

      case 'integratedBrowser':
        await vscode.commands.executeCommand('simpleBrowser.show', url);
        break;

      case 'openExternalBrowser':
      default:
        // Use VS Code's default external browser handling
        await vscode.env.openExternal(vscode.Uri.parse(url));
        break;
    }
  }

  /**
   * Launches a browser as a child debug session.
   * VS Code does not stop this child session when the parent Aspire session terminates, so the
   * started session is tracked here and stopped explicitly during Aspire session shutdown.
   */
  private async launchDebugBrowser(url: string, debugType: 'pwa-chrome' | 'pwa-msedge' | 'firefox'): Promise<void> {
    const debugConfig: vscode.DebugConfiguration = {
      type: debugType,
      name: aspireDashboard,
      request: 'launch',
      url: url,
    };

    // Add type-specific options
    if (debugType === 'pwa-chrome' || debugType === 'pwa-msedge') {
      // Don't pause on entry for Chrome/Edge
      debugConfig.pauseForSourceMap = false;
    }
    else if (debugType === 'firefox') {
      // Firefox debugger requires webRoot; resolve to actual workspace path
      debugConfig.webRoot = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? os.tmpdir();
      debugConfig.pathMappings = [];
    }

    // Register listener before starting so we don't miss the event.
    // The started session must be matched to *this* Aspire session: concurrent Aspire
    // debug sessions all launch their dashboard with the same configuration name and
    // browser type, so name and type alone would let one session adopt (and later close)
    // another session's browser.
    const disposable = vscode.debug.onDidStartDebugSession((session) => {
      if (session.parentSession?.id === this._host.parentSession.id && session.configuration.name === aspireDashboard && session.type === debugType) {
        this._dashboardDebugSession = session;
        disposable.dispose();
        this.trackDashboardTermination(session);
        if (this._host.isShuttingDown) {
          this.closeDashboardInBackground();
        }
      }
    });

    let didStart: boolean;
    const start = Promise.resolve(vscode.debug.startDebugging(
      undefined,
      debugConfig,
      this._host.parentSession));
    const completion = start.then(() => undefined, () => undefined);
    this._pendingDashboardDebugSessionStarts.add(completion);
    try {
      // Start as a child debug session so it is stopped alongside this session in `dispose`.
      didStart = await start;
    }
    finally {
      this._pendingDashboardDebugSessionStarts.delete(completion);
    }

    if (!didStart) {
      disposable.dispose();
      extensionLogOutputChannel.warn(`Failed to start debug browser (${debugType}), falling back to default browser`);

      // Falling back after disposal would pop an untracked browser window open during
      // teardown, long after the user stopped the session.
      if (this._host.isShuttingDown) {
        return;
      }

      await vscode.env.openExternal(vscode.Uri.parse(url));
    }
  }

  /**
   * Closes the dashboard browser if closeDashboardOnDebugEnd is enabled.
   * Handles closing debug browser sessions.
   */
  private closeDashboard(): Promise<void> {
    const aspireConfig = vscode.workspace.getConfiguration('aspire');
    const shouldClose = aspireConfig.get<boolean>('closeDashboardOnDebugEnd', true);

    if (!shouldClose) {
      if (this._dashboardDebugSession) {
        this.clearDashboardDebugSession(this._dashboardDebugSession);
      }
      return Promise.resolve();
    }

    const dashboardDebugSession = this._dashboardDebugSession;
    if (!dashboardDebugSession) {
      return Promise.resolve();
    }

    if (this._dashboardStopPromise) {
      return this._dashboardStopPromise;
    }

    extensionLogOutputChannel.info('Closing dashboard browser...');
    const stopRequest = startStop(() => vscode.debug.stopDebugging(dashboardDebugSession));
    const stop = this._dashboardTerminationPromise
      ? Promise.race([stopRequest, this._dashboardTerminationPromise])
      : stopRequest;
    const attempt = stop.then(
      () => {
        this.clearDashboardDebugSession(dashboardDebugSession);
        if (this._dashboardStopPromise === attempt) {
          this._dashboardStopPromise = undefined;
        }
        extensionLogOutputChannel.info('Dashboard debug session stopped.');
      },
      err => {
        // A natural termination can race the stop request and remove the session before VS Code
        // settles the request. The termination event is authoritative: there is nothing left to
        // retry even if the stale stop request rejects.
        if (this._dashboardDebugSession !== dashboardDebugSession) {
          return;
        }
        if (this._dashboardStopPromise === attempt) {
          this._dashboardStopPromise = undefined;
        }
        throw err;
      });
    this._dashboardStopPromise = attempt;

    return attempt;
  }

  async stopDashboardWithinBudget(shutdownDeadline: number): Promise<void> {
    const deadline = Math.min(shutdownDeadline, Date.now() + DashboardLauncher._dashboardStopTimeoutMs);

    while (this._pendingDashboardDebugSessionStarts.size > 0) {
      const pendingStarts = [...this._pendingDashboardDebugSessionStarts];
      const results = await Promise.allSettled(pendingStarts.map(
        start => this._host.waitWithinBudget(
          start,
          aspireDashboard,
          deadline,
          undefined,
          debugSessionStartTimedOut)));
      for (let index = 0; index < results.length; index++) {
        if (results[index].status === 'rejected') {
          // A browser launch is optional UI work. Do not let a wedged launch block AppHost and
          // parent teardown; the start-event handler will close the browser if it appears later.
          this._pendingDashboardDebugSessionStarts.delete(pendingStarts[index]);
          extensionLogOutputChannel.warn(`Dashboard debug session launch did not settle before shutdown: ${describeStopFailure((results[index] as PromiseRejectedResult).reason)}`);
        }
      }
    }

    await this._host.stopWithinBudget(
      () => this.closeDashboard(),
      this._dashboardDebugSession?.name ?? aspireDashboard,
      deadline,
      () => { this._dashboardStopPromise = undefined; });
  }

  private trackDashboardTermination(session: vscode.DebugSession): void {
    this._dashboardTerminationDisposable?.dispose();
    this._dashboardTerminationPromise = new Promise<void>(resolve => {
      this._resolveDashboardTermination = resolve;
    });
    const disposable = vscode.debug.onDidTerminateDebugSession(terminatedSession => {
      if (terminatedSession.id === session.id) {
        this.clearDashboardDebugSession(session);
      }
    });
    this._dashboardTerminationDisposable = disposable;
  }

  private clearDashboardDebugSession(session: vscode.DebugSession): void {
    if (this._dashboardDebugSession !== session) {
      return;
    }

    this._resolveDashboardTermination?.();
    this._dashboardDebugSession = null;
    this._dashboardStopPromise = undefined;
    this._dashboardTerminationDisposable?.dispose();
    this._dashboardTerminationDisposable = undefined;
    this._dashboardTerminationPromise = undefined;
    this._resolveDashboardTermination = undefined;
  }

  private closeDashboardInBackground(): void {
    startStop(() => this.closeDashboard()).catch(err => {
      extensionLogOutputChannel.warn(`Failed to stop dashboard debug session: ${describeStopFailure(err)}`);

      // Once disposal has released this session from the extension context, no later caller can
      // retry a browser that arrived after the ordered shutdown's launch budget. Give that narrow
      // finalization race one fresh VS Code stop request before giving up.
      if (this._host.isDisposed && this._dashboardDebugSession) {
        stopSessionInBackground(() => this.closeDashboard(), 'dashboard debug session after finalization');
      }
    });
  }

  dispose(): void {
    // Normal teardown awaits this stop as part of stopAllSessions. Keep an idempotent background
    // fallback for direct finalization during extension shutdown.
    this.closeDashboardInBackground();
    this._dashboardTerminationDisposable?.dispose();
    this._dashboardTerminationDisposable = undefined;
  }
}
