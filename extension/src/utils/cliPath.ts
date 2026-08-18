import * as vscode from 'vscode';
import { execFile } from 'child_process';
import { promisify } from 'util';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { extensionLogOutputChannel } from './logging';
import { getCmdShimSpawnCommand, isCommandShimPath, shouldWrapWithCmd } from './cmdShim';
import {
    expandConfiguredCliPath,
    getCliExecutableCandidates,
    getCliPathTargetKey,
    windowCliPathTarget,
    CliPathResolutionTarget,
} from './cliPathVariables';

const execFileAsync = promisify(execFile);
const fsAccessAsync = promisify(fs.access);

/**
 * Gets the default installation paths for the Aspire CLI, in priority order.
 *
 * The CLI can be installed through the bundle installer or as a .NET global
 * tool. Windows global tools expose an aspire.cmd shim and may use
 * DOTNET_CLI_HOME to redirect their install directory.
 *
 * @returns An array of default CLI paths to check, ordered by priority
 */
export function getDefaultCliInstallPaths(): string[] {
    const homeDir = os.homedir();
    const bundleInstallDirectory = path.join(homeDir, '.aspire', 'bin');

    // This fix targets Windows command shims. Keep POSIX discovery unchanged so
    // DOTNET_CLI_HOME cannot unexpectedly change its existing default path.
    if (process.platform === 'win32') {
        // .NET global tools use DOTNET_CLI_HOME as their home directory when set.
        // https://learn.microsoft.com/dotnet/core/tools/dotnet-environment-variables#dotnet_cli_home
        const dotnetCliHome = process.env.DOTNET_CLI_HOME || homeDir;
        const globalToolDirectory = path.join(dotnetCliHome, '.dotnet', 'tools');
        return [
            // Bundle install (recommended): ~/.aspire/bin/aspire.exe
            path.join(bundleInstallDirectory, 'aspire.exe'),
            // Prefer the native executable when both global-tool forms are present.
            path.join(globalToolDirectory, 'aspire.exe'),
            // Some .NET global-tool installs expose only a command shim.
            path.join(globalToolDirectory, 'aspire.cmd'),
        ];
    }

    const globalToolDirectory = path.join(homeDir, '.dotnet', 'tools');
    return [
        // Bundle install (recommended): ~/.aspire/bin/aspire
        path.join(bundleInstallDirectory, 'aspire'),
        // .NET global tool: ~/.dotnet/tools/aspire
        path.join(globalToolDirectory, 'aspire'),
    ];
}

function areCliPathsEqual(left: string, right: string): boolean {
    if (process.platform !== 'win32') {
        // Preserve the existing POSIX behavior: only paths the extension wrote
        // byte-for-byte are considered auto-configured. A normalized equivalent
        // may have been an intentional user pin.
        return left === right;
    }

    return path.win32.normalize(left).toLowerCase() === path.win32.normalize(right).toLowerCase();
}

export function isFullyQualifiedWindowsPath(cliPath: string): boolean {
    const root = path.win32.parse(path.win32.normalize(cliPath)).root;
    return /^[A-Za-z]:[\\/]$/.test(root) || root.startsWith('\\\\');
}

function isAbsoluteCliPath(cliPath: string): boolean {
    return path.posix.isAbsolute(cliPath) || isFullyQualifiedWindowsPath(cliPath);
}

function containsCliPath(paths: readonly string[], candidate: string): boolean {
    return paths.some(defaultPath => areCliPathsEqual(defaultPath, candidate));
}

function getLegacyAutoConfiguredCliPaths(): string[] {
    const homeDir = os.homedir();
    const executableName = process.platform === 'win32' ? 'aspire.exe' : 'aspire';
    return [
        path.join(homeDir, '.aspire', 'bin', executableName),
        path.join(homeDir, '.dotnet', 'tools', executableName),
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
 * Test seam for the process launch performed by {@link tryExecuteCli}.
 */
export type CliProbeExecutor = (
    command: string,
    args: string[],
    options: { timeout: number; windowsVerbatimArguments?: boolean },
) => Promise<unknown>;

const defaultProbeExecutor: CliProbeExecutor = (command, args, options) => execFileAsync(command, args, options);

/**
 * Tries to execute the CLI at the given path to verify it works.
 */
export async function tryExecuteCli(cliPath: string, execute: CliProbeExecutor = defaultProbeExecutor): Promise<boolean> {
    try {
        if (shouldWrapWithCmd(cliPath)) {
            // Reuse the spawn path's cmd.exe wrapper. Passing the shim as a plain argv entry
            // is not enough: libuv only auto-quotes arguments containing a space, tab, or
            // quote, so a shim under a directory such as `C:\Users\a&b\.dotnet\tools` reaches
            // cmd.exe unquoted and gets split at the `&`, rejecting a working CLI.
            const { command, args, windowsVerbatimArguments } = getCmdShimSpawnCommand(cliPath, ['--version']);
            await execute(command, args, { timeout: 5000, windowsVerbatimArguments });
        }
        else {
            await execute(cliPath, ['--version'], { timeout: 5000 });
        }

        return true;
    }
    catch {
        return false;
    }
}

/**
 * The configured path that CLI resolution most recently rejected as unusable.
 *
 * `getForwardableAspireCliPath` reads the raw setting independently of resolution, so a
 * configured file that exists but fails to execute would still be forwarded as
 * `AspireCliPath` after resolution fell back to a different CLI. `ResolveAspireCliBundle`
 * stops at an explicit `AspireCliPath` instead of probing PATH, so the AppHost would be
 * stamped with bundle paths belonging to a CLI that never ran.
 *
 * This state now lives per-target inside `CliPathResolver` below; the exports here are
 * compatibility wrappers over the shared `cliPathResolver` singleton for window-scoped
 * consumers that have not migrated to an explicit `CliPathResolutionTarget`.
 */

interface CliPathLookupOptions {
    platform?: NodeJS.Platform;
    pathValue?: string;
    fileExists?: (candidate: string) => Promise<boolean>;
    tryExecute?: (candidate: string) => Promise<boolean>;
}

/**
 * Finds an executable Aspire CLI on PATH.
 *
 * Windows command shims must be resolved to their concrete path so downstream
 * process launches can route them through cmd.exe instead of passing the bare
 * command name to Node's executable-only spawn path.
 */
export async function findCliOnPath(options: CliPathLookupOptions = {}): Promise<string | undefined> {
    const platform = options.platform ?? process.platform;
    const tryExecute = options.tryExecute ?? tryExecuteCli;
    const pathValue = options.pathValue ?? process.env.PATH;
    if (!pathValue) {
        return undefined;
    }

    const candidateExists = options.fileExists ?? fileExists;
    if (platform !== 'win32') {
        for (const pathEntry of pathValue.split(path.posix.delimiter)) {
            if (!path.posix.isAbsolute(pathEntry)) {
                continue;
            }

            const candidate = path.posix.join(pathEntry, 'aspire');
            if (await candidateExists(candidate) && await tryExecute(candidate)) {
                return candidate;
            }
        }

        return undefined;
    }

    const executableNames = ['aspire.exe', 'aspire.cmd', 'aspire.bat', 'aspire'];
    for (const pathEntry of pathValue.split(path.win32.delimiter)) {
        const directory = pathEntry.trim().replace(/^"(.*)"$/, '$1');
        if (!directory || !isFullyQualifiedWindowsPath(directory)) {
            continue;
        }

        for (const executableName of executableNames) {
            const candidate = path.win32.join(directory, executableName);
            if (await candidateExists(candidate) && await tryExecute(candidate)) {
                return candidate;
            }
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
 *
 * A workspace-folder target reads the setting scoped to that folder, so a folder-level
 * override (or a committed `.vscode/settings.json` value) takes effect for that folder
 * independently of the window-wide value.
 */
export function getConfiguredCliPath(target: CliPathResolutionTarget = windowCliPathTarget): string {
    const resource = target.kind === 'workspaceFolder' ? target.workspaceFolder.uri : undefined;
    const configuration = vscode.workspace.getConfiguration('aspire', resource);
    if (!vscode.workspace.isTrusted) {
        // Repository settings are untrusted input. Never execute a workspace-provided path
        // until the user trusts the workspace. A user-level value containing a workspace
        // token is also repository-directed after expansion, so only preserve a concrete pin.
        const globalValue = asConfiguredPath(configuration.inspect<unknown>('aspireCliExecutablePath')?.globalValue);
        return /\$\{workspaceFolder(?::[^}]*)?\}/.test(globalValue) ? '' : globalValue;
    }

    return asConfiguredPath(configuration.get<unknown>('aspireCliExecutablePath'));
}

/**
 * Settings files are hand-edited, so the value is whatever JSON the user typed. Anything
 * that is not a string is treated as unset rather than allowed to throw from inside CLI
 * resolution, which every command depends on.
 */
function asConfiguredPath(value: unknown): string {
    return typeof value === 'string' ? value.trim() : '';
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
 * Dependencies for `CliPathResolver.resolve` that can be overridden for testing.
 *
 * `getConfiguredPath`, `isConfiguredPathAutoConfigured`, and `setConfiguredPath` receive the
 * resolution target so a workspace-folder scope can read/write its own configuration slice
 * instead of always reading the window-wide setting.
 */
export interface CliPathDependencies {
    getConfiguredPath: (target: CliPathResolutionTarget) => string;
    getWorkspaceFolders: () => readonly vscode.WorkspaceFolder[];
    getDefaultPaths: () => string[];
    isConfiguredPathAutoConfigured: (configuredPath: string, defaultPaths: readonly string[], target: CliPathResolutionTarget) => boolean;
    findOnPath: () => Promise<string | undefined>;
    findAtDefaultPath: () => Promise<string | undefined>;
    tryExecute: (cliPath: string) => Promise<boolean>;
    getExecutableCandidates: (cliPath: string) => string[];
    setConfiguredPath: (cliPath: string, target: CliPathResolutionTarget) => Promise<void>;
    /**
     * Temporary test-observation seam kept alongside resolver state so existing tests can
     * assert on forwarding-fallback updates directly. `CliPathResolver`'s own per-target state
     * (`getResolvedCliPathForForwarding`) remains the authoritative source for consumers.
     */
    updateResolvedPathForForwarding?: (configuredPath: string, resolvedPath: string | undefined) => void;
}

const defaultDependencies: CliPathDependencies = {
    getConfiguredPath: getConfiguredCliPath,
    getWorkspaceFolders: () => vscode.workspace.workspaceFolders ?? [],
    // Only paths that older extension versions could have written to the
    // setting are safe to treat as automatically configured.
    getDefaultPaths: getLegacyAutoConfiguredCliPaths,
    isConfiguredPathAutoConfigured: (configuredPath, defaultPaths, target) => {
        const resource = target.kind === 'workspaceFolder' ? target.workspaceFolder.uri : undefined;
        const inspection = vscode.workspace.getConfiguration('aspire', resource).inspect<string>('aspireCliExecutablePath');
        const hasWorkspaceOverride = inspection?.workspaceValue !== undefined
            || inspection?.workspaceFolderValue !== undefined;

        // Older versions only wrote this setting globally. A workspace-scoped value is
        // therefore an explicit user pin even when it happens to equal a legacy default.
        return !hasWorkspaceOverride && containsCliPath(defaultPaths, configuredPath);
    },
    findOnPath: findCliOnPath,
    findAtDefaultPath: findCliAtDefaultPath,
    tryExecute: tryExecuteCli,
    getExecutableCandidates: getCliExecutableCandidates,
    // Legacy discovery only ever wrote the window-wide (Global) setting; scoped writes are
    // not part of current behavior, so the target is intentionally unused here.
    setConfiguredPath: cliPath => setConfiguredCliPath(cliPath),
};

interface ConfiguredCliPathSnapshot {
    configuredPath: string;
    configuredPathIsLegacyDefault: boolean;
    defaultPaths: string[];
    workspaceFolders: readonly vscode.WorkspaceFolder[];
    workspaceFoldersKey: string;
}

interface InFlightCliPathResolution {
    deps: CliPathDependencies;
    configuredPath: string;
    configuredPathIsLegacyDefault: boolean;
    workspaceFoldersKey: string;
    e2eCliPath: string | undefined;
    promise: Promise<CliPathResolutionResult>;
}

interface CliPathScopeState {
    generation: number;
    rejectedConfiguredPath?: string;
    resolvedPathForForwarding?: { configuredPath: string; resolvedPath: string };
    inFlight?: InFlightCliPathResolution;
}

/**
 * Resolves the Aspire CLI path for a given target, keeping mutable resolution state
 * (in-flight probes, configured-path rejection, and the forwarding-fallback path)
 * scoped per-target so a workspace folder's resolution never coalesces with, or
 * suppresses forwarding for, another folder's or the window's resolution.
 *
 * Resolution checks locations in order:
 * 1. E2E runner-provided CLI path
 * 2. User-configured path in VS Code settings (expanding `${workspaceFolder}` tokens)
 * 3. System PATH
 * 4. Default installation directories (bundle and .NET global-tool locations)
 * 5. A still-valid legacy auto-configured path
 *
 * If the CLI is found at a native default installation path but not on PATH,
 * the VS Code setting is updated to use that path. Command shims are discovered
 * on demand instead so an explicit shim setting remains distinguishable. When
 * current discovery supersedes a legacy auto-configured setting with a path we
 * intentionally do not persist, the old setting is cleared so it cannot keep
 * forwarding a different CLI bundle through AspireCliPath.
 *
 * If the CLI is on PATH and a setting was previously auto-configured to a default path,
 * the setting is cleared to prefer PATH.
 */
export class CliPathResolver implements vscode.Disposable {
    private readonly _scopeStates = new Map<string, CliPathScopeState>();
    private readonly _onDidChangeConfiguredCliPathRejectionEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
    private readonly _onDidChangeResolvedCliPathForForwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();
    private readonly _onDidChangeForwardingEmitter = new vscode.EventEmitter<CliPathResolutionTarget>();

    readonly onDidChangeConfiguredCliPathRejection = this._onDidChangeConfiguredCliPathRejectionEmitter.event;
    readonly onDidChangeResolvedCliPathForForwarding = this._onDidChangeResolvedCliPathForForwardingEmitter.event;
    /** Fires whenever either forwarding-relevant event above fires, for consumers that don't care which. */
    readonly onDidChangeForwarding = this._onDidChangeForwardingEmitter.event;

    constructor(private readonly _deps: CliPathDependencies = defaultDependencies) {
    }

    resolve(
        target: CliPathResolutionTarget = windowCliPathTarget,
        depsOverrideForTests?: CliPathDependencies,
    ): Promise<CliPathResolutionResult> {
        const deps = depsOverrideForTests ?? this._deps;
        const scopeKey = getCliPathTargetKey(target);
        let scopeState = this._scopeStates.get(scopeKey);
        if (!scopeState) {
            scopeState = { generation: 0 };
            this._scopeStates.set(scopeKey, scopeState);
        }
        const state = scopeState;

        const configuredPathSnapshot = this.getConfiguredCliPathSnapshot(deps, target);
        const e2eCliPath = process.env.ASPIRE_EXTENSION_E2E_CLI_PATH?.trim();

        // Different test-provided `deps` objects targeting the same scope must never
        // coalesce with each other's in-flight probe, even when their snapshots match.
        if (state.inFlight?.deps === deps
            && state.inFlight.configuredPath === configuredPathSnapshot.configuredPath
            && state.inFlight.configuredPathIsLegacyDefault === configuredPathSnapshot.configuredPathIsLegacyDefault
            && state.inFlight.workspaceFoldersKey === configuredPathSnapshot.workspaceFoldersKey
            && state.inFlight.e2eCliPath === e2eCliPath) {
            return state.inFlight.promise;
        }

        const generation = ++state.generation;
        let writtenConfiguredPathSnapshot: ConfiguredCliPathSnapshot | undefined;
        let promise!: Promise<CliPathResolutionResult>;
        // resolveCore can synchronously publish a configured-path rejection before its first
        // await. Publish state.inFlight first so forwarding listeners that re-enter resolve()
        // coalesce with this probe instead of replacing it and creating a promise cycle.
        promise = Promise.resolve()
            .then(() => this.resolveCore(
                target,
                state,
                deps,
                configuredPathSnapshot,
                e2eCliPath,
                generation,
                snapshot => writtenConfiguredPathSnapshot = snapshot))
            .then(result => {
                // A setting edit or scope change can race a CLI probe. Do not return a result
                // for a configuration snapshot that is no longer current.
                const currentConfiguredPathSnapshot = this.getConfiguredCliPathSnapshot(deps, target);
                if (generation !== state.generation
                    || (!this.areConfiguredCliPathSnapshotsEqual(currentConfiguredPathSnapshot, configuredPathSnapshot)
                    && !this.areConfiguredCliPathSnapshotsEqual(currentConfiguredPathSnapshot, writtenConfiguredPathSnapshot))
                    || process.env.ASPIRE_EXTENSION_E2E_CLI_PATH?.trim() !== e2eCliPath) {
                    return this.resolve(target, depsOverrideForTests);
                }

                // A raw configured path equal to the concrete result (no token/suffix expansion)
                // need not be republished; the setting itself is already forwardable.
                const forwardingCandidate = (result.source === 'default-install' || result.source === 'path' || result.source === 'configured')
                    && isAbsoluteCliPath(result.cliPath)
                    && !areCliPathsEqual(currentConfiguredPathSnapshot.configuredPath, result.cliPath)
                    ? result.cliPath
                    : undefined;

                deps.updateResolvedPathForForwarding?.(currentConfiguredPathSnapshot.configuredPath, forwardingCandidate);
                this.updateResolvedCliPathForForwarding(state, target, currentConfiguredPathSnapshot.configuredPath, forwardingCandidate);
                return result;
            })
            .finally(() => {
                if (state.inFlight?.promise === promise) {
                    state.inFlight = undefined;
                }
            });

        state.inFlight = {
            deps,
            configuredPath: configuredPathSnapshot.configuredPath,
            configuredPathIsLegacyDefault: configuredPathSnapshot.configuredPathIsLegacyDefault,
            workspaceFoldersKey: configuredPathSnapshot.workspaceFoldersKey,
            e2eCliPath,
            promise,
        };
        return promise;
    }

    /**
     * Reports whether CLI resolution for `target` rejected `rawConfiguredPath` and fell back
     * to a different CLI. Such a path must not be forwarded as `AspireCliPath`.
     */
    isConfiguredPathRejectedForForwarding(target: CliPathResolutionTarget, rawConfiguredPath: string): boolean {
        return this._scopeStates.get(getCliPathTargetKey(target))?.rejectedConfiguredPath === rawConfiguredPath;
    }

    /**
     * Returns the effective absolute discovery path resolved for `target`'s current setting
     * snapshot without persisting it as an explicit user configuration value.
     */
    getResolvedCliPathForForwarding(target: CliPathResolutionTarget, rawConfiguredPath: string): string | undefined {
        const resolved = this._scopeStates.get(getCliPathTargetKey(target))?.resolvedPathForForwarding;
        return resolved?.configuredPath === rawConfiguredPath ? resolved.resolvedPath : undefined;
    }

    /** Test seam that clears all per-target resolution state between cases. */
    resetForTests(): void {
        this._scopeStates.clear();
    }

    dispose(): void {
        this._scopeStates.clear();
        this._onDidChangeConfiguredCliPathRejectionEmitter.dispose();
        this._onDidChangeResolvedCliPathForForwardingEmitter.dispose();
        this._onDidChangeForwardingEmitter.dispose();
    }

    private getConfiguredCliPathSnapshot(deps: CliPathDependencies, target: CliPathResolutionTarget): ConfiguredCliPathSnapshot {
        const configuredPath = deps.getConfiguredPath(target);
        const defaultPaths = deps.getDefaultPaths();
        const workspaceFolders = configuredPath.includes('${workspaceFolder')
            ? [...deps.getWorkspaceFolders()]
            : [];
        return {
            configuredPath,
            configuredPathIsLegacyDefault: configuredPath !== ''
                && deps.isConfiguredPathAutoConfigured(configuredPath, defaultPaths, target),
            defaultPaths,
            workspaceFolders,
            workspaceFoldersKey: JSON.stringify(workspaceFolders.map(folder => [folder.name, folder.uri.toString()])),
        };
    }

    private areConfiguredCliPathSnapshotsEqual(
        left: ConfiguredCliPathSnapshot,
        right: ConfiguredCliPathSnapshot | undefined,
    ): boolean {
        return right !== undefined
            && left.configuredPath === right.configuredPath
            && left.configuredPathIsLegacyDefault === right.configuredPathIsLegacyDefault
            && left.workspaceFoldersKey === right.workspaceFoldersKey;
    }

    private updateRejectedConfiguredCliPath(
        state: CliPathScopeState,
        target: CliPathResolutionTarget,
        expectedConfiguredPath: string,
        rejectedPath: string | undefined,
        generation: number,
        deps: CliPathDependencies,
    ): void {
        // CLI probes are asynchronous and can overlap. Only the latest resolution for
        // the setting that is still current may change forwarding state.
        if (generation !== state.generation || deps.getConfiguredPath(target) !== expectedConfiguredPath) {
            return;
        }

        if (state.rejectedConfiguredPath === rejectedPath) {
            return;
        }

        state.rejectedConfiguredPath = rejectedPath;
        this._onDidChangeConfiguredCliPathRejectionEmitter.fire(target);
        this._onDidChangeForwardingEmitter.fire(target);
    }

    private updateResolvedCliPathForForwarding(
        state: CliPathScopeState,
        target: CliPathResolutionTarget,
        configuredPath: string,
        resolvedPath: string | undefined,
    ): void {
        const nextValue = resolvedPath === undefined
            ? undefined
            : { configuredPath, resolvedPath };
        if (state.resolvedPathForForwarding?.configuredPath === nextValue?.configuredPath
            && state.resolvedPathForForwarding?.resolvedPath === nextValue?.resolvedPath) {
            return;
        }

        state.resolvedPathForForwarding = nextValue;
        this._onDidChangeResolvedCliPathForForwardingEmitter.fire(target);
        this._onDidChangeForwardingEmitter.fire(target);
    }

    private async resolveCore(
        target: CliPathResolutionTarget,
        state: CliPathScopeState,
        deps: CliPathDependencies,
        configuredPathSnapshot: ConfiguredCliPathSnapshot,
        e2eCliPath: string | undefined,
        generation: number,
        configuredPathWritten: (snapshot: ConfiguredCliPathSnapshot) => void,
    ): Promise<CliPathResolutionResult> {
        const { configuredPath, configuredPathIsLegacyDefault, defaultPaths, workspaceFolders } = configuredPathSnapshot;
        let expectedConfiguredPathSnapshot = configuredPathSnapshot;

        const updateConfiguredPath = async (value: string): Promise<void> => {
            // Only the window-wide setting has auto-configuration provenance; a workspace-folder
            // target must never write or clear it on another scope's behalf.
            if (target.kind !== 'window') {
                return;
            }

            // Defensive: a tokenized value should never reach here as a legacy/auto-configured
            // snapshot, but never persist or clear a `${...}` variable reference regardless.
            if (configuredPath.includes('${') || expectedConfiguredPathSnapshot.configuredPath.includes('${')) {
                return;
            }

            // Do not overwrite a setting whose value or scope changed while the CLI probe
            // was running. A workspace pin can have the same effective value as the global
            // auto-configured setting, but must still block a stale global write.
            if (generation !== state.generation
                || !this.areConfiguredCliPathSnapshotsEqual(
                this.getConfiguredCliPathSnapshot(deps, target),
                expectedConfiguredPathSnapshot)) {
                return;
            }

            const writtenConfiguredPathSnapshot = {
                configuredPath: value,
                configuredPathIsLegacyDefault: value !== '' && containsCliPath(defaultPaths, value),
                defaultPaths,
                workspaceFolders,
                workspaceFoldersKey: configuredPathSnapshot.workspaceFoldersKey,
            };
            await deps.setConfiguredPath(value, target);
            expectedConfiguredPathSnapshot = writtenConfiguredPathSnapshot;
            configuredPathWritten(writtenConfiguredPathSnapshot);
        };

        if (e2eCliPath) {
            const isValid = await deps.tryExecute(e2eCliPath);
            if (isValid) {
                return { cliPath: e2eCliPath, available: true, source: 'configured' };
            }

            extensionLogOutputChannel.warn(`E2E CLI path is invalid: ${e2eCliPath}`);
        }

        // Check if user has configured a custom path (not one of the defaults)
        if (configuredPath && (!configuredPathIsLegacyDefault || isCommandShimPath(configuredPath))) {
            const expanded = expandConfiguredCliPath(configuredPath, target, workspaceFolders);

            if (expanded.error) {
                // An unresolvable token (unknown workspace folder, ambiguous window scope, or an
                // unsupported variable) has no concrete candidate to probe; do not call tryExecute.
                extensionLogOutputChannel.warn(`Configured CLI path could not be resolved: ${expanded.error}`);
                this.updateRejectedConfiguredCliPath(state, target, configuredPath, configuredPath, generation, deps);
            }
            else {
                // No-token paths return `resolvedPath === undefined`; use the raw value unchanged.
                const effectiveCandidate = expanded.resolvedPath ?? configuredPath;

                if (isAbsoluteCliPath(effectiveCandidate)) {
                    let matchedCandidate: string | undefined;
                    for (const candidate of deps.getExecutableCandidates(effectiveCandidate)) {
                        if (await deps.tryExecute(candidate)) {
                            matchedCandidate = candidate;
                            break;
                        }
                    }

                    if (matchedCandidate !== undefined) {
                        this.updateRejectedConfiguredCliPath(state, target, configuredPath, undefined, generation, deps);
                        return { cliPath: matchedCandidate, available: true, source: 'configured' };
                    }

                    extensionLogOutputChannel.warn(`Configured CLI path is invalid: ${configuredPath}`);
                    // Everything below this point resolves a different CLI. The setting is kept so an
                    // explicit user pin is not silently erased, but it must stop being forwarded as
                    // AspireCliPath, otherwise MSBuild resolves bundle assets from the CLI that failed.
                    extensionLogOutputChannel.warn('Suppressing AspireCliPath forwarding for the rejected configured CLI path');
                    this.updateRejectedConfiguredCliPath(state, target, configuredPath, configuredPath, generation, deps);
                    // Continue to check other locations
                }
                else {
                    // A plain relative path or bare command name is not a valid configured CLI
                    // candidate; only PATH lookup resolves bare command names.
                    extensionLogOutputChannel.warn(`Configured CLI path must be absolute: ${configuredPath}`);
                    this.updateRejectedConfiguredCliPath(state, target, configuredPath, configuredPath, generation, deps);
                }
            }
        }
        else {
            this.updateRejectedConfiguredCliPath(state, target, configuredPath, undefined, generation, deps);
        }

        // 2. Check if CLI is on PATH
        const cliOnPath = await deps.findOnPath();
        if (cliOnPath) {
            // If we previously auto-set the path to a default install location, clear it
            // since PATH is now working
            if (configuredPathIsLegacyDefault) {
                extensionLogOutputChannel.info('Clearing aspireCliExecutablePath setting since CLI is on PATH');
                await updateConfiguredPath('');
            }

            return { cliPath: cliOnPath, available: true, source: 'path' };
        }

        // 3. Check default installation paths (~/.aspire/bin first, then ~/.dotnet/tools)
        const foundPath = await deps.findAtDefaultPath();
        if (foundPath) {
            // The setting does not record who wrote it, so persist only paths that
            // older versions already recognized as automatic defaults. Newly added
            // discovery locations remain distinguishable from explicit user pins.
            if (!areCliPathsEqual(configuredPath, foundPath)) {
                if ((configuredPath === '' || configuredPathIsLegacyDefault)
                    && containsCliPath(defaultPaths, foundPath)
                    && !isCommandShimPath(foundPath)) {
                    extensionLogOutputChannel.info('Updating aspireCliExecutablePath setting to use default install location');
                    await updateConfiguredPath(foundPath);
                }
                else if (configuredPathIsLegacyDefault) {
                    // The extension will execute foundPath, while the configured setting is independently
                    // forwarded as AspireCliPath for MSBuild bundle resolution. Leaving a legacy setting here
                    // could therefore run one CLI while stamping AppHosts with another CLI's bundle paths.
                    extensionLogOutputChannel.info('Clearing superseded auto-configured aspireCliExecutablePath setting');
                    await updateConfiguredPath('');
                }
            }

            return { cliPath: foundPath, available: true, source: 'default-install' };
        }

        // A legacy extension version may have persisted a default path that is no
        // longer part of current discovery (for example after DOTNET_CLI_HOME is
        // redirected). Keep it as the final fallback without letting it outrank a
        // working PATH or current install location.
        if (configuredPathIsLegacyDefault && await deps.tryExecute(configuredPath)) {
            return { cliPath: configuredPath, available: true, source: 'default-install' };
        }

        // CLI not found anywhere
        return { cliPath: 'aspire', available: false, source: 'not-found' };
    }
}

/** Shared long-lived resolver instance used by all window-scoped compatibility exports below. */
export const cliPathResolver = new CliPathResolver();

const legacyConfiguredCliPathRejectionEmitter = new vscode.EventEmitter<void>();
const legacyResolvedCliPathForForwardingEmitter = new vscode.EventEmitter<void>();
cliPathResolver.onDidChangeConfiguredCliPathRejection(() => legacyConfiguredCliPathRejectionEmitter.fire());
cliPathResolver.onDidChangeResolvedCliPathForForwarding(() => legacyResolvedCliPathForForwardingEmitter.fire());

/**
 * Fires when CLI resolution changes whether the configured path may be forwarded
 * through `AspireCliPath`, for any target. Window-scoped consumers that have not
 * migrated to an explicit `CliPathResolutionTarget` can keep using this event.
 */
export const onDidChangeConfiguredCliPathRejection = legacyConfiguredCliPathRejectionEmitter.event;
export const onDidChangeResolvedCliPathForForwarding = legacyResolvedCliPathForForwardingEmitter.event;

/**
 * Resolves the Aspire CLI path for the given resolution target using the shared
 * `cliPathResolver`.
 *
 * The single-argument `deps` overload is a temporary test seam preserved for existing
 * callers; it resolves against `windowCliPathTarget` with the supplied dependencies. The two
 * shapes are discriminated by the `kind` property, which only a `CliPathResolutionTarget` has.
 */
export function resolveCliPath(
    targetOrDeps?: CliPathResolutionTarget | CliPathDependencies,
): Promise<CliPathResolutionResult> {
    if (targetOrDeps !== undefined && 'kind' in targetOrDeps) {
        return cliPathResolver.resolve(targetOrDeps);
    }

    return cliPathResolver.resolve(windowCliPathTarget, targetOrDeps);
}

/**
 * Reports whether CLI resolution rejected this configured path and fell back to a
 * different CLI. Such a path must not be forwarded as `AspireCliPath`.
 */
export function isConfiguredCliPathRejectedForForwarding(
    configuredPath: string,
    target: CliPathResolutionTarget = windowCliPathTarget,
): boolean {
    return cliPathResolver.isConfiguredPathRejectedForForwarding(target, configuredPath);
}

/**
 * Returns the effective absolute discovery path resolved for the current setting
 * snapshot without persisting it as an explicit user configuration value.
 */
export function getResolvedCliPathForForwarding(
    configuredPath: string,
    target: CliPathResolutionTarget = windowCliPathTarget,
): string | undefined {
    return cliPathResolver.getResolvedCliPathForForwarding(target, configuredPath);
}

/** Test seam that clears the rejected-configured-path and forwarding state between cases. */
export function resetRejectedConfiguredCliPathForForwarding(): void {
    cliPathResolver.resetForTests();
}
