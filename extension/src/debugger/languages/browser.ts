import * as path from 'path';
import * as os from 'os';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isBrowserLaunchConfiguration } from "../../dcp/types";
import { browserDisplayName, browserLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { tryStartWasmDebugging } from "./wasmDebug";

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

        // Map browser name to VS Code js-debug adapter type.
        // js-debug registers both 'msedge'/'chrome' and 'pwa-msedge'/'pwa-chrome'.
        const browser = launchConfig.browser || 'msedge';
        debugConfiguration.type = browser;
        debugConfiguration.request = 'launch';
        debugConfiguration.url = launchConfig.url;

        // webRoot must be a directory for source map resolution.
        // When web_root is a .csproj path (Blazor WASM), use the containing directory.
        const webRoot = launchConfig.web_root?.endsWith('.csproj')
            ? path.dirname(launchConfig.web_root)
            : launchConfig.web_root;
        debugConfiguration.webRoot = webRoot;

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
            '--disable-features=EdgeProfileOnStartup,msEdgeFirstRunExperience',
        ];

        // Remove program/args/cwd since browser debugging doesn't use them
        delete debugConfiguration.program;
        delete debugConfiguration.args;
        delete debugConfiguration.cwd;

        // If web_root points to a .csproj, this is a Blazor WASM project —
        // wire up managed debugging via the C# extension's VSWebAssemblyBridge.
        if (launchConfig.web_root?.endsWith('.csproj') && launchConfig.url) {
            extensionLogOutputChannel.info(`[WASM] Detected Blazor WASM project: ${launchConfig.web_root}`);
            const wasmStarted = await tryStartWasmDebugging(launchConfig.url, launchConfig.web_root, debugConfiguration, launchOptions.debugSession);
            extensionLogOutputChannel.info(`[WASM] tryStartWasmDebugging result: ${wasmStarted}`);
            if (wasmStarted) {
                // The browser session must have debugging enabled so js-debug connects
                // to the DevTools protocol through the bridge's port/inspectUri.
                // Without this, noDebug:true causes js-debug to skip attaching entirely.
                debugConfiguration.noDebug = false;
            }
        }
        extensionLogOutputChannel.info(`[Browser] Final debug config: type=${debugConfiguration.type}, url=${debugConfiguration.url}, webRoot=${debugConfiguration.webRoot}, noDebug=${debugConfiguration.noDebug}`);
    }
};
