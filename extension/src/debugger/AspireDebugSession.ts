import * as vscode from "vscode";
import { EventEmitter } from "vscode";
import { promises as fs } from "fs";
import { createDebugAdapterTracker, AppHostOutputHandler, AppHostRestartHandler } from "./adapterTracker";
import { AspireResourceExtendedDebugConfiguration, AspireResourceDebugSession, EnvVar, AspireExtendedDebugConfiguration, NodeLaunchConfiguration, ProcessRestartedNotification, ProjectLaunchConfiguration, SessionTerminatedNotification, StartAppHostOptions, AspireOperationKind } from "../dcp/types";
import { extensionLogOutputChannel } from "../utils/logging";
import AspireDcpServer, { generateDcpIdPrefix } from "../dcp/AspireDcpServer";
import { spawnCliProcess, terminateCliProcess } from "./languages/cli";
import { disconnectingFromSession, launchingWithAppHost, launchingWithDirectory, processExceptionOccurred, processExitedWithCode, aspireDashboard, appHostSessionTerminated, debugSessionsFailedToStop, debugSessionStartTimedOut, debugSessionStopTimedOut } from "../loc/strings";
import { projectDebuggerExtension } from "./languages/dotnet";
import { AnsiColors } from "../utils/AspireTerminalProvider";
import { applyTextStyle } from "../utils/strings";
import { nodeDebuggerExtension } from "./languages/node";
import { cleanupRun } from "./runCleanupRegistry";
import { runWithRunStartWrappers } from "./runStartRegistry";
import AspireRpcServer from "../server/AspireRpcServer";
import { AlreadyStartedResourceDebugSession, createDebugSessionConfiguration } from "./debuggerExtensions";
import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";
import { ICliRpcClient } from "../server/rpcClient";
import path from "path";
import os from "os";
import { EnvironmentVariables } from "../utils/environment";
import type { ChildProcessWithoutNullStreams } from "child_process";
import { sendTelemetryEvent } from "../utils/telemetry";
import { classifyAppHostPath, classifyAppHostDirectory } from "../utils/appHostLanguage";
import { bucketAspireCommand } from "../utils/telemetryBuckets";
import { getAppHostTargetVersion } from "../utils/appHostTargetVersion";
import type { AspireDebugConsoleOutputEvent } from "../types/extensionApi";
import { appHostRestartSourceSessionIdConfigKey, appHostSelectionOriginConfigKey, appHostTelemetryTargetPathConfigKey } from "./AspireDebugConfigurationMetadata";

export type DashboardLaunchBehavior = 'none' | 'notification' | DashboardBrowserType;
export type DashboardBrowserType = 'openExternalBrowser' | 'integratedBrowser' | 'debugChrome' | 'debugEdge' | 'debugFirefox';
export type AppHostDebugSessionTracker = (owner: AspireDebugSession, appHostPath: string, debugSession: AspireResourceDebugSession) => void;

function getOperationKind(value: unknown): AspireOperationKind {
  if (value === undefined || value === null) {
    return 'run';
  }

  return value === 'run' || value === 'deploy' || value === 'publish' || value === 'do'
    ? value
    : 'unknown';
}

export function getLoggableDebugConfiguration(debugConfig: AspireResourceExtendedDebugConfiguration, includeEnvironment: boolean): vscode.DebugConfiguration {
  if (includeEnvironment && debugConfig.type !== 'maui') {
    return debugConfig;
  }

  if (includeEnvironment) {
    return {
      ...debugConfig,
      environmentVariables: debugConfig.environmentVariables ? '<redacted>' : undefined,
    };
  }

  return {
    ...debugConfig,
    env: debugConfig.env ? '<redacted>' : undefined,
    environmentVariables: debugConfig.environmentVariables ? '<redacted>' : undefined,
    msbuildProperties: debugConfig.msbuildProperties instanceof Map ? Object.fromEntries(debugConfig.msbuildProperties) : debugConfig.msbuildProperties,
  };
}

export class AspireDebugSession implements vscode.DebugAdapter {
  private static readonly _mauiDebugStartMaxAttempts = 3;
  private static readonly _mauiDebugStartRetryDelayMs = 5000;
  /**
   * Wall-clock budget for the whole ordered shutdown, covering the resource stops, the AppHost stop
   * and the Aspire parent stop together.
   *
   * The shutdown is reachable from `AppDomain.CurrentDomain.ProcessExit` in the CLI
   * (`ExtensionBackchannel.StopDebuggingAsync`), which blocks the exiting process on the RPC call
   * with `CancellationToken.None`. `vscode.debug.stopDebugging()` resolves only once the adapter
   * acknowledges, so a single wedged debug adapter would otherwise hang the CLI's exit forever.
   * A budget shared by all three phases keeps that exit bounded no matter how many resources are
   * involved, where per-session timeouts would scale the worst case with the resource count.
   */
  private static readonly _stopSessionsTimeoutMs = 10000;
   /**
    * Dashboard browsers are optional UI children. Give their launch/stop a smaller share of the
    * shutdown budget so a wedged browser adapter cannot starve AppHost and parent teardown.
    */
   private static readonly _dashboardStopTimeoutMs = 2000;
  /**
   * How long the cooperative `stopCli` RPC has to bring the CLI down before its process group is
   * signalled. Long enough for the CLI to stop containers and other resources cleanly, short
   * enough that a wedged CLI does not keep the AppHost alive indefinitely.
   */
  private static readonly _cliCooperativeStopGraceMs = 10_000;
  private readonly _onDidSendMessage = new EventEmitter<any>();
  private readonly _onDidSendDebugConsoleOutput = new EventEmitter<AspireDebugConsoleOutputEvent>();
  private _messageSeq = 1;
  private readonly _appHostParentOutputFilter = new AppHostParentOutputFilter();

  private readonly _session: vscode.DebugSession;
  private readonly _rpcServer: AspireRpcServer;
  private readonly _dcpServer: AspireDcpServer;
  private readonly _terminalProvider: AspireTerminalProvider;
  private readonly _trackAppHostDebugSession: AppHostDebugSessionTracker;
  private readonly _removeAspireDebugSession: (session: AspireDebugSession) => void;

  private _appHostDebugSession?: AspireResourceDebugSession = undefined;
  private _resourceDebugSessions: AspireResourceDebugSession[] = [];
  private _trackedDebugAdapters: string[] = [];
  private _rpcClient?: ICliRpcClient;
  private _dashboardDebugSession: vscode.DebugSession | null = null;
  private _dashboardStopPromise: Promise<void> | undefined;
  private _dashboardTerminationDisposable: vscode.Disposable | undefined;
  private _dashboardTerminationPromise: Promise<void> | undefined;
  private _resolveDashboardTermination: (() => void) | undefined;
  private readonly _pendingDashboardDebugSessionStarts = new Set<Promise<void>>();
  private _dashboardUrl: string | undefined;
  private _startupCompleted = false;
  private readonly _onDidChangeState = new EventEmitter<void>();
  private readonly _disposables: vscode.Disposable[] = [];
  private _disposed = false;
  // Set as soon as stopDebugging() begins, before it snapshots the resource sessions. Resource
  // starts that land during the shutdown's awaits must not be registered as ordinary sessions:
  // they would miss the snapshot and only be stopped later by dispose(), after the AppHost and
  // the Aspire parent had already been stopped, which is the orphaned-resource ordering the
  // shutdown exists to prevent.
  private _stopping = false;
  // The in-flight (or successfully completed) ordered shutdown, so overlapping stop requests share
  // one. Cleared again if that attempt rejects - see stopDebugging().
  private _stopPromise: Promise<void> | undefined;
  // Unlike _stopping, this is cleared between failed attempts. Late resources use the distinction
  // to join an active shutdown or remain queued for the next retry.
  private _stopAttemptInProgress = false;
  // A resource can finish starting after stopAllSessions() snapshots the ordinary resource list.
  // Start its stop immediately, then let the active shutdown await and report the same promise.
  private readonly _lateResourceStops: {
    session: AspireResourceDebugSession;
    stop: Promise<void>;
    retryOnNextShutdown: boolean;
  }[] = [];
  // Publish debug-session start ownership before preparing or invoking VS Code. Start events can
  // arrive after the ordinary session snapshot, so shutdown must wait for every accepted resource
  // start to either register normally or enqueue its late stop before the final drain can complete.
  private readonly _pendingDebugSessionStarts = new Set<{
    name: string;
    completion: Promise<void>;
  }>();
  // Set once the AppHost stop has been confirmed, so a retry after a failed shutdown does not stop
  // it a second time. Kept separate from _appHostDebugSession, which the terminate handler still
  // needs to identify the session.
  private _appHostStopped = false;
  private _parentStopped = false;
  private _removedFromExtensionContext = false;
  private _cliStopPromise: Promise<void> | undefined;
  private _pendingCliStopWithoutRpcClient: { resolve: () => void; reject: (reason: unknown) => void } | undefined;
  private _stopCliWhenRpcClientConnects: ((client: ICliRpcClient) => void) | undefined;
  private _cliProcess: ChildProcessWithoutNullStreams | undefined;
  private _cliTerminationTimer: ReturnType<typeof setTimeout> | undefined;
  private _cliProcessTreeTerminationAttempted = false;
  private _extensionShutdownRequested = false;
  // Timestamp for the `debug/apphost/end` duration measurement. Captured the first
  // time we observe a `launch` request so it covers the actual user-visible session
  // lifetime, not the moment the AspireDebugSession object was constructed.
  private _appHostStartTimeMs: number | undefined = undefined;
  // Tracks the AppHost-language classification of the launched program so it can
  // be repeated on the matching end event without re-deriving from `configuration`.
  private _appHostLanguageAtLaunch: 'csharp' | 'typescript' | 'unknown' = 'unknown';
  private _appHostLanguageAtLaunchPromise: Promise<'csharp' | 'typescript' | 'unknown'> | undefined = undefined;
  // Resolving telemetry metadata can require project/config reads, so the launch
  // path starts the work in the background and reuses the same result for start/end telemetry.
  private _appHostTargetVersionAtLaunch = 'unknown';
  private _appHostTargetVersionAtLaunchPromise: Promise<string> | undefined = undefined;
  private _appHostIsDirectoryAtLaunch: 'true' | 'false' | 'unknown' = 'unknown';
  // Mode the AppHost was launched with (`run` | `debug`) — captured for the
  // matching end event.
  private _appHostModeAtLaunch: 'run' | 'debug' = 'run';

  public readonly onDidSendMessage = this._onDidSendMessage.event;
  public readonly onDidSendDebugConsoleOutput = this._onDidSendDebugConsoleOutput.event;
  public readonly onDidChangeState = this._onDidChangeState.event;
  public readonly debugSessionId: string;
  public readonly operationKind: AspireOperationKind;
  public configuration: AspireExtendedDebugConfiguration;

  get appHostPath(): string | undefined {
    return typeof this.configuration.program === 'string' ? this.configuration.program : undefined;
  }

  /**
   * The AppHost the debug configuration provider resolved for this session when
   * `program` points at a workspace folder instead of a file. It is only populated when
   * the folder maps to a single unambiguous candidate, so consumers can treat it as an
   * exact identity rather than a guess.
   */
  get resolvedAppHostPath(): string | undefined {
    const resolvedPath = this.configuration[appHostTelemetryTargetPathConfigKey];
    return typeof resolvedPath === 'string' ? resolvedPath : undefined;
  }

  get dashboardUrl(): string | undefined {
    return this._dashboardUrl;
  }

  get startupCompleted(): boolean {
    return this._startupCompleted;
  }

  get isDisposed(): boolean {
    return this._disposed;
  }

  get cliProcessId(): number | undefined {
    return this._cliProcess?.pid;
  }

  constructor(session: vscode.DebugSession, rpcServer: AspireRpcServer, dcpServer: AspireDcpServer, terminalProvider: AspireTerminalProvider, removeAspireDebugSession: (session: AspireDebugSession) => void, trackAppHostDebugSession: AppHostDebugSessionTracker = () => { }, debugSessionId: string = generateDcpIdPrefix(), operationKind?: AspireOperationKind) {
    this._session = session;
    this._rpcServer = rpcServer;
    this._dcpServer = dcpServer;
    this._terminalProvider = terminalProvider;
    this._trackAppHostDebugSession = trackAppHostDebugSession;
    this._removeAspireDebugSession = removeAspireDebugSession;
    this.configuration = session.configuration as AspireExtendedDebugConfiguration;
    this.operationKind = operationKind ?? getOperationKind(this.configuration.command);

    this.debugSessionId = debugSessionId;
  }

  /**
   * Records a parent termination observed by an external owner before it requests ordered cleanup.
   * The parent is already gone, so cleanup must stop only the remaining owned sessions.
   */
  recordParentDebugSessionTermination(): void {
    this._parentStopped = true;
  }

  /**
   * Performs the ordered shutdown of this Aspire session: the dashboard browser and resource
   * sessions, then the AppHost, then the synthetic Aspire parent, then disposal. Every caller that
   * wants a session stopped - the CLI's `stopDebugging` RPC endpoint on `InteractionService` and the
   * E2E state-file bridge - must await this method so stop failures reach the caller. Calling
   * `dispose()` starts the same bounded ordering in the background because the Disposable contract
   * cannot return its promise.
   */
  stopDebugging(): Promise<void> {
    // Single-flight. Two overlapping stop requests would otherwise both run the ordered shutdown,
    // stopping every session twice and handing the two callers different failure lists for the
    // same shutdown. Memoizing the whole operation gives every caller the one result, the same way
    // the parent stop is memoized.
    if (this._stopPromise) {
      return this._stopPromise;
    }

    // Nothing left to stop. A timed-out parent request can resolve after its failed shutdown skipped
    // cleanup but before the retry reaches here. Dispose idempotently so the session is removed from
    // its context without issuing another stop request.
    if (!this.hasSessionsToStop) {
      this.disposeCore();
      return Promise.resolve();
    }

    this._stopAttemptInProgress = true;
    this._stopping = true;
    let resolveAttempt!: () => void;
    let rejectAttempt!: (reason: unknown) => void;
    const attempt = new Promise<void>((resolve, reject) => {
      resolveAttempt = resolve;
      rejectAttempt = reject;
    });
    this._stopPromise = attempt;
    // Publish the shared promise before starting the core operation. Resource stop callbacks can
    // synchronously re-enter stopDebugging(), but the stops themselves must still start eagerly so
    // dispose() retains its existing synchronous initiation behavior.
    void this.stopDebuggingCore().then(resolveAttempt, rejectAttempt);

    // A rejected shutdown left something running. Caching that rejection forever would make every
    // later attempt rethrow the original failure without retrying, so drop it and let the next
    // caller try again against whatever is still up. Successful attempts stay cached, which is what
    // makes repeat calls idempotent. The handler also marks `attempt` as handled, so the rejection
    // this method returns to its caller cannot surface as an unhandled rejection when a caller
    // (such as the fire-and-forget DAP disconnect path) logs it through a different reference.
    void attempt.then(
      () => {
        if (this._stopPromise === attempt) {
          this._stopAttemptInProgress = false;
        }
      },
      () => {
        if (this._stopPromise === attempt) {
          this._stopAttemptInProgress = false;
          this._stopPromise = undefined;
        }
      });

    return attempt;
  }

  /**
   * Whether any session this Aspire session owns has yet to be asked to stop. Drives both the
   * post-disposal no-op and the retry-after-failure path in {@link stopDebugging}.
   */
  private get hasSessionsToStop(): boolean {
    return this._dashboardDebugSession !== null
      || this._pendingDashboardDebugSessionStarts.size > 0
      || this._pendingDebugSessionStarts.size > 0
      || this._resourceDebugSessions.length > 0
      || (this._appHostDebugSession !== undefined && !this._appHostStopped)
      || this._lateResourceStops.length > 0
      || !this._parentStopped;
  }

  private async stopDebuggingCore(): Promise<void> {
    const stopFailures = await this.stopAllSessions();

    if (stopFailures.length === 1) {
      throw stopFailures[0];
    }

    if (stopFailures.length > 1) {
      // More than one adapter failed, so no single reason describes the shutdown. AggregateError
      // keeps every reason instead of picking one and discarding the rest.
      //
      // The reasons go in the message as well as in `errors`. Nothing between here and the user
      // reads `errors`: the RPC boundary in interactionService.ts logs and shows `err.message`
      // alone, so a message of just "N debug sessions failed to stop." would report that something
      // failed while discarding what - the loss this shutdown exists to stop. The reason list is a
      // placeholder in the localized string rather than something appended to it, so a translation
      // controls where the list lands in the sentence.
      throw new AggregateError(
        stopFailures,
        debugSessionsFailedToStop(stopFailures.length, stopFailures.map(describeStopFailure).join('; ')));
    }

    // The context is also how the CLI finds this session for a retry. Run lifecycle cleanup only
    // after every owned stop succeeds; removing a failed session here would make the next RPC
    // request see no session and falsely report success.
    this.disposeCore();
  }

  /**
   * Stops every session this Aspire session owns and returns the reasons of the ones that failed
   * instead of throwing, so the caller can finish the shutdown before surfacing them.
   */
  private async stopAllSessions(): Promise<unknown[]> {
    // One deadline for the whole shutdown rather than one per stop, so the worst case does not grow
    // with the number of resources. See _stopSessionsTimeoutMs for why this has to be bounded.
    const deadline = Date.now() + AspireDebugSession._stopSessionsTimeoutMs;

    // A dashboard or resource launched under a debugger can keep the AppHost shutdown in flight
    // until its debug session exits. Stop those sessions before the AppHost to avoid waiting on a
    // process whose debugger would otherwise only be stopped later by disposal.
    const resourceDebugSessions = this._resourceDebugSessions.filter(session => session.id !== this._appHostDebugSession?.id);
    // Deliberately allSettled rather than Promise.all. Promise.all settles on the first rejection,
    // which would start the AppHost stop while the remaining resource stops were still in flight
    // and reintroduce the orphaned-resource behavior this ordering exists to prevent - on exactly
    // the path where a resource is most likely to be left behind. The rejection is kept and
    // rethrown after the AppHost and the synthetic Aspire parent have been stopped.
    const [dashboardResult, ...resourceResults] = await Promise.allSettled([
      this.stopDashboardWithinBudget(deadline),
      ...resourceDebugSessions.map(session => this.stopWithinBudget(
        () => session.stopSession(),
        session.session.name,
        deadline,
        () => session.resetStopSessionAttempt?.())),
    ]);
    const stopFailures: unknown[] = [dashboardResult, ...resourceResults]
      .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
      .map(result => result.reason);

    // Forget the sessions that confirmed a stop. A retry after a failed shutdown then targets only
    // what is still running, instead of calling stopSession() again on sessions VS Code has already
    // torn down.
    const unstoppedResourceSessions = new Set(resourceDebugSessions.filter((_, index) => resourceResults[index].status === 'rejected'));
    this._resourceDebugSessions = this._resourceDebugSessions.filter(
      session => unstoppedResourceSessions.has(session) || session.id === this._appHostDebugSession?.id);

    let pendingStartBudgetExhausted = await this.drainPendingDebugSessionStarts(deadline, stopFailures);
    await this.drainLateResourceStops(deadline, stopFailures);

    // Global/E2E stop requests target the synthetic Aspire session. Stop the real AppHost session
    // explicitly before the parent so we do not rely on VS Code cascading termination before the
    // AppHost registry refresh runs.
    if (this._appHostDebugSession && !this._appHostStopped) {
      const appHostDebugSession = this._appHostDebugSession;
      try {
        await this.stopWithinBudget(
          () => appHostDebugSession.stopSession(),
          appHostDebugSession.session.name,
          deadline,
          () => appHostDebugSession.resetStopSessionAttempt?.());
        this._appHostStopped = true;
        this._resourceDebugSessions = this._resourceDebugSessions.filter(session => session.id !== appHostDebugSession.id);
      }
      catch (err) {
        stopFailures.push(err);
      }
    }

    if (!pendingStartBudgetExhausted) {
      pendingStartBudgetExhausted = await this.drainPendingDebugSessionStarts(deadline, stopFailures);
    }
    await this.drainLateResourceStops(deadline, stopFailures);

    if (!this._parentStopped) {
      try {
        await this.stopWithinBudget(() => this.stopParentDebugSession(), this._session.name, deadline);
      }
      catch (err) {
        stopFailures.push(err);
      }
    }

    if (!pendingStartBudgetExhausted) {
      await this.drainPendingDebugSessionStarts(deadline, stopFailures);
    }
    await this.drainLateResourceStops(deadline, stopFailures);

    return stopFailures;
  }

  private async drainPendingDebugSessionStarts(deadline: number, stopFailures: unknown[]): Promise<boolean> {
    while (this._pendingDebugSessionStarts.size > 0) {
      const pendingStarts = [...this._pendingDebugSessionStarts];
      const results = await Promise.allSettled(pendingStarts.map(
        pendingStart => this.waitWithinBudget(
          pendingStart.completion,
          pendingStart.name,
          deadline,
          undefined,
          debugSessionStartTimedOut)));
      const failures = results
        .filter((result): result is PromiseRejectedResult => result.status === 'rejected')
        .map(result => result.reason);
      stopFailures.push(...failures);

      if (failures.length > 0) {
        return true;
      }
    }

    return false;
  }

  beginPendingDebugSessionStart(name: string): vscode.Disposable {
    let resolveCompletion!: () => void;
    const pendingStart = {
      name,
      completion: new Promise<void>(resolve => {
        resolveCompletion = resolve;
      }),
    };
    this._pendingDebugSessionStarts.add(pendingStart);

    return {
      dispose: () => {
        if (this._pendingDebugSessionStarts.delete(pendingStart)) {
          resolveCompletion();
        }
      },
    };
  }

  private stopLateResourceSession(session: AspireResourceDebugSession): void {
    const description = `late resource session ${session.session.name}`;
    if (this._disposed) {
      stopSessionInBackground(() => session.stopSession(), description);
      return;
    }

    const stop = startStop(() => session.stopSession());
    const retryOnNextShutdown = !this._stopAttemptInProgress;
    // Attach a handler immediately because an active shutdown may still be waiting on its original
    // snapshot, while a failed attempt has no caller left to observe a between-attempt stop.
    void stop.catch(err => {
      if (retryOnNextShutdown) {
        extensionLogOutputChannel.warn(`Failed to stop ${description}: ${describeStopFailure(err)}`);
      }
    });
    this._lateResourceStops.push({ session, stop, retryOnNextShutdown });
  }

  private async drainLateResourceStops(deadline: number, stopFailures: unknown[]): Promise<void> {
    while (this._lateResourceStops.length > 0) {
      const lateStops = this._lateResourceStops.splice(0);
      const results = await Promise.allSettled(lateStops.map(
        lateStop => this.stopLateResourceWithinBudget(lateStop, deadline)));

      results.forEach((result, index) => {
        if (result.status === 'fulfilled') {
          return;
        }

        stopFailures.push(result.reason);
        const failedSession = lateStops[index].session;
        if (!this._resourceDebugSessions.some(session => session.id === failedSession.id)) {
          this._resourceDebugSessions.push(failedSession);
        }
      });
    }
  }

  private async stopLateResourceWithinBudget(
    lateStop: (typeof this._lateResourceStops)[number],
    deadline: number): Promise<void> {
    try {
      await this.waitWithinBudget(
        lateStop.stop,
        lateStop.session.session.name,
        deadline,
        () => lateStop.session.resetStopSessionAttempt?.());
    }
    catch (err) {
      if (!lateStop.retryOnNextShutdown) {
        throw err;
      }

      // This stop was started after a prior shutdown had already failed. Its rejection had no
      // active owner, so the next ordered attempt must issue a fresh request rather than replay it.
      await this.stopWithinBudget(
        () => lateStop.session.stopSession(),
        lateStop.session.session.name,
        deadline,
        () => lateStop.session.resetStopSessionAttempt?.());
    }
  }

  requestCliStopForExtensionShutdown(): Promise<void> {
    this._extensionShutdownRequested = true;
    if (this._cliStopPromise) {
      return this._cliStopPromise;
    }

    if (!this._rpcClient) {
      const cliStopPromise = new Promise<void>((resolve, reject) => {
        this._pendingCliStopWithoutRpcClient = { resolve, reject };
        this._stopCliWhenRpcClientConnects = client => {
          client.stopCli().then(resolve, reject).finally(() => {
            this._pendingCliStopWithoutRpcClient = undefined;
          });
        };
      });

      return this.trackCliStopPromise(cliStopPromise);
    }

    return this.trackCliStopPromise(this._rpcClient.stopCli());
  }

  private trackCliStopPromise(cliStopPromise: Promise<void>): Promise<void> {
    this._cliStopPromise = cliStopPromise;
    void cliStopPromise.catch(() => {
      if (this._cliStopPromise === cliStopPromise) {
        this._cliStopPromise = undefined;
      }
    });

    return cliStopPromise;
  }

  /**
   * Signals the `aspire` CLI process tree.
   *
   * The cooperative `stopCli` RPC resolving proves only that the request was accepted, and on a
   * closed transport it resolves having done nothing at all. Neither outcome terminates the CLI,
   * so the process it owns — the AppHost and every resource process beneath it — has to be
   * signalled directly whenever the cooperative path did not finish the job. The leader may already
   * have exited by then, but that does not prove its descendants exited too.
   */
  terminateCliProcessTree(options?: { force?: boolean }): void {
    this.cancelScheduledCliProcessTermination();
    const cliProcess = this._cliProcess;
    if (!cliProcess) {
      return;
    }

    // A force sweep can run after the CLI leader has exited. Never aim another signal at that
    // recorded PID afterward: on Windows the PID may already have been recycled, and `taskkill /t`
    // would then target an unrelated process tree.
    if (this._cliProcessTreeTerminationAttempted) {
      return;
    }

    // Deliberately not skipped once the leader has exited. `terminateCliProcess` reaps the surviving
    // members of a managed process group in that case, and that is the only path that collects
    // AppHost and resource processes which outlived the CLI that owned them.
    this._cliProcessTreeTerminationAttempted = true;
    terminateCliProcess(cliProcess, `Aspire CLI for debug session ${this.debugSessionId}`, options);
    if (this._disposed) {
      this.releaseExtensionContextOwnership();
    }
  }

  private scheduleCliProcessTermination(): void {
    if (!this._cliProcess || this._cliTerminationTimer || this._cliProcessTreeTerminationAttempted) {
      return;
    }

    // Give the cooperative stop the first chance so the CLI can shut its resources down cleanly;
    // after this timer fires the session may be disposed and unowned except for the extension
    // context, so use the hard-kill path rather than scheduling another unref'd escalation.
    this._cliTerminationTimer = setTimeout(() => {
      this._cliTerminationTimer = undefined;
      this.terminateCliProcessTree({ force: true });
      this.releaseExtensionContextOwnership();
    }, AspireDebugSession._cliCooperativeStopGraceMs);
    this._cliTerminationTimer.unref?.();
  }

  private cancelScheduledCliProcessTermination(): void {
    if (this._cliTerminationTimer) {
      clearTimeout(this._cliTerminationTimer);
      this._cliTerminationTimer = undefined;
    }
  }

  /**
   * Permanently gives up signalling the recorded CLI process tree, without signalling it.
   *
   * Windows has no equivalent of the POSIX process group: `taskkill /pid <pid> /t` walks the live
   * process table to find children, so it can only reach descendants while the recorded PID still
   * names the running leader. Once that PID is released the same number can be assigned to an
   * unrelated process, and the sweep would then terminate that process and its children instead.
   *
   * Cancelling the pending timer is not enough on its own, because the disposable installed for the
   * CLI schedules a new one every time it runs. Marking the PID as spent is what makes every later
   * path — the scheduled escalation and any direct `terminateCliProcessTree` call — decline to aim
   * at it.
   */
  private abandonCliProcessTree(): void {
    this.cancelScheduledCliProcessTermination();
    this._cliProcessTreeTerminationAttempted = true;
    if (this._disposed) {
      this.releaseExtensionContextOwnership();
    }
  }

  private releaseExtensionContextOwnership(): void {
    if (this._removedFromExtensionContext) {
      return;
    }

    this._removedFromExtensionContext = true;
    this._removeAspireDebugSession(this);
  }

  private completePendingCliStopWithoutRpcClient(): void {
    this._stopCliWhenRpcClientConnects = undefined;
    const pendingStop = this._pendingCliStopWithoutRpcClient;
    this._pendingCliStopWithoutRpcClient = undefined;
    pendingStop?.resolve();
  }

  /**
   * Starts a stop and gives up waiting for it once `deadline` passes, rejecting with a message that
   * names the session.
   *
   * The stop request is always issued before the race, so a session whose adapter is slow to
   * acknowledge is still being torn down by VS Code after we stop waiting for it. Giving up does
   * mean the AppHost stop can begin while a wedged resource stop is outstanding, which is the one
   * case where the resource-before-AppHost ordering is not honoured. That is the deliberate
   * trade-off for the shutdown being reachable from the CLI's process-exit handler: an unbounded
   * wait there hangs the exiting process indefinitely. The timeout is reported through the same
   * `stopFailures` list as an ordinary rejection, so it is surfaced rather than swallowed.
   */
  private stopWithinBudget(
    operation: () => Thenable<void>,
    sessionName: string,
    deadline: number,
    onTimeout?: () => void): Promise<void> {
    return this.waitWithinBudget(startStop(operation), sessionName, deadline, onTimeout);
  }

  private waitWithinBudget(
    stop: PromiseLike<void>,
    sessionName: string,
    deadline: number,
    onTimeout?: () => void,
    timeoutMessage: (sessionName: string, seconds: number) => string = debugSessionStopTimedOut): Promise<void> {
    const remainingMs = Math.max(0, deadline - Date.now());
    const remainingSeconds = Math.ceil(remainingMs / 1000);

    return new Promise<void>((resolve, reject) => {
      const timer = setTimeout(
        () => {
          onTimeout?.();
          reject(new Error(timeoutMessage(sessionName, remainingSeconds)));
        },
        remainingMs);

      // Whichever way this settles the timer has to be cleared: an outstanding timer keeps the
      // event loop alive, and the shutdown runs while the extension host may be exiting.
      stop.then(
        value => {
          clearTimeout(timer);
          resolve(value);
        },
        err => {
          clearTimeout(timer);
          reject(err);
        });
    });
  }

  /**
   * True once the session has begun stopping, whether through {@link stopDebugging} or
   * {@link dispose}. Resource start paths use this to stop a resource that arrives too late to be
   * covered by the ordered shutdown, rather than registering it behind the snapshot where only
   * dispose() - after the AppHost and the Aspire parent - would reach it.
   */
  get isShuttingDown(): boolean {
    return this._disposed || this._stopping || this._extensionShutdownRequested;
  }

  /**
   * Runs the ordered shutdown for callers that cannot await it - DAP request handlers that owe VS
   * Code a prompt response, and process/session termination callbacks that return void. The
   * failures are logged rather than rethrown because there is no caller left to receive them, and
   * an unobserved rejection here would surface as an unhandled promise rejection in the extension
   * host.
   */
  private stopDebuggingInBackground(reason: string): void {
    startStop(() => this.stopDebugging()).catch(err => {
      extensionLogOutputChannel.error(`Ordered shutdown triggered by ${reason} failed: ${describeStopFailure(err)}`);

      // Ordered shutdown remains retryable, but a background failure must not leave the CLI and
      // AppHost process tree running indefinitely when no caller remains to perform cleanup.
      void this.requestCliStopForExtensionShutdown().catch(cliStopError => {
        extensionLogOutputChannel.warn(`Failed to stop Aspire CLI after debug-session shutdown failed: ${cliStopError}`);
      });
      this.scheduleCliProcessTermination();
    });
  }

  private async stopParentDebugSession(): Promise<void> {
    await vscode.debug.stopDebugging(this._session);
    // Promise creation only proves that VS Code accepted the request. Mark the parent stopped after
    // the request resolves so a timed-out pending attempt remains retryable.
    this._parentStopped = true;
  }

  handleMessage(message: any): void {
    if (message.command === 'initialize') {
      this.sendEvent({
        type: 'event',
        seq: this._messageSeq++,
        event: 'initialized',
        body: {}
      });

      this.sendResponse(message, {
        supportsConfigurationDoneRequest: true
      });
    }
    else if (message.command === 'launch') {
      this.sendEvent({
        type: 'response',
        request_seq: message.seq,
        seq: this._messageSeq++,
        success: true,
        command: 'launch',
        body: {}
      });

      void this.handleLaunchMessage(message);
    }
    else if (message.command === 'disconnect' || message.command === 'terminate') {
      this.sendMessageWithEmoji("🔌", disconnectingFromSession);
      // This is the dominant user Stop path - the red square on the debug toolbar, "Stop All
      // Sessions", and the window closing all arrive here - so it has to run the same ordered
      // shutdown as the CLI's stopDebugging RPC.
      //
      // Deliberately not awaited. The shutdown stops the synthetic Aspire parent, which makes VS
      // Code send this very `disconnect` request; awaiting it before responding would deadlock the
      // two against each other. The re-entrant call is safe because stopDebugging() is single-
      // flight: the second caller joins the in-flight shutdown instead of starting another.
      this.stopDebuggingInBackground('DAP disconnect/terminate');

      this.sendEvent({
        type: 'response',
        request_seq: message.seq,
        seq: this._messageSeq++,
        success: true,
        command: message.command,
        body: {}
      });
    }
    else if (message.command === 'setBreakpoints') {
      const breakpoints = Array.isArray(message.arguments?.breakpoints)
        ? message.arguments.breakpoints
        : [];

      this.sendResponse(message, {
        // The Aspire adapter does not bind user breakpoints itself, but VS Code still
        // sends breakpoint requests to every active debug session. The DAP response
        // must include a breakpoint array; otherwise newer VS Code builds throw while
        // reading the missing body.breakpoints field and can prevent child sessions
        // from receiving the same source breakpoints.
        breakpoints: breakpoints.map((breakpoint: { line?: number; column?: number }, index: number) => ({
          id: index + 1,
          verified: false,
          line: breakpoint.line,
          column: breakpoint.column,
        }))
      });
    }
    else if (message.command === 'setFunctionBreakpoints' || message.command === 'setDataBreakpoints') {
      this.sendResponse(message, { breakpoints: [] });
    }
    else if (message.command === 'setExceptionBreakpoints') {
      this.sendResponse(message, { breakpoints: [] });
    }
    else if (message.command) {
      // Respond to all other requests with a generic success
      this.sendEvent({
        type: 'response',
        request_seq: message.seq,
        seq: this._messageSeq++,
        success: true,
        command: message.command,
        body: {}
      });
    }

  }

  private async handleLaunchMessage(message: any): Promise<void> {
    const command = this.configuration.command ?? 'run';
    const noDebug = !!message.arguments?.noDebug && command === 'run';

    // Append any additional command args forwarded from the CLI (e.g., step name for 'do', unmatched tokens)
    const commandArgs = this.configuration.args ?? [];
    const appHostPath = this._session.configuration.program as string;
    const appHostTelemetryTargetPath = typeof this._session.configuration[appHostTelemetryTargetPathConfigKey] === 'string'
      ? this._session.configuration[appHostTelemetryTargetPathConfigKey]
      : undefined;
    const appHostSelectionOrigin = this.configuration[appHostSelectionOriginConfigKey];
    const extensionArgs: string[] = [];
    // Telemetry: emit `debug/apphost/start` once per AppHost launch. This must
    // happen before any awaited filesystem metadata work because child
    // `debug/runsession/start` events can arrive immediately after CLI spawn.
    // Values that need async enrichment are resolved in the background for the
    // matching end event instead of being reported as permanently-unknown start
    // dimensions.
    this._appHostStartTimeMs = Date.now();
    this._appHostModeAtLaunch = noDebug ? 'run' : 'debug';
    // Before the filesystem probe below, the file extension is the only language
    // signal available. Prefer the resolved telemetry target when a default
    // workspace launch already selected a concrete AppHost.
    const appHostTelemetryTargetLanguage = classifyAppHostPath(appHostTelemetryTargetPath);
    this._appHostLanguageAtLaunch = appHostTelemetryTargetLanguage !== 'unknown'
      ? appHostTelemetryTargetLanguage
      : classifyAppHostPath(appHostPath);
    this._appHostTargetVersionAtLaunch = 'unknown';
    this._appHostTargetVersionAtLaunchPromise = this.resolveAppHostTargetVersionAtLaunch(appHostTelemetryTargetPath ?? appHostPath);
    this._appHostIsDirectoryAtLaunch = 'unknown';
    sendTelemetryEvent('aspire/vscode/debug/apphost/start', {
      mode: this._appHostModeAtLaunch,
      apphost_language: this._appHostLanguageAtLaunch,
      command: bucketAspireCommand(command),
    });

    const appHostIsDirectory = await this.isDirectory(appHostPath);
    if (this.isShuttingDown) {
      extensionLogOutputChannel.info(`Skipping Aspire CLI launch for disposed or shutting-down debug session ${this.debugSessionId}.`);
      return;
    }

    this._appHostIsDirectoryAtLaunch = appHostIsDirectory ? 'true' : 'false';
    this._appHostLanguageAtLaunchPromise = this.resolveAppHostLanguageAtLaunch(appHostPath, appHostIsDirectory, appHostTelemetryTargetPath);

    // For 'do' with an explicit step (old CLI fallback), pass it as a positional argument
    const step = this.configuration.step;
    if (command === 'do' && step && commandArgs.length === 0) {
      extensionArgs.push(step);
    }

    // --start-debug-session tells the CLI to launch the AppHost via the extension with debugger attached
    if (!noDebug) {
      extensionArgs.push('--start-debug-session');
    }

    if (!commandArgs.includes('--nologo')) {
      extensionArgs.push('--nologo');
    }

    if (process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true') {
      extensionArgs.push('--cli-wait-for-debugger');
    }

    if (process.env[EnvironmentVariables.ASPIRE_APPHOST_STOP_ON_ENTRY] === 'true') {
      extensionArgs.push('--wait-for-debugger');
    }

    if (this._terminalProvider.isCliDebugLoggingEnabled()) {
      extensionArgs.push('--debug');
    }

    if (!appHostIsDirectory) {
      extensionArgs.push('--apphost', appHostPath);
    }

    const args = buildAspireCommandArgs(command, commandArgs, extensionArgs);
    const commandLabel = `aspire ${command}`;
    const sessionType = noDebug ? 'run' : 'debug';

    if (appHostIsDirectory) {
      this.sendMessageWithEmoji("📁", launchingWithDirectory(sessionType, appHostPath));

      void this.spawnAspireCommand(args, appHostPath, noDebug, commandLabel, this.getAppHostSelectionOriginEnvironment(appHostSelectionOrigin));
    }
    else {
      this.sendMessageWithEmoji("📂", launchingWithAppHost(sessionType, appHostPath));

      const workspaceFolder = path.dirname(appHostPath);
      void this.spawnAspireCommand(args, workspaceFolder, noDebug, commandLabel, this.getAppHostSelectionOriginEnvironment(appHostSelectionOrigin));
    }
  }

  private async isDirectory(pathToCheck: string): Promise<boolean> {
    try {
      return (await fs.stat(pathToCheck)).isDirectory();
    }
    catch {
      return false;
    }
  }

  private async resolveAppHostLanguageAtLaunch(appHostPath: string | undefined, appHostIsDirectory: boolean, appHostTelemetryTargetPath: string | undefined): Promise<'csharp' | 'typescript' | 'unknown'> {
    try {
      const telemetryTargetLanguage = classifyAppHostPath(appHostTelemetryTargetPath);
      this._appHostLanguageAtLaunch = telemetryTargetLanguage !== 'unknown'
        ? telemetryTargetLanguage
        : (appHostIsDirectory
          ? await classifyAppHostDirectory(appHostPath)
          : classifyAppHostPath(appHostPath));
    }
    catch {
      // Telemetry enrichment must never break or delay the debug launch path.
      this._appHostLanguageAtLaunch = 'unknown';
    }

    return this._appHostLanguageAtLaunch;
  }

  private async resolveAppHostTargetVersionAtLaunch(appHostPath: string | undefined): Promise<string> {
    try {
      this._appHostTargetVersionAtLaunch = await getAppHostTargetVersion(appHostPath) ?? 'unknown';
    }
    catch {
      // Telemetry enrichment must never break or delay the debug launch path.
      this._appHostTargetVersionAtLaunch = 'unknown';
    }

    return this._appHostTargetVersionAtLaunch;
  }

  private getAppHostSelectionOriginEnvironment(selectionOrigin: AspireExtendedDebugConfiguration[typeof appHostSelectionOriginConfigKey]): EnvVar[] | undefined {
    return selectionOrigin
      ? [{ name: EnvironmentVariables.ASPIRE_CLI_APPHOST_SELECTION_ORIGIN, value: selectionOrigin }]
      : undefined;
  }

  async spawnAspireCommand(args: string[], workingDirectory: string | undefined, noDebug: boolean, commandLabel: string = 'aspire run', internalEnv?: EnvVar[]) {
    const disposable = this._rpcServer.onNewConnection((client: ICliRpcClient) => {
      if (client.debugSessionId === this.debugSessionId) {
        this._rpcClient = client;
        disposable.dispose();
        this._stopCliWhenRpcClientConnects?.(client);
        this._stopCliWhenRpcClientConnects = undefined;
      }
    });

    const configuredEnv = this.configuration.env;
    const env = configuredEnv
      ? Object.entries(configuredEnv).map(([name, value]) => ({ name, value: String(value) }))
      : [];
    if (internalEnv) {
      env.push(...internalEnv);
    }

    // Per-stream line buffers. CLI stdio chunks aren't guaranteed to arrive aligned to line
    // boundaries; without buffering, partial lines (and split-point ANSI sequences) would be
    // emitted as their own debug-console events, producing broken output like a bare emoji on
    // one line followed by the rest of the message on the next.
    let stdoutBuffer = '';
    let stderrBuffer = '';

    const flushBuffer = (buffer: string, category: 'stdout' | 'stderr') => {
      const remainder = buffer.replace(/\r$/, '');
      if (remainder.length > 0 && !isProgressEscapeSequence(remainder)) {
        // Spectre's stderr is intentionally bare for non-error notifications (e.g. the version
        // update banner). The DAP `'stderr'` category alone causes the debug console to render
        // these lines in red; we don't add an extra `❌` because legitimate CLI errors are
        // already emoji-prefixed by Spectre at the source.
        this.sendMessage(remainder, true, category);
      }
    };

    const handleChunk = (chunk: string, currentBuffer: string, category: 'stdout' | 'stderr'): string => {
      const combined = currentBuffer + chunk;
      const lines = combined.split('\n');
      const partial = lines.pop() ?? '';
      for (const line of lines) {
        flushBuffer(line, category);
      }
      return partial;
    };

    const cliPath = await this._terminalProvider.getAspireCliExecutablePath();
    if (this.isShuttingDown) {
      // CLI resolution can outlive shutdown. Spawning now would create a detached `aspire run`
      // after every teardown owner has already started or completed its cleanup.
      extensionLogOutputChannel.info(`Skipping Aspire CLI launch for disposed or shutting-down debug session ${this.debugSessionId}.`);
      disposable.dispose();
      this.completePendingCliStopWithoutRpcClient();
      return;
    }

    this._cliProcess = spawnCliProcess(
      this._terminalProvider,
      cliPath,
      args,
      {
        stdoutCallback: (data) => {
          stdoutBuffer = handleChunk(data, stdoutBuffer, 'stdout');
        },
        stderrCallback: (data) => {
          stderrBuffer = handleChunk(data, stderrBuffer, 'stderr');
        },
        errorCallback: (error) => {
          extensionLogOutputChannel.error(`Error spawning aspire process: ${error}`);
          vscode.window.showErrorMessage(processExceptionOccurred(error.message, commandLabel));
        },
        exitCallback: (code) => {
          // A detached POSIX leader's descendants can keep the process group alive after the
          // leader exits, and the group id can be reused later, so collect that group immediately.
          // Windows taskkill needs the target PID to still identify a live process tree; after the
          // close event the CLI PID may already be reusable, so do not taskkill from this path.
          // `dispose()` below re-runs the CLI disposable, which would otherwise schedule a forced
          // sweep of that same spent PID once the grace period elapses, so retire it here instead
          // of only skipping the immediate call.
          if (process.platform !== 'win32') {
            this.terminateCliProcessTree({ force: true });
          }
          else {
            this.abandonCliProcessTree();
          }
          this.completePendingCliStopWithoutRpcClient();
          this._dcpServer.recordAppHostProcessExit(this.debugSessionId, code);
          // Flush any partial line left in either buffer so trailing output isn't lost.
          if (stdoutBuffer.length > 0) {
            flushBuffer(stdoutBuffer, 'stdout');
            stdoutBuffer = '';
          }
          if (stderrBuffer.length > 0) {
            flushBuffer(stderrBuffer, 'stderr');
            stderrBuffer = '';
          }
          this.sendMessageWithEmoji("🔚", processExitedWithCode(code ?? '?'));
          // The CLI process is gone but the resource debug sessions it asked us to start are not,
          // so run the ordered shutdown rather than dispose(), which would stop the AppHost entry
          // ahead of them.
          this.stopDebuggingInBackground('Aspire CLI process exit');
        },
        workingDirectory: workingDirectory,
        debugSessionId: this.debugSessionId,
        noDebug: noDebug,
        env: env.length > 0 ? env : undefined,
        // `aspire run` owns the AppHost and every resource process beneath it. Spawning this
        // long-lived CLI as a process-group leader is what lets `terminateCliProcess` signal the
        // whole tree by negative PID when the cooperative `stopCli` RPC does not finish the job.
        createProcessGroup: true,
      },
    );

    this._disposables.push({
      dispose: () => {
        void this.requestCliStopForExtensionShutdown().catch((err) => {
          extensionLogOutputChannel.info(`stopCli failed (connection may already be closed): ${err}`);
        });
        extensionLogOutputChannel.info(`Requested Aspire CLI exit with args: ${args.join(' ')}`);
        // `stopCli` is cooperative and cannot be the only stop mechanism: it resolves without
        // effect when the transport is already closed, and never settles when the CLI has stopped
        // servicing the connection. Escalate to signalling the process group once the CLI has had
        // a chance to exit on its own, so a CLI that ignores the request cannot outlive the
        // session and keep the AppHost and its resource processes alive.
        this.scheduleCliProcessTermination();
      }
    });

    function isProgressEscapeSequence(line: string): boolean {
      // ConEmu/iTerm2 progress-reporting OSC sequence (`OSC 9;4;<state>;<value> ST`).
      return /^\u001b\]9;4;\d+\u001b\\$/.test(line.trim());
    }
  }

  createDebugAdapterTrackerCore(debugAdapter: string, onAppHostRestartRequested?: AppHostRestartHandler, onAppHostOutput?: AppHostOutputHandler) {
    if (this._trackedDebugAdapters.includes(debugAdapter)) {
      return;
    }

    this._trackedDebugAdapters.push(debugAdapter);
    this._disposables.push(createDebugAdapterTracker(this._dcpServer, debugAdapter, onAppHostRestartRequested, onAppHostOutput));
  }

  private static readonly _nodeAppHostExtensions = ['.js', '.ts', '.mjs', '.mts', '.cjs', '.cts'];
  private static readonly _csharpAppHostExtensions = ['.cs', '.csproj'];

  private _appHostRestartRequested = false;
  private _preserveAppHostRestartSourceSessionId = false;

  async startAppHost(projectFile: string, args: string[], environment: EnvVar[], debug: boolean, options: StartAppHostOptions): Promise<void> {
    try {
      const fileExtension = path.extname(projectFile).toLowerCase();
      const isNodeAppHost = AspireDebugSession._nodeAppHostExtensions.includes(fileExtension);
      const isCSharpAppHost = AspireDebugSession._csharpAppHostExtensions.includes(fileExtension);

      const debuggerExtension = isNodeAppHost ? nodeDebuggerExtension : projectDebuggerExtension;

      // Register the adapter tracker with an app host restart handler.
      // When the user clicks "restart" on the app host child session,
      // we suppress VS Code's automatic child restart and restart the
      // entire Aspire debug session instead.
      //
      // The output filter is intentionally a positive opt-in for C# AppHosts only.
      // The .NET debugger (`coreclr`) emits a lot of `console`-category chatter
      // (module loads, exception-thrown notifications, the debugger banner, etc.)
      // into the parent debug console, and structured `Microsoft.Extensions.Logging`
      // lines need trce/dbug-level filtering. Other languages (Node, and future
      // additions like Python/Go) use different debug adapters that don't produce
      // that noise, so we pass their output through unmodified until/unless they
      // explicitly opt in to filtering.
      this.createDebugAdapterTrackerCore(
        debuggerExtension.debugAdapter,
        (debugSessionId) => {
          if (debugSessionId === this.debugSessionId) {
            this._appHostRestartRequested = true;
            this.configuration[appHostRestartSourceSessionIdConfigKey] = this._session.id;
            return true; // suppress VS Code's child restart
          }
          return false;
        },
        isCSharpAppHost
          ? (output, category) => this.sendAppHostMessage(output, category)
          : (output, category) => this.sendMessage(output, false, category === 'stderr' ? 'stderr' : 'stdout')
      );

      let appHostArgs: string[];
      let launchConfig;

      if (isNodeAppHost) {
        // The CLI prepends the runtime command (e.g., "npx") as args[0].
        // Extract it as the runtimeExecutable and use the rest as the actual args.
        const runtimeExecutable = args.length > 0 ? args[0] : undefined;
        appHostArgs = args.slice(1);
        launchConfig = {
          script_path: projectFile,
          working_directory: path.dirname(projectFile),
          type: 'node',
          ...(runtimeExecutable ? { runtime_executable: runtimeExecutable } : {})
        } as NodeLaunchConfiguration;
      }
      else {
        // The CLI sends the full dotnet CLI args (e.g., ["run", "--no-build", "--project", "...", "--", ...appHostArgs]).
        // Since we launch the apphost directly via the debugger (not via dotnet run), extract only the args after "--".
        const separatorIndex = args.indexOf('--');
        appHostArgs = separatorIndex >= 0 ? args.slice(separatorIndex + 1) : args;
        launchConfig = { project_path: projectFile, type: 'project' } as ProjectLaunchConfiguration;
      }

      extensionLogOutputChannel.info(`Starting AppHost for project: ${projectFile} with args: ${appHostArgs.join(' ')}`);

      const appHostDebugSessionConfiguration = await createDebugSessionConfiguration(
        this.configuration,
        launchConfig,
        appHostArgs,
        environment,
        { debug, forceBuild: isNodeAppHost ? false : options.forceBuild, runId: '', debugSessionId: this.debugSessionId, isApphost: true, debugSession: this },
        debuggerExtension);

      const appHostDebugSession = await this.startAndGetDebugSession(appHostDebugSessionConfiguration);

      if (!appHostDebugSession) {
        return;
      }

      this._appHostDebugSession = appHostDebugSession;
      this._trackAppHostDebugSession(this, projectFile, appHostDebugSession);

      const disposable = vscode.debug.onDidTerminateDebugSession(async session => {
        if (this._appHostDebugSession && session.id === this._appHostDebugSession.id) {
          this._appHostStopped = true;
          this._resourceDebugSessions = this._resourceDebugSessions.filter(resourceSession => resourceSession.id !== session.id);

          if (!this._appHostRestartRequested) {
            this.sendMessageWithEmoji("ℹ️", applyTextStyle(appHostSessionTerminated, AnsiColors.Yellow));
          }

          // Only restart the Aspire session when the user explicitly clicked
          // "restart" on the app host debug toolbar (detected via DAP tracker).
          // All other cases (user stop, process crash/exit) just dispose.
          const shouldRestart = this._appHostRestartRequested;
          const config = this.configuration;
          // The AppHost is already gone, but its resources are not: run the ordered shutdown so
          // they are stopped before the AppHost entry and the Aspire parent, and so a resource that
          // refuses to stop is reported rather than silently dropped by dispose().
          if (shouldRestart) {
            // The ordered shutdown finalizes this session before VS Code reuses its configuration
            // for the replacement. Keep the source ID long enough for the terminating parent to
            // suppress optimistic stopping without affecting the replacement session.
            this._preserveAppHostRestartSourceSessionId = true;
            // Awaited only on the restart path, so the replacement session is not started while the
            // outgoing one is still tearing its resources down. The wait is bounded by
            // _stopSessionsTimeoutMs.
            try {
              await this.stopDebugging();
            }
            catch (err) {
              this._preserveAppHostRestartSourceSessionId = false;
              delete config[appHostRestartSourceSessionIdConfigKey];
              extensionLogOutputChannel.error(`Ordered shutdown before AppHost restart failed: ${describeStopFailure(err)}`);
              void this.requestCliStopForExtensionShutdown().catch(cliStopError => {
                extensionLogOutputChannel.warn(`Failed to stop Aspire CLI after AppHost restart cleanup failed: ${cliStopError}`);
              });
              this.terminateCliProcessTree({ force: true });
              return;
            }

            extensionLogOutputChannel.info('AppHost restart requested, restarting Aspire debug session');
            await vscode.debug.startDebugging(undefined, config);
          }
          else {
            this.stopDebuggingInBackground('AppHost session termination');
          }
        }
      });

      this._disposables.push(disposable);
    }
    catch (err) {
      const errorMessage = err instanceof Error ? err.message : String(err);
      const errorDetails = err instanceof Error ? (err.stack ?? err.message) : String(err);
      extensionLogOutputChannel.error(`Error starting AppHost debug session: ${errorDetails}`);
      if (!isErrorWithStreamedDebugConsoleOutput(err)) {
        this.sendMessageWithEmoji("❌", errorDetails, true, 'stderr');
      }
      vscode.window.showErrorMessage(errorMessage);
      this.dispose();
    }
  }

  trackAlreadyStartedResourceSession(debugConfig: AspireResourceExtendedDebugConfiguration, resourceDebugSession: AlreadyStartedResourceDebugSession): AspireResourceDebugSession | undefined {
    // isShuttingDown rather than _disposed: an ordered shutdown snapshots the resource sessions
    // before its awaits, so a resource registered after that snapshot would only be stopped by the
    // dispose() that ends the shutdown - after the AppHost and the Aspire parent had already been
    // stopped, which is the orphaned-resource ordering the shutdown exists to prevent.
    if (this.isShuttingDown) {
      this.stopLateResourceSession(resourceDebugSession);
      return undefined;
    }

    if (debugConfig.debugSessionId === null) {
      extensionLogOutputChannel.warn(`Unable to report process start for run ${debugConfig.runId} because the DCP session ID is missing.`);
    }
    else {
      const notification: ProcessRestartedNotification = {
        notification_type: 'processRestarted',
        session_id: debugConfig.runId,
        dcp_id: debugConfig.debugSessionId,
        pid: resourceDebugSession.processId
      };

      this._dcpServer.sendNotification(notification);
    }

    void resourceDebugSession.termination.then(exitCode => {
      if (debugConfig.debugSessionId === null) {
        extensionLogOutputChannel.warn(`Unable to report termination for run ${debugConfig.runId} because the DCP session ID is missing.`);
        return;
      }

      const notification: SessionTerminatedNotification = {
        notification_type: 'sessionTerminated',
        session_id: debugConfig.runId,
        dcp_id: debugConfig.debugSessionId,
        exit_code: exitCode
      };

      this._dcpServer.sendNotification(notification);
    });

    this._resourceDebugSessions.push(resourceDebugSession);

    return resourceDebugSession;
  }

  startAndGetDebugSession(debugConfig: AspireResourceExtendedDebugConfiguration): Promise<AspireResourceDebugSession | undefined> {
    const pendingStart = this.beginPendingDebugSessionStart(debugConfig.name);
    const start = this.startAndGetDebugSessionCore(debugConfig);
    void start.then(() => pendingStart.dispose(), () => pendingStart.dispose());

    return start;
  }

  private startAndGetDebugSessionCore(debugConfig: AspireResourceExtendedDebugConfiguration): Promise<AspireResourceDebugSession | undefined> {
    return new Promise(async (resolve) => {
      const logConfig = getLoggableDebugConfiguration(debugConfig, this._terminalProvider.isDebugConfigEnvironmentLoggingEnabled());
      extensionLogOutputChannel.info(`Starting debug session with configuration: ${JSON.stringify(logConfig)}`);
      this.createDebugAdapterTrackerCore(debugConfig.type);

      let resolved = false;
      const disposable = vscode.debug.onDidStartDebugSession(session => {
        if (session.configuration.runId === debugConfig.runId) {
          extensionLogOutputChannel.info(`Debug session started: ${session.name} (run id: ${session.configuration.runId})`);
          disposable.dispose();

          let stopSessionPromise: Promise<void> | undefined;
          let runCleanedUp = false;
          let terminated = false;
          let resolveTermination: () => void;
          const termination = new Promise<void>(resolve => {
            resolveTermination = resolve;
          });
          const cleanupResource = () => {
            // Run any cleanup registered by resource-type extensions (e.g. func host for Azure Functions)
            if (!runCleanedUp) {
              cleanupRun(debugConfig.runId);
              runCleanedUp = true;
            }
          };
          const terminationDisposable = vscode.debug.onDidTerminateDebugSession(terminatedSession => {
            if (terminatedSession.id !== session.id) {
              return;
            }

            // A resource can exit after a failed ordered stop but before its retry. Remove it
            // synchronously so the retry does not stop a gone session, and resolve any in-flight
            // stop that is still waiting for VS Code to confirm the same termination.
            terminated = true;
            this._resourceDebugSessions = this._resourceDebugSessions.filter(resourceSession => resourceSession.id !== session.id);
            cleanupResource();
            resolveTermination();
            terminationDisposable.dispose();
          });
          this._disposables.push(terminationDisposable);
          const disposalFunction = () => {
            if (terminated) {
              return Promise.resolve();
            }

            if (stopSessionPromise) {
              return stopSessionPromise;
            }

            extensionLogOutputChannel.info(`Stopping debug session: ${session.name} (run id: ${session.configuration.runId})`);
            const stop = Promise.race([
              Promise.resolve(vscode.debug.stopDebugging(session)),
              termination,
            ]);
            stopSessionPromise = stop;

            // A rejected adapter stop leaves the resource running. Forget only that failed attempt
            // so the ordered shutdown can issue a fresh VS Code stop request on its next retry.
            void stop.catch(() => {
              if (stopSessionPromise === stop) {
                stopSessionPromise = undefined;
              }
            });

            cleanupResource();

            return stop;
          };
          const resetStopSessionAttempt = () => {
            stopSessionPromise = undefined;
          };

          const vsCodeDebugSession: AspireResourceDebugSession = {
            id: session.id,
            session: session,
            stopSession: disposalFunction,
            resetStopSessionAttempt,
          };

          if (this.isShuttingDown) {
            extensionLogOutputChannel.info(`Stopping debug session that started after Aspire session shutdown began: ${session.name} (run id: ${session.configuration.runId})`);
            this.stopLateResourceSession(vsCodeDebugSession);
            resolved = true;
            resolve(undefined);
            return;
          }

          this._resourceDebugSessions.push(vsCodeDebugSession);

          resolved = true;
          resolve(vsCodeDebugSession);
        }
      });

      let started = false;
      try {
        const workspaceFolder = this.getDebugSessionWorkspaceFolder(debugConfig);
        const maxAttempts = debugConfig.type === 'maui' ? AspireDebugSession._mauiDebugStartMaxAttempts : 1;
        for (let attempt = 1; attempt <= maxAttempts; attempt++) {
          // isShuttingDown rather than _disposed: _disposed is only set at the very end of an
          // ordered shutdown, so gating on it would let a MAUI launch keep retrying - up to two
          // 5s sleeps - and start a resource process the shutdown has already passed by.
          if (this.isShuttingDown) {
            break;
          }

          started = await runWithRunStartWrappers(debugConfig.runId, () => this.startDebugging(workspaceFolder, debugConfig));
          if (started) {
            break;
          }

          if (attempt < maxAttempts && !this.isShuttingDown) {
            extensionLogOutputChannel.warn(`Debug session did not start for run ID ${debugConfig.runId}; retrying (${attempt}/${maxAttempts}).`);
            await delay(AspireDebugSession._mauiDebugStartRetryDelayMs);
          }
        }
      } catch (error) {
        disposable.dispose();
        cleanupRun(debugConfig.runId);
        extensionLogOutputChannel.error(`Failed to start debug session: ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
        resolved = true;
        resolve(undefined);
        return;
      }

      if (!started) {
        disposable.dispose();
        cleanupRun(debugConfig.runId);
        resolved = true;
        resolve(undefined);
      }

      setTimeout(() => {
        if (!resolved) {
          disposable.dispose();
          cleanupRun(debugConfig.runId);
          resolved = true;
          resolve(undefined);
        }
      }, 10000);
    });
  }

  private async startDebugging(workspaceFolder: vscode.WorkspaceFolder | undefined, debugConfig: AspireResourceExtendedDebugConfiguration): Promise<boolean> {
    // VS Code terminates the parent debug session when the MAUI extension cancels
    // a parented child launch before the MAUI project system is ready. We still
    // track and stop the MAUI session ourselves once it starts, so leave it
    // unparented to keep the AppHost alive across bounded start retries.
    const parentSession = debugConfig.type === 'maui' ? undefined : this._session;
    return await vscode.debug.startDebugging(workspaceFolder, debugConfig, parentSession);
  }

  private getDebugSessionWorkspaceFolder(debugConfig: AspireResourceExtendedDebugConfiguration): vscode.WorkspaceFolder | undefined {
    const resourcePath = typeof debugConfig.cwd === 'string'
      ? debugConfig.cwd
      : typeof debugConfig.program === 'string' ? debugConfig.program : undefined;

    return resourcePath ? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(resourcePath)) : undefined;
  }

  /**
   * Opens the dashboard URL in the specified browser.
   * For debugChrome/debugEdge/debugFirefox, launches as a child debug session that is stopped by
   * the ordered shutdown or by the late-start handler when shutdown is already in progress.
   */
  async openDashboard(url: string, browserType: DashboardBrowserType): Promise<void> {
    extensionLogOutputChannel.info(`Opening dashboard in browser: ${browserType}.`);

    if (this._disposed || this._stopAttemptInProgress || this._extensionShutdownRequested) {
      extensionLogOutputChannel.info('Skipping dashboard browser launch because the Aspire session is shutting down.');
      return;
    }

    this._dashboardUrl = url;
    this._onDidChangeState.fire();

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
      if (session.parentSession?.id === this._session.id && session.configuration.name === aspireDashboard && session.type === debugType) {
        this._dashboardDebugSession = session;
        disposable.dispose();
        this.trackDashboardTermination(session);
        if (this.isShuttingDown) {
          this.closeDashboardInBackground();
        }
      }
    });

    let didStart: boolean;
    const start = Promise.resolve(vscode.debug.startDebugging(
      undefined,
      debugConfig,
      this._session));
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
      if (this.isShuttingDown) {
        return;
      }

      await vscode.env.openExternal(vscode.Uri.parse(url));
    }
  }

  dispose(): void {
    if (!this._preserveAppHostRestartSourceSessionId) {
      delete this.configuration[appHostRestartSourceSessionIdConfigKey];
    }

    if (this._disposed || this._stopping) {
      return;
    }

    // Disposable.dispose() cannot return the shutdown promise, but it can still enter the same
    // bounded resources-before-AppHost path. Resource stop requests are started synchronously
    // before this method returns; the AppHost and parent follow only after those stops settle.
    this.stopDebuggingInBackground('session disposal');
  }

  finalizeForExtensionShutdown(): void {
    // Extension teardown is irreversible. Release listeners and terminal ownership even when
    // ordered shutdown failed and deliberately left the session available for an explicit retry.
    this.disposeCore();
  }

  private disposeCore(): void {
    if (this._disposed) {
      return;
    }
    this._disposed = true;
    if (!this._preserveAppHostRestartSourceSessionId) {
      delete this.configuration[appHostRestartSourceSessionIdConfigKey];
    }
    extensionLogOutputChannel.info('Stopping the Aspire debug session');
    this._onDidChangeState.fire();

    // Snapshot start-event metadata before we run disposables so the deferred
    // `debug/apphost/end` callback has a stable view even if instance state
    // mutates further (or the instance is reaped by VS Code before the timer
    // fires).
    const startMs = this._appHostStartTimeMs;
    const mode = this._appHostModeAtLaunch;
    const language = this._appHostLanguageAtLaunch;
    const languagePromise = this._appHostLanguageAtLaunchPromise;
    const targetVersion = this._appHostTargetVersionAtLaunch;
    const targetVersionPromise = this._appHostTargetVersionAtLaunchPromise;
    const appHostIsDirectory = this._appHostIsDirectoryAtLaunch;
    const debugSessionId = this.debugSessionId;
    const dcpServer = this._dcpServer;

    // Normal teardown awaits this stop as part of stopAllSessions. Keep an idempotent background
    // fallback for direct finalization during extension shutdown.
    this.closeDashboardInBackground();
    this._dashboardTerminationDisposable?.dispose();
    this._dashboardTerminationDisposable = undefined;

    // Stop child debug sessions first so their `sessionTerminated`
    // notifications can flow back through `AspireDcpServer.sendNotification`
    // and update the aggregate stats BEFORE we snapshot them for
    // `debug/apphost/end`. Without this ordering, late nonzero exits (notably
    // Windows' SIGTERM → 143 exit code which is not normalized to 0) would
    // be missed and the summary would under-report failures.
    this._disposables.forEach(disposable => disposable.dispose());

    this._trackedDebugAdapters = [];
    this._onDidSendDebugConsoleOutput.dispose();
    // Keep this disposed session tracked while its delayed CLI termination is pending, so
    // extension deactivation can still force-drain the process tree before VS Code exits.
    if (!this._cliTerminationTimer) {
      this.releaseExtensionContextOwnership();
    }

    // Telemetry: emit `debug/apphost/end` after a short grace window so any
    // pending `sessionTerminated` notifications kicked off by the child-stop
    // disposables above have time to flow through the adapterTracker → DCP
    // notification pipeline and update `anyNonZeroExit`. 500ms is enough for
    // the common case under normal load while keeping the bound short enough
    // to survive most extension teardown scenarios. We only fire the event if
    // `launch` ever ran — otherwise we'd be reporting a phantom session for
    // AppHosts that aborted before reaching the CLI spawn.
    if (startMs !== undefined) {
      setTimeout(() => {
        void (async () => {
          const durationMs = Date.now() - startMs;
          const resolvedLanguage = await languagePromise ?? language;
          const resolvedTargetVersion = await targetVersionPromise ?? targetVersion;
          const aggregate = dcpServer.takeDebugSessionAggregateStats(debugSessionId);
          sendTelemetryEvent('aspire/vscode/debug/apphost/end', {
            mode,
            apphost_language: resolvedLanguage,
            apphost_target_version: resolvedTargetVersion,
            apphost_is_directory: appHostIsDirectory,
            ended_with_error: aggregate?.anyNonZeroExit ? 'true' : 'false',
            distinct_resource_types: aggregate ? aggregate.distinctResourceTypes.join(',') : '',
          }, {
            duration_ms: durationMs,
            total_child_sessions: aggregate?.totalChildSessions ?? 0,
            distinct_resource_type_count: aggregate?.distinctResourceTypes.length ?? 0,
          });
        })();
      }, 500);
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

  private async stopDashboardWithinBudget(shutdownDeadline: number): Promise<void> {
    const deadline = Math.min(shutdownDeadline, Date.now() + AspireDebugSession._dashboardStopTimeoutMs);

    while (this._pendingDashboardDebugSessionStarts.size > 0) {
      const pendingStarts = [...this._pendingDashboardDebugSessionStarts];
      const results = await Promise.allSettled(pendingStarts.map(
        start => this.waitWithinBudget(
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

    await this.stopWithinBudget(
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
      if (this._disposed && this._dashboardDebugSession) {
        stopSessionInBackground(() => this.closeDashboard(), 'dashboard debug session after finalization');
      }
    });
  }

  private sendResponse(request: any, body: any = {}) {
    this._onDidSendMessage.fire({
      type: 'response',
      seq: this._messageSeq++,
      request_seq: request.seq,
      success: true,
      command: request.command,
      body
    });
  }

  private sendEvent(event: any) {
    this._onDidSendMessage.fire(event);
  }

  sendMessageWithEmoji(emoji: string, message: string, addNewLine: boolean = true, category: 'stdout' | 'stderr' = 'stdout') {
    this.sendMessage(`${emoji}  ${message}`, addNewLine, category);
  }

  private sendAppHostMessage(message: string, category: string | undefined) {
    const filteredMessage = this._appHostParentOutputFilter.filter(message, category);
    if (filteredMessage) {
      this.sendMessage(filteredMessage.output, false, filteredMessage.category);
    }
  }

  sendMessage(message: string, addNewLine: boolean = true, category: 'stdout' | 'stderr' = 'stdout') {
    const output = `${message}${addNewLine ? '\n' : ''}`;
    this.sendEvent({
      type: 'event',
      seq: this._messageSeq++,
      event: 'output',
      body: {
        category: category,
        output
      }
    });
    this._onDidSendDebugConsoleOutput.fire({
      debugSessionId: this.debugSessionId,
      appHostPath: this.appHostPath,
      category,
      output,
    });
  }

  notifyAppHostStartupCompleted() {
    this._startupCompleted = true;
    this._onDidChangeState.fire();
    extensionLogOutputChannel.info(`AppHost startup completed and dashboard is running.`);
  }
}

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/**
 * Renders a stop failure for an aggregate message. A rejection reason is `unknown`: adapters reject
 * with plain strings and DAP error objects as readily as with Errors.
 */
function describeStopFailure(reason: unknown): string {
  return reason instanceof Error ? reason.message : String(reason);
}

/**
 * Starts a session stop and always returns a promise.
 *
 * `stopSession()` is contributed by resource debugger extensions and is only typed as returning a
 * `Thenable<void>` - nothing forces the implementation to be `async`. A synchronous throw from one
 * of them would escape the surrounding `.map(...)` callback before `Promise.allSettled` ever saw
 * the array, aborting the whole shutdown and leaving every not-yet-visited resource, the AppHost,
 * and the Aspire parent running. `Promise.allSettled` only absorbs rejected promises, not throws
 * raised while the promise array is being built, so the conversion has to happen here.
 *
 * The call itself stays synchronous (rather than being deferred with `Promise.resolve().then(...)`)
 * so all resource stops are still started eagerly and run concurrently.
 */
function startStop<T>(operation: () => Thenable<T>): Promise<T> {
  try {
    return Promise.resolve(operation());
  }
  catch (err) {
    return Promise.reject(err);
  }
}

/**
 * Asks a session to stop without waiting for it, for the paths that cannot await: the late-start
 * handlers, which stop a session that arrived after the shutdown snapshot, and dispose(), whose
 * `Disposable.dispose()` contract returns void.
 *
 * The stop is still a `Thenable` and can reject - `vscode.debug.stopDebugging()` rejects for a
 * session VS Code no longer knows about - and dropping it produced an unhandled promise rejection
 * in the extension host with no indication of which session failed.
 */
function stopSessionInBackground(operation: () => Thenable<unknown>, description: string): void {
  startStop(operation).catch(err => {
    extensionLogOutputChannel.warn(`Failed to stop ${description}: ${describeStopFailure(err)}`);
  });
}

export function buildAspireCommandArgs(command: string, commandArgs: string[], extensionArgs: string[]): string[] {
  const args = [command];
  const separatorIndex = commandArgs.indexOf('--');
  if (separatorIndex < 0) {
    args.push(...commandArgs, ...extensionArgs);
  }
  else {
    // Extension-owned CLI switches must stay before the `--` app-args separator.
    // Otherwise commands delegated from the Aspire terminal, such as:
    //   aspire start --apphost AppHost.csproj -- --custom-arg value
    // would pass --apphost/--start-debug-session to the AppHost instead of the CLI.
    args.push(...commandArgs.slice(0, separatorIndex), ...extensionArgs, ...commandArgs.slice(separatorIndex));
  }

  return args;
}

function isErrorWithStreamedDebugConsoleOutput(err: unknown): boolean {
  return err instanceof Error && (err as Error & { debugConsoleOutputAlreadyWritten?: boolean }).debugConsoleOutputAlreadyWritten === true;
}

export interface AppHostParentOutput {
  output: string;
  category: 'stdout' | 'stderr';
}

export class AppHostParentOutputFilter {
  private _continuingDroppedLog = false;
  private _continuingErrorBlock = false;
  private _lastCategory: string | undefined;

  filter(output: string, category: string | undefined): AppHostParentOutput | undefined {
    // Per the DAP spec the `category` field is optional; clients should treat a
    // missing category as `'console'`. Normalize once at the boundary so state
    // tracking and per-line classification see a consistent value, and so
    // category-less debug-adapter output gets the same suppression as `'console'`
    // instead of being mirrored to the parent debug console as stdout.
    const normalizedCategory = category ?? 'console';

    if (normalizedCategory === 'debug') {
      this.resetState();
      this._lastCategory = normalizedCategory;
      return undefined;
    }

    // Continuation state (dropped log / error block) only makes sense within a single
    // logical stream. When the DAP category changes (e.g. console -> stdout) we are
    // looking at a different stream and previous indented-continuation context no
    // longer applies.
    if (normalizedCategory !== this._lastCategory) {
      this.resetState();
    }
    this._lastCategory = normalizedCategory;

    const segments = output.match(/[^\r\n]*(?:\r\n|\r|\n|$)/g)?.filter(segment => segment.length > 0) ?? [];
    let filteredOutput = '';
    // If the DAP delivered this chunk on stderr, keep the whole emitted message on
    // stderr — the channel itself is authoritative regardless of per-line classification.
    let hasErrorOutput = normalizedCategory === 'stderr';

    for (const segment of segments) {
      const outputCategory = this.getLineCategory(segment, normalizedCategory);
      if (outputCategory) {
        filteredOutput += segment;
        hasErrorOutput ||= outputCategory === 'stderr';
      }
    }

    if (filteredOutput.length === 0) {
      return undefined;
    }

    return {
      output: filteredOutput,
      category: hasErrorOutput ? 'stderr' : 'stdout'
    };
  }

  private getLineCategory(segment: string, category: string): 'stdout' | 'stderr' | undefined {
    const line = segment.replace(/(?:\r\n|\r|\n)$/, '');
    const trimmedLine = line.trim();

    if (trimmedLine.length === 0) {
      return !this._continuingDroppedLog && this.shouldMirrorConsoleOutput(category) ? this.getCurrentCategory(category) : undefined;
    }

    if (this._continuingDroppedLog && isIndentedContinuation(line)) {
      return undefined;
    }

    if (this._continuingErrorBlock && isIndentedContinuation(line)) {
      return 'stderr';
    }

    const logSeverity = getConsoleLogSeverity(trimmedLine);
    if (logSeverity) {
      this._continuingDroppedLog = logSeverity === 'low';
      this._continuingErrorBlock = logSeverity === 'severe';

      return logSeverity === 'low' ? undefined : this.getCurrentCategory(category);
    }

    const isSevereOutput = isSevereRuntimeOutputLine(trimmedLine);
    this._continuingDroppedLog = false;
    this._continuingErrorBlock = isSevereOutput;

    if (category === 'console' && !isSevereOutput) {
      return undefined;
    }

    return this.getCurrentCategory(category);
  }

  private shouldMirrorConsoleOutput(category: string): boolean {
    return category !== 'console' || this._continuingErrorBlock;
  }

  private getCurrentCategory(category: string): 'stdout' | 'stderr' {
    return category === 'stderr' || this._continuingErrorBlock ? 'stderr' : 'stdout';
  }

  private resetState() {
    this._continuingDroppedLog = false;
    this._continuingErrorBlock = false;
  }
}

function getConsoleLogSeverity(line: string): 'low' | 'normal' | 'severe' | undefined {
  const defaultConsoleLogLevel = /^(trce|dbug|info|warn|fail|crit):\s/.exec(line)?.[1];
  if (defaultConsoleLogLevel) {
    return defaultConsoleLogLevel === 'trce' || defaultConsoleLogLevel === 'dbug'
      ? 'low'
      : defaultConsoleLogLevel === 'fail' || defaultConsoleLogLevel === 'crit'
        ? 'severe'
        : 'normal';
  }

  // Microsoft.Extensions.Logging "simple" console formatter emits lines shaped like
  // `<CategoryTypeName>[<EventId>]?: <Level>: <message>`. Real category names are
  // namespaced .NET type names containing at least one dot (e.g.
  // `Aspire.Hosting.Health.ResourceHealthCheckService`). Requiring a dot avoids
  // matching arbitrary user stdout like `"Status: Error: connection refused"`.
  const simpleConsoleLogLevel = /^[A-Za-z_]\w*(?:\.\w+)+(?:\[[^\]]+\])?:\s*(Trace|Debug|Information|Warning|Error|Critical):\s/.exec(line)?.[1];
  if (simpleConsoleLogLevel) {
    return simpleConsoleLogLevel === 'Trace' || simpleConsoleLogLevel === 'Debug'
      ? 'low'
      : simpleConsoleLogLevel === 'Error' || simpleConsoleLogLevel === 'Critical'
        ? 'severe'
        : 'normal';
  }

  return undefined;
}

function isIndentedContinuation(line: string): boolean {
  return /^\s+\S/.test(line);
}

function isSevereRuntimeOutputLine(line: string): boolean {
  // Typed exception — `Namespace.Type.NameException: message` (also matches plain `System.Exception:`).
  return /(?:^|\s)(?:[A-Za-z_][\w`]*\.)+(?:[A-Za-z_][\w`]*Exception|Exception):/.test(line)
    // JavaScript / Node.js error shapes — `Uncaught TypeError: ...`, `Error [CODE]: ...`.
    || /^(?:Uncaught\s+)?(?:[A-Za-z_$][\w$]*Error|Error)(?:\s+\[[^\]]+\])?:/.test(line)
    // Anchored fatal-marker prefixes only — bare word matches like `\bfailed\b` produced
    // false positives on user stdout (`"Failed payment retry queued"`, file paths
    // containing "error", etc.).
    || /^(?:fatal|critical|panic|aborted|segmentation\s+fault|unhandled\s+exception)\b/i.test(line);
}
