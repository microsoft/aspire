import path from "path";
import { ExecutableLaunchConfiguration, EnvVar, LaunchOptions, AspireResourceExtendedDebugConfiguration, AspireExtendedDebugConfiguration } from "../dcp/types";
import { debugProject, runProject } from "../loc/strings";
import { mergeEnvs } from "../utils/environment";
import { extensionLogOutputChannel } from "../utils/logging";
import { projectDebuggerExtension } from "./languages/dotnet";
import { isAzureFunctionsExtensionInstalled, isCsharpInstalled, isPythonInstalled } from '../capabilities';
import { pythonDebuggerExtension } from "./languages/python";
import { nodeDebuggerExtension } from "./languages/node";
import { browserDebuggerExtension } from "./languages/browser";
import { azureFunctionsDebuggerExtension } from "./languages/azureFunctions";
import { isDirectory } from "../utils/io";

// Represents a resource-specific debugger extension for when the default session configuration is not sufficient to launch the resource.
export interface ResourceDebuggerExtension {
    resourceType: string;
    debugAdapter: string;
    extensionId: string | null;
    getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => string;
    getProjectFile: (launchConfig: ExecutableLaunchConfiguration) => string;
    getSupportedFileTypes: () => string[];
    createDebugSessionConfigurationCallback?: (launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration) => Promise<void>;
}

export async function createDebugSessionConfiguration(debugSessionConfig: AspireExtendedDebugConfiguration, launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debuggerExtension: ResourceDebuggerExtension): Promise<AspireResourceExtendedDebugConfiguration> {
    if (debuggerExtension === null) {
        extensionLogOutputChannel.warn(`Unknown type: ${launchConfig.type}.`);
    }

    const projectPath = debuggerExtension.getProjectFile(launchConfig);

    const configuration: AspireResourceExtendedDebugConfiguration = {
        type: debuggerExtension.debugAdapter || launchConfig.type,
        request: 'launch',
        name: launchOptions.debug ? debugProject(debuggerExtension.getDisplayName(launchConfig)) : runProject(debuggerExtension.getDisplayName(launchConfig)),
        program: projectPath,
        args: args,
        cwd: await isDirectory(projectPath) ? projectPath : path.dirname(projectPath),
        env: mergeEnvs(process.env, env),
        justMyCode: false,
        stopAtEntry: false,
        noDebug: !launchOptions.debug,
        runId: launchOptions.runId,
        debugSessionId: launchOptions.debugSessionId,
        console: 'internalConsole',
        isApphost: launchOptions.isApphost
    };

    if (debugSessionConfig.debuggers) {
        // 1. Check if this is the apphost
        if (launchOptions.isApphost && debugSessionConfig.debuggers['apphost']) {
            Object.assign(configuration, debugSessionConfig.debuggers['apphost']);
        }

        // 2. Check for resource type specific debugger settings
        if (debugSessionConfig.debuggers[launchConfig.type]) {
            Object.assign(configuration, debugSessionConfig.debuggers[launchConfig.type]);
        }
    }


    if (debuggerExtension.createDebugSessionConfigurationCallback) {
        await debuggerExtension.createDebugSessionConfigurationCallback(launchConfig, args, env, launchOptions, configuration);
    }

    return configuration;
}

export function getResourceDebuggerExtensions(): ResourceDebuggerExtension[] {
    const extensions = [];
    if (isCsharpInstalled()) {
        extensions.push(projectDebuggerExtension);

        if (isAzureFunctionsExtensionInstalled()) {
            extensions.push(azureFunctionsDebuggerExtension);
        }
    }

    if (isPythonInstalled()) {
        extensions.push(pythonDebuggerExtension);
    }

    extensions.push(nodeDebuggerExtension);
    extensions.push(browserDebuggerExtension);

    return extensions;
}

