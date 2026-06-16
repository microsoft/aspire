import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isJavaLaunchConfiguration, JavaLaunchConfiguration } from "../../dcp/types";
import { invalidLaunchConfiguration, javaDisplayName, javaLabel } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";

const JAVA_EXTENSION_ID = 'redhat.java';
const JAVA_DEBUG_EXTENSION_ID = 'vscjava.vscode-java-debug';

const JAVA_EXECUTE_WORKSPACE_COMMAND = 'java.execute.workspaceCommand';
const JAVA_RESOLVE_BUILD_FILES_COMMAND = 'vscode.java.resolveBuildFiles';
const JAVA_PROJECT_CONFIGURATION_UPDATE_COMMAND = 'java.projectConfiguration.update';

async function getJavaExtensionApi() {
    // Subset of the redhat.java extension API surface we use
    interface JavaExtensionApi {
        serverMode: string;
        serverReady: () => Promise<boolean>;
    }

    const extension = vscode.extensions.getExtension<JavaExtensionApi>(JAVA_EXTENSION_ID);

    if (!extension) {
        return null;
    }

    if (!extension.isActive) {
        await extension.activate();
    }

    return extension.exports;
}

async function waitForJavaLanguageServerReady(): Promise<boolean> {
    const api = await getJavaExtensionApi();

    if (!api) {
        extensionLogOutputChannel.warn('Java Language Server is not installed');
        return false;
    }

    extensionLogOutputChannel.info(`Java Language Server is in ${api.serverMode} mode, waiting for readiness...`);

    try {
        return await api.serverReady();
    } catch (e) {
        extensionLogOutputChannel.warn(`Error waiting for Java Language Server readiness: ${e}`);
    }

    return false;
}

async function updateJavaProjectConfiguration(): Promise<void> {
    const buildFiles = await vscode.commands.executeCommand<string[]>(
        JAVA_EXECUTE_WORKSPACE_COMMAND,
        JAVA_RESOLVE_BUILD_FILES_COMMAND
    );

    if (buildFiles?.length) {
        extensionLogOutputChannel.info(`Updating project configuration for ${buildFiles.length} build file(s)...`);

        for (const buildFile of buildFiles) {
            await vscode.commands.executeCommand(JAVA_PROJECT_CONFIGURATION_UPDATE_COMMAND, vscode.Uri.parse(buildFile));
        }
    }
}

function getWorkspaceUri(launchConfig: JavaLaunchConfiguration): vscode.Uri | null {
    if (launchConfig.working_directory) {
        return vscode.Uri.file(launchConfig.working_directory);
    }

    return null;
}

export const javaDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'java',
    debugAdapter: 'java',
    extensionId: JAVA_DEBUG_EXTENSION_ID,

    getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => {
        if (!isJavaLaunchConfiguration(launchConfig)) {
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        const workspace = getWorkspaceUri(launchConfig as JavaLaunchConfiguration)?.toString();

        if (workspace) {
            return javaDisplayName(workspace);
        }

        return javaLabel;
    },

    getSupportedFileTypes: () => ['.java'],

    getProjectFile: (launchConfig: ExecutableLaunchConfiguration) => {
        return (launchConfig as JavaLaunchConfiguration).working_directory ?? '';
    },

    createDebugSessionConfigurationCallback: async (
        launchConfig: ExecutableLaunchConfiguration,
        args: string[] | undefined,
        env: { name: string; value: string }[],
        launchOptions: { debug: boolean;[key: string]: any },
        debugConfiguration: AspireResourceExtendedDebugConfiguration
    ): Promise<void> => {
        if (!isJavaLaunchConfiguration(launchConfig)) {
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        const javaConfig = launchConfig as JavaLaunchConfiguration;

        // Wait for the Java Language Server to be ready
        await waitForJavaLanguageServerReady();

        // Refresh project configuration
        // This is useful for fresh clones, or for scenarios where class files are not present yet
        await updateJavaProjectConfiguration();

        debugConfiguration.type = 'java';
        debugConfiguration.request = 'launch';
        debugConfiguration.noDebug = !launchOptions.debug;

        if (javaConfig.working_directory) {
            debugConfiguration.cwd = javaConfig.working_directory;
        }

        if (env?.length) {
            debugConfiguration.env = Object.fromEntries(env.map(e => [e.name, e.value]));
        }
    }
};
