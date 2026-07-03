import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { denoDisplayName, denoLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { getJavaScriptRuntimeDisplayName, getJavaScriptRuntimeTargetPath, jsRuntimeBaseFileTypes } from "./javascriptRuntime";

// Deno exposes a V8 inspector; --inspect-wait blocks execution until a debugger attaches (unlike
// --inspect-brk it guarantees no early code — including module top-level — runs before attach, which
// is what makes IDE attach reliable). The inspector defaults to 127.0.0.1:9229.
const denoInspectorDefaultPort = 9229;

// Deno sub-commands that accept runtime flags (so --inspect-wait must be inserted AFTER this token,
// not before it — `deno --inspect-wait run` is invalid).
const denoSubcommandsAcceptingRuntimeFlags = new Set(['run', 'serve', 'task', 'test', 'bench']);

function asDenoConfig(launchConfig: ExecutableLaunchConfiguration): JavaScriptRuntimeLaunchConfiguration {
    if (isJavaScriptRuntimeLaunchConfiguration(launchConfig) && launchConfig.type === 'deno') {
        return launchConfig;
    }

    extensionLogOutputChannel.info(`The resource type was not deno for ${JSON.stringify(launchConfig)}`);
    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

// Matches any Deno inspector flag already present in the arg vector (e.g. a user-configured
// WithDenoInspect*): --inspect, --inspect-brk, --inspect-wait, optionally with =host:port.
const denoInspectFlagPattern = /^--inspect(-brk|-wait)?(=|$)/;

function parseInspectPort(args: string[]): number | undefined {
    for (const arg of args) {
        const match = /^--inspect(?:-brk|-wait)?=(?:.*:)?(\d+)$/.exec(arg);
        if (match) {
            return Number(match[1]);
        }

        // A bare inspector flag (no host:port) uses Deno's default inspector port.
        if (denoInspectFlagPattern.test(arg)) {
            return denoInspectorDefaultPort;
        }
    }

    return undefined;
}

/**
 * Injects `--inspect-wait` into a Deno argument vector so VS Code's built-in js-debug (pwa-node) can
 * attach. The flag is placed immediately after the leading sub-command (run/serve/task/…) so it is
 * parsed as a runtime flag rather than a script argument. If the caller already configured an
 * inspector flag (WithDenoInspect*), the vector is returned unchanged.
 */
function withDenoInspectWait(args: string[]): { runtimeArgs: string[]; port: number } {
    const existingPort = parseInspectPort(args);
    if (existingPort !== undefined) {
        return { runtimeArgs: [...args], port: existingPort };
    }

    const runtimeArgs = [...args];
    const insertAt = runtimeArgs.length > 0 && denoSubcommandsAcceptingRuntimeFlags.has(runtimeArgs[0]) ? 1 : 0;
    runtimeArgs.splice(insertAt, 0, '--inspect-wait');
    return { runtimeArgs, port: denoInspectorDefaultPort };
}

export const denoDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'deno',
    // Deno debugging uses js-debug's pwa-node adapter (VS Code built-in, no third-party extension):
    // it launches the Deno process and attaches to its V8 inspector via attachSimplePort. outputCapture
    // 'std' forwards stdout/stderr as DAP output events for dashboard log forwarding.
    debugAdapter: 'pwa-node',
    extensionId: null,
    getDisplayName: (launchConfig) => getJavaScriptRuntimeDisplayName(launchConfig, 'deno', denoDisplayName, denoLabel),
    // Deno runs TypeScript and JSX/TSX natively, so it supports the same file types as Bun.
    getSupportedFileTypes: () => [...jsRuntimeBaseFileTypes, '.jsx', '.tsx'],
    getProjectFile: (launchConfig) => getJavaScriptRuntimeTargetPath(asDenoConfig(launchConfig)),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, _launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        const config = asDenoConfig(launchConfig);
        debugConfiguration.type = 'pwa-node';
        debugConfiguration.outputCapture = 'std';

        if (config.working_directory) {
            debugConfiguration.cwd = config.working_directory;
        }

        // Deno is always launched as `deno <subcommand> [flags] <entrypoint> [script-args]`: the hosting
        // side emits the complete argument vector (run/task/serve mode already resolved), so — unlike
        // node/bun — there is no separate "program" file to hoist. Drive js-debug purely through
        // runtimeExecutable + runtimeArgs and let it attach to the inspector.
        debugConfiguration.runtimeExecutable = config.runtime_executable || 'deno';

        const { runtimeArgs, port } = withDenoInspectWait(args ?? []);
        debugConfiguration.runtimeArgs = runtimeArgs;

        // attachSimplePort tells js-debug to spawn the runtime and then attach to this inspector port
        // rather than expecting a Node bootstrap. Paired with --inspect-wait this is the reliable
        // Deno attach path.
        debugConfiguration.attachSimplePort = port;

        // program/args are meaningless for the pwa-node simple-attach path; remove any defaults set
        // upstream so js-debug does not try to launch a node script.
        delete debugConfiguration.program;
        delete debugConfiguration.args;

        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
    }
};
