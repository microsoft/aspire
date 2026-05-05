import * as vscode from 'vscode';
import * as path from 'path';
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

export interface ResourceHealthReportJson {
    status: string | null;
    description: string | null;
    exceptionMessage: string | null;
}

export interface ResourceJson {
    name: string;
    displayName: string | null;
    resourceType: string;
    state: string | null;
    stateStyle: string | null;
    healthStatus: string | null;
    healthReports: Record<string, ResourceHealthReportJson> | null;
    exitCode: number | null;
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
 *    via NDJSON.  Only active while the tree-view panel is visible **and**
 *    workspace mode is selected.
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
    private _appHostFileOpen = false;

    // ── Workspace mode state (describe --follow) ──
    private _workspaceResources: Map<string, ResourceJson> = new Map();
    private _describeProcess: ChildProcessWithoutNullStreams | undefined;
    private _describeRestartDelay = 5000;
    private _describeRestartTimer: ReturnType<typeof setTimeout> | undefined;
    private _describeReceivedData = false;
    private _describeStartPending = false;
    private _describeStartVersion = 0;

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

    // ── Loading state ──
    private _loadingWorkspace = true;
    private _loadingGlobal = true;

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
        this._setError(undefined);
        this._updateLoadingContext();
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

    /**
     * Signals whether at least one visible editor currently shows an AppHost file.
     *
     * When `true`, the repository will run the same data-source(s) it would when the
     * tree-view panel is visible.  This lets code-lens decorations on a freshly-created
     * AppHost file show live resource state without the user first opening the panel.
     */
    setAppHostFileOpen(open: boolean): void {
        if (this._appHostFileOpen === open) {
            return;
        }
        this._appHostFileOpen = open;
        this._syncPolling();
    }

    refresh(): void {
        this._stopDescribeWatch();
        this._workspaceResources.clear();
        this._setError(undefined);
        this._updateWorkspaceContext();
        this._describeRestartDelay = 5000;
        if (this._shouldWatchWorkspace) {
            this._startDescribeWatch();
        }
        if (this._shouldPoll) {
            this._fetchAppHosts();
        }
    }

    activate(): void {
        vscode.commands.executeCommand('setContext', 'aspire.viewMode', this._viewMode);
        this._syncPolling();
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

    /** Either source is active when the panel is visible **or** an AppHost file is open in the editor. */
    private get _dataActive(): boolean {
        return this._panelVisible || this._appHostFileOpen;
    }

    private get _shouldPoll(): boolean {
        return this._dataActive && this._viewMode === 'global';
    }

    private get _shouldWatchWorkspace(): boolean {
        return this._dataActive && this._viewMode === 'workspace';
    }

    private _syncPolling(): void {
        if (this._disposed) {
            return;
        }

        if (this._shouldWatchWorkspace) {
            this._startDescribeWatch();
        } else {
            this._stopDescribeWatch({ clearWorkspaceResources: true });
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
                            const appHostCandidates = parsed.all_project_file_candidates.filter((candidate: unknown): candidate is string => typeof candidate === 'string');
                            const appHostPath = parsed.selected_project_file
                                ?? (appHostCandidates.length === 1 ? appHostCandidates[0] : null);
                            if (appHostPath) {
                                this._workspaceAppHostPath = appHostPath;
                                const appHostLabels = shortenPaths(appHostCandidates);
                                const candidateIndex = appHostCandidates.indexOf(appHostPath);
                                this._workspaceAppHostName = candidateIndex >= 0 ? appHostLabels[candidateIndex] : shortenPath(appHostPath);
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
        if (this._describeProcess || this._describeStartPending || this._disposed) {
            return;
        }

        this._loadingWorkspace = true;
        this._updateLoadingContext();
        this._describeStartPending = true;
        const startVersion = ++this._describeStartVersion;

        this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
            if (this._disposed || !this._shouldWatchWorkspace || startVersion !== this._describeStartVersion) {
                return;
            }

            const args = ['describe', '--follow', '--format', 'json'];

            extensionLogOutputChannel.info('Starting aspire describe --follow for workspace resources');

            this._describeReceivedData = false;
            const describeProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                noExtensionVariables: true,
                lineCallback: (line) => {
                    if (this._describeProcess !== describeProcess) {
                        return;
                    }
                    this._handleDescribeLine(line);
                },
                exitCallback: (code) => {
                    if (this._describeProcess !== describeProcess) {
                        return;
                    }

                    extensionLogOutputChannel.info(`aspire describe --follow exited with code ${code}`);
                    this._describeProcess = undefined;

                    if (!this._disposed) {
                        if (!this._describeReceivedData && code !== 0) {
                            // The process exited with a non-zero code without ever producing valid data.
                            // This is expected when no apphost is running. Don't set the error state
                            // since that would show the "CLI not supported" banner; instead just show
                            // the normal "no running apphost" welcome.
                            extensionLogOutputChannel.warn('aspire describe --follow exited without producing data; no running apphost or CLI may not support this feature.');
                            this._workspaceResources.clear();
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
                                if (!this._disposed && this._shouldWatchWorkspace) {
                                    this._startDescribeWatch();
                                }
                            }, delay);
                        }
                    }
                },
                errorCallback: (error) => {
                    if (this._describeProcess !== describeProcess) {
                        return;
                    }

                    extensionLogOutputChannel.warn(`aspire describe --follow error: ${error.message}`);
                    this._describeProcess = undefined;
                    if (!this._disposed) {
                        this._loadingWorkspace = false;
                        this._updateLoadingContext();
                        this._setError(errorFetchingAppHosts(error.message));
                    }
                }
            });
            this._describeProcess = describeProcess;
        }).catch(error => {
            if (this._disposed || !this._shouldWatchWorkspace || startVersion !== this._describeStartVersion) {
                return;
            }
            extensionLogOutputChannel.warn(`Failed to start describe watch: ${error}`);
            this._loadingWorkspace = false;
            this._updateLoadingContext();
            this._setError(errorFetchingAppHosts(String(error)));
        }).finally(() => {
            if (startVersion === this._describeStartVersion) {
                this._describeStartPending = false;
            }
        });
    }

    private _stopDescribeWatch(options?: { clearWorkspaceResources?: boolean }): void {
        this._describeStartVersion++;
        this._describeStartPending = false;
        if (this._describeRestartTimer) {
            clearTimeout(this._describeRestartTimer);
            this._describeRestartTimer = undefined;
        }
        if (this._describeProcess) {
            const describeProcess = this._describeProcess;
            extensionLogOutputChannel.info('Stopping aspire describe --follow for workspace resources');
            this._describeProcess = undefined;
            describeProcess.kill();
        }
        if (options?.clearWorkspaceResources) {
            this._clearWorkspaceResources();
        }
    }

    private _clearWorkspaceResources(): void {
        if (this._workspaceResources.size === 0) {
            return;
        }

        this._workspaceResources.clear();
        this._updateWorkspaceContext();
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
        if (this._loadingWorkspace) {
            this._loadingWorkspace = false;
            this._updateLoadingContext();
        }
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
            extensionLogOutputChannel.info(`aspire ps polling stopped`);
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
                        this._loadingGlobal = false;
                        this._updateLoadingContext();
                        this._setError(errorFetchingAppHosts(retryStderr || `exit code ${retryCode}`));
                    }
                    this._fetchInProgress = false;
                });
            } else {
                this._loadingGlobal = false;
                this._updateLoadingContext();
                this._setError(errorFetchingAppHosts(stderr || `exit code ${code}`));
                this._fetchInProgress = false;
            }
        });
    }

    private _updateLoadingContext(): void {
        const isLoading = this._viewMode === 'workspace' ? this._loadingWorkspace : this._loadingGlobal;
        vscode.commands.executeCommand('setContext', 'aspire.loading', isLoading);
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

            if (this._loadingGlobal) {
                this._loadingGlobal = false;
                this._updateLoadingContext();
            }

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
    return shortenPaths([filePath])[0] ?? filePath;
}

const projectFileExtensions = new Set(['.csproj', '.fsproj', '.vbproj']);

export function shortenPaths(filePaths: readonly string[]): string[] {
    const states: ShortenedPathState[] = [];
    const stateByPath = new Map<string, ShortenedPathState>();

    for (const filePath of filePaths) {
        const pathKey = getComparisonKey(filePath);
        let state = stateByPath.get(pathKey);
        if (!state) {
            state = createShortenedPathState(filePath);
            stateByPath.set(pathKey, state);
            states.push(state);
        }
    }

    while (true) {
        const duplicateLabels = new Set<string>();
        const seenLabels = new Set<string>();

        for (const state of states) {
            const labelKey = getComparisonKey(state.label);
            if (seenLabels.has(labelKey)) {
                duplicateLabels.add(labelKey);
            } else {
                seenLabels.add(labelKey);
            }
        }

        if (duplicateLabels.size === 0) {
            break;
        }

        for (const state of states) {
            if (duplicateLabels.has(getComparisonKey(state.label))) {
                expandShortenedPathState(state);
            }
        }
    }

    return filePaths.map(filePath => stateByPath.get(getComparisonKey(filePath))?.label ?? filePath);
}

interface ShortenedPathState {
    originalPath: string;
    segments: string[];
    depth: number;
    label: string;
}

function createShortenedPathState(filePath: string): ShortenedPathState {
    const normalized = filePath.replace(/\\/g, '/').replace(/\/+$/, '');
    const segments = normalized.split('/');
    const fileName = segments[segments.length - 1] || filePath;
    const extension = path.extname(fileName).toLowerCase();
    const isProjectFile = projectFileExtensions.has(extension);
    const depth = !isProjectFile && segments.length >= 2 ? 2 : 1;

    return {
        originalPath: filePath,
        segments,
        depth,
        label: depth >= 2 ? joinPathSegments(segments.slice(-depth)) : fileName,
    };
}

function expandShortenedPathState(state: ShortenedPathState): void {
    state.depth++;

    if (state.depth >= state.segments.length) {
        state.label = state.originalPath;
        return;
    }

    const firstCandidateIndex = state.segments.length - state.depth;
    const firstCandidateSegment = state.segments[firstCandidateIndex];
    if (firstCandidateSegment.length === 0 || isWindowsDriveSegment(firstCandidateSegment)) {
        state.label = state.originalPath;
        return;
    }

    state.label = joinPathSegments(state.segments.slice(firstCandidateIndex));
}

function joinPathSegments(segments: readonly string[]): string {
    return segments.join('/');
}

function isWindowsDriveSegment(segment: string): boolean {
    return /^[a-zA-Z]:$/.test(segment);
}

function getComparisonKey(value: string): string {
    return process.platform === 'win32' ? value.toLowerCase() : value;
}
