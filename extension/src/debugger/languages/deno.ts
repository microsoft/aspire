import * as net from 'net';
import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, LaunchOptions, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { denoDisplayName, denoInspectorPortAllocationFailed, denoLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { registerRunCleanup } from "../runCleanupRegistry";
import { getJavaScriptRuntimeDisplayName, getJavaScriptRuntimeTargetPath, jsRuntimeBaseFileTypes } from "./javascriptRuntime";

// Deno exposes a V8 inspector; --inspect-wait blocks execution until a debugger attaches (unlike
// --inspect-brk it guarantees no early code — including module top-level — runs before attach, which
// is what makes IDE attach reliable).
const denoInspectorHost = '127.0.0.1';
const reservedDenoInspectorPorts = new Set<number>();

// Deno sub-commands that accept runtime flags (so --inspect-wait must be inserted AFTER this token,
// not before it — `deno --inspect-wait run` is invalid).
const denoSubcommandsAcceptingRuntimeFlags = new Set(['run', 'serve', 'test', 'bench']);

function asDenoConfig(launchConfig: ExecutableLaunchConfiguration): JavaScriptRuntimeLaunchConfiguration {
    if (isJavaScriptRuntimeLaunchConfiguration(launchConfig) && launchConfig.type === 'deno') {
        return launchConfig;
    }

    extensionLogOutputChannel.info(`The resource type was not deno for ${JSON.stringify(launchConfig)}`);
    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

interface DenoInspectFlag {
    index: number;
    flagName: string;
    port?: number;
}

function findDenoInspectFlag(args: string[]): DenoInspectFlag | undefined {
    for (let index = 0; index < args.length; index++) {
        const arg = args[index];
        const explicitPortMatch = /^(--inspect(?:-brk|-wait)?)=(?:.*:)?(\d+)$/.exec(arg);
        if (explicitPortMatch) {
            return {
                index,
                flagName: explicitPortMatch[1],
                port: Number(explicitPortMatch[2])
            };
        }

        const bareMatch = /^(--inspect(?:-brk|-wait)?)$/.exec(arg);
        if (bareMatch) {
            return {
                index,
                flagName: bareMatch[1],
                port: undefined
            };
        }
    }

    return undefined;
}

async function getAvailableTcpPort(): Promise<number> {
    return await new Promise<number>((resolve, reject) => {
        const server = net.createServer();
        server.unref();
        server.once('error', reject);
        server.listen(0, denoInspectorHost, () => {
            const address = server.address();
            if (!address || typeof address === 'string') {
                server.close();
                reject(new Error(denoInspectorPortAllocationFailed));
                return;
            }

            const port = address.port;
            server.close(error => error ? reject(error) : resolve(port));
        });
    });
}

async function allocateDenoInspectorPort(): Promise<number> {
    for (let attempt = 0; attempt < 20; attempt++) {
        const port = await getAvailableTcpPort();
        if (!reservedDenoInspectorPorts.has(port)) {
            reservedDenoInspectorPorts.add(port);
            return port;
        }
    }

    throw new Error(denoInspectorPortAllocationFailed);
}

function registerDenoInspectorPortRelease(port: number, launchOptions: LaunchOptions): void {
    let released = false;
    const releasePort = () => {
        if (!released) {
            released = true;
            reservedDenoInspectorPorts.delete(port);
        }
    };

    let debugSessionTermination: vscode.Disposable | undefined;
    const disposeRelease = () => {
        releasePort();
        debugSessionTermination?.dispose();
    };

    debugSessionTermination = vscode.debug.onDidTerminateDebugSession(session => {
        if (session.configuration.runId === launchOptions.runId) {
            disposeRelease();
        }
    });

    registerRunCleanup(launchOptions.runId, disposeRelease);
    launchOptions.debugSession.registerResourceCleanup({
        dispose: disposeRelease
    });
}

/**
 * Injects `--inspect-wait` into a Deno argument vector so VS Code's built-in js-debug (pwa-node) can
 * attach. The flag is placed immediately after a leading sub-command that accepts runtime flags
 * (run/serve/test/bench) so it is parsed as a runtime flag rather than a script argument. `deno task`
 * does not accept inspector flags, so task launches are left unchanged instead of generating an
 * invalid command line. If the caller already configured an inspector flag (WithDenoInspect*), the
 * vector is returned unchanged.
 */
async function withDenoInspectWait(args: string[], launchOptions: LaunchOptions): Promise<{ runtimeArgs: string[]; port?: number }> {
    const existingInspectFlag = findDenoInspectFlag(args);
    if (existingInspectFlag?.port !== undefined) {
        return { runtimeArgs: [...args], port: existingInspectFlag.port };
    }

    if (existingInspectFlag !== undefined) {
        const port = await allocateDenoInspectorPort();
        registerDenoInspectorPortRelease(port, launchOptions);
        const runtimeArgs = [...args];
        runtimeArgs[existingInspectFlag.index] = `${existingInspectFlag.flagName}=${denoInspectorHost}:${port}`;
        return { runtimeArgs, port };
    }

    if (args[0] === 'task') {
        extensionLogOutputChannel.info('Skipping Deno inspector injection for deno task because Deno does not accept runtime inspector flags on the task subcommand.');
        return { runtimeArgs: [...args] };
    }

    const port = await allocateDenoInspectorPort();
    registerDenoInspectorPortRelease(port, launchOptions);
    const runtimeArgs = [...args];
    const insertAt = runtimeArgs.length > 0 && denoSubcommandsAcceptingRuntimeFlags.has(runtimeArgs[0]) ? 1 : 0;
    runtimeArgs.splice(insertAt, 0, `--inspect-wait=${denoInspectorHost}:${port}`);
    return { runtimeArgs, port };
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
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
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

        const { runtimeArgs, port } = await withDenoInspectWait(args ?? [], launchOptions);
        debugConfiguration.runtimeArgs = runtimeArgs;

        if (port !== undefined) {
            // attachSimplePort tells js-debug to spawn the runtime and then attach to this inspector port
            // rather than expecting a Node bootstrap. Paired with --inspect-wait this is the reliable
            // Deno attach path.
            debugConfiguration.attachSimplePort = port;
        }

        // program/args are meaningless for the pwa-node simple-attach path; remove any defaults set
        // upstream so js-debug does not try to launch a node script.
        delete debugConfiguration.program;
        delete debugConfiguration.args;

        debugConfiguration.resolveSourceMapLocations = ['**', '!**/node_modules/**'];
    }
};
