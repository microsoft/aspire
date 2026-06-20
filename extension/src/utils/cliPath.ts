import * as vscode from 'vscode';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { extensionLogOutputChannel } from './logging';
import { getCliExecutionCommand } from './cliExecution';

const execFileAsync = promisify(execFile);
const fsAccessAsync = promisify(fs.access);

/**
 * Gets the default installation paths for the Aspire CLI, in priority order.
 *
 * The CLI can be installed in two ways:
 * 1. Bundle install (recommended): ~/.aspire/bin/aspire
 * 2. .NET global tool: ~/.dotnet/tools/aspire
 *
 * @returns An array of default CLI paths to check, ordered by priority
 */
export function getDefaultCliInstallPaths(): string[] {
    const homeDir = os.homedir();
    const bundleInstallDirectory = path.join(homeDir, '.aspire', 'bin');
    const globalToolDirectory = path.join(homeDir, '.dotnet', 'tools');

    if (process.platform === 'win32') {
        return [
            // Bundle install (recommended): ~/.aspire/bin/aspire.exe
            path.join(bundleInstallDirectory, 'aspire.exe'),
            // Some Windows installs expose command shims instead of native executables.
            path.join(bundleInstallDirectory, 'aspire.cmd'),
            // .NET global tool: ~/.dotnet/tools/aspire.exe
            path.join(globalToolDirectory, 'aspire.exe'),
            // .NET global tool command shim: ~/.dotnet/tools/aspire.cmd
            path.join(globalToolDirectory, 'aspire.cmd'),
        ];
    }

    return [
        // Bundle install (recommended): ~/.aspire/bin/aspire
        path.join(bundleInstallDirectory, 'aspire'),
        // .NET global tool: ~/.dotnet/tools/aspire
        path.join(globalToolDirectory, 'aspire'),
    ];
}

/**
 * Checks if a file exists and is accessible.
 */
async function fileExists(filePath: string): Promise<boolean> {
    try {
        await fsAccessAsync(filePath, fs.constants.F_OK);
        return true;
    }
    catch {
        return false;
    }
}

/**
 * Tries to execute the CLI at the given path to verify it works.
 */
export async function tryExecuteCli(cliPath: string): Promise<boolean> {
    try {
        const command = getCliExecutionCommand(cliPath, ['--version']);
        await execFileAsync(command.file, command.args, { timeout: 5000, windowsVerbatimArguments: command.windowsVerbatimArguments });

        return true;
    }
    catch {
        return false;
    }
}

export function getWindowsPathCliCandidates(env: NodeJS.ProcessEnv = process.env): string[] {
    // Avoid `where.exe aspire` here because bare patterns include the current directory,
    // which could make a workspace-local aspire.cmd look like the user's global CLI.
    // See: https://learn.microsoft.com/windows-server/administration/windows-commands/where
    const pathValue = env.Path ?? env.PATH ?? '';
    const pathExtensions = (env.PATHEXT ?? '.COM;.EXE;.BAT;.CMD')
        .split(';')
        .map(extension => extension.trim())
        .filter(Boolean);
    const candidates: string[] = [];
    const seenCandidates = new Set<string>();

    for (const pathEntry of pathValue.split(';')) {
        const directory = pathEntry.trim().replace(/^"(.*)"$/, '$1');
        if (!directory) {
            continue;
        }

        for (const extension of pathExtensions) {
            const candidate = path.win32.join(directory, `aspire${extension}`);
            const normalizedCandidate = candidate.toLowerCase();

            if (!seenCandidates.has(normalizedCandidate)) {
                candidates.push(candidate);
                seenCandidates.add(normalizedCandidate);
            }
        }
    }

    return candidates;
}

/**
 * Finds the Aspire CLI on the system PATH.
 */
export async function findCliOnPath(): Promise<string | undefined> {
    if (process.platform !== 'win32') {
        return await tryExecuteCli('aspire') ? 'aspire' : undefined;
    }

    for (const candidate of getWindowsPathCliCandidates()) {
        if (await fileExists(candidate) && await tryExecuteCli(candidate)) {
            return candidate;
        }
    }

    return undefined;
}

/**
 * Finds the first default installation path where the Aspire CLI exists and is executable.
 *
 * @returns The path where CLI was found, or undefined if not found at any default location
 */
export async function findCliAtDefaultPath(): Promise<string | undefined> {
    for (const defaultPath of getDefaultCliInstallPaths()) {
        if (await fileExists(defaultPath) && await tryExecuteCli(defaultPath)) {
            return defaultPath;
        }
    }

    return undefined;
}

/**
 * Gets the VS Code configuration setting for the Aspire CLI path.
 */
export function getConfiguredCliPath(): string {
    return vscode.workspace.getConfiguration('aspire').get<string>('aspireCliExecutablePath', '').trim();
}

/**
 * Updates the VS Code configuration setting for the Aspire CLI path.
 * Uses ConfigurationTarget.Global to set it at the user level.
 */
export async function setConfiguredCliPath(cliPath: string): Promise<void> {
    extensionLogOutputChannel.info(`Setting aspire.aspireCliExecutablePath to: ${cliPath || '(empty)'}`);
    await vscode.workspace.getConfiguration('aspire').update(
        'aspireCliExecutablePath',
        cliPath || undefined, // Use undefined to remove the setting
        vscode.ConfigurationTarget.Global
    );
}

/**
 * Result of checking CLI availability.
 */
export interface CliPathResolutionResult {
    /** The resolved CLI path to use */
    cliPath: string;
    /** Whether the CLI is available */
    available: boolean;
    /** Where the CLI was found */
    source: 'path' | 'default-install' | 'configured' | 'not-found';
}

/**
 * Dependencies for resolveCliPath that can be overridden for testing.
 */
export interface CliPathDependencies {
    getConfiguredPath: () => string;
    findOnPath: () => Promise<string | undefined>;
    findAtDefaultPath: () => Promise<string | undefined>;
    tryExecute: (cliPath: string) => Promise<boolean>;
    setConfiguredPath: (cliPath: string) => Promise<void>;
}

const defaultDependencies: CliPathDependencies = {
    getConfiguredPath: getConfiguredCliPath,
    findOnPath: findCliOnPath,
    findAtDefaultPath: findCliAtDefaultPath,
    tryExecute: tryExecuteCli,
    setConfiguredPath: setConfiguredCliPath,
};

/**
 * Resolves the Aspire CLI path, checking multiple locations in order:
 * 1. E2E runner-provided CLI path
 * 2. User-configured path in VS Code settings
 * 3. System PATH
 * 4. Default installation directories (~/.aspire/bin, ~/.dotnet/tools)
 *
 * If the CLI is found at a default installation path but not on PATH,
 * the VS Code setting is updated to use that path.
 *
 * If a setting is configured, it is treated as authoritative when valid because users can
 * intentionally pin a default-location shim while PATH resolves a different CLI.
 */
export async function resolveCliPath(deps: CliPathDependencies = defaultDependencies): Promise<CliPathResolutionResult> {
    const configuredPath = deps.getConfiguredPath();
    const e2eCliPath = process.env.ASPIRE_EXTENSION_E2E_CLI_PATH?.trim();

    if (e2eCliPath) {
        const isValid = await deps.tryExecute(e2eCliPath);
        if (isValid) {
            return { cliPath: e2eCliPath, available: true, source: 'configured' };
        }

        extensionLogOutputChannel.warn(`E2E CLI path is invalid: ${e2eCliPath}`);
    }

    if (configuredPath) {
        const isValid = await deps.tryExecute(configuredPath);
        if (isValid) {
            return { cliPath: configuredPath, available: true, source: 'configured' };
        }

        extensionLogOutputChannel.warn(`Configured CLI path is invalid: ${configuredPath}`);
        // Continue to check other locations
    }

    // 2. Check if CLI is on PATH
    const pathCli = await deps.findOnPath();
    if (pathCli) {
        return { cliPath: pathCli, available: true, source: 'path' };
    }

    // 3. Check default installation paths (~/.aspire/bin first, then ~/.dotnet/tools)
    const foundPath = await deps.findAtDefaultPath();
    if (foundPath) {
        // Update the setting so future invocations use this path
        if (configuredPath !== foundPath) {
            extensionLogOutputChannel.info('Updating aspireCliExecutablePath setting to use default install location');
            await deps.setConfiguredPath(foundPath);
        }

        return { cliPath: foundPath, available: true, source: 'default-install' };
    }

    // 4. CLI not found anywhere
    return { cliPath: 'aspire', available: false, source: 'not-found' };
}
