import * as net from 'net';
import * as path from 'path';
import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, JavaScriptRuntimeLaunchConfiguration, LaunchOptions, isJavaScriptRuntimeLaunchConfiguration } from "../../dcp/types";
import { denoDisplayName, denoInspectorPortAllocationFailed, denoLabel, denoTaskDebuggingUnsupported, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { getJavaScriptRuntimeDisplayName, getJavaScriptRuntimeTargetPath, jsRuntimeBaseFileTypes, launchMethodDirect, launchMethodPackageManager, resolveJavaScriptLaunchMethod } from "./javascriptRuntime";

// Deno exposes a V8 inspector; --inspect-wait blocks execution until a debugger attaches (unlike
// --inspect-brk it guarantees no early code — including module top-level — runs before attach, which
// is what makes IDE attach reliable).
const denoInspectorHost = '127.0.0.1';
const reservedDenoInspectorPorts = new Set<number>();

// Deno sub-commands that accept runtime flags (so --inspect-wait must be inserted AFTER this token,
// not before it — `deno --inspect-wait run` is invalid).
const denoSubcommandsAcceptingRuntimeFlags = new Set(['run', 'serve', 'test', 'bench']);
const denoFlagsWithSeparateValue = new Set(['--cert', '--config', '--env-file', '--import-map', '--lock', '--location', '--v8-flags']);

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

function findDenoInspectFlag(args: string[], config: JavaScriptRuntimeLaunchConfiguration): DenoInspectFlag | undefined {
    const runtimeFlagEnd = getDenoRuntimeFlagEndIndex(args, config);
    for (let index = 0; index < runtimeFlagEnd; index++) {
        const arg = args[index];
        const explicitPortMatch = /^(--inspect(?:-brk|-wait)?)=(?:.*:)?(\d+)$/.exec(arg);
        if (explicitPortMatch) {
            const explicitPort = Number(explicitPortMatch[2]);
            return {
                index,
                flagName: explicitPortMatch[1],
                // Deno treats an explicit port of 0 as "choose an ephemeral port", so the inspector ends up on
                // an unknown nonzero port (verified on 2.9.0: `--inspect=127.0.0.1:0` reported ports 61501,
                // 61835 and 61931 across runs). Report it as unresolved so the caller substitutes a concrete
                // allocated port; otherwise js-debug would attach to port 0 and never connect.
                port: explicitPort === 0 ? undefined : explicitPort
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

function getDenoRuntimeFlagEndIndex(args: string[], config: JavaScriptRuntimeLaunchConfiguration): number {
    const startIndex = args.length > 0 && denoSubcommandsAcceptingRuntimeFlags.has(args[0]) ? 1 : 0;
    for (let index = startIndex; index < args.length; index++) {
        const arg = args[index];
        if (isDenoFlagWithSeparateValue(arg)) {
            index++;
            continue;
        }

        if (!arg.startsWith('-') && isDenoEntrypoint(arg, config)) {
            return index;
        }
    }

    return args.length;
}

function isDenoFlagWithSeparateValue(arg: string): boolean {
    const equalsIndex = arg.indexOf('=');
    const flagName = equalsIndex >= 0 ? arg.substring(0, equalsIndex) : arg;
    return equalsIndex < 0 && denoFlagsWithSeparateValue.has(flagName);
}

function isDenoEntrypoint(arg: string, config: JavaScriptRuntimeLaunchConfiguration): boolean {
    const scriptPath = config.script_path;
    if (!scriptPath) {
        return true;
    }

    if (arePathsEqual(arg, scriptPath)) {
        return true;
    }

    if (!config.working_directory) {
        return false;
    }

    const candidatePath = path.isAbsolute(arg)
        ? arg
        : path.join(config.working_directory, arg);
    return arePathsEqual(candidatePath, scriptPath);
}

function arePathsEqual(left: string, right: string): boolean {
    const normalizedLeft = path.normalize(left);
    const normalizedRight = path.normalize(right);
    return process.platform === 'win32'
        ? normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
        : normalizedLeft === normalizedRight;
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
        if (session.configuration.runId === launchOptions.runId &&
            session.configuration.debugSessionId === launchOptions.debugSessionId) {
            disposeRelease();
        }
    });

    launchOptions.debugSession.registerResourceCleanup({
        dispose: disposeRelease
    });
}

/**
 * Injects `--inspect-wait` into a Deno argument vector so VS Code's built-in js-debug (pwa-node) can
 * attach. The flag is placed immediately after a leading sub-command that accepts runtime flags
 * (run/serve/test/bench) so it is parsed as a runtime flag rather than a script argument. `deno task`
 * does not accept inspector flags, so debug task launches fail fast instead of starting a
 * nonfunctional attach session. No-debug launches are left unchanged. An existing inspector flag
 * with a concrete nonzero port is preserved; a bare flag or port 0 is rewritten with an allocated
 * port so js-debug has a usable attach target.
 */
async function withDenoInspectWait(args: string[], config: JavaScriptRuntimeLaunchConfiguration, launchOptions: LaunchOptions): Promise<{ runtimeArgs: string[]; port?: number }> {
    if (!launchOptions.debug) {
        return { runtimeArgs: [...args] };
    }

    if (args[0] === 'task') {
        extensionLogOutputChannel.info('Skipping Deno debug launch for deno task because Deno does not accept runtime inspector flags on the task subcommand.');
        throw new Error(denoTaskDebuggingUnsupported);
    }

    const existingInspectFlag = findDenoInspectFlag(args, config);
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

        const launchMethod = resolveJavaScriptLaunchMethod(
            config,
            () => args?.[0] === 'task' ? launchMethodPackageManager : launchMethodDirect);
        if (launchOptions.debug && launchMethod === launchMethodPackageManager) {
            extensionLogOutputChannel.info('Skipping Deno debug launch for a package-manager task because Deno does not accept runtime inspector flags on the task subcommand.');
            throw new Error(denoTaskDebuggingUnsupported);
        }

        const { runtimeArgs, port } = await withDenoInspectWait(args ?? [], config, launchOptions);
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
