import { ChildProcessWithoutNullStreams, spawn } from "child_process";
import { EnvVar } from "../../dcp/types";
import { extensionLogOutputChannel } from "../../utils/logging";
import { AspireTerminalProvider } from "../../utils/AspireTerminalProvider";
import * as readline from 'readline';
import * as vscode from 'vscode';
import { EnvironmentVariables } from "../../utils/environment";

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
}

export function getCliSpawnCommand(command: string, args?: string[]): { command: string; args: string[] } {
    if (process.platform === 'win32' && /\.(?:cmd|bat)$/i.test(command)) {
        return {
            command: process.env.ComSpec ?? 'cmd.exe',
            args: ['/d', '/c', 'call', command, ...args ?? []],
        };
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

    extensionLogOutputChannel.info(getCliSpawnDiagnostics(spawnCommand.command, spawnCommand.args, workingDirectory, options?.noDebug, options?.debugSessionId, env));

    const child = spawn(spawnCommand.command, spawnCommand.args, {
        cwd: workingDirectory,
        env: env,
        shell: false
    });

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
