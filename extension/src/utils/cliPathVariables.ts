import * as vscode from 'vscode';
import * as path from 'path';

export type CliPathResolutionTarget =
    | { readonly kind: 'window' }
    | { readonly kind: 'workspaceFolder'; readonly workspaceFolder: vscode.WorkspaceFolder };

export const windowCliPathTarget: CliPathResolutionTarget = { kind: 'window' };

export function workspaceFolderCliPathTarget(folder: vscode.WorkspaceFolder): CliPathResolutionTarget {
    return { kind: 'workspaceFolder', workspaceFolder: folder };
}

/**
 * Returns the configuration scope key for a resolution target.
 * Window scope uses the literal string 'window'; workspace-folder scope
 * uses the folder URI so that each folder has its own stored value.
 */
export function getCliPathTargetKey(target: CliPathResolutionTarget): string {
    if (target.kind === 'window') {
        return 'window';
    }
    return `workspaceFolder:${target.workspaceFolder.uri.toString()}`;
}

export interface ExpandedConfiguredCliPath {
    /** The raw configured path before any variable substitution. */
    configuredPath: string;
    /** The fully resolved path after token expansion and normalization, if successful. */
    resolvedPath?: string;
    /** Human-readable description of why expansion failed, if it did. */
    error?: string;
}

// Matches any ${...} variable token in a configured path string.
const tokenRegex = /\$\{([^}]+)\}/g;

/**
 * Expands `${workspaceFolder}` and `${workspaceFolder:name}` tokens in a
 * configured CLI path and returns both the original and the resolved form.
 *
 * Rules:
 *   - `${workspaceFolder}` uses the operation folder when the target is a
 *     workspace folder, or the single open folder when the target is window.
 *   - `${workspaceFolder:name}` requires exactly one open folder with that name.
 *   - Unknown `${...}` tokens are rejected with an error.
 *   - Paths with no tokens are returned unchanged (no resolvedPath).
 *   - Normalization is applied only when at least one token was expanded.
 *
 * @param configuredPath - The raw string from configuration.
 * @param target - The resolution target (window or a specific workspace folder).
 * @param workspaceFolders - The open workspace folders; defaults to VS Code's live list.
 * @param platform - The OS platform; defaults to `process.platform`.
 */
export function expandConfiguredCliPath(
    configuredPath: string,
    target: CliPathResolutionTarget,
    workspaceFolders: readonly vscode.WorkspaceFolder[] = vscode.workspace.workspaceFolders ?? [],
    platform: NodeJS.Platform = process.platform
): ExpandedConfiguredCliPath {
    // Fast path: no tokens — absent resolvedPath and error signals the caller to use configuredPath unchanged.
    if (!configuredPath.includes('${')) {
        return { configuredPath };
    }

    const pathLib = platform === 'win32' ? path.win32 : path.posix;

    let error: string | undefined;
    let hasExpandedToken = false;

    const resolved = configuredPath.replace(tokenRegex, (fullToken, tokenContent: string) => {
        // Once an error is recorded stop substituting further tokens.
        if (error !== undefined) {
            return fullToken;
        }

        if (tokenContent === 'workspaceFolder') {
            if (target.kind === 'workspaceFolder') {
                hasExpandedToken = true;
                return target.workspaceFolder.uri.fsPath;
            }

            // Window target: only valid when exactly one folder is open.
            if (workspaceFolders.length === 1) {
                hasExpandedToken = true;
                return workspaceFolders[0].uri.fsPath;
            }
            if (workspaceFolders.length === 0) {
                error = `${fullToken} cannot be expanded: no workspace folders are open`;
            } else {
                error = `${fullToken} is ambiguous: multiple workspace folders are open`;
            }
            return fullToken;
        }

        if (tokenContent.startsWith('workspaceFolder:')) {
            const name = tokenContent.slice('workspaceFolder:'.length);
            const matches = workspaceFolders.filter(f => f.name === name);

            if (matches.length === 0) {
                error = `${fullToken} cannot be expanded: no workspace folder named '${name}' is open`;
                return fullToken;
            }
            if (matches.length > 1) {
                error = `${fullToken} is ambiguous: multiple workspace folders named '${name}' are open`;
                return fullToken;
            }

            hasExpandedToken = true;
            return matches[0].uri.fsPath;
        }

        error = `Unknown variable ${fullToken} in configured CLI path`;
        return fullToken;
    });

    if (error !== undefined) {
        return { configuredPath, error };
    }

    if (hasExpandedToken) {
        return { configuredPath, resolvedPath: pathLib.normalize(resolved) };
    }

    return { configuredPath };
}

/**
 * Returns the resolution target that owns `uri`: the workspace folder containing it, or the
 * window scope when no open folder owns it (for example, a path outside every open folder).
 *
 * AppHost/project/config URIs must always resolve their scope this way rather than from
 * process cwd, so a hidden CLI operation always resolves and forwards the same CLI its
 * owning folder configured.
 */
export function getCliPathTargetForUri(uri: vscode.Uri): CliPathResolutionTarget {
    const folder = vscode.workspace.getWorkspaceFolder(uri);
    return folder ? workspaceFolderCliPathTarget(folder) : windowCliPathTarget;
}

/** File name of the Aspire CLI executable, without any platform-specific extension. */
const cliExecutableFileName = 'aspire';

/**
 * Extensions Windows treats as directly executable, used both to complete an extensionless path and
 * to recognise a path that already names the executable.
 */
const windowsExecutableExtensions = ['.exe', '.cmd', '.bat'];

/**
 * Returns the set of filesystem paths to probe when looking for the CLI
 * executable at `cliPath`.
 *
 * On non-Windows platforms, or when the path already carries an extension,
 * only the path itself is returned. On Windows, an extensionless path yields
 * four candidates so that both native executables and command shims are found.
 *
 * A directory containing the CLI is also probed, because pointing the setting at a build
 * output folder such as `artifacts/bin/Aspire.Cli/Debug/net10.0` is a natural mistake that
 * otherwise fails silently. Probing is a process launch that fails fast when the joined path
 * does not exist, so the extra candidates cost nothing once an earlier candidate matches.
 */
export function getCliExecutableCandidates(
    cliPath: string,
    platform: NodeJS.Platform = process.platform
): string[] {
    const withExtensions = (candidate: string): string[] => {
        if (platform !== 'win32') {
            return [candidate];
        }

        // An existing extension (e.g. .exe, .cmd) means the caller already knows
        // the exact form; do not append additional extensions.
        if (path.win32.extname(candidate) !== '') {
            return [candidate];
        }

        return [candidate, ...windowsExecutableExtensions.map(extension => `${candidate}${extension}`)];
    };
    const joiner = platform === 'win32' ? path.win32 : path.posix;

    // Only an executable extension means the path names the CLI itself. extname() reports the text
    // after the last dot, so testing for "has any extension" classified a perfectly ordinary
    // directory - `...\bin\Debug\net10.0`, which is where a locally built CLI lives - as a file with
    // extension `.0`, and `aspire.exe` inside it was never probed.
    const namesFile = platform === 'win32'
        && windowsExecutableExtensions.includes(path.win32.extname(cliPath).toLowerCase());
    if (namesFile) {
        return withExtensions(cliPath);
    }

    return [
        ...withExtensions(cliPath),
        ...withExtensions(joiner.join(cliPath, cliExecutableFileName)),
    ];
}
