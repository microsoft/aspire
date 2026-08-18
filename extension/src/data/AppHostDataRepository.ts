import * as vscode from 'vscode';
import * as path from 'path';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess, terminateCliProcess } from '../utils/process/cliProcess';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostDescribeMayNotBeSupported, appHostDiscoveryProgress, appHostPathMustBeNonEmptyAbsolute, aspireCliDescribeNotSupported, aspireDescribeMinimumVersion, errorFetchingAppHosts, workspaceViewSelectedMultipleAppHosts, workspaceViewSelectedSingleAppHost } from '../loc/strings';
import { AppHostCandidate, AppHostDiscoveryService, CandidateAppHostDisplayInfo, formatAppHostLanguage, getWorkspaceAppHostProjectSearchResult, isBuildableAppHostCandidate } from '../utils/appHostDiscovery';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { describeIncludeDisabledCommandsCapability } from '../types/configInfo';
import { nonInteractiveCliEnvironment } from '../utils/environment';
import { getComparisonKey, isAppHostPathUnderFolder, isSameAppHostPath } from '../utils/paths/comparison';
import { FileSystemEntryDescriptor, FileSystemEntryDescriptorIndex, getFileSystemEntryDescriptor } from '../utils/paths/fileSystemIdentity';
import { shortenPath, shortenPaths } from '../utils/paths/shortening';
import { AppHostDisplayInfo, AspireCliFailedError, AspireCliParseError, DescribeSnapshotJson, ResourceCommandExecutionOutput, ResourceJson, ViewMode } from './appHostCliContracts';
import { AppHostCliRunner, isDescribeUnsupportedOutput, isIncludeDisabledCommandsUnsupportedOutput, oneShotOutputBufferLimit, parseCliJsonOutput, RunCliCommandOptions } from './appHostCliRunner';
import { isMatchingAppHostInstance, isMatchingAppHostPath, isPathInWorkspace } from './appHostPathMatching';
import { AppHostPsPoller } from './appHostPsPoller';
import { filterResourceCommandStatusOutput } from './resourceCommandStatusOutput';
import { getCliPathTargetForUri } from '../utils/cliPathVariables';

export * from './appHostCliContracts';
export { shortenPath, shortenPaths };
export { filterResourceCommandStatusOutput };
export { isAppHostPathUnderFolder, isMatchingAppHostPath };

interface WorkspaceFolderAppHostCandidates {
    readonly workspaceFolder: vscode.WorkspaceFolder;
    candidates: CandidateAppHostDisplayInfo[];
}

interface WorkspaceFolderDiscoveryError {
    readonly workspaceFolder: vscode.WorkspaceFolder;
    readonly error: unknown;
}

interface CombinedWorkspaceAppHostCandidates {
    appHostCandidates: AppHostCandidate[];
    selectedAppHostPath: string | null;
}

interface DescribeStream {
    appHostPath: string;
    process: ChildProcessWithoutNullStreams | undefined;
    resources: Map<string, ResourceJson>;
    receivedData: boolean;
    nonJsonLines: string[];
    stderr: string;
    restartTimer: ReturnType<typeof setTimeout> | undefined;
    restartDelay: number;
    version: number;
}

interface DescribeNoDataError {
    message: string | undefined;
    isCompatibilityError: boolean;
}

interface PostStopRefreshTimer {
    timer: ReturnType<typeof setTimeout>;
}

/**
 * Central data repository for app host and resource information.
 *
 * Owns two independent data sources:
 *  - `aspire describe --follow --apphost <path>` — one stream per running
 *    AppHost, all held in a single `_describeStreams` map and started by the
 *    single {@link _startDescribe} method. Every stream is an equal peer: a host
 *    is described only once `aspire ps` reports it running, and each stream
 *    merges its resources into `appHost.resources`.
 *  - `aspire ps` polling — periodically fetches running app hosts. In global
 *    mode this backs the full tree; in workspace mode it confirms whether the
 *    workspace AppHost is running and drives which hosts get described.
 */
export class AppHostDataRepository {
    private static readonly _appHostStopRefreshDelayMs = 400;
    private static readonly _appHostStopRefreshMaxAttempts = 75;
    private static readonly _oneShotOutputBufferLimit = oneShotOutputBufferLimit;
    private static readonly _streamedCandidateUpdateDebounceMs = 50;
    private static readonly _streamedCandidateUpdateMaxWaitMs = 250;
    private static readonly _workspaceAppHostDiscoveryConcurrency = 4;

    private readonly _onDidChangeData = new vscode.EventEmitter<void>();
    readonly onDidChangeData = this._onDidChangeData.event;

    // ── Mode / panel state ──
    private _viewMode: ViewMode = 'workspace';
    private _panelVisible = false;
    private _openAppHostPaths: readonly string[] = [];
    private _hasEverBeenDataActive = false;

    private readonly _configInfoProvider: ConfigInfoProvider;

    // ── Running AppHost state (ps polling) ──
    private _appHosts: AppHostDisplayInfo[] = [];
    // Cached JSON serialization of the app-host list rendered by the current view after the most
    // recent reconcile so _handlePsOutput can detect real changes. We can't compare raw `ps` output
    // directly because the in-memory state has merged resources, while `ps` no longer emits them
    // (#17479) — see _handlePsOutput for the rationale.
    private _appHostsSnapshot = '[]';
    private _postStopRefreshTimers = new Map<string, PostStopRefreshTimer>();
    private _runtimeSnapshotAfterWorkspaceDiscovery = false;
    private readonly _psPoller: AppHostPsPoller;
    private readonly _psPollerDisposable: vscode.Disposable;

    // ── Per-AppHost describe streams ──
    // Every running AppHost has one `aspire describe --follow --apphost <path>`
    // stream held here, keyed by appHostPath.
    private _describeStreams = new Map<string, DescribeStream>();

    // ── Workspace app host (from aspire ls) ──
    // The singular fields track a selected/default workspace AppHost. The candidate
    // paths track every buildable AppHost found by `aspire ls`, so workspace-mode
    // `aspire ps` polling can filter and render multiple running workspace AppHosts.
    private _workspaceAppHostName: string | undefined;
    private _workspaceAppHostPath: string | undefined;
    private _workspaceAppHostCandidatePaths: string[] = [];
    private readonly _workspaceFolderAppHostCandidates = new Map<string, CandidateAppHostDisplayInfo[]>();
    private _workspaceAppHostDescription: string | undefined;
    private _workspaceAppHostDiscoveryComplete = false;
    private _workspaceAppHostDiscoveryVersion = 0;
    private _workspaceAppHostDiscoveryInProgress = false;
    private _workspaceAppHostDiscoveryRefreshQueued = false;
    private _workspaceAppHostDiscoveryForceRefreshQueued = false;
    private _workspaceAppHostDiscoveryProgressResolve: (() => void) | undefined;
    private _workspaceAppHostDiscoveryCancellationSource: vscode.CancellationTokenSource | undefined;
    private readonly _appHostDiscoveryChangeDisposable: vscode.Disposable;
    private readonly _workspaceFoldersChangeDisposable: vscode.Disposable;
    private readonly _appHostDiscoveryService: AppHostDiscoveryService;
    private readonly _ownsAppHostDiscoveryService: boolean;

    // ── Error state ──
    private _describeErrorMessage: string | undefined;
    private _describeErrorIsCompatibility = false;
    private _describeErrorAppHostPath: string | undefined;
    private _psErrorMessage: string | undefined;
    private _errorMessage: string | undefined;
    private _errorIsCompatibility = false;

    // ── Loading state ──
    private _loadingWorkspace = true;
    private _loadingGlobal = true;

    private readonly _configChangeDisposable: vscode.Disposable;
    private _disposed = false;
    private readonly _cliRunner: AppHostCliRunner;

    constructor(private readonly _terminalProvider: AspireTerminalProvider, appHostDiscoveryService?: AppHostDiscoveryService, configInfoProvider?: ConfigInfoProvider) {
        this._cliRunner = new AppHostCliRunner(_terminalProvider);
        this._psPoller = new AppHostPsPoller(
            _terminalProvider,
            this._cliRunner,
            () => this._disposed,
            () => this._dataActive,
            () => this._clearPostStopRefreshTimers());
        this._psPollerDisposable = vscode.Disposable.from(
            this._psPoller.onDidReceivePsOutput(psOutput => this._handlePsOutput(psOutput.stdout, psOutput.canCompleteGlobalLoading)),
            this._psPoller.onDidChangePsError(message => this._setPsError(message)),
            this._psPoller.onDidRequestClearLoading(() => this._clearLoading()),
            this._psPoller.onDidStartPsFollow(() => this._handlePsFollowStarted()));
        this._configInfoProvider = configInfoProvider ?? new ConfigInfoProvider(_terminalProvider);
        this._appHostDiscoveryService = appHostDiscoveryService ?? new AppHostDiscoveryService(_terminalProvider, this._configInfoProvider);
        this._ownsAppHostDiscoveryService = appHostDiscoveryService === undefined;
        this._appHostDiscoveryChangeDisposable = this._appHostDiscoveryService.onDidChangeCandidates(workspaceFolder => {
            const workspaceFolders = vscode.workspace.workspaceFolders;
            if (workspaceFolders?.some(currentWorkspaceFolder =>
                currentWorkspaceFolder.uri.toString() === workspaceFolder.uri.toString())) {
                this._markWorkspaceAppHostDiscoveryPending();
                this._fetchWorkspaceAppHost();
            }
        });
        this._workspaceFoldersChangeDisposable = vscode.workspace.onDidChangeWorkspaceFolders(event => {
            this._removeWorkspaceFolderCandidates(event.removed);
            for (const workspaceFolder of event.removed) {
                this._appHostDiscoveryService.forgetWorkspaceFolder?.(workspaceFolder);
            }
            const forceRefresh = this._cancelWorkspaceAppHostDiscovery();
            this._markWorkspaceAppHostDiscoveryPending({ preserveCandidates: true });
            this._clearErrors();
            this._syncPolling();
            this._fetchWorkspaceAppHost(forceRefresh ? { forceRefresh: true } : undefined);
        });
        this._fetchWorkspaceAppHost();
        this._configChangeDisposable = vscode.workspace.onDidChangeConfiguration(e => {
            if ((e.affectsConfiguration('aspire.appHostsPollingInterval') || e.affectsConfiguration('aspire.globalAppHostsPollingInterval')) && this._dataActive) {
                this._psPoller.startPsPolling();
            }
        });
    }

    // ── Public accessors ──

    get viewMode(): ViewMode {
        return this._viewMode;
    }

    get workspaceResources(): readonly ResourceJson[] {
        const workspaceAppHost = this._getWorkspaceAppHost();
        if (!workspaceAppHost) {
            return [];
        }

        const stream = this._describeStreams.get(workspaceAppHost.appHostPath);
        return stream ? Array.from(stream.resources.values()) : [];
    }

    get appHosts(): readonly AppHostDisplayInfo[] {
        if (this._viewMode === 'workspace') {
            return this._appHosts.filter(appHost => this._isWorkspaceAppHost(appHost));
        }
        return this._appHosts;
    }

    get workspaceAppHost(): AppHostDisplayInfo | undefined {
        return this._getWorkspaceAppHost();
    }

    get workspaceAppHostName(): string | undefined {
        return this._workspaceAppHostName;
    }

    get workspaceAppHostPath(): string | undefined {
        return this._workspaceAppHostPath;
    }

    get workspaceAppHostCandidatePaths(): readonly string[] {
        return this._workspaceAppHostCandidatePaths;
    }

    get workspaceAppHostDescription(): string | undefined {
        return this._workspaceAppHostDescription;
    }

    get isLoading(): boolean {
        const isLoading = this._viewMode === 'workspace' ? this._loadingWorkspace : this._loadingGlobal;
        return this._dataActive && isLoading;
    }

    get isWorkspaceAppHostDiscoveryComplete(): boolean {
        return this._workspaceAppHostDiscoveryComplete;
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
        if (mode === 'workspace') {
            this._showWorkspaceAppHostDiscoveryProgress();
        } else {
            this._hideWorkspaceAppHostDiscoveryProgress();
        }
        vscode.commands.executeCommand('setContext', 'aspire.viewMode', mode);
        this._clearErrors();
        // Re-scope the current `aspire ps` snapshot for the new view
        this._handlePsSnapshot(this._appHosts, { force: true });
        this._updateLoadingContext();
        this._syncPolling();
    }

    setPanelVisible(visible: boolean): void {
        if (this._panelVisible === visible) {
            return;
        }
        const wasDataActive = this._dataActive;
        this._panelVisible = visible;
        if (visible) {
            this._showWorkspaceAppHostDiscoveryProgress();
        } else {
            this._hideWorkspaceAppHostDiscoveryProgress();
        }
        const becameDataActive = !wasDataActive && this._dataActive;
        const resumedFromInactive = becameDataActive && this._hasEverBeenDataActive;
        if (this._dataActive) {
            this._hasEverBeenDataActive = true;
        }
        this._syncPolling(resumedFromInactive);
    }

    /**
     * Reports the current set of open AppHost editor tabs — focused or backgrounded — so the sources
     * can follow AppHosts the user is working on, even outside a workspace folder.
     *
     * Any open AppHost tab both surfaces its running host in the workspace panel and keeps the data
     * sources alive (see {@link _dataActive}). Keying keep-alive on the *open* set rather than the
     * currently *visible* subset is deliberate: switching between editor tabs (which momentarily
     * changes which editors are visible) must not change the open set, otherwise `aspire ps`/`describe`
     * would wind down and immediately respawn, churning the CLI processes on every tab switch.
     *
     * Data sources wind down only once the panel is hidden and no AppHost tab remains open.
     */
    setAppHostFilesOpen(openAppHostPaths: readonly string[]): void {
        const unchanged = openAppHostPaths.length === this._openAppHostPaths.length
            && openAppHostPaths.every((openPath, index) => openPath === this._openAppHostPaths[index]);
        if (unchanged) {
            return;
        }
        const wasDataActive = this._dataActive;
        this._openAppHostPaths = openAppHostPaths;
        const becameDataActive = !wasDataActive && this._dataActive;
        const resumedFromInactive = becameDataActive && this._hasEverBeenDataActive;
        if (this._dataActive) {
            this._hasEverBeenDataActive = true;
        }
        // Re-scope the displayed AppHosts against the new open-tab set right away.
        this._handlePsSnapshot(this._appHosts, { force: true });
        this._syncPolling(resumedFromInactive);
    }

    refresh(): void {
        this._clearErrors();
        this._setGlobalLoading(true);
        if (this._viewMode === 'workspace') {
            this._runtimeSnapshotAfterWorkspaceDiscovery = false;
            // A workspace refresh should observe AppHost/config files written by tools even when
            // the file watcher has not delivered an invalidation event yet.
            this._markWorkspaceAppHostDiscoveryPending();
            this._fetchWorkspaceAppHost({ forceRefresh: true });
        } else {
            this._loadingWorkspace = true;
        }
        this._reconcileDescribes();
        if (this._dataActive) {
            this._psPoller.refreshAppHostsFromAuthoritativeSnapshot();
        }
    }

    refreshRuntimeState(): void {
        if (this._disposed || !this._dataActive) {
            return;
        }

        const forceSnapshot = this._viewMode === 'workspace' && this._workspaceAppHostCandidatePaths.length === 0;
        if (this._dataActive && this._viewMode === 'workspace' && !this._workspaceAppHostDiscoveryComplete && this._workspaceAppHostCandidatePaths.length === 0) {
            this._runtimeSnapshotAfterWorkspaceDiscovery = true;
        }

        this._clearErrors();
        this._psPoller.refreshAppHostsFromAuthoritativeSnapshot(forceSnapshot);
    }

    requestAppHostStopRefresh(appHostPath: string): void {
        if (this._disposed || !this._dataActive || !appHostPath) {
            return;
        }

        const key = this._resolveStopRefreshKey(appHostPath);
        this._schedulePostStopRefresh(key, AppHostDataRepository._appHostStopRefreshMaxAttempts);
    }

    private _schedulePostStopRefresh(appHostPath: string, remainingAttempts: number): void {
        const existing = this._postStopRefreshTimers.get(appHostPath);
        if (existing) {
            clearTimeout(existing.timer);
        }

        const refreshTimer = setTimeout(() => {
            this._postStopRefreshTimers.delete(appHostPath);
            if (this._disposed || !this._dataActive) {
                return;
            }

            if (remainingAttempts < AppHostDataRepository._appHostStopRefreshMaxAttempts && !this._hasAppHost(appHostPath)) {
                return;
            }

            this._psPoller.refreshAppHostsFromAuthoritativeSnapshot();
            if (remainingAttempts > 1) {
                this._schedulePostStopRefresh(appHostPath, remainingAttempts - 1);
            }
        }, AppHostDataRepository._appHostStopRefreshDelayMs);
        (refreshTimer as { unref?: () => void }).unref?.();
        this._postStopRefreshTimers.set(appHostPath, { timer: refreshTimer });
    }

    private _hasAppHost(appHostPath: string): boolean {
        return this._findMatchingRunningAppHostPath(appHostPath) !== undefined;
    }

    private _resolveStopRefreshKey(appHostPath: string): string {
        const resolvedAppHostPath = this._findMatchingRunningAppHostPath(appHostPath) ?? appHostPath;
        for (const existingPath of this._postStopRefreshTimers.keys()) {
            if (isMatchingAppHostPath(existingPath, resolvedAppHostPath)) {
                return existingPath;
            }
        }

        return getComparisonKey(path.normalize(resolvedAppHostPath));
    }

    private _findMatchingRunningAppHostPath(appHostPath: string): string | undefined {
        const runningAppHostPaths = this._getRunningAppHostPaths();
        const exactMatch = runningAppHostPaths.find(runningPath => isMatchingAppHostPath(runningPath, appHostPath));
        if (exactMatch) {
            return exactMatch;
        }

        const folderMatches = runningAppHostPaths.filter(runningPath => isAppHostPathUnderFolder(runningPath, appHostPath));
        return folderMatches.length === 1 ? folderMatches[0] : undefined;
    }

    private _getRunningAppHostPaths(): string[] {
        const paths: string[] = [];
        for (const appHostPath of [
            ...this._appHosts.map(appHost => appHost.appHostPath),
        ]) {
            if (appHostPath && !paths.some(existingPath => isSameAppHostPath(existingPath, appHostPath))) {
                paths.push(appHostPath);
            }
        }

        return paths;
    }

    private _getWorkspaceAppHost(): AppHostDisplayInfo | undefined {
        if (this._viewMode !== 'workspace' || !this._dataActive || !this._workspaceAppHostPath) {
            return undefined;
        }

        return this._appHosts.find(appHost => isMatchingAppHostPath(appHost.appHostPath, this._workspaceAppHostPath));
    }

    private _clearPostStopRefreshTimers(): void {
        for (const state of this._postStopRefreshTimers.values()) {
            clearTimeout(state.timer);
        }
        this._postStopRefreshTimers.clear();
    }

    activate(): void {
        vscode.commands.executeCommand('setContext', 'aspire.viewMode', this._viewMode);
        this._syncPolling();
        this._updateLoadingContext();
    }

    async fetchRunningAppHostsOnce(cancellationToken?: vscode.CancellationToken): Promise<AppHostDisplayInfo[]> {
        const appHosts = await this._runCliJson<AppHostDisplayInfo[] | AppHostDisplayInfo>(
            'aspire ps',
            this._cliRunner.withNoLogo(['ps', '--format', 'json']),
            { cancellationToken });
        return Array.isArray(appHosts) ? appHosts : [appHosts];
    }

    async fetchAppHostsOnce(): Promise<AppHostDisplayInfo[]> {
        const appHostList = await this.fetchRunningAppHostsOnce();
        const appHostsWithResources = await Promise.allSettled(appHostList.map(async appHost => ({
            ...appHost,
            resources: await this._fetchAppHostResourcesOnce(appHost.appHostPath),
        })));

        return appHostsWithResources.map((result, index) => {
            if (result.status === 'fulfilled') {
                return result.value;
            }

            extensionLogOutputChannel.warn(`Failed to describe AppHost ${appHostList[index].appHostPath}: ${result.reason}`);
            return {
                ...appHostList[index],
                resources: [],
            };
        });
    }

    /**
     * Executes a resource command (e.g. start/stop/restart or a custom command) by spawning a
     * hidden `aspire resource <name> <command>` child process rather than typing into the visible
     * Aspire terminal. The CLI runs the command non-interactively over the AppHost backchannel,
     * routes human-readable status to stderr, and writes any returned command value (text/json/
     * markdown) to stdout, so callers can surface success/failure and rendered output inside VS Code.
     *
     * @param appHostPath Absolute path to the owning AppHost, or `undefined` to let the CLI resolve
     * the running AppHost itself (workspace mode with no explicit selection). A provided-but-invalid
     * path is rejected so we never spawn the CLI with a relative or blank `--apphost` value.
     * @param additionalArgs Extra CLI tokens collected from argument prompts. These already include
     * the `--` delimiter from {@link buildResourceCommandCliArgs}, which keeps them out of the spawn
     * diagnostics log (see redactCliSpawnArgs) so secret values are not persisted.
     */
    async runResourceCommand(resourceName: string, appHostPath: string | undefined, commandName: string, additionalArgs: readonly string[] = [], cancellationToken?: vscode.CancellationToken): Promise<ResourceCommandExecutionOutput> {
        const args = ['resource', resourceName, commandName, '--non-interactive'];
        let target;
        if (appHostPath !== undefined) {
            const trimmedAppHostPath = appHostPath.trim();
            if (!trimmedAppHostPath || !path.isAbsolute(trimmedAppHostPath)) {
                throw new Error(appHostPathMustBeNonEmptyAbsolute);
            }

            args.push('--apphost', trimmedAppHostPath);
            target = getCliPathTargetForUri(vscode.Uri.file(trimmedAppHostPath));
        }

        if (additionalArgs.length > 0) {
            args.push(...additionalArgs);
        }

        try {
            const output = await this._cliRunner.runCliCommand(`aspire resource ${commandName}`, args, {
                timeoutMs: null,
                stdoutBufferLimit: AppHostDataRepository._oneShotOutputBufferLimit,
                cancellationToken,
                env: nonInteractiveCliEnvironment,
                target,
            });
            return {
                stdout: filterResourceCommandStatusOutput(output.stdout, resourceName, commandName),
                stderr: output.stderr,
            };
        } catch (error) {
            if (error instanceof AspireCliFailedError) {
                throw new AspireCliFailedError(
                    error.command,
                    error.exitCode,
                    filterResourceCommandStatusOutput(error.stdout, resourceName, commandName),
                    filterResourceCommandStatusOutput(error.stderr, resourceName, commandName));
            }

            throw error;
        }
    }

    dispose(): void {
        this._disposed = true;
        this._clearPostStopRefreshTimers();
        this._psPoller.clearPendingAuthoritativeSnapshot();
        this._runtimeSnapshotAfterWorkspaceDiscovery = false;
        this._psPoller.stopPolling();
        this._stopAllDescribes();
        this._cliRunner.dispose();
        this._cancelWorkspaceAppHostDiscovery();
        this._configChangeDisposable.dispose();
        this._appHostDiscoveryChangeDisposable.dispose();
        this._workspaceFoldersChangeDisposable.dispose();
        this._psPollerDisposable.dispose();
        this._psPoller.dispose();
        this._onDidChangeData.dispose();
        if (this._ownsAppHostDiscoveryService) {
            this._appHostDiscoveryService.dispose();
        }
    }

    // ── PS polling lifecycle ──

    /**
     * Either source is active when the panel is visible **or** at least one AppHost tab is open
     * (visible or backgrounded).
     */
    private get _dataActive(): boolean {
        return this._panelVisible || this._openAppHostPaths.length > 0;
    }

    private _syncPolling(refreshBeforeFollowOnResume = false): void {
        if (this._disposed) {
            return;
        }

        if (this._dataActive) {
            const pollingActive = this._psPoller.pollingActive;
            if (!pollingActive) {
                this._psPoller.startPsPolling();
                if (refreshBeforeFollowOnResume && this._psPoller.supportsPsFollow && this._appHosts.length > 0) {
                    this._psPoller.refreshAppHostsFromAuthoritativeSnapshot();
                }
            }
        } else {
            this._psPoller.stopPolling();
        }

        this._reconcileDescribes();
    }

    // ── Workspace app host (from aspire ls) ──

    private _fetchWorkspaceAppHost(options?: { forceRefresh?: boolean }): void {
        const workspaceFolders = vscode.workspace.workspaceFolders;
        if (!workspaceFolders || workspaceFolders.length === 0) {
            this._cancelWorkspaceAppHostDiscovery();
            this._runtimeSnapshotAfterWorkspaceDiscovery = false;
            this._workspaceAppHostDiscoveryComplete = true;
            this._clearWorkspaceAppHostDiscovery();
            this._clearErrors();
            this._syncPolling();
            this._updateWorkspaceContext({ clearLoading: true });
            return;
        }

        if (this._workspaceAppHostDiscoveryInProgress) {
            this._workspaceAppHostDiscoveryRefreshQueued = true;
            this._workspaceAppHostDiscoveryForceRefreshQueued ||= options?.forceRefresh === true;
            // Let the current discovery finish so we don't start overlapping CLI work, but
            // prevent its now-stale result from briefly restoring old AppHost candidates.
            this._workspaceAppHostDiscoveryVersion++;
            return;
        }

        const discoveryVersion = ++this._workspaceAppHostDiscoveryVersion;
        const workspaceFolderSnapshot = [...workspaceFolders];
        const workspaceFolderCandidates: WorkspaceFolderAppHostCandidates[] = workspaceFolderSnapshot.map(workspaceFolder => ({
            workspaceFolder,
            candidates: [],
        }));

        extensionLogOutputChannel.info('Fetching workspace apphosts via shared AppHost discovery');

        const cancellationSource = new vscode.CancellationTokenSource();
        this._workspaceAppHostDiscoveryInProgress = true;
        this._workspaceAppHostDiscoveryCancellationSource = cancellationSource;
        this._showWorkspaceAppHostDiscoveryProgress();
        let incrementalCandidateUpdateTimer: ReturnType<typeof setTimeout> | undefined;
        let incrementalCandidateMaxWaitTimer: ReturnType<typeof setTimeout> | undefined;
        const cancelIncrementalCandidateUpdate = (): void => {
            if (incrementalCandidateUpdateTimer) {
                clearTimeout(incrementalCandidateUpdateTimer);
                incrementalCandidateUpdateTimer = undefined;
            }
            if (incrementalCandidateMaxWaitTimer) {
                clearTimeout(incrementalCandidateMaxWaitTimer);
                incrementalCandidateMaxWaitTimer = undefined;
            }
        };
        const applyIncrementalCandidateUpdates = (): void => {
            cancelIncrementalCandidateUpdate();
            if (cancellationSource.token.isCancellationRequested || !this._isCurrentWorkspaceDiscovery(discoveryVersion, workspaceFolderSnapshot)) {
                return;
            }

            const result = combineWorkspaceAppHostCandidates(workspaceFolderCandidates);
            const buildableAppHostCandidates = result.appHostCandidates.filter(isBuildableAppHostCandidate);
            if (buildableAppHostCandidates.length > 0) {
                this._setWorkspaceAppHostCandidatePaths(buildableAppHostCandidates);
                this._updateWorkspaceContext();
            }
        };
        const onIncrementalCandidate = (folderCandidates: WorkspaceFolderAppHostCandidates, candidate: CandidateAppHostDisplayInfo): void => {
            if (cancellationSource.token.isCancellationRequested || !this._isCurrentWorkspaceDiscovery(discoveryVersion, workspaceFolderSnapshot)) {
                return;
            }

            const existingCandidateIndex = folderCandidates.candidates.findIndex(existingCandidate => isMatchingAppHostPath(existingCandidate.path, candidate.path));
            if (existingCandidateIndex >= 0) {
                folderCandidates.candidates[existingCandidateIndex] = candidate;
            } else {
                folderCandidates.candidates.push(candidate);
            }
            this._workspaceFolderAppHostCandidates.set(
                folderCandidates.workspaceFolder.uri.toString(),
                [...folderCandidates.candidates]);

            // Use a trailing debounce to coalesce short bursts, but start the maximum-wait timer
            // only for the first candidate so a dense stream cannot postpone every tree update.
            if (!incrementalCandidateMaxWaitTimer) {
                incrementalCandidateMaxWaitTimer = setTimeout(applyIncrementalCandidateUpdates, AppHostDataRepository._streamedCandidateUpdateMaxWaitMs);
            }
            if (incrementalCandidateUpdateTimer) {
                clearTimeout(incrementalCandidateUpdateTimer);
            }
            incrementalCandidateUpdateTimer = setTimeout(applyIncrementalCandidateUpdates, AppHostDataRepository._streamedCandidateUpdateDebounceMs);
        };

        const discoverWorkspaceFolders = async (): Promise<WorkspaceFolderDiscoveryError[]> => {
            const errors: Array<WorkspaceFolderDiscoveryError | undefined> = new Array(workspaceFolderCandidates.length);
            let nextWorkspaceFolderIndex = 0;
            const discoverNextWorkspaceFolder = async (): Promise<void> => {
                while (nextWorkspaceFolderIndex < workspaceFolderCandidates.length) {
                    const workspaceFolderIndex = nextWorkspaceFolderIndex++;
                    const folderCandidates = workspaceFolderCandidates[workspaceFolderIndex];
                    try {
                        folderCandidates.candidates = await this._appHostDiscoveryService.discover(
                            folderCandidates.workspaceFolder,
                            options?.forceRefresh,
                            cancellationSource.token,
                            candidate => onIncrementalCandidate(folderCandidates, candidate));
                    } catch (error) {
                        folderCandidates.candidates = [];
                        errors[workspaceFolderIndex] = {
                            workspaceFolder: folderCandidates.workspaceFolder,
                            error,
                        };
                    }
                }
            };
            const workerCount = Math.min(
                AppHostDataRepository._workspaceAppHostDiscoveryConcurrency,
                workspaceFolderCandidates.length);
            await Promise.all(Array.from({ length: workerCount }, () => discoverNextWorkspaceFolder()));
            return errors.filter((error): error is WorkspaceFolderDiscoveryError => error !== undefined);
        };

        discoverWorkspaceFolders().then(errors => {
            cancelIncrementalCandidateUpdate();
            if (cancellationSource.token.isCancellationRequested || !this._isCurrentWorkspaceDiscovery(discoveryVersion, workspaceFolderSnapshot)) {
                return;
            }

            this._setWorkspaceFolderAppHostCandidates(workspaceFolderCandidates);
            const result = combineWorkspaceAppHostCandidates(workspaceFolderCandidates);
            const buildableAppHostCandidates = result.appHostCandidates.filter(isBuildableAppHostCandidate);
            if (errors.length > 0 && buildableAppHostCandidates.length === 0) {
                throw new Error(formatWorkspaceFolderDiscoveryError(errors[0]));
            }
            for (const error of errors) {
                extensionLogOutputChannel.warn(`Failed to fetch workspace apphost from one workspace folder: ${formatWorkspaceFolderDiscoveryError(error)}`);
            }
            this._workspaceAppHostDiscoveryComplete = true;
            this._handleWorkspaceAppHostCandidates(result.appHostCandidates, result.selectedAppHostPath);
        }).catch(error => {
            cancelIncrementalCandidateUpdate();
            if (cancellationSource.token.isCancellationRequested || !this._isCurrentWorkspaceDiscovery(discoveryVersion, workspaceFolderSnapshot)) {
                return;
            }

            if (error instanceof vscode.CancellationError) {
                return;
            }

            cancellationSource.cancel();
            this._workspaceAppHostDiscoveryComplete = true;
            extensionLogOutputChannel.warn(`Failed to fetch workspace apphost: ${error}`);
            this._runtimeSnapshotAfterWorkspaceDiscovery = false;
            this._clearWorkspaceAppHostDiscovery();
            this._setDescribeError(errorFetchingAppHosts(String(error)));
            this._updateWorkspaceContext({ clearLoading: true });
            this._syncPolling();
        }).finally(() => {
            cancelIncrementalCandidateUpdate();
            cancellationSource.dispose();
            if (this._workspaceAppHostDiscoveryCancellationSource !== cancellationSource) {
                return;
            }

            this._workspaceAppHostDiscoveryCancellationSource = undefined;
            this._workspaceAppHostDiscoveryInProgress = false;
            this._hideWorkspaceAppHostDiscoveryProgress();
            if (this._workspaceAppHostDiscoveryRefreshQueued && !this._disposed) {
                const forceRefresh = this._workspaceAppHostDiscoveryForceRefreshQueued;
                this._workspaceAppHostDiscoveryRefreshQueued = false;
                this._workspaceAppHostDiscoveryForceRefreshQueued = false;
                this._fetchWorkspaceAppHost({ forceRefresh });
            }
        });
    }

    private _cancelWorkspaceAppHostDiscovery(): boolean {
        const forceRefresh = this._workspaceAppHostDiscoveryForceRefreshQueued;
        this._workspaceAppHostDiscoveryRefreshQueued = false;
        this._workspaceAppHostDiscoveryForceRefreshQueued = false;
        this._runtimeSnapshotAfterWorkspaceDiscovery = false;
        this._workspaceAppHostDiscoveryCancellationSource?.cancel();
        this._workspaceAppHostDiscoveryCancellationSource?.dispose();
        this._workspaceAppHostDiscoveryCancellationSource = undefined;
        this._workspaceAppHostDiscoveryInProgress = false;
        this._hideWorkspaceAppHostDiscoveryProgress();
        return forceRefresh;
    }

    private _showWorkspaceAppHostDiscoveryProgress(): void {
        if (this._viewMode !== 'workspace'
            || !this._panelVisible
            || !this._workspaceAppHostDiscoveryInProgress
            || this._workspaceAppHostDiscoveryProgressResolve) {
            return;
        }

        // `Window` rather than `Notification`: discovery runs for as long as the workspace scan
        // takes and a progress notification cannot be dismissed while it is active, so it covers
        // the editor for the whole scan (https://github.com/microsoft/aspire/issues/19036).
        void vscode.window.withProgress({
            location: vscode.ProgressLocation.Window,
            title: appHostDiscoveryProgress,
            cancellable: false,
        }, () => new Promise<void>(resolve => {
            if (this._viewMode !== 'workspace'
                || !this._panelVisible
                || !this._workspaceAppHostDiscoveryInProgress) {
                resolve();
                return;
            }

            this._workspaceAppHostDiscoveryProgressResolve = resolve;
        }));
    }

    private _hideWorkspaceAppHostDiscoveryProgress(): void {
        const resolve = this._workspaceAppHostDiscoveryProgressResolve;
        this._workspaceAppHostDiscoveryProgressResolve = undefined;
        resolve?.();
    }

    private _markWorkspaceAppHostDiscoveryPending(options?: { preserveCandidates?: boolean }): void {
        this._workspaceAppHostDiscoveryComplete = false;
        if (!options?.preserveCandidates) {
            this._clearWorkspaceAppHostDiscovery();
        }
        this._loadingWorkspace = true;
        if (this._viewMode === 'workspace') {
            this._updateLoadingContext();
            this._updateWorkspaceContext({ clearLoading: false });
        }
    }

    private _handleWorkspaceAppHostCandidates(appHostCandidates: readonly AppHostCandidate[], selectedAppHostPath: string | null): void {
        const buildableAppHostCandidates = appHostCandidates.filter(isBuildableAppHostCandidate);

        if (buildableAppHostCandidates.length === 0) {
            const refreshRuntimeStateAfterDiscovery = this._runtimeSnapshotAfterWorkspaceDiscovery;
            this._runtimeSnapshotAfterWorkspaceDiscovery = false;
            this._clearWorkspaceAppHostDiscovery();
            if (appHostCandidates.length > 0) {
                extensionLogOutputChannel.info(`aspire ls found ${appHostCandidates.length} AppHost candidates, but none are buildable`);
            }
            this._clearErrors();
            this._syncPolling();
            if (refreshRuntimeStateAfterDiscovery && this._dataActive && this._viewMode === 'workspace') {
                this._psPoller.refreshAppHostsFromAuthoritativeSnapshot(true);
            }
            this._updateWorkspaceContext({ clearLoading: true });
            return;
        }

        this._runtimeSnapshotAfterWorkspaceDiscovery = false;

        if (buildableAppHostCandidates.length > 1) {
            this._setWorkspaceAppHostCandidatePaths(buildableAppHostCandidates);
            if (selectedAppHostPath) {
                this._setWorkspaceAppHostPath(selectedAppHostPath, buildableAppHostCandidates);
            } else {
                this._clearWorkspaceAppHostSelection();
            }
            this._workspaceAppHostDescription = workspaceViewSelectedMultipleAppHosts(buildableAppHostCandidates.length);
            extensionLogOutputChannel.info(`Workspace contains ${buildableAppHostCandidates.length} buildable AppHosts`);
        } else {
            const selectedAppHostCandidate = selectedAppHostPath
                ? buildableAppHostCandidates.find(candidate => isMatchingAppHostPath(candidate.path, selectedAppHostPath))
                : buildableAppHostCandidates[0];
            if (!selectedAppHostCandidate) {
                this._clearWorkspaceAppHostDiscovery();
                this._syncPolling();
                this._updateWorkspaceContext({ clearLoading: true });
                return;
            }

            this._setWorkspaceAppHostCandidatePaths(buildableAppHostCandidates);
            this._setWorkspaceAppHostPath(selectedAppHostCandidate.path, buildableAppHostCandidates);
            this._workspaceAppHostDescription = workspaceViewSelectedSingleAppHost(formatAppHostLanguage(selectedAppHostCandidate.language));
            extensionLogOutputChannel.info(`Workspace apphost resolved: ${selectedAppHostCandidate.path} (${selectedAppHostCandidate.language}, ${selectedAppHostCandidate.status})`);
        }

        this._syncPolling();
        const workspaceLoadingChanged = this._loadingWorkspace;
        this._loadingWorkspace = false;
        if (this._viewMode === 'workspace') {
            // Re-scope the stable ps snapshot now that discovery has resolved the candidates.
            this._handlePsSnapshot(this._appHosts, { force: true });
            if (workspaceLoadingChanged) {
                this._updateLoadingContext();
            }
        }
    }

    private _isCurrentWorkspaceDiscovery(discoveryVersion: number, workspaceFolders: readonly vscode.WorkspaceFolder[]): boolean {
        const currentWorkspaceFolders = vscode.workspace.workspaceFolders;
        if (this._disposed
            || discoveryVersion !== this._workspaceAppHostDiscoveryVersion
            || !currentWorkspaceFolders
            || currentWorkspaceFolders.length !== workspaceFolders.length) {
            return false;
        }

        return workspaceFolders.every((workspaceFolder, index) =>
            currentWorkspaceFolders[index].uri.toString() === workspaceFolder.uri.toString());
    }

    private _setWorkspaceAppHostPath(appHostPath: string, appHostCandidates: readonly AppHostCandidate[]): void {
        this._workspaceAppHostPath = appHostPath;
        const appHostCandidatePaths = appHostCandidates.map(candidate => candidate.path);
        const appHostLabels = shortenPaths(appHostCandidatePaths);
        const candidateIndex = appHostCandidatePaths.findIndex(candidatePath => isMatchingAppHostPath(candidatePath, appHostPath));
        this._workspaceAppHostName = candidateIndex >= 0 ? appHostLabels[candidateIndex] : shortenPath(appHostPath);
    }

    private _setWorkspaceAppHostPathFromCurrentCandidates(appHostPath: string): void {
        this._workspaceAppHostPath = appHostPath;
        const appHostLabels = shortenPaths(this._workspaceAppHostCandidatePaths);
        const candidateIndex = this._workspaceAppHostCandidatePaths.findIndex(candidatePath => isMatchingAppHostPath(candidatePath, appHostPath));
        this._workspaceAppHostName = candidateIndex >= 0 ? appHostLabels[candidateIndex] : shortenPath(appHostPath);
    }

    private _setWorkspaceAppHostCandidatePaths(appHostCandidates: readonly AppHostCandidate[]): void {
        this._workspaceAppHostCandidatePaths = appHostCandidates
            .map(candidate => candidate.path)
            .sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
    }

    private _clearWorkspaceAppHostSelection(): void {
        this._workspaceAppHostPath = undefined;
        this._workspaceAppHostName = undefined;
    }

    private _clearWorkspaceAppHostDiscovery(): void {
        this._clearWorkspaceAppHostSelection();
        this._workspaceAppHostCandidatePaths = [];
        this._workspaceFolderAppHostCandidates.clear();
        this._workspaceAppHostDescription = undefined;
    }

    private _removeWorkspaceFolderCandidates(removedWorkspaceFolders: readonly vscode.WorkspaceFolder[]): void {
        for (const workspaceFolder of removedWorkspaceFolders) {
            this._workspaceFolderAppHostCandidates.delete(workspaceFolder.uri.toString());
        }

        const workspaceFolderCandidates = (vscode.workspace.workspaceFolders ?? []).map(workspaceFolder => ({
            workspaceFolder,
            candidates: this._workspaceFolderAppHostCandidates.get(workspaceFolder.uri.toString()) ?? [],
        }));
        const result = combineWorkspaceAppHostCandidates(workspaceFolderCandidates);
        this._setWorkspaceAppHostCandidatePaths(result.appHostCandidates.filter(isBuildableAppHostCandidate));

        const selectedAppHostPath = this._workspaceAppHostPath;
        if (selectedAppHostPath) {
            if (this._workspaceAppHostCandidatePaths.some(candidatePath => isMatchingAppHostPath(candidatePath, selectedAppHostPath))) {
                this._setWorkspaceAppHostPathFromCurrentCandidates(selectedAppHostPath);
            } else {
                this._clearWorkspaceAppHostSelection();
            }
        }
        this._workspaceAppHostDescription = this._workspaceAppHostCandidatePaths.length > 1
            ? workspaceViewSelectedMultipleAppHosts(this._workspaceAppHostCandidatePaths.length)
            : undefined;
    }

    private _setWorkspaceFolderAppHostCandidates(workspaceFolderCandidates: readonly WorkspaceFolderAppHostCandidates[]): void {
        this._workspaceFolderAppHostCandidates.clear();
        for (const folderCandidates of workspaceFolderCandidates) {
            this._workspaceFolderAppHostCandidates.set(
                folderCandidates.workspaceFolder.uri.toString(),
                [...folderCandidates.candidates]);
        }
    }

    // ── describe --follow ──

    /**
     * Starts a single `aspire describe --follow --apphost <path>` stream, held in
     * {@link _describeStreams} keyed by `appHostPath`. Every stream is an equal peer for resource
     * population: it merges its resources into `appHost.resources` and, while its host remains in
     * `_appHosts`, restarts with backoff. A describe is only ever started for a host `aspire ps` has
     * confirmed running, so there is no proactive/eager start.
     *
    * Describe errors belong to the selected workspace AppHost (`_workspaceAppHostPath`): only that
    * host sets the shared banner, and only its own working describe clears it. Each workspace folder
    * can resolve a different CLI, so a non-selected peer's failure or recovery says nothing about the
    * selected host's compatibility.
     */
    private _startDescribe(appHostPath: string, forceIncludeDisabledCommands?: boolean, initialRestartDelay?: number): void {
        if (this._disposed) {
            return;
        }

        const stream: DescribeStream = {
            appHostPath,
            process: undefined,
            resources: new Map(),
            receivedData: false,
            nonJsonLines: [],
            stderr: '',
            restartTimer: undefined,
            // A fresh stream restarts after 5s; a restart carries the backed-off delay forward via
            // `initialRestartDelay` so repeated no-data exits grow the interval (5s -> 10s -> 20s ...)
            // instead of hammering the CLI every 5s. Each stream is single-use, so the backoff has to
            // be threaded into the next stream rather than mutated on this one.
            restartDelay: initialRestartDelay ?? 5000,
            version: 0,
        };
        this._describeStreams.set(appHostPath, stream);
        const startVersion = ++stream.version;
        const target = getCliPathTargetForUri(vscode.Uri.file(appHostPath));

        this._terminalProvider.getAspireCliExecutablePath(target).then(async cliPath => {
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream || startVersion !== stream.version) {
                return;
            }

            // The capability is a property of the CLI this AppHost resolves to, not of the window: a
            // multi-root workspace can point each folder at a different aspire.cliPath, so a single
            // eagerly-probed flag would describe the wrong CLI. Probing per stream is safe to await
            // because the resolved value now travels with the stream — the retry paths below pass the
            // decision they already made rather than re-reading shared state that another stream
            // could have changed underneath them.
            const configInfo = await this._configInfoProvider.getConfigInfo({
                suppressErrors: true,
                cliPath,
                target,
            });
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream || startVersion !== stream.version) {
                return;
            }

            const includeDisabledCommands = forceIncludeDisabledCommands
                ?? configInfo?.capabilities?.includes(describeIncludeDisabledCommandsCapability)
                ?? false;
            const args = this._cliRunner.withNoLogo(['describe', '--follow', '--format', 'json'], cliPath);
            if (includeDisabledCommands) {
                args.push('--include-disabled-commands');
            }
            args.push('--apphost', appHostPath);

            this._startResolvedDescribeProcess(stream, startVersion, cliPath, args);
        }).catch(error => {
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream || startVersion !== stream.version) {
                return;
            }
            // Resolving the CLI path failed, so no describe stream can run. Only the selected workspace
            // AppHost surfaces this through the shared describe banner (workspace mode shows it; other modes
            // swallow it, matching prior behavior); a non-selected peer is logged only.
            extensionLogOutputChannel.warn(`Failed to start describe watch (--apphost ${appHostPath}): ${error}`);
            this._describeStreams.delete(appHostPath);
            if (isMatchingAppHostPath(appHostPath, this._workspaceAppHostPath)) {
                this._setDescribeError(errorFetchingAppHosts(String(error)), { appHostPath });
            }
        });
    }

    private _startResolvedDescribeProcess(stream: DescribeStream, startVersion: number, cliPath: string, args: string[]): void {
        const appHostPath = stream.appHostPath;
        if (this._disposed || this._describeStreams.get(appHostPath) !== stream || startVersion !== stream.version) {
            return;
        }

        const includeDisabledCommands = args.includes('--include-disabled-commands');

        extensionLogOutputChannel.info(`Starting aspire describe --follow (--apphost ${appHostPath})`);

        stream.receivedData = false;
        const describeProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
            createProcessGroup: true,
            noExtensionVariables: true,
            lineCallback: (line) => {
                if (this._describeStreams.get(appHostPath) !== stream || stream.process !== describeProcess) {
                    return;
                }
                const handled = this._handleDescribeLine(stream, line);
                if (!handled && stream.nonJsonLines.length < 20) {
                    stream.nonJsonLines.push(line);
                }
            },
            stderrCallback: (data) => {
                if (this._describeStreams.get(appHostPath) !== stream || stream.process !== describeProcess) {
                    return;
                }
                extensionLogOutputChannel.warn(`aspire describe --follow (--apphost ${appHostPath}) stderr: ${data}`);
                if (stream.stderr.length < 4000) {
                    stream.stderr += data;
                }
            },
            exitCallback: (code) => {
                if (this._describeStreams.get(appHostPath) !== stream || stream.process !== describeProcess) {
                    return;
                }

                extensionLogOutputChannel.info(`aspire describe --follow (--apphost ${appHostPath}) exited with code ${code}`);
                stream.process = undefined;

                if (this._disposed) {
                    return;
                }

                if (code !== 0 && this._cliRunner.disableNoLogoForRetry(cliPath, args, stream.nonJsonLines.join('\n'), stream.stderr, `aspire describe --follow --apphost ${appHostPath}`)) {
                    this._describeStreams.delete(appHostPath);
                    this._startDescribe(appHostPath, includeDisabledCommands);
                    return;
                }

                // Capability fallback: a CLI too old to accept `--include-disabled-commands` exits
                // without data. Retry once without the flag.
                if (includeDisabledCommands && !stream.receivedData && isIncludeDisabledCommandsUnsupportedOutput(stream.nonJsonLines, stream.stderr)) {
                    this._describeStreams.delete(appHostPath);
                    this._startDescribe(appHostPath, false);
                    return;
                }

                // Host no longer running: drop the stream silently (the app stopped — not an error).
                if (!this._appHosts.some(appHost => isMatchingAppHostPath(appHost.appHostPath, appHostPath))) {
                    stream.resources.clear();
                    this._describeStreams.delete(appHostPath);
                    this._attachResourcesToAppHosts();
                    this._onDidChangeData.fire();
                    return;
                }

                // ps is the authority on whether the host stopped, so a describe stream exiting
                // does not necessarily indicate the host has stopped.
                if (stream.receivedData) {
                    extensionLogOutputChannel.info(`aspire describe --follow (--apphost ${appHostPath}) exited (code ${code}) after producing data; restarting.`);
                } else {
                    // A stream that never produced resources and exits (cleanly or with an error) means
                    // the CLI cannot describe that host. Only the selected workspace AppHost surfaces
                    // this through the shared banner; another folder can resolve a different CLI.
                    extensionLogOutputChannel.warn(`aspire describe --follow (--apphost ${appHostPath}) exited (code ${code}) without producing data.`);
                    if (isMatchingAppHostPath(appHostPath, this._workspaceAppHostPath)) {
                        const noDataError = this._getDescribeNoDataError(code, stream.nonJsonLines, stream.stderr);
                        if (noDataError.message) {
                            this._setDescribeError(noDataError.message, {
                                compatibility: noDataError.isCompatibilityError,
                                appHostPath,
                            });
                        }
                    }
                }

                stream.resources.clear();
                this._attachResourcesToAppHosts();
                this._onDidChangeData.fire();
                this._scheduleDescribeRestart(appHostPath, stream);
                this._psPoller.refreshAppHostsFromAuthoritativeSnapshot();
            },
            errorCallback: (error) => {
                if (this._describeStreams.get(appHostPath) !== stream || stream.process !== describeProcess) {
                    return;
                }

                if (this._disposed) {
                    return;
                }

                // Spawn/stream error (as opposed to a clean exit). Only the selected workspace AppHost
                // surfaces it through the shared describe banner; a non-selected peer is logged only so it
                // can't masquerade as the selected host's error.
                extensionLogOutputChannel.warn(`aspire describe --follow --apphost ${appHostPath} error: ${error.message}`);
                stream.process = undefined;
                stream.resources.clear();
                if (isMatchingAppHostPath(appHostPath, this._workspaceAppHostPath)) {
                    this._setDescribeError(errorFetchingAppHosts(error.message), { appHostPath });
                }

                // Host no longer running: drop the stream silently
                if (!this._appHosts.some(appHost => isMatchingAppHostPath(appHost.appHostPath, appHostPath))) {
                    this._describeStreams.delete(appHostPath);
                    this._attachResourcesToAppHosts();
                    this._onDidChangeData.fire();
                    return;
                }

                this._attachResourcesToAppHosts();
                this._onDidChangeData.fire();
                this._scheduleDescribeRestart(appHostPath, stream);
                this._psPoller.refreshAppHostsFromAuthoritativeSnapshot();
            }
        });
        stream.process = describeProcess;
    }

    private _scheduleDescribeRestart(appHostPath: string, stream: DescribeStream): void {
        const delay = stream.restartDelay;
        const nextDelay = Math.max(delay, Math.min(delay * 2, this._psPoller.getPollingIntervalMs()));
        extensionLogOutputChannel.info(`Restarting describe --follow --apphost ${appHostPath} in ${delay}ms`);
        stream.restartTimer = setTimeout(() => {
            stream.restartTimer = undefined;
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream) {
                return;
            }
            this._describeStreams.delete(appHostPath);
            if (!this._appHosts.some(appHost => isMatchingAppHostPath(appHost.appHostPath, appHostPath))) {
                this._attachResourcesToAppHosts();
                this._onDidChangeData.fire();
                return;
            }
            this._startDescribe(appHostPath, undefined, nextDelay);
        }, delay);
    }

    private _handleDescribeLine(stream: DescribeStream, line: string): boolean {
        const trimmed = line.trim();
        if (!trimmed) {
            return true;
        }

        try {
            const resource: ResourceJson = JSON.parse(trimmed);
            if (resource.name) {
                stream.resources.set(resource.name, resource);
                stream.receivedData = true;
                stream.restartDelay = 5000;
                // Once a host raises an error, only that host's recovery clears it, even if workspace
                // selection changes while the stream is restarting. Ownerless errors retain the selected
                // workspace-host behavior used for discovery failures.
                const recoveredOwnedError = this._describeErrorAppHostPath !== undefined
                    && isMatchingAppHostPath(stream.appHostPath, this._describeErrorAppHostPath);
                const recoveredOwnerlessError = this._describeErrorAppHostPath === undefined
                    && isMatchingAppHostPath(stream.appHostPath, this._workspaceAppHostPath);
                if (recoveredOwnedError || recoveredOwnerlessError) {
                    this._setDescribeError(undefined);
                }
                this._attachResourcesToAppHosts();
                if (this._viewMode === 'workspace') {
                    this._updateWorkspaceContext();
                } else {
                    this._onDidChangeData.fire();
                }
                return true;
            }
        } catch (e) {
            extensionLogOutputChannel.warn(`Failed to parse describe NDJSON line for ${stream.appHostPath}: ${e}`);
        }

        return false;
    }

    private _getDescribeNoDataError(exitCode: number | null, nonJsonLines: readonly string[], stderr: string): DescribeNoDataError {
        if (isDescribeUnsupportedOutput(nonJsonLines, stderr)) {
            return {
                message: aspireCliDescribeNotSupported(aspireDescribeMinimumVersion),
                isCompatibilityError: true,
            };
        }

        if (this._workspaceAppHostPath && exitCode !== 0) {
            return {
                message: errorFetchingAppHosts(stderr || `exit code ${exitCode ?? 1}`),
                isCompatibilityError: false,
            };
        }

        // A clean exit before `ps` observes the AppHost can happen while the app is still starting.
        // Once `ps` reports the workspace AppHost as running, an empty successful describe stream means
        // the AppHost cannot serve workspace resources even though the CLI command itself was accepted.
        if (this._workspaceAppHostPath && this._getWorkspaceAppHost() !== undefined) {
            return {
                message: appHostDescribeMayNotBeSupported(aspireDescribeMinimumVersion),
                isCompatibilityError: true,
            };
        }

        return {
            message: undefined,
            isCompatibilityError: false,
        };
    }

    // ── Describe reconcile ──
    // `_describeStreams` holds one `aspire describe --follow --apphost <path>` stream per rendered
    // running AppHost. `ps` is AppHost-level only, so to keep the tree populated with resources we run
    // one stream per host. All streams are equal peers merged into `appHost.resources`; a host is
    // described only once `ps` confirms it running.

    private _reconcileDescribes(): void {
        if (this._disposed || !this._dataActive) {
            this._stopAllDescribes();
            return;
        }

        const desired = new Set<string>(this.appHosts.map(appHost => appHost.appHostPath));

        // Stop streams whose host is no longer running or wanted.
        for (const path of Array.from(this._describeStreams.keys())) {
            if (!desired.has(path)) {
                this._stopDescribe(path);
            }
        }

        // Start a stream for any desired host that does not already have one.
        for (const path of desired) {
            if (!this._describeStreams.has(path)) {
                this._startDescribe(path);
            }
        }

        const errorOwnerIsDesired = this._describeErrorAppHostPath !== undefined
            && Array.from(desired).some(path => isMatchingAppHostPath(path, this._describeErrorAppHostPath));
        if ((this._describeErrorAppHostPath !== undefined && !errorOwnerIsDesired)
            || (desired.size === 0 && this._describeErrorIsCompatibility)) {
            this._setDescribeError(undefined);
        }

        this._attachResourcesToAppHosts();
    }

    private _attachResourcesToAppHosts(): void {
        for (const appHost of this._appHosts) {
            const stream = this._describeStreams.get(appHost.appHostPath);
            appHost.resources = stream ? Array.from(stream.resources.values()) : null;
        }
    }

    private async _runCliJson<T>(command: string, args: string[], options: RunCliCommandOptions = {}): Promise<T> {
        const { stdout } = await this._cliRunner.runCliCommand(command, args, options);

        try {
            return parseCliJsonOutput<T>(stdout);
        } catch (error) {
            throw new AspireCliParseError(command, stdout, error);
        }
    }

    private async _fetchAppHostResourcesOnce(appHostPath: string): Promise<ResourceJson[]> {
        const snapshot = await this._runCliJson<DescribeSnapshotJson>(
            'aspire describe',
            this._cliRunner.withNoLogo(['describe', '--format', 'json', '--apphost', appHostPath]),
            { target: getCliPathTargetForUri(vscode.Uri.file(appHostPath)) });
        return snapshot.resources ?? [];
    }

    private _stopDescribe(appHostPath: string): void {
        const stream = this._describeStreams.get(appHostPath);
        if (!stream) {
            return;
        }
        this._describeStreams.delete(appHostPath);
        stream.version++;
        if (stream.restartTimer) {
            clearTimeout(stream.restartTimer);
            stream.restartTimer = undefined;
        }
        if (stream.process) {
            const childProcess = stream.process;
            stream.process = undefined;
            terminateCliProcess(childProcess, `aspire describe --follow (${appHostPath})`, { suppressTimeoutWarning: true });
        }
    }

    private _stopAllDescribes(): void {
        for (const path of Array.from(this._describeStreams.keys())) {
            this._stopDescribe(path);
        }
        this._attachResourcesToAppHosts();
    }

    private _updateWorkspaceContext(options?: { clearLoading?: boolean }): void {
        const workspaceAppHost = this._getWorkspaceAppHost();
        const hasWorkspaceAppHost = workspaceAppHost !== undefined;
        const selectedResources = this.workspaceResources;
        const hasResources = selectedResources.length > 0;
        const workspaceAppHosts = this._appHosts.filter(appHost => this._isWorkspaceAppHost(appHost));
        const hasRunningAppHosts = workspaceAppHosts.length > 0;
        const hasDashboardUrl = Boolean(workspaceAppHost?.dashboardUrl)
            || selectedResources.some(resource => Boolean(resource.dashboardUrl))
            || workspaceAppHosts.some(appHost => Boolean(appHost.dashboardUrl));
        const hasWorkspaceCandidates = this._workspaceAppHostCandidatePaths.length > 0;
        const clearLoading = options?.clearLoading ?? (hasResources || hasWorkspaceAppHost || hasRunningAppHosts || hasWorkspaceCandidates);

        if (this._viewMode !== 'workspace') {
            if (clearLoading) {
                this._loadingWorkspace = false;
            }
            return;
        }

        vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', !hasWorkspaceAppHost && !hasResources && !hasRunningAppHosts && !hasWorkspaceCandidates);
        // Keep this distinct from `noAppHosts`, which also considers discovered idle
        // candidates that have no live dashboard URL.
        vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasDashboardUrl);
        if (this._loadingWorkspace && clearLoading) {
            this._loadingWorkspace = false;
            this._updateLoadingContext();
        }
        this._onDidChangeData.fire();
    }

    // ── ps polling ──

    private _handlePsFollowStarted(): void {
        this._setGlobalLoading(false);
        if (this._viewMode === 'global') {
            const hasDashboardUrl = this._appHosts.some(appHost => Boolean(appHost.dashboardUrl));
            vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', this._appHosts.length === 0);
            vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasDashboardUrl);
        }
    }

    private _updateLoadingContext(): void {
        const isLoading = this._viewMode === 'workspace' ? this._loadingWorkspace : this._loadingGlobal;
        vscode.commands.executeCommand('setContext', 'aspire.loading', isLoading);
    }

    private _setGlobalLoading(isLoading: boolean): void {
        const loadingChanged = this._loadingGlobal !== isLoading;
        this._loadingGlobal = isLoading;
        if (this._viewMode !== 'global') {
            return;
        }

        if (loadingChanged) {
            this._onDidChangeData.fire();
        }
        this._updateLoadingContext();
    }

    private _clearLoading(): void {
        const loadingChanged = this._viewMode === 'workspace'
            ? this._loadingWorkspace
            : this._loadingGlobal;
        this._loadingWorkspace = false;
        this._loadingGlobal = false;
        if (loadingChanged) {
            this._onDidChangeData.fire();
        }
        this._updateLoadingContext();
    }

    private _clearErrors(): void {
        this._describeErrorMessage = undefined;
        this._describeErrorIsCompatibility = false;
        this._describeErrorAppHostPath = undefined;
        this._psErrorMessage = undefined;
        this._updateErrorMessage();
    }

    private _setDescribeError(message: string | undefined, options?: { compatibility?: boolean; appHostPath?: string }): void {
        const compatibility = message !== undefined && (options?.compatibility ?? false);
        const appHostPath = message !== undefined ? options?.appHostPath : undefined;
        if (this._describeErrorMessage !== message
            || this._describeErrorIsCompatibility !== compatibility
            || !isMatchingAppHostPath(this._describeErrorAppHostPath, appHostPath)) {
            this._describeErrorMessage = message;
            this._describeErrorIsCompatibility = compatibility;
            this._describeErrorAppHostPath = appHostPath;
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
        const workspaceMode = this._viewMode === 'workspace';
        const message = workspaceMode
            ? this._describeErrorMessage ?? this._psErrorMessage
            : this._psErrorMessage;
        const isCompatibilityError = workspaceMode
            ? (this._describeErrorMessage !== undefined
                ? this._describeErrorIsCompatibility
                : false)
            : false;
        const hasError = message !== undefined;
        if (this._errorMessage !== message || this._errorIsCompatibility !== isCompatibilityError) {
            this._errorMessage = message;
            this._errorIsCompatibility = isCompatibilityError;
            if (message) {
                extensionLogOutputChannel.warn(message);
            }
            vscode.commands.executeCommand('setContext', 'aspire.fetchAppHostsError', hasError);
            vscode.commands.executeCommand('setContext', 'aspire.fetchAppHostsCompatibilityError', hasError && isCompatibilityError);
            this._onDidChangeData.fire();
        }
    }

    private _handlePsOutput(stdout: string, canCompleteGlobalLoading: boolean): void {
        try {
            const parsed: AppHostDisplayInfo[] | AppHostDisplayInfo = JSON.parse(stdout);
            const appHosts = Array.isArray(parsed)
                ? parsed
                : this._applyPsDelta(parsed);

            const completesGlobalLoading = canCompleteGlobalLoading && this._loadingGlobal;
            // A fresh ps result wins the workspace loading race when it finds a workspace host,
            // or when discovery has finished and an empty result is therefore authoritative.
            const completesWorkspaceLoading = this._loadingWorkspace
                && (this._workspaceAppHostDiscoveryComplete || appHosts.some(appHost => this._isWorkspaceAppHost(appHost)));
            if (completesWorkspaceLoading) {
                this._loadingWorkspace = false;
            }
            this._handlePsSnapshot(appHosts, { force: completesWorkspaceLoading && this._viewMode === 'workspace' });
            if (completesWorkspaceLoading && this._viewMode === 'workspace') {
                this._updateLoadingContext();
            }
            if (completesGlobalLoading) {
                // Clear the loading context only after the tree has been invalidated with the fresh
                // snapshot, otherwise VS Code can briefly render a blank global view.
                this._setGlobalLoading(false);
            }
        } catch (e) {
            extensionLogOutputChannel.warn(`Failed to parse aspire ps output: ${e}`);
            if (canCompleteGlobalLoading) {
                this._clearLoading();
                this._setPsError(errorFetchingAppHosts(String(e)));
            }
        }
    }

    private _applyPsDelta(appHost: AppHostDisplayInfo): AppHostDisplayInfo[] {
        if (appHost.status?.toLowerCase() === 'stopped') {
            return this._appHosts.filter(current => !isMatchingAppHostInstance(current, appHost));
        }

        return [
            ...this._appHosts.filter(current => !isMatchingAppHostInstance(current, appHost)),
            appHost,
        ];
    }

    private _handlePsSnapshot(appHosts: AppHostDisplayInfo[], options?: { force?: boolean }): void {
        const force = options?.force ?? false;
        const previousSelectedWorkspaceAppHost = this._getWorkspaceAppHost();

        // Resolve the selected workspace AppHost and auto-retarget its describe stream when a single
        // workspace AppHost is running but the configured path points elsewhere (e.g. `aspire ps`
        // reports the running source file while `aspire ls` resolved a `.csproj`).
        const workspaceAppHosts = appHosts.filter(appHost => this._isWorkspaceAppHost(appHost));
        const selectedWorkspaceAppHostPath = this._workspaceAppHostPath;
        let selectedWorkspaceAppHost = selectedWorkspaceAppHostPath
            ? workspaceAppHosts.find(appHost => isMatchingAppHostPath(appHost.appHostPath, selectedWorkspaceAppHostPath))
            : undefined;
        if (!selectedWorkspaceAppHost && workspaceAppHosts.length === 1) {
            const candidate = workspaceAppHosts[0];
            // Only adopt/retarget the selected workspace AppHost for a host that is genuinely part of
            // the workspace — a configured/discovered candidate or a host running inside the workspace
            // folder. A host that is a "workspace" host ONLY because its editor tab is open must NOT be
            // adopted as `_workspaceAppHostPath`: doing so would make the no-data
            // describe path treat a clean, empty open-tab describe as a workspace compatibility error.
            const isOnlyOpenTabAppHost = !isPathInWorkspace(candidate.appHostPath)
                && !this._workspaceAppHostCandidatePaths.some(candidatePath => isMatchingAppHostPath(candidate.appHostPath, candidatePath));
            if (!isOnlyOpenTabAppHost) {
                selectedWorkspaceAppHost = candidate;
                if (!isMatchingAppHostPath(selectedWorkspaceAppHostPath, selectedWorkspaceAppHost.appHostPath)) {
                    extensionLogOutputChannel.info(`Retargeting workspace AppHost describe to running AppHost ${selectedWorkspaceAppHost.appHostPath}`);
                    this._setWorkspaceAppHostPathFromCurrentCandidates(selectedWorkspaceAppHost.appHostPath);
                    this._setDescribeError(undefined);
                }
            }
        }

        this._appHosts = appHosts;

        this._reconcileDescribes();

        // Change-detect against the previous post-reconcile rendered list rather than the raw ps
        // payload. `appHosts` lacks the `resources` field (ps no longer emits it after #17479), while
        // `_attachResourcesToAppHosts` re-attaches resources from the describe streams — comparing the
        // raw payload would always report a change once any stream produced resources. In workspace
        // mode, using only the workspace subset means global-only host churn doesn't re-render it.
        const renderedAppHosts = this._viewMode === 'workspace' ? workspaceAppHosts : this._appHosts;
        const appHostsSnapshot = JSON.stringify(renderedAppHosts);
        const appHostsChanged = appHostsSnapshot !== this._appHostsSnapshot;
        const workspaceAppHostChanged = JSON.stringify(selectedWorkspaceAppHost) !== JSON.stringify(previousSelectedWorkspaceAppHost);
        this._appHostsSnapshot = appHostsSnapshot;

        if (this._viewMode === 'workspace') {
            if (appHostsChanged || workspaceAppHostChanged || force || this._loadingWorkspace) {
                this._updateWorkspaceContext({ clearLoading: false });
            }
        } else {
            if (appHostsChanged || force) {
                const hasDashboardUrl = this._appHosts.some(appHost => Boolean(appHost.dashboardUrl));
                vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', this._appHosts.length === 0);
                vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasDashboardUrl);
                this._onDidChangeData.fire();
            }
        }
    }

    private _isWorkspaceAppHost(appHost: AppHostDisplayInfo): boolean {
        const isOpenAppHostPath = this._openAppHostPaths.some(openPath => isMatchingAppHostPath(appHost.appHostPath, openPath));
        const isSelectedWorkspaceAppHostPath = isMatchingAppHostPath(appHost.appHostPath, this._workspaceAppHostPath);
        const isWorkspaceCandidatePath = this._workspaceAppHostCandidatePaths.some(candidatePath => isMatchingAppHostPath(appHost.appHostPath, candidatePath));
        return isOpenAppHostPath || isSelectedWorkspaceAppHostPath || isWorkspaceCandidatePath || isPathInWorkspace(appHost.appHostPath);
    }

}

function formatWorkspaceFolderDiscoveryError(error: WorkspaceFolderDiscoveryError): string {
    return `${error.workspaceFolder.uri.fsPath}: ${String(error.error)}`;
}

function combineWorkspaceAppHostCandidates(workspaceFolderCandidates: readonly WorkspaceFolderAppHostCandidates[]): CombinedWorkspaceAppHostCandidates {
    const appHostCandidates: Array<{ candidate: AppHostCandidate; descriptor: FileSystemEntryDescriptor; workspaceFolderDepth: number }> = [];
    const appHostCandidateIndex = new FileSystemEntryDescriptorIndex();
    const explicitlySelectedPaths: string[] = [];
    const explicitlySelectedPathIndex = new FileSystemEntryDescriptorIndex();
    const descriptorByResolvedPath = new Map<string, FileSystemEntryDescriptor>();
    const getDescriptor = (candidatePath: string): FileSystemEntryDescriptor => {
        const resolvedPath = path.resolve(candidatePath);
        let descriptor = descriptorByResolvedPath.get(resolvedPath);
        if (!descriptor) {
            descriptor = getFileSystemEntryDescriptor(resolvedPath);
            descriptorByResolvedPath.set(resolvedPath, descriptor);
        }

        return descriptor;
    };

    for (const folderCandidates of workspaceFolderCandidates) {
        const result = getWorkspaceAppHostProjectSearchResult(folderCandidates.workspaceFolder, folderCandidates.candidates);
        const workspaceFolderDepth = path.resolve(folderCandidates.workspaceFolder.uri.fsPath).length;
        for (const candidate of result.app_host_candidates) {
            const descriptor = getDescriptor(candidate.path);
            const existingIndex = appHostCandidateIndex.find(descriptor);
            if (existingIndex === undefined) {
                appHostCandidates.push({ candidate, descriptor, workspaceFolderDepth });
                appHostCandidateIndex.add(descriptor);
            } else if (appHostCandidates[existingIndex].workspaceFolderDepth < workspaceFolderDepth) {
                appHostCandidates[existingIndex] = { candidate, descriptor, workspaceFolderDepth };
                appHostCandidateIndex.replace(existingIndex, descriptor);
            }
        }
        for (const candidate of folderCandidates.candidates) {
            const descriptor = getDescriptor(candidate.path);
            if (candidate.selected === true
                && candidate.status === 'buildable'
                && explicitlySelectedPathIndex.find(descriptor) === undefined) {
                explicitlySelectedPaths.push(candidate.path);
                explicitlySelectedPathIndex.add(descriptor);
            }
        }
    }

    const combinedAppHostCandidates = appHostCandidates.map(({ candidate }) => candidate);
    const buildableAppHostCandidates = combinedAppHostCandidates.filter(isBuildableAppHostCandidate);
    const selectedAppHostPath = explicitlySelectedPaths.length === 1
        ? combinedAppHostCandidates[appHostCandidateIndex.find(getDescriptor(explicitlySelectedPaths[0])) ?? -1]?.path
        : explicitlySelectedPaths.length === 0 && buildableAppHostCandidates.length === 1
            ? buildableAppHostCandidates[0].path
            : null;

    return {
        appHostCandidates: combinedAppHostCandidates,
        selectedAppHostPath: selectedAppHostPath ?? null,
    };
}
