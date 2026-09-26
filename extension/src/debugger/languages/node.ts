import * as net from "net";
import * as path from "path";
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { denoAppHostRunCommandMissing, denoInspectorAddressUnavailable, nodeDisplayName, nodeLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { getJavaScriptRuntimeDisplayName, getJavaScriptRuntimeTargetPath, jsRuntimeBaseFileTypes, launchMethodDirect, launchMethodPackageManager, resolveJavaScriptLaunchMethod } from "./javascriptRuntime";

function asNodeConfig(launchConfig: ExecutableLaunchConfiguration): JavaScriptRuntimeLaunchConfiguration {
    if (isJavaScriptRuntimeLaunchConfiguration(launchConfig) && launchConfig.type === 'node') {
        return launchConfig;
    }

    extensionLogOutputChannel.info(`The resource type was not node for ${JSON.stringify(launchConfig)}`);
    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

function isDenoRuntimeExecutable(runtimeExecutable: string | undefined): boolean {
    if (!runtimeExecutable) {
        return false;
    }

    const extension = path.extname(runtimeExecutable);
    return path.basename(runtimeExecutable, extension).toLowerCase() === 'deno';
}

async function getAvailableLoopbackPort(): Promise<number> {
    const server = net.createServer();

    return await new Promise<number>((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            const address = server.address();
            if (!address || typeof address === 'string') {
                server.close();
                reject(new Error(denoInspectorAddressUnavailable));
                return;
            }

            server.close(error => error ? reject(error) : resolve(address.port));
        });
    });
}

export const nodeDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'node',
    // Use js-debug's pwa-node adapter so outputCapture emits stdout/stderr DAP output events for dashboard log forwarding.
    debugAdapter: 'pwa-node',
    extensionId: null,
    getDisplayName: (launchConfig) => getJavaScriptRuntimeDisplayName(launchConfig, 'node', nodeDisplayName, nodeLabel),
    getSupportedFileTypes: () => jsRuntimeBaseFileTypes,
    getProjectFile: (launchConfig) => getJavaScriptRuntimeTargetPath(asNodeConfig(launchConfig)),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        const config = asNodeConfig(launchConfig);
        debugConfiguration.type = 'pwa-node';
        debugConfiguration.outputCapture = 'std';

        // Use working_directory for cwd if available
        if (config.working_directory) {
            debugConfiguration.cwd = config.working_directory;
        }

        if (config.runtime_executable) {
            debugConfiguration.runtimeExecutable = config.runtime_executable;
        }

        // For package manager script execution (e.g., npm run dev), use args directly as runtimeArgs.
        // The args from DCP already contain the full command (e.g., ["run", "dev", "--port", "5173"]).
        const launchMethod = resolveJavaScriptLaunchMethod(config, () => config.runtime_executable && config.runtime_executable !== 'node' ? launchMethodPackageManager : launchMethodDirect);
        if (launchMethod === launchMethodPackageManager) {
            const runtimeArgs = [...(args ?? [])];

            if (launchOptions.debug && launchOptions.isApphost && isDenoRuntimeExecutable(config.runtime_executable)) {
                const runArgumentIndex = runtimeArgs.indexOf('run');
                if (runArgumentIndex < 0) {
                    throw new Error(denoAppHostRunCommandMissing);
                }

                // Deno does not load js-debug's Node bootloader. Open its native inspector and tell
                // js-debug to attach directly, matching the official Deno VS Code provider.
                const inspectorPort = await getAvailableLoopbackPort();
                runtimeArgs.splice(runArgumentIndex + 1, 0, `--inspect-wait=127.0.0.1:${inspectorPort}`);
                debugConfiguration.attachSimplePort = inspectorPort;
            }

            debugConfiguration.runtimeArgs = runtimeArgs;
            delete debugConfiguration.args;
            delete debugConfiguration.program;
        }

        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
    }
};
