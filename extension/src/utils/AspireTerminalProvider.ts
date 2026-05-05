import * as vscode from 'vscode';
import { aspireTerminalName, dcpServerNotInitialized, rpcServerNotInitialized } from '../loc/strings';
import { extensionLogOutputChannel } from './logging';
import { RpcServerConnectionInfo } from '../server/AspireRpcServer';
import { DcpServerConnectionInfo } from '../dcp/types';
import { getRunSessionInfo, getSupportedCapabilities } from '../capabilities';
import { EnvironmentVariables } from './environment';
import { resolveCliPath } from './cliPath';
import path from 'path';

export const enum AnsiColors {
    Green = '\x1b[32m',
    Yellow = '\x1b[33m',
    Blue = '\x1b[34m',
}

export interface AspireTerminal {
    terminal: vscode.Terminal;
    dispose: () => void;
}

export class AspireTerminalProvider implements vscode.Disposable {
    private _terminalByDebugSessionId: Map<string | null, AspireTerminal> = new Map();
    private _rpcServerConnectionInfo?: RpcServerConnectionInfo;
    private _dcpServerConnectionInfo?: DcpServerConnectionInfo;

    constructor(subscriptions: vscode.Disposable[]) {
        subscriptions.push(vscode.window.onDidCloseTerminal(closedTerminal => {
            for (const [debugSessionId, terminal] of this._terminalByDebugSessionId.entries()) {
                if (terminal.terminal === closedTerminal) {
                    this._terminalByDebugSessionId.delete(debugSessionId);
                    break;
                }
            }
        }));
    }

    get rpcServerConnectionInfo() {
        if (!this._rpcServerConnectionInfo) {
            throw new Error(rpcServerNotInitialized);
        }

        return this._rpcServerConnectionInfo;
    }

    set rpcServerConnectionInfo(value: RpcServerConnectionInfo) {
        this._rpcServerConnectionInfo = value;
    }

    get dcpServerConnectionInfo() {
        if (!this._dcpServerConnectionInfo) {
            throw new Error(dcpServerNotInitialized);
        }

        return this._dcpServerConnectionInfo;
    }

    set dcpServerConnectionInfo(value: DcpServerConnectionInfo) {
        this._dcpServerConnectionInfo = value;
    }

    async sendAspireCommandToAspireTerminal(subcommand: string, showTerminal: boolean = true, additionalArgs?: string[]) {
        const cliPath = await this.getAspireCliExecutablePath();

        // On Windows, use & to execute paths, especially those with special characters
        // On Unix, just use the path directly
        let command: string;
        if (process.platform === 'win32') {
            // Use & call operator with quoted path for Windows
            command = `& "${cliPath}" ${subcommand}`;
        } else {
            // For Unix-like systems, quote only if needed
            const quotedPath = /[\s"'`$!*?()&|<>;]/.test(cliPath) ? `'${cliPath.replace(/'/g, `'\"'\"'`)}'` : cliPath;
            command = `${quotedPath} ${subcommand}`;
        }

        if (additionalArgs && additionalArgs.length > 0) {
            const quotedArgs = additionalArgs.map(arg => {
                if (process.platform === 'win32') {
                    // On Windows PowerShell, wrap in double quotes and escape inner double quotes
                    return `"${arg.replace(/"/g, '`"')}"`;
                } else {
                    // On Unix, wrap in single quotes and escape inner single quotes
                    return `'${arg.replace(/'/g, "'\"'\"'")}'`;
                }
            });
            command += ' ' + quotedArgs.join(' ');
        }

        if (this.isCliDebugLoggingEnabled()) {
            command += ' --debug';
        }

        if (process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] === 'true') {
            command += ' --cli-wait-for-debugger';
        }

        const aspireTerminal = this.getAspireTerminal();
        extensionLogOutputChannel.info(`Sending command to Aspire terminal: ${command}`);

        // Send Ctrl+C (\x03 / ETX) before the new command. This serves two purposes:
        //  1. If a previous Aspire command (e.g. `aspire logs ... --follow`) is still
        //     running in the foreground of the terminal, the new command would otherwise
        //     be delivered to that process's stdin instead of the shell. Ctrl+C sends
        //     SIGINT to interrupt it so the shell prompt comes back.
        //  2. At an idle shell prompt, Ctrl+C cancels any pre-existing typed text on the
        //     current line (bash/zsh as SIGINT-on-empty-line cancels input; PSReadLine
        //     treats it as a cancel) — preserving the previous "clear input buffer" intent.
        aspireTerminal.terminal.sendText('\x03', false);

        aspireTerminal.terminal.sendText(command);
        if (showTerminal) {
            aspireTerminal.terminal.show();
        }
    }

    getAspireTerminal(forceCreate?: boolean): AspireTerminal {
        const terminalName = aspireTerminalName;

        const existingTerminal = this._terminalByDebugSessionId.get(null);
        if (existingTerminal) {
            if (!forceCreate) {
                return existingTerminal;
            }
            else {
                existingTerminal.dispose();
            }
        }

        extensionLogOutputChannel.info(`Creating new Aspire terminal`);
        const terminal = vscode.window.createTerminal({
            name: terminalName,
            env: this.createEnvironment(),
        });

        const aspireTerminal: AspireTerminal = {
            terminal,
            dispose: () => {
                terminal.dispose();
                this._terminalByDebugSessionId.delete(null);
            }
        };

        this._terminalByDebugSessionId.set(null, aspireTerminal);

        return aspireTerminal;
    }

    createEnvironment(debugSessionId?: string, noDebug?: boolean, noExtensionVariables?: boolean): any {
        if (noExtensionVariables) {
            return process.env;
        }

        const env: any = {
            ...process.env,

            // Extension connection information
            ASPIRE_EXTENSION_ENDPOINT: this.rpcServerConnectionInfo.address,
            ASPIRE_EXTENSION_TOKEN: this.rpcServerConnectionInfo.token,
            ASPIRE_EXTENSION_CERT: Buffer.from(this.rpcServerConnectionInfo.cert, 'utf-8').toString('base64'),
            ASPIRE_EXTENSION_PROMPT_ENABLED: 'true',

            // Use the current locale in the CLI
            ASPIRE_LOCALE_OVERRIDE: vscode.env.language,

            // Include DCP server info
            DEBUG_SESSION_PORT: this.dcpServerConnectionInfo.address,
            DEBUG_SESSION_TOKEN: this.dcpServerConnectionInfo.token,
            DEBUG_SESSION_SERVER_CERTIFICATE: this.dcpServerConnectionInfo.certificate,
        };

        if (debugSessionId) {
            env.ASPIRE_EXTENSION_DEBUG_SESSION_ID = debugSessionId;
            env.DCP_INSTANCE_ID_PREFIX = debugSessionId + '-';
            env.DEBUG_SESSION_RUN_MODE = noDebug === false ? "Debug" : "NoDebug";
            env.ASPIRE_EXTENSION_DEBUG_RUN_MODE = noDebug === false ? "Debug" : "NoDebug";
            env.DEBUG_SESSION_INFO = JSON.stringify(getRunSessionInfo());
            env.ASPIRE_EXTENSION_CAPABILITIES = getSupportedCapabilities().join(',');

            // if DCP debug logging is enabled, set DCP-specific logging environment variables
            const dcpDebugLoggingEnabled = vscode.workspace.getConfiguration('aspire').get<boolean>('enableAspireDcpDebugLogging', false);
            const workspaceRoot = vscode.workspace.workspaceFolders?.[0];
            if (dcpDebugLoggingEnabled && workspaceRoot) {
                env.DCP_DIAGNOSTICS_LOG_LEVEL = "debug";
                env.DCP_PRESERVE_EXECUTABLE_LOGS = "1";
                env.DCP_DIAGNOSTICS_LOG_FOLDER = path.join(workspaceRoot.uri.fsPath, '.aspire', 'dcp', `logs-${debugSessionId}`);
            }
        }

        return env;
    }

    closeAllOpenAspireTerminals() {
        extensionLogOutputChannel.info('Closing all open Aspire terminals');

        // First, dispose any terminals we are explicitly tracking
        for (const [debugSessionId, aspireTerminal] of this._terminalByDebugSessionId.entries()) {
            try {
                aspireTerminal.terminal.dispose();
            }
            catch (err) {
                extensionLogOutputChannel.error(`Failed to dispose Aspire terminal for session ${debugSessionId}: ${err}`);
            }
        }

        // Also dispose any terminals left over from previous runs that we didn't track
        for (const term of vscode.window.terminals) {
            try {
                if (term.name === aspireTerminalName) {
                    extensionLogOutputChannel.info(`Disposing unregistered Aspire terminal: ${term.name}`);
                    term.dispose();
                }
            }
            catch (err) {
                extensionLogOutputChannel.error(`Failed to dispose unregistered Aspire terminal ${term.name}: ${err}`);
            }
        }

        this._terminalByDebugSessionId.clear();
    }

    dispose() {
        for (const terminal of this._terminalByDebugSessionId.values()) {
            terminal.dispose();
        }
    }


    async getAspireCliExecutablePath(): Promise<string> {
        const result = await resolveCliPath();
        return result.cliPath;
    }

    isCliDebugLoggingEnabled(): boolean {
        return vscode.workspace.getConfiguration('aspire').get<boolean>('enableAspireCliDebugLogging', false);
    }

    isDebugConfigEnvironmentLoggingEnabled(): boolean {
        return vscode.workspace.getConfiguration('aspire').get<boolean>('enableDebugConfigEnvironmentLogging', false);
    }
}
