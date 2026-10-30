import * as path from 'path';
import * as vscode from 'vscode';
import { isSamePath } from './paths/comparison';
import { findConfiguredAppHostPaths } from './appHostProjectFiles';
import type { AppHostCandidate, AppHostProjectSearchResult, CandidateAppHostDisplayInfo } from './appHostCandidateTypes';

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

export function findWorkspaceDefaultCandidate(candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo | undefined {
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

export function isBuildableAppHostCandidate(candidate: Pick<AppHostCandidate, 'status'>): boolean {
    return candidate.status === 'buildable';
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

export function sortCandidatesByPath(candidates: readonly CandidateAppHostDisplayInfo[]): CandidateAppHostDisplayInfo[] {
    return [...candidates].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
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
