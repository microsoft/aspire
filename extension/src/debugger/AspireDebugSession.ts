import * as vscode from "vscode";
import { EventEmitter } from "vscode";
import * as fs from "fs";
import { createDebugAdapterTracker, AppHostOutputHandler, AppHostRestartHandler } from "./adapterTracker";
import { AspireResourceExtendedDebugConfiguration, AspireResourceDebugSession, EnvVar, AspireExtendedDebugConfiguration, NodeLaunchConfiguration, ProjectLaunchConfiguration, StartAppHostOptions } from "../dcp/types";
import { extensionLogOutputChannel } from "../utils/logging";
import AspireDcpServer, { generateDcpIdPrefix } from "../dcp/AspireDcpServer";
import { spawnCliProcess } from "./languages/cli";
import { disconnectingFromSession, launchingWithAppHost, launchingWithDirectory, processExceptionOccurred, processExitedWithCode, aspireDashboard, appHostSessionTerminated } from "../loc/strings";
import { projectDebuggerExtension } from "./languages/dotnet";
import { AnsiColors } from "../utils/AspireTerminalProvider";
import { applyTextStyle } from "../utils/strings";
import { nodeDebuggerExtension } from "./languages/node";
import { cleanupRun } from "./runCleanupRegistry";
import { runWithRunStartWrappers } from "./runStartRegistry";
import AspireRpcServer from "../server/AspireRpcServer";
import { createDebugSessionConfiguration } from "./debuggerExtensions";
import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";
import { ICliRpcClient } from "../server/rpcClient";
import path from "path";
import os from "os";
import { EnvironmentVariables } from "../utils/environment";
import { sendTelemetryEvent } from "../utils/telemetry";
import { classifyAppHostPath, classifyAppHostDirectory } from "../utils/appHostLanguage";
import type { AspireDebugConsoleOutputEvent } from "../types/extensionApi";

export type DashboardBrowserType = 'openExternalBrowser' | 'integratedBrowser' | 'debugChrome' | 'debugEdge' | 'debugFirefox';

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
  private readonly _onDidSendMessage = new EventEmitter<any>();
  private readonly _onDidSendDebugConsoleOutput = new EventEmitter<AspireDebugConsoleOutputEvent>();
  private _messageSeq = 1;
  private readonly _appHostParentOutputFilter = new AppHostParentOutputFilter();

  private readonly _session: vscode.DebugSession;
  private readonly _rpcServer: AspireRpcServer;
  private readonly _dcpServer: AspireDcpServer;
  private readonly _terminalProvider: AspireTerminalProvider;

  private _appHostDebugSession?: AspireResourceDebugSession = undefined;
  private _resourceDebugSessions: AspireResourceDebugSession[] = [];
  private _trackedDebugAdapters: string[] = [];
  private _rpcClient?: ICliRpcClient;
  private _dashboardDebugSession: vscode.DebugSession | null = null;
  private _dashboardUrl: string | undefined;
  private _startupCompleted = false;
  private readonly _onDidChangeState = new EventEmitter<void>();
  private readonly _disposables: vscode.Disposable[] = [];
  private _disposed = false;
  // Timestamp for the `debug/apphost/end` duration measurement. Captured the first
  // time we observe a `launch` request so it covers the actual user-visible session
  // lifetime, not the moment the AspireDebugSession object was constructed.
  private _appHostStartTimeMs: number | undefined = undefined;
  // Tracks the AppHost-language classification of the launched program so it can
  // be repeated on the matching end event without re-deriving from `configuration`.
  private _appHostLanguageAtLaunch: 'csharp' | 'typescript' | 'unknown' = 'unknown';
  // Mode the AppHost was launched with (`run` | `debug`) — captured for the
  // matching end event.
  private _appHostModeAtLaunch: 'run' | 'debug' = 'run';

  public readonly onDidSendMessage = this._onDidSendMessage.event;
  public readonly onDidSendDebugConsoleOutput = this._onDidSendDebugConsoleOutput.event;
  public readonly onDidChangeState = this._onDidChangeState.event;
  public readonly debugSessionId: string;
  public configuration: AspireExtendedDebugConfiguration;

  get appHostPath(): string | undefined {
    return typeof this.configuration.program === 'string' ? this.configuration.program : undefined;
  }

  get dashboardUrl(): string | undefined {
    return this._dashboardUrl;
  }

  get startupCompleted(): boolean {
    return this._startupCompleted;
  }

  constructor(session: vscode.DebugSession, rpcServer: AspireRpcServer, dcpServer: AspireDcpServer, terminalProvider: AspireTerminalProvider, removeAspireDebugSession: (session: AspireDebugSession) => void, debugSessionId: string = generateDcpIdPrefix()) {
    this._session = session;
    this._rpcServer = rpcServer;
    this._dcpServer = dcpServer;
    this._terminalProvider = terminalProvider;
    this.configuration = session.configuration as AspireExtendedDebugConfiguration;

    this.debugSessionId = debugSessionId;

    this._disposables.push({
      dispose: () => removeAspireDebugSession(this)
    });
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

      const command = this.configuration.command ?? 'run';
      const noDebug = !!message.arguments?.noDebug && command === 'run';

      // Append any additional command args forwarded from the CLI (e.g., step name for 'do', unmatched tokens)
      const commandArgs = this.configuration.args ?? [];
      const appHostPath = this._session.configuration.program as string;
      const appHostIsDirectory = isDirectory(appHostPath);
      const extensionArgs: string[] = [];

      // Telemetry: emit `debug/apphost/start` once per AppHost launch. We do it
      // here (rather than in the constructor) because the constructor runs
      // before VS Code's debug-launch UX completes; this branch is the single
      // entry point that triggers an actual CLI spawn. The matching end event
      // is emitted from dispose().
      this._appHostStartTimeMs = Date.now();
      this._appHostLanguageAtLaunch = appHostIsDirectory
        ? classifyAppHostDirectory(appHostPath)
        : classifyAppHostPath(appHostPath);
      this._appHostModeAtLaunch = noDebug ? 'run' : 'debug';
      // `command` originates in the user's launch.json and is typed in the
      // contributing extension surface as AspireCommandType ('run'|'deploy'|
      // 'publish'|'do'), but launch.json is freeform JSON — a typo or custom
      // value would otherwise leak verbatim into telemetry. Clamp to the known
      // set so the dimension stays bounded.
      const knownCommands: ReadonlySet<string> = new Set(['run', 'deploy', 'publish', 'do']);
      const commandForTelemetry = knownCommands.has(command) ? command : 'other';
      sendTelemetryEvent('debug/apphost/start', {
        mode: this._appHostModeAtLaunch,
        apphost_language: this._appHostLanguageAtLaunch,
        apphost_is_directory: appHostIsDirectory ? 'true' : 'false',
        command: commandForTelemetry,
      });

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

      if (appHostIsDirectory) {
        this.sendMessageWithEmoji("📁", launchingWithDirectory(appHostPath));

        void this.spawnAspireCommand(args, appHostPath, noDebug, commandLabel);
      }
      else {
        this.sendMessageWithEmoji("📂", launchingWithAppHost(appHostPath));

        const workspaceFolder = path.dirname(appHostPath);
        void this.spawnAspireCommand(args, workspaceFolder, noDebug, commandLabel);
      }
    }
    else if (message.command === 'disconnect' || message.command === 'terminate') {
      this.sendMessageWithEmoji("🔌", disconnectingFromSession);
      this.dispose();

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

    function isDirectory(pathToCheck: string): boolean {
      return fs.existsSync(pathToCheck) && fs.statSync(pathToCheck).isDirectory();
    }
  }

  async spawnAspireCommand(args: string[], workingDirectory: string | undefined, noDebug: boolean, commandLabel: string = 'aspire run') {
    const disposable = this._rpcServer.onNewConnection((client: ICliRpcClient) => {
      if (client.debugSessionId === this.debugSessionId) {
        this._rpcClient = client;
        disposable.dispose();
      }
    });

    const configuredEnv = this.configuration.env;
    const env = configuredEnv
      ? Object.entries(configuredEnv).map(([name, value]) => ({ name, value: String(value) }))
      : undefined;

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

    spawnCliProcess(
      this._terminalProvider,
      await this._terminalProvider.getAspireCliExecutablePath(),
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
          // if the process failed, we want to stop the debug session
          this.dispose();
        },
        workingDirectory: workingDirectory,
        debugSessionId: this.debugSessionId,
        noDebug: noDebug,
        env: env
      },
    );

    this._disposables.push({
      dispose: () => {
        this._rpcClient?.stopCli().catch((err) => {
          extensionLogOutputChannel.info(`stopCli failed (connection may already be closed): ${err}`);
        });
        extensionLogOutputChannel.info(`Requested Aspire CLI exit with args: ${args.join(' ')}`);
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

      const disposable = vscode.debug.onDidTerminateDebugSession(async session => {
        if (this._appHostDebugSession && session.id === this._appHostDebugSession.id) {
          if (!this._appHostRestartRequested) {
            this.sendMessageWithEmoji("ℹ️", applyTextStyle(appHostSessionTerminated, AnsiColors.Yellow));
          }

          // Only restart the Aspire session when the user explicitly clicked
          // "restart" on the app host debug toolbar (detected via DAP tracker).
          // All other cases (user stop, process crash/exit) just dispose.
          const shouldRestart = this._appHostRestartRequested;
          const config = this.configuration;
          this.dispose();

          if (shouldRestart) {
            extensionLogOutputChannel.info('AppHost restart requested, restarting Aspire debug session');
            await vscode.debug.startDebugging(undefined, config);
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

  async startAndGetDebugSession(debugConfig: AspireResourceExtendedDebugConfiguration): Promise<AspireResourceDebugSession | undefined> {
    return new Promise(async (resolve) => {
      const logConfig = getLoggableDebugConfiguration(debugConfig, this._terminalProvider.isDebugConfigEnvironmentLoggingEnabled());
      extensionLogOutputChannel.info(`Starting debug session with configuration: ${JSON.stringify(logConfig)}`);
      this.createDebugAdapterTrackerCore(debugConfig.type);

      let resolved = false;
      const disposable = vscode.debug.onDidStartDebugSession(session => {
        if (session.configuration.runId === debugConfig.runId) {
          extensionLogOutputChannel.info(`Debug session started: ${session.name} (run id: ${session.configuration.runId})`);
          disposable.dispose();

          if (this._disposed) {
            extensionLogOutputChannel.info(`Stopping debug session that started after Aspire session disposal: ${session.name} (run id: ${session.configuration.runId})`);
            vscode.debug.stopDebugging(session);
            cleanupRun(debugConfig.runId);
            resolved = true;
            resolve(undefined);
            return;
          }

          const disposalFunction = () => {
            extensionLogOutputChannel.info(`Stopping debug session: ${session.name} (run id: ${session.configuration.runId})`);
            vscode.debug.stopDebugging(session);

            // Run any cleanup registered by resource-type extensions (e.g. func host for Azure Functions)
            cleanupRun(debugConfig.runId);
          };

          const vsCodeDebugSession: AspireResourceDebugSession = {
            id: session.id,
            session: session,
            stopSession: disposalFunction
          };

          this._resourceDebugSessions.push(vsCodeDebugSession);
          this._disposables.push({
            dispose: disposalFunction
          });

          resolved = true;
          resolve(vsCodeDebugSession);
        }
      });

      let started = false;
      try {
        const workspaceFolder = this.getDebugSessionWorkspaceFolder(debugConfig);
        const maxAttempts = debugConfig.type === 'maui' ? AspireDebugSession._mauiDebugStartMaxAttempts : 1;
        for (let attempt = 1; attempt <= maxAttempts; attempt++) {
          if (this._disposed) {
            break;
          }

          started = await runWithRunStartWrappers(debugConfig.runId, () => this.startDebugging(workspaceFolder, debugConfig));
          if (started) {
            break;
          }

          if (attempt < maxAttempts && !this._disposed) {
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
   * For debugChrome/debugEdge/debugFirefox, launches as a child debug session that auto-closes with the Aspire debug session.
   */
  async openDashboard(url: string, browserType: DashboardBrowserType): Promise<void> {
    extensionLogOutputChannel.info(`Opening dashboard in browser: ${browserType}.`);
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
   * The browser will automatically close when the parent Aspire debug session ends.
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

    // Register listener before starting so we don't miss the event
    const disposable = vscode.debug.onDidStartDebugSession((session) => {
      if (session.configuration.name === aspireDashboard && session.type === debugType) {
        this._dashboardDebugSession = session;
        disposable.dispose();
      }
    });

    // Start as a child debug session - it will close when parent closes
    const didStart = await vscode.debug.startDebugging(
      undefined,
      debugConfig,
      this._session
    );

    if (!didStart) {
      disposable.dispose();
      extensionLogOutputChannel.warn(`Failed to start debug browser (${debugType}), falling back to default browser`);
      await vscode.env.openExternal(vscode.Uri.parse(url));
    }
  }

  dispose(): void {
    if (this._disposed) {
      return;
    }
    this._disposed = true;
    extensionLogOutputChannel.info('Stopping the Aspire debug session');
    this._onDidChangeState.fire();

    // Snapshot start-event metadata before we run disposables so the deferred
    // `debug/apphost/end` callback has a stable view even if instance state
    // mutates further (or the instance is reaped by VS Code before the timer
    // fires).
    const startMs = this._appHostStartTimeMs;
    const mode = this._appHostModeAtLaunch;
    const language = this._appHostLanguageAtLaunch;
    const debugSessionId = this.debugSessionId;
    const dcpServer = this._dcpServer;

    // Stop child debug sessions first so their `sessionTerminated`
    // notifications can flow back through `AspireDcpServer.sendNotification`
    // and update the aggregate stats BEFORE we snapshot them for
    // `debug/apphost/end`. Without this ordering, late nonzero exits (notably
    // Windows' SIGTERM → 143 exit code which is not normalized to 0) would
    // be missed and the summary would under-report failures.
    this._disposables.forEach(disposable => disposable.dispose());
    this._trackedDebugAdapters = [];
    vscode.debug.stopDebugging(this._session);
    this._onDidSendDebugConsoleOutput.dispose();

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
        const aggregate = dcpServer.takeDebugSessionAggregateStats(debugSessionId);
        sendTelemetryEvent('debug/apphost/end', {
          mode,
          apphost_language: language,
          ended_with_error: aggregate?.anyNonZeroExit ? 'true' : 'false',
          distinct_resource_types: aggregate ? aggregate.distinctResourceTypes.join(',') : '',
        }, {
          duration_ms: Date.now() - startMs,
          total_child_sessions: aggregate?.totalChildSessions ?? 0,
          distinct_resource_type_count: aggregate?.distinctResourceTypes.length ?? 0,
        });
      }, 500);
    }
  }

  /**
   * Closes the dashboard browser if closeDashboardOnDebugEnd is enabled.
   * Handles closing debug browser sessions.
   */
  private closeDashboard(): void {
    const aspireConfig = vscode.workspace.getConfiguration('aspire');
    const shouldClose = aspireConfig.get<boolean>('closeDashboardOnDebugEnd', true);

    if (!shouldClose) {
      this._dashboardDebugSession = null;
      return;
    }

    extensionLogOutputChannel.info('Closing dashboard browser...');

    // For debug browsers, stop the debug session
    if (this._dashboardDebugSession) {
      vscode.debug.stopDebugging(this._dashboardDebugSession).then(
        () => extensionLogOutputChannel.info('Dashboard debug session stopped.'),
        (err) => extensionLogOutputChannel.warn(`Failed to stop dashboard debug session: ${err}`)
      );
      this._dashboardDebugSession = null;
      return;
    }
    // At this point there is no tracked dashboard debug session to stop.
    // Any debug browser child sessions (debugChrome, debugEdge, debugFirefox) will
    // automatically close when the parent Aspire session is stopped, so no further
    // cleanup is required here.
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
