import * as vscode from 'vscode';
import * as path from 'path';
import { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostDescribeMayNotBeSupported, appHostPathMustBeNonEmptyAbsolute, aspireCliCommandFailed, aspireCliCommandTimedOut, aspireCliDescribeNotSupported, aspireCliOutputParseFailed, aspireDescribeMinimumVersion, errorFetchingAppHosts, workspaceViewSelectedMultipleAppHosts, workspaceViewSelectedSingleAppHost } from '../loc/strings';
import { AppHostCandidate, AppHostDiscoveryService, formatAppHostLanguage, getWorkspaceAppHostProjectSearchResult, isBuildableAppHostCandidate } from '../utils/appHostDiscovery';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { describeIncludeDisabledCommandsCapability } from '../types/configInfo';

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
    state?: string | null;
    sortOrder?: number | null;
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

export interface ResourceCommandArgumentDynamicLoadingJson {
    alwaysLoadOnStart?: boolean;
    dependsOnInputs?: string[] | null;
}

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
    dynamicLoading?: ResourceCommandArgumentDynamicLoadingJson | null;
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
    status?: string;
    cliPid: number | null;
    dashboardUrl: string | null;
    logFilePath?: string | null;
    resources: ResourceJson[] | null | undefined;
}

interface DescribeSnapshotJson {
    resources?: ResourceJson[];
}

export class AspireCliNotInstalledError extends Error {
    constructor(message: string) {
        super(message);
        this.name = 'AspireCliNotInstalledError';
    }
}

export class AspireCliFailedError extends Error {
    constructor(
        public readonly command: string,
        public readonly exitCode: number | null,
        public readonly stdout: string,
        public readonly stderr: string) {
        super(aspireCliCommandFailed(command, String(exitCode), getCommandOutputSuffix(stdout, stderr)));
        this.name = 'AspireCliFailedError';
    }
}

export class AspireCliParseError extends Error {
    constructor(
        public readonly command: string,
        public readonly output: string,
        innerError: unknown) {
        super(aspireCliOutputParseFailed(command, String(innerError)));
        this.name = 'AspireCliParseError';
    }
}

export type ViewMode = 'workspace' | 'global';

// Describe-stream key for single-file mode: an AppHost file is open but no workspace folder anchors
// discovery, so the stream runs path-less (no `--apphost`). Normalizes to dirname '.', so
// `isMatchingAppHostPath` only matches it against itself — never against a real `.csproj`/`.cs` key.
const SINGLE_FILE_DESCRIBE_KEY = '<single-file>';

type DescribeParkedState = 'not-parked' | 'parked-idle' | 'parked-active';

interface DescribeStream {
    appHostPath: string;
    process: ChildProcessWithoutNullStreams | undefined;
    resources: Map<string, ResourceJson>;
    nonJsonLines: string[];
    stderr: string;
    restartTimer: ReturnType<typeof setTimeout> | undefined;
    restartDelay: number;
    version: number;
    receivedData: boolean;
    parkedState: DescribeParkedState;
}

type ErrorSource = 'describe' | 'ps';

interface ErrorState {
    message: string | undefined;
    isCompatibility: boolean;
}

interface PostStopRefreshTimer {
    timer: ReturnType<typeof setTimeout>;
}

/**
 * Central data repository for app host and resource information.
 *
 * Owns two data sources unified behind one describe-stream map (`_describeStreams`):
 *  - `aspire describe --follow --apphost <path>` — one stream per AppHost in the desired set,
 *    keyed by AppHost path. `_reconcileDescribeStreams` is the single authority
 *    that starts/stops these streams; they run only while the tree-view panel is visible.
 *  - `aspire ps` polling — periodically fetches running app hosts. In global mode this backs the
 *    full tree; in workspace mode it confirms whether the selected workspace AppHost is running
 *    when the resource stream is empty.
 */
const oneShotOutputBufferLimit = 4000;

export class AppHostDataRepository {
    private static readonly _processShutdownGracePeriodMs = 5000;
    private static readonly _appHostStopRefreshDelayMs = 400;
    private static readonly _appHostStopRefreshMaxAttempts = 75;
    private static readonly _oneShotCommandTimeoutMs = 30000;
    private static readonly _oneShotOutputBufferLimit = oneShotOutputBufferLimit;

    private readonly _onDidChangeData = new vscode.EventEmitter<void>();
    readonly onDidChangeData = this._onDidChangeData.event;

    // ── Mode / panel state ──
    private _viewMode: ViewMode = 'workspace';
    private _panelVisible = false;
    private _appHostFileOpen = false;
    private _hasEverBeenDataActive = false;

    // ── Describe state ──
    // Whether `aspire describe` accepts the hidden `--include-disabled-commands` flag. Resolved lazily
    // from CLI capabilities; starts optimistic so that if resolution fails we still attempt the flag
    // and rely on the locale-independent no-data fallback. Older CLIs reject it and emit no data.
    private _includeDisabledCommandsSupported = true;
    private readonly _configInfoProvider: ConfigInfoProvider;

    // ── Running AppHost state (ps polling) ──
    private _appHosts: AppHostDisplayInfo[] = [];
    // Cached JSON serialization of `_appHosts` after the most recent reconcile so
    // _handlePsOutput can detect real changes. We can't compare raw `ps` output to
    // `_appHosts` directly because the in-memory state has merged resources, while
    // `ps` no longer emits them (#17479) — see _handlePsOutput for the rationale.
    private _appHostsSnapshot = '[]';
    private _workspaceAppHost: AppHostDisplayInfo | undefined;
    private _pollingInterval: ReturnType<typeof setInterval> | undefined;
    private _psProcesses = new Set<ChildProcessWithoutNullStreams>();
    private _psPollingGeneration = 0;
    private _oneShotProcesses = new Set<ChildProcessWithoutNullStreams>();
    private _psFetchVersion = 0;
    private _supportsPsFollow = true;
    private _fetchInProgress = false;
    private _postStopRefreshTimers = new Map<string, PostStopRefreshTimer>();
    private _authoritativeSnapshotInProgress = false;
    private _authoritativeSnapshotPending = false;
    private _authoritativeSnapshotRequestId = 0;
    private _activeAuthoritativeSnapshotRequestId: number | undefined;

    // ── Describe streams (shared workspace + global) ──
    // One per-AppHost `aspire describe --follow --apphost <path>` stream set drives BOTH modes. Global:
    // every running AppHost gets a stream, resources attached onto its `_appHosts` entry. Workspace:
    // the SELECTED host's stream is the projection behind `workspaceResources` and the loading/error
    // UX. Keyed by appHostPath.
    private _describeStreams = new Map<string, DescribeStream>();

    // ── Workspace app host (from aspire ls) ──
    // The singular fields track a selected/default workspace AppHost. The candidate
    // paths track every buildable AppHost found by `aspire ls`, so workspace-mode
    // `aspire ps` polling can filter and render multiple running workspace AppHosts.
    private _workspaceAppHostName: string | undefined;
    private _workspaceAppHostPath: string | undefined;
    private _workspaceAppHostCandidatePaths: string[] = [];
    private _workspaceAppHostDescription: string | undefined;
    private _workspaceAppHostDiscoveryComplete = false;
    // False until discovery runs against a workspace root folder. Stays false in single-file mode (no
    // root to anchor `aspire ls`), which uses the path-less describe watch instead of a resolved path.
    private _workspaceUsesWorkspaceRoot = false;

    // ── Describe-target coordinator input state ──
    // `ps` (running) and `ls` (idle/configured) feed these; `_updateWorkspaceSelection` turns them
    // into the selected describe target. `_reconcileDescribeStreams` is the sole start/stop authority.
    private _runningWorkspaceAppHosts: readonly AppHostDisplayInfo[] = [];
    private _configuredWorkspaceAppHostPath: string | undefined;
    private _workspaceAppHostDiscoveryVersion = 0;
    private _workspaceAppHostDiscoveryInProgress = false;
    private _workspaceAppHostDiscoveryRefreshQueued = false;
    private _workspaceAppHostDiscoveryCancellationSource: vscode.CancellationTokenSource | undefined;
    private readonly _appHostDiscoveryChangeDisposable: vscode.Disposable;
    private readonly _workspaceFoldersChangeDisposable: vscode.Disposable;
    private readonly _appHostDiscoveryService: AppHostDiscoveryService;
    private readonly _ownsAppHostDiscoveryService: boolean;

    // ── Error state ──
    // Per-source error inputs. `describe` only surfaces in workspace mode; `ps` surfaces in both.
    private readonly _errors: Record<ErrorSource, ErrorState> = {
        describe: { message: undefined, isCompatibility: false },
        ps: { message: undefined, isCompatibility: false },
    };
    // Effective error rendered by the tree, derived from `_errors` + view mode.
    private _effectiveError: ErrorState = { message: undefined, isCompatibility: false };

    // ── Loading state ──
    private _loadingWorkspace = true;
    private _loadingGlobal = true;

    private readonly _configChangeDisposable: vscode.Disposable;
    private _disposed = false;

    constructor(private readonly _terminalProvider: AspireTerminalProvider, appHostDiscoveryService?: AppHostDiscoveryService) {
        this._appHostDiscoveryService = appHostDiscoveryService ?? new AppHostDiscoveryService(_terminalProvider);
        this._ownsAppHostDiscoveryService = appHostDiscoveryService === undefined;
        this._configInfoProvider = new ConfigInfoProvider(_terminalProvider);
        this._appHostDiscoveryChangeDisposable = this._appHostDiscoveryService.onDidChangeCandidates(workspaceFolder => {
            const rootFolder = vscode.workspace.workspaceFolders?.[0];
            if (rootFolder?.uri.toString() === workspaceFolder.uri.toString()) {
                this._fetchWorkspaceAppHost();
            }
        });
        this._workspaceFoldersChangeDisposable = vscode.workspace.onDidChangeWorkspaceFolders(() => {
            this._stopAllDescribes();
            this._stopPolling();
            this._workspaceAppHostDiscoveryComplete = false;
            this._clearWorkspaceAppHostDiscovery();
            this._clearWorkspaceAppHostData();
            this._clearErrors();
            this._updateWorkspaceContext();
            this._fetchWorkspaceAppHost({ forceRefresh: true });
        });
        this._fetchWorkspaceAppHost();
        this._configChangeDisposable = vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration('aspire.globalAppHostsPollingInterval') && this._shouldPoll) {
                this._startPsPolling();
            }
        });
        // Kick off the CLI capability probe eagerly (fire-and-forget) so the cached describe gate is
        // ready by the time a describe stream starts. We must NOT await capabilities on the describe
        // start path: an await there would reorder the describe spawn after other streams (e.g. ps)
        // and change observable process ordering. Until the probe resolves we use the optimistic
        // default and the per-stream no-data fallback corrects a stale CLI.
        void this._resolveDescribeCapability();
    }

    // ── Public accessors ──

    get viewMode(): ViewMode {
        return this._viewMode;
    }

    get workspaceResources(): readonly ResourceJson[] {
        return this._workspaceResourceList();
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
        return this._effectiveError.message;
    }

    get hasError(): boolean {
        return this._effectiveError.message !== undefined;
    }

    // ── Mode / panel control ──

    setViewMode(mode: ViewMode): void {
        if (this._viewMode === mode) {
            return;
        }
        this._viewMode = mode;
        vscode.commands.executeCommand('setContext', 'aspire.viewMode', mode);
        this._clearErrors();
        if (mode === 'workspace') {
            // Reinterpret the current `aspire ps` snapshot through the workspace filters when
            // leaving global view. Otherwise an empty window can keep rendering global AppHosts
            // until the next workspace-mode poll clears them.
            this._handleWorkspacePsOutput(this._appHosts);
        }
        this._updateLoadingContext();
        this._syncPolling();
        this._onDidChangeData.fire();
    }

    setPanelVisible(visible: boolean): void {
        if (this._panelVisible === visible) {
            return;
        }
        const wasDataActive = this._dataActive;
        this._panelVisible = visible;
        const becameDataActive = !wasDataActive && this._dataActive;
        const resumedFromInactive = becameDataActive && this._hasEverBeenDataActive;
        if (this._dataActive) {
            this._hasEverBeenDataActive = true;
        }
        this._syncPolling(resumedFromInactive);
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
        const wasDataActive = this._dataActive;
        this._appHostFileOpen = open;
        const becameDataActive = !wasDataActive && this._dataActive;
        const resumedFromInactive = becameDataActive && this._hasEverBeenDataActive;
        if (this._dataActive) {
            this._hasEverBeenDataActive = true;
        }
        this._syncPolling(resumedFromInactive);
    }

    refresh(): void {
        this._stopAllDescribes();
        this._clearErrors();
        // A user-triggered refresh should observe AppHost/config files written by tools
        // even when the file watcher has not delivered an invalidation event yet.
        this._workspaceAppHostDiscoveryComplete = false;
        this._clearWorkspaceAppHostDiscovery();
        this._updateWorkspaceContext();
        // Re-discovery resolves through `_fetchWorkspaceAppHost` → `_syncPolling` →
        // `_reconcileDescribeStreams`, which is the only thing that (re)starts describe.
        this._fetchWorkspaceAppHost({ forceRefresh: true });
        if (this._shouldPoll) {
            this._refreshAppHostsFromAuthoritativeSnapshot();
        }
    }

    requestAppHostStopRefresh(appHostPath: string): void {
        if (this._disposed || !this._shouldPoll || !appHostPath) {
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
            if (this._disposed || !this._shouldPoll) {
                return;
            }

            if (remainingAttempts < AppHostDataRepository._appHostStopRefreshMaxAttempts && !this._hasAppHost(appHostPath)) {
                return;
            }

            this._refreshAppHostsFromAuthoritativeSnapshot();
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
            this._workspaceAppHost?.appHostPath,
        ]) {
            if (appHostPath && !paths.some(existingPath => isSameAppHostPath(existingPath, appHostPath))) {
                paths.push(appHostPath);
            }
        }

        return paths;
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
    }

    async fetchAppHostsOnce(): Promise<AppHostDisplayInfo[]> {
        const appHosts = await this._runCliJson<AppHostDisplayInfo[] | AppHostDisplayInfo>('aspire ps', ['ps', '--format', 'json']);
        const appHostList = Array.isArray(appHosts) ? appHosts : [appHosts];
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

    async runResourceCommand(resourceName: string, appHostPath: string, commandName: 'start' | 'stop'): Promise<void> {
        const trimmedAppHostPath = appHostPath.trim();
        if (!trimmedAppHostPath || !path.isAbsolute(trimmedAppHostPath)) {
            throw new Error(appHostPathMustBeNonEmptyAbsolute);
        }

        await this._runCliCommand(`aspire resource ${commandName}`, ['resource', resourceName, commandName, '--apphost', trimmedAppHostPath]);
    }

    dispose(): void {
        this._disposed = true;
        this._clearPostStopRefreshTimers();
        this._authoritativeSnapshotPending = false;
        this._stopPolling();
        this._stopAllDescribes();
        this._stopOneShotProcesses();
        this._cancelWorkspaceAppHostDiscovery();
        this._configChangeDisposable.dispose();
        this._appHostDiscoveryChangeDisposable.dispose();
        this._workspaceFoldersChangeDisposable.dispose();
        this._onDidChangeData.dispose();
        if (this._ownsAppHostDiscoveryService) {
            this._appHostDiscoveryService.dispose();
        }
    }

    // ── PS polling lifecycle ──

    /** Either source is active when the panel is visible **or** an AppHost file is open in the editor. */
    private get _dataActive(): boolean {
        return this._panelVisible || this._appHostFileOpen;
    }

    private get _shouldPoll(): boolean {
        // Workspace discovery can take longer than `aspire ps`. Poll immediately so
        // already-running AppHosts appear in the pane while idle candidates stream in.
        return this._dataActive
            && (this._viewMode === 'global'
                || !this._workspaceAppHostDiscoveryComplete
                || this._workspaceAppHostCandidatePaths.length > 0);
    }

    private _syncPolling(refreshBeforeFollowOnResume = false): void {
        if (this._disposed) {
            return;
        }

        this._updateWorkspaceSelection();

        if (this._viewMode !== 'workspace' || !this._dataActive) {
            this._clearWorkspaceAppHost();
        }

        if (this._shouldPoll) {
            // `aspire ps --follow` is a global stream never targeted at a specific AppHost, so
            // restarting it can't change its output — only (re)start when not already running.
            const pollingActive = this._pollingInterval !== undefined
                || this._psProcesses.size > 0
                || this._fetchInProgress;
            if (!pollingActive) {
                this._startPsPolling();
                // On resume from inactive, reconcile against an authoritative `aspire ps` snapshot to
                // catch AppHosts that started/stopped while we weren't following.
                if (refreshBeforeFollowOnResume && this._supportsPsFollow && this._appHosts.length > 0) {
                    this._refreshAppHostsFromAuthoritativeSnapshot();
                }
            }
        } else {
            this._stopPolling();
        }

        this._reconcileDescribeStreams();
    }

    // ── Describe-target coordinator ──

    // Decides which workspace AppHost `describe --follow` targets from the coordinator inputs (running
    // set from `aspire ps`, configured selection + idle candidates from `aspire ls`). A running
    // AppHost wins over a configured-but-idle selection, so `ls` completion can't retarget describe
    // away from the AppHost the user actually started.
    private _resolveWorkspaceDescribeTarget(): string | undefined {
        const running = this._runningWorkspaceAppHosts;
        const configured = this._configuredWorkspaceAppHostPath;

        // Configured selection that is actually running — honor it outright.
        if (configured && running.some(a => isMatchingAppHostPath(a.appHostPath, configured))) {
            return configured;
        }

        // A single running AppHost is a strong signal; adopt it even over a configured-but-idle selection.
        if (running.length === 1) {
            return running[0].appHostPath;
        }

        // While idle discovery is still streaming, don't fall back to an idle selection; wait for `ps`
        // (above) or completion (below) so we don't briefly target a non-running host.
        if (!this._workspaceAppHostDiscoveryComplete) {
            return undefined;
        }

        // Configured idle AppHost, honored even when it sits outside the ls candidate list.
        if (configured) {
            return configured;
        }

        // Exactly one idle candidate and nothing else to disambiguate → select it.
        if (this._workspaceAppHostCandidatePaths.length === 1) {
            return this._workspaceAppHostCandidatePaths[0];
        }

        // Multiple idle candidates, none running, none configured → the user must pick.
        return undefined;
    }

    // Resolves and persists the selected workspace describe target
    private _updateWorkspaceSelection(): void {
        if (this._disposed) {
            return;
        }

        const selection = this._viewMode === 'workspace'
            ? this._resolveWorkspaceDescribeTarget()
            : undefined;

        if (selection === undefined) {
            this._clearWorkspaceAppHostSelection();
        } else {
            this._setWorkspaceAppHostPathFromCurrentCandidates(selection);
        }
    }

    // ── Workspace app host (from aspire ls) ──

    private _fetchWorkspaceAppHost(options?: { forceRefresh?: boolean }): void {
        if (this._workspaceAppHostDiscoveryInProgress) {
            this._workspaceAppHostDiscoveryRefreshQueued = true;
            // Let the current discovery finish so we don't start overlapping CLI work, but
            // prevent its now-stale result from briefly restoring old AppHost candidates.
            this._workspaceAppHostDiscoveryVersion++;
            return;
        }

        const discoveryVersion = ++this._workspaceAppHostDiscoveryVersion;
        const workspaceFolders = vscode.workspace.workspaceFolders;
        if (!workspaceFolders || workspaceFolders.length === 0) {
            this._workspaceUsesWorkspaceRoot = false;
            this._workspaceAppHostDiscoveryComplete = true;
            this._clearWorkspaceAppHostDiscovery();
            this._clearWorkspaceAppHostData();
            this._clearErrors();
            this._syncPolling();
            this._updateWorkspaceContext({ clearLoading: true });
            return;
        }
        const rootFolder = workspaceFolders[0];
        this._workspaceUsesWorkspaceRoot = true;

        extensionLogOutputChannel.info('Fetching workspace apphost via shared AppHost discovery');

        const cancellationSource = new vscode.CancellationTokenSource();
        this._workspaceAppHostDiscoveryInProgress = true;
        this._workspaceAppHostDiscoveryCancellationSource = cancellationSource;

        // Kick off `aspire ps` polling now so the coordinator can adopt a running workspace
        // AppHost and start describe early, even while idle `aspire ls` discovery is still streaming.
        this._syncPolling();

        this._appHostDiscoveryService.discover(rootFolder, options?.forceRefresh, cancellationSource.token).then(appHosts => {
            if (cancellationSource.token.isCancellationRequested || !this._isCurrentWorkspaceDiscovery(discoveryVersion, rootFolder)) {
                return;
            }

            const result = getWorkspaceAppHostProjectSearchResult(rootFolder, appHosts);
            this._workspaceAppHostDiscoveryComplete = true;
            this._handleWorkspaceAppHostCandidates(result.app_host_candidates, result.selected_project_file);
        }).catch(error => {
            if (cancellationSource.token.isCancellationRequested || !this._isCurrentWorkspaceDiscovery(discoveryVersion, rootFolder)) {
                return;
            }

            this._workspaceAppHostDiscoveryComplete = true;
            extensionLogOutputChannel.warn(`Failed to fetch workspace apphost: ${error}`);
            this._clearWorkspaceAppHostDiscovery();
            this._clearWorkspaceAppHostData();
            this._setError('describe', errorFetchingAppHosts(String(error)));
            this._updateWorkspaceContext({ clearLoading: true });
            this._syncPolling();
        }).finally(() => {
            cancellationSource.dispose();
            if (this._workspaceAppHostDiscoveryCancellationSource !== cancellationSource) {
                return;
            }

            this._workspaceAppHostDiscoveryCancellationSource = undefined;
            this._workspaceAppHostDiscoveryInProgress = false;
            if (this._workspaceAppHostDiscoveryRefreshQueued && !this._disposed) {
                this._workspaceAppHostDiscoveryRefreshQueued = false;
                this._fetchWorkspaceAppHost({ forceRefresh: true });
            }
        });
    }

    private _cancelWorkspaceAppHostDiscovery(): void {
        this._workspaceAppHostDiscoveryRefreshQueued = false;
        this._workspaceAppHostDiscoveryCancellationSource?.cancel();
        this._workspaceAppHostDiscoveryCancellationSource?.dispose();
        this._workspaceAppHostDiscoveryCancellationSource = undefined;
        this._workspaceAppHostDiscoveryInProgress = false;
    }

    private _handleWorkspaceAppHostCandidates(appHostCandidates: readonly AppHostCandidate[], selectedAppHostPath: string | null): void {
        const buildableAppHostCandidates = appHostCandidates.filter(isBuildableAppHostCandidate);

        if (buildableAppHostCandidates.length === 0) {
            this._clearWorkspaceAppHostDiscovery();
            this._clearWorkspaceAppHostData();
            if (appHostCandidates.length > 0) {
                extensionLogOutputChannel.info(`aspire ls found ${appHostCandidates.length} AppHost candidates, but none are buildable`);
            }
            this._clearErrors();
            this._syncPolling();
            this._updateWorkspaceContext({ clearLoading: true });
            return;
        }

        if (buildableAppHostCandidates.length > 1) {
            this._setWorkspaceAppHostCandidatePaths(buildableAppHostCandidates);
            // Record the configured selection as coordinator input; the reconciler decides whether
            // to honor it (running) or adopt a running AppHost instead (configured-but-idle).
            this._configuredWorkspaceAppHostPath = selectedAppHostPath ?? undefined;
            this._workspaceAppHostDescription = workspaceViewSelectedMultipleAppHosts(buildableAppHostCandidates.length);
            extensionLogOutputChannel.info(`Workspace contains ${buildableAppHostCandidates.length} buildable AppHosts`);
            this._syncPolling();
            this._onDidChangeData.fire();
            return;
        }

        const selectedAppHostCandidate = selectedAppHostPath
            ? buildableAppHostCandidates.find(candidate => isMatchingAppHostPath(candidate.path, selectedAppHostPath))
            : buildableAppHostCandidates[0];
        if (selectedAppHostCandidate) {
            this._setWorkspaceAppHostCandidatePaths(buildableAppHostCandidates);
            this._configuredWorkspaceAppHostPath = selectedAppHostCandidate.path;
            this._workspaceAppHostDescription = workspaceViewSelectedSingleAppHost(formatAppHostLanguage(selectedAppHostCandidate.language));
            extensionLogOutputChannel.info(`Workspace apphost resolved: ${selectedAppHostCandidate.path} (${selectedAppHostCandidate.language}, ${selectedAppHostCandidate.status})`);
            this._syncPolling();
            this._onDidChangeData.fire();
            return;
        }

        this._clearWorkspaceAppHostDiscovery();
        this._syncPolling();
        this._updateWorkspaceContext({ clearLoading: true });
    }

    private _isCurrentWorkspaceDiscovery(discoveryVersion: number, workspaceFolder: vscode.WorkspaceFolder): boolean {
        const rootFolder = vscode.workspace.workspaceFolders?.[0];
        return !this._disposed
            && discoveryVersion === this._workspaceAppHostDiscoveryVersion
            && rootFolder?.uri.toString() === workspaceFolder.uri.toString();
    }

    private _setWorkspaceAppHostPathFromCurrentCandidates(appHostPath: string): void {
        this._workspaceAppHostPath = appHostPath;
        const appHostLabels = shortenPaths(this._workspaceAppHostCandidatePaths);
        const candidateIndex = this._workspaceAppHostCandidatePaths.findIndex(candidatePath => isMatchingAppHostPath(candidatePath, appHostPath));
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
        this._configuredWorkspaceAppHostPath = undefined;
        // Reset the running set too so a stale entry can't make the reconciler briefly target an
        // unvalidated host after a refresh re-runs discovery.
        this._runningWorkspaceAppHosts = [];
    }

    private _clearWorkspaceAppHostData(): void {
        this._workspaceAppHost = undefined;
        if (this._viewMode === 'workspace') {
            this._appHosts = [];
            this._appHostsSnapshot = '[]';
        }
    }

    // ── Workspace mode: describe --follow ──

    /**
     * Reads the CLI's advertised capabilities and maps the describe `--include-disabled-commands`
     * capability onto {@link _includeDisabledCommandsSupported}. Best-effort: on a missing/older CLI
     * the optimistic default and per-stream no-data fallback still cover us.
     */
    private async _resolveDescribeCapability(): Promise<void> {
        const configInfo = await this._configInfoProvider.getConfigInfo({ suppressErrors: true });
        if (this._disposed || !configInfo) {
            return;
        }

        this._includeDisabledCommandsSupported = configInfo.capabilities?.includes(describeIncludeDisabledCommandsCapability) ?? false;
        extensionLogOutputChannel.info(`CLI capability '${describeIncludeDisabledCommandsCapability}' ${this._includeDisabledCommandsSupported ? 'advertised' : 'not advertised'}; describe --include-disabled-commands ${this._includeDisabledCommandsSupported ? 'enabled' : 'disabled'}.`);
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

    private _clearStoppedWorkspaceAppHost(): void {
        const appHostPath = this._workspaceAppHost?.appHostPath ?? this._workspaceAppHostPath;
        this._workspaceAppHost = undefined;
        this._appHosts = appHostPath
            ? this._appHosts.filter(appHost => !isMatchingAppHostPath(appHost.appHostPath, appHostPath))
            : [];
    }

    // NDJSON line handler for both modes: a line either parses to a resource (merge it into the
    // stream's resource map) or is non-JSON noise. A projected workspace stream drives the error/
    // loading UX; others attach onto `_appHosts`.
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
                stream.parkedState = 'not-parked';
                stream.restartDelay = 5000; // Reset backoff on successful data
                if (this._isProjectedWorkspaceStream(stream.appHostPath)) {
                    this._setError('describe', undefined);
                    this._updateWorkspaceContext();
                } else {
                    this._attachResourcesToAppHosts();
                    this._onDidChangeData.fire();
                }
                return true;
            }
        } catch (e) {
            extensionLogOutputChannel.warn(`Failed to parse describe NDJSON line for ${stream.appHostPath}: ${e}`);
        }

        return false;
    }

    // ── Describe stream reconciliation ──
    // `_reconcileDescribeStreams` is the SOLE authority that starts/stops describe streams: computes
    // the desired path set, tears down streams outside it, starts newly-desired hosts, re-attaches
    // resources.

    private _reconcileDescribeStreams(): void {
        if (this._disposed) {
            return;
        }

        const desiredPaths = Array.from(this._computeDesiredDescribePaths());

        // Match by `isMatchingAppHostPath`, not raw key identity: the same AppHost can surface under
        // equivalent spellings (`My.AppHost.csproj` from get-apphosts vs `apphost.cs` from `aspire ps`).
        // A raw compare would stop a live stream when the spelling flips on the idle->running edge,
        // flickering the pane and respawning an identical stream.
        for (const key of Array.from(this._describeStreams.keys())) {
            if (!desiredPaths.some(d => isMatchingAppHostPath(key, d))) {
                this._stopDescribe(key);
            }
        }

        for (const path of desiredPaths) {
            const key = this._findDescribeStreamKey(path);
            if (key === undefined) {
                this._startDescribe(path);
                continue;
            }

            // A stream parked while idle restarts now that the host is running. If it still produces no
            // data it re-parks as `parked-active`, so this fires once on the idle->running edge instead
            // of looping. Pass `existingKey`: the live entry may be filed under a different spelling, so
            // `_restartDescribe` re-keys it.
            const stream = this._describeStreams.get(key)!;
            if (stream.parkedState === 'parked-idle' && this._isDescribeHostActive(path)) {
                this._restartDescribe(path, { existingKey: key });
            }
        }
        this._attachResourcesToAppHosts();
    }

    private _isRunningWorkspaceHost(path: string): boolean {
        return this._runningWorkspaceAppHosts.some(a => isMatchingAppHostPath(a.appHostPath, path));
    }

    // The set of AppHost paths that should have a live describe stream in the current view mode.
    private _activeDescribeHostPaths(): readonly string[] {
        if (this._viewMode === 'global') {
            return this._appHosts.map(a => a.appHostPath);
        }
        if (this._isSingleFileDescribeMode()) {
            return [SINGLE_FILE_DESCRIBE_KEY];
        }
        return this._runningWorkspaceAppHosts.map(a => a.appHostPath);
    }

    private _isDescribeHostActive(path: string): boolean {
        return this._activeDescribeHostPaths().some(d => isMatchingAppHostPath(path, d));
    }

    private _computeDesiredDescribePaths(): Set<string> {
        if (!this._dataActive) {
            return new Set();
        }

        const desired = new Set(this._activeDescribeHostPaths());

        // Single-file's only desired path is the sentinel (already included), so the idle-target
        // exception applies to real workspace targets only.
        if (this._viewMode === 'workspace' && !this._isSingleFileDescribeMode()) {
            const target = this._resolveWorkspaceDescribeTarget();
            if (target !== undefined && !this._isRunningWorkspaceHost(target)) {
                const existing = this._findDescribeStream(target);
                if (!(existing && existing.receivedData)) {
                    desired.add(target);
                }
            }
        }
        return desired;
    }

    // Attach each stream's resources onto its `_appHosts` entry. The projected workspace selection is
    // skipped (resources = null) because the pane renders it via `workspaceResources`, so attaching
    // here too would double-render.
    private _attachResourcesToAppHosts(): void {
        for (const appHost of this._appHosts) {
            if (this._isProjectedWorkspaceStream(appHost.appHostPath)) {
                appHost.resources = null;
                continue;
            }
            const stream = this._findDescribeStream(appHost.appHostPath);
            appHost.resources = stream ? Array.from(stream.resources.values()) : null;
        }
    }

    // The describe stream that drives the workspace pane. Normally the selected AppHost; in single-file
    // mode there is no resolved path, so the sentinel stream drives the pane.
    private _projectedDescribeKey(): string | undefined {
        if (this._viewMode !== 'workspace') {
            return undefined;
        }
        if (this._workspaceAppHostPath !== undefined) {
            return this._workspaceAppHostPath;
        }
        return this._isSingleFileDescribeMode() ? SINGLE_FILE_DESCRIBE_KEY : undefined;
    }

    // Single-file mode: workspace view is active with data flowing but no workspace folder anchors
    // discovery, so the describe watch runs path-less under the sentinel key.
    private _isSingleFileDescribeMode(): boolean {
        return this._dataActive
            && this._viewMode === 'workspace'
            && !this._workspaceUsesWorkspaceRoot;
    }

    // True when `path` is the AppHost the workspace pane is focused on. Evaluated lazily (not captured
    // at start) so a mid-stream selection change is honored.
    private _isProjectedWorkspaceStream(path: string): boolean {
        const key = this._projectedDescribeKey();
        return key !== undefined && isMatchingAppHostPath(path, key);
    }

    private _selectedDescribeStream(): DescribeStream | undefined {
        const key = this._projectedDescribeKey();
        return key !== undefined ? this._findDescribeStream(key) : undefined;
    }

    private _workspaceResourceList(): readonly ResourceJson[] {
        const stream = this._selectedDescribeStream();
        return stream ? Array.from(stream.resources.values()) : [];
    }

    private _findDescribeStreamKey(path: string): string | undefined {
        if (this._describeStreams.has(path)) {
            return path;
        }

        for (const key of this._describeStreams.keys()) {
            if (isMatchingAppHostPath(key, path)) {
                return key;
            }
        }
        return undefined;
    }

    private _findDescribeStream(path: string): DescribeStream | undefined {
        const key = this._findDescribeStreamKey(path);
        return key !== undefined ? this._describeStreams.get(key) : undefined;
    }

    private _isDescribePathDesired(path: string): boolean {
        for (const desired of this._computeDesiredDescribePaths()) {
            if (isMatchingAppHostPath(desired, path)) {
                return true;
            }
        }
        return false;
    }

    private _isCurrentStream(appHostPath: string, stream: DescribeStream, childProcess: ChildProcessWithoutNullStreams): boolean {
        return this._describeStreams.get(appHostPath) === stream && stream.process === childProcess;
    }

    private _startDescribe(appHostPath: string): void {
        if (this._disposed) {
            return;
        }

        const stream: DescribeStream = {
            appHostPath,
            process: undefined,
            resources: new Map(),
            nonJsonLines: [],
            stderr: '',
            restartTimer: undefined,
            restartDelay: 5000,
            version: 0,
            receivedData: false,
            parkedState: 'not-parked',
        };
        this._describeStreams.set(appHostPath, stream);
        const startVersion = ++stream.version;

        // The selected workspace host's stream backs the workspace loading UX; show the spinner while
        // it spins up. Other streams (global, or non-selected workspace hosts) load silently.
        if (this._isProjectedWorkspaceStream(appHostPath)) {
            this._loadingWorkspace = true;
            this._updateLoadingContext();
        }

        this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
            // Bail if we were stopped, replaced, or torn down while resolving the cli path.
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream || startVersion !== stream.version) {
                return;
            }

            // Read the cached capability synchronously — see constructor for why we don't await here.
            const includeDisabledCommands = this._includeDisabledCommandsSupported;
            const args = ['describe', '--follow', '--format', 'json'];
            if (includeDisabledCommands) {
                args.push('--include-disabled-commands');
            }
            // The single-file watch has no AppHost path to target, so it omits `--apphost` and lets the
            // CLI resolve the AppHost from the current directory, exactly as the old path-less watch did.
            const isSingleFile = appHostPath === SINGLE_FILE_DESCRIBE_KEY;
            if (!isSingleFile) {
                args.push('--apphost', appHostPath);
            }
            extensionLogOutputChannel.info(`Starting aspire describe --follow for AppHost ${isSingleFile ? '(single file)' : appHostPath}`);

            const childProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                noExtensionVariables: true,
                lineCallback: (line) => {
                    if (!this._isCurrentStream(appHostPath, stream, childProcess)) {
                        return;
                    }
                    if (!this._handleDescribeLine(stream, line) && stream.nonJsonLines.length < 20) {
                        stream.nonJsonLines.push(line);
                    }
                },
                stderrCallback: (data) => {
                    // Non-selected describe errors must not pollute the workspace error banner, but
                    // MUST be logged so users can diagnose missing resources (e.g. a CLI too old for
                    // `describe --apphost`). The projected selection surfaces errors via the no-data
                    // exit path, so only log here when NOT projected.
                    if (!this._isProjectedWorkspaceStream(appHostPath)) {
                        extensionLogOutputChannel.warn(`aspire describe --follow stderr for ${appHostPath}: ${data}`);
                    }
                    if (stream.stderr.length < 4000) {
                        stream.stderr += data;
                    }
                },
                exitCallback: (code) => {
                    if (!this._isCurrentStream(appHostPath, stream, childProcess)) {
                        return;
                    }
                    stream.process = undefined;
                    extensionLogOutputChannel.info(`aspire describe --follow for ${appHostPath} exited with code ${code}`);
                    if (this._disposed) {
                        return;
                    }
                    // A no-data exit while `--include-disabled-commands` was passed usually means the CLI is
                    // too old to accept that flag; disable it and retry once so older CLIs still get resources.
                    if (includeDisabledCommands && !stream.receivedData
                        && isIncludeDisabledCommandsUnsupportedOutput(stream.nonJsonLines, stream.stderr)) {
                        this._includeDisabledCommandsSupported = false;
                        this._restartDescribe(appHostPath);
                        return;
                    }
                    if (!this._isProjectedWorkspaceStream(appHostPath)) {
                        // Non-projected (global or non-selected workspace) stream. Drop it if its host is no
                        // longer active (gone from the tree, or no longer running in workspace mode);
                        // otherwise clear its resources and retry with backoff.
                        if (!this._isDescribeHostActive(appHostPath)) {
                            this._describeStreams.delete(appHostPath);
                            return;
                        }
                        this._clearNonProjectedStreamResources(stream);
                        this._scheduleDescribeRestart(stream);
                        return;
                    }

                    // Projected workspace selection — preserves the original main-pipeline UX.
                    if (!stream.receivedData) {
                        // Never produced data. Surface a compatibility hint when we have context, but do not
                        // auto-restart on a 5s loop forever — park the entry so reconcile does not respawn it.
                        this._parkStream(stream);
                        extensionLogOutputChannel.warn(`aspire describe --follow exited (code ${code}) without producing data; not auto-restarting.`);

                        let message: string | undefined;
                        let compatibility = false;
                        if (isDescribeUnsupportedOutput(stream.nonJsonLines, stream.stderr)) {
                            message = aspireCliDescribeNotSupported(aspireDescribeMinimumVersion);
                            compatibility = true;
                        } else if (this._workspaceAppHostPath && code !== 0) {
                            message = errorFetchingAppHosts(stream.stderr || `exit code ${code ?? 1}`);
                        } else if (this._workspaceAppHostPath && this._workspaceAppHost !== undefined) {
                            // A clean exit before `ps` observes the AppHost can happen while the app is still
                            // starting. Once `ps` reports the workspace AppHost as running, an empty successful
                            // describe stream means the AppHost cannot serve workspace resources even though the
                            // CLI command itself was accepted.
                            message = appHostDescribeMayNotBeSupported(aspireDescribeMinimumVersion);
                            compatibility = true;
                        }

                        this._setError('describe', message, compatibility);
                        this._updateWorkspaceContext({ clearLoading: true });
                        return;
                    }

                    // We had a working stream that ended (apphost shut down). Reset and retry once with backoff
                    // in case the apphost is restarting; a second no-data exit falls into the park branch above.
                    stream.resources.clear();
                    this._clearStoppedWorkspaceAppHost();
                    this._setError('describe', undefined);
                    this._updateWorkspaceContext();
                    this._scheduleDescribeRestart(stream);
                },
                errorCallback: (error) => {
                    if (!this._isCurrentStream(appHostPath, stream, childProcess)) {
                        return;
                    }
                    stream.process = undefined;
                    // A Node spawn `error` (e.g. ENOENT) fires without any subsequent `exit`, so it cannot
                    // drive the restart loop. Drop the dead entry; the next reconcile recreates it.
                    const message = errorFetchingAppHosts(error.message);
                    extensionLogOutputChannel.warn(`aspire describe --follow for ${appHostPath} error: ${message}`);
                    if (this._disposed) {
                        return;
                    }
                    this._describeStreams.delete(appHostPath);
                    if (this._isProjectedWorkspaceStream(appHostPath)) {
                        this._loadingWorkspace = false;
                        this._updateLoadingContext();
                        this._setError('describe', message, false);
                    } else {
                        this._clearNonProjectedStreamResources(stream);
                    }
                }
            });
            stream.process = childProcess;
        }).catch(error => {
            extensionLogOutputChannel.warn(`Failed to start describe for ${appHostPath}: ${error}`);
            // Same hazard as errorCallback below: getAspireCliExecutablePath() can reject (CLI missing,
            // permission denied) without firing the spawn error/exit callbacks that normally clean up.
            // Drop the dead entry so the next reconcile recreates it instead of leaving a zombie.
            if (this._describeStreams.get(appHostPath) === stream && startVersion === stream.version) {
                this._describeStreams.delete(appHostPath);
                if (this._isProjectedWorkspaceStream(appHostPath)) {
                    this._loadingWorkspace = false;
                    this._updateLoadingContext();
                    this._setError('describe', errorFetchingAppHosts(String(error)));
                }
            }
        });
    }

    // "Park" a stream: keep its map entry but drop resources and any pending restart timer so the
    // reconciler does not immediately respawn it. Records `parked-idle` vs `parked-active` from whether
    // the host was active at park time: reconcile restarts an idle-parked stream once its host becomes
    // active, but leaves an active-parked one alone so it cannot restart-loop. The single-file sentinel
    // has no idle state, so it parks as active (recovered via an explicit refresh).
    private _parkStream(stream: DescribeStream): void {
        stream.parkedState = this._isDescribeHostActive(stream.appHostPath) ? 'parked-active' : 'parked-idle';
        stream.resources.clear();
        if (stream.restartTimer) {
            clearTimeout(stream.restartTimer);
            stream.restartTimer = undefined;
        }
    }

    // Clear a non-projected (global / non-selected workspace) stream's resources and refresh the tree.
    // Projected streams drive the workspace pane through a different update path.
    private _clearNonProjectedStreamResources(stream: DescribeStream): void {
        stream.resources.clear();
        this._attachResourcesToAppHosts();
        this._onDidChangeData.fire();
    }

    // Tear down the existing map entry and start a fresh stream for `appHostPath`, guaranteeing the new
    // stream begins from a clean entry (no stale process/timer/version). The live entry can be filed
    // under a different path spelling than `appHostPath` (e.g. `My.AppHost.csproj` from get-apphosts vs
    // `apphost.cs` from `aspire ps`); callers that resolved the live key pass it as `existingKey` so the
    // old entry is removed rather than left as a duplicate. When `onlyIfDesired` is set, skip the
    // restart if the path has dropped out of the desired set.
    private _restartDescribe(appHostPath: string, options?: { onlyIfDesired?: boolean; existingKey?: string }): void {
        this._stopDescribe(options?.existingKey ?? appHostPath);
        if (options?.onlyIfDesired && !this._isDescribePathDesired(appHostPath)) {
            return;
        }
        this._startDescribe(appHostPath);
    }

    private _scheduleDescribeRestart(stream: DescribeStream): void {
        const appHostPath = stream.appHostPath;
        stream.parkedState = 'not-parked';
        const delay = stream.restartDelay;
        stream.restartDelay = Math.min(stream.restartDelay * 2, this._getPollingIntervalMs());
        extensionLogOutputChannel.info(`Restarting describe --follow for ${appHostPath} in ${delay}ms`);
        stream.restartTimer = setTimeout(() => {
            stream.restartTimer = undefined;
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream) {
                return;
            }
            this._restartDescribe(appHostPath, { onlyIfDesired: true });
        }, delay);
    }

    private async _runCliJson<T>(command: string, args: string[]): Promise<T> {
        const { stdout } = await this._runCliCommand(command, args);

        try {
            return parseCliJsonOutput<T>(stdout);
        } catch (error) {
            throw new AspireCliParseError(command, stdout, error);
        }
    }

    private async _runCliCommand(command: string, args: string[]): Promise<{ stdout: string; stderr: string }> {
        const cliPath = await this._terminalProvider.getAspireCliExecutablePath().catch(error => {
            throw new AspireCliNotInstalledError(String(error));
        });

        return new Promise<{ stdout: string; stderr: string }>((resolve, reject) => {
            let stdout = '';
            let stderr = '';
            let settled = false;
            let timeoutTimer: ReturnType<typeof setTimeout> | undefined;
            let cliProcess: ChildProcessWithoutNullStreams | undefined;

            const settle = (callback: () => void) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeoutTimer) {
                    clearTimeout(timeoutTimer);
                    timeoutTimer = undefined;
                }
                if (cliProcess) {
                    this._oneShotProcesses.delete(cliProcess);
                    if (cliProcess.exitCode === null && !cliProcess.killed) {
                        this._terminateProcess(cliProcess, command);
                    }
                }
                callback();
            };

            timeoutTimer = setTimeout(() => {
                settle(() => reject(new AspireCliFailedError(command, null, stdout, stderr || aspireCliCommandTimedOut(AppHostDataRepository._oneShotCommandTimeoutMs))));
            }, AppHostDataRepository._oneShotCommandTimeoutMs);

            cliProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                noExtensionVariables: true,
                stdoutCallback: (data) => { stdout += data; },
                stderrCallback: (data) => { stderr = appendLimitedOutput(stderr, data, AppHostDataRepository._oneShotOutputBufferLimit); },
                exitCallback: (code) => {
                    if (code !== 0) {
                        settle(() => reject(new AspireCliFailedError(command, code, stdout, stderr)));
                        return;
                    }

                    settle(() => resolve({ stdout, stderr }));
                },
                errorCallback: (error) => {
                    settle(() => reject(new AspireCliNotInstalledError(error.message)));
                },
            });
            this._oneShotProcesses.add(cliProcess);
        });
    }

    private async _fetchAppHostResourcesOnce(appHostPath: string): Promise<ResourceJson[]> {
        const snapshot = await this._runCliJson<DescribeSnapshotJson>('aspire describe', ['describe', '--format', 'json', '--apphost', appHostPath]);
        return snapshot.resources ?? [];
    }

    private _stopOneShotProcesses(): void {
        for (const process of this._oneShotProcesses) {
            this._terminateProcess(process, 'one-shot aspire command');
        }
        this._oneShotProcesses.clear();
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
            this._terminateProcess(childProcess, `aspire describe --follow (${appHostPath})`);
        }
    }

    private _stopAllDescribes(): void {
        for (const path of Array.from(this._describeStreams.keys())) {
            this._stopDescribe(path);
        }
    }

    private _updateWorkspaceContext(options?: { clearLoading?: boolean }): void {
        const hasWorkspaceAppHost = this._workspaceAppHost !== undefined;
        const workspaceResources = this._workspaceResourceList();
        const hasResources = workspaceResources.length > 0;
        const hasRunningAppHosts = this._appHosts.length > 0;
        const hasDashboardUrl = Boolean(this._workspaceAppHost?.dashboardUrl)
            || workspaceResources.some(resource => Boolean(resource.dashboardUrl))
            || this._appHosts.some(appHost => Boolean(appHost.dashboardUrl));
        const hasWorkspaceCandidates = this._workspaceAppHostCandidatePaths.length > 0;
        vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', !hasWorkspaceAppHost && !hasResources && !hasRunningAppHosts && !hasWorkspaceCandidates);
        // Keep this distinct from `noAppHosts`, which also considers discovered idle
        // candidates that have no live dashboard URL.
        vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasDashboardUrl);
        const clearLoading = options?.clearLoading ?? (hasResources || hasWorkspaceAppHost || hasRunningAppHosts || hasWorkspaceCandidates);
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
        if (this._pollingInterval) {
            clearInterval(this._pollingInterval);
            this._pollingInterval = undefined;
        }

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
        this._psPollingGeneration++;
        this._psFetchVersion++;
        this._fetchInProgress = false;
        this._authoritativeSnapshotInProgress = false;
        this._authoritativeSnapshotPending = false;
        this._activeAuthoritativeSnapshotRequestId = undefined;
        this._clearPostStopRefreshTimers();
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
                this._setError('ps', errorMessage);
                this._clearLoadingForCurrentView();
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
        let callbackInvoked = false;
        const removePsProcess = () => {
            if (psProcess) {
                this._psProcesses.delete(psProcess);
            } else {
                psProcessCompletedSynchronously = true;
            }
        };

        const args = ['ps', '--follow', '--format', 'json'];

        psProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
            noExtensionVariables: true,
            lineCallback: (line) => {
                if (!this._isCurrentPsFetch(fetchVersion) || line.trim().length === 0) {
                    return;
                }

                this._setError('ps', undefined);
                this._handlePsOutput(line);
            },
            exitCallback: (code) => {
                removePsProcess();
                if (callbackInvoked) {
                    return;
                }
                callbackInvoked = true;
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
                if (callbackInvoked) {
                    return;
                }
                callbackInvoked = true;
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

        if (this._viewMode === 'global' && this._loadingGlobal) {
            const hasDashboardUrl = this._appHosts.some(appHost => Boolean(appHost.dashboardUrl));
            this._loadingGlobal = false;
            this._updateLoadingContext();
            vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', this._appHosts.length === 0);
            vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasDashboardUrl);
        }
    }

    private _fetchAppHosts(): void {
        if (this._fetchInProgress || this._disposed || !this._shouldPoll) {
            return;
        }
        this._fetchInProgress = true;
        const fetchVersion = ++this._psFetchVersion;

        const args = ['ps', '--format', 'json'];
        this._runPsCommand(args, (code, stdout, stderr) => {
            if (code === 0) {
                this._setError('ps', undefined);
                this._handlePsOutput(stdout);
            } else {
                this._clearLoadingForCurrentView();
                this._setError('ps', errorFetchingAppHosts(stderr || `exit code ${code}`));
            }
            this._fetchInProgress = false;
        }, { fetchVersion });
    }

    private _refreshAppHostsFromAuthoritativeSnapshot(): void {
        if (this._disposed || !this._shouldPoll) {
            return;
        }

        if (this._authoritativeSnapshotInProgress) {
            this._authoritativeSnapshotPending = true;
            return;
        }

        this._authoritativeSnapshotInProgress = true;
        const snapshotRequestId = ++this._authoritativeSnapshotRequestId;
        this._activeAuthoritativeSnapshotRequestId = snapshotRequestId;
        const pollingGeneration = this._psPollingGeneration;
        const args = ['ps', '--format', 'json'];
        this._runPsCommand(args, (code, stdout, stderr) => {
            if (this._activeAuthoritativeSnapshotRequestId !== snapshotRequestId) {
                return;
            }

            if (pollingGeneration !== this._psPollingGeneration) {
                this._activeAuthoritativeSnapshotRequestId = undefined;
                this._authoritativeSnapshotInProgress = false;
                return;
            }

            if (!this._disposed && this._shouldPoll) {
                if (code === 0) {
                    this._setError('ps', undefined);
                    this._handlePsOutput(stdout);
                } else {
                    this._clearLoadingForCurrentView();
                    this._setError('ps', errorFetchingAppHosts(stderr || `exit code ${code}`));
                }
            }

            this._activeAuthoritativeSnapshotRequestId = undefined;
            this._authoritativeSnapshotInProgress = false;
            if (this._authoritativeSnapshotPending) {
                this._authoritativeSnapshotPending = false;
                this._refreshAppHostsFromAuthoritativeSnapshot();
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

    private _clearLoadingForCurrentView(): void {
        if (this._viewMode === 'workspace') {
            this._loadingWorkspace = false;
        } else {
            this._loadingGlobal = false;
        }
        this._updateLoadingContext();
    }

    private _clearErrors(): void {
        this._errors.describe = { message: undefined, isCompatibility: false };
        this._errors.ps = { message: undefined, isCompatibility: false };
        this._recomputeEffectiveError();
    }

    private _setError(source: ErrorSource, message: string | undefined, isCompatibility = false): void {
        // Compatibility is only meaningful when there's an actual message to flag.
        const normalized = message !== undefined && isCompatibility;
        const current = this._errors[source];
        if (current.message === message && current.isCompatibility === normalized) {
            return;
        }
        this._errors[source] = { message, isCompatibility: normalized };
        this._recomputeEffectiveError();
    }

    private _recomputeEffectiveError(): void {
        // Workspace mode prefers the describe error (discovery + describe-stream failures) and falls
        // back to ps; global mode only ever surfaces ps, which is never a compatibility error.
        const workspaceMode = this._viewMode === 'workspace';
        const { describe, ps } = this._errors;
        const message = workspaceMode ? describe.message ?? ps.message : ps.message;
        const isCompatibility = workspaceMode && describe.message !== undefined ? describe.isCompatibility : false;
        if (this._effectiveError.message === message && this._effectiveError.isCompatibility === isCompatibility) {
            return;
        }
        this._effectiveError = { message, isCompatibility };
        if (message) {
            extensionLogOutputChannel.warn(message);
        }
        const hasError = message !== undefined;
        vscode.commands.executeCommand('setContext', 'aspire.fetchAppHostsError', hasError);
        vscode.commands.executeCommand('setContext', 'aspire.fetchAppHostsCompatibilityError', hasError && isCompatibility);
        this._onDidChangeData.fire();
    }

    private _handlePsOutput(stdout: string): void {
        try {
            const parsed: AppHostDisplayInfo[] | AppHostDisplayInfo = JSON.parse(stdout);
            const appHosts = Array.isArray(parsed)
                ? parsed
                : this._applyPsDelta(parsed);

            if (this._viewMode === 'workspace') {
                this._handleWorkspacePsOutput(appHosts);
                return;
            }

            // Compare against the previous post-reconcile snapshot, not the raw ps payload. `appHosts`
            // here lacks the `resources` field (ps no longer emits it after #17479), while `_appHosts`
            // was mutated by the prior _attachResourcesToAppHosts to include resources — a direct
            // compare would always report `changed` once any stream produced resources.
            const previousSnapshot = this._appHostsSnapshot;
            this._appHosts = appHosts;
            this._reconcileDescribeStreams();
            const nextSnapshot = JSON.stringify(this._appHosts);
            const changed = nextSnapshot !== previousSnapshot;
            this._appHostsSnapshot = nextSnapshot;

            if (this._loadingGlobal) {
                this._loadingGlobal = false;
                this._updateLoadingContext();
            }

            if (changed) {
                const hasDashboardUrl = this._appHosts.some(appHost => Boolean(appHost.dashboardUrl));
                vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', appHosts.length === 0);
                vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasDashboardUrl);
                this._onDidChangeData.fire();
            }
        } catch (e) {
            extensionLogOutputChannel.warn(`Failed to parse aspire ps output: ${e}`);
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

    private _handleWorkspacePsOutput(appHosts: readonly AppHostDisplayInfo[]): void {
        const discoveryPending = !this._workspaceAppHostDiscoveryComplete;
        let workspaceAppHosts: AppHostDisplayInfo[];
        if (discoveryPending && this._workspaceAppHostCandidatePaths.length === 0) {
            workspaceAppHosts = appHosts.filter(appHost => isPathInWorkspace(appHost.appHostPath));
        } else if (this._workspaceAppHostCandidatePaths.length > 0) {
            workspaceAppHosts = appHosts.filter(appHost => this._workspaceAppHostCandidatePaths.some(candidatePath => isMatchingAppHostPath(appHost.appHostPath, candidatePath)));
        } else {
            workspaceAppHosts = [];
        }

        // Feed the running set into the coordinator and let it (re)target describe.
        // `_reconcileDescribeStreams` is the sole authority that starts/stops describe streams.
        this._runningWorkspaceAppHosts = workspaceAppHosts;
        this._updateWorkspaceSelection();

        const workspaceAppHostPath = this._workspaceAppHostPath;
        const workspaceAppHost = workspaceAppHostPath
            ? workspaceAppHosts.find(appHost => isMatchingAppHostPath(appHost.appHostPath, workspaceAppHostPath))
            : undefined;
        const changed = JSON.stringify(workspaceAppHosts) !== JSON.stringify(this._appHosts)
            || JSON.stringify(workspaceAppHost) !== JSON.stringify(this._workspaceAppHost);

        // Update `_appHosts`/`_workspaceAppHost` BEFORE reconciling: the reconcile computes the desired
        // set and attaches resources from `_appHosts`, so it must see the new running set.
        this._appHosts = workspaceAppHosts;
        this._appHostsSnapshot = JSON.stringify(this._appHosts);
        this._workspaceAppHost = workspaceAppHost;
        this._reconcileDescribeStreams();

        if (changed || this._loadingWorkspace) {
            this._updateWorkspaceContext({ clearLoading: !discoveryPending || workspaceAppHosts.length > 0 });
        }
    }

    private async _runPsCommand(args: string[], callback: (code: number, stdout: string, stderr: string) => void, options?: { fetchVersion?: number }): Promise<void> {
        const fetchVersion = options?.fetchVersion;
        const isCurrentPsCommand = () => {
            if (fetchVersion !== undefined) {
                return this._isCurrentPsFetch(fetchVersion);
            }

            return !this._disposed && this._shouldPoll;
        };

        let cliPath: string;
        try {
            cliPath = await this._terminalProvider.getAspireCliExecutablePath();
        } catch (error) {
            if (isCurrentPsCommand()) {
                const rawErrorMessage = String(error);
                extensionLogOutputChannel.warn(errorFetchingAppHosts(rawErrorMessage));
                callback(1, '', rawErrorMessage);
            }
            return;
        }

        if (!isCurrentPsCommand()) {
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
                    if (isCurrentPsCommand()) {
                        callback(code ?? 1, stdout, stderr);
                    }
                }
            },
            errorCallback: (error) => {
                removePsProcess();
                extensionLogOutputChannel.warn(errorFetchingAppHosts(error.message));
                if (!callbackInvoked) {
                    callbackInvoked = true;
                    if (isCurrentPsCommand()) {
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

function getCommandOutputSuffix(stdout: string, stderr: string): string {
    const output = (stderr || stdout).trim();
    const limitedOutput = output.length <= oneShotOutputBufferLimit
        ? output
        : output.slice(output.length - oneShotOutputBufferLimit);

    return limitedOutput ? `: ${limitedOutput}` : '';
}

function appendLimitedOutput(existing: string, data: string, limit: number): string {
    const combined = existing + data;

    return combined.length <= limit ? combined : combined.slice(combined.length - limit);
}

function parseCliJsonOutput<T>(stdout: string): T {
    try {
        return JSON.parse(stdout);
    } catch (error) {
        // Some CLI invocations can emit startup diagnostics before the final JSON payload:
        //   Starting AppHost...
        //   {"resources":[{"name":"api", ...}]}
        // Parse the whole output first for the normal deterministic path, then fall back to
        // the last JSON-looking line so older or chatty CLIs do not poison the snapshot.
        for (const line of stdout.split(/\r?\n/).reverse()) {
            const trimmed = line.trim();
            if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
                try {
                    return JSON.parse(trimmed);
                } catch {
                    // Keep scanning in case the CLI wrote a JSON-looking diagnostic after the payload.
                }
            }
        }

        throw error;
    }
}

function isPathInWorkspace(filePath: string): boolean {
    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    if (!workspaceFolder) {
        return false;
    }

    const relativePath = path.relative(workspaceFolder.uri.fsPath, filePath);
    return relativePath !== ''
        && !relativePath.startsWith('..')
        && !path.isAbsolute(relativePath);
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

function isIncludeDisabledCommandsUnsupportedOutput(nonJsonLines: readonly string[], stderr: string): boolean {
    // This is only consulted after a describe attempt produced no resource data, so any
    // non-JSON/stderr output here is diagnostic text rather than successful output. When the
    // CLI accepts `--include-disabled-commands` it streams JSON resources and never echoes the
    // flag name back, so the literal flag token only appears when the CLI is reporting that it
    // does not recognize the option, e.g.:
    //   English:  Unrecognized command or argument '--include-disabled-commands'.
    //   Spanish:  No se encuentra el recurso '--include-disabled-commands'.
    // The flag token itself is never localized, so detecting on its presence keeps this fallback
    // locale-independent — matching on translated phrases like "unrecognized option" would miss
    // non-English CLI output (e.g. via ASPIRE_LOCALE_OVERRIDE or the system locale).
    const output = [...nonJsonLines, stderr].join('\n');
    return output.includes('--include-disabled-commands');
}

export function isMatchingAppHostPath(left: string | undefined, right: string | undefined): boolean {
    if (!left || !right) {
        return false;
    }

    const normalizedLeft = getComparisonKey(path.normalize(left));
    const normalizedRight = getComparisonKey(path.normalize(right));
    if (normalizedLeft === normalizedRight) {
        return true;
    }

    // `aspire extension get-apphosts` resolves a project file while `aspire ps`
    // can report the AppHost source file. Match by directory only for that
    // project/source-file shape so sibling AppHost projects don't collapse into
    // the same workspace AppHost.
    return getComparisonKey(path.dirname(normalizedLeft)) === getComparisonKey(path.dirname(normalizedRight))
        && isProjectFileToSourceFileMatch(normalizedLeft, normalizedRight);
}

export function isAppHostPathUnderFolder(appHostPath: string | undefined, folderPath: string | undefined): boolean {
    if (!appHostPath || !folderPath) {
        return false;
    }

    const normalizedAppHostPath = getComparisonKey(path.normalize(appHostPath));
    const normalizedFolderPath = getComparisonKey(path.normalize(folderPath));
    if (normalizedAppHostPath === normalizedFolderPath) {
        return false;
    }

    const folderPrefix = normalizedFolderPath.endsWith(path.sep) ? normalizedFolderPath : `${normalizedFolderPath}${path.sep}`;
    return normalizedAppHostPath.startsWith(folderPrefix);
}

function isProjectFileToSourceFileMatch(left: string, right: string): boolean {
    return (isProjectFile(left) && isAppHostSourceFile(right)) || (isAppHostSourceFile(left) && isProjectFile(right));
}

function isProjectFile(value: string): boolean {
    return path.extname(value).toLowerCase() === '.csproj';
}

function isAppHostSourceFile(value: string): boolean {
    const fileName = path.basename(value).toLowerCase();
    return fileName === 'apphost.cs' || fileName === 'program.cs';
}

function isSameAppHostPath(left: string | undefined, right: string | undefined): boolean {
    if (!left || !right) {
        return false;
    }

    return getComparisonKey(path.normalize(left)) === getComparisonKey(path.normalize(right));
}

function isMatchingAppHostInstance(left: AppHostDisplayInfo, right: AppHostDisplayInfo): boolean {
    return left.appHostPid === right.appHostPid
        && isSameAppHostPath(left.appHostPath, right.appHostPath);
}
