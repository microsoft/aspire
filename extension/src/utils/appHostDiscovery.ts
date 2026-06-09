import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { aspireConfigFileName, getAppHostPathFromConfig, readJsonFile } from './cliTypes';
import { EnvironmentVariables } from './environment';
import { extensionLogOutputChannel } from './logging';
import { getAppHostDiscoveryTimeoutMs } from './settings';
import { appHostDiscoveryFindFilesMaxResults, getAppHostDiscoveryExcludeGlob, isExcludedDiscoveryUri } from './workspaceFileSearch';

// Mirrors the `aspire ls --format json` candidate shape documented in
// docs/specs/cli-output-formats.md. Older CLI fallback results are adapted into
// this shape so extension code can keep using the modern discovery contract.
export interface CandidateAppHostDisplayInfo {
    path: string;
    language: string | null;
    status: string | null;
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

export class AppHostDiscoveryService implements vscode.Disposable {
    private static readonly _candidateChangeDebounceMs = 250;

    private readonly _onDidChangeCandidates = new vscode.EventEmitter<vscode.WorkspaceFolder>();
    private readonly _cache = new Map<string, Promise<CandidateAppHostDisplayInfo[]>>();
    private readonly _watchers = new Map<string, vscode.Disposable[]>();
    private readonly _pendingInvalidationTimers = new Map<string, ReturnType<typeof setTimeout>>();
    private readonly _activeCliProcesses = new Set<ChildProcessWithoutNullStreams>();
    private readonly _cancelActiveCliProcesses = new Set<(error: Error) => void>();
    private _disposed = false;
    readonly onDidChangeCandidates = this._onDidChangeCandidates.event;

    constructor(private readonly _terminalProvider: AspireTerminalProvider) {
    }

    async discover(workspaceFolder: vscode.WorkspaceFolder, forceRefresh = false, cancellationToken?: vscode.CancellationToken): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();
        throwIfCancellationRequested(cancellationToken);

        const key = path.resolve(workspaceFolder.uri.fsPath);
        if (forceRefresh) {
            this._cache.delete(key);
        }

        this._ensureWatchers(workspaceFolder, key);

        let resultPromise = this._cache.get(key);
        if (!resultPromise) {
            // The cached discovery promise is shared across extension features. Keep caller
            // cancellation outside the cached operation so one cancelled refresh doesn't reject
            // unrelated callers that are awaiting the same workspace discovery.
            const discoveryPromise = this._discoverCore(workspaceFolder)
                .then(candidates => this._includeConfiguredAppHostCandidate(workspaceFolder, candidates));
            let cachedPromise: Promise<CandidateAppHostDisplayInfo[]>;
            cachedPromise = discoveryPromise.catch(error => {
                if (this._cache.get(key) === cachedPromise) {
                    this._cache.delete(key);
                }
                throw error;
            });
            resultPromise = cachedPromise;
            this._cache.set(key, resultPromise);
        }

        return await withCancellation(resultPromise, cancellationToken);
    }

    async resolveDebugTarget(filePath: string, workspaceFolder?: vscode.WorkspaceFolder): Promise<string> {
        return await this.tryResolveDebugTarget(filePath, workspaceFolder) ?? filePath;
    }

    async tryResolveDebugTarget(filePath: string, workspaceFolder?: vscode.WorkspaceFolder): Promise<string | undefined> {
        const candidate = await this.tryFindCandidateForEditorFile(filePath, workspaceFolder);
        return candidate ? getDebugTargetForCandidate(candidate) : undefined;
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

    private async _discoverCore(workspaceFolder: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo[]> {
        try {
            const appHosts = await this._discoverWithLs(workspaceFolder);
            extensionLogOutputChannel.info(`Discovered ${appHosts.length} AppHost candidate(s) via aspire ls`);
            return appHosts;
        }
        catch (error) {
            this._throwIfDisposed();
            extensionLogOutputChannel.warn(`aspire ls discovery failed, falling back to aspire extension get-apphosts: ${formatErrorMessage(error)}`);
            try {
                const appHosts = await this._discoverWithLegacyGetAppHosts(workspaceFolder);
                extensionLogOutputChannel.info(`Discovered ${appHosts.length} AppHost candidate(s) via aspire extension get-apphosts`);
                return appHosts;
            }
            catch (fallbackError) {
                this._throwIfDisposed();
                let fileFallbackError: unknown;
                try {
                    const appHosts = await discoverCSharpAppHostProjectsFromWorkspaceFiles(workspaceFolder);
                    if (appHosts.length > 0) {
                        extensionLogOutputChannel.warn(`CLI AppHost discovery failed; using ${appHosts.length} C# AppHost project candidate(s) found in the workspace.`);
                        return appHosts;
                    }
                }
                catch (error) {
                    fileFallbackError = error;
                }

                const fileFallbackMessage = fileFallbackError
                    ? `\nworkspace file fallback failed: ${formatErrorMessage(fileFallbackError)}`
                    : '';
                throw new Error(`aspire ls discovery failed: ${formatErrorMessage(error)}\naspire extension get-apphosts fallback failed: ${formatErrorMessage(fallbackError)}${fileFallbackMessage}`);
            }
        }
    }

    private async _discoverWithLs(workspaceFolder: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();

        const cliPath = await this._terminalProvider.getAspireCliExecutablePath();
        const args = ['ls', '--format', 'json'];
        if (process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true') {
            args.push('--cli-wait-for-debugger');
        }

        const output = await this._runCliForStdout(cliPath, args, workspaceFolder.uri.fsPath);
        return parseCandidateOutput(output, 'aspire ls');
    }

    private async _discoverWithLegacyGetAppHosts(workspaceFolder: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();

        const cliPath = await this._terminalProvider.getAspireCliExecutablePath();
        const args = ['extension', 'get-apphosts'];
        if (process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true') {
            args.push('--cli-wait-for-debugger');
        }

        const output = await this._runCliForStdout(cliPath, args, workspaceFolder.uri.fsPath);
        const parsed = parseLegacyGetAppHostsOutput(output);
        return toCandidatesFromLegacySearchResult(parsed);
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

            this._cache.delete(key);

            const timer = setTimeout(() => {
                this._pendingInvalidationTimers.delete(key);
                if (this._disposed) {
                    return;
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

    private async _includeConfiguredAppHostCandidate(workspaceFolder: vscode.WorkspaceFolder, candidates: CandidateAppHostDisplayInfo[]): Promise<CandidateAppHostDisplayInfo[]> {
        if (candidates.some(candidate => candidate.selected)) {
            return candidates;
        }

        const configuredPaths = await findConfiguredAppHostPaths(workspaceFolder);
        const configuredPath = configuredPaths.find(configuredPath => candidates.some(candidate => isSamePath(candidate.path, configuredPath)))
            ?? configuredPaths[0];
        if (!configuredPath) {
            return candidates;
        }

        const matchingCandidate = candidates.find(candidate => isSamePath(candidate.path, configuredPath));
        if (matchingCandidate) {
            return candidates.map(candidate => ({
                ...candidate,
                selected: isSamePath(candidate.path, configuredPath),
            }));
        }

        return [
            ...candidates,
            {
                path: configuredPath,
                language: null,
                status: 'buildable',
                selected: true,
            },
        ];
    }

    private _runCliForStdout(cliPath: string, args: string[], workingDirectory: string): Promise<string> {
        return new Promise((resolve, reject) => {
            this._throwIfDisposed();

            let stdout = '';
            let stderr = '';
            let settled = false;
            let childProcess: ChildProcessWithoutNullStreams | undefined;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            const cancel = (error: Error) => {
                if (childProcess && !childProcess.killed) {
                    try {
                        if (!childProcess.kill()) {
                            extensionLogOutputChannel.warn(`Failed to stop AppHost discovery command: aspire ${args.join(' ')}`);
                        }
                    }
                    catch (killError) {
                        extensionLogOutputChannel.warn(`Failed to stop AppHost discovery command: ${killError}`);
                    }
                }

                settle(() => reject(error));
            };
            const cleanup = () => {
                if (timeout) {
                    clearTimeout(timeout);
                    timeout = undefined;
                }
                if (childProcess) {
                    this._activeCliProcesses.delete(childProcess);
                }
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

            this._cancelActiveCliProcesses.add(cancel);
            try {
                childProcess = spawnCliProcess(this._terminalProvider, cliPath, args, {
                    noExtensionVariables: true,
                    workingDirectory,
                    stdoutCallback: data => { stdout += data; },
                    stderrCallback: data => { stderr += data; },
                    exitCallback: code => {
                        settle(() => {
                            if (code === 0) {
                                resolve(stdout);
                            }
                            else {
                                reject(new Error(stderr || `exit code ${code ?? 1}`));
                            }
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
            const timeoutMs = getAppHostDiscoveryTimeoutMs();
            timeout = setTimeout(() => {
                cancel(new Error(`aspire ${args.join(' ')} timed out after ${timeoutMs / 1000} seconds.`));
            }, timeoutMs);
        });
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

export function getDebugTargetForCandidate(candidate: CandidateAppHostDisplayInfo): string {
    return candidate.path;
}

export function getWorkspaceAppHostProjectSearchResult(workspaceFolder: vscode.WorkspaceFolder, candidates: readonly CandidateAppHostDisplayInfo[]): AppHostProjectSearchResult {
    const appHostCandidates = candidates.map(candidate => toAppHostCandidate(workspaceFolder, candidate));
    const selectedAppHostPath = candidates.find(candidate => candidate.selected)?.path
        ?? (candidates.length === 1 ? candidates[0].path : null);
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
    const selectedCandidate = candidates.find(candidate => candidate.selected);
    if (selectedCandidate) {
        return selectedCandidate.path;
    }

    const configuredPaths = await findConfiguredAppHostPaths(workspaceFolder);
    for (const configuredPath of configuredPaths) {
        const candidate = candidates.find(candidate => isSamePath(candidate.path, configuredPath));
        if (candidate) {
            return candidate.path;
        }
    }

    return candidates.length === 1 ? candidates[0].path : undefined;
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
        status: candidate.status ?? 'buildable',
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

function parseCandidateOutput(output: string, commandName: string): CandidateAppHostDisplayInfo[] {
    const trimmed = output.trim();
    if (!trimmed) {
        return [];
    }

    const parsed = JSON.parse(trimmed);
    if (Array.isArray(parsed)) {
        const appHosts = parsed
            .filter(isLsCandidate)
            .map(candidate => ({
                path: candidate.path,
                language: candidate.language,
                status: candidate.status,
            }));

        const unexpectedCandidateCount = parsed.length - appHosts.length;
        if (unexpectedCandidateCount > 0) {
            extensionLogOutputChannel.warn(`${commandName} returned ${unexpectedCandidateCount} candidate(s) with an unexpected shape; ignoring those entries.`);
        }

        return appHosts;
    }

    if (isAppHostProjectSearchResult(parsed)) {
        return parsed.app_host_candidates.map(candidate => ({
            path: candidate.path,
            language: candidate.language,
            status: candidate.status,
            selected: typeof parsed.selected_project_file === 'string' && isSamePath(parsed.selected_project_file, candidate.path),
        }));
    }

    if (isLegacyAppHostProjectSearchResult(parsed)) {
        return toCandidatesFromLegacySearchResult(parsed);
    }

    throw new Error(`${commandName} returned an unexpected output shape.`);
}

async function discoverCSharpAppHostProjectsFromWorkspaceFiles(workspaceFolder: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo[]> {
    // This is the final fallback after both CLI discovery paths fail. Do not cap the
    // project scan here: VS Code returns only the first maxResults matches, which can
    // hide the only AppHost in a large workspace.
    const projectUris = await vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/*.csproj'), getAppHostDiscoveryExcludeGlob());
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

        if (isCSharpAppHostProject(projectContents)) {
            candidates.push({
                path: uri.fsPath,
                language: 'csharp',
                status: 'buildable',
            });
        }
    }

    return candidates;
}

function isCSharpAppHostProject(projectContents: string): boolean {
    return /<Project\b[^>]*\bSdk\s*=\s*["']Aspire\.AppHost\.Sdk(?:\/[^"']*)?["']/i.test(projectContents);
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

function formatErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

function throwIfCancellationRequested(cancellationToken?: vscode.CancellationToken): void {
    if (cancellationToken?.isCancellationRequested) {
        throw new Error('AppHost discovery was cancelled.');
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
            reject(new Error('AppHost discovery was cancelled.'));
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
        language: null,
        status: null,
        selected: typeof parsed.selected_project_file === 'string' && isSamePath(parsed.selected_project_file, candidatePath),
    }));
}

function isCSharpProjectCandidate(candidate: CandidateAppHostDisplayInfo): boolean {
    // Only `.csproj` candidates can own nearby C# source files for the editor alias
    // heuristic above. Modern `aspire ls` candidates include the CLI language id
    // (`language: "csharp"`); legacy `aspire extension get-apphosts` fallback
    // candidates do not have a language, so `null` is treated as C# here to
    // preserve old CLI support while keeping the compatibility gap local to
    // candidate adaptation/matching.
    return path.extname(candidate.path).toLowerCase() === '.csproj'
        && (candidate.language === null || candidate.language.toLowerCase() === 'csharp');
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

function isSamePath(left: string, right: string): boolean {
    const comparison = process.platform === 'win32' || process.platform === 'darwin'
        ? 'case-insensitive'
        : 'case-sensitive';
    const resolvedLeft = path.resolve(left);
    const resolvedRight = path.resolve(right);
    return comparison === 'case-insensitive'
        ? resolvedLeft.toLowerCase() === resolvedRight.toLowerCase()
        : resolvedLeft === resolvedRight;
}
