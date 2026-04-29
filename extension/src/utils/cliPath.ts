import * as vscode from 'vscode';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { extensionLogOutputChannel } from './logging';

const execFileAsync = promisify(execFile);
const fsAccessAsync = promisify(fs.access);

let cachedCliPathResults = new WeakMap<CliPathDependencies, CliPathResolutionResult>();
let pendingCliPathResolutions = new WeakMap<CliPathDependencies, Promise<CliPathResolutionResult>>();
let cliPathCacheGeneration = 0;

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
    const exeName = process.platform === 'win32' ? 'aspire.exe' : 'aspire';

    return [
        // Bundle install (recommended): ~/.aspire/bin/aspire
        path.join(homeDir, '.aspire', 'bin', exeName),
        // .NET global tool: ~/.dotnet/tools/aspire
        path.join(homeDir, '.dotnet', 'tools', exeName),
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
async function tryExecuteCli(cliPath: string): Promise<boolean> {
    try {
        await execFileAsync(cliPath, ['--version'], { timeout: 5000 });
        return true;
    }
    catch {
        return false;
    }
}

/**
 * Checks if the Aspire CLI is available on the system PATH.
 */
export async function isCliOnPath(): Promise<boolean> {
    return await tryExecuteCli('aspire');
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
    getDefaultPaths: () => string[];
    isOnPath: () => Promise<boolean>;
    findAtDefaultPath: () => Promise<string | undefined>;
    tryExecute: (cliPath: string) => Promise<boolean>;
    setConfiguredPath: (cliPath: string) => Promise<void>;
}

const defaultDependencies: CliPathDependencies = {
    getConfiguredPath: getConfiguredCliPath,
    getDefaultPaths: getDefaultCliInstallPaths,
    isOnPath: isCliOnPath,
    findAtDefaultPath: findCliAtDefaultPath,
    tryExecute: tryExecuteCli,
    setConfiguredPath: setConfiguredCliPath,
};

/**
 * Clears the cached CLI path so the next resolveCliPath call re-probes.
 */
export function clearCliPathCache(): void {
    cliPathCacheGeneration++;
    cachedCliPathResults = new WeakMap<CliPathDependencies, CliPathResolutionResult>();
    pendingCliPathResolutions = new WeakMap<CliPathDependencies, Promise<CliPathResolutionResult>>();
}

function shouldMutateConfiguration(cacheGeneration: number): boolean {
    return cacheGeneration === cliPathCacheGeneration;
}

function setCachedResultIfCurrent(deps: CliPathDependencies, result: CliPathResolutionResult, cacheGeneration: number): CliPathResolutionResult {
    if (cacheGeneration === cliPathCacheGeneration) {
        cachedCliPathResults.set(deps, result);
    }

    return result;
}

async function resolveCliPathCore(deps: CliPathDependencies, cacheGeneration: number): Promise<CliPathResolutionResult> {
    const configuredPath = deps.getConfiguredPath();
    const defaultPaths = deps.getDefaultPaths();

    // 1. Check if user has configured a custom path (not one of the defaults)
    if (configuredPath && !defaultPaths.includes(configuredPath)) {
        const isValid = await deps.tryExecute(configuredPath);
        if (isValid) {
            return setCachedResultIfCurrent(deps, { cliPath: configuredPath, available: true, source: 'configured' }, cacheGeneration);
        }

        extensionLogOutputChannel.warn(`Configured CLI path is invalid: ${configuredPath}`);
        // Continue to check other locations
    }

    // 2. Check if CLI is on PATH
    const onPath = await deps.isOnPath();
    if (onPath) {
        // If we previously auto-set the path to a default install location, clear it
        // since PATH is now working
        if (defaultPaths.includes(configuredPath) && shouldMutateConfiguration(cacheGeneration)) {
            extensionLogOutputChannel.info('Clearing aspireCliExecutablePath setting since CLI is on PATH');
            await deps.setConfiguredPath('');
        }

        return setCachedResultIfCurrent(deps, { cliPath: 'aspire', available: true, source: 'path' }, cacheGeneration);
    }

    // 3. Check default installation paths (~/.aspire/bin first, then ~/.dotnet/tools)
    const foundPath = await deps.findAtDefaultPath();
    if (foundPath) {
        // Update the setting so future invocations use this path
        if (configuredPath !== foundPath && shouldMutateConfiguration(cacheGeneration)) {
            extensionLogOutputChannel.info('Updating aspireCliExecutablePath setting to use default install location');
            await deps.setConfiguredPath(foundPath);
        }

        return setCachedResultIfCurrent(deps, { cliPath: foundPath, available: true, source: 'default-install' }, cacheGeneration);
    }

    // 4. CLI not found anywhere. Don't cache so we re-probe next time.
    return { cliPath: 'aspire', available: false, source: 'not-found' };
}

/**
 * Resolves the Aspire CLI path, checking multiple locations in order:
 * 1. User-configured path in VS Code settings
 * 2. System PATH
 * 3. Default installation directories (~/.aspire/bin, ~/.dotnet/tools)
 *
 * If the CLI is found at a default installation path but not on PATH,
 * the VS Code setting is updated to use that path.
 *
 * If the CLI is on PATH and a setting was previously auto-configured to a default path,
 * the setting is cleared to prefer PATH.
 */
export async function resolveCliPath(deps: CliPathDependencies = defaultDependencies): Promise<CliPathResolutionResult> {
    const cacheGeneration = cliPathCacheGeneration;

    const cachedResult = cachedCliPathResults.get(deps);
    if (cachedResult) {
        return cachedResult;
    }

    const pendingResolution = pendingCliPathResolutions.get(deps);
    if (pendingResolution) {
        return pendingResolution;
    }

    const resolution = resolveCliPathCore(deps, cacheGeneration);
    pendingCliPathResolutions.set(deps, resolution);

    const clearPending = () => {
        if (pendingCliPathResolutions.get(deps) === resolution) {
            pendingCliPathResolutions.delete(deps);
        }
    };

    void resolution.then(clearPending, clearPending);

    return resolution;
}
