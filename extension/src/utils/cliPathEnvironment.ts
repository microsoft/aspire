import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import { getConfiguredCliPath } from './cliPath';
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

export interface ForwardableCliPathDependencies {
    isAbsolute: (cliPath: string) => boolean;
    fileExists: (cliPath: string) => boolean;
    realpath: (cliPath: string) => string | undefined;
}

/**
 * Test seam: the synchronizer asks the collection (not vscode.workspace) for the
 * current configured CLI path so unit tests can avoid mocking `vscode.workspace`.
 */
export interface CliPathEnvironmentDependencies extends ForwardableCliPathDependencies {
    getConfiguredPath: () => string;
    log?: (message: string) => void;
}

const defaultForwardableCliPathDeps: ForwardableCliPathDependencies = {
    isAbsolute: path.isAbsolute,
    fileExists: fileExists,
    realpath: realpath,
};

const defaultDeps: CliPathEnvironmentDependencies = {
    getConfiguredPath: () => getConfiguredCliPath(),
    ...defaultForwardableCliPathDeps,
    log: (message) => extensionLogOutputChannel.info(message),
};

export function isForwardableAspireCliPath(
    configuredPath: string,
    deps: ForwardableCliPathDependencies = defaultForwardableCliPathDeps,
): boolean {
    return configuredPath.length > 0
        && deps.isAbsolute(configuredPath)
        && deps.fileExists(configuredPath)
        && !isUnbundledFrameworkDependentCliPath(configuredPath, deps)
        && !isResolvedUnbundledFrameworkDependentCliPath(configuredPath, deps);
}

export function getForwardableAspireCliPath(deps: CliPathEnvironmentDependencies = defaultDeps): string | undefined {
    const configuredPath = deps.getConfiguredPath();
    return isForwardableAspireCliPath(configuredPath, deps) ? configuredPath : undefined;
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

function isUnbundledFrameworkDependentCliPath(configuredPath: string, deps: ForwardableCliPathDependencies): boolean {
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

function isResolvedUnbundledFrameworkDependentCliPath(configuredPath: string, deps: ForwardableCliPathDependencies): boolean {
    const resolvedPath = deps.realpath(configuredPath);
    if (resolvedPath === undefined || resolvedPath === configuredPath || !deps.isAbsolute(resolvedPath) || !deps.fileExists(resolvedPath)) {
        return false;
    }

    return isUnbundledFrameworkDependentCliPath(resolvedPath, deps);
}

function hasInstallSidecar(cliDirectory: string, deps: ForwardableCliPathDependencies): boolean {
    return deps.fileExists(path.join(cliDirectory, '.aspire-install.json'));
}

function hasAdjacentBundleLayout(cliDirectory: string, deps: ForwardableCliPathDependencies): boolean {
    return hasBundleRoot(cliDirectory, deps)
        || hasBundleRoot(path.join(cliDirectory, 'bundle'), deps);
}

function hasBundleRoot(bundleRoot: string, deps: ForwardableCliPathDependencies): boolean {
    return (deps.fileExists(path.join(bundleRoot, 'dcp', 'dcp')) || deps.fileExists(path.join(bundleRoot, 'dcp', 'dcp.exe')))
        && (deps.fileExists(path.join(bundleRoot, 'managed', 'aspire-managed')) || deps.fileExists(path.join(bundleRoot, 'managed', 'aspire-managed.exe')));
}

/**
 * Applies the current value of `aspire.aspireCliExecutablePath` to the supplied
 * environment variable collection. Called both at activation and from a
 * configuration-change listener so user edits to the setting take effect for
 * any subsequently created terminals or task processes.
 *
 * The contributed variable is cleared when the configured path is empty or not
 * an absolute path. Relative values and the on-PATH `aspire` fallback would
 * either fail `ResolveAspireCliBundle` (which logs a warning and returns no
 * outputs) or be ambiguous, so propagating them would only add noise.
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
        deps.log?.(`Not forwarding ${ASPIRE_CLI_PATH_ENV_VAR}: aspireCliExecutablePath must be an existing absolute path (current: ${configuredPath || '(empty)'}).`);
        return undefined;
    }

    collection.description = aspireCliPathEnvironmentDescription;
    collection.replace(ASPIRE_CLI_PATH_ENV_VAR, forwardablePath);
    deps.log?.(`Forwarding ${ASPIRE_CLI_PATH_ENV_VAR}=${forwardablePath} to terminals, tasks, and debug processes.`);
    return forwardablePath;
}

/**
 * Wires `syncAspireCliPathEnvironment` into the extension lifecycle: applies the
 * current setting once at activation and re-applies whenever the user edits
 * `aspire.aspireCliExecutablePath`.
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
    onForwardedPathChanged?: (previousPath: string | undefined, currentPath: string | undefined) => void,
): vscode.Disposable {
    let forwardedPath = syncAspireCliPathEnvironment(collection, deps);

    const disposable = vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration(`aspire.${ASPIRE_CLI_EXECUTABLE_PATH_SETTING}`)) {
            const previousPath = forwardedPath;
            forwardedPath = syncAspireCliPathEnvironment(collection, deps);
            if (previousPath !== forwardedPath) {
                onForwardedPathChanged?.(previousPath, forwardedPath);
            }
        }
    });

    subscriptions.push(disposable);
    return disposable;
}
