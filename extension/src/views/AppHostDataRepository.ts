import * as vscode from 'vscode';
import * as path from 'path';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostDescribeMayNotBeSupported, aspireCliDescribeNotSupported, aspireDescribeMinimumVersion, errorFetchingAppHosts, workspaceViewSelectedMultipleAppHosts, workspaceViewSelectedSingleAppHost } from '../loc/strings';
import { AppHostCandidate, findAppHostsWithAspireLs, formatAppHostLanguage, isBuildableAppHostCandidate } from '../utils/workspace';

export interface ResourceUrlJson {
    name: string | null;
    displayName: string | null;
    url: string;
    isInternal: boolean;
}

export interface ResourceCommandJson {
    displayName?: string | null;
    description: string | null;
    visibility?: string | null;
    argumentInputs?: ResourceCommandArgumentInputJson[] | null;
}

// Resource command argument input types. Values match the strings emitted by the CLI
// JSON contract (ResourceCommandArgumentJson.InputType in
// src/Shared/Model/Serialization/ResourceJson.cs).
export const ResourceCommandInputType = {
    Text: 'Text',
    SecretText: 'SecretText',
    Choice: 'Choice',
    Boolean: 'Boolean',
    Number: 'Number',
} as const;

export type ResourceCommandInputType = typeof ResourceCommandInputType[keyof typeof ResourceCommandInputType];

// Mirrors the CLI JSON contract in src/Shared/Model/Serialization/ResourceJson.cs
// (`ResourceCommandArgumentJson`), populated by Aspire.Cli's ResourceSnapshotMapper.
export interface ResourceCommandArgumentInputJson {
    name: string;
    label: string | null;
    description: string | null;
    enableDescriptionMarkdown?: boolean;
    inputType: ResourceCommandInputType;
    required?: boolean;
    placeholder: string | null;
    value: string | null;
    options: Record<string, string | null> | null;
    allowCustomChoice?: boolean;
    disabled?: boolean;
    maxLength: number | null;
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
 *    via NDJSON for the selected workspace AppHost.  Only active while the
 *    tree-view panel is visible **and** workspace mode is selected.
 *  - `aspire ps` polling — periodically fetches running app hosts. In global
 *    mode this backs the full tree; in workspace mode it confirms whether the
 *    selected workspace AppHost is running when the resource stream is empty.
 */
export class AppHostDataRepository {
    private static readonly _processShutdownGracePeriodMs = 5000;

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

    // ── Running AppHost state (ps polling) ──
    private _appHosts: AppHostDisplayInfo[] = [];
    private _workspaceAppHost: AppHostDisplayInfo | undefined;
    private _pollingInterval: ReturnType<typeof setInterval> | undefined;
    private _psProcesses = new Set<ChildProcessWithoutNullStreams>();
    private _psFetchVersion = 0;
    private _supportsResources = true;
    private _supportsPsFollow = true;
    private _fetchInProgress = false;

    // ── Workspace app host (from aspire ls) ──
    // The singular fields track a selected/default workspace AppHost. The candidate
    // paths track every buildable AppHost found by `aspire ls`, so workspace-mode
    // `aspire ps` polling can filter and render multiple running workspace AppHosts.
    private _workspaceAppHostName: string | undefined;
    private _workspaceAppHostPath: string | undefined;
    private _workspaceAppHostCandidatePaths: string[] = [];
    private _workspaceAppHostDescription: string | undefined;
    private _workspaceAppHostDiscoveryComplete = false;
    private _workspaceAppHostDiscoveryUsesWorkspaceRoot = false;
    private _getAppHostsProcess: ChildProcessWithoutNullStreams | undefined;

    // ── Error state ──
    private _describeErrorMessage: string | undefined;
    private _psErrorMessage: string | undefined;
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

    get workspaceAppHost(): AppHostDisplayInfo | undefined {
        return this._workspaceAppHost;
    }

    get workspaceAppHostName(): string | undefined {
        return this._workspaceAppHostName;
    }

    get workspaceAppHostPath(): string | undefined {
        return this._workspaceAppHostPath;
    }

    get hasMultipleWorkspaceAppHosts(): boolean {
        return this._workspaceAppHostCandidatePaths.length > 1;
    }

    get workspaceAppHostDescription(): string | undefined {
        return this._workspaceAppHostDescription;
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
        this._clearErrors();
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
        this._clearErrors();
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
        if (this._getAppHostsProcess) {
            const getAppHostsProcess = this._getAppHostsProcess;
            this._getAppHostsProcess = undefined;
            this._terminateProcess(getAppHostsProcess, 'aspire ls');
        }
        this._configChangeDisposable.dispose();
        this._onDidChangeData.dispose();
    }

    // ── PS polling lifecycle ──

    /** Either source is active when the panel is visible **or** an AppHost file is open in the editor. */
    private get _dataActive(): boolean {
        return this._panelVisible || this._appHostFileOpen;
    }

    private get _shouldPoll(): boolean {
        // Workspace mode still polls ps after the selected AppHost path is known so
        // a running AppHost can be shown even when describe has no resources to emit.
        return this._dataActive && (this._viewMode === 'global' || this._workspaceAppHostCandidatePaths.length > 0);
    }

    private get _shouldWatchWorkspace(): boolean {
        if (!this._dataActive || this._viewMode !== 'workspace') {
            return false;
        }

        if (!this._workspaceAppHostDiscoveryUsesWorkspaceRoot) {
            return true;
        }

        return this._workspaceAppHostDiscoveryComplete && this._workspaceAppHostPath !== undefined;
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

        if (this._viewMode !== 'workspace' || !this._dataActive) {
            this._clearWorkspaceAppHost();
        }

        if (this._shouldPoll) {
            this._startPsPolling();
        } else {
            this._stopPolling();
        }
    }

    // ── Workspace app host (from aspire ls) ──

    private _fetchWorkspaceAppHost(): void {
        const workspaceFolders = vscode.workspace.workspaceFolders;
        if (!workspaceFolders || workspaceFolders.length === 0) {
            this._workspaceAppHostDiscoveryComplete = true;
            return;
        }
        const rootFolder = workspaceFolders[0];
        this._workspaceAppHostDiscoveryUsesWorkspaceRoot = true;

        extensionLogOutputChannel.info('Fetching workspace apphost via: aspire ls');

        this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
            if (this._disposed) {
                return;
            }

            const discovery = findAppHostsWithAspireLs(this._terminalProvider, cliPath, rootFolder);
            this._getAppHostsProcess = discovery.process;
            discovery.result.then(result => {
                if (this._disposed) {
                    return;
                }

                this._getAppHostsProcess = undefined;
                this._workspaceAppHostDiscoveryComplete = true;
                this._handleWorkspaceAppHostCandidates(result.app_host_candidates, result.selected_project_file);
            }).catch(error => {
                this._getAppHostsProcess = undefined;
                this._workspaceAppHostDiscoveryComplete = true;
                extensionLogOutputChannel.warn(`aspire ls error: ${error}`);
                this._syncPolling();
            });
        }).catch(error => {
            this._workspaceAppHostDiscoveryComplete = true;
            extensionLogOutputChannel.warn(`Failed to fetch workspace apphost: ${error}`);
            this._syncPolling();
        });
    }

    private _handleWorkspaceAppHostCandidates(appHostCandidates: readonly AppHostCandidate[], selectedAppHostPath: string | null): void {
        const buildableAppHostCandidates = appHostCandidates.filter(isBuildableAppHostCandidate);

        if (buildableAppHostCandidates.length > 1) {
            this._setWorkspaceAppHostCandidatePaths(buildableAppHostCandidates);
            if (selectedAppHostPath) {
                this._setWorkspaceAppHostPath(selectedAppHostPath, buildableAppHostCandidates);
            } else {
                this._clearWorkspaceAppHostSelection();
            }
            this._workspaceAppHostDescription = workspaceViewSelectedMultipleAppHosts(buildableAppHostCandidates.length);
            extensionLogOutputChannel.info(`Workspace contains ${buildableAppHostCandidates.length} buildable AppHosts; keeping workspace view`);
            this.setViewMode('workspace');
            this._syncPolling();
            this._onDidChangeData.fire();
            return;
        }

        const selectedAppHostCandidate = selectedAppHostPath
            ? buildableAppHostCandidates.find(candidate => isMatchingAppHostPath(candidate.path, selectedAppHostPath))
            : buildableAppHostCandidates[0];
        if (selectedAppHostCandidate) {
            this._setWorkspaceAppHostCandidatePaths(buildableAppHostCandidates);
            this._setWorkspaceAppHostPath(selectedAppHostCandidate.path, buildableAppHostCandidates);
            this._workspaceAppHostDescription = workspaceViewSelectedSingleAppHost(formatAppHostLanguage(selectedAppHostCandidate.language));
            extensionLogOutputChannel.info(`Workspace apphost resolved: ${selectedAppHostCandidate.path} (${selectedAppHostCandidate.language}, ${selectedAppHostCandidate.status})`);
            this._syncPolling();
            this._onDidChangeData.fire();
        } else if (appHostCandidates.length > 0) {
            extensionLogOutputChannel.info(`aspire ls found ${appHostCandidates.length} AppHost candidates, but none are buildable`);
        }
    }

    private _setWorkspaceAppHostPath(appHostPath: string, appHostCandidates: readonly AppHostCandidate[]): void {
        this._workspaceAppHostPath = appHostPath;
        const appHostCandidatePaths = appHostCandidates.map(candidate => candidate.path);
        const appHostLabels = shortenPaths(appHostCandidatePaths);
        const candidateIndex = appHostCandidatePaths.findIndex(candidatePath => isMatchingAppHostPath(candidatePath, appHostPath));
        this._workspaceAppHostName = candidateIndex >= 0 ? appHostLabels[candidateIndex] : shortenPath(appHostPath);
    }

    private _setWorkspaceAppHostCandidatePaths(appHostCandidates: readonly AppHostCandidate[]): void {
        this._workspaceAppHostCandidatePaths = appHostCandidates.map(candidate => candidate.path);
    }

    private _clearWorkspaceAppHostSelection(): void {
        this._workspaceAppHostPath = undefined;
        this._workspaceAppHostName = undefined;
    }

    private _clearWorkspaceAppHostDiscovery(): void {
        this._clearWorkspaceAppHostSelection();
        this._workspaceAppHostCandidatePaths = [];
        this._workspaceAppHostDescription = undefined;
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
            if (this._workspaceAppHostPath) {
                args.push('--apphost', this._workspaceAppHostPath);
            }

            extensionLogOutputChannel.info('Starting aspire describe --follow for workspace resources');

            this._describeReceivedData = false;
            const describeNonJsonLines: string[] = [];
            let describeStderr = '';
            const describeProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                noExtensionVariables: true,
                lineCallback: (line) => {
                    if (this._describeProcess !== describeProcess) {
                        return;
                    }
                    const handled = this._handleDescribeLine(line);
                    if (!handled && describeNonJsonLines.length < 20) {
                        describeNonJsonLines.push(line);
                    }
                },
                stderrCallback: (data) => {
                    if (this._describeProcess !== describeProcess) {
                        return;
                    }
                    if (describeStderr.length < 4000) {
                        describeStderr += data;
                    }
                },
                exitCallback: (code) => {
                    if (this._describeProcess !== describeProcess) {
                        return;
                    }

                    extensionLogOutputChannel.info(`aspire describe --follow exited with code ${code}`);
                    this._describeProcess = undefined;

                    if (this._disposed) {
                        return;
                    }

                    // If this attempt never produced any data, surface a compatibility
                    // hint when we have enough context, but do not auto-restart on a 5s
                    // loop forever. The panel will refresh when the user explicitly
                    // retries or when activity resumes.
                    if (!this._describeReceivedData) {
                        extensionLogOutputChannel.warn(`aspire describe --follow exited (code ${code}) without producing data; not auto-restarting.`);
                        this._workspaceResources.clear();
                        this._setDescribeError(this._getDescribeNoDataError(code, describeNonJsonLines, describeStderr));
                        this._updateWorkspaceContext({ clearLoading: true });
                        return;
                    }

                    // We had a working stream that ended (apphost shut down). Reset and try
                    // once more with backoff in case the apphost is restarting; if that
                    // attempt also produces no data we'll fall into the branch above.
                    this._workspaceResources.clear();
                    this._setDescribeError(undefined);
                    this._updateWorkspaceContext();

                    const delay = this._describeRestartDelay;
                    this._describeRestartDelay = Math.min(this._describeRestartDelay * 2, this._getPollingIntervalMs());
                    extensionLogOutputChannel.info(`Restarting describe --follow in ${delay}ms`);
                    this._describeRestartTimer = setTimeout(() => {
                        this._describeRestartTimer = undefined;
                        if (!this._disposed && this._shouldWatchWorkspace) {
                            this._startDescribeWatch();
                        }
                    }, delay);
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
                        this._setDescribeError(errorFetchingAppHosts(error.message));
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
            this._setDescribeError(errorFetchingAppHosts(String(error)));
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
            this._terminateProcess(describeProcess, 'aspire describe --follow');
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

    private _clearWorkspaceAppHost(): void {
        if (this._workspaceAppHost === undefined) {
            return;
        }

        this._workspaceAppHost = undefined;
        if (this._viewMode === 'workspace') {
            this._updateWorkspaceContext();
        } else {
            this._onDidChangeData.fire();
        }
    }

    private _handleDescribeLine(line: string): boolean {
        const trimmed = line.trim();
        if (!trimmed) {
            return true;
        }

        try {
            const resource: ResourceJson = JSON.parse(trimmed);
            if (resource.name) {
                this._workspaceResources.set(resource.name, resource);
                this._describeReceivedData = true;
                this._setDescribeError(undefined);
                this._describeRestartDelay = 5000; // Reset backoff on successful data
                this._updateWorkspaceContext();
                return true;
            }
        } catch (e) {
            extensionLogOutputChannel.warn(`Failed to parse describe NDJSON line: ${e}`);
        }

        return false;
    }

    private _getDescribeNoDataError(exitCode: number | null, nonJsonLines: readonly string[], stderr: string): string | undefined {
        if (isDescribeUnsupportedOutput(nonJsonLines, stderr)) {
            return aspireCliDescribeNotSupported(aspireDescribeMinimumVersion);
        }

        if (exitCode !== 0 && this._workspaceAppHostPath) {
            return appHostDescribeMayNotBeSupported(aspireDescribeMinimumVersion);
        }

        return undefined;
    }

    private _updateWorkspaceContext(options?: { clearLoading?: boolean }): void {
        const hasWorkspaceAppHost = this._workspaceAppHost !== undefined;
        const hasResources = this._workspaceResources.size > 0;
        const hasRunningAppHosts = this._appHosts.length > 0;
        vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasWorkspaceAppHost && !hasResources && !hasRunningAppHosts);
        const clearLoading = options?.clearLoading ?? (hasResources || hasWorkspaceAppHost || hasRunningAppHosts);
        if (this._loadingWorkspace && clearLoading) {
            this._loadingWorkspace = false;
            this._updateLoadingContext();
        }
        this._onDidChangeData.fire();
    }

    // ── Global mode: ps polling ──

    private _startPsPolling(): void {
        this._stopPolling();
        if (this._supportsPsFollow) {
            this._startPsFollow();
            return;
        }

        this._startPsIntervalPolling();
    }

    private _startPsIntervalPolling(fetchImmediately = true): void {
        const intervalMs = this._getPollingIntervalMs();
        if (fetchImmediately) {
            this._fetchAppHosts();
        }
        this._pollingInterval = setInterval(() => {
            if (!this._disposed) {
                this._fetchAppHosts();
            }
        }, intervalMs);
    }

    private _stopPolling(): void {
        this._psFetchVersion++;
        this._fetchInProgress = false;
        if (this._pollingInterval) {
            clearInterval(this._pollingInterval);
            this._pollingInterval = undefined;
            extensionLogOutputChannel.info(`aspire ps polling stopped`);
        }
        for (const psProcess of this._psProcesses) {
            this._terminateProcess(psProcess, 'aspire ps');
        }
        this._psProcesses.clear();
    }

    private _getPollingIntervalMs(): number {
        const config = vscode.workspace.getConfiguration('aspire');
        const interval = config.get<number>('globalAppHostsPollingInterval', 30000);
        return Math.max(interval, 1000);
    }

    private async _startPsFollow(): Promise<void> {
        const fetchVersion = ++this._psFetchVersion;
        let cliPath: string;
        try {
            cliPath = await this._terminalProvider.getAspireCliExecutablePath();
        } catch (error) {
            if (this._isCurrentPsFetch(fetchVersion)) {
                const errorMessage = errorFetchingAppHosts(String(error));
                extensionLogOutputChannel.warn(errorMessage);
                this._setPsError(errorMessage);
                if (this._loadingGlobal) {
                    this._loadingGlobal = false;
                    this._updateLoadingContext();
                }
                this._supportsPsFollow = false;
                this._startPsIntervalPolling(false);
            }
            return;
        }

        if (!this._isCurrentPsFetch(fetchVersion)) {
            return;
        }

        let psProcess: ChildProcessWithoutNullStreams | undefined;
        let psProcessCompletedSynchronously = false;
        const removePsProcess = () => {
            if (psProcess) {
                this._psProcesses.delete(psProcess);
            } else {
                psProcessCompletedSynchronously = true;
            }
        };

        const args = ['ps', '--follow', '--format', 'json'];
        if (this._supportsResources) {
            args.push('--resources');
        }

        psProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
            noExtensionVariables: true,
            lineCallback: (line) => {
                if (!this._isCurrentPsFetch(fetchVersion) || line.trim().length === 0) {
                    return;
                }

                this._setPsError(undefined);
                this._handlePsOutput(line);
            },
            exitCallback: (code) => {
                removePsProcess();
                if (!this._isCurrentPsFetch(fetchVersion)) {
                    return;
                }

                if (code !== 0) {
                    this._supportsPsFollow = false;
                    extensionLogOutputChannel.info('aspire ps --follow failed, falling back to aspire ps polling');
                    this._startPsIntervalPolling();
                    return;
                }

                this._startPsIntervalPolling();
            },
            errorCallback: (error) => {
                removePsProcess();
                if (!this._isCurrentPsFetch(fetchVersion)) {
                    return;
                }

                extensionLogOutputChannel.warn(errorFetchingAppHosts(error.message));
                this._supportsPsFollow = false;
                this._startPsIntervalPolling();
            }
        });
        if (!psProcessCompletedSynchronously) {
            this._psProcesses.add(psProcess);
        }
    }

    private _fetchAppHosts(): void {
        if (this._fetchInProgress || this._disposed || !this._shouldPoll) {
            return;
        }
        this._fetchInProgress = true;
        const fetchVersion = ++this._psFetchVersion;

        const args = ['ps', '--format', 'json'];
        if (this._supportsResources) {
            args.push('--resources');
        }
        this._runPsCommand(args, fetchVersion, (code, stdout, stderr) => {
            if (code === 0) {
                this._setPsError(undefined);
                this._handlePsOutput(stdout);
                this._fetchInProgress = false;
            } else if (this._supportsResources) {
                this._supportsResources = false;
                extensionLogOutputChannel.info('aspire ps --resources failed, falling back to aspire ps without --resources');
                this._runPsCommand(['ps', '--format', 'json'], fetchVersion, (retryCode, retryStdout, retryStderr) => {
                    if (retryCode === 0) {
                        this._setPsError(undefined);
                        this._handlePsOutput(retryStdout);
                    } else {
                        this._loadingGlobal = false;
                        this._updateLoadingContext();
                        this._setPsError(errorFetchingAppHosts(retryStderr || `exit code ${retryCode}`));
                    }
                    this._fetchInProgress = false;
                });
            } else {
                this._loadingGlobal = false;
                this._updateLoadingContext();
                this._setPsError(errorFetchingAppHosts(stderr || `exit code ${code}`));
                this._fetchInProgress = false;
            }
        });
    }

    private _isCurrentPsFetch(fetchVersion: number): boolean {
        return !this._disposed && this._shouldPoll && fetchVersion === this._psFetchVersion;
    }

    private _updateLoadingContext(): void {
        const isLoading = this._viewMode === 'workspace' ? this._loadingWorkspace : this._loadingGlobal;
        vscode.commands.executeCommand('setContext', 'aspire.loading', isLoading);
    }

    private _clearErrors(): void {
        this._describeErrorMessage = undefined;
        this._psErrorMessage = undefined;
        this._updateErrorMessage();
    }

    private _setDescribeError(message: string | undefined): void {
        if (this._describeErrorMessage !== message) {
            this._describeErrorMessage = message;
            this._updateErrorMessage();
        }
    }

    private _setPsError(message: string | undefined): void {
        if (this._psErrorMessage !== message) {
            this._psErrorMessage = message;
            this._updateErrorMessage();
        }
    }

    private _updateErrorMessage(): void {
        const message = this._viewMode === 'workspace'
            ? this._describeErrorMessage ?? this._psErrorMessage
            : this._psErrorMessage ?? this._describeErrorMessage;
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
            if (this._viewMode === 'workspace') {
                this._handleWorkspacePsOutput(parsed);
                return;
            }

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

    private _handleWorkspacePsOutput(appHosts: readonly AppHostDisplayInfo[]): void {
        const workspaceAppHostPath = this._workspaceAppHostPath;
        const workspaceAppHosts = this._workspaceAppHostCandidatePaths.length > 0
            ? appHosts.filter(appHost => this._workspaceAppHostCandidatePaths.some(candidatePath => isMatchingAppHostPath(appHost.appHostPath, candidatePath)))
            : [];
        const workspaceAppHost = workspaceAppHostPath
            ? workspaceAppHosts.find(appHost => isMatchingAppHostPath(appHost.appHostPath, workspaceAppHostPath))
            : undefined;
        const changed = JSON.stringify(workspaceAppHosts) !== JSON.stringify(this._appHosts)
            || JSON.stringify(workspaceAppHost) !== JSON.stringify(this._workspaceAppHost);

        if (workspaceAppHostPath && !workspaceAppHost && (this._workspaceAppHost || this._workspaceResources.size > 0)) {
            this._stopDescribeWatch({ clearWorkspaceResources: true });
        }

        this._appHosts = workspaceAppHosts;
        this._workspaceAppHost = workspaceAppHost;

        if (changed) {
            this._updateWorkspaceContext();
        }
    }

    private async _runPsCommand(args: string[], fetchVersion: number, callback: (code: number, stdout: string, stderr: string) => void): Promise<void> {
        let cliPath: string;
        try {
            cliPath = await this._terminalProvider.getAspireCliExecutablePath();
        } catch (error) {
            if (this._isCurrentPsFetch(fetchVersion)) {
                const errorMessage = errorFetchingAppHosts(String(error));
                extensionLogOutputChannel.warn(errorMessage);
                this._setPsError(errorMessage);
                this._fetchInProgress = false;
                if (this._loadingGlobal) {
                    this._loadingGlobal = false;
                    this._updateLoadingContext();
                }
            }
            return;
        }

        if (!this._isCurrentPsFetch(fetchVersion)) {
            return;
        }

        let stdout = '';
        let stderr = '';
        let callbackInvoked = false;

        let psProcess: ChildProcessWithoutNullStreams | undefined;
        let psProcessCompletedSynchronously = false;
        const removePsProcess = () => {
            if (psProcess) {
                this._psProcesses.delete(psProcess);
            } else {
                psProcessCompletedSynchronously = true;
            }
        };

        psProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
            noExtensionVariables: true,
            stdoutCallback: (data) => { stdout += data; },
            stderrCallback: (data) => { stderr += data; },
            exitCallback: (code) => {
                removePsProcess();
                if (!callbackInvoked) {
                    callbackInvoked = true;
                    if (this._isCurrentPsFetch(fetchVersion)) {
                        callback(code ?? 1, stdout, stderr);
                    }
                }
            },
            errorCallback: (error) => {
                removePsProcess();
                extensionLogOutputChannel.warn(errorFetchingAppHosts(error.message));
                if (!callbackInvoked) {
                    callbackInvoked = true;
                    if (this._isCurrentPsFetch(fetchVersion)) {
                        callback(1, stdout, stderr || error.message);
                    }
                }
            }
        });
        if (!psProcessCompletedSynchronously) {
            this._psProcesses.add(psProcess);
        }
    }

    private _terminateProcess(childProcess: ChildProcessWithoutNullStreams, description: string): void {
        let exited = childProcess.exitCode !== null || childProcess.signalCode !== null;
        let forceKillTimer: ReturnType<typeof setTimeout> | undefined;
        const cleanup = () => {
            exited = true;
            childProcess.off('close', cleanup);
            childProcess.off('exit', cleanup);
            if (forceKillTimer) {
                clearTimeout(forceKillTimer);
                forceKillTimer = undefined;
            }
        };

        if (!exited) {
            childProcess.once('close', cleanup);
            childProcess.once('exit', cleanup);
        } else {
            return;
        }

        try {
            if (!childProcess.killed) {
                const signalSent = childProcess.kill();
                if (!signalSent) {
                    cleanup();
                    return;
                }
            }
        } catch (error) {
            extensionLogOutputChannel.warn(`Failed to stop ${description}: ${error}`);
            cleanup();
            return;
        }

        if (!exited) {
            forceKillTimer = setTimeout(() => {
                if (exited) {
                    return;
                }

                extensionLogOutputChannel.warn(`${description} did not exit within ${AppHostDataRepository._processShutdownGracePeriodMs}ms; forcing termination.`);
                try {
                    const signalSent = childProcess.kill('SIGKILL');
                    if (!signalSent) {
                        cleanup();
                    }
                } catch (error) {
                    extensionLogOutputChannel.warn(`Failed to force stop ${description}: ${error}`);
                    cleanup();
                }
            }, AppHostDataRepository._processShutdownGracePeriodMs);
            forceKillTimer.unref();
        }
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

function isDescribeUnsupportedOutput(nonJsonLines: readonly string[], stderr: string): boolean {
    const output = [...nonJsonLines, stderr].join('\n').toLowerCase();
    if (!output) {
        return false;
    }

    return (output.includes('usage:') && output.includes('commands:'))
        || output.includes('unknown command')
        || output.includes('unrecognized command')
        || output.includes('unrecognized option')
        || output.includes('is not a recognized command');
}

function isMatchingAppHostPath(left: string | undefined, right: string | undefined): boolean {
    if (!left || !right) {
        return false;
    }

    const normalizedLeft = getComparisonKey(path.normalize(left));
    const normalizedRight = getComparisonKey(path.normalize(right));
    if (normalizedLeft === normalizedRight) {
        return true;
    }

    // `aspire extension get-apphosts` resolves a project file while `aspire ps`
    // can report the AppHost source file. Match by directory as a fallback to
    // mirror the CodeLens AppHost resolution strategy.
    return getComparisonKey(path.dirname(normalizedLeft)) === getComparisonKey(path.dirname(normalizedRight));
}
