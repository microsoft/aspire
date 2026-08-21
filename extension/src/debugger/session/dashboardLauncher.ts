import * as vscode from "vscode";
import os from "os";
import { extensionLogOutputChannel } from "../../utils/logging";
import {
  aspireDashboard,
  codespacesLink,
  debugSessionStartTimedOut,
  directLink,
  openAspireDashboard,
  settingsLabel,
} from "../../loc/strings";
import { describeStopFailure, startStop, stopSessionInBackground } from "./stopHelpers";
import { normalizeLaunchFailure, type LaunchFailureMode, type SanitizedLaunchFailure } from "../../services/launchFailureJournal";

export type DashboardLaunchBehavior = 'none' | 'notification' | DashboardBrowserType;
export type DashboardBrowserType = 'openExternalBrowser' | 'integratedBrowser' | 'debugChrome' | 'debugEdge' | 'debugFirefox';
export type DashboardPresentation = 'integratedBrowser' | 'externalBrowser' | 'debugBrowser' | 'notification';
export type DashboardLaunchBehaviorSource = 'debugConfiguration' | 'globalConfiguration' | 'legacyConfiguration' | 'default';
export type ResolvedDashboardLaunchBehavior = {
  readonly behavior: DashboardLaunchBehavior;
  readonly source: DashboardLaunchBehaviorSource;
};

const dashboardLaunchIdConfigKey = '__aspireDashboardLaunchId';
const preOptInDefaultDashboardBrowser: DashboardLaunchBehavior = 'integratedBrowser';

export function normalizeDashboardLaunchBehavior(value: unknown): DashboardLaunchBehavior | undefined {
  return value === 'none'
    || value === 'notification'
    || value === 'openExternalBrowser'
    || value === 'integratedBrowser'
    || value === 'debugChrome'
    || value === 'debugEdge'
    || value === 'debugFirefox'
    ? value
    : undefined;
}

export function resolveDashboardLaunchBehavior(
  aspireConfig: vscode.WorkspaceConfiguration,
  debugConfigurationBehaviorValue?: unknown): ResolvedDashboardLaunchBehavior {
  const debugConfigurationBehavior = normalizeDashboardLaunchBehavior(debugConfigurationBehaviorValue);
  if (debugConfigurationBehavior) {
    return { behavior: debugConfigurationBehavior, source: 'debugConfiguration' };
  }

  const configuredGlobalBehavior = getConfiguredDashboardLaunchBehavior(aspireConfig);
  if (configuredGlobalBehavior === 'none' || configuredGlobalBehavior === 'notification') {
    return { behavior: configuredGlobalBehavior, source: 'globalConfiguration' };
  }

  // Migration precedence is intentionally conservative:
  // - per-launch `dashboardBrowser` always wins because it only affects this debug run;
  // - explicit global `none`/`notification` always wins so users can opt out or opt into the toast;
  // - legacy `notification`/`off` keeps the less intrusive historical behavior even if a new
  //   browser preference is also configured;
  // - legacy `launch` falls through to the new browser preference, or to the pinned pre-opt-in
  //   integrated-browser default when no new preference exists.
  const legacyBehavior = getConfiguredLegacyDashboardLaunchBehavior(aspireConfig);

  if (legacyBehavior) {
    if (legacyBehavior === 'notification' || legacyBehavior === 'none') {
      return { behavior: legacyBehavior, source: 'legacyConfiguration' };
    }

    return {
      behavior: configuredGlobalBehavior ?? preOptInDefaultDashboardBrowser,
      source: configuredGlobalBehavior ? 'globalConfiguration' : 'legacyConfiguration'
    };
  }

  if (configuredGlobalBehavior) {
    return { behavior: configuredGlobalBehavior, source: 'globalConfiguration' };
  }

  return {
    behavior: normalizeDashboardLaunchBehavior(aspireConfig.get<unknown>('dashboardBrowser', 'none')) ?? 'none',
    source: 'default'
  };
}

export function resolveExplicitDashboardLaunchBehavior(
  aspireConfig: vscode.WorkspaceConfiguration,
  debugConfigurationBehaviorValue?: unknown): {
    readonly behavior: Exclude<DashboardLaunchBehavior, 'none'>;
    readonly source: DashboardLaunchBehaviorSource;
  } {
  const debugPreference = normalizeDashboardLaunchBehavior(debugConfigurationBehaviorValue);
  if (debugPreference && debugPreference !== 'none') {
    return { behavior: debugPreference, source: 'debugConfiguration' };
  }

  const configuredPreference = getConfiguredDashboardLaunchBehavior(aspireConfig);
  if (configuredPreference && configuredPreference !== 'none') {
    return { behavior: configuredPreference, source: 'globalConfiguration' };
  }

  const legacyBehavior = getConfiguredLegacyDashboardLaunchBehavior(aspireConfig);
  if (legacyBehavior === 'notification') {
    return { behavior: 'notification', source: 'legacyConfiguration' };
  }

  // `none` suppresses automatic launch; it is not a browser presentation. Explicit
  // handoff still honors any separately configured browser or notification preference,
  // then falls back to the external browser rather than the integrated one: the model
  // asked to open the Dashboard right now, so a user who left the setting unset or
  // explicitly opted out of the automatic popup should still see a normal, full-featured
  // browser tab instead of the more constrained Simple Browser. This matches the existing
  // "open in browser" tree command, which always opens the Dashboard externally and never
  // routes through this resolver at all.
  //
  // `source` here is provenance, not a presentation selector: it names the most specific
  // layer the user actually configured and this fallback did not honor. The returned
  // behavior is the same fixed fallback for all four cases, so the source can never change
  // what is opened. Only a `notification` behavior feeds `source` into
  // `openDashboardLaunchBehaviorSettings`, and that case has already returned above with the
  // legacy setting that really selected it, so this fallback cannot send a user to a setting
  // that does not control what they just saw.
  return {
    behavior: 'openExternalBrowser',
    source: debugPreference === 'none'
      ? 'debugConfiguration'
      : configuredPreference === 'none'
        ? 'globalConfiguration'
        : legacyBehavior
          ? 'legacyConfiguration'
          : 'default',
  };
}

export function isWebDashboardUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  }
  catch {
    return false;
  }
}

export interface DashboardLaunchNotificationOptions {
  readonly baseUrl: string;
  readonly codespacesUrl?: string;
  readonly source: DashboardLaunchBehaviorSource;
  readonly delayMs?: number;
}

export async function showDashboardLaunchNotification(options: DashboardLaunchNotificationOptions): Promise<boolean> {
  if (options.delayMs && options.delayMs > 0) {
    await new Promise(resolve => setTimeout(resolve, options.delayMs));
  }

  const actions: vscode.MessageItem[] = [{ title: directLink }];
  if (options.codespacesUrl) {
    actions.push({ title: codespacesLink });
  }
  actions.push({ title: settingsLabel });

  let selection: Thenable<vscode.MessageItem | undefined>;
  try {
    selection = vscode.window.showInformationMessage(openAspireDashboard, ...actions);
  }
  catch {
    extensionLogOutputChannel.error('Failed to show the Aspire Dashboard notification.');
    return false;
  }

  const selectionPromise = Promise.resolve(selection);
  const initialState = await Promise.race([
    selectionPromise.then(
      selected => ({ kind: 'selected' as const, selected }),
      () => ({ kind: 'rejected' as const })),
    new Promise<{ kind: 'pending' }>(resolve =>
      setTimeout(() => resolve({ kind: 'pending' }), 0)),
  ]);

  if (initialState.kind === 'rejected') {
    extensionLogOutputChannel.error('Failed to show the Aspire Dashboard notification.');
    return false;
  }

  if (initialState.kind === 'selected') {
    void handleDashboardLaunchNotificationSelection(initialState.selected, options)
      .catch(() => extensionLogOutputChannel.error('Failed to handle the Aspire Dashboard notification.'));
    return true;
  }

  void selectionPromise
    .then(selected => handleDashboardLaunchNotificationSelection(selected, options))
    .catch(() => extensionLogOutputChannel.error('Failed to handle the Aspire Dashboard notification.'));
  return true;
}

async function handleDashboardLaunchNotificationSelection(
  selected: vscode.MessageItem | undefined,
  options: DashboardLaunchNotificationOptions): Promise<void> {
  if (!selected) {
    return;
  }

  extensionLogOutputChannel.info(`Selected action: ${selected.title}`);
  if (selected.title === directLink) {
    await openDashboardNotificationLink(options.baseUrl);
  }
  else if (selected.title === codespacesLink && options.codespacesUrl) {
    await openDashboardNotificationLink(options.codespacesUrl);
  }
  else if (selected.title === settingsLabel) {
    openDashboardLaunchBehaviorSettings(options.source);
  }
}

async function openDashboardNotificationLink(url: string): Promise<void> {
  try {
    await vscode.env.openExternal(vscode.Uri.parse(url));
  }
  catch {
    // The notification has already been presented successfully. Keep the launch result tied to that
    // presentation and avoid logging the URL or the raw error, either of which may contain credentials.
    extensionLogOutputChannel.error('Failed to open the selected Aspire Dashboard link.');
  }
}

export function openDashboardLaunchBehaviorSettings(source: DashboardLaunchBehaviorSource): void {
  let command: Thenable<unknown>;
  try {
    command = source === 'debugConfiguration'
      ? vscode.commands.executeCommand('workbench.action.debug.configure')
      : vscode.commands.executeCommand(
        'workbench.action.openSettings',
        source === 'legacyConfiguration'
          ? 'aspire.enableAspireDashboardAutoLaunch'
          : 'aspire.dashboardBrowser');
  }
  catch {
    extensionLogOutputChannel.error('Failed to open the Aspire Dashboard launch settings.');
    return;
  }

  void Promise.resolve(command).catch(
    () => extensionLogOutputChannel.error('Failed to open the Aspire Dashboard launch settings.'));
}

type DashboardDebugType = 'pwa-chrome' | 'pwa-msedge' | 'firefox';

interface DashboardBrowserOperations {
  readonly openIntegrated: () => Promise<boolean>;
  readonly openExternal: () => Promise<boolean>;
  readonly openDebug: (debugType: DashboardDebugType) => Promise<DashboardPresentation | undefined>;
}

async function openDashboardWithOperations(
  browserType: DashboardBrowserType,
  operations: DashboardBrowserOperations): Promise<DashboardPresentation | undefined> {
  switch (browserType) {
    case 'debugChrome':
      return operations.openDebug('pwa-chrome');
    case 'debugEdge':
      return operations.openDebug('pwa-msedge');
    case 'debugFirefox':
      return operations.openDebug('firefox');
    case 'integratedBrowser':
      return await operations.openIntegrated() ? 'integratedBrowser' : undefined;
    case 'openExternalBrowser':
    default:
      return await operations.openExternal() ? 'externalBrowser' : undefined;
  }
}

export async function openDashboardInBrowser(
  url: string,
  browserType: DashboardBrowserType): Promise<DashboardPresentation | undefined> {
  return openDashboardWithOperations(browserType, {
    openIntegrated: () => runDashboardOpenOperation(
      () => vscode.commands.executeCommand('simpleBrowser.show', url)),
    openExternal: () => runDashboardOpenOperation(
      () => vscode.env.openExternal(vscode.Uri.parse(url))),
    openDebug: async debugType => {
      const didStart = await vscode.debug.startDebugging(
        undefined,
        createDashboardDebugConfiguration(url, debugType));
      if (didStart) {
        return 'debugBrowser';
      }

      extensionLogOutputChannel.warn(`Failed to start debug browser (${debugType}), falling back to default browser`);
      return await runDashboardOpenOperation(
        () => vscode.env.openExternal(vscode.Uri.parse(url)))
        ? 'externalBrowser'
        : undefined;
    },
  });
}

function createDashboardDebugConfiguration(
  url: string,
  debugType: DashboardDebugType): vscode.DebugConfiguration {
  const debugConfig: vscode.DebugConfiguration = {
    type: debugType,
    name: aspireDashboard,
    request: 'launch',
    url,
  };

  if (debugType === 'pwa-chrome' || debugType === 'pwa-msedge') {
    debugConfig.pauseForSourceMap = false;
  }
  else {
    // Firefox requires a webRoot even though the Dashboard sources are not local.
    debugConfig.webRoot = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? os.tmpdir();
    debugConfig.pathMappings = [];
  }

  return debugConfig;
}

async function runDashboardOpenOperation(operation: () => Thenable<unknown>): Promise<boolean> {
  return await operation() !== false;
}

function getConfiguredLegacyDashboardLaunchBehavior(
  aspireConfig: vscode.WorkspaceConfiguration): 'launch' | 'notification' | 'none' | undefined {
  const inspection = aspireConfig.inspect<unknown>('enableAspireDashboardAutoLaunch');
  const configuredValue = inspection?.workspaceFolderValue
    ?? inspection?.workspaceValue
    ?? inspection?.globalValue;

  if (configuredValue === undefined) {
    return undefined;
  }
  if (configuredValue === true || configuredValue === 'launch') {
    return 'launch';
  }
  if (configuredValue === false || configuredValue === 'notification') {
    return 'notification';
  }
  if (configuredValue === 'off') {
    return 'none';
  }

  return undefined;
}

function getConfiguredDashboardLaunchBehavior(
  aspireConfig: vscode.WorkspaceConfiguration): DashboardLaunchBehavior | undefined {
  const inspection = aspireConfig.inspect<unknown>('dashboardBrowser');
  const configuredValue = inspection?.workspaceFolderValue
    ?? inspection?.workspaceValue
    ?? inspection?.globalValue;

  return normalizeDashboardLaunchBehavior(configuredValue);
}

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
  readonly dashboardLaunchFailureMode: LaunchFailureMode;
  notifyStateChanged(): void;
  recordDashboardLaunchFailure(failure: SanitizedLaunchFailure): void;
  stopWithinBudget(operation: () => Thenable<void>, sessionName: string, deadline: number, onTimeout?: () => void): Promise<void>;
  waitWithinBudget(stop: PromiseLike<void>, sessionName: string, deadline: number, onTimeout?: () => void, timeoutMessage?: (sessionName: string, seconds: number) => string): Promise<void>;
}

interface TrackedDashboardDebugSession {
  readonly session: vscode.DebugSession;
  readonly terminationDisposable: vscode.Disposable;
  readonly terminationPromise: Promise<void>;
  readonly resolveTermination: () => void;
  stopPromise?: Promise<void>;
}

export class DashboardLauncher implements vscode.Disposable {
  /**
   * Dashboard browsers are optional UI children. Give their launch/stop a smaller share of the
   * shutdown budget so a wedged browser adapter cannot starve AppHost and parent teardown.
   */
  private static readonly _dashboardStopTimeoutMs = 2000;

  private readonly _host: DashboardLauncherHost;

  private readonly _dashboardDebugSessions = new Map<string, TrackedDashboardDebugSession>();
  private readonly _pendingDashboardDebugSessionStarts = new Set<Promise<void>>();
  private _dashboardUrl: string | undefined;
  private _nextDashboardLaunchId = 0;

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
    return this._dashboardDebugSessions.size > 0
      || this._pendingDashboardDebugSessionStarts.size > 0;
  }

  /**
   * Opens the dashboard URL in the specified browser.
   * For debugChrome/debugEdge/debugFirefox, launches as a child debug session that is stopped by
   * the ordered shutdown or by the late-start handler when shutdown is already in progress.
   */
  async openDashboard(url: string, browserType: DashboardBrowserType): Promise<DashboardPresentation | undefined> {
    extensionLogOutputChannel.info(`Opening dashboard in browser: ${browserType}.`);

    if (this._host.isDisposed
      || this._host.isShuttingDown
      || this._host.isStopAttemptInProgress
      || this._host.isExtensionShutdownRequested) {
      extensionLogOutputChannel.info('Skipping dashboard browser launch because the Aspire session is shutting down.');
      return undefined;
    }

    this._dashboardUrl = url;
    this._host.notifyStateChanged();

    return openDashboardWithOperations(browserType, {
      openIntegrated: () => this.openDashboardCore(
        () => vscode.commands.executeCommand('simpleBrowser.show', url)),
      openExternal: () => this.openDashboardCore(
        () => vscode.env.openExternal(vscode.Uri.parse(url))),
      openDebug: debugType => this.launchDebugBrowser(url, debugType),
    });
  }

  /**
   * Launches a browser as a child debug session.
   * VS Code does not stop this child session when the parent Aspire session terminates, so the
   * started session is tracked here and stopped explicitly during Aspire session shutdown.
   */
  private async launchDebugBrowser(
    url: string,
    debugType: DashboardDebugType): Promise<DashboardPresentation | undefined> {
    const debugConfig = createDashboardDebugConfiguration(url, debugType);
    const launchId = ++this._nextDashboardLaunchId;
    debugConfig[dashboardLaunchIdConfigKey] = launchId;

    // Register listener before starting so we don't miss the event.
    // The started session must be matched to *this* Aspire session: concurrent Aspire
    // debug sessions all launch their dashboard with the same configuration name and
    // browser type, so name and type alone would let one session adopt (and later close)
    // another session's browser.
    const disposable = vscode.debug.onDidStartDebugSession((session) => {
      if (session.parentSession?.id === this._host.parentSession.id && session.configuration.name === aspireDashboard && session.type === debugType) {
        const startedLaunchId = session.configuration[dashboardLaunchIdConfigKey];
        if (startedLaunchId !== undefined && startedLaunchId !== launchId) {
          return;
        }
        if (this._dashboardDebugSessions.has(session.id)) {
          return;
        }

        disposable.dispose();
        this.trackDashboardDebugSession(session);
        if (this._host.isShuttingDown) {
          this.closeDashboardInBackground();
        }
      }
    });

    let didStart: boolean;
    let start: Promise<boolean>;
    try {
      start = Promise.resolve(vscode.debug.startDebugging(
        undefined,
        debugConfig,
        this._host.parentSession));
    }
    catch (error) {
      disposable.dispose();
      this.recordDashboardLaunchFailure(error);
      throw error;
    }
    const completion = start.then(() => undefined, () => undefined);
    this._pendingDashboardDebugSessionStarts.add(completion);
    try {
      // Start as a child debug session so it is stopped alongside this session in `dispose`.
      didStart = await start;
    }
    catch (error) {
      disposable.dispose();
      this.recordDashboardLaunchFailure(error);
      throw error;
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
        return undefined;
      }

      return await this.openDashboardCore(() => vscode.env.openExternal(vscode.Uri.parse(url)))
        ? 'externalBrowser'
        : undefined;
    }

    return 'debugBrowser';
  }

  private async openDashboardCore(operation: () => Thenable<unknown>): Promise<boolean> {
    try {
      const result = await operation();
      if (result === false) {
        this.recordDashboardLaunchFailure();
        return false;
      }

      return true;
    }
    catch (error) {
      this.recordDashboardLaunchFailure(error);
      throw error;
    }
  }

  private recordDashboardLaunchFailure(error?: unknown): void {
    if (this._host.isShuttingDown) {
      return;
    }

    this._host.recordDashboardLaunchFailure(normalizeLaunchFailure({
      stage: 'dashboard',
      category: error === undefined ? 'unknown' : undefined,
      controller: 'editor',
      mode: this._host.dashboardLaunchFailureMode,
      providerKind: 'browser',
      error,
    }));
  }

  /**
   * Closes the dashboard browser if closeDashboardOnDebugEnd is enabled.
   * Handles closing debug browser sessions.
   */
  private closeDashboard(): Promise<void> {
    const aspireConfig = vscode.workspace.getConfiguration('aspire');
    const shouldClose = aspireConfig.get<boolean>('closeDashboardOnDebugEnd', true);
    const dashboardDebugSessions = [...this._dashboardDebugSessions.values()];

    if (!shouldClose) {
      for (const tracked of dashboardDebugSessions) {
        this.clearDashboardDebugSession(tracked.session);
      }
      return Promise.resolve();
    }

    if (dashboardDebugSessions.length === 0) {
      return Promise.resolve();
    }

    extensionLogOutputChannel.info('Closing dashboard browser...');
    return Promise.allSettled(
      dashboardDebugSessions.map(tracked => this.closeDashboardDebugSession(tracked)))
      .then(results => {
        const failures = results
          .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
          .map(result => result.reason);
        if (failures.length > 0) {
          throw failures[0];
        }
      });
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
      this._dashboardDebugSessions.values().next().value?.session.name ?? aspireDashboard,
      deadline,
      () => this.resetDashboardStopAttempts());
  }

  private closeDashboardDebugSession(tracked: TrackedDashboardDebugSession): Promise<void> {
    if (this._dashboardDebugSessions.get(tracked.session.id) !== tracked) {
      return Promise.resolve();
    }
    if (tracked.stopPromise) {
      return tracked.stopPromise;
    }

    const stopRequest = startStop(() => vscode.debug.stopDebugging(tracked.session));
    const attempt = Promise.race([stopRequest, tracked.terminationPromise]).then(
      () => {
        this.clearDashboardDebugSession(tracked.session);
        if (tracked.stopPromise === attempt) {
          tracked.stopPromise = undefined;
        }
        extensionLogOutputChannel.info('Dashboard debug session stopped.');
      },
      err => {
        // A natural termination can race the stop request and remove the session before VS Code
        // settles the request. The termination event is authoritative: there is nothing left to
        // retry even if the stale stop request rejects.
        if (this._dashboardDebugSessions.get(tracked.session.id) !== tracked) {
          return;
        }
        if (tracked.stopPromise === attempt) {
          tracked.stopPromise = undefined;
        }
        throw err;
      });
    tracked.stopPromise = attempt;

    return attempt;
  }

  private trackDashboardDebugSession(session: vscode.DebugSession): void {
    let resolveTermination!: () => void;
    const terminationPromise = new Promise<void>(resolve => {
      resolveTermination = resolve;
    });
    const disposable = vscode.debug.onDidTerminateDebugSession(terminatedSession => {
      if (terminatedSession.id === session.id) {
        this.clearDashboardDebugSession(session);
      }
    });
    this._dashboardDebugSessions.set(session.id, {
      session,
      terminationDisposable: disposable,
      terminationPromise,
      resolveTermination,
    });
  }

  private clearDashboardDebugSession(session: vscode.DebugSession): void {
    const tracked = this._dashboardDebugSessions.get(session.id);
    if (!tracked || tracked.session !== session) {
      return;
    }

    tracked.resolveTermination();
    tracked.stopPromise = undefined;
    tracked.terminationDisposable.dispose();
    this._dashboardDebugSessions.delete(session.id);
  }

  private resetDashboardStopAttempts(): void {
    for (const tracked of this._dashboardDebugSessions.values()) {
      tracked.stopPromise = undefined;
    }
  }

  private closeDashboardInBackground(): void {
    startStop(() => this.closeDashboard()).catch(err => {
      extensionLogOutputChannel.warn(`Failed to stop dashboard debug session: ${describeStopFailure(err)}`);

      // Once disposal has released this session from the extension context, no later caller can
      // retry a browser that arrived after the ordered shutdown's launch budget. Give that narrow
      // finalization race one fresh VS Code stop request before giving up.
      if (this._host.isDisposed && this._dashboardDebugSessions.size > 0) {
        stopSessionInBackground(() => this.closeDashboard(), 'dashboard debug session after finalization');
      }
    });
  }

  dispose(): void {
    // Normal teardown awaits this stop as part of stopAllSessions. Keep an idempotent background
    // fallback for direct finalization during extension shutdown.
    this.closeDashboardInBackground();
  }
}
