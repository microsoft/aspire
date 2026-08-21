import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import {
    CliPathResolver,
    getConfiguredCliPath,
    getResolvedCliPathForForwarding,
    isConfiguredCliPathRejectedForForwarding,
    isFullyQualifiedWindowsPath,
    onDidChangeConfiguredCliPathRejection,
    onDidChangeResolvedCliPathForForwarding,
    resolveCliPath,
} from './cliPath';
import {
    CliPathResolutionTarget,
    getCliPathTargetKey,
    windowCliPathTarget,
    workspaceFolderCliPathTarget,
} from './cliPathVariables';
import { extensionLogOutputChannel } from './logging';
import { aspireCliPathEnvironmentDescription } from '../loc/strings';

/**
 * Name of the MSBuild property/env var read by the Aspire SDK's
 * `ResolveAspireCliBundle` task (see `src/Aspire.Hosting.Tasks/ResolveAspireCliBundle.cs`).
 * When set to an absolute path to an `aspire` executable, MSBuild resolves the
 * bundle layout (managed/, dcp/, terminal-host binary) relative to that CLI
 * instead of probing PATH. The `[AssemblyMetadata("aspireterminalhostpath", …)]`
 * / `aspiredashboardpath` attributes baked into the built AppHost then point at
 * the configured CLI's bundle.
 */
export const ASPIRE_CLI_PATH_ENV_VAR = 'AspireCliPath';
export const ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR = 'ASPIRE_VSCODE_EXTENSION_VERSION';
export const ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR = 'ASPIRE_VSCODE_EXTENSION_CHANNEL';
export const ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR = 'ASPIRE_VSCODE_EXTENSION_SOURCE';

export type AspireExtensionEnvironment = Readonly<{
    [ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR]: string;
    [ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR]: 'stable' | 'prerelease';
    [ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR]: 'microsoft-marketplace' | 'other';
}>;

export interface VsCodeProductIdentity {
    appName: string;
    uriScheme: string;
    extensionGalleryServiceUrl?: unknown;
}

/**
 * Configuration key under the `aspire` namespace whose value the user-facing
 * "Aspire Cli Executable Path" setting writes into.
 */
const ASPIRE_CLI_EXECUTABLE_PATH_SETTING = 'aspireCliExecutablePath';

/**
 * Wraps the platform `EnvironmentVariableCollection` API so tests can drive the
 * synchronizer without instantiating a real VS Code extension context.
 */
export interface CliPathEnvironmentCollection {
    description: string | vscode.MarkdownString | undefined;
    replace(variable: string, value: string): void;
    delete(variable: string): void;
}

export interface ResolvedCliPathDependencies {
    isAbsolute: (cliPath: string) => boolean;
    fileExists: (cliPath: string) => boolean;
    realpath: (cliPath: string) => string | undefined;
}

export interface ForwardableCliPathDependencies extends ResolvedCliPathDependencies {
    isRejectedForForwarding: (cliPath: string) => boolean;
}

interface ExtensionPackageJson {
    version?: unknown;
    preRelease?: unknown;
}

/**
 * Test seam: the synchronizer asks its dependencies for both configured and
 * unpersisted resolved paths so unit tests can avoid mocking `vscode.workspace`.
 */
export interface CliPathEnvironmentDependencies extends ForwardableCliPathDependencies {
    getConfiguredPath: () => string;
    getResolvedPath: (configuredPath: string) => string | undefined;
    log?: (message: string) => void;
}

const defaultResolvedCliPathDeps: ResolvedCliPathDependencies = {
    isAbsolute: cliPath => process.platform === 'win32'
        ? isFullyQualifiedWindowsPath(cliPath)
        : path.isAbsolute(cliPath),
    fileExists: fileExists,
    realpath: realpath,
};

const defaultForwardableCliPathDeps: ForwardableCliPathDependencies = {
    ...defaultResolvedCliPathDeps,
    isRejectedForForwarding: isConfiguredCliPathRejectedForForwarding,
};

const defaultDeps: CliPathEnvironmentDependencies = {
    getConfiguredPath: () => getConfiguredCliPath(),
    getResolvedPath: getResolvedCliPathForForwarding,
    ...defaultForwardableCliPathDeps,
    log: (message) => extensionLogOutputChannel.info(message),
};

function isForwardableResolvedCliPath(cliPath: string, deps: ResolvedCliPathDependencies): boolean {
    return cliPath.length > 0
        && deps.isAbsolute(cliPath)
        && deps.fileExists(cliPath)
        && !isUnbundledFrameworkDependentCliPath(cliPath, deps)
        && !isResolvedUnbundledFrameworkDependentCliPath(cliPath, deps);
}

export function isForwardableAspireCliPath(
    configuredPath: string,
    deps: ForwardableCliPathDependencies = defaultForwardableCliPathDeps,
): boolean {
    // CLI resolution rejected this path and is running a different CLI, so forwarding it
    // would make ResolveAspireCliBundle stamp bundle paths from a CLI that never ran.
    return isForwardableResolvedCliPath(configuredPath, deps)
        && !deps.isRejectedForForwarding(configuredPath);
}

export function getForwardableAspireCliPath(deps: CliPathEnvironmentDependencies = defaultDeps): string | undefined {
    const configuredPath = deps.getConfiguredPath();
    if (isForwardableAspireCliPath(configuredPath, deps)) {
        return configuredPath;
    }

    const resolvedPath = deps.getResolvedPath(configuredPath);
    return resolvedPath !== undefined && isForwardableAspireCliPath(resolvedPath, deps)
        ? resolvedPath
        : undefined;
}

/**
 * Validates a concrete CLI path that has already been chosen and launched (for example, the
 * exact executable `spawnCliProcess` invokes) so it can be forwarded to the child's environment.
 * Unlike `getForwardableAspireCliPath`, this intentionally does not consult configured-path
 * rejection state: that state describes a raw setting/target combination, but the caller here
 * already selected and ran this exact executable, so rejection of a *different* configured value
 * must not suppress it.
 */
export function getForwardableResolvedAspireCliPath(
    cliPath: string | undefined,
    deps: ResolvedCliPathDependencies = defaultResolvedCliPathDeps,
): string | undefined {
    return cliPath !== undefined && isForwardableResolvedCliPath(cliPath, deps)
        ? cliPath
        : undefined;
}

export function createAspireCliPathProcessEnvironment(
    baseEnv: NodeJS.ProcessEnv = process.env,
    deps: CliPathEnvironmentDependencies = defaultDeps,
): NodeJS.ProcessEnv {
    const forwardablePath = getForwardableAspireCliPath(deps);
    if (forwardablePath === undefined) {
        return baseEnv;
    }

    return {
        ...baseEnv,
        [ASPIRE_CLI_PATH_ENV_VAR]: forwardablePath,
    };
}

/**
 * Builds a child process environment that forwards exactly the concrete, already-resolved CLI
 * path a caller is about to invoke (or run against a project via `dotnet build`/`msbuild`/
 * `dotnet run-api`), rather than re-reading the configured setting.
 *
 * Unlike {@link createAspireCliPathProcessEnvironment}, this does not consult configured-path
 * rejection state: the caller already resolved and is about to run this exact executable, so a
 * *different* configured value being rejected must not suppress it. When the resolved path is
 * not forwardable (relative, missing, or an unbundled framework-dependent build), any stale
 * `AspireCliPath` already present in `baseEnv` is removed instead of being left to point at a
 * different CLI than the one actually running.
 */
export function createResolvedAspireCliPathProcessEnvironment(
    resolvedCliPath: string | undefined,
    baseEnv: NodeJS.ProcessEnv = process.env,
    deps: ResolvedCliPathDependencies = defaultResolvedCliPathDeps,
): NodeJS.ProcessEnv {
    const env = { ...baseEnv };
    const forwardablePath = getForwardableResolvedAspireCliPath(resolvedCliPath, deps);
    if (forwardablePath === undefined) {
        deleteEnvVarCaseInsensitive(env, ASPIRE_CLI_PATH_ENV_VAR);
        return env;
    }

    deleteEnvVarCaseInsensitive(env, ASPIRE_CLI_PATH_ENV_VAR);
    env[ASPIRE_CLI_PATH_ENV_VAR] = forwardablePath;
    return env;
}

// Windows environment variable names are case-insensitive, so a stale `AspireCliPath` (or
// `ASPIRECLIPATH`, etc.) already in `baseEnv` must be removed regardless of casing, matching the
// case-insensitive override semantics `mergeCliSpawnEnvironment` applies to spawned CLI processes.
function deleteEnvVarCaseInsensitive(env: NodeJS.ProcessEnv, name: string): void {
    if (process.platform !== 'win32') {
        delete env[name];
        return;
    }

    const lowerName = name.toLowerCase();
    for (const key of Object.keys(env)) {
        if (key.toLowerCase() === lowerName) {
            delete env[key];
        }
    }
}

function fileExists(filePath: string): boolean {
    try {
        return fs.statSync(filePath).isFile();
    }
    catch {
        return false;
    }
}

function realpath(filePath: string): string | undefined {
    try {
        return fs.realpathSync.native(filePath);
    }
    catch {
        return undefined;
    }
}

function isUnbundledFrameworkDependentCliPath(configuredPath: string, deps: ResolvedCliPathDependencies): boolean {
    const cliDirectory = path.dirname(configuredPath);
    const cliAssemblyPath = path.join(cliDirectory, 'aspire.dll');

    if (!deps.fileExists(cliAssemblyPath)) {
        return false;
    }

    // Inner-loop `dotnet build` outputs place the apphost next to aspire.dll,
    // but they do not contain an embedded bundle or a sidecar that identifies an
    // extraction root. Forwarding those paths makes MSBuild resolve whatever
    // unrelated ASPIRE_HOME bundle happens to exist and stamp stale metadata.
    // Installed layouts either have a sidecar or an adjacent bundle layout that
    // `ResolveAspireCliBundle` can bind to the selected CLI path.
    return !hasInstallSidecar(cliDirectory, deps) && !hasAdjacentBundleLayout(cliDirectory, deps);
}

function isResolvedUnbundledFrameworkDependentCliPath(configuredPath: string, deps: ResolvedCliPathDependencies): boolean {
    const resolvedPath = deps.realpath(configuredPath);
    if (resolvedPath === undefined || resolvedPath === configuredPath || !deps.isAbsolute(resolvedPath) || !deps.fileExists(resolvedPath)) {
        return false;
    }

    return isUnbundledFrameworkDependentCliPath(resolvedPath, deps);
}

function hasInstallSidecar(cliDirectory: string, deps: ResolvedCliPathDependencies): boolean {
    return deps.fileExists(path.join(cliDirectory, '.aspire-install.json'));
}

function hasAdjacentBundleLayout(cliDirectory: string, deps: ResolvedCliPathDependencies): boolean {
    return hasBundleRoot(cliDirectory, deps)
        || hasBundleRoot(path.join(cliDirectory, 'bundle'), deps);
}

function hasBundleRoot(bundleRoot: string, deps: ResolvedCliPathDependencies): boolean {
    return (deps.fileExists(path.join(bundleRoot, 'dcp', 'dcp')) || deps.fileExists(path.join(bundleRoot, 'dcp', 'dcp.exe')))
        && (deps.fileExists(path.join(bundleRoot, 'managed', 'aspire-managed')) || deps.fileExists(path.join(bundleRoot, 'managed', 'aspire-managed.exe')));
}

export interface CliPathEnvironmentSynchronizerDependencies {
    getWorkspaceFolders: () => readonly vscode.WorkspaceFolder[];
    getForwardablePath: (cliPath: string | undefined) => string | undefined;
    onDidChangeConfiguration: vscode.Event<vscode.ConfigurationChangeEvent>;
    onDidChangeWorkspaceFolders: vscode.Event<vscode.WorkspaceFoldersChangeEvent>;
    onDidGrantWorkspaceTrust: vscode.Event<void>;
}

const defaultSynchronizerDependencies: CliPathEnvironmentSynchronizerDependencies = {
    getWorkspaceFolders: () => vscode.workspace.workspaceFolders ?? [],
    getForwardablePath: getForwardableResolvedAspireCliPath,
    onDidChangeConfiguration: vscode.workspace.onDidChangeConfiguration,
    onDidChangeWorkspaceFolders: vscode.workspace.onDidChangeWorkspaceFolders,
    onDidGrantWorkspaceTrust: vscode.workspace.onDidGrantWorkspaceTrust,
};

/**
 * Keeps VS Code's window and workspace-folder environment collections aligned with the
 * exact CLI each target resolves. Folder collections are retained until their folder is
 * removed so stale persisted mutations can be explicitly cleared.
 */
export class CliPathEnvironmentSynchronizer implements vscode.Disposable {
    private readonly _scopedCollections = new Map<string, vscode.EnvironmentVariableCollection>();
    private readonly _forwardedPaths = new Map<string, string | undefined>();
    private readonly _activeFolderTargets = new Map<string, CliPathResolutionTarget>();
    private readonly _syncGenerations = new Map<string, number>();
    private readonly _disposable: vscode.Disposable;
    private _disposed = false;

    constructor(
        private readonly _globalCollection: vscode.GlobalEnvironmentVariableCollection,
        private readonly _resolver: CliPathResolver,
        subscriptions: vscode.Disposable[],
        private readonly _onForwardedPathChanged?: (
            target: CliPathResolutionTarget,
            previousPath: string | undefined,
            currentPath: string | undefined,
        ) => void,
        private readonly _deps: CliPathEnvironmentSynchronizerDependencies = defaultSynchronizerDependencies,
    ) {
        this._disposable = vscode.Disposable.from(
            this._deps.onDidChangeConfiguration(event => this.handleConfigurationChange(event)),
            this._resolver.onDidChangeForwarding(target => this.syncTargetInBackground(target)),
            this._deps.onDidChangeWorkspaceFolders(event => this.handleWorkspaceFoldersChange(event)),
            this._deps.onDidGrantWorkspaceTrust(() => this.initializeInBackground()));
        subscriptions.push(this._disposable);
    }

    async initialize(workspaceFolders: readonly vscode.WorkspaceFolder[] = this._deps.getWorkspaceFolders()): Promise<void> {
        const nextFolderKeys = new Set(workspaceFolders.map(folder => getCliPathTargetKey(workspaceFolderCliPathTarget(folder))));
        for (const [key, target] of this._activeFolderTargets) {
            if (!nextFolderKeys.has(key)) {
                this.removeFolderTarget(target);
            }
        }

        const folderTargets = workspaceFolders.map(folder => workspaceFolderCliPathTarget(folder));
        for (const target of folderTargets) {
            this._activeFolderTargets.set(getCliPathTargetKey(target), target);
        }
        this.clearGlobalPathForFolders();

        await Promise.all([
            this.syncTarget(windowCliPathTarget),
            ...folderTargets.map(target => this.syncTarget(target)),
        ]);
    }

    async syncTarget(target: CliPathResolutionTarget): Promise<void> {
        const key = getCliPathTargetKey(target);
        if (this._disposed || (target.kind === 'workspaceFolder' && !this._activeFolderTargets.has(key))) {
            return;
        }

        const generation = (this._syncGenerations.get(key) ?? 0) + 1;
        this._syncGenerations.set(key, generation);
        const result = await this._resolver.resolve(target);
        if (this._disposed
            || this._syncGenerations.get(key) !== generation
            || (target.kind === 'workspaceFolder' && !this._activeFolderTargets.has(key))) {
            return;
        }

        const collection = target.kind === 'window'
            ? this._globalCollection
            : this._globalCollection.getScoped({ workspaceFolder: target.workspaceFolder });
        if (target.kind === 'workspaceFolder') {
            this._scopedCollections.set(key, collection);
        }

        const resolvedPath = this._deps.getForwardablePath(result.available ? result.cliPath : undefined);
        // An unscoped mutation is inherited by every workspace folder and cannot be masked by
        // deleting a folder-scoped mutator. Use it only when no folder scopes exist.
        const nextPath = target.kind === 'window' && this._activeFolderTargets.size > 0
            ? undefined
            : resolvedPath;
        const hadPreviousPath = this._forwardedPaths.has(key);
        const previousPath = this._forwardedPaths.get(key);
        if (nextPath === undefined) {
            collection.description = undefined;
            collection.delete(ASPIRE_CLI_PATH_ENV_VAR);
        }
        else {
            collection.description = aspireCliPathEnvironmentDescription;
            collection.replace(ASPIRE_CLI_PATH_ENV_VAR, nextPath);
        }
        this._forwardedPaths.set(key, nextPath);

        if (hadPreviousPath && previousPath !== nextPath) {
            this._onForwardedPathChanged?.(target, previousPath, nextPath);
        }
    }

    dispose(): void {
        this._disposed = true;
        this._disposable.dispose();
        this._scopedCollections.clear();
        this._forwardedPaths.clear();
        this._activeFolderTargets.clear();
        this._syncGenerations.clear();
    }

    private handleConfigurationChange(event: vscode.ConfigurationChangeEvent): void {
        const section = `aspire.${ASPIRE_CLI_EXECUTABLE_PATH_SETTING}`;
        if (!event.affectsConfiguration(section)) {
            return;
        }

        this.syncTargetInBackground(windowCliPathTarget);
        for (const target of this._activeFolderTargets.values()) {
            if (target.kind === 'workspaceFolder' && event.affectsConfiguration(section, target.workspaceFolder.uri)) {
                this.syncTargetInBackground(target);
            }
        }
    }

    private handleWorkspaceFoldersChange(event: vscode.WorkspaceFoldersChangeEvent): void {
        for (const folder of event.removed) {
            this.removeFolderTarget(workspaceFolderCliPathTarget(folder));
        }
        const addedTargets = event.added.map(folder => workspaceFolderCliPathTarget(folder));
        for (const target of addedTargets) {
            this._activeFolderTargets.set(getCliPathTargetKey(target), target);
        }
        this.clearGlobalPathForFolders();

        // A folder setting can reference any other folder through ${workspaceFolder:name},
        // so additions and removals can change every active folder's effective executable.
        for (const target of this._activeFolderTargets.values()) {
            this.syncTargetInBackground(target);
        }

        // An unqualified ${workspaceFolder} window setting changes meaning as folders are
        // added or removed, even when no configuration value itself changed.
        this.syncTargetInBackground(windowCliPathTarget);
    }

    private clearGlobalPathForFolders(): void {
        if (this._activeFolderTargets.size === 0) {
            return;
        }

        this._globalCollection.description = undefined;
        this._globalCollection.delete(ASPIRE_CLI_PATH_ENV_VAR);

        const key = getCliPathTargetKey(windowCliPathTarget);
        const hadPreviousPath = this._forwardedPaths.has(key);
        const previousPath = this._forwardedPaths.get(key);
        this._forwardedPaths.set(key, undefined);
        if (hadPreviousPath && previousPath !== undefined) {
            this._onForwardedPathChanged?.(windowCliPathTarget, previousPath, undefined);
        }
    }

    private removeFolderTarget(target: CliPathResolutionTarget): void {
        if (target.kind !== 'workspaceFolder') {
            return;
        }

        const key = getCliPathTargetKey(target);
        this._activeFolderTargets.delete(key);
        this._syncGenerations.set(key, (this._syncGenerations.get(key) ?? 0) + 1);

        // getScoped is stable for a folder scope and also reaches a mutation restored by VS Code
        // before this activation's first asynchronous resolution completed.
        const collection = this._globalCollection.getScoped({ workspaceFolder: target.workspaceFolder });
        collection.description = undefined;
        collection.delete(ASPIRE_CLI_PATH_ENV_VAR);
        this._scopedCollections.delete(key);

        const hadPreviousPath = this._forwardedPaths.has(key);
        const previousPath = this._forwardedPaths.get(key);
        this._forwardedPaths.delete(key);
        if (hadPreviousPath && previousPath !== undefined) {
            this._onForwardedPathChanged?.(target, previousPath, undefined);
        }
    }

    private syncTargetInBackground(target: CliPathResolutionTarget): void {
        void this.syncTarget(target).catch(error => {
            extensionLogOutputChannel.warn(`Aspire CLI path environment synchronization failed: ${String(error)}`);
        });
    }

    private initializeInBackground(): void {
        void this.initialize().catch(error => {
            extensionLogOutputChannel.warn(`Aspire CLI path environment initialization failed: ${String(error)}`);
        });
    }
}

/**
 * Applies the configured CLI path, or the effective unpersisted fallback, to
 * the supplied environment variable collection. Called at activation and when
 * configuration or resolution state changes so subsequently created terminals
 * and task processes use the same CLI installation as the extension.
 *
 * Relative values and the bare on-PATH `aspire` fallback are not propagated because
 * they would either fail `ResolveAspireCliBundle` or be ambiguous. An absolute
 * discovered path can be contributed without persisting it as a user setting.
 *
 * Returns the value that was applied (or `undefined` when the variable was
 * cleared) so the caller — and tests — can verify the decision without poking
 * at the collection internals.
 */
export function syncAspireCliPathEnvironment(
    collection: CliPathEnvironmentCollection,
    deps: CliPathEnvironmentDependencies = defaultDeps,
): string | undefined {
    const configuredPath = deps.getConfiguredPath();
    const forwardablePath = getForwardableAspireCliPath({
        ...deps,
        getConfiguredPath: () => configuredPath,
    });

    // Only forward paths that `ResolveAspireCliBundle` can consume. Relative,
    // shell-resolved, or stale absolute values fail the task's File.Exists guard
    // and make it stop before its PATH fallback logic runs.
    if (forwardablePath === undefined) {
        collection.description = undefined;
        collection.delete(ASPIRE_CLI_PATH_ENV_VAR);
        deps.log?.(`Not forwarding ${ASPIRE_CLI_PATH_ENV_VAR}: no resolved CLI path with a bundle is available (configured: ${configuredPath || '(empty)'}).`);
        return undefined;
    }

    collection.description = aspireCliPathEnvironmentDescription;
    collection.replace(ASPIRE_CLI_PATH_ENV_VAR, forwardablePath);
    deps.log?.(`Forwarding ${ASPIRE_CLI_PATH_ENV_VAR}=${forwardablePath} to terminals, tasks, and debug processes.`);
    return forwardablePath;
}

/**
 * Derives the running extension's version, release channel, and editor source once so every
 * child-process boundary forwards the same identity. `aspire doctor` relies on the active
 * extension host because several desktop and remote extension roots can coexist on disk.
 */
export function getAspireExtensionEnvironment(
    packageJson: ExtensionPackageJson | undefined,
    productIdentity: VsCodeProductIdentity,
): AspireExtensionEnvironment | undefined {
    const trimmedVersion = typeof packageJson?.version === 'string'
        ? packageJson.version.trim()
        : undefined;
    if (trimmedVersion === undefined || trimmedVersion.length === 0) {
        return undefined;
    }

    // Marketplace installs synthesize packageJSON.preRelease, but VS Code reports false for a
    // pre-release VSIX installed from disk. Webpack replaces this package-time value in the
    // extension bundle so the artifact retains its channel regardless of how it was installed.
    const isPreRelease = packageJson?.preRelease === true
        || process.env.ASPIRE_VSCODE_EXTENSION_PACKAGE_PRERELEASE === 'true';

    return {
        [ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR]: trimmedVersion,
        [ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR]: isPreRelease ? 'prerelease' : 'stable',
        [ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR]: isMicrosoftVsCodeProduct(productIdentity)
            ? 'microsoft-marketplace'
            : 'other',
    };
}

function isMicrosoftVsCodeProduct(productIdentity: VsCodeProductIdentity): boolean {
    const appName = productIdentity.appName.trim();
    const uriScheme = productIdentity.uriScheme.trim();

    // VS Code uses raw JavaScript truthiness to decide whether a configured gallery replaces the
    // product's default Marketplace manifest after restart. Match that behavior even when manually
    // supplied settings do not conform to the declared string schema.
    // See https://github.com/microsoft/vscode/blob/main/src/vs/workbench/services/extensionManagement/electron-browser/extensionGalleryManifestService.ts.
    if (productIdentity.extensionGalleryServiceUrl) {
        return false;
    }

    // Require the matching Microsoft product name and URI scheme so forks and side-loaded
    // Code - OSS builds do not direct users to the Microsoft Marketplace.
    return appName === 'Visual Studio Code' && uriScheme === 'vscode'
        || appName === 'Visual Studio Code - Insiders' && uriScheme === 'vscode-insiders';
}

export function overlayAspireExtensionEnvironment(
    target: Record<string, string | undefined>,
    extensionEnvironment: AspireExtensionEnvironment | undefined,
    platform: NodeJS.Platform = process.platform,
): void {
    if (extensionEnvironment === undefined) {
        return;
    }

    for (const [name, value] of Object.entries(extensionEnvironment)) {
        if (platform === 'win32') {
            // Windows environment variables are case-insensitive, and Node keeps only one
            // casing when spawning. Remove stale aliases before writing the authoritative identity.
            for (const existingName of Object.keys(target)) {
                if (existingName !== name && existingName.toLowerCase() === name.toLowerCase()) {
                    delete target[existingName];
                }
            }
        }

        target[name] = value;
    }
}

/**
 * Contributes the package-derived identity to terminals and tasks created by VS Code.
 */
export function syncAspireExtensionEnvironment(
    collection: CliPathEnvironmentCollection,
    extensionEnvironment: AspireExtensionEnvironment | undefined,
): void {
    if (extensionEnvironment === undefined) {
        collection.delete(ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR);
        collection.delete(ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR);
        collection.delete(ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR);
        return;
    }

    collection.replace(
        ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR,
        extensionEnvironment[ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR]);
    collection.replace(
        ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR,
        extensionEnvironment[ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR]);
    collection.replace(
        ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR,
        extensionEnvironment[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR]);
}

/**
 * Wires `syncAspireCliPathEnvironment` into the extension lifecycle: applies the
 * current setting once and re-applies whenever configuration or CLI validation
 * changes whether the path can be forwarded.
 *
 * The returned disposable removes the configuration listener but does *not*
 * clear `EnvironmentVariableCollection` itself — VS Code preserves contributed
 * variables across reloads, so the next activation re-syncs them with the
 * up-to-date setting value rather than briefly clearing them and re-adding.
 */
export function registerCliPathEnvironmentSync(
    collection: CliPathEnvironmentCollection,
    subscriptions: vscode.Disposable[],
    deps: CliPathEnvironmentDependencies = defaultDeps,
    onForwardedPathChanged?: (
        previousPath: string | undefined,
        currentPath: string | undefined,
    ) => void,
): vscode.Disposable {
    let forwardedPath = syncAspireCliPathEnvironment(collection, deps);

    const syncForwardedPath = () => {
        const previousPath = forwardedPath;
        forwardedPath = syncAspireCliPathEnvironment(collection, deps);
        if (previousPath !== forwardedPath) {
            onForwardedPathChanged?.(previousPath, forwardedPath);
        }
    };

    const configurationDisposable = vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration(`aspire.${ASPIRE_CLI_EXECUTABLE_PATH_SETTING}`)) {
            syncForwardedPath();
        }
    });
    const rejectionDisposable = onDidChangeConfiguredCliPathRejection(syncForwardedPath);
    const resolvedPathDisposable = onDidChangeResolvedCliPathForForwarding(syncForwardedPath);
    const disposable = vscode.Disposable.from(configurationDisposable, rejectionDisposable, resolvedPathDisposable);

    subscriptions.push(disposable);
    return disposable;
}

/**
 * Registers CLI path forwarding immediately, then resolves the initial CLI path
 * so activation can wait until any unpersisted fallback has been contributed.
 */
export async function initializeCliPathEnvironmentSync(
    collection: CliPathEnvironmentCollection,
    subscriptions: vscode.Disposable[],
    deps: CliPathEnvironmentDependencies = defaultDeps,
    onForwardedPathChanged?: (
        previousPath: string | undefined,
        currentPath: string | undefined,
    ) => void,
    resolvePath: () => Promise<unknown> = resolveCliPath,
): Promise<void> {
    registerCliPathEnvironmentSync(collection, subscriptions, deps, onForwardedPathChanged);
    await resolvePath();
}
