import { ChildProcessWithoutNullStreams, spawn } from "child_process";
import { EnvVar } from "../../dcp/types";
import { cliLogsOutputChannel } from "../../utils/logging";
import { AspireTerminalProvider } from "../../utils/AspireTerminalProvider";
import * as readline from 'readline';
import * as vscode from 'vscode';

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
    logToCliOutputChannel?: boolean;
}

export function withCliLogOutputChannelArgs(args: readonly string[] = []): string[] {
    const delimiterIndex = args.indexOf('--');
    const insertionIndex = delimiterIndex === -1 ? args.length : delimiterIndex;
    const cliArgs = args.slice(0, insertionIndex);
    const forwardedArgs = args.slice(insertionIndex);
    const updatedArgs = [...cliArgs];

    if (!cliArgs.includes('--debug')) {
        updatedArgs.push('--debug');
    }

    if (!cliArgs.includes('--no-log-file')) {
        updatedArgs.push('--no-log-file');
    }

    return [...updatedArgs, ...forwardedArgs];
}

export function spawnCliProcess(terminalProvider: AspireTerminalProvider, command: string, args?: string[], options?: SpawnProcessOptions): ChildProcessWithoutNullStreams {
    const workingDirectory = options?.workingDirectory ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
    const env = {};

    Object.assign(env, terminalProvider.createEnvironment(options?.debugSessionId, options?.noDebug, options?.noExtensionVariables));
    if (options?.env) {
        Object.assign(env, Object.fromEntries(options.env.map(e => [e.name, e.value])));
    }
    if (options?.logToCliOutputChannel) {
        args = withCliLogOutputChannelArgs(args);
        cliLogsOutputChannel.appendLine(`Spawning CLI process with command: ${command} ${args.join(' ')} in directory: ${workingDirectory}`);
    }

    const child = spawn(command, args ?? [], {
        cwd: workingDirectory,
        env: env,
        shell: false
    });

    if (options?.lineCallback) {
        const rl = readline.createInterface(child.stdout);
        rl.on('line', line => {
            options?.lineCallback?.(line);
        });
    }

    child.stdout.on("data", (data) => {
        options?.stdoutCallback?.(new String(data).toString());
    });

    child.stderr.on("data", (data) => {
        const text = new String(data).toString();
        options?.stderrCallback?.(text);
        if (options?.logToCliOutputChannel) {
            cliLogsOutputChannel.append(text);
        }
    });

    child.on('error', (error) => {
        if (options?.logToCliOutputChannel) {
            cliLogsOutputChannel.appendLine(`CLI process error: ${error.message} (command: ${command} ${(args ?? []).join(' ')})`);
        }
        options?.errorCallback?.(error);
    });

    child.on("close", (code) => {
        if (options?.logToCliOutputChannel) {
            cliLogsOutputChannel.appendLine(`CLI process exited with code ${code} (command: ${command} ${(args ?? []).join(' ')})`);
        }
        options?.exitCallback?.(code);
    });

    return child;
}
