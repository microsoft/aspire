import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isPythonLaunchConfiguration } from "../../dcp/types";
import { invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import * as vscode from 'vscode';

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    if (isPythonLaunchConfiguration(launchConfig)) {
        const programPath = launchConfig.program_path || launchConfig.project_path;
        if (programPath) {
            return programPath;
        }

        // Some Python entrypoints, including module-based and executable-based launches, may not
        // have a program path; fall back to the working directory so the central cwd derivation
        // in createDebugSessionConfiguration has something to work with. The per-callback
        // override below sets `cwd` from `working_directory` when present.
        if (launchConfig.working_directory) {
            return launchConfig.working_directory;
        }
    }

    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

export const pythonDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'python',
    debugAdapter: 'debugpy',
    extensionId: 'ms-python.python',
    getDisplayName: (launchConfiguration: ExecutableLaunchConfiguration) => `Python: ${vscode.workspace.asRelativePath(getProjectFile(launchConfiguration))}`,
    getSupportedFileTypes: () => ['.py'],
    getProjectFile: (launchConfig) => getProjectFile(launchConfig),
    createDebugSessionConfigurationCallback: async (launchConfig, args, env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isPythonLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not python for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        if (launchConfig.interpreter_path) {
            debugConfiguration.python = launchConfig.interpreter_path;
        }

        // Use the explicit working_directory when provided so .WithWorkingDirectory(...) overrides
        // and the resource's app directory both flow through to the debugger's cwd.
        if (launchConfig.working_directory) {
            debugConfiguration.cwd = launchConfig.working_directory;
        }

        // By default, activate support for Jinja debugging
        debugConfiguration.jinja = true;

        // If module is specified, remove program from the debug configuration
        if (!!launchConfig.module) {
            delete debugConfiguration.program;
            debugConfiguration.module = launchConfig.module;
        }
    }
};
