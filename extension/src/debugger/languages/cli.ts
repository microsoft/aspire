import { ChildProcessWithoutNullStreams, spawn } from "child_process";
import { EnvVar } from "../../dcp/types";
import { extensionLogOutputChannel } from "../../utils/logging";
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
}

export function spawnCliProcess(terminalProvider: AspireTerminalProvider, command: string, args?: string[], options?: SpawnProcessOptions): ChildProcessWithoutNullStreams {
    const workingDirectory = options?.workingDirectory ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
    const env = {};

    Object.assign(env, terminalProvider.createEnvironment(options?.debugSessionId, options?.noDebug, options?.noExtensionVariables));
    if (options?.env) {
        Object.assign(env, Object.fromEntries(options.env.map(e => [e.name, e.value])));
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
        options?.stderrCallback?.(new String(data).toString());
    });

    child.on('error', (error) => {
        options?.errorCallback?.(error);
    });

    child.on("close", (code) => {
        options?.exitCallback?.(code);
    });

    return child;
}
