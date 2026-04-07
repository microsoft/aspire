import * as vscode from "vscode";
import { EventEmitter } from "vscode";
import * as fs from "fs";
import { createDebugAdapterTracker, AppHostRestartHandler } from "./adapterTracker";
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
import AspireRpcServer from "../server/AspireRpcServer";
import { createDebugSessionConfiguration } from "./debuggerExtensions";
import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";
import { ICliRpcClient } from "../server/rpcClient";
import path from "path";
import os from "os";
import { EnvironmentVariables } from "../utils/environment";

export type DashboardBrowserType = 'openExternalBrowser' | 'integratedBrowser' | 'debugChrome' | 'debugEdge' | 'debugFirefox';

export class AspireDebugSession implements vscode.DebugAdapter {
  private readonly _onDidSendMessage = new EventEmitter<any>();
  private _messageSeq = 1;

  private readonly _session: vscode.DebugSession;
  private readonly _rpcServer: AspireRpcServer;
  private readonly _dcpServer: AspireDcpServer;
  private readonly _terminalProvider: AspireTerminalProvider;

  private _appHostDebugSession?: AspireResourceDebugSession = undefined;
  private _resourceDebugSessions: AspireResourceDebugSession[] = [];
  private _trackedDebugAdapters: string[] = [];
  private _rpcClient?: ICliRpcClient;
  private _dashboardDebugSession: vscode.DebugSession | null = null;
  private readonly _disposables: vscode.Disposable[] = [];
  private _disposed = false;

  public readonly onDidSendMessage = this._onDidSendMessage.event;
  public readonly debugSessionId: string;
  public configuration: AspireExtendedDebugConfiguration;

  constructor(session: vscode.DebugSession, rpcServer: AspireRpcServer, dcpServer: AspireDcpServer, terminalProvider: AspireTerminalProvider, removeAspireDebugSession: (session: AspireDebugSession) => void) {
    this._session = session;
    this._rpcServer = rpcServer;
    this._dcpServer = dcpServer;
    this._terminalProvider = terminalProvider;
    this.configuration = session.configuration as AspireExtendedDebugConfiguration;

    this.debugSessionId = generateDcpIdPrefix();

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

      const appHostPath = this._session.configuration.program as string;
      const command = this.configuration.command ?? 'run';
      const noDebug = !!message.arguments?.noDebug && command === 'run';

      const args: string[] = [command];

      // Append any additional command args forwarded from the CLI (e.g., step name for 'do', unmatched tokens)
      const commandArgs = this.configuration.args;
      if (commandArgs && commandArgs.length > 0) {
        args.push(...commandArgs);
      }

      // For 'do' with an explicit step (old CLI fallback), pass it as a positional argument
      const step = this.configuration.step;
      if (command === 'do' && step && !commandArgs?.length) {
        args.push(step);
      }

      // --start-debug-session tells the CLI to launch the AppHost via the extension with debugger attached
      if (!noDebug) {
        args.push('--start-debug-session');
      }

      if (process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true') {
        args.push('--cli-wait-for-debugger');
      }

      if (process.env[EnvironmentVariables.ASPIRE_APPHOST_STOP_ON_ENTRY] === 'true') {
        args.push('--wait-for-debugger');
      }

      if (this._terminalProvider.isCliDebugLoggingEnabled()) {
        args.push('--debug');
      }

      const commandLabel = `aspire ${command}`;

      if (isDirectory(appHostPath)) {
        this.sendMessageWithEmoji("📁", launchingWithDirectory(appHostPath));

        void this.spawnAspireCommand(args, appHostPath, noDebug, commandLabel);
      }
      else {
        this.sendMessageWithEmoji("📂", launchingWithAppHost(appHostPath));

        const workspaceFolder = path.dirname(appHostPath);
        args.push('--apphost', appHostPath);
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

    spawnCliProcess(
      this._terminalProvider,
      await this._terminalProvider.getAspireCliExecutablePath(),
      args,
      {
        stdoutCallback: (data) => {
          for (const line of trimMessage(data)) {
            this.sendMessage(line);
          }
        },
        stderrCallback: (data) => {
          for (const line of trimMessage(data)) {
            this.sendMessageWithEmoji("❌", line, false);
          }
        },
        errorCallback: (error) => {
          extensionLogOutputChannel.error(`Error spawning aspire process: ${error}`);
          vscode.window.showErrorMessage(processExceptionOccurred(error.message, commandLabel));
        },
        exitCallback: (code) => {
          this.sendMessageWithEmoji("🔚", processExitedWithCode(code ?? '?'));
          // if the process failed, we want to stop the debug session
          this.dispose();
        },
        workingDirectory: workingDirectory,
        debugSessionId: this.debugSessionId,
        noDebug: noDebug
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

    function trimMessage(message: string): string[] {
      return message
        .replace('\r\n', '\n')
        .split('\n')
        .map(line => line.trim())
        // Filter empty lines and terminal progress bar escape sequences
        .filter(line => line.length > 0 && !line.match(/^\u001b\]9;4;\d+\u001b\\$/));
    }
  }

  createDebugAdapterTrackerCore(debugAdapter: string, onAppHostRestartRequested?: AppHostRestartHandler) {
    if (this._trackedDebugAdapters.includes(debugAdapter)) {
      return;
    }

    this._trackedDebugAdapters.push(debugAdapter);
    this._disposables.push(createDebugAdapterTracker(this._dcpServer, debugAdapter, onAppHostRestartRequested));
  }

  private static readonly _nodeAppHostExtensions = ['.js', '.ts', '.mjs', '.mts', '.cjs', '.cts'];

  private _appHostRestartRequested = false;

  async startAppHost(projectFile: string, args: string[], environment: EnvVar[], debug: boolean, options: StartAppHostOptions): Promise<void> {
    try {
      const fileExtension = path.extname(projectFile).toLowerCase();
      const isNodeAppHost = AspireDebugSession._nodeAppHostExtensions.includes(fileExtension);

      const debuggerExtension = isNodeAppHost ? nodeDebuggerExtension : projectDebuggerExtension;

      // Register the adapter tracker with an app host restart handler.
      // When the user clicks "restart" on the app host child session,
      // we suppress VS Code's automatic child restart and restart the
      // entire Aspire debug session instead.
      this.createDebugAdapterTrackerCore(
        debuggerExtension.debugAdapter,
        (debugSessionId) => {
          if (debugSessionId === this.debugSessionId) {
            this._appHostRestartRequested = true;
            return true; // suppress VS Code's child restart
          }
          return false;
        }
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
      extensionLogOutputChannel.error(`Error starting AppHost debug session: ${err}`);
      vscode.window.showErrorMessage(String(err));
      this.dispose();
    }
  }

  async startAndGetDebugSession(debugConfig: AspireResourceExtendedDebugConfiguration): Promise<AspireResourceDebugSession | undefined> {
    return new Promise(async (resolve) => {
      const logConfig = this._terminalProvider.isDebugConfigEnvironmentLoggingEnabled()
        ? debugConfig
        : { ...debugConfig, env: debugConfig.env ? '<redacted>' : undefined };
      extensionLogOutputChannel.info(`Starting debug session with configuration: ${JSON.stringify(logConfig)}`);
      this.createDebugAdapterTrackerCore(debugConfig.type);

      const disposable = vscode.debug.onDidStartDebugSession(session => {
        if (session.configuration.runId === debugConfig.runId) {
          extensionLogOutputChannel.info(`Debug session started: ${session.name} (run id: ${session.configuration.runId})`);
          disposable.dispose();

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

          resolve(vsCodeDebugSession);
        }
      });

      const started = await vscode.debug.startDebugging(undefined, debugConfig, this._session);
      if (!started) {
        disposable.dispose();
        resolve(undefined);
      }

      setTimeout(() => {
        disposable.dispose();
        resolve(undefined);
      }, 10000);
    });
  }

  /**
   * Opens the dashboard URL in the specified browser.
   * For debugChrome/debugEdge/debugFirefox, launches as a child debug session that auto-closes with the Aspire debug session.
   */
  async openDashboard(url: string, browserType: DashboardBrowserType): Promise<void> {
    extensionLogOutputChannel.info(`Opening dashboard in browser: ${browserType}, URL: ${url}`);

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
    vscode.debug.stopDebugging(this._session);
    this._disposables.forEach(disposable => disposable.dispose());
    this._trackedDebugAdapters = [];
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

  sendMessageWithEmoji(emoji: string, message: string, addNewLine: boolean = true) {
    this.sendMessage(`${emoji}  ${message}`, addNewLine);
  }

  sendMessage(message: string, addNewLine: boolean = true, category: 'stdout' | 'stderr' = 'stdout') {
    this.sendEvent({
      type: 'event',
      seq: this._messageSeq++,
      event: 'output',
      body: {
        category: category,
        output: `${message}${addNewLine ? '\n' : ''}`
      }
    });
  }

  notifyAppHostStartupCompleted() {
    extensionLogOutputChannel.info(`AppHost startup completed and dashboard is running.`);
  }
}
