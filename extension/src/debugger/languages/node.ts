import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isNodeLaunchConfiguration } from "../../dcp/types";
import { invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import * as vscode from 'vscode';

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    if (isNodeLaunchConfiguration(launchConfig)) {
        // Use the absolute script path if available, otherwise fall back to working directory.
        // The working directory ensures cwd is set correctly for package manager mode (npm run dev).
        return launchConfig.script_path || launchConfig.working_directory || '';
    }

    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

export const nodeDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'node',
    debugAdapter: 'pwa-node',
    extensionId: null,
    getDisplayName: (launchConfiguration: ExecutableLaunchConfiguration) => {
        if (isNodeLaunchConfiguration(launchConfiguration)) {
            const displayPath = launchConfiguration.script_path || launchConfiguration.working_directory || '';
            return `Node.js: ${displayPath ? vscode.workspace.asRelativePath(displayPath) : 'unknown'}`;
        }
        return 'Node.js';
    },
    getSupportedFileTypes: () => ['.js', '.ts', '.mjs', '.mts', '.cjs', '.cts'],
    getProjectFile: (launchConfig) => getProjectFile(launchConfig),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, _launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isNodeLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not node for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        debugConfiguration.type = 'pwa-node';
        debugConfiguration.outputCapture = 'std';

        // Use working_directory for cwd if available
        if (launchConfig.working_directory) {
            debugConfiguration.cwd = launchConfig.working_directory;
        }

        if (launchConfig.runtime_executable) {
            debugConfiguration.runtimeExecutable = launchConfig.runtime_executable;
        }

        // For package manager script execution (e.g., npm run dev), use args directly as runtimeArgs.
        // The args from DCP already contain the full command (e.g., ["run", "dev", "--port", "5173"]).
        if (launchConfig.runtime_executable && launchConfig.runtime_executable !== 'node') {
            debugConfiguration.runtimeArgs = args ?? [];
            delete debugConfiguration.args;
            delete debugConfiguration.program;
        }

        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
    }
};
