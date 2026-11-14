import * as path from 'path';
import * as vscode from 'vscode';
import { aspireConfigFileName, getAppHostPathFromConfig, readJsonFile } from './cliTypes';
import { extensionLogOutputChannel } from './logging';
import { classifyAppHostPath, projectContentsReferencesRunnableAspireAppHost } from './appHostLanguage';
import { appHostDiscoveryFindFilesMaxResults, appHostSourceFilePatterns, getAppHostDiscoveryExcludeGlob, isExcludedDiscoveryCandidate } from './workspaceFileSearch';
import { FileSystemEntryDescriptorIndex, getFileSystemEntryDescriptor } from './paths/fileSystemIdentity';
import type { CandidateAppHostDisplayInfo } from './appHostCandidateTypes';

export async function findConfiguredAppHostPaths(workspaceFolder: vscode.WorkspaceFolder, cancellationToken?: vscode.CancellationToken, options?: { readonly throwOnError: boolean }): Promise<string[]> {
    let newConfigFiles: vscode.Uri[];
    let legacySettingsFiles: vscode.Uri[];
    try {
        const excludePattern = getAppHostDiscoveryExcludeGlob();
        // A truncated search cannot establish that the workspace has no configured AppHost.
        const maxResults = options?.throwOnError ? undefined : appHostDiscoveryFindFilesMaxResults;
        [newConfigFiles, legacySettingsFiles] = await Promise.all([
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, `**/${aspireConfigFileName}`), excludePattern, maxResults, cancellationToken),
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/.aspire/settings.json'), excludePattern, maxResults, cancellationToken),
        ]);
    }
    catch (error) {
        if (options?.throwOnError) {
            throw error;
        }
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
        catch (error) {
            if (options?.throwOnError) {
                throw error;
            }
        }
    }

    return configuredPaths;
}

export async function discoverSourceAppHostsFromWorkspaceFiles(workspaceFolder: vscode.WorkspaceFolder, cancellationToken: vscode.CancellationToken): Promise<CandidateAppHostDisplayInfo[]> {
    const uris = await vscode.workspace.findFiles(
        new vscode.RelativePattern(workspaceFolder, `{${appHostSourceFilePatterns.join(',')}}`),
        getAppHostDiscoveryExcludeGlob(),
        undefined,
        cancellationToken);
    return uris.map(uri => ({
        path: uri.fsPath,
        language: classifyAppHostPath(uri.fsPath),
        status: 'possibly-unbuildable',
    }));
}

export async function discoverConfiguredAppHostsFromWorkspaceFiles(workspaceFolder: vscode.WorkspaceFolder, cancellationToken: vscode.CancellationToken): Promise<CandidateAppHostDisplayInfo[]> {
    const configuredPaths = await findConfiguredAppHostPaths(workspaceFolder, cancellationToken, { throwOnError: true });
    const candidates: CandidateAppHostDisplayInfo[] = [];
    const candidateDescriptors = new FileSystemEntryDescriptorIndex();
    let unreadableTarget: Error | undefined;
    for (const configuredPath of configuredPaths) {
        if (cancellationToken.isCancellationRequested) {
            throw new vscode.CancellationError();
        }

        const uri = vscode.Uri.file(configuredPath);
        if (isExcludedDiscoveryCandidate(workspaceFolder, uri)) {
            continue;
        }

        const isProject = ['.csproj', '.fsproj', '.vbproj'].includes(path.extname(configuredPath).toLowerCase());
        const language = isProject ? getProjectLanguage(configuredPath) : classifyAppHostPath(configuredPath);
        if (language === 'unknown') {
            extensionLogOutputChannel.warn(`Workspace file fallback cannot validate configured AppHost ${configuredPath}: unsupported file type.`);
            continue;
        }

        let contents: string;
        try {
            contents = Buffer.from(await vscode.workspace.fs.readFile(uri)).toString('utf8');
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to read configured AppHost ${configuredPath}: ${formatErrorMessage(error)}`);
            if (!isMissingConfiguredTarget(error)) {
                unreadableTarget ??= new Error(`Failed to read configured AppHost ${configuredPath}: ${formatErrorMessage(error)}`);
            }
            continue;
        }

        if (isProject && !isAppHostProject(contents)) {
            extensionLogOutputChannel.warn(`Ignoring configured project that does not reference a runnable Aspire AppHost: ${configuredPath}`);
            continue;
        }

        const descriptor = getFileSystemEntryDescriptor(configuredPath);
        if (candidateDescriptors.find(descriptor) !== undefined) {
            continue;
        }

        // A readable configured source establishes presence, not buildability, without the CLI.
        candidates.push({
            path: configuredPath,
            language,
            status: isProject ? 'buildable' : 'possibly-unbuildable',
        });
        candidateDescriptors.add(descriptor);
    }

    if (candidates.length === 0 && unreadableTarget) {
        throw unreadableTarget;
    }

    return candidates;
}

export async function discoverProjectAppHostsFromWorkspaceFiles(workspaceFolder: vscode.WorkspaceFolder): Promise<CandidateAppHostDisplayInfo[]> {
    // This is the final fallback after both CLI discovery paths fail. Do not cap the
    // project scan here: VS Code returns only the first maxResults matches, which can
    // hide the only AppHost in a large workspace.
    const [projectUriLists, configuredPaths] = await Promise.all([
        Promise.all([
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/*.csproj'), getAppHostDiscoveryExcludeGlob()),
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/*.fsproj'), getAppHostDiscoveryExcludeGlob()),
            vscode.workspace.findFiles(new vscode.RelativePattern(workspaceFolder, '**/*.vbproj'), getAppHostDiscoveryExcludeGlob()),
        ]),
        findConfiguredAppHostPaths(workspaceFolder),
    ]);
    const projectUris = projectUriLists.flat().sort((left, right) => left.fsPath.localeCompare(right.fsPath));
    // Only identical paths can skip a read: case-only Windows paths can have
    // different native identities. Candidate aliases are deduplicated below.
    const projectPaths = new Set(projectUris.map(uri => path.resolve(uri.fsPath)));
    const configuredProjectPaths = new Set<string>();

    // Configured projects can live outside the workspace, but must pass the same
    // content check as scanned projects before becoming candidates.
    for (const configuredPath of configuredPaths) {
        if (!['.csproj', '.fsproj', '.vbproj'].includes(path.extname(configuredPath).toLowerCase())) {
            extensionLogOutputChannel.warn(`Workspace file fallback cannot validate configured AppHost ${configuredPath}: only .NET project files are supported.`);
            continue;
        }

        const uri = vscode.Uri.file(configuredPath);
        if (isExcludedDiscoveryCandidate(workspaceFolder, uri)) {
            continue;
        }

        const key = path.resolve(uri.fsPath);
        configuredProjectPaths.add(key);
        if (!projectPaths.has(key)) {
            projectUris.push(uri);
            projectPaths.add(key);
        }
    }

    const candidates: CandidateAppHostDisplayInfo[] = [];
    let unreadableProject: Error | undefined;
    const candidateDescriptors = new FileSystemEntryDescriptorIndex();
    for (const uri of projectUris) {
        let projectContents: string;
        try {
            projectContents = Buffer.from(await vscode.workspace.fs.readFile(uri)).toString('utf8');
        }
        catch (error) {
            extensionLogOutputChannel.warn(`Failed to read possible AppHost project ${uri.fsPath}: ${formatErrorMessage(error)}`);
            if (!configuredProjectPaths.has(path.resolve(uri.fsPath)) || !isMissingConfiguredTarget(error)) {
                unreadableProject ??= new Error(`Failed to read possible AppHost project ${uri.fsPath}: ${formatErrorMessage(error)}`);
            }
            continue;
        }

        if (isAppHostProject(projectContents)) {
            const descriptor = getFileSystemEntryDescriptor(uri.fsPath);
            if (candidateDescriptors.find(descriptor) !== undefined) {
                continue;
            }

            candidates.push({
                path: uri.fsPath,
                language: getProjectLanguage(uri.fsPath),
                status: 'buildable',
            });
            candidateDescriptors.add(descriptor);
        }
        else if (configuredProjectPaths.has(path.resolve(uri.fsPath))) {
            extensionLogOutputChannel.warn(`Ignoring configured project that does not reference a runnable Aspire AppHost: ${uri.fsPath}`);
        }
    }

    if (candidates.length === 0 && unreadableProject) {
        throw unreadableProject;
    }

    return candidates;
}

export function formatErrorMessage(error: unknown): string {
    return error instanceof Error ? error.message : String(error);
}

function isAppHostProject(projectContents: string): boolean {
    return projectContentsReferencesRunnableAspireAppHost(projectContents);
}

function isMissingConfiguredTarget(error: unknown): boolean {
    return error instanceof vscode.FileSystemError
        && ['FileNotFound', 'FileIsADirectory', 'FileNotADirectory'].includes(error.code);
}

function getProjectLanguage(projectPath: string): string {
    return path.extname(projectPath).toLowerCase() === '.fsproj'
        ? 'fsharp'
        : path.extname(projectPath).toLowerCase() === '.vbproj'
            ? 'visualbasic'
            : 'csharp';
}
