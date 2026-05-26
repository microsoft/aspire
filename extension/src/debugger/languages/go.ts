import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isGoLaunchConfiguration } from "../../dcp/types";
import { goDisplayName, goLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    if (isGoLaunchConfiguration(launchConfig)) {
        return launchConfig.program || launchConfig.working_directory || '';
    }

    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

export const goDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'go',
    debugAdapter: 'go',
    extensionId: 'golang.go',
    getDisplayName: (launchConfiguration: ExecutableLaunchConfiguration) => {
        if (isGoLaunchConfiguration(launchConfiguration)) {
            const displayPath = launchConfiguration.program || launchConfiguration.working_directory || '';
            return displayPath ? goDisplayName(vscode.workspace.asRelativePath(displayPath)) : goLabel;
        }

        return goLabel;
    },
    getSupportedFileTypes: () => ['.go'],
    getProjectFile: (launchConfig) => getProjectFile(launchConfig),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isGoLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not go for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        debugConfiguration.type = 'go';
        debugConfiguration.request = 'launch';
        debugConfiguration.mode = 'debug';
        debugConfiguration.debugAdapter = 'dlv-dap';
        debugConfiguration.noDebug = !launchOptions.debug;

        const program = launchConfig.program || launchConfig.working_directory;
        if (program) {
            debugConfiguration.program = program;
        }

        if (launchConfig.working_directory) {
            debugConfiguration.cwd = launchConfig.working_directory;
        }

        if (launchConfig.build_flags) {
            debugConfiguration.buildFlags = launchConfig.build_flags;
        }

        debugConfiguration.args = args ?? [];
    }
};
