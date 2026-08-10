import { ChildProcessWithoutNullStreams, spawn } from "child_process";
import { EnvVar } from "../../dcp/types";
import { extensionLogOutputChannel } from "../../utils/logging";
import { AspireTerminalProvider } from "../../utils/AspireTerminalProvider";
import { CmdShimSpawnCommand, getCmdShimSpawnCommand, shouldWrapWithCmd } from "../../utils/cmdShim";
import * as readline from 'readline';
import * as vscode from 'vscode';
import { EnvironmentVariables } from "../../utils/environment";

const processShutdownGracePeriodMs = 5_000;
const managedPosixProcessGroups = new WeakSet<ChildProcessWithoutNullStreams>();

export interface SpawnProcessOptions {
    stdoutCallback?: (data: string) => void;
    stderrCallback?: (data: string) => void;
    exitCallback?: (code: number | null) => void;
    errorCallback?: (error: Error) => void;
    lineCallback?: (line: string) => void;
    env?: EnvVar[];
    workingDirectory?: string;
    debugSessionId?: string,
    noDebug?: boolean;
    noExtensionVariables?: boolean;
    createProcessGroup?: boolean;
}

export type CliSpawnCommand = CmdShimSpawnCommand;

export function getCliSpawnCommand(command: string, args?: string[]): CliSpawnCommand {
    if (shouldWrapWithCmd(command)) {
        return getCmdShimSpawnCommand(command, args ?? []);
    }

    return { command, args: args ?? [] };
}

export function getCliSpawnDiagnostics(command: string, args: string[] | undefined, workingDirectory: string, noDebug: boolean | undefined, debugSessionId: string | undefined, env: Record<string, string | undefined>): string {
    const startupTimeout = getEnvironmentValue(env, EnvironmentVariables.ASPIRE_CLI_START_TIMEOUT);
    return `Spawning Aspire CLI process: ${[command, ...redactCliSpawnArgs(args)].join(' ')}; cwd=${workingDirectory}; noDebug=${noDebug}; debugSessionId=${debugSessionId}; ${EnvironmentVariables.ASPIRE_CLI_START_TIMEOUT}=${startupTimeout}`;
}

export function mergeCliSpawnEnvironment(env: Record<string, string | undefined>, envVars?: EnvVar[]): void {
    if (!envVars) {
        return;
    }

    for (const e of envVars) {
        if (process.platform === 'win32') {
            const incomingKey = e.name.toLowerCase();
            const existingKeys = Object.keys(env).filter(key => key.toLowerCase() === incomingKey && key !== e.name);
            for (const key of existingKeys) {
                delete env[key];
            }
        }

        env[e.name] = e.value;
    }
}

export function spawnCliProcess(terminalProvider: AspireTerminalProvider, command: string, args?: string[], options?: SpawnProcessOptions): ChildProcessWithoutNullStreams {
    const workingDirectory = options?.workingDirectory ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
    const env: Record<string, string | undefined> = {};
    const spawnCommand = getCliSpawnCommand(command, args);

    Object.assign(env, terminalProvider.createEnvironment(options?.debugSessionId, options?.noDebug, options?.noExtensionVariables));
    mergeCliSpawnEnvironment(env, options?.env);

    extensionLogOutputChannel.info(getCliSpawnDiagnostics(spawnCommand.command, spawnCommand.diagnosticArgs ?? spawnCommand.args, workingDirectory, options?.noDebug, options?.debugSessionId, env));

    const createProcessGroup = process.platform !== 'win32' && options?.createProcessGroup === true;
    const child = spawn(spawnCommand.command, spawnCommand.args, {
        cwd: workingDirectory,
        env: env,
        shell: false,
        detached: createProcessGroup,
        windowsVerbatimArguments: spawnCommand.windowsVerbatimArguments,
    });
    if (createProcessGroup) {
        managedPosixProcessGroups.add(child);
    }

    // Set UTF-8 encoding so Node reassembles multi-byte characters across chunk boundaries instead of yielding broken bytes.
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');

    if (options?.lineCallback) {
        const rl = readline.createInterface(child.stdout);
        rl.on('line', line => {
            options?.lineCallback?.(line);
        });
    }

    child.stdout.on("data", (data: string) => {
        options?.stdoutCallback?.(data);
    });

    child.stderr.on("data", (data: string) => {
        options?.stderrCallback?.(data);
    });

    child.on('error', (error) => {
        options?.errorCallback?.(error);
    });

    child.on("close", (code) => {
        options?.exitCallback?.(code);
    });

    return child;
}

export function terminateCliProcess(childProcess: ChildProcessWithoutNullStreams, description: string, options?: { suppressTimeoutWarning?: boolean }): void {
    const processGroupPid = process.platform !== 'win32' && managedPosixProcessGroups.has(childProcess)
        ? childProcess.pid
        : undefined;
    let exited = childProcess.exitCode !== null || childProcess.signalCode !== null;
    let forceKillTimer: ReturnType<typeof setTimeout> | undefined;
    let forceSignalSent = false;
    const hasLiveProcessGroup = () => processGroupPid !== undefined && isPosixProcessGroupAlive(processGroupPid);
    const forceTermination = (): boolean => {
        if (forceSignalSent) {
            return true;
        }

        try {
            forceSignalSent = terminateCliProcessTree(childProcess, true);
            if (!forceSignalSent) {
                extensionLogOutputChannel.warn(`Failed to forcefully terminate ${description}.`);
            }
        } catch (error) {
            extensionLogOutputChannel.error(`Failed to forcefully terminate ${description}: ${String(error)}`);
        }

        return forceSignalSent;
    };
    const stopTracking = () => {
        exited = true;
        childProcess.off('close', onExit);
        childProcess.off('exit', onExit);
        if (forceKillTimer) {
            clearTimeout(forceKillTimer);
            forceKillTimer = undefined;
        }
    };
    const onExit = () => {
        stopTracking();
        if (processGroupPid !== undefined && !forceSignalSent && hasLiveProcessGroup()) {
            // Once the leader exits, force any remaining descendants immediately. Delaying another
            // negative-PID signal would allow the operating system to recycle the process-group ID.
            forceTermination();
        }
        managedPosixProcessGroups.delete(childProcess);
    };

    if (!exited) {
        childProcess.once('close', onExit);
        childProcess.once('exit', onExit);
    } else {
        if (processGroupPid !== undefined) {
            if (hasLiveProcessGroup()) {
                forceTermination();
            }
            managedPosixProcessGroups.delete(childProcess);
        }
        return;
    }

    try {
        if (!childProcess.killed) {
            const signalSent = terminateCliProcessTree(childProcess, false);
            if (!signalSent) {
                extensionLogOutputChannel.warn(`Failed to terminate ${description}.`);
                onExit();
                return;
            }
        }
    } catch (error) {
        extensionLogOutputChannel.error(`Failed to terminate ${description}: ${String(error)}`);
        onExit();
        return;
    }

    if (!exited) {
        forceKillTimer = setTimeout(() => {
            forceKillTimer = undefined;
            if (exited) {
                return;
            }

            if (childProcess.exitCode !== null || childProcess.signalCode !== null) {
                onExit();
                return;
            }

            if (!options?.suppressTimeoutWarning) {
                extensionLogOutputChannel.warn(`${description} did not exit within ${processShutdownGracePeriodMs}ms; forcing termination.`);
            }

            if (!forceTermination()) {
                stopTracking();
            }
        }, processShutdownGracePeriodMs);
        forceKillTimer.unref();
    }
}

function terminateCliProcessTree(childProcess: ChildProcessWithoutNullStreams, force: boolean): boolean {
    if (process.platform !== 'win32') {
        if (managedPosixProcessGroups.has(childProcess) && childProcess.pid !== undefined) {
            try {
                // A detached POSIX child is a process-group leader. Signaling its negative PID
                // terminates Aspire and its descendants together.
                // https://nodejs.org/api/child_process.html#optionsdetached
                return process.kill(-childProcess.pid, force ? 'SIGKILL' : 'SIGTERM');
            } catch {
                // The group may have exited between the liveness check and signal delivery.
            }
        }

        return childProcess.kill(force ? 'SIGKILL' : undefined);
    }

    if (childProcess.pid === undefined) {
        return childProcess.kill(force ? 'SIGKILL' : undefined);
    }

    const args = ['/pid', String(childProcess.pid), '/t'];
    if (force) {
        args.push('/f');
    }

    const taskkill = spawn('taskkill.exe', args, {
        stdio: 'ignore',
        windowsHide: true,
    });
    taskkill.on('error', error => {
        extensionLogOutputChannel.warn(`Failed to stop process tree for PID ${childProcess.pid}: ${error}`);
        childProcess.kill();
    });
    taskkill.unref();

    return true;
}

function isPosixProcessGroupAlive(pid: number): boolean {
    try {
        return process.kill(-pid, 0);
    } catch (error) {
        return error instanceof Error && 'code' in error && error.code === 'EPERM';
    }
}

function redactCliSpawnArgs(args: string[] | undefined): string[] {
    if (!args) {
        return [];
    }

    const delimiterIndex = args.indexOf('--');
    if (delimiterIndex === -1) {
        return args;
    }

    // Resource command arguments after "--" can include values collected from secret prompts.
    // Keep the stable command shape that helps diagnose debug launches, but do not persist
    // user-provided command values in the extension log.
    return [...args.slice(0, delimiterIndex + 1), '<redacted>'];
}

function getEnvironmentValue(env: Record<string, string | undefined>, key: string): string | undefined {
    if (process.platform !== 'win32' || env[key] !== undefined) {
        return env[key];
    }

    const matchingKey = Object.keys(env).find(k => k.toLowerCase() === key.toLowerCase());
    return matchingKey ? env[matchingKey] : undefined;
}
