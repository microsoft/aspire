import * as path from 'path';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess, terminateCliProcess } from './process/cliProcess';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { aspireConfigFileName } from './cliTypes';
import { isNoLogoUnsupportedOutput, noLogoOption } from './cliCompatibility';
import { EnvironmentVariables } from './environment';
import { extensionLogOutputChannel } from './logging';
import { getAppHostDiscoveryTimeoutMs } from './settings';
import { classifyAppHostPath, summarizeAppHostLanguages } from './appHostLanguage';
import { sendTelemetryEvent } from './telemetry';
import { isExcludedDiscoveryCandidate, isExcludedDiscoveryUri } from './workspaceFileSearch';
import { ConfigInfoProvider } from './configInfoProvider';
import { lsJsonStreamCapability } from '../types/configInfo';
import { workspaceFolderCliPathTarget } from './cliPathVariables';
import { isSamePath } from './paths/comparison';
import { isSameFileSystemEntry } from './paths/fileSystemIdentity';
import { createLsStreamCandidateHandler, parseCandidateOutput, parseLegacyGetAppHostsOutput, toCandidatesFromLegacySearchResult } from './appHostCandidateParsing';
import { discoverProjectAppHostsFromWorkspaceFiles, findConfiguredAppHostPaths, formatErrorMessage } from './appHostProjectFiles';
import { findCandidateForEditorFile, findWorkspaceDefaultCandidate, getDebugTargetForCandidate, sortCandidatesByPath } from './appHostCandidateSelection';
import type { CandidateAppHostDisplayInfo, IncrementalCandidateCallback } from './appHostCandidateTypes';

export { isSamePath };
export { isSameFileSystemEntry };
export { getFileSystemEntryDescriptor } from './paths/fileSystemIdentity';
export type { FileSystemEntryDescriptor } from './paths/fileSystemIdentity';
export { findCandidateForEditorFile };
export { getDebugTargetForCandidate };
export { findConfiguredAppHostPaths };
export { getWorkspaceAppHostProjectSearchResult, isBuildableAppHostCandidate, selectWorkspaceAppHostPath } from './appHostCandidateSelection';
export { formatAppHostLanguage } from './appHostLanguage';
export type { AppHostCandidate, AppHostProjectSearchResult, CandidateAppHostDisplayInfo } from './appHostCandidateTypes';

type AppHostDiscoverySource = 'ls' | 'legacy-get-apphosts' | 'workspace-files' | 'all';

interface AppHostDiscoveryResult {
    source: Exclude<AppHostDiscoverySource, 'all'>;
    candidates: CandidateAppHostDisplayInfo[];
}

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

    forgetWorkspaceFolder(workspaceFolder: vscode.WorkspaceFolder): void {
        const key = path.resolve(workspaceFolder.uri.fsPath);
        this._cache.delete(key);
        const watchers = this._watchers.get(key);
        if (watchers) {
            watchers.forEach(watcher => watcher.dispose());
            this._watchers.delete(key);
        }
        const pendingInvalidationTimer = this._pendingInvalidationTimers.get(key);
        if (pendingInvalidationTimer) {
            clearTimeout(pendingInvalidationTimer);
            this._pendingInvalidationTimers.delete(key);
        }
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
            cliPath = await this._getAspireCliExecutablePath(workspaceFolder, cancellationToken);
            const lsJsonStreamSupported = await this._resolveLsStreamCapability(cliPath, workspaceFolder, forceRefresh);
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

    private _getAspireCliExecutablePath(workspaceFolder: vscode.WorkspaceFolder, cancellationToken: vscode.CancellationToken): Promise<string> {
        this._throwIfDisposed();
        throwIfCancellationRequested(cancellationToken);

        const target = workspaceFolderCliPathTarget(workspaceFolder);
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
                this._terminalProvider.getAspireCliExecutablePath(target).then(
                    cliPath => settle(() => resolve(cliPath)),
                    error => settle(() => reject(error instanceof Error ? error : new Error(String(error)))));
            }
            catch (error) {
                settle(() => reject(error instanceof Error ? error : new Error(String(error))));
            }
        });
    }

    private async _resolveLsStreamCapability(cliPath: string, workspaceFolder: vscode.WorkspaceFolder, forceRefresh: boolean): Promise<boolean> {
        const configInfo = await this._configInfoProvider.getConfigInfo({
            suppressErrors: true,
            forceRefresh,
            cliPath,
            target: workspaceFolderCliPathTarget(workspaceFolder),
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
            '**/apphost.rs',
            // Java requires the file name to match the public class name, so this is always
            // AppHost.java. Watcher globs are case-sensitive on Linux, so the pattern has to
            // carry the real casing rather than the lowercase form used by the other entries.
            '**/AppHost.java',
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
