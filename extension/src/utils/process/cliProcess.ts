import { ChildProcessWithoutNullStreams, spawn } from "child_process";
import { EnvVar } from "../../dcp/types";
import { extensionLogOutputChannel } from "../../utils/logging";
import { AspireTerminalProvider } from "../../utils/AspireTerminalProvider";
import { CmdShimSpawnCommand, getCmdShimSpawnCommand, shouldWrapWithCmd } from "../../utils/cmdShim";
import * as readline from 'readline';
import * as vscode from 'vscode';
import { EnvironmentVariables } from "../../utils/environment";
import { overlayAspireExtensionEnvironment } from "../../utils/cliPathEnvironment";

const processShutdownGracePeriodMs = 5_000;
const processShutdownConfirmationIntervalMs = 50;
const windowsForcedTaskkillCloseReserveMs = 250;
const windowsForcedTaskkillTotalReserveMs = 1_250;
const windowsTaskkillProcessNotFoundExitCode = 128;
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

    Object.assign(env, terminalProvider.createEnvironment(options?.debugSessionId, options?.noDebug, options?.noExtensionVariables, command));
    mergeCliSpawnEnvironment(env, options?.env);
    // These values describe the extension instance that owns this child process, so stale
    // inherited values and caller-provided CLI options must not override the active identity.
    overlayAspireExtensionEnvironment(env, terminalProvider.aspireExtensionEnvironment);

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

    return new Promise((resolve, reject) => {
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
        const settle = (error?: Error) => {
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
            if (error) {
                reject(error);
            } else {
                resolve();
            }
        };
        const rejectUnconfirmedTermination = (message: string) => {
            const error = new Error(message);
            if (!options?.suppressTimeoutWarning) {
                extensionLogOutputChannel.warn(error.message);
            }
            settle(error);
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
                settle();
                return;
            }

            if (confirmationDeadline !== undefined && Date.now() >= confirmationDeadline) {
                rejectUnconfirmedTermination(`Could not confirm ${description} process group termination within ${processShutdownGracePeriodMs}ms.`);
                return;
            }

            scheduleProcessGroupConfirmation();
        };
        const scheduleChildExitConfirmation = () => {
            confirmationTimer = setTimeout(() => {
                confirmationTimer = undefined;
                rejectUnconfirmedTermination(`Could not confirm ${description} termination within ${processShutdownGracePeriodMs}ms after forced termination.`);
            }, processShutdownGracePeriodMs);
            confirmationTimer.unref();
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
                    if (!forceTermination()) {
                        settle(new Error(`Could not forcefully terminate ${description}.`));
                        return;
                    }
                    processGroupAlive = hasLiveProcessGroup();
                }

                if (processGroupAlive) {
                    scheduleProcessGroupConfirmation();
                    return;
                }
            }

            settle();
        };

        if (!exited) {
            childProcess.once('close', onExit);
            childProcess.once('exit', onExit);
        } else {
            if (processGroupPid !== undefined) {
                if (hasLiveProcessGroup()) {
                    if (!forceTermination()) {
                        settle(new Error(`Could not forcefully terminate ${description}.`));
                        return;
                    }
                }
                if (hasLiveProcessGroup()) {
                    scheduleProcessGroupConfirmation();
                    return;
                }
            }
            settle();
            return;
        }

        if (options?.force) {
            if (!forceTermination()) {
                settle(new Error(`Could not forcefully terminate ${description}.`));
                return;
            }
            if (processGroupPid !== undefined) {
                scheduleProcessGroupConfirmation();
            } else {
                scheduleChildExitConfirmation();
            }
            return;
        }

        try {
            if (!childProcess.killed) {
                const signalSent = terminateCliProcessTree(childProcess, false);
                if (!signalSent) {
                    extensionLogOutputChannel.warn(`Failed to terminate ${description}.`);
                    if (childProcess.pid === undefined) {
                        settle(new Error(`Could not terminate ${description} because no process identifier was available.`));
                        return;
                    }
                }
            }
        } catch (error) {
            extensionLogOutputChannel.error(`Failed to terminate ${description}: ${String(error)}`);
            if (childProcess.pid === undefined) {
                settle(new Error(`Could not terminate ${description} because no process identifier was available.`));
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
                settle(new Error(`Could not forcefully terminate ${description}.`));
                return;
            }
            if (processGroupPid !== undefined) {
                scheduleProcessGroupConfirmation();
            } else {
                scheduleChildExitConfirmation();
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

    const childClose = observeChildProcessClose(childProcess);
    const deadline = Date.now() + processShutdownGracePeriodMs;
    const remainingTime = () => Math.max(0, deadline - Date.now());
    const waitForClose = () => childClose.wait(remainingTime());
    const killLeaderAsFallback = async (taskkillError: unknown): Promise<void> => {
        if (childClose.hasClosed() || childProcess.exitCode !== null || childProcess.signalCode !== null) {
            return;
        }

        extensionLogOutputChannel.warn(
            `Failed to terminate ${description} with taskkill; falling back to the CLI leader process: ${String(taskkillError)}`);
        if (!childProcess.kill('SIGKILL')) {
            throw taskkillError;
        }
        if (remainingTime() === 0) {
            throw taskkillError;
        }
        if (!await waitForClose()) {
            throw new Error(`${description} did not report exit within ${processShutdownGracePeriodMs}ms after taskkill failed and the leader was killed.`);
        }
        // TerminateProcess only confirms the leader exited. Descendants may still be orphaned, so
        // preserve the taskkill failure instead of reporting successful process-tree cleanup.
        throw taskkillError;
    };
    try {
        if (childProcess.pid === undefined) {
            if (!childProcess.kill(options?.force ? 'SIGKILL' : undefined)) {
                throw new Error(`Could not terminate ${description} because no process identifier was available.`);
            }
            if (!await waitForClose()) {
                throw new Error(`${description} did not report exit within ${processShutdownGracePeriodMs}ms after termination.`);
            }
            return;
        }

        const runTaskkillWithinDeadline = async (force: boolean): Promise<number | null> => {
            try {
                const closeReserveMs = force ? windowsForcedTaskkillCloseReserveMs : 0;
                return await runTaskkill(
                    childProcess,
                    description,
                    force,
                    Math.max(0, remainingTime() - closeReserveMs));
            }
            catch (error) {
                await killLeaderAsFallback(error);
                throw error;
            }
        };

        const forceRequested = options?.force === true;
        const firstExitCode = await runTaskkillWithinDeadline(forceRequested);
        if (childClose.hasClosed()) {
            return;
        }

        if (firstExitCode === windowsTaskkillProcessNotFoundExitCode) {
            if (!await waitForClose()) {
                throw new Error(`taskkill could not find ${description} PID ${childProcess.pid}, but the process did not report exit within ${processShutdownGracePeriodMs}ms.`);
            }
            return;
        }

        if (!forceRequested) {
            // A nonzero graceful result means the tree was not terminated. Escalate immediately
            // instead of rejecting before `/f` gets a chance to clean up descendants.
            if (firstExitCode === 0) {
                const forcedTaskkillReserveMs = Math.min(windowsForcedTaskkillTotalReserveMs, remainingTime());
                if (await childClose.wait(Math.max(0, remainingTime() - forcedTaskkillReserveMs))) {
                    return;
                }
            }

            if (!options?.suppressTimeoutWarning) {
                extensionLogOutputChannel.warn(`${description} did not exit within ${processShutdownGracePeriodMs}ms; forcing termination.`);
            }

            const forcedExitCode = await runTaskkillWithinDeadline(true);
            if (childClose.hasClosed()) {
                return;
            }
            if (forcedExitCode === windowsTaskkillProcessNotFoundExitCode) {
                if (!await waitForClose()) {
                    throw new Error(`taskkill could not find ${description} PID ${childProcess.pid}, but the process did not report exit within ${processShutdownGracePeriodMs}ms.`);
                }
                return;
            }
            if (forcedExitCode !== 0) {
                await killLeaderAsFallback(new Error(
                    `taskkill for ${description} exited with code ${forcedExitCode} while PID ${childProcess.pid} remained live.`));
                return;
            }
        }
        else if (firstExitCode !== 0) {
            await killLeaderAsFallback(new Error(
                `taskkill for ${description} exited with code ${firstExitCode} while PID ${childProcess.pid} remained live.`));
            return;
        }

        if (!await waitForClose()) {
            throw new Error(`${description} did not report exit within ${processShutdownGracePeriodMs}ms after taskkill succeeded.`);
        }
    }
    finally {
        childClose.dispose();
    }
}

function runTaskkill(
    childProcess: ChildProcessWithoutNullStreams,
    description: string,
    force: boolean,
    timeoutMs: number
): Promise<number | null> {
    return new Promise((resolve, reject) => {
        const args = ['/pid', String(childProcess.pid), '/t'];
        if (force) {
            args.push('/f');
        }

        const taskkill = spawn('taskkill.exe', args, {
            stdio: 'ignore',
            windowsHide: true,
        });
        let settled = false;
        const timer = setTimeout(() => {
            if (settled) {
                return;
            }

            settled = true;
            taskkill.off('error', onError);
            taskkill.off('close', onClose);
            try {
                taskkill.kill('SIGKILL');
            } catch {
                // The bounded failure remains authoritative even if the helper exits during cleanup.
            }
            reject(new Error(`taskkill for ${description} did not exit within ${timeoutMs}ms.`));
        }, timeoutMs);
        timer.unref();
        const cleanup = () => {
            clearTimeout(timer);
            taskkill.off('error', onError);
            taskkill.off('close', onClose);
        };
        const onError = (error: Error) => {
            if (settled) {
                return;
            }

            settled = true;
            cleanup();
            reject(new Error(`Failed to run taskkill for ${description} (PID ${childProcess.pid}): ${error.message}`));
        };
        const onClose = (code: number | null) => {
            if (settled) {
                return;
            }

            settled = true;
            cleanup();
            resolve(code);
        };

        taskkill.once('error', onError);
        taskkill.once('close', onClose);
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

function observeChildProcessClose(childProcess: ChildProcessWithoutNullStreams): {
    hasClosed(): boolean;
    wait(timeoutMs: number): Promise<boolean>;
    dispose(): void;
} {
    let closed = false;
    let resolveClose: (() => void) | undefined;
    const closePromise = new Promise<void>(resolve => {
        resolveClose = resolve;
    });
    const onClose = () => {
        if (closed) {
            return;
        }

        closed = true;
        childProcess.off('close', onClose);
        resolveClose?.();
    };
    childProcess.once('close', onClose);

    return {
        hasClosed(): boolean {
            return closed;
        },
        async wait(timeoutMs: number): Promise<boolean> {
            if (closed) {
                return true;
            }

            return await new Promise(resolve => {
                let settled = false;
                const complete = (result: boolean) => {
                    if (settled) {
                        return;
                    }

                    settled = true;
                    clearTimeout(timer);
                    resolve(result);
                };
                const timer = setTimeout(() => complete(false), timeoutMs);
                timer.unref();
                void closePromise.then(() => complete(true));
            });
        },
        dispose(): void {
            childProcess.off('close', onClose);
        },
    };
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
