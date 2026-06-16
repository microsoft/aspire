import * as vscode from 'vscode';
import * as path from 'path';
import * as os from 'os';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isBrowserLaunchConfiguration, LaunchOptions } from "../../dcp/types";
import { browserDisplayName, browserLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";

function getBlazorWasmBrowser(browser: string | undefined): string {
    if (!browser || browser === 'msedge' || browser === 'pwa-msedge') {
        return 'edge';
    }

    if (browser === 'pwa-chrome') {
        return 'chrome';
    }

    return browser;
}

export const browserDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'browser',
    debugAdapter: 'msedge',
    extensionId: null, // built-in to VS Code via js-debug
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

        const projectPath = launchConfig.web_root;
        const url = launchConfig.url;
        if (typeof projectPath === 'string' && projectPath.endsWith('.csproj') && url) {
            extensionLogOutputChannel.info(`[WASM] Detected Blazor WASM project: ${projectPath}`);
            debugConfiguration.type = 'blazorwasm';
            debugConfiguration.request = 'attach';
            debugConfiguration.projectPath = projectPath;
            debugConfiguration.cwd = path.dirname(projectPath);
            debugConfiguration.url = url;
            debugConfiguration.browser = getBlazorWasmBrowser(launchConfig.browser);
            debugConfiguration.noDebug = !launchOptions.debug;

            delete debugConfiguration.program;
            delete debugConfiguration.args;

            extensionLogOutputChannel.info(`[WASM] Final debug config: type=${debugConfiguration.type}, request=${debugConfiguration.request}, projectPath=${debugConfiguration.projectPath}, cwd=${debugConfiguration.cwd}, url=${debugConfiguration.url}, browser=${debugConfiguration.browser}, noDebug=${debugConfiguration.noDebug}`);
            registerBrowserSessionTerminationNotification(debugConfiguration, launchOptions);
            return;
        }

        // Map browser name to VS Code js-debug adapter type.
        // js-debug registers both 'msedge'/'chrome' and 'pwa-msedge'/'pwa-chrome'.
        const browser = launchConfig.browser || 'msedge';
        debugConfiguration.type = browser;
        debugConfiguration.request = 'launch';
        debugConfiguration.url = launchConfig.url;
        debugConfiguration.webRoot = launchConfig.web_root;;

        debugConfiguration.sourceMaps = true;
        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
        // Use a stable user data directory dedicated to Aspire debugging.
        // This avoids the managed-profile sign-in prompt that appears with a fresh
        // temp dir (userDataDir: true) on corp machines, and avoids conflicts with
        // the user's existing browser profile (userDataDir: false).
        debugConfiguration.userDataDir = path.join(os.tmpdir(), 'aspire-browser-debug');

        // Suppress Edge/Chrome first-run wizards and profile selection prompts
        // that appear on managed machines with enterprise policies.
        debugConfiguration.runtimeArgs = [
            '--no-first-run',
            '--no-default-browser-check',
            '--hide-crash-restore-bubble',
            '--disable-features=EdgeProfileOnStartup,msEdgeFirstRunExperience,EdgeBackgroundMode',
            '--disable-background-mode',
        ];

        // Remove program/args/cwd since browser debugging doesn't use them
        delete debugConfiguration.program;
        delete debugConfiguration.args;
        delete debugConfiguration.cwd;

        extensionLogOutputChannel.info(`[Browser] Final debug config: type=${debugConfiguration.type}, url=${debugConfiguration.url}, webRoot=${debugConfiguration.webRoot}, noDebug=${debugConfiguration.noDebug}`);

        registerBrowserSessionTerminationNotification(debugConfiguration, launchOptions);
    }
};

function registerBrowserSessionTerminationNotification(debugConfiguration: AspireResourceExtendedDebugConfiguration, launchOptions: LaunchOptions): void {
    // Listen for the browser debug session to terminate (e.g., user closes the browser window).
    // When it does, notify DCP so the resource transitions to a terminal state and
    // the dashboard UI can reset.
    // We match by session name only because js-debug child sessions do not carry
    // custom configuration properties (runId) from the parent launch config.
    const runId = debugConfiguration.runId;
    const debugSessionId = debugConfiguration.debugSessionId;
    const aspireSession = launchOptions.debugSession;
    const browserSessionName = debugConfiguration.name;

    extensionLogOutputChannel.info(`[Browser] Registering terminate listener for session name="${browserSessionName}", runId=${runId}, debugSessionId=${debugSessionId}`);

    if (runId && debugSessionId) {
        const disposable = vscode.debug.onDidTerminateDebugSession((session) => {
            extensionLogOutputChannel.info(`[Browser] onDidTerminateDebugSession fired: name="${session.name}", configRunId=${session.configuration?.runId}, expected="${browserSessionName}"`);
            if (session.name === browserSessionName) {
                disposable.dispose();
                extensionLogOutputChannel.info(`[Browser] Browser debug session terminated — notifying DCP (runId: ${runId}, debugSessionId: ${debugSessionId})`);
                aspireSession.sendSessionTerminated(runId, debugSessionId, 0);
            }
        });
    }
}
