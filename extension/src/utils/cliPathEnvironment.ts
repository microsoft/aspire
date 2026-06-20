import * as path from 'path';
import * as fs from 'fs';
import * as vscode from 'vscode';
import { getConfiguredCliPath } from './cliPath';
import { extensionLogOutputChannel } from './logging';

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
 * Description prefixed onto the `AspireCliPath` value in
 * VS Code's terminal contributed-environment UI ("View > Terminal > Environment
 * Contributions"). Surfaces the dev-loop intent so contributors can see *why*
 * the variable is being injected.
 *
 * VS Code exposes the description via `environmentVariableCollection.description`,
 * not per-variable, so this string covers the whole collection.
 */
const ENVIRONMENT_COLLECTION_DESCRIPTION = 'Forwards aspire.aspireCliExecutablePath as AspireCliPath so MSBuild bundle resolution and integrated terminals use the configured Aspire CLI.';

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
}

/**
 * Test seam: the synchronizer asks the collection (not vscode.workspace) for the
 * current configured CLI path so unit tests can avoid mocking `vscode.workspace`.
 */
export interface CliPathEnvironmentDependencies extends ForwardableCliPathDependencies {
    getConfiguredPath: () => string;
    log?: (message: string) => void;
    warn?: (message: string) => void;
}

const defaultForwardableCliPathDeps: ForwardableCliPathDependencies = {
    isAbsolute: path.isAbsolute,
    fileExists: fileExists,
};

const defaultDeps: CliPathEnvironmentDependencies = {
    getConfiguredPath: getConfiguredCliPath,
    ...defaultForwardableCliPathDeps,
    log: (message) => extensionLogOutputChannel.info(message),
    warn: (message) => extensionLogOutputChannel.warn(message),
};

export function isForwardableAspireCliPath(
    configuredPath: string,
    deps: ForwardableCliPathDependencies = defaultForwardableCliPathDeps,
): boolean {
    return configuredPath.length > 0 && deps.isAbsolute(configuredPath) && deps.fileExists(configuredPath);
}

function fileExists(filePath: string): boolean {
    try {
        return fs.statSync(filePath).isFile();
    }
    catch {
        return false;
    }
}

/**
 * Applies the current value of `aspire.aspireCliExecutablePath` to the supplied
 * environment variable collection. Called both at activation and from a
 * configuration-change listener so user edits to the setting take effect for
 * any subsequently created terminals or task processes.
 *
 * The collection is left untouched when the configured path is empty or not an
 * absolute path. Relative values and the on-PATH `aspire` fallback would either
 * fail `ResolveAspireCliBundle` (which logs a warning and returns no outputs)
 * or be ambiguous, so propagating them would only add noise.
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

    // Only forward paths that `ResolveAspireCliBundle` can consume. Relative,
    // shell-resolved, or stale absolute values fail the task's File.Exists guard
    // and make it stop before its PATH/ASPIRE_HOME fallback logic runs.
    if (!configuredPath || !deps.isAbsolute(configuredPath)) {
        collection.description = undefined;
        collection.delete(ASPIRE_CLI_PATH_ENV_VAR);
        deps.log?.(`Not forwarding ${ASPIRE_CLI_PATH_ENV_VAR}: no absolute aspireCliExecutablePath is configured (current: ${configuredPath || '(empty)'}).`);
        return undefined;
    }

    if (!deps.fileExists(configuredPath)) {
        collection.description = undefined;
        collection.delete(ASPIRE_CLI_PATH_ENV_VAR);
        deps.warn?.(`Not forwarding ${ASPIRE_CLI_PATH_ENV_VAR}: configured aspireCliExecutablePath does not exist (${configuredPath}).`);
        return undefined;
    }

    collection.description = ENVIRONMENT_COLLECTION_DESCRIPTION;
    collection.replace(ASPIRE_CLI_PATH_ENV_VAR, configuredPath);
    deps.log?.(`Forwarding ${ASPIRE_CLI_PATH_ENV_VAR}=${configuredPath} to terminals, tasks, and debug processes.`);
    return configuredPath;
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
): vscode.Disposable {
    syncAspireCliPathEnvironment(collection, deps);

    const disposable = vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration(`aspire.${ASPIRE_CLI_EXECUTABLE_PATH_SETTING}`)) {
            syncAspireCliPathEnvironment(collection, deps);
        }
    });

    subscriptions.push(disposable);
    return disposable;
}
