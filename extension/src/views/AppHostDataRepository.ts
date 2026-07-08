import * as vscode from 'vscode';
import * as path from 'path';
import { ChildProcessWithoutNullStreams, spawn as spawnProcess } from 'child_process';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import { appHostDescribeMayNotBeSupported, appHostPathMustBeNonEmptyAbsolute, aspireCliCommandFailed, aspireCliCommandTimedOut, aspireCliDescribeNotSupported, aspireCliOutputParseFailed, aspireCommandOutputTruncated, aspireDescribeMinimumVersion, errorFetchingAppHosts, workspaceViewSelectedMultipleAppHosts, workspaceViewSelectedSingleAppHost } from '../loc/strings';
import { AppHostCandidate, AppHostDiscoveryService, formatAppHostLanguage, getWorkspaceAppHostProjectSearchResult, isBuildableAppHostCandidate } from '../utils/appHostDiscovery';
import { isNoLogoUnsupportedOutput, noLogoOption, removeRootNoLogoOption } from '../utils/cliCompatibility';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { describeIncludeDisabledCommandsCapability } from '../types/configInfo';
import { nonInteractiveCliEnvironment } from '../utils/environment';

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
        super(aspireCliCommandFailed(command, String(exitCode), ''));
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

/**
 * Captured output from a hidden `aspire resource ...` execution. `stdout` carries the rendered
 * command value (when the command returns one); `stderr` carries human-readable status/errors.
 */
export interface ResourceCommandExecutionOutput {
    stdout: string;
    stderr: string;
}

export type ViewMode = 'workspace' | 'global';

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
const oneShotOutputBufferLimit = 64 * 1024;

interface RunCliCommandOptions {
    timeoutMs?: number | null;
    stdoutBufferLimit?: number | null;
    cancellationToken?: vscode.CancellationToken;
    env?: { name: string; value: string }[];
}

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
    private _openAppHostPaths: readonly string[] = [];
    private _hasEverBeenDataActive = false;

    // ── Workspace mode state (describe --follow) ──
    // Whether `aspire describe` accepts the hidden `--include-disabled-commands` flag. Resolved
    // lazily from the CLI's advertised capabilities (`aspire config info --json`) so we don't pass
    // the flag to an older CLI that would reject it and emit no resource data. Starts optimistic so
    // that, if capability resolution fails (e.g. a CLI too old to support `config info`), we still
    // attempt the flag and rely on the locale-independent no-data fallback below.
    private _includeDisabledCommandsSupported = true;
    private _noLogoSupported = true;
    private readonly _configInfoProvider: ConfigInfoProvider;

    // ── Running AppHost state (ps polling) ──
    private _appHosts: AppHostDisplayInfo[] = [];
    // Cached JSON serialization of the app-host list rendered by the current view after the most
    // recent reconcile so _handlePsOutput can detect real changes. We can't compare raw `ps` output
    // directly because the in-memory state has merged resources, while `ps` no longer emits them
    // (#17479) — see _handlePsOutput for the rationale.
    private _appHostsSnapshot = '[]';
    private _pollingInterval: ReturnType<typeof setInterval> | undefined;
    private _psProcesses = new Set<ChildProcessWithoutNullStreams>();
    private _psPollingGeneration = 0;
    private _oneShotProcesses = new Set<ChildProcessWithoutNullStreams>();
    private _psFetchVersion = 0;
    private _supportsPsFollow = true;
    private _fetchInProgress = false;
    // Prevents a second `ps --follow` start while the first one is still resolving the CLI path.
    private _psFollowStartPending = false;
    private _postStopRefreshTimers = new Map<string, PostStopRefreshTimer>();
    private _authoritativeSnapshotInProgress = false;
    private _authoritativeSnapshotPending = false;
    private _authoritativeSnapshotPendingForce = false;
    private _runtimeSnapshotAfterWorkspaceDiscovery = false;
    private _authoritativeSnapshotRequestId = 0;
    private _activeAuthoritativeSnapshotRequestId: number | undefined;

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
    private _workspaceAppHostDescription: string | undefined;
    private _workspaceAppHostDiscoveryComplete = false;
    private _workspaceAppHostDiscoveryVersion = 0;
    private _workspaceAppHostDiscoveryInProgress = false;
    private _workspaceAppHostDiscoveryRefreshQueued = false;
    private _workspaceAppHostDiscoveryCancellationSource: vscode.CancellationTokenSource | undefined;
    private readonly _appHostDiscoveryChangeDisposable: vscode.Disposable;
    private readonly _workspaceFoldersChangeDisposable: vscode.Disposable;
    private readonly _appHostDiscoveryService: AppHostDiscoveryService;
    private readonly _ownsAppHostDiscoveryService: boolean;

    // ── Error state ──
    private _describeErrorMessage: string | undefined;
    private _describeErrorIsCompatibility = false;
    private _psErrorMessage: string | undefined;
    private _errorMessage: string | undefined;
    private _errorIsCompatibility = false;

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
            this._clearErrors();
            this._updateWorkspaceContext();
            this._fetchWorkspaceAppHost({ forceRefresh: true });
        });
        this._fetchWorkspaceAppHost();
        this._configChangeDisposable = vscode.workspace.onDidChangeConfiguration(e => {
            if ((e.affectsConfiguration('aspire.appHostsPollingInterval') || e.affectsConfiguration('aspire.globalAppHostsPollingInterval')) && this._dataActive) {
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
        this._runtimeSnapshotAfterWorkspaceDiscovery = false;
        // A user-triggered refresh should observe AppHost/config files written by tools
        // even when the file watcher has not delivered an invalidation event yet.
        // Refresh only needs to re-pull discovery + the authoritative `ps` snapshot and let
        // reconcile preserve streams for still-rendered hosts.
        this._workspaceAppHostDiscoveryComplete = false;
        this._clearWorkspaceAppHostDiscovery();
        this._updateWorkspaceContext();
        this._fetchWorkspaceAppHost({ forceRefresh: true });
        this._reconcileDescribes();
        if (this._dataActive) {
            this._refreshAppHostsFromAuthoritativeSnapshot();
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
        this._refreshAppHostsFromAuthoritativeSnapshot(forceSnapshot);
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

    async fetchAppHostsOnce(): Promise<AppHostDisplayInfo[]> {
        const appHosts = await this._runCliJson<AppHostDisplayInfo[] | AppHostDisplayInfo>('aspire ps', this._withNoLogo(['ps', '--format', 'json']));
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
        if (appHostPath !== undefined) {
            const trimmedAppHostPath = appHostPath.trim();
            if (!trimmedAppHostPath || !path.isAbsolute(trimmedAppHostPath)) {
                throw new Error(appHostPathMustBeNonEmptyAbsolute);
            }

            args.push('--apphost', trimmedAppHostPath);
        }

        if (additionalArgs.length > 0) {
            args.push(...additionalArgs);
        }

        try {
            const output = await this._runCliCommand(`aspire resource ${commandName}`, args, {
                timeoutMs: null,
                stdoutBufferLimit: AppHostDataRepository._oneShotOutputBufferLimit,
                cancellationToken,
                env: nonInteractiveCliEnvironment,
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
        this._authoritativeSnapshotPending = false;
        this._authoritativeSnapshotPendingForce = false;
        this._runtimeSnapshotAfterWorkspaceDiscovery = false;
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
            const pollingActive = this._pollingInterval !== undefined
                || this._psProcesses.size > 0
                || this._fetchInProgress
                || this._psFollowStartPending;
            if (!pollingActive) {
                this._startPsPolling();
                if (refreshBeforeFollowOnResume && this._supportsPsFollow && this._appHosts.length > 0) {
                    this._refreshAppHostsFromAuthoritativeSnapshot();
                }
            }
        } else {
            this._stopPolling();
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
            // Let the current discovery finish so we don't start overlapping CLI work, but
            // prevent its now-stale result from briefly restoring old AppHost candidates.
            this._workspaceAppHostDiscoveryVersion++;
            return;
        }

        const discoveryVersion = ++this._workspaceAppHostDiscoveryVersion;
        const rootFolder = workspaceFolders[0];

        extensionLogOutputChannel.info('Fetching workspace apphost via shared AppHost discovery');

        const cancellationSource = new vscode.CancellationTokenSource();
        this._workspaceAppHostDiscoveryInProgress = true;
        this._workspaceAppHostDiscoveryCancellationSource = cancellationSource;

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
            this._runtimeSnapshotAfterWorkspaceDiscovery = false;
            this._clearWorkspaceAppHostDiscovery();
            this._setDescribeError(errorFetchingAppHosts(String(error)));
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
        this._runtimeSnapshotAfterWorkspaceDiscovery = false;
        this._workspaceAppHostDiscoveryCancellationSource?.cancel();
        this._workspaceAppHostDiscoveryCancellationSource?.dispose();
        this._workspaceAppHostDiscoveryCancellationSource = undefined;
        this._workspaceAppHostDiscoveryInProgress = false;
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
                this._refreshAppHostsFromAuthoritativeSnapshot(true);
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
            this._syncPolling();
            // Re-scope the current `aspire ps` snapshot now that discovery has resolved the workspace
            // candidate paths. This refreshes the view AND clears the workspace "Searching…" loading
            // state immediately (discovery is now complete). Without it, loading would linger until the
            // next `aspire ps --follow` change, which may never arrive when the running set is already
            // stable — leaving the workspace view stuck on "Searching…" until the user toggles view mode.
            this._handlePsSnapshot(this._appHosts, { force: true });
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
            // Force a re-scope so the workspace view refreshes and the loading state clears immediately
            // once discovery resolves the candidate, without waiting for the next ps change (see the
            // multi-candidate branch above for the full rationale).
            this._handlePsSnapshot(this._appHosts, { force: true });
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

    // ── describe --follow ──

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

    /**
     * Starts a single `aspire describe --follow --apphost <path>` stream, held in
     * {@link _describeStreams} keyed by `appHostPath`. Every stream is an equal peer: it merges its
     * resources into `appHost.resources` and, while its host remains in `_appHosts`, restarts with
     * backoff. A describe is only ever started for a host `aspire ps` has confirmed running, so there
     * is no proactive/eager start and no special "selected" stream.
     *
     * A no-data exit that looks like a compatibility problem surfaces the describe error banner once
     * (via the idempotent {@link _setDescribeError}); any working describe from any host clears it,
     * since a single working stream proves the CLI supports `describe`.
     */
    private _startDescribe(appHostPath: string, forceIncludeDisabledCommands?: boolean): void {
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
            restartDelay: 5000,
            version: 0,
        };
        this._describeStreams.set(appHostPath, stream);
        const startVersion = ++stream.version;

        this._terminalProvider.getAspireCliExecutablePath().then(cliPath => {
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream || startVersion !== stream.version) {
                return;
            }

            // Read the cached capability synchronously — see constructor for why we don't await here.
            const includeDisabledCommands = forceIncludeDisabledCommands ?? this._includeDisabledCommandsSupported;
            const args = this._withNoLogo(['describe', '--follow', '--format', 'json']);
            if (includeDisabledCommands) {
                args.push('--include-disabled-commands');
            }
            args.push('--apphost', appHostPath);

            extensionLogOutputChannel.info(`Starting aspire describe --follow (--apphost ${appHostPath})`);

            stream.receivedData = false;
            const describeProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
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

                    if (code !== 0 && this._disableNoLogoForRetry(args, stream.nonJsonLines.join('\n'), stream.stderr, `aspire describe --follow --apphost ${appHostPath}`)) {
                        this._describeStreams.delete(appHostPath);
                        this._startDescribe(appHostPath, forceIncludeDisabledCommands);
                        return;
                    }

                    // Capability fallback: a CLI too old to accept `--include-disabled-commands` exits
                    // without data. Retry once without the flag.
                    if (includeDisabledCommands && !stream.receivedData && isIncludeDisabledCommandsUnsupportedOutput(stream.nonJsonLines, stream.stderr)) {
                        this._includeDisabledCommandsSupported = false;
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

                    // A stream that already produced resources and then exits means that AppHost stopped.
                    if (stream.receivedData) {
                        stream.resources.clear();
                        this._describeStreams.delete(appHostPath);
                        this._handlePsSnapshot(this._appHosts.filter(appHost => !isMatchingAppHostPath(appHost.appHostPath, appHostPath)));
                        return;
                    }

                    // A stream that never produced resources and exits (cleanly or with an error) means the CLI
                    // cannot describe the host. Surface it once through the describe banner
                    const noDataError = this._getDescribeNoDataError(code, stream.nonJsonLines, stream.stderr);
                    if (noDataError.message) {
                        extensionLogOutputChannel.warn(`aspire describe --follow (--apphost ${appHostPath}) exited (code ${code}) without producing data.`);
                        this._setDescribeError(noDataError.message, { compatibility: noDataError.isCompatibilityError });
                    }

                    stream.resources.clear();
                    this._attachResourcesToAppHosts();
                    this._onDidChangeData.fire();

                    const delay = stream.restartDelay;
                    stream.restartDelay = Math.min(delay * 2, this._getPollingIntervalMs());
                    extensionLogOutputChannel.info(`Restarting describe --follow --apphost ${appHostPath} in ${delay}ms`);
                    stream.restartTimer = setTimeout(() => {
                        stream.restartTimer = undefined;
                        if (this._disposed || this._describeStreams.get(appHostPath) !== stream) {
                            return;
                        }
                        this._describeStreams.delete(appHostPath);
                        if (!this._appHosts.some(appHost => appHost.appHostPath === appHostPath)) {
                            this._attachResourcesToAppHosts();
                            this._onDidChangeData.fire();
                            return;
                        }
                        this._startDescribe(appHostPath);
                    }, delay);
                },
                errorCallback: (error) => {
                    if (this._describeStreams.get(appHostPath) !== stream || stream.process !== describeProcess) {
                        return;
                    }

                    // Spawn/stream error (as opposed to a clean exit): surface it through the single
                    // describe banner. `_setDescribeError` is idempotent, so the same error is shown only
                    // once across peer streams. Drop the dead stream so the next `ps` reconcile recreates
                    // it if the host is still running.
                    extensionLogOutputChannel.warn(`aspire describe --follow --apphost ${appHostPath} error: ${error.message}`);
                    stream.process = undefined;
                    this._describeStreams.delete(appHostPath);
                    stream.resources.clear();
                    this._setDescribeError(errorFetchingAppHosts(error.message));
                    this._attachResourcesToAppHosts();
                    this._onDidChangeData.fire();
                }
            });
            stream.process = describeProcess;
        }).catch(error => {
            if (this._disposed || this._describeStreams.get(appHostPath) !== stream || startVersion !== stream.version) {
                return;
            }
            // Resolving the CLI path failed, so no describe stream can run. Surface it once through the
            // describe banner (workspace mode shows it; other modes swallow it, matching prior behavior).
            extensionLogOutputChannel.warn(`Failed to start describe watch (--apphost ${appHostPath}): ${error}`);
            this._describeStreams.delete(appHostPath);
            this._setDescribeError(errorFetchingAppHosts(String(error)));
        });
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
                this._setDescribeError(undefined);
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

        if (desired.size === 0 && this._describeErrorIsCompatibility) {
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

    private async _runCliJson<T>(command: string, args: string[]): Promise<T> {
        const { stdout } = await this._runCliCommand(command, args);

        try {
            return parseCliJsonOutput<T>(stdout);
        } catch (error) {
            throw new AspireCliParseError(command, stdout, error);
        }
    }

    private _withNoLogo(args: string[]): string[] {
        if (!this._noLogoSupported) {
            return args;
        }

        const appHostIndex = args.indexOf('--apphost');
        const insertIndex = appHostIndex === -1 ? args.length : appHostIndex;
        return [...args.slice(0, insertIndex), noLogoOption, ...args.slice(insertIndex)];
    }

    // Returns the args to retry with when the installed CLI does not recognize --nologo, or
    // undefined when this failure is unrelated to --nologo. Has the intentional side effect of
    // flipping _noLogoSupported to false the first time the unsupported pattern is observed so
    // subsequent _withNoLogo calls stop adding the option for the lifetime of the repository.
    //
    // Callers that own their own retry args use the returned value directly; long-lived watch
    // restarters (describe/ps follow) use _disableNoLogoForRetry below and intentionally discard
    // the returned args because the watch starter rebuilds args via _withNoLogo.
    private _tryGetNoLogoRetryArgs(args: string[], stdout: string, stderr: string, operation: string): string[] | undefined {
        if (!isNoLogoUnsupportedOutput(args, stdout, stderr)) {
            return undefined;
        }

        if (this._noLogoSupported) {
            this._noLogoSupported = false;
            extensionLogOutputChannel.info(`Installed Aspire CLI does not recognize ${noLogoOption}; retrying ${operation} without it.`);
        }

        return removeRootNoLogoOption(args);
    }

    // Boolean variant of _tryGetNoLogoRetryArgs for watch restarters that rebuild args via
    // _withNoLogo when they restart. These call sites only need to know "did we just disable
    // --nologo support for the rest of this session?" — the recomputed args from
    // _tryGetNoLogoRetryArgs would be thrown away.
    private _disableNoLogoForRetry(args: string[], stdout: string, stderr: string, operation: string): boolean {
        return this._tryGetNoLogoRetryArgs(args, stdout, stderr, operation) !== undefined;
    }

    private async _runCliCommand(command: string, args: string[], options: RunCliCommandOptions = {}): Promise<{ stdout: string; stderr: string }> {
        const cliPath = await this._terminalProvider.getAspireCliExecutablePath().catch(error => {
            throw new AspireCliNotInstalledError(String(error));
        });

        if (options.cancellationToken?.isCancellationRequested) {
            throw new vscode.CancellationError();
        }

        return new Promise<{ stdout: string; stderr: string }>((resolve, reject) => {
            let settled = false;
            let timeoutTimer: ReturnType<typeof setTimeout> | undefined;
            let cliProcess: ChildProcessWithoutNullStreams | undefined;
            let cancellationRegistration: vscode.Disposable | undefined;
            const timeoutMs = options.timeoutMs === undefined ? AppHostDataRepository._oneShotCommandTimeoutMs : options.timeoutMs;
            const stdoutBufferLimit = options.stdoutBufferLimit === undefined ? null : options.stdoutBufferLimit;
            const stdout = new LimitedOutputBuffer(stdoutBufferLimit);
            const stderr = new LimitedOutputBuffer(AppHostDataRepository._oneShotOutputBufferLimit);

            const settle = (callback: () => void) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeoutTimer) {
                    clearTimeout(timeoutTimer);
                    timeoutTimer = undefined;
                }
                cancellationRegistration?.dispose();
                cancellationRegistration = undefined;
                if (cliProcess) {
                    this._oneShotProcesses.delete(cliProcess);
                    if (cliProcess.exitCode === null && !cliProcess.killed) {
                        this._terminateProcess(cliProcess, command);
                    }
                }
                callback();
            };

            if (timeoutMs !== null) {
                timeoutTimer = setTimeout(() => {
                    settle(() => reject(new AspireCliFailedError(command, null, stdout.value, stderr.value || aspireCliCommandTimedOut(timeoutMs))));
                }, timeoutMs);
            }

            cancellationRegistration = options.cancellationToken?.onCancellationRequested(() => {
                settle(() => reject(new vscode.CancellationError()));
            });

            cliProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                noExtensionVariables: true,
                env: options.env,
                stdoutCallback: (data) => { stdout.append(data); },
                stderrCallback: (data) => { stderr.append(data); },
                exitCallback: (code) => {
                    if (code !== 0) {
                        const retryArgs = this._tryGetNoLogoRetryArgs(args, stdout.value, stderr.value, command);
                        if (retryArgs) {
                            settle(() => {
                                this._runCliCommand(command, retryArgs, options).then(resolve, reject);
                            });
                            return;
                        }

                        settle(() => reject(new AspireCliFailedError(command, code, stdout.value, stderr.value)));
                        return;
                    }

                    settle(() => resolve({ stdout: stdout.value, stderr: stderr.value }));
                },
                errorCallback: (error) => {
                    settle(() => reject(new AspireCliNotInstalledError(error.message)));
                },
            });
            this._oneShotProcesses.add(cliProcess);
        });
    }

    private async _fetchAppHostResourcesOnce(appHostPath: string): Promise<ResourceJson[]> {
        const snapshot = await this._runCliJson<DescribeSnapshotJson>('aspire describe', this._withNoLogo(['describe', '--format', 'json', '--apphost', appHostPath]));
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
            this._terminateProcess(childProcess, `aspire describe --follow (${appHostPath})`, { suppressTimeoutWarning: true });
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

    // ── ps polling ──

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
        this._psFollowStartPending = false;
        this._authoritativeSnapshotInProgress = false;
        this._authoritativeSnapshotPending = false;
        this._authoritativeSnapshotPendingForce = false;
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
        const interval = getConfiguredNumber(config, 'appHostsPollingInterval')
            ?? getConfiguredNumber(config, 'globalAppHostsPollingInterval')
            ?? config.get<number>('appHostsPollingInterval', 30000);
        return Math.max(interval, 1000);
    }

    private async _startPsFollow(): Promise<void> {
        const fetchVersion = ++this._psFetchVersion;
        this._psFollowStartPending = true;
        let cliPath: string;
        try {
            cliPath = await this._terminalProvider.getAspireCliExecutablePath();
        } catch (error) {
            if (this._isCurrentPsFetch(fetchVersion)) {
                this._psFollowStartPending = false;
                const errorMessage = errorFetchingAppHosts(String(error));
                extensionLogOutputChannel.warn(errorMessage);
                this._setPsError(errorMessage);
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

        const args = this._withNoLogo(['ps', '--follow', '--format', 'json']);
        const psFollowStdout = new LimitedOutputBuffer(AppHostDataRepository._oneShotOutputBufferLimit);
        const psFollowStderr = new LimitedOutputBuffer(AppHostDataRepository._oneShotOutputBufferLimit);

        psProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
            noExtensionVariables: true,
            stdoutCallback: (data) => {
                psFollowStdout.append(data);
            },
            lineCallback: (line) => {
                if (!this._isCurrentPsFetch(fetchVersion) || line.trim().length === 0) {
                    return;
                }

                this._setPsError(undefined);
                this._handlePsOutput(line);
            },
            stderrCallback: (data) => {
                psFollowStderr.append(data);
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
                    if (this._disableNoLogoForRetry(args, psFollowStdout.value, psFollowStderr.value, 'aspire ps --follow')) {
                        this._startPsFollow();
                        return;
                    }

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

        this._psFollowStartPending = false;
        this._clearGlobalLoading();
    }

    private _fetchAppHosts(): void {
        if (this._fetchInProgress || this._disposed || !this._dataActive) {
            return;
        }
        this._fetchInProgress = true;
        const fetchVersion = ++this._psFetchVersion;

        const args = this._withNoLogo(['ps', '--format', 'json']);
        this._runPsCommand(args, (code, stdout, stderr) => {
            if (code === 0) {
                this._setPsError(undefined);
                this._handlePsOutput(stdout);
            } else {
                this._clearLoadingForCurrentView();
                this._setPsError(errorFetchingAppHosts(stderr || `exit code ${code}`));
            }
            this._fetchInProgress = false;
        }, { fetchVersion });
    }

    private _refreshAppHostsFromAuthoritativeSnapshot(force = false): void {
        if (this._disposed || (!force && !this._dataActive)) {
            return;
        }

        if (this._authoritativeSnapshotInProgress) {
            this._authoritativeSnapshotPending = true;
            this._authoritativeSnapshotPendingForce ||= force;
            return;
        }

        this._authoritativeSnapshotInProgress = true;
        const snapshotRequestId = ++this._authoritativeSnapshotRequestId;
        this._activeAuthoritativeSnapshotRequestId = snapshotRequestId;
        const isCurrentSnapshot = () => this._activeAuthoritativeSnapshotRequestId === snapshotRequestId
            && !this._disposed
            && (force || this._dataActive);
        const pollingGeneration = this._psPollingGeneration;
        const args = this._withNoLogo(['ps', '--format', 'json']);
        this._runPsCommand(args, (code, stdout, stderr) => {
            if (this._activeAuthoritativeSnapshotRequestId !== snapshotRequestId) {
                return;
            }

            if (pollingGeneration !== this._psPollingGeneration) {
                this._activeAuthoritativeSnapshotRequestId = undefined;
                this._authoritativeSnapshotInProgress = false;
                return;
            }

            if (!this._disposed && (force || this._dataActive)) {
                if (code === 0) {
                    this._setPsError(undefined);
                    this._handlePsOutput(stdout);
                } else {
                    this._clearLoadingForCurrentView();
                    this._setPsError(errorFetchingAppHosts(stderr || `exit code ${code}`));
                }
            }

            this._activeAuthoritativeSnapshotRequestId = undefined;
            this._authoritativeSnapshotInProgress = false;
            if (this._authoritativeSnapshotPending) {
                const pendingForce = this._authoritativeSnapshotPendingForce;
                this._authoritativeSnapshotPending = false;
                this._authoritativeSnapshotPendingForce = false;
                this._refreshAppHostsFromAuthoritativeSnapshot(pendingForce);
            }
        }, { force, isCurrent: isCurrentSnapshot });
    }

    private _isCurrentPsFetch(fetchVersion: number): boolean {
        return !this._disposed && this._dataActive && fetchVersion === this._psFetchVersion;
    }

    private _updateLoadingContext(): void {
        const isLoading = this._viewMode === 'workspace' ? this._loadingWorkspace : this._loadingGlobal;
        vscode.commands.executeCommand('setContext', 'aspire.loading', isLoading);
    }

    private _clearGlobalLoading(): void {
        if (this._viewMode !== 'global' || !this._loadingGlobal) {
            return;
        }

        const hasDashboardUrl = this._appHosts.some(appHost => Boolean(appHost.dashboardUrl));
        this._loadingGlobal = false;
        this._updateLoadingContext();
        vscode.commands.executeCommand('setContext', 'aspire.noAppHosts', this._appHosts.length === 0);
        vscode.commands.executeCommand('setContext', 'aspire.noRunningAppHosts', !hasDashboardUrl);
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
        this._describeErrorMessage = undefined;
        this._describeErrorIsCompatibility = false;
        this._psErrorMessage = undefined;
        this._updateErrorMessage();
    }

    private _setDescribeError(message: string | undefined, options?: { compatibility?: boolean }): void {
        const compatibility = message !== undefined && (options?.compatibility ?? false);
        if (this._describeErrorMessage !== message || this._describeErrorIsCompatibility !== compatibility) {
            this._describeErrorMessage = message;
            this._describeErrorIsCompatibility = compatibility;
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

    private _handlePsOutput(stdout: string): void {
        try {
            const parsed: AppHostDisplayInfo[] | AppHostDisplayInfo = JSON.parse(stdout);
            const appHosts = Array.isArray(parsed)
                ? parsed
                : this._applyPsDelta(parsed);

            this._handlePsSnapshot(appHosts);
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
            const discoveryPending = !this._workspaceAppHostDiscoveryComplete;
            if (appHostsChanged || workspaceAppHostChanged || force || this._loadingWorkspace) {
                this._updateWorkspaceContext({ clearLoading: !discoveryPending || workspaceAppHosts.length > 0 });
            }
        } else {
            if (this._loadingGlobal) {
                this._loadingGlobal = false;
                this._updateLoadingContext();
            }
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

    private async _runPsCommand(args: string[], callback: (code: number, stdout: string, stderr: string) => void, options?: { fetchVersion?: number; force?: boolean; isCurrent?: () => boolean }): Promise<void> {
        const fetchVersion = options?.fetchVersion;
        const force = options?.force === true;
        const isCurrentPsCommand = () => {
            if (options?.isCurrent) {
                return options.isCurrent();
            }

            if (fetchVersion !== undefined) {
                return this._isCurrentPsFetch(fetchVersion);
            }

        return !this._disposed && (force || this._dataActive);
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
                    if ((code ?? 1) !== 0) {
                        const retryArgs = this._tryGetNoLogoRetryArgs(args, stdout, stderr, 'aspire ps');
                        if (retryArgs) {
                            this._runPsCommand(retryArgs, callback, options);
                            return;
                        }
                    }

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

    private _terminateProcess(childProcess: ChildProcessWithoutNullStreams, description: string, options?: { suppressTimeoutWarning?: boolean }): void {
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
                const signalSent = this._terminateProcessTree(childProcess, false);
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

                if (!options?.suppressTimeoutWarning) {
                    extensionLogOutputChannel.warn(`${description} did not exit within ${AppHostDataRepository._processShutdownGracePeriodMs}ms; forcing termination.`);
                }
                try {
                    const signalSent = this._terminateProcessTree(childProcess, true);
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

    private _terminateProcessTree(childProcess: ChildProcessWithoutNullStreams, force: boolean): boolean {
        if (process.platform !== 'win32' || childProcess.pid === undefined) {
            return childProcess.kill(force ? 'SIGKILL' : undefined);
        }

        const args = ['/pid', String(childProcess.pid), '/t'];
        if (force) {
            args.push('/f');
        }

        const taskkill = spawnProcess('taskkill.exe', args, {
            stdio: 'ignore',
            windowsHide: true,
        });
        taskkill.on('error', error => {
            extensionLogOutputChannel.warn(`Failed to stop process tree for PID ${childProcess.pid}: ${error}`);
            childProcess.kill();
        });
        taskkill.unref();

        return true;
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

function getConfiguredNumber(config: vscode.WorkspaceConfiguration, key: string): number | undefined {
    const inspection = config.inspect<number>(key);
    return inspection?.workspaceFolderValue
        ?? inspection?.workspaceValue
        ?? inspection?.globalValue;
}

class LimitedOutputBuffer {
    private readonly _marker: string;
    private readonly _headLimit: number;
    private readonly _tailLimit: number;
    private _head = '';
    private _tail = '';
    private _truncated = false;

    constructor(private readonly _limit: number | null) {
        if (_limit === null) {
            this._marker = '';
            this._headLimit = 0;
            this._tailLimit = 0;
            return;
        }

        this._marker = getOutputTruncationMarker(_limit);
        const available = Math.max(_limit - this._marker.length, 0);
        this._headLimit = Math.ceil(available / 2);
        this._tailLimit = available - this._headLimit;
    }

    append(data: string): void {
        if (this._limit === null) {
            this._head += data;
            return;
        }

        if (!this._truncated) {
            const combined = this._head + data;
            if (combined.length <= this._limit) {
                this._head = combined;
                return;
            }

            this._head = combined.slice(0, this._headLimit);
            this._tail = takeLast(combined, this._tailLimit);
            this._truncated = true;
            return;
        }

        this._tail = takeLast(this._tail + data, this._tailLimit);
    }

    get value(): string {
        if (!this._truncated) {
            return this._head;
        }

        return `${this._head}${this._marker}${this._tail}`;
    }
}

function getOutputTruncationMarker(limit: number): string {
    const marker = `\n${aspireCommandOutputTruncated(limit)}\n`;

    return marker.length <= limit ? marker : marker.slice(0, limit);
}

function takeLast(value: string, count: number): string {
    return count === 0 ? '' : value.slice(-count);
}

export function filterResourceCommandStatusOutput(output: string, resourceName: string, commandName: string): string {
    if (!output) {
        return '';
    }

    const filteredLines = output
        .split(/\r?\n/)
        .filter(line => !isResourceCommandStatusLine(line, resourceName, commandName));

    while (filteredLines.length > 0 && filteredLines[0].trim().length === 0) {
        filteredLines.shift();
    }

    while (filteredLines.length > 0 && filteredLines[filteredLines.length - 1].trim().length === 0) {
        filteredLines.pop();
    }

    return filteredLines.join('\n');
}

function isResourceCommandStatusLine(line: string, resourceName: string, commandName: string): boolean {
    const normalized = normalizeResourceCommandStatusLine(line);

    return getResourceCommandStatusLines(resourceName, commandName).includes(normalized);
}

function getResourceCommandStatusLines(resourceName: string, commandName: string): string[] {
    // Older CLIs emitted resource command status to stdout before the command value, for example:
    //   Restarting resource 'cache'...
    //   Resource 'cache' restarted successfully.
    //   Executing command 'echo-arguments' on resource 'cache'...
    //   Command 'echo-arguments' executed successfully on resource 'cache'.
    // Keep this compatibility filter narrow so real command output is preserved.
    const lines = [
        `Validating and executing command '${commandName}' on resource '${resourceName}'...`,
        `Executing command '${commandName}' on resource '${resourceName}'...`,
        `Command '${commandName}' executed successfully on resource '${resourceName}'.`,
    ];

    const knownCommand = getKnownResourceCommandStatus(commandName);
    if (knownCommand) {
        lines.push(
            `${knownCommand.progressVerb} resource '${resourceName}'...`,
            `Resource '${resourceName}' ${knownCommand.pastTenseVerb} successfully.`);
    }

    return lines;
}

function getKnownResourceCommandStatus(commandName: string): { progressVerb: string; pastTenseVerb: string } | undefined {
    switch (commandName) {
        case 'start':
            return { progressVerb: 'Starting', pastTenseVerb: 'started' };
        case 'stop':
            return { progressVerb: 'Stopping', pastTenseVerb: 'stopped' };
        case 'restart':
            return { progressVerb: 'Restarting', pastTenseVerb: 'restarted' };
        case 'rebuild':
            return { progressVerb: 'Rebuilding', pastTenseVerb: 'rebuilt' };
        case 'set-parameter':
        case 'parameter-set':
            return { progressVerb: 'Setting parameter for', pastTenseVerb: 'set' };
        case 'delete-parameter':
        case 'parameter-delete':
            return { progressVerb: 'Deleting parameter for', pastTenseVerb: 'deleted' };
        default:
            return undefined;
    }
}

function normalizeResourceCommandStatusLine(line: string): string {
    return line
        .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
        .trim()
        .replace(/^[✅✔✓]\s*/, '');
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
    const lines = [...nonJsonLines, ...stderr.split(/\r?\n/)];
    const output = lines.join('\n');
    if (!output) {
        return false;
    }

    // The surrounding help/error text and placeholder names are localized by System.CommandLine,
    // but the command name and bracket/angle syntax are stable. Older CLIs that do not support
    // `describe` either print top-level help such as:
    //   Uso:
    //   aspire <comando> [opciones]
    // or reject stable tokens from the attempted invocation, such as `describe` or `--follow`.
    const normalizedOutput = output.toLowerCase();
    return lines.some(isAspireCommandHelpSyntaxLine)
        || containsQuotedCliToken(output, 'describe')
        || containsQuotedCliToken(output, '--follow')
        || containsQuotedCliToken(output, '--format')
        || containsQuotedCliToken(output, '--apphost')
        || (normalizedOutput.includes('usage:') && normalizedOutput.includes('commands:'))
        || normalizedOutput.includes('unknown command')
        || normalizedOutput.includes('unrecognized command')
        || normalizedOutput.includes('unrecognized option')
        || normalizedOutput.includes('is not a recognized command');
}

function isAspireCommandHelpSyntaxLine(line: string): boolean {
    return /^aspire(?:\.exe)?\s+(?:<[^>]+>|\[[^\]]+\])(?:\s|$)/i.test(normalizeResourceCommandStatusLine(line));
}

function containsQuotedCliToken(output: string, token: string): boolean {
    const escapedToken = token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(`[\\'"\`\\u2018\\u2019\\u201C\\u201D]${escapedToken}[\\'"\`\\u2018\\u2019\\u201C\\u201D]`).test(output);
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
