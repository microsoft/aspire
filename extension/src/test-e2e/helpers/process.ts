import { spawn, spawnSync } from 'child_process';

export interface RunProcessOptions {
    cwd?: string;
    env?: NodeJS.ProcessEnv;
    timeoutMs?: number;
    rejectOnNonZeroExit?: boolean;
}

export interface RunProcessResult {
    exitCode: number | null;
    signal: NodeJS.Signals | null;
    stdout: string;
    stderr: string;
}

export class ProcessError extends Error {
    constructor(message: string, public readonly result: RunProcessResult) {
        super(`${message}\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`);
    }
}

export function runProcess(file: string, args: readonly string[], options: RunProcessOptions = {}): Promise<RunProcessResult> {
    const timeoutMs = options.timeoutMs ?? 120000;

    return new Promise((resolve, reject) => {
        const child = spawn(file, [...args], {
            cwd: options.cwd,
            env: { ...process.env, ...options.env },
            shell: false,
            detached: process.platform !== 'win32',
        });

        const stdout: string[] = [];
        const stderr: string[] = [];
        let settled = false;
        let timedOut = false;
        let forceKillTimeout: NodeJS.Timeout | undefined;

        const timeout = setTimeout(() => {
            if (!settled) {
                timedOut = true;
                terminateProcessTree(child.pid, 'SIGTERM');
                forceKillTimeout = setTimeout(() => {
                    if (!settled) {
                        terminateProcessTree(child.pid, 'SIGKILL');
                        rejectWithResult(`${file} ${args.join(' ')} timed out after ${timeoutMs}ms.`, null, 'SIGKILL');
                    }
                }, 5000);
            }
        }, timeoutMs);

        // Drain both streams as data arrives so a verbose CLI cannot block on a full pipe.
        child.stdout.on('data', chunk => stdout.push(chunk.toString()));
        child.stderr.on('data', chunk => stderr.push(chunk.toString()));
        child.on('error', error => {
            settle();
            reject(error);
        });
        child.on('close', (exitCode, signal) => {
            if (settled) {
                return;
            }

            const result: RunProcessResult = {
                exitCode,
                signal,
                stdout: stdout.join(''),
                stderr: stderr.join(''),
            };

            settle();
            if (timedOut) {
                reject(new ProcessError(`${file} ${args.join(' ')} timed out after ${timeoutMs}ms.`, result));
                return;
            }

            if (signal !== null) {
                reject(new ProcessError(`${file} ${args.join(' ')} was terminated by ${signal}.`, result));
                return;
            }

            if ((options.rejectOnNonZeroExit ?? true) && exitCode !== 0) {
                reject(new ProcessError(`${file} ${args.join(' ')} exited with code ${exitCode}.`, result));
                return;
            }

            resolve(result);
        });

        function rejectWithResult(message: string, exitCode: number | null, signal: NodeJS.Signals | null): void {
            const result: RunProcessResult = {
                exitCode,
                signal,
                stdout: stdout.join(''),
                stderr: stderr.join(''),
            };

            settle();
            reject(new ProcessError(message, result));
        }

        function settle(): void {
            settled = true;
            clearTimeout(timeout);
            if (forceKillTimeout) {
                clearTimeout(forceKillTimeout);
            }
        }
    });
}

export function terminateProcessTree(pid: number | undefined, signal: NodeJS.Signals): void {
    if (pid === undefined) {
        return;
    }

    if (process.platform === 'win32') {
        spawnSync('taskkill', ['/pid', String(pid), '/t', '/f'], { stdio: 'ignore', timeout: 15000 });
        return;
    }

    try {
        process.kill(-pid, signal);
    } catch {
        try {
            process.kill(pid, signal);
        } catch {
            // The process may have exited between timeout detection and cleanup.
        }
    }
}
