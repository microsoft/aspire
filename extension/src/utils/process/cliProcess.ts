import { ChildProcessWithoutNullStreams, spawn } from "child_process";
import { EnvVar } from "../../dcp/types";
import { extensionLogOutputChannel } from "../../utils/logging";
import { AspireTerminalProvider } from "../../utils/AspireTerminalProvider";
import { CmdShimSpawnCommand, getCmdShimSpawnCommand, shouldWrapWithCmd } from "../../utils/cmdShim";
import * as readline from 'readline';
import * as vscode from 'vscode';
import { EnvironmentVariables } from "../../utils/environment";

const processShutdownGracePeriodMs = 5_000;
const processShutdownConfirmationIntervalMs = 50;
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
    return `Spawning Aspire CLI process: ${[command, ...redactCliArgsForLogging(args)].join(' ')}; cwd=${workingDirectory}; noDebug=${noDebug}; debugSessionId=${debugSessionId}; ${EnvironmentVariables.ASPIRE_CLI_START_TIMEOUT}=${startupTimeout}`;
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

export function terminateCliProcess(childProcess: ChildProcessWithoutNullStreams, description: string, options?: { suppressTimeoutWarning?: boolean; force?: boolean }): Promise<void> {
    if (process.platform === 'win32') {
        return terminateWindowsCliProcess(childProcess, description, options);
    }

    return new Promise(resolve => {
        const processGroupPid = managedPosixProcessGroups.has(childProcess)
            ? childProcess.pid
            : undefined;
        let exited = childProcess.exitCode !== null || childProcess.signalCode !== null;
        let forceKillTimer: ReturnType<typeof setTimeout> | undefined;
        let confirmationTimer: ReturnType<typeof setTimeout> | undefined;
        let confirmationDeadline: number | undefined;
        let forceSignalSent = false;
        let settled = false;
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
            if (settled) {
                return;
            }

            settled = true;
            exited = true;
            childProcess.off('close', onExit);
            childProcess.off('exit', onExit);
            if (forceKillTimer) {
                clearTimeout(forceKillTimer);
                forceKillTimer = undefined;
            }
            if (confirmationTimer) {
                clearTimeout(confirmationTimer);
                confirmationTimer = undefined;
            }
            managedPosixProcessGroups.delete(childProcess);
            resolve();
        };
        const scheduleProcessGroupConfirmation = () => {
            if (settled || confirmationTimer) {
                return;
            }

            confirmationDeadline ??= Date.now() + processShutdownGracePeriodMs;
            confirmationTimer = setTimeout(confirmProcessGroupExit, processShutdownConfirmationIntervalMs);
            confirmationTimer.unref();
        };
        const confirmProcessGroupExit = () => {
            confirmationTimer = undefined;
            if (settled) {
                return;
            }

            if (!hasLiveProcessGroup()) {
                stopTracking();
                return;
            }

            if (confirmationDeadline !== undefined && Date.now() >= confirmationDeadline) {
                if (!options?.suppressTimeoutWarning) {
                    extensionLogOutputChannel.warn(`${description} process group remained live after forced termination; stopping process tracking.`);
                }
                stopTracking();
                return;
            }

            scheduleProcessGroupConfirmation();
        };
        const onExit = () => {
            if (settled) {
                return;
            }

            exited = true;
            if (forceKillTimer) {
                clearTimeout(forceKillTimer);
                forceKillTimer = undefined;
            }
            if (processGroupPid !== undefined) {
                let processGroupAlive = hasLiveProcessGroup();
                if (!forceSignalSent && processGroupAlive) {
                    // Once the leader exits, force any remaining descendants immediately. Delaying another
                    // negative-PID signal would allow the operating system to recycle the process-group ID.
                    forceTermination();
                    processGroupAlive = hasLiveProcessGroup();
                }

                if (processGroupAlive) {
                    scheduleProcessGroupConfirmation();
                    return;
                }
            }

            stopTracking();
        };

        if (!exited) {
            childProcess.once('close', onExit);
            childProcess.once('exit', onExit);
        } else {
            if (processGroupPid !== undefined) {
                if (hasLiveProcessGroup()) {
                    forceTermination();
                }
                if (hasLiveProcessGroup()) {
                    scheduleProcessGroupConfirmation();
                    return;
                }
            }
            stopTracking();
            return;
        }

        if (options?.force) {
            if (!forceTermination()) {
                stopTracking();
                return;
            }
            if (processGroupPid !== undefined) {
                scheduleProcessGroupConfirmation();
            } else {
                confirmationTimer = setTimeout(() => {
                    confirmationTimer = undefined;
                    if (!options.suppressTimeoutWarning) {
                        extensionLogOutputChannel.warn(`${description} did not report exit after forced termination; stopping process tracking.`);
                    }
                    stopTracking();
                }, processShutdownGracePeriodMs);
                confirmationTimer.unref();
            }
            return;
        }

        try {
            if (!childProcess.killed) {
                const signalSent = terminateCliProcessTree(childProcess, false);
                if (!signalSent) {
                    extensionLogOutputChannel.warn(`Failed to terminate ${description}.`);
                    if (childProcess.pid === undefined) {
                        stopTracking();
                        return;
                    }
                }
            }
        } catch (error) {
            extensionLogOutputChannel.error(`Failed to terminate ${description}: ${String(error)}`);
            if (childProcess.pid === undefined) {
                stopTracking();
                return;
            }
        }

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
                return;
            }
            if (processGroupPid !== undefined) {
                scheduleProcessGroupConfirmation();
            } else {
                confirmationTimer = setTimeout(() => {
                    confirmationTimer = undefined;
                    if (!options?.suppressTimeoutWarning) {
                        extensionLogOutputChannel.warn(`${description} did not report exit after forced termination; stopping process tracking.`);
                    }
                    stopTracking();
                }, processShutdownGracePeriodMs);
                confirmationTimer.unref();
            }
        }, processShutdownGracePeriodMs);
        forceKillTimer.unref();
    });
}

function terminateCliProcessTree(childProcess: ChildProcessWithoutNullStreams, force: boolean): boolean {
    if (managedPosixProcessGroups.has(childProcess) && childProcess.pid !== undefined) {
        try {
            // A detached POSIX child is a process-group leader. Signaling its negative PID
            // terminates Aspire and its descendants together.
            // https://nodejs.org/api/child_process.html#optionsdetached
            return process.kill(-childProcess.pid, force ? 'SIGKILL' : 'SIGTERM');
        } catch (error) {
            if (isNoSuchProcessError(error)) {
                return true;
            }

            throw error;
        }
    }

    return childProcess.kill(force ? 'SIGKILL' : undefined);
}

async function terminateWindowsCliProcess(
    childProcess: ChildProcessWithoutNullStreams,
    description: string,
    options?: { suppressTimeoutWarning?: boolean; force?: boolean }
): Promise<void> {
    if (childProcess.exitCode !== null || childProcess.signalCode !== null) {
        return;
    }

    if (childProcess.pid === undefined) {
        childProcess.kill(options?.force ? 'SIGKILL' : undefined);
        await waitForChildProcessExit(childProcess, processShutdownGracePeriodMs);
        return;
    }

    const childExit = observeChildProcessExit(childProcess);
    try {
        await runTaskkill(childProcess, options?.force === true);
        if (await childExit.wait(processShutdownGracePeriodMs)) {
            return;
        }

        if (!options?.force) {
            if (!options?.suppressTimeoutWarning) {
                extensionLogOutputChannel.warn(`${description} did not exit within ${processShutdownGracePeriodMs}ms; forcing termination.`);
            }

            await runTaskkill(childProcess, true);
            if (await childExit.wait(processShutdownGracePeriodMs)) {
                return;
            }
        }

        if (!options?.suppressTimeoutWarning) {
            extensionLogOutputChannel.warn(`${description} did not report exit after forced termination; stopping process tracking.`);
        }
    }
    finally {
        childExit.dispose();
    }
}

function runTaskkill(childProcess: ChildProcessWithoutNullStreams, force: boolean): Promise<void> {
    return new Promise(resolve => {
        const args = ['/pid', String(childProcess.pid), '/t'];
        if (force) {
            args.push('/f');
        }

        const taskkill = spawn('taskkill.exe', args, {
            stdio: 'ignore',
            windowsHide: true,
        });
        let completed = false;
        const complete = () => {
            if (completed) {
                return;
            }

            completed = true;
            taskkill.off('error', onError);
            taskkill.off('close', complete);
            resolve();
        };
        const onError = (error: Error) => {
            extensionLogOutputChannel.warn(`Failed to stop process tree for PID ${childProcess.pid}: ${error}`);
            childProcess.kill();
            complete();
        };

        taskkill.once('error', onError);
        taskkill.once('close', complete);
    });
}

function isPosixProcessGroupAlive(pid: number): boolean {
    try {
        return process.kill(-pid, 0);
    } catch (error) {
        return error instanceof Error && 'code' in error && error.code === 'EPERM';
    }
}

function isNoSuchProcessError(error: unknown): boolean {
    return error instanceof Error && 'code' in error && error.code === 'ESRCH';
}

function observeChildProcessExit(childProcess: ChildProcessWithoutNullStreams): {
    wait(timeoutMs: number): Promise<boolean>;
    dispose(): void;
} {
    let exited = childProcess.exitCode !== null || childProcess.signalCode !== null;
    let resolveExit: (() => void) | undefined;
    const exitPromise = new Promise<void>(resolve => {
        resolveExit = resolve;
    });
    const onExit = () => {
        if (exited) {
            return;
        }

        exited = true;
        childProcess.off('close', onExit);
        childProcess.off('exit', onExit);
        resolveExit?.();
    };
    if (!exited) {
        childProcess.once('close', onExit);
        childProcess.once('exit', onExit);
    }

    return {
        async wait(timeoutMs: number): Promise<boolean> {
            if (exited) {
                return true;
            }

            return await new Promise(resolve => {
                const timer = setTimeout(() => resolve(false), timeoutMs);
                timer.unref();
                void exitPromise.then(() => {
                    clearTimeout(timer);
                    resolve(true);
                });
            });
        },
        dispose(): void {
            childProcess.off('close', onExit);
            childProcess.off('exit', onExit);
        },
    };
}

async function waitForChildProcessExit(childProcess: ChildProcessWithoutNullStreams, timeoutMs: number): Promise<boolean> {
    const childExit = observeChildProcessExit(childProcess);
    try {
        return await childExit.wait(timeoutMs);
    }
    finally {
        childExit.dispose();
    }
}

export function redactCliArgsForLogging(args: string[] | undefined): string[] {
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
