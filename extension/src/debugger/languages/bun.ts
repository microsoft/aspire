import * as path from 'path';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { bunDisplayName, bunLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { getJavaScriptRuntimeDisplayName, getJavaScriptRuntimeTargetPath, jsRuntimeBaseFileTypes, launchMethodDirect, launchMethodPackageManager, resolveJavaScriptLaunchMethod } from "./javascriptRuntime";

function asBunConfig(launchConfig: ExecutableLaunchConfiguration): JavaScriptRuntimeLaunchConfiguration {
    if (isJavaScriptRuntimeLaunchConfiguration(launchConfig) && launchConfig.type === 'bun') {
        return launchConfig;
    }

    extensionLogOutputChannel.info(`The resource type was not bun for ${JSON.stringify(launchConfig)}`);
    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

export const bunDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'bun',
    debugAdapter: 'bun',
    extensionId: 'oven.bun-vscode',
    getDisplayName: (launchConfig) => getJavaScriptRuntimeDisplayName(launchConfig, 'bun', bunDisplayName, bunLabel),
    getSupportedFileTypes: () => [...jsRuntimeBaseFileTypes, '.jsx', '.tsx'],
    getProjectFile: (launchConfig) => getJavaScriptRuntimeTargetPath(asBunConfig(launchConfig)),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, _launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        const config = asBunConfig(launchConfig);
        debugConfiguration.type = 'bun';

        // oven.bun-vscode uses "runtime" (not "runtimeExecutable") and requires a non-empty "program".
        if (config.working_directory) {
            debugConfiguration.cwd = config.working_directory;
        }

        if (config.runtime_executable) {
            debugConfiguration.runtime = config.runtime_executable;
        }

        // args[0] === "run" means a package-manager run-script (e.g. "bun run start"); anything else is a direct file.
        const subcommand = args ?? [];
        const launchMethod = resolveJavaScriptLaunchMethod(config, () => subcommand[0] === 'run' ? launchMethodPackageManager : launchMethodDirect);
        if (launchMethod === launchMethodPackageManager) {
            // Guard against empty args: if launch_method is explicitly "package-manager" but no
            // subcommand was provided, fall back to "run" as the default bun subcommand.
            debugConfiguration.program = subcommand[0] ?? 'run';
            debugConfiguration.args = subcommand.slice(1);
        }
        else {
            // direct mode args examples:
            //   ["index.ts", "--flag"]  -> program "index.ts" is args[0]; drop it -> args ["--flag"]
            //   ["--flag", "value"]     -> args[0] is NOT the script path; keep all args
            // DCP often repeats the resolved script path as args[0]; only drop it when it actually
            // matches the script path / program so a genuine first user argument is not lost.
            // Compare both the raw value and the resolved absolute path to handle cases where the
            // hosting side emits a relative path that DCP did not normalize.
            const scriptPath = getJavaScriptRuntimeTargetPath(config);
            const firstArg = subcommand[0];
            const resolvedFirstArg = firstArg !== undefined && config.working_directory
                ? path.resolve(config.working_directory, firstArg)
                : undefined;
            if (firstArg !== undefined && (firstArg === scriptPath || firstArg === debugConfiguration.program || resolvedFirstArg === scriptPath)) {
                debugConfiguration.args = subcommand.slice(1);
            }
            else {
                debugConfiguration.args = subcommand;
            }

            // "program" is normally already set upstream (scaffold default or user override); only fall
            // back when neither supplied one.
            if (!debugConfiguration.program) {
                debugConfiguration.program = getJavaScriptRuntimeTargetPath(config) || '.';
            }
        }
    }
};
