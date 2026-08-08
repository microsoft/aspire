import { promises as fs } from "node:fs";
import type { Stats } from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isBrowserLaunchConfiguration } from "../../dcp/types";
import { browserDisplayName, browserLabel, firefoxDebuggerNotInstalled, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { firefoxDebugAdapterType, isFirefoxDebuggerInstalled, promptToInstallFirefoxDebugger } from "../firefoxDebugger";
import { registerRunCleanup } from "../runCleanupRegistry";

const defaultBrowserRuntimeArgs = [
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-background-mode'
];

/** Directory under the OS temp directory that contains Aspire-owned browser profiles. */
const browserProfileRootDirectoryName = 'aspire-vscode-browser-debug';

export const browserDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'browser',
    debugAdapter: 'pwa-msedge',
    extensionId: null, // built-in to VS Code via js-debug
    // js-debug is a server-hosted adapter shared across debug sessions, so its adapter exit is not
    // a per-run signal, and it tears down child target sessions (page/worker) independently of the
    // root session. The end of the root VS Code debug session is the only reliable run lifetime
    // signal, so AspireDebugSession reports termination for browser runs.
    terminationSignal: 'debugSessionEnd',
    getDisplayName: (launchConfiguration: ExecutableLaunchConfiguration) => {
        if (isBrowserLaunchConfiguration(launchConfiguration) && launchConfiguration.url) {
            return browserDisplayName(launchConfiguration.url);
        }
        return browserLabel;
    },
    getSupportedFileTypes: () => [],
    getProjectFile: () => '',
    createDebugSessionConfigurationCallback: async (launchConfig, _args, _env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isBrowserLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not browser for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        if (!launchConfig.url) {
            extensionLogOutputChannel.info(`Browser launch configuration did not include a URL for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        debugConfiguration.type = getBrowserDebugAdapter(launchConfig.browser);
        // The `firefox` adapter is not built into VS Code; it comes from the
        // firefox-devtools.vscode-firefox-debug extension. If it is missing, VS Code would
        // fail to start the session with only a generic "debug session failed to start"
        // error, so detect it here and surface an actionable install prompt instead.
        if (debugConfiguration.type === firefoxDebugAdapterType && !isFirefoxDebuggerInstalled()) {
            promptToInstallFirefoxDebugger();
            throw new Error(firefoxDebuggerNotInstalled);
        }
        debugConfiguration.request = 'launch';
        debugConfiguration.url = launchConfig.url;
        debugConfiguration.webRoot = launchConfig.web_root;
        debugConfiguration.sourceMaps = true;
        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
        debugConfiguration.runtimeArgs = mergeRuntimeArgs(debugConfiguration.runtimeArgs, defaultBrowserRuntimeArgs);
        const userDataDir = await createBrowserUserDataDir(debugConfiguration.runId);
        if (userDataDir) {
            debugConfiguration.userDataDir = userDataDir;
            // Only a path that createBrowserUserDataDir() created itself, inside a profile root it
            // verified, ever reaches this recursive delete. That function is the single gate for
            // both the launch argument and the cleanup, so the two can never disagree about which
            // directory Aspire owns.
            registerRunCleanup(debugConfiguration.runId, () => {
                void fs.rm(userDataDir, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 }).catch(error => {
                    extensionLogOutputChannel.warn(`Failed to delete browser debug profile directory '${userDataDir}': ${error instanceof Error ? error.message : String(error)}`);
                });
            });
        }
        else {
            // Fail closed: run without an isolated profile rather than pointing the browser at, or
            // aiming a recursive delete at, a directory Aspire does not own. js-debug falls back to
            // its own default profile, so debugging still works and only profile isolation is lost.
            extensionLogOutputChannel.warn(`Could not create a contained browser debug profile directory for run '${debugConfiguration.runId}'; launching without an isolated profile.`);
        }
        // Remove program/args/cwd since browser debugging doesn't use them
        delete debugConfiguration.program;
        delete debugConfiguration.args;
        delete debugConfiguration.cwd;
    }
};

function getBrowserDebugAdapter(browser: string | undefined): string {
    const normalizedBrowser = browser?.trim().toLowerCase();
    switch (normalizedBrowser) {
        case undefined:
        case '':
        case 'edge':
        case 'msedge':
        case 'microsoft-edge':
        case 'microsoftedge':
            return 'pwa-msedge';
        case 'chrome':
        case 'google-chrome':
        case 'chromium':
            return 'pwa-chrome';
        case 'firefox':
        case 'mozilla-firefox':
            return firefoxDebugAdapterType;
        default:
            return normalizedBrowser.startsWith('pwa-') ? normalizedBrowser : `pwa-${normalizedBrowser}`;
    }
}

function mergeRuntimeArgs(existingRuntimeArgs: unknown, argsToAdd: string[]): string[] {
    const runtimeArgs = Array.isArray(existingRuntimeArgs)
        ? existingRuntimeArgs.filter((arg): arg is string => typeof arg === 'string')
        : typeof existingRuntimeArgs === 'string' ? [existingRuntimeArgs] : [];

    for (const arg of argsToAdd) {
        if (!runtimeArgs.includes(arg)) {
            runtimeArgs.push(arg);
        }
    }

    return runtimeArgs;
}

/**
 * Creates the isolated browser profile directory for a run, or returns `undefined` when one could
 * not be established.
 *
 * The run id is a readability prefix only. It is deliberately not what makes the path unique, and
 * it is not trusted: `runId` is workspace-writable, so it is reduced to a single path segment first.
 * The post-creation realpath containment check is still load-bearing because this path is later
 * deleted recursively. If a future change accidentally lets a `..` segment through, the profile is
 * refused rather than aiming cleanup at the temp directory or another run.
 */
async function createBrowserUserDataDir(runId: string): Promise<string | undefined> {
    try {
        const profileRoot = getBrowserProfileRootDirectory();
        await fs.mkdir(profileRoot, { recursive: true, mode: 0o700 });

        const profileRootStats = await fs.lstat(profileRoot);
        if (!isSafeBrowserProfileRoot(profileRootStats)) {
            extensionLogOutputChannel.warn(`Refusing to use unsafe browser debug profile root '${profileRoot}'.`);

            return undefined;
        }

        const created = await fs.mkdtemp(path.join(profileRoot, `${sanitizeRunIdSegment(runId)}-`));
        const profileRootRealPath = await fs.realpath(profileRoot);
        const createdRealPath = await fs.realpath(created);

        if (!isProperDescendant(profileRootRealPath, createdRealPath)) {
            extensionLogOutputChannel.warn(`Refusing to use browser debug profile directory '${created}' because its real path '${createdRealPath}' is outside '${profileRootRealPath}'.`);

            return undefined;
        }

        return created;
    }
    catch (error) {
        extensionLogOutputChannel.warn(`Failed to create a browser debug profile directory under '${getBrowserProfileRootDirectory()}': ${error instanceof Error ? error.message : String(error)}`);

        return undefined;
    }
}

function getBrowserProfileRootDirectory(): string {
    return path.join(os.tmpdir(), browserProfileRootDirectoryName);
}

function isSafeBrowserProfileRoot(stats: Stats): boolean {
    if (!stats.isDirectory() || stats.isSymbolicLink()) {
        return false;
    }

    if (process.platform === 'win32') {
        return true;
    }

    if (typeof process.getuid === 'function' && stats.uid !== process.getuid()) {
        return false;
    }

    return (stats.mode & 0o077) === 0;
}

/**
 * Reduces a run id to a single safe path segment.
 *
 * Only characters that are unsafe in a path segment are replaced. `.` and `-` are deliberately kept
 * because run ids legitimately contain them, which is why sanitizing alone was never a containment
 * guarantee on its own: `..` and `.` survive this replacement untouched.
 */
function sanitizeRunIdSegment(runId: string): string {
    return runId.replace(/[^a-zA-Z0-9._-]/g, '-');
}

/**
 * Returns whether `candidateRealPath` is a proper descendant of `parentRealPath`.
 *
 * `path.relative` is used rather than a string prefix test, and both inputs are real paths before
 * they get here. The parent itself is rejected as well as anything above it — the returned path is
 * deleted recursively, and deleting the parent would take every other profile directory with it.
 *
 * The comparison is lexical and case-sensitive. On a case-insensitive filesystem a differently
 * cased path would be rejected rather than accepted, which is the safe direction; both sides come
 * from `realpath`, so this does not occur in practice.
 */
function isProperDescendant(parentRealPath: string, candidateRealPath: string): boolean {
    const relative = path.relative(parentRealPath, candidateRealPath);

    return relative.length > 0
        && relative !== '..'
        && !relative.startsWith(`..${path.sep}`)
        && !path.isAbsolute(relative);
}
