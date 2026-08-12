import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess, terminateCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { aspireConfigFileName, getAppHostPathFromConfig, readJsonFile } from './cliTypes';
import { isNoLogoUnsupportedOutput, noLogoOption } from './cliCompatibility';
import { EnvironmentVariables } from './environment';
import { extensionLogOutputChannel } from './logging';
import { getAppHostDiscoveryTimeoutMs } from './settings';
import { classifyAppHostPath, projectContentsReferencesRunnableAspireAppHost, summarizeAppHostLanguages } from './appHostLanguage';
import { sendTelemetryEvent } from './telemetry';
import { appHostDiscoveryFindFilesMaxResults, getAppHostDiscoveryExcludeGlob, isExcludedDiscoveryCandidate, isExcludedDiscoveryUri } from './workspaceFileSearch';
import { ConfigInfoProvider } from './configInfoProvider';
import { lsJsonStreamCapability } from '../types/configInfo';

// Mirrors the `aspire ls --format json` candidate shape documented in
// docs/specs/cli-output-formats.md. Older CLI fallback results are adapted into
// this shape so extension code can keep using the modern discovery contract.
export interface CandidateAppHostDisplayInfo {
    path: string;
    language: string | null;
    status: string;
    selected?: boolean;
}

export interface AppHostCandidate {
    relativePath: string;
    path: string;
    language: string;
    status: string;
}

export interface AppHostProjectSearchResult {
    selected_project_file: string | null;
    all_project_file_candidates: string[];
    app_host_candidates: AppHostCandidate[];
}

interface LegacyAppHostProjectSearchResult {
    selected_project_file: string | null;
    all_project_file_candidates: string[];
}

type AppHostDiscoverySource = 'ls' | 'legacy-get-apphosts' | 'workspace-files' | 'all';

interface AppHostDiscoveryResult {
    source: Exclude<AppHostDiscoverySource, 'all'>;
    candidates: CandidateAppHostDisplayInfo[];
}

// Best-effort notification for candidates discovered before the final result is available.
// Buffered discovery does not invoke this callback; the returned promise remains authoritative.
type IncrementalCandidateCallback = (candidate: CandidateAppHostDisplayInfo) => void;

interface CachedAppHostDiscovery {
    promise: Promise<CandidateAppHostDisplayInfo[]>;
    reportedCandidates: CandidateAppHostDisplayInfo[];
    candidateProgressCallbacks: Set<IncrementalCandidateCallback>;
    cancellationSource: vscode.CancellationTokenSource;
    completed: boolean;
    started: boolean;
    stale: boolean;
}

interface CliProcessResult {
    stdout: string;
    stderr: string;
    exitCode: number | null | undefined;
}

export class AppHostDiscoveryService implements vscode.Disposable {
    private static readonly _candidateChangeDebounceMs = 250;
    private static readonly _streamingDiscoveryMaxRuntimeMs = 5 * 60 * 1000;

    private readonly _onDidChangeCandidates = new vscode.EventEmitter<vscode.WorkspaceFolder>();
    private readonly _cache = new Map<string, CachedAppHostDiscovery>();
    private readonly _activeDiscoveries = new Set<CachedAppHostDiscovery>();
    private readonly _watchers = new Map<string, vscode.Disposable[]>();
    private readonly _pendingInvalidationTimers = new Map<string, ReturnType<typeof setTimeout>>();
    private readonly _activeCliProcesses = new Set<ChildProcessWithoutNullStreams>();
    private readonly _cancelActiveCliProcesses = new Set<(error: Error) => void>();
    private readonly _configInfoProvider: ConfigInfoProvider;
    private _disposed = false;
    readonly onDidChangeCandidates = this._onDidChangeCandidates.event;

    constructor(private readonly _terminalProvider: AspireTerminalProvider, configInfoProvider?: ConfigInfoProvider) {
        this._configInfoProvider = configInfoProvider ?? new ConfigInfoProvider(_terminalProvider);
    }

    async discover(workspaceFolder: vscode.WorkspaceFolder, forceRefresh = false, cancellationToken?: vscode.CancellationToken, onIncrementalCandidate: IncrementalCandidateCallback = () => { }): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();
        throwIfCancellationRequested(cancellationToken);

        const key = path.resolve(workspaceFolder.uri.fsPath);
        if (forceRefresh) {
            // Existing callers still await the shared promise. Replace the cache entry without
            // cancelling that operation so those callers can finish on their original snapshot.
            this._cache.delete(key);
        }

        this._ensureWatchers(workspaceFolder, key);

        let cachedDiscovery = this._cache.get(key);
        if (cachedDiscovery?.stale) {
            // Keep existing subscribers on their original snapshot, but put one replacement in the
            // cache immediately so every new caller joins the same scan after stale work finishes.
            cachedDiscovery = this._createCachedDiscovery(workspaceFolder, key, false, cachedDiscovery.promise);
        }
        if (!cachedDiscovery) {
            cachedDiscovery = this._createCachedDiscovery(workspaceFolder, key, forceRefresh);
        }

        const candidateProgressCallback = (candidate: CandidateAppHostDisplayInfo): void => {
            if (!cancellationToken?.isCancellationRequested) {
                notifyCandidateProgressCallback(onIncrementalCandidate, candidate);
            }
        };
        if (!cachedDiscovery.completed) {
            cachedDiscovery.candidateProgressCallbacks.add(candidateProgressCallback);
        }
        try {
            if (!cachedDiscovery.completed) {
                for (const candidate of cachedDiscovery.reportedCandidates) {
                    candidateProgressCallback(candidate);
                }
            }

            return await withCancellation(cachedDiscovery.promise, cancellationToken);
        }
        finally {
            cachedDiscovery.candidateProgressCallbacks.delete(candidateProgressCallback);
        }
    }

    private _createCachedDiscovery(
        workspaceFolder: vscode.WorkspaceFolder,
        key: string,
        forceRefresh: boolean,
        startAfter?: Promise<CandidateAppHostDisplayInfo[]>): CachedAppHostDiscovery {
        const cancellationSource = new vscode.CancellationTokenSource();
        const candidateProgressCallbacks = new Set<IncrementalCandidateCallback>();
        const reportedCandidates: CandidateAppHostDisplayInfo[] = [];
        const cachedDiscovery: CachedAppHostDiscovery = {
            promise: Promise.resolve([]),
            reportedCandidates,
            candidateProgressCallbacks,
            cancellationSource,
            completed: false,
            started: false,
            stale: false,
        };
        const reportCandidateProgress = (candidate: CandidateAppHostDisplayInfo) => {
            if (isExcludedDiscoveryCandidate(workspaceFolder, vscode.Uri.file(candidate.path))) {
                return;
            }

            reportedCandidates.push(candidate);
            for (const callback of candidateProgressCallbacks) {
                notifyCandidateProgressCallback(callback, candidate);
            }
        };
        const startDiscovery = () => {
            cachedDiscovery.started = true;
            const startTime = Date.now();
            return this._discoverCore(workspaceFolder, reportCandidateProgress, cancellationSource.token, forceRefresh)
                .then(async discovery => {
                    let candidates = discovery.candidates;
                    try {
                        candidates = await this._includeConfiguredAppHostCandidate(workspaceFolder, candidates);
                        candidates = sortCandidatesByPath(this._filterExcludedCandidates(workspaceFolder, candidates));
                        emitAppHostDiscoveryTelemetry(discovery.source, 'success', candidates, startTime);
                    }
                    catch (error) {
                        emitAppHostDiscoveryTelemetry(discovery.source, 'error', candidates, startTime);
                        throw error;
                    }
                    return candidates;
                }, error => {
                    emitAppHostDiscoveryTelemetry('all', 'error', [], startTime);
                    throw error;
                });
        };

        // The cached discovery promise is shared across extension features. Keep caller
        // cancellation outside the cached operation so one cancelled refresh doesn't reject
        // unrelated callers that are awaiting the same workspace discovery.
        const discoveryPromise = startAfter
            ? startAfter.then(() => startDiscovery(), () => startDiscovery())
            : startDiscovery();
        cachedDiscovery.promise = discoveryPromise.catch(error => {
            if (this._cache.get(key) === cachedDiscovery) {
                this._cache.delete(key);
            }
            throw error;
        }).finally(() => {
            this._activeDiscoveries.delete(cachedDiscovery);
            cachedDiscovery.completed = true;
            cachedDiscovery.candidateProgressCallbacks.clear();
            cachedDiscovery.cancellationSource.dispose();
            if (cachedDiscovery.stale && this._cache.get(key) === cachedDiscovery) {
                this._cache.delete(key);
            }
        });
        this._activeDiscoveries.add(cachedDiscovery);
        this._cache.set(key, cachedDiscovery);

        return cachedDiscovery;
    }

    async resolveDebugTarget(filePath: string, workspaceFolder?: vscode.WorkspaceFolder): Promise<string> {
        return await this.tryResolveDebugTarget(filePath, workspaceFolder) ?? filePath;
    }

    async tryResolveDebugTarget(filePath: string, workspaceFolder?: vscode.WorkspaceFolder): Promise<string | undefined> {
        const folder = workspaceFolder ?? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(filePath));
        if (!folder) {
            return undefined;
        }

        if (isSamePath(filePath, folder.uri.fsPath)) {
            return undefined;
        }

        const candidates = await this.discover(folder);
        const candidate = findCandidateForEditorFile(filePath, candidates);
        return candidate ? getDebugTargetForCandidate(candidate) : undefined;
    }

    async tryFindWorkspaceDefaultCandidate(filePath: string, workspaceFolder?: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo | undefined> {
        const folder = workspaceFolder ?? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(filePath));
        if (!folder || !isSamePath(filePath, folder.uri.fsPath)) {
            return undefined;
        }

        const candidates = await this.discover(folder);
        return findWorkspaceDefaultCandidate(candidates);
    }

    async tryFindCandidateForEditorFile(filePath: string, workspaceFolder?: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo | undefined> {
        const folder = workspaceFolder ?? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(filePath));
        if (!folder) {
            return undefined;
        }

        const result = await this.discover(folder);
        return findCandidateForEditorFile(filePath, result);
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        for (const disposables of this._watchers.values()) {
            disposables.forEach(disposable => disposable.dispose());
        }
        this._watchers.clear();
        this._cache.clear();
        for (const discovery of this._activeDiscoveries) {
            discovery.cancellationSource.cancel();
        }
        this._activeDiscoveries.clear();
        for (const timer of this._pendingInvalidationTimers.values()) {
            clearTimeout(timer);
        }
        this._pendingInvalidationTimers.clear();
        for (const cancel of [...this._cancelActiveCliProcesses]) {
            cancel(new Error('AppHost discovery service was disposed.'));
        }
        this._cancelActiveCliProcesses.clear();
        this._activeCliProcesses.clear();
        this._onDidChangeCandidates.dispose();
    }

    private async _discoverCore(workspaceFolder: vscode.WorkspaceFolder, reportCandidateProgress: IncrementalCandidateCallback, cancellationToken: vscode.CancellationToken, forceRefresh: boolean): Promise<AppHostDiscoveryResult> {
        let cliPath: string | undefined;
        try {
            cliPath = await this._getAspireCliExecutablePath(cancellationToken);
            const lsJsonStreamSupported = await this._resolveLsStreamCapability(cliPath, forceRefresh);
            let appHosts: CandidateAppHostDisplayInfo[];
            if (lsJsonStreamSupported) {
                try {
                    appHosts = await this._discoverWithLsStream(cliPath, workspaceFolder, reportCandidateProgress, cancellationToken);
                }
                catch (streamError) {
                    this._throwIfDisposed();
                    throwIfCancellationRequested(cancellationToken);
                    extensionLogOutputChannel.warn(`aspire ls streaming discovery failed, retrying without --stream: ${formatErrorMessage(streamError)}`);

                    try {
                        appHosts = await this._discoverWithLs(cliPath, workspaceFolder, cancellationToken);
                    }
                    catch (bufferedError) {
                        this._throwIfDisposed();
                        throwIfCancellationRequested(cancellationToken);
                        throw new Error(`aspire ls streaming discovery failed: ${formatErrorMessage(streamError)}\naspire ls buffered fallback failed: ${formatErrorMessage(bufferedError)}`);
                    }
                }
            }
            else {
                appHosts = await this._discoverWithLs(cliPath, workspaceFolder, cancellationToken);
            }

            extensionLogOutputChannel.info(`Discovered ${appHosts.length} AppHost candidate(s) via aspire ls`);
            return { source: 'ls', candidates: appHosts };
        }
        catch (error) {
            this._throwIfDisposed();
            throwIfCancellationRequested(cancellationToken);
            let fallbackError: unknown;
            if (cliPath) {
                extensionLogOutputChannel.warn(`aspire ls discovery failed, falling back to aspire extension get-apphosts: ${formatErrorMessage(error)}`);
                try {
                    const appHosts = await this._discoverWithLegacyGetAppHosts(cliPath, workspaceFolder, cancellationToken);
                    extensionLogOutputChannel.info(`Discovered ${appHosts.length} AppHost candidate(s) via aspire extension get-apphosts`);
                    return { source: 'legacy-get-apphosts', candidates: appHosts };
                }
                catch (error) {
                    fallbackError = error;
                    this._throwIfDisposed();
                    throwIfCancellationRequested(cancellationToken);
                }
            }

            let fileFallbackError: unknown;
            try {
                const appHosts = await discoverProjectAppHostsFromWorkspaceFiles(workspaceFolder);
                throwIfCancellationRequested(cancellationToken);
                if (appHosts.length > 0) {
                    extensionLogOutputChannel.warn(`CLI AppHost discovery failed; using ${appHosts.length} AppHost project candidate(s) found in the workspace.`);
                    return { source: 'workspace-files', candidates: appHosts };
                }
            }
            catch (error) {
                throwIfCancellationRequested(cancellationToken);
                fileFallbackError = error;
            }

            const legacyFallbackMessage = cliPath
                ? `\naspire extension get-apphosts fallback failed: ${formatErrorMessage(fallbackError)}`
                : '';
            const fileFallbackMessage = fileFallbackError
                ? `\nworkspace file fallback failed: ${formatErrorMessage(fileFallbackError)}`
                : '';
            throw new Error(`aspire ls discovery failed: ${formatErrorMessage(error)}${legacyFallbackMessage}${fileFallbackMessage}`);
        }
    }

    private async _discoverWithLsStream(cliPath: string, workspaceFolder: vscode.WorkspaceFolder, reportCandidateProgress: IncrementalCandidateCallback, cancellationToken: vscode.CancellationToken): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();
        const args = ['ls', '--format', 'json', '--stream'];
        const streamedCandidates = await this._runStreamingCliCommand(cliPath, args, workspaceFolder.uri.fsPath, reportCandidateProgress, cancellationToken);

        return streamedCandidates;
    }

    private async _discoverWithLs(cliPath: string, workspaceFolder: vscode.WorkspaceFolder, cancellationToken: vscode.CancellationToken): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();

        const args = ['ls', '--format', 'json'];
        const result = await this._runBufferedCliCommand(cliPath, args, workspaceFolder.uri.fsPath, cancellationToken);

        return parseCandidateOutput(result.stdout);
    }

    private async _discoverWithLegacyGetAppHosts(cliPath: string, workspaceFolder: vscode.WorkspaceFolder, cancellationToken: vscode.CancellationToken): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();

        const args = ['extension', 'get-apphosts'];
        const result = await this._runBufferedCliCommand(cliPath, args, workspaceFolder.uri.fsPath, cancellationToken);
        const parsed = parseLegacyGetAppHostsOutput(result.stdout);
        return toCandidatesFromLegacySearchResult(parsed);
    }

    private _getAspireCliExecutablePath(cancellationToken: vscode.CancellationToken): Promise<string> {
        this._throwIfDisposed();
        throwIfCancellationRequested(cancellationToken);

        return new Promise<string>((resolve, reject) => {
            let settled = false;
            let cancellationDisposable: vscode.Disposable | undefined;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            const settle = (complete: () => void) => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeout) {
                    clearTimeout(timeout);
                }
                cancellationDisposable?.dispose();
                complete();
            };

            const timeoutMs = getAppHostDiscoveryTimeoutMs();
            timeout = setTimeout(() => {
                settle(() => reject(new Error(`Aspire CLI path resolution timed out after ${timeoutMs / 1000} seconds.`)));
            }, timeoutMs);
            cancellationDisposable = cancellationToken.onCancellationRequested(() => {
                settle(() => reject(new vscode.CancellationError()));
            });
            if (settled) {
                return;
            }

            try {
                this._terminalProvider.getAspireCliExecutablePath().then(
                    cliPath => settle(() => resolve(cliPath)),
                    error => settle(() => reject(error instanceof Error ? error : new Error(String(error)))));
            }
            catch (error) {
                settle(() => reject(error instanceof Error ? error : new Error(String(error))));
            }
        });
    }

    private async _resolveLsStreamCapability(cliPath: string, forceRefresh: boolean): Promise<boolean> {
        const configInfo = await this._configInfoProvider.getConfigInfo({
            suppressErrors: true,
            forceRefresh,
            cliPath,
        });
        const supported = configInfo?.capabilities?.includes(lsJsonStreamCapability) ?? false;
        extensionLogOutputChannel.info(`CLI capability '${lsJsonStreamCapability}' ${supported ? 'advertised' : 'not advertised'}; aspire ls --stream ${supported ? 'enabled' : 'disabled'}.`);
        return supported;
    }

    private _ensureWatchers(workspaceFolder: vscode.WorkspaceFolder, key: string): void {
        if (this._watchers.has(key)) {
            return;
        }

        const invalidate = (uri: vscode.Uri) => {
            if (isExcludedDiscoveryUri(workspaceFolder, uri)) {
                return;
            }

            const existingTimer = this._pendingInvalidationTimers.get(key);
            if (existingTimer) {
                clearTimeout(existingTimer);
            }

            const timer = setTimeout(() => {
                this._pendingInvalidationTimers.delete(key);
                if (this._disposed) {
                    return;
                }

                const cachedDiscovery = this._cache.get(key);
                if (cachedDiscovery?.completed === false) {
                    if (cachedDiscovery.started) {
                        // Let the current shared stream finish. Cancelling here would only turn
                        // repeated file notifications into a cancel-and-restart loop. A queued
                        // replacement has not observed workspace state yet, so it remains fresh.
                        cachedDiscovery.stale = true;
                    }
                } else {
                    this._invalidateCachedDiscovery(key);
                }
                this._onDidChangeCandidates.fire(workspaceFolder);
            }, AppHostDiscoveryService._candidateChangeDebounceMs);
            this._pendingInvalidationTimers.set(key, timer);
        };
        const patterns = [
            '**/*.csproj',
            '**/*.fsproj',
            '**/*.vbproj',
            '**/apphost.cs',
            '**/apphost.ts',
            '**/apphost.mts',
            '**/apphost.cts',
            '**/apphost.js',
            '**/apphost.mjs',
            '**/apphost.cjs',
            `**/${aspireConfigFileName}`,
            '**/.aspire/settings.json',
        ];

        const watchers = patterns.map(pattern => {
            const watcher = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(workspaceFolder, pattern));
            watcher.onDidCreate(uri => invalidate(uri));
            watcher.onDidChange(uri => invalidate(uri));
            watcher.onDidDelete(uri => invalidate(uri));
            return watcher;
        });
        this._watchers.set(key, watchers);
    }

    private _throwIfDisposed(): void {
        if (this._disposed) {
            throw new Error('AppHost discovery service has been disposed.');
        }
    }

    private _invalidateCachedDiscovery(key: string): void {
        const cachedDiscovery = this._cache.get(key);
        if (!cachedDiscovery) {
            return;
        }

        this._cache.delete(key);
        if (!cachedDiscovery.completed) {
            cachedDiscovery.cancellationSource.cancel();
        }
    }

    private async _includeConfiguredAppHostCandidate(workspaceFolder: vscode.WorkspaceFolder, candidates: CandidateAppHostDisplayInfo[]): Promise<CandidateAppHostDisplayInfo[]> {
        if (candidates.some(candidate => candidate.selected)) {
            return candidates;
        }

        const configuredPaths = await findConfiguredAppHostPaths(workspaceFolder);
        const configuredPath = configuredPaths.find(configuredPath => candidates.some(candidate => isSameFileSystemEntry(candidate.path, configuredPath)))
            ?? configuredPaths[0];
        if (!configuredPath) {
            return candidates;
        }

        const matchingCandidate = candidates.find(candidate => isSameFileSystemEntry(candidate.path, configuredPath));
        if (matchingCandidate) {
            return candidates.map(candidate => ({
                ...candidate,
                selected: isSameFileSystemEntry(candidate.path, configuredPath),
            }));
        }

        const configuredLanguage = classifyAppHostPath(configuredPath);
        return [
            ...candidates,
            {
                path: configuredPath,
                language: configuredLanguage === 'unknown' ? null : configuredLanguage,
                status: 'buildable',
                selected: true,
            },
        ];
    }

    private _filterExcludedCandidates(workspaceFolder: vscode.WorkspaceFolder, candidates: CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo[] {
        const filteredCandidates = candidates.filter(candidate => !isExcludedDiscoveryCandidate(workspaceFolder, vscode.Uri.file(candidate.path)));
        const excludedCandidateCount = candidates.length - filteredCandidates.length;
        if (excludedCandidateCount > 0) {
            extensionLogOutputChannel.info(`Filtered ${excludedCandidateCount} AppHost candidate(s) in excluded paths`);
        }

        return filteredCandidates;
    }

    private async _runBufferedCliCommand(
        cliPath: string,
        args: string[],
        workingDirectory: string,
        cancellationToken: vscode.CancellationToken): Promise<CliProcessResult> {
        const argsWithNoLogo = [...args, noLogoOption];
        let result = await this._runCliProcess(cliPath, argsWithNoLogo, workingDirectory, undefined, cancellationToken);
        if (result.exitCode !== 0 && isNoLogoUnsupportedOutput(argsWithNoLogo, result.stdout, result.stderr)) {
            extensionLogOutputChannel.info(`Installed Aspire CLI does not recognize ${noLogoOption}; retrying AppHost discovery without it.`);
            result = await this._runCliProcess(cliPath, args, workingDirectory, undefined, cancellationToken);
        }

        throwIfCliCommandFailed(result);
        return result;
    }

    private async _runStreamingCliCommand(
        cliPath: string,
        args: string[],
        workingDirectory: string,
        reportCandidateProgress: IncrementalCandidateCallback,
        cancellationToken: vscode.CancellationToken): Promise<CandidateAppHostDisplayInfo[]> {
        const argsWithNoLogo = [...args, noLogoOption];
        let candidates: CandidateAppHostDisplayInfo[] = [];
        const reportedCandidatePaths: string[] = [];
        const createCandidateHandler = () => createLsStreamCandidateHandler(candidate => {
            candidates.push(candidate);
            if (!reportedCandidatePaths.some(reportedPath => isSamePath(reportedPath, candidate.path))) {
                reportedCandidatePaths.push(candidate.path);
                notifyCandidateProgressCallback(reportCandidateProgress, candidate);
            }
        });
        let result = await this._runCliProcess(
            cliPath,
            argsWithNoLogo,
            workingDirectory,
            createCandidateHandler(),
            cancellationToken);

        if (result.exitCode !== 0 && isNoLogoUnsupportedOutput(argsWithNoLogo, result.stdout, result.stderr)) {
            extensionLogOutputChannel.info(`Installed Aspire CLI does not recognize ${noLogoOption}; retrying AppHost discovery without it.`);
            candidates = [];
            result = await this._runCliProcess(
                cliPath,
                args,
                workingDirectory,
                createCandidateHandler(),
                cancellationToken);
        }

        // Streaming stdout is NDJSON containing absolute AppHost paths. It is useful for parsing,
        // but should not become a user-visible error when the process exits without stderr.
        throwIfCliCommandFailed(result, false);
        return candidates;
    }

    private _runCliProcess(
        cliPath: string,
        args: string[],
        workingDirectory: string,
        onLine: ((line: string) => void) | undefined,
        cancellationToken: vscode.CancellationToken): Promise<CliProcessResult> {
        return new Promise<CliProcessResult>((resolve, reject) => {
            this._throwIfDisposed();
            throwIfCancellationRequested(cancellationToken);

            const cliArgs = process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true'
                ? [...args, '--cli-wait-for-debugger']
                : args;

            let stdout = '';
            let stderr = '';
            let settled = false;
            let cancellationDisposable: vscode.Disposable | undefined;
            let childProcess: ChildProcessWithoutNullStreams | undefined;
            let inactivityTimeout: ReturnType<typeof setTimeout> | undefined;
            let overallTimeout: ReturnType<typeof setTimeout> | undefined;
            const cancel = (error: Error) => {
                settle(() => reject(error));
                if (childProcess) {
                    terminateCliProcess(childProcess, `AppHost discovery command: aspire ${cliArgs.join(' ')}`);
                }
            };
            const cleanup = () => {
                if (inactivityTimeout) {
                    clearTimeout(inactivityTimeout);
                    inactivityTimeout = undefined;
                }
                if (overallTimeout) {
                    clearTimeout(overallTimeout);
                    overallTimeout = undefined;
                }
                if (childProcess) {
                    this._activeCliProcesses.delete(childProcess);
                }
                cancellationDisposable?.dispose();
                this._cancelActiveCliProcesses.delete(cancel);
            };
            const settle = (complete: () => void) => {
                if (settled) {
                    return;
                }

                settled = true;
                cleanup();
                complete();
            };

            const timeoutMs = getAppHostDiscoveryTimeoutMs();
            const startTimeout = () => {
                if (settled) {
                    return;
                }

                if (inactivityTimeout) {
                    clearTimeout(inactivityTimeout);
                }
                inactivityTimeout = setTimeout(() => {
                    const silence = onLine ? ' without output' : '';
                    cancel(new Error(`aspire ${cliArgs.join(' ')} timed out after ${timeoutMs / 1000} seconds${silence}.`));
                }, timeoutMs);
            };
            const onActivity = onLine ? startTimeout : undefined;
            this._cancelActiveCliProcesses.add(cancel);
            cancellationDisposable = cancellationToken.onCancellationRequested(() => {
                cancel(new vscode.CancellationError());
            });
            try {
                childProcess = spawnCliProcess(this._terminalProvider, cliPath, cliArgs, {
                    createProcessGroup: true,
                    noExtensionVariables: true,
                    workingDirectory,
                    stdoutCallback: data => {
                        onActivity?.();
                        stdout += data;
                    },
                    lineCallback: onLine
                        ? line => {
                            // readline can deliver lines that were already queued when a parser failure
                            // settled this process. Do not publish those lines while fallback is running.
                            if (settled) {
                                return;
                            }

                            try {
                                onActivity?.();
                                onLine(line);
                            }
                            catch (error) {
                                cancel(error instanceof Error ? error : new Error(String(error)));
                            }
                        }
                        : undefined,
                    stderrCallback: data => {
                        onActivity?.();
                        stderr += data;
                    },
                    exitCallback: code => {
                        settle(() => {
                            resolve({ stdout, stderr, exitCode: code });
                        });
                    },
                    errorCallback: error => {
                        settle(() => reject(error));
                    },
                });
            }
            catch (error) {
                settle(() => reject(error instanceof Error ? error : new Error(String(error))));
                return;
            }

            if (settled) {
                return;
            }

            this._activeCliProcesses.add(childProcess);
            startTimeout();
            if (onLine) {
                // Stream activity re-arms the inactivity watchdog, but it must not let a chatty
                // hung process keep workspace discovery alive forever.
                overallTimeout = setTimeout(() => {
                    cancel(new Error(`aspire ${cliArgs.join(' ')} exceeded the maximum streaming runtime of ${AppHostDiscoveryService._streamingDiscoveryMaxRuntimeMs / 1000} seconds.`));
                }, AppHostDiscoveryService._streamingDiscoveryMaxRuntimeMs);
            }
        });
    }
}

function emitAppHostDiscoveryTelemetry(
    source: AppHostDiscoverySource,
    outcome: 'success' | 'error',
    candidates: readonly CandidateAppHostDisplayInfo[],
    startTime: number,
): void {
    sendTelemetryEvent('aspire/vscode/apphost/discovery/result', {
        outcome,
        source,
        apphost_languages: summarizeAppHostLanguages(candidates),
    }, {
        duration_ms: Date.now() - startTime,
        candidate_count: candidates.length,
        buildable_candidate_count: candidates.filter(candidate => candidate.status === 'buildable').length,
    });
}

function createLsStreamCandidateHandler(onCandidate: IncrementalCandidateCallback): (line: string) => void {
    return line => {
        const trimmed = line.trim();
        if (!trimmed) {
            return;
        }

        let parsed: unknown;
        try {
            // `aspire ls --format json --stream` emits newline-delimited JSON, one candidate per line:
            //   {"path":"/repo/AppHost/AppHost.csproj","language":"csharp","status":"buildable"}
            // Treat malformed lines as a failed stream instead of accepting a truncated partial result.
            parsed = JSON.parse(trimmed);
        }
        catch {
            throw new Error('aspire ls --stream returned malformed JSON.');
        }

        if (!isLsCandidate(parsed)) {
            throw new Error('aspire ls --stream returned a candidate with an unexpected shape.');
        }

        onCandidate(toDisplayCandidate(parsed));
    };
}

function notifyCandidateProgressCallback(callback: IncrementalCandidateCallback, candidate: CandidateAppHostDisplayInfo): void {
    try {
        callback(candidate);
    }
    catch (error) {
        extensionLogOutputChannel.warn(`AppHost discovery candidate callback failed: ${formatErrorMessage(error)}`);
    }
}

function throwIfCliCommandFailed(result: CliProcessResult, includeStdout = true): void {
    if (result.exitCode !== 0) {
        throw new Error(result.stderr.trim() || (includeStdout ? result.stdout.trim() : '') || `exit code ${result.exitCode ?? 1}`);
    }
}

export function findCandidateForEditorFile(filePath: string, candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo | undefined {
    const matchingCandidate = candidates.find(candidate => isSamePath(candidate.path, filePath));
    if (matchingCandidate) {
        return matchingCandidate;
    }

    if (path.extname(filePath).toLowerCase() !== '.cs') {
        return undefined;
    }

    // IMPORTANT: `aspire ls` is still the source of truth for what is a valid AppHost.
    // This block does not discover AppHosts by reading C# source files or by deciding
    // that a project "looks like" an AppHost. It only handles the editor affordance gap
    // in the current CLI shape:
    //
    //   aspire ls --format json
    //   [
    //     { "path": "/repo/AppHost/AppHost.csproj", "language": "csharp", "status": "buildable" }
    //   ]
    //
    // For SDK-style .NET AppHosts the launch target is the `.csproj`, but users usually
    // have `Program.cs` or another C# source file open when they invoke Run/Debug from
    // the editor or debug picker. Until the CLI returns source identity/project membership
    // in the candidate payload, treat C# files under a candidate `.csproj` directory as
    // editor aliases for that candidate. Pick the deepest candidate directory so nested
    // AppHost candidates prefer their own project over an outer candidate. Keep this
    // heuristic bounded to C# project candidates from `aspire ls` and remove it when the
    // CLI can report the canonical source file or owning project for each candidate.
    const projectCandidate = candidates
        .filter(candidate => isCSharpProjectCandidate(candidate) && isCSharpSourceFileForProjectCandidate(filePath, candidate.path))
        .sort((left, right) => path.dirname(right.path).length - path.dirname(left.path).length)[0];
    return projectCandidate;
}

function findWorkspaceDefaultCandidate(candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo | undefined {
    return findSingleSelectedBuildableCandidate(candidates) ?? findOnlyBuildableCandidate(candidates);
}

export function getDebugTargetForCandidate(candidate: CandidateAppHostDisplayInfo): string {
    return candidate.path;
}

export function getWorkspaceAppHostProjectSearchResult(workspaceFolder: vscode.WorkspaceFolder, candidates: readonly CandidateAppHostDisplayInfo[]): AppHostProjectSearchResult {
    const appHostCandidates = candidates.map(candidate => toAppHostCandidate(workspaceFolder, candidate));
    const selectedAppHostPath = (findSingleSelectedBuildableCandidate(candidates) ?? findOnlyCandidateIfBuildable(candidates))?.path ?? null;
    const effectiveAppHostCandidates = selectedAppHostPath && !appHostCandidates.some(candidate => isSamePath(candidate.path, selectedAppHostPath))
        ? [...appHostCandidates, toConfiguredAppHostCandidate(workspaceFolder, selectedAppHostPath)]
        : appHostCandidates;
    const buildableCandidates = effectiveAppHostCandidates.filter(isBuildableAppHostCandidate);

    return {
        selected_project_file: selectedAppHostPath && buildableCandidates.some(candidate => isSamePath(candidate.path, selectedAppHostPath))
            ? selectedAppHostPath
            : null,
        all_project_file_candidates: buildableCandidates.map(candidate => candidate.path),
        app_host_candidates: effectiveAppHostCandidates,
    };
}

export function isBuildableAppHostCandidate(candidate: AppHostCandidate): boolean {
    return candidate.status === 'buildable';
}

export function formatAppHostLanguage(language: string): string | undefined {
    if (!language) {
        return undefined;
    }

    switch (language.toLowerCase()) {
        case 'csharp':
            return 'C#';
        case 'typescript':
        case 'typescript/nodejs':
            return 'TypeScript';
        default:
            return language.charAt(0).toUpperCase() + language.slice(1);
    }
}

export async function selectWorkspaceAppHostPath(workspaceFolder: vscode.WorkspaceFolder, candidates: readonly CandidateAppHostDisplayInfo[]): Promise<string | undefined> {
    const selectedCandidate = findSingleSelectedBuildableCandidate(candidates);
    if (selectedCandidate) {
        return selectedCandidate.path;
    }

    const configuredPaths = await findConfiguredAppHostPaths(workspaceFolder);
    for (const configuredPath of configuredPaths) {
        const candidate = candidates.find(candidate => isBuildableCandidate(candidate) && isSamePath(candidate.path, configuredPath));
        if (candidate) {
            return candidate.path;
        }
    }

    return findOnlyCandidateIfBuildable(candidates)?.path;
}

export async function findConfiguredAppHostPaths(workspaceFolder: vscode.WorkspaceFolder, cancellationToken?: vscode.CancellationToken): Promise<string[]> {
    let newConfigFiles: vscode.Uri[];
    let legacySettingsFiles: vscode.Uri[];
    try {
        const excludePattern = getAppHostDiscoveryExcludeGlob();
        [newConfigFiles, legacySettingsFiles] = await Promise.all([
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, `**/${aspireConfigFileName}`), excludePattern, appHostDiscoveryFindFilesMaxResults, cancellationToken),
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/.aspire/settings.json'), excludePattern, appHostDiscoveryFindFilesMaxResults, cancellationToken),
        ]);
    }
    catch (error) {
        extensionLogOutputChannel.warn(`Failed to find AppHost configuration files: ${formatErrorMessage(error)}`);
        return [];
    }

    const newConfigDirs = new Set(newConfigFiles.map(uri => path.dirname(uri.fsPath)));
    const filteredLegacyFiles = legacySettingsFiles.filter(legacyUri => {
        const projectRoot = path.dirname(path.dirname(legacyUri.fsPath));
        return !newConfigDirs.has(projectRoot);
    });

    const configuredPaths: string[] = [];
    for (const uri of [...newConfigFiles, ...filteredLegacyFiles]) {
        try {
            const json = await readJsonFile(uri);
            const appHostPath = getAppHostPathFromConfig(json);
            if (appHostPath) {
                configuredPaths.push(path.isAbsolute(appHostPath) ? appHostPath : path.join(path.dirname(uri.fsPath), appHostPath));
            }
        }
        catch {
        }
    }

    return configuredPaths;
}

function toAppHostCandidate(workspaceFolder: vscode.WorkspaceFolder, candidate: CandidateAppHostDisplayInfo): AppHostCandidate {
    return {
        relativePath: path.relative(workspaceFolder.uri.fsPath, candidate.path),
        path: candidate.path,
        language: candidate.language ?? '',
        status: candidate.status,
    };
}

function toConfiguredAppHostCandidate(workspaceFolder: vscode.WorkspaceFolder, appHostPath: string): AppHostCandidate {
    return {
        relativePath: path.relative(workspaceFolder.uri.fsPath, appHostPath),
        path: appHostPath,
        language: '',
        status: 'buildable',
    };
}

function sortCandidatesByPath(candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo[] {
    return [...candidates].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}

function parseCandidateOutput(output: string): CandidateAppHostDisplayInfo[] {
    const trimmed = output.trim();
    if (!trimmed) {
        return [];
    }

    const parsed = JSON.parse(trimmed);
    if (Array.isArray(parsed)) {
        const appHosts = parsed
            .filter(isLsCandidate)
            .map(candidate => toDisplayCandidate(candidate));

        const unexpectedCandidateCount = parsed.length - appHosts.length;
        if (unexpectedCandidateCount > 0) {
            extensionLogOutputChannel.warn(`AppHost discovery returned ${unexpectedCandidateCount} candidate(s) with an unexpected shape; ignoring those entries.`);
        }

        return appHosts;
    }

    if (isAppHostProjectSearchResult(parsed)) {
        return parsed.app_host_candidates.map(candidate => ({
            ...toDisplayCandidate(candidate),
            selected: typeof parsed.selected_project_file === 'string' && isSamePath(parsed.selected_project_file, candidate.path),
        }));
    }

    if (isLegacyAppHostProjectSearchResult(parsed)) {
        return toCandidatesFromLegacySearchResult(parsed);
    }

    throw new Error('AppHost discovery returned an unexpected output shape.');
}

async function discoverProjectAppHostsFromWorkspaceFiles(workspaceFolder: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo[]> {
    // This is the final fallback after both CLI discovery paths fail. Do not cap the
    // project scan here: VS Code returns only the first maxResults matches, which can
    // hide the only AppHost in a large workspace.
    const projectUris = (await Promise.all([
        vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/*.csproj'), getAppHostDiscoveryExcludeGlob()),
        vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/*.fsproj'), getAppHostDiscoveryExcludeGlob()),
        vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/*.vbproj'), getAppHostDiscoveryExcludeGlob()),
    ])).flat();
    const candidates: CandidateAppHostDisplayInfo[] = [];
    for (const uri of projectUris.sort((left, right) => left.fsPath.localeCompare(right.fsPath))) {
        let projectContents: string;
        try {
            projectContents = Buffer.from(await vscode.workspace.fs.readFile(uri)).toString('utf8');
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to read possible AppHost project ${uri.fsPath}: ${formatErrorMessage(error)}`);
            continue;
        }

        if (isAppHostProject(projectContents)) {
            candidates.push({
                path: uri.fsPath,
                language: getProjectLanguage(uri.fsPath),
                status: 'buildable',
            });
        }
    }

    return candidates;
}

function isAppHostProject(projectContents: string): boolean {
    return projectContentsReferencesRunnableAspireAppHost(projectContents);
}

function getProjectLanguage(projectPath: string): string {
    return path.extname(projectPath).toLowerCase() === '.fsproj'
        ? 'fsharp'
        : path.extname(projectPath).toLowerCase() === '.vbproj'
            ? 'visualbasic'
            : 'csharp';
}

function parseLegacyGetAppHostsOutput(output: string): LegacyAppHostProjectSearchResult {
    // `aspire extension get-apphosts` prints a single JSON object:
    //   {"selected_project_file":"/repo/AppHost/AppHost.csproj","all_project_file_candidates":["/repo/AppHost/AppHost.csproj"]}
    // Older builds can include log lines, so scan for the first line with the expected shape.
    for (const line of output.split(/\r?\n/)) {
        try {
            const parsed = JSON.parse(line);
            if (isLegacyAppHostProjectSearchResult(parsed)) {
                return parsed;
            }
        }
        catch {
        }
    }

    const parsed = JSON.parse(output.trim());
    if (isLegacyAppHostProjectSearchResult(parsed)) {
        return parsed;
    }

    throw new Error('aspire extension get-apphosts returned an unexpected output shape.');
}

function isLsCandidate(obj: unknown): obj is CandidateAppHostDisplayInfo {
    return !!obj
        && typeof obj === 'object'
        && typeof (obj as CandidateAppHostDisplayInfo).path === 'string'
        && typeof (obj as CandidateAppHostDisplayInfo).language === 'string'
        && typeof (obj as CandidateAppHostDisplayInfo).status === 'string';
}

function toDisplayCandidate(candidate: CandidateAppHostDisplayInfo | AppHostCandidate): CandidateAppHostDisplayInfo {
    const displayCandidate: CandidateAppHostDisplayInfo = {
        path: candidate.path,
        language: candidate.language,
        status: candidate.status,
    };

    const selected = 'selected' in candidate ? candidate.selected : undefined;
    if (selected !== undefined) {
        displayCandidate.selected = selected;
    }

    return displayCandidate;
}

function formatErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

function throwIfCancellationRequested(cancellationToken?: vscode.CancellationToken): void {
    if (cancellationToken?.isCancellationRequested) {
        throw new vscode.CancellationError();
    }
}

function withCancellation<T>(promise: Promise<T>, cancellationToken?: vscode.CancellationToken): Promise<T> {
    if (!cancellationToken) {
        return promise;
    }

    try {
        throwIfCancellationRequested(cancellationToken);
    }
    catch (error) {
        return Promise.reject(error);
    }

    return new Promise<T>((resolve, reject) => {
        const disposable = cancellationToken.onCancellationRequested(() => {
            disposable.dispose();
            reject(new vscode.CancellationError());
        });

        promise.then(
            value => {
                disposable.dispose();
                resolve(value);
            },
            error => {
                disposable.dispose();
                reject(error);
            });
    });
}

function isLegacyAppHostProjectSearchResult(obj: unknown): obj is LegacyAppHostProjectSearchResult {
    return !!obj
        && typeof obj === 'object'
        && (typeof (obj as LegacyAppHostProjectSearchResult).selected_project_file === 'string' || (obj as LegacyAppHostProjectSearchResult).selected_project_file === null)
        && Array.isArray((obj as LegacyAppHostProjectSearchResult).all_project_file_candidates);
}

function isAppHostProjectSearchResult(obj: unknown): obj is AppHostProjectSearchResult {
    return !!obj
        && typeof obj === 'object'
        && (typeof (obj as AppHostProjectSearchResult).selected_project_file === 'string' || (obj as AppHostProjectSearchResult).selected_project_file === null)
        && Array.isArray((obj as AppHostProjectSearchResult).app_host_candidates)
        && (obj as AppHostProjectSearchResult).app_host_candidates.every(candidate =>
            candidate
            && typeof candidate.relativePath === 'string'
            && typeof candidate.path === 'string'
            && typeof candidate.language === 'string'
            && typeof candidate.status === 'string');
}

function toCandidatesFromLegacySearchResult(parsed: LegacyAppHostProjectSearchResult): CandidateAppHostDisplayInfo[] {
    return parsed.all_project_file_candidates.filter(candidate => typeof candidate === 'string').map(candidatePath => ({
        path: candidatePath,
        language: 'csharp',
        status: 'buildable',
        selected: typeof parsed.selected_project_file === 'string' && isSamePath(parsed.selected_project_file, candidatePath),
    }));
}

function isCSharpProjectCandidate(candidate: CandidateAppHostDisplayInfo): boolean {
    // Only `.csproj` candidates can own nearby C# source files for the editor alias
    // heuristic above. Modern `aspire ls` candidates include the CLI language id
    // (`language: "csharp"`). Legacy `aspire extension get-apphosts` fallback
    // candidates are adapted to that modern C# shape before reaching here. That
    // preserves old CLI support while keeping the compatibility gap local to
    // candidate adaptation/matching.
    return path.extname(candidate.path).toLowerCase() === '.csproj'
        && candidate.language?.toLowerCase() === 'csharp';
}

function isBuildableCandidate(candidate: CandidateAppHostDisplayInfo): boolean {
    return candidate.status === 'buildable';
}

function findSingleSelectedBuildableCandidate(candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo | undefined {
    const selectedCandidates = candidates.filter(candidate => candidate.selected && isBuildableCandidate(candidate));
    return selectedCandidates.length === 1 ? selectedCandidates[0] : undefined;
}

function findOnlyBuildableCandidate(candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo | undefined {
    const buildableCandidates = candidates.filter(isBuildableCandidate);
    return buildableCandidates.length === 1 ? buildableCandidates[0] : undefined;
}

function findOnlyCandidateIfBuildable(candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo | undefined {
    return candidates.length === 1 && isBuildableCandidate(candidates[0]) ? candidates[0] : undefined;
}

function isCSharpSourceFileForProjectCandidate(filePath: string, projectPath: string): boolean {
    const projectDirectory = path.dirname(path.resolve(projectPath));
    const sourcePath = path.resolve(filePath);
    const comparison = process.platform === 'win32' || process.platform === 'darwin'
        ? 'case-insensitive'
        : 'case-sensitive';
    const normalizedProjectDirectory = comparison === 'case-insensitive' ? projectDirectory.toLowerCase() : projectDirectory;
    const normalizedSourcePath = comparison === 'case-insensitive' ? sourcePath.toLowerCase() : sourcePath;
    const relativePath = path.relative(normalizedProjectDirectory, normalizedSourcePath);
    return relativePath !== ''
        && !relativePath.startsWith('..')
        && !path.isAbsolute(relativePath)
        && !relativePath.split(path.sep).some(segment => segment.toLowerCase() === 'bin' || segment.toLowerCase() === 'obj');
}

type FileSystemEntryIdentity = Pick<fs.BigIntStats, 'dev' | 'ino'>;
type FileSystemEntryIdentityProvider = (filePath: string) => FileSystemEntryIdentity | undefined;

export function isSameFileSystemEntry(
    left: string,
    right: string,
    getIdentity: FileSystemEntryIdentityProvider = tryGetFileSystemEntryIdentity): boolean {
    const resolvedLeft = path.resolve(left);
    const resolvedRight = path.resolve(right);
    if (resolvedLeft === resolvedRight) {
        return true;
    }

    const leftIdentity = getIdentity(resolvedLeft);
    const rightIdentity = getIdentity(resolvedRight);
    if (leftIdentity && rightIdentity && leftIdentity.ino !== 0n && rightIdentity.ino !== 0n) {
        // Stable native identities are authoritative: case-sensitive Windows directories can
        // contain distinct entries such as Foo and foo even though textual comparison ignores case.
        return leftIdentity.dev === rightIdentity.dev && leftIdentity.ino === rightIdentity.ino;
    }

    // Missing or unstable identities cannot distinguish entries, so retain the platform fallback.
    return isSamePath(resolvedLeft, resolvedRight);
}

function tryGetFileSystemEntryIdentity(filePath: string): FileSystemEntryIdentity | undefined {
    try {
        return fs.statSync(filePath, { bigint: true });
    }
    catch {
        return undefined;
    }
}

export function isSamePath(left: string, right: string): boolean {
    const comparison = process.platform === 'win32'
        ? 'case-insensitive'
        : 'case-sensitive';
    const resolvedLeft = path.resolve(left);
    const resolvedRight = path.resolve(right);
    return comparison === 'case-insensitive'
        ? resolvedLeft.toLowerCase() === resolvedRight.toLowerCase()
        : resolvedLeft === resolvedRight;
}
