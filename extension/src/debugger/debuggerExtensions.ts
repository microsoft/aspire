import path from "path";
import { ExecutableLaunchConfiguration, EnvVar, LaunchOptions, AspireResourceExtendedDebugConfiguration, AspireExtendedDebugConfiguration, AspireResourceDebugSession, getLaunchConfigurationExecutablePaths, getLaunchConfigurationTargetPath } from "../dcp/types";
import { debugProject, runProject } from "../loc/strings";
import { getEnvironmentForChildProcess, mergeEnvs } from "../utils/environment";
import { extensionLogOutputChannel } from "../utils/logging";
import { projectDebuggerExtension } from "./languages/dotnet";
import { isAzureFunctionsExtensionInstalled, isBunInstalled, isCsharpInstalled, isGoInstalled, isJavaInstalled, isMauiInstalled, isPythonInstalled, isRustInstalled } from '../capabilities';
import { pythonDebuggerExtension } from "./languages/python";
import { nodeDebuggerExtension } from "./languages/node";
import { browserDebuggerExtension } from "./languages/browser";
import { azureFunctionsDebuggerExtension } from "./languages/azureFunctions";
import { goDebuggerExtension } from "./languages/go";
import { createDefaultRustDebuggerExtension } from "./languages/rust";
import { bunDebuggerExtension } from "./languages/bun";
import { denoDebuggerExtension } from "./languages/deno";
import { javaDebuggerExtension } from "./languages/java";
import { mauiDebuggerExtension } from "./languages/maui";
import { isDirectory } from "../utils/io";
import { waitForRunStartIdle } from "./runStartRegistry";

// Represents a resource-specific debugger extension for when the default session configuration is not sufficient to launch the resource.
export interface ResourceDebuggerExtension {
    resourceType: string;
    debugAdapter: string;
    extensionId: string | null;
    getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => string;
    getProjectFile: (launchConfig: ExecutableLaunchConfiguration) => string;
    getSupportedFileTypes: () => string[];
    createDebugSessionConfigurationCallback?: (launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration) => Promise<AlreadyStartedResourceDebugSession | void>;
}

export interface AlreadyStartedResourceDebugSession extends AspireResourceDebugSession {
    processId: number;
    termination: Promise<number>;
}

export interface PreparedDebugSession {
    debugConfiguration: AspireResourceExtendedDebugConfiguration;
    alreadyStartedSession?: AlreadyStartedResourceDebugSession;
}

export async function createDebugSessionConfiguration(debugSessionConfig: AspireExtendedDebugConfiguration, launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debuggerExtension: ResourceDebuggerExtension): Promise<AspireResourceExtendedDebugConfiguration> {
    return (await prepareDebugSession(debugSessionConfig, launchConfig, args, env, launchOptions, debuggerExtension)).debugConfiguration;
}

export async function prepareDebugSession(debugSessionConfig: AspireExtendedDebugConfiguration, launchConfig: ExecutableLaunchConfiguration, args: string[] | undefined, env: EnvVar[], launchOptions: LaunchOptions, debuggerExtension: ResourceDebuggerExtension): Promise<PreparedDebugSession> {
    if (debuggerExtension === null) {
        extensionLogOutputChannel.warn(`Unknown type: ${launchConfig.type}.`);
    }

    const projectPath = debuggerExtension.getProjectFile(launchConfig);
    await waitForRunStartIdle();

    const configuration: AspireResourceExtendedDebugConfiguration = {
        type: debuggerExtension.debugAdapter || launchConfig.type,
        request: 'launch',
        name: launchOptions.debug ? debugProject(debuggerExtension.getDisplayName(launchConfig)) : runProject(debuggerExtension.getDisplayName(launchConfig)),
        program: projectPath,
        args: args,
        cwd: await isDirectory(projectPath) ? projectPath : path.dirname(projectPath),
        env: mergeEnvs(getEnvironmentForChildProcess(), env),
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


    let alreadyStartedSession: AlreadyStartedResourceDebugSession | undefined;
    if (debuggerExtension.createDebugSessionConfigurationCallback) {
        alreadyStartedSession = await debuggerExtension.createDebugSessionConfigurationCallback(launchConfig, args, env, launchOptions, configuration) ?? undefined;
    }

    // Debugger-specific callbacks can replace `program` with an output binary,
    // runtime executable, or package-manager command. Keep the source target and
    // resource executable identity from DCP's typed launch metadata so editor
    // assistance never has to inspect the final debug configuration or infer
    // identity from free-form args/environment.
    configuration.targetPath = getLaunchConfigurationTargetPath(launchConfig);
    const resourceExecutablePaths = getLaunchConfigurationExecutablePaths(launchConfig);
    if (resourceExecutablePaths.length > 0) {
        configuration.resourceExecutablePaths = resourceExecutablePaths;
    }

    return {
        debugConfiguration: configuration,
        alreadyStartedSession
    };
}

export function getResourceDebuggerExtensions(platform: NodeJS.Platform = process.platform): ResourceDebuggerExtension[] {
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

    if (isGoInstalled()) {
        extensions.push(goDebuggerExtension);
    }

    if (isRustInstalled(platform)) {
        // Adapter availability can change when an extension is installed without reloading VS Code.
        // Resolve the Rust descriptor with the same current platform/extension state as capabilities.
        extensions.push(createDefaultRustDebuggerExtension(platform));
    }

    if (isJavaInstalled()) {
        extensions.push(javaDebuggerExtension);
    }

    extensions.push(nodeDebuggerExtension);
    extensions.push(browserDebuggerExtension);

    if (isBunInstalled()) {
        extensions.push(bunDebuggerExtension);
    }

    extensions.push(denoDebuggerExtension);

    if (isMauiInstalled()) {
        extensions.push(mauiDebuggerExtension);
    }

    return extensions;
}
