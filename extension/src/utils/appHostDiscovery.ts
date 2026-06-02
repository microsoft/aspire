import * as path from 'path';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { aspireConfigFileName, getAppHostPathFromConfig, readJsonFile } from './cliTypes';
import { EnvironmentVariables } from './environment';
import { extensionLogOutputChannel } from './logging';
import { getAppHostDiscoveryTimeoutMs } from './settings';

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

const discoveryExcludePattern = '{**/artifacts/**,**/[Bb]in/**,**/[Oo]bj/**,**/node_modules/**,**/.git/**,**/.vs/**,**/.vscode-test/**,**/.worktrees/**,**/.idea/**,**/.aspire/modules/**}';

export class AppHostDiscoveryService implements vscode.Disposable {
    private readonly _onDidChangeCandidates = new vscode.EventEmitter<vscode.WorkspaceFolder>();
    private readonly _cache = new Map<string, Promise<CandidateAppHostDisplayInfo[]>>();
    private readonly _watchers = new Map<string, vscode.Disposable[]>();
    private readonly _activeCliProcesses = new Set<ChildProcessWithoutNullStreams>();
    private readonly _cancelActiveCliProcesses = new Set<(error: Error) => void>();
    private _disposed = false;
    readonly onDidChangeCandidates = this._onDidChangeCandidates.event;

    constructor(private readonly _terminalProvider: AspireTerminalProvider) {
    }

    async discover(workspaceFolder: vscode.WorkspaceFolder, forceRefresh = false): Promise<CandidateAppHostDisplayInfo[]> {
        this._throwIfDisposed();

        const key = path.resolve(workspaceFolder.uri.fsPath);
        if (forceRefresh) {
            this._cache.delete(key);
        }

        this._ensureWatchers(workspaceFolder, key);

        let resultPromise = this._cache.get(key);
        if (!resultPromise) {
            resultPromise = this._discoverCore(workspaceFolder)
                .then(candidates => this._includeConfiguredAppHostCandidate(workspaceFolder, candidates))
                .catch(error => {
                    this._cache.delete(key);
                    throw error;
                });
            this._cache.set(key, resultPromise);
        }

        return resultPromise;
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
                throw new Error(`aspire ls discovery failed: ${formatErrorMessage(error)}\naspire extension get-apphosts fallback failed: ${formatErrorMessage(fallbackError)}`);
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

            this._cache.delete(key);
            this._onDidChangeCandidates.fire(workspaceFolder);
        };
        const patterns = [
            '**/*.csproj',
            '**/*.fsproj',
            '**/*.vbproj',
            '**/apphost.cs',
            '**/apphost.ts',
            '**/apphost.js',
            '**/apphost.mts',
            '**/apphost.mjs',
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

export async function findConfiguredAppHostPaths(workspaceFolder: vscode.WorkspaceFolder): Promise<string[]> {
    let newConfigFiles: vscode.Uri[];
    let legacySettingsFiles: vscode.Uri[];
    try {
        [newConfigFiles, legacySettingsFiles] = await Promise.all([
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, `**/${aspireConfigFileName}`), discoveryExcludePattern),
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/.aspire/settings.json'), discoveryExcludePattern),
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

function isExcludedDiscoveryUri(workspaceFolder: vscode.WorkspaceFolder, uri: vscode.Uri): boolean {
    const relativePath = path.relative(workspaceFolder.uri.fsPath, uri.fsPath);
    if (relativePath === '' || relativePath.startsWith('..') || path.isAbsolute(relativePath)) {
        return true;
    }

    const segments = relativePath.split(/[\\/]+/);
    return segments.some((segment, index) => {
        const lowerSegment = segment.toLowerCase();
        return lowerSegment === 'artifacts'
            || lowerSegment === 'bin'
            || lowerSegment === 'obj'
            || lowerSegment === 'node_modules'
            || lowerSegment === '.git'
            || lowerSegment === '.vs'
            || lowerSegment === '.vscode-test'
            || lowerSegment === '.worktrees'
            || lowerSegment === '.idea'
            || (lowerSegment === '.aspire' && segments[index + 1]?.toLowerCase() === 'modules');
    });
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
