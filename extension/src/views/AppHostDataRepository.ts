import * as vscode from 'vscode';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { EnvironmentVariables } from '../utils/environment';
import { errorFetchingAppHosts } from '../loc/strings';

export interface ResourceUrlJson {
    name: string | null;
    displayName: string | null;
    url: string;
    isInternal: boolean;
}

export interface ResourceCommandJson {
    description: string | null;
}

export interface ResourceJson {
    name: string;
    displayName: string | null;
    resourceType: string;
    state: string | null;
    stateStyle: string | null;
    healthStatus: string | null;
    dashboardUrl: string | null;
    urls: ResourceUrlJson[] | null;
    commands: Record<string, ResourceCommandJson> | null;
    properties: Record<string, string | null> | null;
}

export interface AppHostDisplayInfo {
    appHostPath: string;
    appHostPid: number;
    cliPid: number | null;
    dashboardUrl: string | null;
    resources: ResourceJson[] | null | undefined;
}

export type ViewMode = 'workspace' | 'global';

/**
 * Central data repository for app host and resource information.
 *
 * Owns two independent data sources:
 *  - `aspire describe --follow` (workspace mode) — streams resource updates
 *    via NDJSON.  Runs unconditionally from activation so workspace data is
 *    available across the extension.
 *  - `aspire ps` polling (global mode) — periodically fetches all running
 *    app hosts.  Only active while the tree-view panel is visible **and**
 *    global mode is selected.
 */
export class AppHostDataRepository {
    private readonly _onDidChangeData = new vscode.EventEmitter<void>();
    readonly onDidChangeData = this._onDidChangeData.event;

    // ── Mode / panel state ──
    private _viewMode: ViewMode = 'workspace';
    private _panelVisible = false;

    // ── Workspace mode state (describe --follow) ──
    private _workspaceResources: Map<string, ResourceJson> = new Map();
    private _describeProcess: ChildProcessWithoutNullStreams | undefined;
    private _describeRestarting = false;
    private _describeRestartDelay = 5000;
    private _describeRestartTimer: ReturnType<typeof setTimeout> | undefined;
    private _describeReceivedData = false;

    // ── Global mode state (ps polling) ──
    private _appHosts: AppHostDisplayInfo[] = [];
    private _pollingInterval: ReturnType<typeof setInterval> | undefined;
    private _supportsResources = true;
    private _fetchInProgress = false;

    // ── Workspace app host (from aspire extension get-apphosts) ──
    private _workspaceAppHostName: string | undefined;
    private _workspaceAppHostPath: string | undefined;
    private _getAppHostsProcess: ChildProcessWithoutNullStreams | undefined;

    // ── Error state ──
    private _errorMessage: string | undefined;

    private readonly _configChangeDisposable: vscode.Disposable;
    private _disposed = false;

    constructor(private readonly _terminalProvider: AspireTerminalProvider) {
        this._fetchWorkspaceAppHost();
        this._configChangeDisposable = vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration('aspire.globalAppHostsPollingInterval') && this._shouldPoll) {
                this._startPsPolling();
            }
        });
    }

    // ── Public accessors ──

    get viewMode(): ViewMode {
        return this._viewMode;
    }

    get workspaceResources(): readonly ResourceJson[] {
        return Array.from(this._workspaceResources.values());
    }

    get appHosts(): readonly AppHostDisplayInfo[] {
        return this._appHosts;
    }

    get workspaceAppHostName(): string | undefined {
        return this._workspaceAppHostName;
    }

    get workspaceAppHostPath(): string | undefined {
        return this._workspaceAppHostPath;
    }

    get errorMessage(): string | undefined {
        return this._errorMessage;
    }

    get hasError(): boolean {
        return this._errorMessage !== undefined;
    }

    // ── Mode / panel control ──

    setViewMode(mode: ViewMode): void {
        if (this._viewMode === mode) {
            return;
        }
        this._viewMode = mode;
        vscode.commands.executeCommand('setContext', 'aspire.viewMode', mode);
        this._syncPolling();
        this._onDidChangeData.fire();
    }

    setPanelVisible(visible: boolean): void {
        if (this._panelVisible === visible) {
            return;
        }
        this._panelVisible = visible;
        this._syncPolling();
    }

    refresh(): void {
        this._stopDescribeWatch();
        this._workspaceResources.clear();
        this._setError(undefined);
        this._updateWorkspaceContext();
        this._describeRestartDelay = 5000;
        this._startDescribeWatch();
        if (this._shouldPoll) {
            this._fetchAppHosts();
        }
    }

    activate(): void {
        vscode.commands.executeCommand('setContext', 'aspire.viewMode', this._viewMode);
        this._startDescribeWatch();
    }

    dispose(): void {
        this._disposed = true;
        this._stopPolling();
        this._stopDescribeWatch();
        this._getAppHostsProcess?.kill();
        this._configChangeDisposable.dispose();
        this._onDidChangeData.dispose();
    }

    // ── PS polling lifecycle ──

    private get _shouldPoll(): boolean {
        return this._panelVisible && this._viewMode === 'global';
    }

    private _syncPolling(): void {
        if (this._disposed) {
            return;
        }
        if (this._shouldPoll) {
            this._startPsPolling();
        } else {
            this._stopPolling();
        }
    }

    // ── Workspace app host (from aspire extension get-apphosts) ──

    private _fetchWorkspaceAppHost(): void {
        const workspaceFolders = vscode.workspace.workspaceFolders;
        if (!workspaceFolders || workspaceFolders.length === 0) {
            return;
        }
        const rootFolder = workspaceFolders[0];

        extensionLogOutputChannel.info('Fetching workspace apphost via: aspire extension get-apphosts');

        this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
            if (this._disposed) {
                return;
            }

            const args = ['extension', 'get-apphosts'];
            if (process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true') {
                args.push('--cli-wait-for-debugger');
            }

            this._getAppHostsProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                noExtensionVariables: true,
                workingDirectory: rootFolder.uri.fsPath,
                lineCallback: (line) => {
                    try {
                        const parsed = JSON.parse(line);
                        if (parsed && (typeof parsed.selected_project_file === 'string' || parsed.selected_project_file === null) && Array.isArray(parsed.all_project_file_candidates)) {
                            const appHostPath = parsed.selected_project_file
                                ?? (parsed.all_project_file_candidates.length === 1 ? parsed.all_project_file_candidates[0] : null);
                            if (appHostPath) {
                                this._workspaceAppHostPath = appHostPath;
                                this._workspaceAppHostName = shortenPath(appHostPath);
                                extensionLogOutputChannel.info(`Workspace apphost resolved: ${appHostPath}`);
                                this._onDidChangeData.fire();
                            }
                        }
                    } catch {
                        // Not a JSON line we care about
                    }
                },
                exitCallback: (code) => {
                    this._getAppHostsProcess = undefined;
                    if (code !== 0) {
                        extensionLogOutputChannel.warn(`aspire extension get-apphosts exited with code ${code}`);
                    }
                },
                errorCallback: (error) => {
                    this._getAppHostsProcess = undefined;
                    extensionLogOutputChannel.warn(`aspire extension get-apphosts error: ${error.message}`);
                },
            });
        }).catch(error => {
            extensionLogOutputChannel.warn(`Failed to fetch workspace apphost: ${error}`);
        });
    }

    // ── Workspace mode: describe --follow ──

    private _startDescribeWatch(): void {
        if (this._describeProcess || this._disposed) {
            return;
        }

        this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
            if (this._disposed) {
                return;
            }

            const args = ['describe', '--follow', '--format', 'json'];

            extensionLogOutputChannel.info('Starting aspire describe --follow for workspace resources');

            this._describeReceivedData = false;
            this._describeProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                noExtensionVariables: true,
                lineCallback: (line) => {
                    this._handleDescribeLine(line);
                },
                exitCallback: (code) => {
                    extensionLogOutputChannel.info(`aspire describe --follow exited with code ${code}`);
                    this._describeProcess = undefined;

                    if (!this._disposed && !this._describeRestarting) {
                        if (!this._describeReceivedData && code !== 0) {
                            // The process exited with a non-zero code without ever producing valid data.
                            // This likely means the CLI does not support the describe command.
                            extensionLogOutputChannel.warn('aspire describe --follow exited without producing data; the installed Aspire CLI may not support this feature.');
                            this._workspaceResources.clear();
                            this._setError(errorFetchingAppHosts(`exit code ${code}`));
                            this._updateWorkspaceContext();
                        } else {
                            this._workspaceResources.clear();
                            this._setError(undefined);
                            this._updateWorkspaceContext();

                            // Auto-restart with exponential backoff
                            const delay = this._describeRestartDelay;
                            this._describeRestartDelay = Math.min(this._describeRestartDelay * 2, this._getPollingIntervalMs());
                            extensionLogOutputChannel.info(`Restarting describe --follow in ${delay}ms`);
                            this._describeRestartTimer = setTimeout(() => {
                                this._describeRestartTimer = undefined;
                                if (!this._disposed) {
                                    this._startDescribeWatch();
                                }
                            }, delay);
                        }
                    }
                    this._describeRestarting = false;
                },
                errorCallback: (error) => {
                    extensionLogOutputChannel.warn(`aspire describe --follow error: ${error.message}`);
                    this._describeProcess = undefined;
                    if (!this._disposed && !this._describeRestarting) {
                        this._setError(errorFetchingAppHosts(error.message));
                    }
                }
            });
        }).catch(error => {
            extensionLogOutputChannel.warn(`Failed to start describe watch: ${error}`);
            this._setError(errorFetchingAppHosts(String(error)));
        });
    }

    private _stopDescribeWatch(): void {
        if (this._describeRestartTimer) {
            clearTimeout(this._describeRestartTimer);
            this._describeRestartTimer = undefined;
        }
        if (this._describeProcess) {
            this._describeRestarting = true;
            this._describeProcess.kill();
            this._describeProcess = undefined;
        }
    }

    private _handleDescribeLine(line: string): void {
        const trimmed = line.trim();
        if (!trimmed) {
            return;
        }

        try {
            const resource: ResourceJson = JSON.parse(trimmed);
            if (resource.name) {
                this._workspaceResources.set(resource.name, resource);
                this._describeReceivedData = true;
                this._setError(undefined);
                this._describeRestartDelay = 5000; // Reset backoff on successful data
                this._updateWorkspaceContext();
            }
        } catch (e) {
            extensionLogOutputChannel.warn(`Failed to parse describe NDJSON line: ${e}`);
        }
    }

    private _updateWorkspaceContext(): void {
        const hasResources = this._workspaceResources.size > 0;
        vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasResources);
        this._onDidChangeData.fire();
    }

    // ── Global mode: ps polling ──

    private _startPsPolling(): void {
        this._stopPolling();
        const intervalMs = this._getPollingIntervalMs();
        this._fetchAppHosts();
        this._pollingInterval = setInterval(() => {
            if (!this._disposed) {
                this._fetchAppHosts();
            }
        }, intervalMs);
    }

    private _stopPolling(): void {
        if (this._pollingInterval) {
            clearInterval(this._pollingInterval);
            this._pollingInterval = undefined;
        }
    }

    private _getPollingIntervalMs(): number {
        const config = vscode.workspace.getConfiguration('aspire');
        const interval = config.get<number>('globalAppHostsPollingInterval', 30000);
        return Math.max(interval, 1000);
    }

    private _fetchAppHosts(): void {
        if (this._fetchInProgress) {
            return;
        }
        this._fetchInProgress = true;

        const args = ['ps', '--format', 'json'];
        if (this._supportsResources) {
            args.push('--resources');
        }
        this._runPsCommand(args, (code, stdout, stderr) => {
            if (code === 0) {
                this._setError(undefined);
                this._handlePsOutput(stdout);
                this._fetchInProgress = false;
            } else if (this._supportsResources) {
                this._supportsResources = false;
                extensionLogOutputChannel.info('aspire ps --resources failed, falling back to aspire ps without --resources');
                this._runPsCommand(['ps', '--format', 'json'], (retryCode, retryStdout, retryStderr) => {
                    if (retryCode === 0) {
                        this._setError(undefined);
                        this._handlePsOutput(retryStdout);
                    } else {
                        this._setError(errorFetchingAppHosts(retryStderr || `exit code ${retryCode}`));
                    }
                    this._fetchInProgress = false;
                });
            } else {
                this._setError(errorFetchingAppHosts(stderr || `exit code ${code}`));
                this._fetchInProgress = false;
            }
        });
    }

    private _setError(message: string | undefined): void {
        const hasError = message !== undefined;
        if (this._errorMessage !== message) {
            this._errorMessage = message;
            if (message) {
                extensionLogOutputChannel.warn(message);
            }
            vscode.commands.executeCommand('setContext', 'aspire.fetchAppHostsError', hasError);
            this._onDidChangeData.fire();
        }
    }

    private _handlePsOutput(stdout: string): void {
        try {
            const parsed: AppHostDisplayInfo[] = JSON.parse(stdout);
            const changed = JSON.stringify(parsed) !== JSON.stringify(this._appHosts);
            this._appHosts = parsed;

            if (changed) {
                vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', parsed.length === 0);
                this._onDidChangeData.fire();
            }
        } catch (e) {
            extensionLogOutputChannel.warn(`Failed to parse aspire ps output: ${e}`);
        }
    }

    private async _runPsCommand(args: string[], callback: (code: number, stdout: string, stderr: string) => void): Promise<void> {
        const cliPath = await this._terminalProvider.getAspireCliExecutablePath();

        let stdout = '';
        let stderr = '';
        let callbackInvoked = false;

        spawnCliProcess(this._terminalProvider, cliPath, args, {
            noExtensionVariables: true,
            stdoutCallback: (data) => { stdout += data; },
            stderrCallback: (data) => { stderr += data; },
            exitCallback: (code) => {
                if (!callbackInvoked) {
                    callbackInvoked = true;
                    callback(code ?? 1, stdout, stderr);
                }
            },
            errorCallback: (error) => {
                extensionLogOutputChannel.warn(errorFetchingAppHosts(error.message));
                if (!callbackInvoked) {
                    callbackInvoked = true;
                    callback(1, stdout, stderr || error.message);
                }
            }
        });
    }
}

export function shortenPath(filePath: string): string {
    const fileName = filePath.split(/[/\\]/).pop() ?? filePath;

    if (fileName.endsWith('.csproj')) {
        return fileName;
    }

    // For single-file AppHosts (.cs), show parent/filename
    const parts = filePath.split(/[/\\]/);
    if (parts.length >= 2) {
        return `${parts[parts.length - 2]}/${fileName}`;
    }

    return fileName;
}
